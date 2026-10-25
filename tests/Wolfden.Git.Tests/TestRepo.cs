using System.Diagnostics;
using System.Text;
using Wolfden.Core.History;

namespace Wolfden.Git.Tests;

/// <summary>
/// A git repository in a fresh temporary folder, holding a den at its root
/// or in a subfolder, and deleted on dispose. Git runs with a configuration
/// of the tests' own, never the developer's: a fixed name and email, no
/// signing, and no system or global settings.
/// </summary>
public sealed class TestRepo : IDisposable
{
    private static readonly Lazy<string> ConfigFile = new(WriteConfig);

    private TestRepo(string root, string denRoot)
    {
        Root = root;
        DenRoot = denRoot;
    }

    /// <summary>The folder every test repository goes in. Git looks no higher than it for a repository.</summary>
    public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "wolfden-git-tests");

    /// <summary>Git with the tests' own configuration.</summary>
    public static GitOptions Options => new()
    {
        Environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = ConfigFile.Value,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CEILING_DIRECTORIES"] = TempRoot,
            ["GIT_DIR"] = null,
            ["GIT_WORK_TREE"] = null,
            ["GIT_INDEX_FILE"] = null,
        },
    };

    /// <summary>The repository's top folder.</summary>
    public string Root { get; }

    /// <summary>The den's folder: <see cref="Root"/>, or a folder inside it.</summary>
    public string DenRoot { get; }

    /// <summary>The den's history, found as Wolfden finds it.</summary>
    public IHistoryProvider History => GitHistory.ForDen(DenRoot, Options);

    /// <summary>A new repository whose den is at <paramref name="denFolder"/> inside it (repository-relative), or at its root.</summary>
    public static TestRepo Create(string? denFolder = null)
    {
        var root = NewFolder();
        var den = denFolder is null ? root : Path.Combine(root, denFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(den);
        var repo = new TestRepo(root, den);
        repo.Git("init", "--quiet");
        repo.WriteDen("den.yaml", "format: 1\n");
        return repo;
    }

    /// <summary>A new empty folder in <see cref="TempRoot"/>, outside any repository.</summary>
    public static string NewFolder()
    {
        var path = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file at a den-relative path.</summary>
    public void WriteDen(string path, string text) => WriteFile(DenRoot, path, text);

    /// <summary>Writes a file at a repository-relative path.</summary>
    public void WriteRepo(string path, string text) => WriteFile(Root, path, text);

    /// <summary>Writes an executable git hook, such as <c>pre-commit</c>. Hooks run in the repository's top folder.</summary>
    public void WriteHook(string name, string script)
    {
        var hook = Path.Combine(Root, ".git", "hooks", name);
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, script.ReplaceLineEndings("\n"), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Deletes a file at a den-relative path.</summary>
    public void DeleteDen(string path) => File.Delete(Path.Combine(DenRoot, path.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Stages everything and commits it, as a writer using git by hand would, and returns the commit's hash.</summary>
    public string CommitAll(string message)
    {
        Git("add", "--all");
        Git("commit", "--quiet", "-m", message);
        return Git("rev-parse", "HEAD").Trim();
    }

    /// <summary>Like <see cref="CommitAll"/>, with the author and commit dates set to <paramref name="minutes"/> past a fixed time, so the log's order is known.</summary>
    public string CommitAt(string message, int minutes)
    {
        var date = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero).AddMinutes(minutes).ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        var dates = new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date };
        Git("add", "--all");
        RunGit(Root, dates, "commit", "--quiet", "-m", message);
        return Git("rev-parse", "HEAD").Trim();
    }

    /// <summary>Runs git in the repository's top folder and returns its output; a failure fails the test.</summary>
    public string Git(params string[] args) => RunGit(Root, args);

    /// <summary>Runs git in <paramref name="folder"/> with the tests' configuration and returns its output; a failure fails the test.</summary>
    public static string RunGit(string folder, params string[] args) => RunGit(folder, null, args);

    /// <summary>Runs git in <paramref name="folder"/> with the tests' configuration and <paramref name="environment"/>, and returns its output.</summary>
    public static string RunGit(string folder, IReadOnlyDictionary<string, string?>? environment, params string[] args)
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

        foreach (var (name, value) in Options.Environment.Concat(environment ?? new Dictionary<string, string?>()))
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

    public void Dispose() => DeleteFolder(Root);

    /// <summary>Deletes a folder, read-only files included (git makes its objects read-only), as best it can.</summary>
    public static void DeleteFolder(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort; the OS cleans its temp folder.
        }
    }

    private static void WriteFile(string root, string path, string text)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static string WriteConfig()
    {
        Directory.CreateDirectory(TempRoot);
        var file = Path.Combine(TempRoot, $"gitconfig-{Environment.ProcessId}-{Guid.NewGuid():N}");
        File.WriteAllText(
            file,
            """
            [user]
            	name = Test Writer
            	email = writer@example.com
            [commit]
            	gpgsign = false
            [tag]
            	gpgsign = false
            [init]
            	defaultBranch = main
            [core]
            	autocrlf = false
            	safecrlf = false
            [advice]
            	detachedHead = false
            """.ReplaceLineEndings("\n") + "\n",
            new UTF8Encoding(false));
        return file;
    }
}
