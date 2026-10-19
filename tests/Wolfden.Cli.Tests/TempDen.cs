using System.Text;

namespace Wolfden.Cli.Tests;

/// <summary>A den written to a fresh temporary folder, deleted on dispose.</summary>
public sealed class TempDen : IDisposable
{
    private static readonly Lazy<string> RepoRootValue = new(FindRepoRoot);

    private TempDen(string root)
    {
        Root = root;
    }

    /// <summary>The repo root: the nearest folder above the test binaries that holds Wolfden.slnx.</summary>
    public static string RepoRoot => RepoRootValue.Value;

    /// <summary>The Dracula sample, in place. <c>wolfden check</c> never writes, so tests can read it directly.</summary>
    public static string DraculaRoot => Path.Combine(RepoRoot, "samples", "dracula");

    /// <summary>The den root.</summary>
    public string Root { get; }

    /// <summary>A den made of the given files, by den-relative path.</summary>
    public static TempDen FromFiles(IReadOnlyDictionary<string, string> files)
    {
        var path = Path.Combine(Path.GetTempPath(), "wolfden-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var den = new TempDen(path);
        foreach (var (file, text) in files)
        {
            den.Write(file, text);
        }

        return den;
    }

    /// <summary>Writes a file in the den, creating folders as needed.</summary>
    public void Write(string path, string text)
    {
        var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS cleans its temp folder.
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wolfden.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Couldn't find the repo root (Wolfden.slnx) above the test binaries.");
    }
}
