namespace Wolfden.Cli.Tests;

/// <summary>Runs <c>wolfden</c> in-process and captures what it prints.</summary>
internal static class Cli
{
    /// <summary>The exit code, standard output, and standard error, with line endings normalized to LF.</summary>
    public static (int ExitCode, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliApp.Run(args, output, error);
        return (exitCode, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    /// <summary>Runs with the process's current folder set to <paramref name="folder"/>. Only for tests in the <see cref="CurrentFolder"/> collection.</summary>
    public static (int ExitCode, string Output, string Error) RunIn(string folder, params string[] args)
    {
        var saved = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = folder;
            return Run(args);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    /// <summary>Every file and folder under <paramref name="root"/>, with each file's SHA-256 and last write time, so an in-place rewrite shows too.</summary>
    public static List<string> Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(root, p).StartsWith(".wolfden", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p)
                ? $"{p} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))} {File.GetLastWriteTimeUtc(p).Ticks}"
                : p)
            .ToList();
}
