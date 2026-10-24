using System.Diagnostics;
using System.Text;
using Wolfden.Git;

namespace Wolfden.Cli.Tests;

/// <summary>
/// Git for the CLI tests, with a configuration of their own, never the
/// developer's: a fixed name and email, no signing, no system or global
/// settings, and no repository found above <see cref="TempDen.TempRoot"/>.
/// </summary>
internal static class TestGit
{
    private static readonly Lazy<string> ConfigFile = new(WriteConfig);

    /// <summary>Git with the tests' configuration.</summary>
    public static GitOptions Options => new()
    {
        Environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = ConfigFile.Value,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CEILING_DIRECTORIES"] = TempDen.TempRoot,
            ["GIT_DIR"] = null,
            ["GIT_WORK_TREE"] = null,
            ["GIT_INDEX_FILE"] = null,
        },
    };

    /// <summary>Every command the tests run uses the tests' git.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void UseTestGit() => CliApp.Git = Options;

    /// <summary>Runs git in <paramref name="folder"/> and returns its output; a failure fails the test.</summary>
    public static string Run(string folder, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in Options.Environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {error.Result}");
        }

        return output.Result.ReplaceLineEndings("\n");
    }

    /// <summary>Clears read-only attributes (git makes its objects read-only), so a temporary folder can be deleted.</summary>
    public static void MakeDeletable(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private static string WriteConfig()
    {
        Directory.CreateDirectory(TempDen.TempRoot);
        var file = Path.Combine(TempDen.TempRoot, $"gitconfig-{Environment.ProcessId}-{Guid.NewGuid():N}");
        File.WriteAllText(
            file,
            "[user]\n\tname = Test Writer\n\temail = writer@example.com\n[commit]\n\tgpgsign = false\n[tag]\n\tgpgsign = false\n" +
            "[init]\n\tdefaultBranch = main\n[core]\n\tautocrlf = false\n",
            new UTF8Encoding(false));
        return file;
    }
}
