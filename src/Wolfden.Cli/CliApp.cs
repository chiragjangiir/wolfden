using System.Reflection;

namespace Wolfden.Cli;

/// <summary>
/// Logic for the <c>wolfden</c> command, kept out of <c>Program.cs</c> so
/// tests can call it with their own writers.
/// </summary>
/// <remarks>
/// The argument handling is deliberately minimal: a command name, then that
/// command's own arguments. The full command set and a real argument parser
/// come with #17.
/// </remarks>
internal static class CliApp
{
    /// <summary>Success. For <c>check</c>: the den has no errors (it may have warnings, F-GEN-1).</summary>
    internal const int ExitOk = 0;

    /// <summary>For <c>check</c>: the den has at least one error (F-GEN-1).</summary>
    internal const int ExitErrors = 1;

    /// <summary>The command line is wrong, or the den couldn't be read or loaded at all.</summary>
    internal const int ExitUsage = 2;

    /// <summary>Commands that work.</summary>
    internal static readonly string[] Commands = ["check"];

    /// <summary>Commands planned for v0 that aren't implemented yet (#17).</summary>
    internal static readonly string[] PlannedCommands = ["init", "index", "query", "serve", "mcp"];

    internal static string Version =>
        typeof(CliApp).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "unknown";

    internal static string Usage =>
        $"""
        usage: wolfden [--version] <command> [<args>]

        commands:
          check [<den>] [--json]   check a den for errors and continuity problems
                                   (<den> defaults to the current folder)

        planned, not yet implemented: {string.Join(", ", PlannedCommands)}
        """;

    /// <summary>Runs the command and returns the process exit code. Output and errors both go to <paramref name="output"/>.</summary>
    internal static int Run(string[] args, TextWriter output) => Run(args, output, output);

    /// <summary>Runs the command and returns the process exit code.</summary>
    /// <param name="args">The command line, without the program name.</param>
    /// <param name="output">Standard output: the command's results.</param>
    /// <param name="error">Standard error: usage problems and failures.</param>
    internal static int Run(string[] args, TextWriter output, TextWriter error)
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
            case ["check", .. var rest]:
                return CheckCommand.Run(rest, output, error);
            case [var command, ..] when PlannedCommands.Contains(command):
                error.WriteLine($"wolfden: '{command}' isn't implemented yet.");
                error.WriteLine(Usage);
                return ExitUsage;
            default:
                error.WriteLine($"wolfden: unknown command '{args[0]}'.");
                error.WriteLine(Usage);
                return ExitUsage;
        }
    }
}
