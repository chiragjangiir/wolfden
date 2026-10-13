using System.Reflection;

namespace Wolfden.Cli;

/// <summary>
/// Logic for the <c>wolfden</c> command, kept out of <c>Program.cs</c> so
/// tests can call it with their own writer.
/// </summary>
internal static class CliApp
{
    /// <summary>Commands planned for v0. None is implemented yet.</summary>
    internal static readonly string[] PlannedCommands = ["init", "index", "check", "query", "serve", "mcp"];

    internal static string Version =>
        typeof(CliApp).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "unknown";

    internal static string Usage =>
        $"usage: wolfden [--version] <{string.Join(" | ", PlannedCommands)}>  (commands not yet implemented)";

    /// <summary>Runs the command and returns the process exit code.</summary>
    internal static int Run(string[] args, TextWriter output)
    {
        if (args is ["--version"])
        {
            output.WriteLine(Version);
            return 0;
        }

        output.WriteLine(Usage);
        return args.Length == 0 ? 0 : 1;
    }
}
