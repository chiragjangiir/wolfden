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
                collection.Add(McpServerTool.Create(method, target));
            }
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "wolfden", Title = "Wolfden", Version = version },
            ServerInstructions = Instructions,
            ToolCollection = collection,
        };
    }

    /// <summary>
    /// Serves the den over <paramref name="transport"/> until the client
    /// disconnects or <paramref name="cancellationToken"/> is cancelled.
    /// With <paramref name="watch"/>, it first starts a <see cref="DenWatcher"/>
    /// that keeps the index up to date while it runs, as <c>wolfden index --watch</c>
    /// does. If another process is already watching the den, that watcher
    /// does the job, and each tool call waits briefly for it, as
    /// <c>wolfden query</c> does. Everything it logs goes to <paramref name="log"/>,
    /// never to the transport.
    /// </summary>
    public static async Task RunAsync(
        ITransport transport, DenTools tools, string version, TextWriter log, bool watch = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(log);
        using var watcher = watch ? StartWatcher(tools.Root, log) : null;
        using var logs = LoggerFactory.Create(b => b.AddProvider(new TextWriterLoggerProvider(log)).SetMinimumLevel(LogLevel.Warning));
        await using var server = McpServer.Create(transport, Options(tools, version), logs);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
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
