using System.Reflection;
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
        changes as the story goes on.

        Every tool except check reads one universe. A den with one universe uses it. In a den with several, pass 'universe'
        (its key), or a position whose book only one universe has, which settles it. Only search and list_entities take
        universe '*', for every universe at once, and never with a position. check validates files, so it checks the whole
        den unless given a universe, and then checks that universe's files and the den-level ones.

        Every entity, section, and field entry has a status: idea, undecided, or candidate (not decided yet), draft or canon (in
        canon; draft isn't settled), or retconned (replaced). Pinned facts are hard canon: check them before writing.

        To draft or review a chapter without spoilers, use canon_as_of with that chapter's position, or get_entity with as_of.
        Without a position, get_entity, list_entities, and search give the author's full view, including ideas and material the
        reader meets later. Use check for continuity problems. These tools only read; they never change the den's files.
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
    /// The server answers the client before the watcher's first build of
    /// the index is done, which can take seconds on a big den or a busy
    /// machine. A client that waits too long for its first answer gives up
    /// on the newest protocol and tries the older handshake, which can then
    /// fail (#135). A tool call that comes in during that build waits for
    /// it, or builds the index itself, as it does when no watcher runs.
    /// </remarks>
    public static Task RunAsync(
        ITransport transport, DenTools tools, string version, TextWriter log, bool watch = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(log);
        return RunAsync(transport, tools, version, log, watch ? () => StartWatcher(tools.Root, log) : null, cancellationToken);
    }

    /// <summary>
    /// <see cref="RunAsync(ITransport, DenTools, string, TextWriter, bool, CancellationToken)"/>,
    /// with <paramref name="startWatcher"/>, if given, starting the watcher
    /// alongside the server. For tests, which hold it up.
    /// </summary>
    internal static async Task RunAsync(
        ITransport transport, DenTools tools, string version, TextWriter log, Func<DenWatcher?>? startWatcher, CancellationToken cancellationToken)
    {
        using var logs = LoggerFactory.Create(b => b.AddProvider(new TextWriterLoggerProvider(log)).SetMinimumLevel(LogLevel.Warning));
        await using var server = McpServer.Create(transport, Options(tools, version), logs);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Serve first: the client's first message is answered while the watcher builds the index.
        var watching = startWatcher is null
            ? Task.FromResult<DenWatcher?>(null)
            : Task.Run(
                () =>
                {
                    try
                    {
                        return startWatcher();
                    }
                    catch
                    {
                        // A failure StartWatcher doesn't expect stops the server, and the await below reports it.
                        stop.Cancel();
                        throw;
                    }
                },
                CancellationToken.None);
        try
        {
            await server.RunAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Only a watcher that failed to start cancels stop on its own: the await below throws why.
        }
        finally
        {
            // Never leave a watcher running: wait for it to finish starting, then stop it.
            DenWatcher? watcher = null;
            try
            {
                watcher = await watching.ConfigureAwait(false);
            }
            finally
            {
                watcher?.Dispose();
            }
        }
    }

    /// <summary>Starts a watcher on the den, or returns null after logging why it couldn't.</summary>
    private static DenWatcher? StartWatcher(string root, TextWriter log)
    {
        var watcher = new DenWatcher(root);
        var gate = new object();
        watcher.Failed += (_, e) =>
        {
            lock (gate)
            {
                Log(log, $"wolfden mcp: {e.Message}");
            }
        };
        try
        {
            watcher.Start();
            Log(log, $"wolfden mcp: serving the den at '{root}', keeping its index up to date as files change.");
            return watcher;
        }
        catch (InvalidOperationException e)
        {
            watcher.Dispose();
            Log(log, $"wolfden mcp: serving the den at '{root}' without its own watcher: {e.Message} Each call checks the files first.");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            watcher.Dispose();
            Log(log, $"wolfden mcp: couldn't watch the den at '{root}': {e.Message} Each call checks the files first.");
            return null;
        }
    }

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
