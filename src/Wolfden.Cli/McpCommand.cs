using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolfden.Mcp;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden mcp [&lt;den&gt;] [--no-watch]</c>: runs the MCP server over
/// stdio for an AI client (design.md §8, §9), with the read tools of
/// <see cref="WolfdenTools"/>. Standard output carries the protocol, so
/// everything else, from Wolfden or the MCP SDK, goes to standard error.
/// </summary>
/// <remarks>
/// <para>
/// It hosts the same watcher as <c>wolfden index --watch</c>, so the index
/// stays up to date while it runs; each tool call also checks the files
/// first, as <c>wolfden query</c> does. If another Wolfden is already
/// watching the den, that watcher keeps the index up to date instead.
/// </para>
/// <para>
/// Exit codes: 0 when the client disconnects (standard input closes) or on
/// Ctrl+C, and 2 when the command line is wrong or there's no den. A den
/// whose <c>den.yaml</c> doesn't load is still served, so the client can
/// call <c>check</c> to find out why; the other tools report
/// <c>den_not_loaded</c>.
/// </para>
/// </remarks>
internal static class McpCommand
{
    internal static readonly CommandSpec Spec = new(
        "mcp",
        "usage: wolfden mcp [<den>] [--den <den>] [--no-watch]",
        """
        Runs Wolfden's MCP server over standard input and output, for an AI
        client such as an editor or a desktop assistant. Its tools only read
        the den: search, get_entity, list_entities, canon_as_of, and check.
        It keeps the den's index up to date as files change while it runs.
        Log messages go to standard error.

          <den>, --den <den>  the den's folder; by default, the current folder or
                              the nearest folder above it with a den.yaml
          --no-watch          don't watch the files; each tool call still checks
                              them first and rebuilds the index if they changed

        To use it, add a server to your AI client's MCP settings that runs
        'wolfden mcp --den <folder>'.

        Exits 0 when the client disconnects, and 2 if the server couldn't start.
        """,
        ["--no-watch"],
        ["--den"],
        [],
        1,
        "give at most one den folder.");

    /// <summary>For tests: makes the transport in place of standard input and output.</summary>
    internal static Func<ITransport>? TransportFactory { get; set; }

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken stop = default)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        if (!DenLocation.TryGetGiven(Spec, line, error, out var given) || DenLocation.Resolve(Spec.Name, given, error) is not { } root)
        {
            return CliApp.ExitUsage;
        }

        ITransport transport;
        if (TransportFactory is { } factory)
        {
            transport = factory();
        }
        else
        {
            transport = new StdioServerTransport("wolfden");

            // Anything written to Console.Out now would corrupt the protocol.
            Console.SetOut(error);
        }

        CliApp.RunsUntilStopped = true;
        try
        {
            WolfdenMcpServer.RunAsync(transport, new DenTools(root), CliApp.Version, error, !line.Has("--no-watch"), stop)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Ctrl+C.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            error.WriteLine($"wolfden mcp: the server stopped: {e.Message}");
            return CliApp.ExitUsage;
        }
        finally
        {
            CliApp.RunsUntilStopped = false;
        }

        return CliApp.ExitOk;
    }
}
