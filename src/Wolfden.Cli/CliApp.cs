using System.Reflection;

namespace Wolfden.Cli;

/// <summary>
/// Logic for the <c>wolfden</c> command, kept out of <c>Program.cs</c> so
/// tests can call it with their own writers.
/// </summary>
/// <remarks>
/// <para>
/// Every command shares one surface: <c>--help</c> on any command, the
/// den found from the current folder unless <c>--den</c> names one
/// (<see cref="DenLocation"/>), <c>--json</c> for one JSON document with a
/// <c>version</c> (<see cref="CliJson"/>), and these exit codes. Results go
/// to standard output; usage mistakes and failures go to standard error.
/// </para>
/// </remarks>
internal static class CliApp
{
    /// <summary>Success. For <c>check</c>: the den has no errors (it may have warnings, F-GEN-1).</summary>
    internal const int ExitOk = 0;

    /// <summary>For <c>check</c>: the den has at least one error (F-GEN-1).</summary>
    internal const int ExitErrors = 1;

    /// <summary>The command couldn't do its job: a wrong command line, no den, a <c>den.yaml</c> that doesn't load, or a failure.</summary>
    internal const int ExitUsage = 2;

    /// <summary>For a command that runs until stopped (<c>mcp</c>): it started and did its job for a while, then stopped because of a failure.</summary>
    internal const int ExitStoppedByFailure = 3;

    /// <summary>
    /// True while a command that runs until it's stopped (<c>index --watch</c>, <c>serve</c>, <c>mcp</c>)
    /// is running, so Ctrl+C stops it cleanly instead of ending the process.
    /// Other commands end at Ctrl+C as usual.
    /// </summary>
    internal static volatile bool RunsUntilStopped;

    /// <summary>
    /// How commands run git (<c>init --git</c>, and history in <c>mcp</c>):
    /// the user's own git, by default. Tests give git a configuration of
    /// their own.
    /// </summary>
    internal static Wolfden.Git.GitOptions Git { get; set; } = Wolfden.Git.GitOptions.Default;

    /// <summary>Commands that work, in the order the usage lists them.</summary>
    internal static readonly string[] Commands = ["init", "check", "index", "query", "new", "copy", "freeze", "unfreeze", "new-id", "serve", "mcp"];

    internal static string Version =>
        typeof(CliApp).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "unknown";

    internal static string Usage =>
        """
        usage: wolfden [--version] <command> [<args>]

        commands:
          init [<folder>]          create a new den
          check [<den>]            check a den for errors and continuity problems
                                   (--fix adds missing ids)
          index [<den>]            rebuild the den's index from its files
                                   (--watch keeps it up to date as files change)
          query [<universe>]       show canon as of a story position (--at book:chapter)
          new <type> <name>        create a note from its type's template
                                   (--in <scope> says where it goes)
          copy <entity> <name>     copy an entity under a new name, with a new id
                                   (--to <scope> puts it elsewhere)
          freeze <book>            mark a finished book final, and tag its snapshot
          unfreeze <book>          open a frozen book for editing again
          new-id [<count>]         print new ids for files made by hand
          serve [<den>]            open the den's web UI in a browser (runs until Ctrl+C)
          mcp [<den>]              run the MCP server over stdio for an AI client

        Run 'wolfden <command> --help' for a command's options. Commands find the
        den from the current folder, or take '--den <folder>', and '--json'
        prints one JSON document instead of text.
        """;

    /// <summary>Runs the command and returns the process exit code. Output and errors both go to <paramref name="output"/>.</summary>
    internal static int Run(string[] args, TextWriter output) => Run(args, output, output);

    /// <summary>Runs the command and returns the process exit code.</summary>
    /// <param name="args">The command line, without the program name.</param>
    /// <param name="output">Standard output: the command's results.</param>
    /// <param name="error">Standard error: usage problems and failures.</param>
    internal static int Run(string[] args, TextWriter output, TextWriter error) => RunUntil(args, output, error, CancellationToken.None);

    /// <summary>
    /// Runs the command and returns the process exit code. A command that
    /// runs until it's stopped, such as <c>index --watch</c>, stops when
    /// <paramref name="stop"/> is cancelled (Ctrl+C).
    /// </summary>
    /// <param name="args">The command line, without the program name.</param>
    /// <param name="output">Standard output: the command's results.</param>
    /// <param name="error">Standard error: usage problems and failures.</param>
    /// <param name="stop">Cancelled to stop a command that runs until stopped.</param>
    internal static int RunUntil(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(args);
        switch (args)
        {
            case ["--version"]:
                output.WriteLine(Version);
                return ExitOk;
            case [] or ["--help"] or ["-h"] or ["help"]:
                output.WriteLine(Usage);
                return ExitOk;
            case ["help", var command] when Commands.Contains(command):
                return RunUntil([command, "--help"], output, error, stop);
            case ["init", .. var rest]:
                return InitCommand.Run(rest, output, error);
            case ["check", .. var rest]:
                return CheckCommand.Run(rest, output, error);
            case ["index", .. var rest]:
                return IndexCommand.Run(rest, output, error, stop);
            case ["query", .. var rest]:
                return QueryCommand.Run(rest, output, error);
            case ["new", .. var rest]:
                return NewCommand.Run(rest, output, error);
            case ["copy", .. var rest]:
                return CopyCommand.Run(rest, output, error);
            case ["freeze", .. var rest]:
                return FreezeCommand.Run(freeze: true, rest, output, error);
            case ["unfreeze", .. var rest]:
                return FreezeCommand.Run(freeze: false, rest, output, error);
            case ["new-id", .. var rest]:
                return NewIdCommand.Run(rest, output, error);
            case ["serve", .. var rest]:
                return ServeCommand.Run(rest, output, error, stop);
            case ["mcp", .. var rest]:
                return McpCommand.Run(rest, output, error, stop);
            default:
                error.WriteLine($"wolfden: unknown command '{args[0]}'.");
                error.WriteLine(Usage);
                return ExitUsage;
        }
    }
}
