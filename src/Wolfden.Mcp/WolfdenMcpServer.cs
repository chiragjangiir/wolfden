using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolfden.Index;

namespace Wolfden.Mcp;

/// <summary>
/// The Wolfden MCP server (design.md §8): the read tools of
/// <see cref="WolfdenTools"/> over one den, on any MCP transport.
/// <c>wolfden mcp</c> runs it over stdio, with a <see cref="DenWatcher"/>
/// keeping the index up to date while it runs (<see cref="RunAsync"/>).
/// </summary>
public static class WolfdenMcpServer
{
    /// <summary>What the server tells a client about Wolfden when it connects.</summary>
    public const string Instructions =
        """
        Wolfden is a story bible for long fiction. This server reads one den: a folder of markdown notes that the writer controls.
        A den holds universes. A universe holds series of books and standalone books, in story (reading) order, and each book
        lists its chapters. Notes are entities (characters, locations, events, items, factions, threads, notes, and other types),
        each filed at one scope: the universe, a series, or a book.

        A story position is 'book:chapter' using the den's keys (such as 'dracula:5'), or a bare book key for the start of that
        book. Facts can carry the position where the reader learns them (from) and where they stop holding (until), so canon
        changes as the story goes on. Chapter keys come from get_chapter. A read at a position the universe doesn't have, or
        create_entity with one as from, is refused (bad_position). update_fields and append_section write it anyway, and the fact
        is then left out of canon views, so take keys from get_chapter.

        Every tool except check reads one universe. A den with one universe uses it. In a den with several, pass 'universe'
        (its key), or a position whose book only one universe has, which settles it. Only search and list_entities take
        universe '*', for every universe at once, and never with a position. check validates files, so it checks the whole
        den unless given a universe, and then checks that universe's files and the den-level ones.

        Every entity, section, and field entry has a status: idea, undecided, or candidate (not decided yet), draft or canon (in
        canon; draft isn't settled), or retconned (replaced). Pinned facts are hard canon: check them before writing.

        To draft or review a chapter without spoilers, use canon_as_of with that chapter's position, or get_entity with as_of.
        Without a position, get_entity, list_entities, and search give the author's full view, including ideas and material the
        reader meets later. Use check for continuity problems, and get_history for the checkpoints that changed an entity and
        what changed since.

        An image kept in the den is written in a note as ![alt text](path), with the path relative to the folder of the note's own
        file, or from the den root if it starts with '/'. move_entity and copy_entity don't move images or rewrite their paths.

        The write tools (create_entity, update_fields, append_section, add_idea, rename_section, rename_entity, change_alias, copy_entity,
        move_entity, update_chapter_fields, update_chapter_defaults) change the den's files, keeping everything they don't change as the
        writer wrote it. A rename or a move rewrites the links that depend on it, in the same change, and a copy gets a new id. Make
        the changes these tools cover through them, not by editing the den's files, so these checks apply. Edit a file directly
        only for what they don't cover (a new book or chapter, an outline) and only when the writer asks, then run check. Never
        touch .wolfden/ (a rebuildable index) or .git/. Each edit of an existing file takes the hash the file had when you read it (from get_entity,
        get_chapter, get_chapter_defaults, or your last write), and is refused if the file has changed since (stale_file): the
        writer may be editing it, so read it again and redo the edit on what's there now. An edit that would leave an error is
        refused. Nothing in a final (frozen) book can be changed, and only the writer can unfreeze one: on frozen, ask them, and
        don't work around it. A change that would send existing links to a different entity is refused unless allow_relinks:
        show the writer the links, and set it only if they agree. Edits to hard canon are allowed, but the result lists them:
        check them with the writer. Writes aren't committed: call checkpoint at a natural stopping point, such as the end of a
        notes pass or a planning session, with a message that says what the pass was ('Notes pass after drafting ch 12'), naming
        yourself as co_author.
        """;

    /// <summary>The server's options: its name and version, its instructions, and its tools over <paramref name="tools"/>.</summary>
    public static McpServerOptions Options(DenTools tools, string version)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var target = new WolfdenTools(tools);
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in typeof(WolfdenTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            {
                collection.Add(new BindingErrorsAsBadArgument(McpServerTool.Create(method, target)));
            }
        }

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "wolfden", Title = "Wolfden", Version = version },
            ServerInstructions = tools.PinnedUniverse is null
                ? Instructions
                : Instructions + $"\nThis session is pinned to universe '{tools.PinnedUniverse}': every call reads it (check included), and a call " +
                  "that names another universe, or '*', is refused.\n",
            ToolCollection = collection,
        };
        return options;
    }

    /// <summary>
    /// A tool whose argument-binding errors come back in Wolfden's error
    /// shape. The tool methods catch every failure of their own
    /// (<see cref="WolfdenTools.Respond{T}"/>), so what the SDK throws, or
    /// reports as an error result in its own words, is a failure to bind the
    /// arguments: a required one missing, or one of the wrong type.
    /// </summary>
    private sealed class BindingErrorsAsBadArgument(McpServerTool inner) : DelegatingMcpServerTool(inner)
    {
        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            CallToolResult result;
            try
            {
                result = await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                return WolfdenTools.BadArgument(ProtocolTool.Name, e.Message);
            }

            return WolfdenTools.AsBadArgument(result, ProtocolTool.Name);
        }
    }

    /// <summary>
    /// Serves the den over <paramref name="transport"/> until the client
    /// disconnects or <paramref name="cancellationToken"/> is cancelled.
    /// With <paramref name="watch"/>, it also starts a <see cref="DenWatcher"/>
    /// that keeps the index up to date while it runs, as <c>wolfden index --watch</c>
    /// does. If another process is already watching the den, that watcher
    /// does the job, and each tool call waits briefly for it, as
    /// <c>wolfden query</c> does. Everything it logs goes to <paramref name="log"/>,
    /// never to the transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server doesn't make the client wait for the watcher's first build
    /// of the index, which can take seconds on a big den or a busy machine:
    /// it starts the watcher in the background. A client that waits too long
    /// for its first answer gives up on the newest protocol and tries the
    /// older handshake, which can then fail (#135). A tool call that comes in
    /// during that build waits for it, or builds the index itself, as it does
    /// when no watcher runs.
    /// </para>
    /// <para>
    /// If the watcher can't start because of an error that can clear, such
    /// as a tool call holding the index open when the first build replaces
    /// it, it tries again a few times (<see cref="WatcherRetryDelays"/>)
    /// before the server goes on without one. After an error that can't
    /// clear by itself, such as <c>.wolfden</c> being a file, it goes on
    /// without one at once.
    /// </para>
    /// </remarks>
    public static Task RunAsync(
        ITransport transport, DenTools tools, string version, TextWriter log, bool watch = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(log);
        return RunAsync(transport, tools, version, log, watch ? DefaultWatcher(tools.Root) : null, cancellationToken);
    }

    /// <summary>How long the server waits before each new try at starting a watcher whose start failed with an error that can clear (<see cref="CanClear"/>).</summary>
    internal static readonly IReadOnlyList<TimeSpan> WatcherRetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>Starts a <see cref="DenWatcher"/> on the den at <paramref name="root"/>, the way <c>wolfden mcp</c> does.</summary>
    internal static Func<TextWriter, CancellationToken, DenWatcher?> DefaultWatcher(string root) =>
        (log, stop) => StartWatcher(root, log, WatcherRetryDelays, r => new DenWatcher(r), stop);

    /// <summary>
    /// <see cref="RunAsync(ITransport, DenTools, string, TextWriter, bool, CancellationToken)"/>,
    /// with <paramref name="startWatcher"/>, if given, starting the watcher
    /// in the background, given the log and a token that says the server is
    /// stopping. For tests, which hold it up or make it fail.
    /// </summary>
    internal static async Task RunAsync(
        ITransport transport,
        DenTools tools,
        string version,
        TextWriter log,
        Func<TextWriter, CancellationToken, DenWatcher?>? startWatcher,
        CancellationToken cancellationToken)
    {
        // The watcher logs from another thread.
        log = TextWriter.Synchronized(log);
        using var logs = LoggerFactory.Create(b => b.AddProvider(new TextWriterLoggerProvider(log)).SetMinimumLevel(LogLevel.Warning));
        await using var server = McpServer.Create(transport, Options(tools, version), logs);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Which failed first, the server or the watcher, so a knock-on error is never reported as the cause.
        var firstToFail = new StrongBox<int>();

        // Serve first: the client's first message is answered while the watcher builds the index.
        var watching = startWatcher is null
            ? Task.FromResult<DenWatcher?>(null)
            : Task.Run(
                () =>
                {
                    if (stop.IsCancellationRequested)
                    {
                        // The client has gone already, so there's nothing to build the index for.
                        return null;
                    }

                    try
                    {
                        return startWatcher(log, stop.Token);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return null;
                    }
                    catch
                    {
                        // A failure StartWatcher doesn't expect stops the server, and is reported below.
                        Interlocked.CompareExchange(ref firstToFail.Value, WatcherFailedFirst, 0);
                        stop.Cancel();
                        throw;
                    }
                },
                CancellationToken.None);

        ExceptionDispatchInfo? failure = null;
        try
        {
            await server.RunAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Only a watcher that failed to start cancels stop on its own: the await below throws why.
        }
        catch (Exception e)
        {
            failure = ExceptionDispatchInfo.Capture(e);
            Interlocked.CompareExchange(ref firstToFail.Value, ServerFailedFirst, 0);
        }

        // The server has stopped, say because the client closed standard
        // input: a watcher that hasn't started yet doesn't, and one waiting
        // to try again gives up.
        await stop.CancelAsync().ConfigureAwait(false);

        // Never leave a watcher running: wait for it to finish starting, then stop it.
        DenWatcher? watcher = null;
        try
        {
            watcher = await watching.ConfigureAwait(false);
        }
        catch (Exception e) when (failure is not null)
        {
            // Both failed: the one that failed first is the cause, and comes first.
            throw firstToFail.Value == WatcherFailedFirst
                ? new AggregateException(e, failure.SourceException)
                : new AggregateException(failure.SourceException, e);
        }
        finally
        {
            watcher?.Dispose();
        }

        failure?.Throw();
    }

    private const int ServerFailedFirst = 1;
    private const int WatcherFailedFirst = 2;

    /// <summary>
    /// Starts a watcher on the den, or returns null after logging why it
    /// couldn't. After an error that can clear (<see cref="CanClear"/>) it
    /// tries again after each of <paramref name="retryDelays"/> in turn,
    /// unless <paramref name="stop"/> is cancelled meanwhile. Another
    /// watcher on the den, or an error that can't clear by itself, such as
    /// a missing den folder, <c>.wolfden</c> being a file, or the index path
    /// being a folder, gives up at once. Messages start with
    /// <paramref name="command"/>, the command hosting the watcher.
    /// </summary>
    internal static DenWatcher? StartWatcher(
        string root, TextWriter log, IReadOnlyList<TimeSpan> retryDelays, Func<string, DenWatcher> create, CancellationToken stop, string command = "wolfden mcp")
    {
        for (var attempt = 0; ; attempt++)
        {
            if (stop.IsCancellationRequested)
            {
                return null;
            }

            var watcher = create(root);
            watcher.Failed += (_, e) => Log(log, $"{command}: {e.Message}");
            try
            {
                watcher.Start();
                Log(log, $"{command}: serving the den at '{root}', keeping its index up to date as files change.");
                return watcher;
            }
            catch (DenAlreadyWatchedException e)
            {
                watcher.Dispose();
                Log(log, $"{command}: serving the den at '{root}' without its own watcher: {e.Message} Each call checks the files first.");
                return null;
            }
            catch (Exception e) when (CanClear(e) && attempt < retryDelays.Count)
            {
                watcher.Dispose();
                var delay = retryDelays[attempt];
                Log(
                    log,
                    $"{command}: couldn't start watching the den at '{root}' yet: {e.Message} Trying again in " +
                    $"{delay.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s.");
                if (stop.WaitHandle.WaitOne(delay))
                {
                    return null;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
            {
                watcher.Dispose();
                Log(log, $"{command}: couldn't watch the den at '{root}': {e.Message} Each call checks the files first.");
                return null;
            }
        }
    }

    /// <summary>
    /// True for a failed start that can succeed if tried again soon: the
    /// rebuilt index couldn't replace one a query held open
    /// (<see cref="IndexInUseException"/>), or SQLite found the index busy
    /// or locked. Other errors, such as a missing den folder or a file
    /// where <c>.wolfden</c> should be, stay until the writer fixes them.
    /// </summary>
    internal static bool CanClear(Exception e) =>
        e is IndexInUseException || e is SqliteException s && (s.SqliteErrorCode & 0xFF) is SqliteBusy or SqliteLocked;

    // SQLite's primary result codes for a database another connection is using (sqlite.org/rescode.html).
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private static void Log(TextWriter log, string message)
    {
        try
        {
            log.WriteLine(message);
            log.Flush();
        }
        catch (IOException)
        {
            // Nowhere left to report it.
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
