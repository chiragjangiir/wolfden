using System.Text;

namespace Wolfden.Index.Tests;

/// <summary>
/// A copy of a den in a fresh temporary folder, deleted on dispose. Tests
/// build indexes only in copies, never inside <c>samples/</c> or the
/// fixtures.
/// </summary>
public sealed class TempDen : IDisposable
{
    private static readonly Lazy<string> RepoRootValue = new(FindRepoRoot);

    private TempDen(string root)
    {
        Root = root;
    }

    /// <summary>The repo root: the nearest folder above the test binaries that holds Wolfden.slnx.</summary>
    public static string RepoRoot => RepoRootValue.Value;

    /// <summary>The den root.</summary>
    public string Root { get; }

    /// <summary>The index file's path.</summary>
    public string IndexPath => IndexBuilder.IndexPath(Root);

    /// <summary>A copy of the Dracula sample.</summary>
    public static TempDen Dracula() => Copy(Path.Combine(RepoRoot, "samples", "dracula"));

    /// <summary>A copy of the invented Starfall den from the Core tests.</summary>
    public static TempDen Starfall() => Copy(Path.Combine(RepoRoot, "tests", "Wolfden.Core.Tests", "Dens", "starfall"));

    /// <summary>A den made of the given files, by den-relative path.</summary>
    public static TempDen FromFiles(IReadOnlyDictionary<string, string> files)
    {
        var den = new TempDen(NewFolder());
        foreach (var (path, text) in files)
        {
            den.Write(path, text);
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

    /// <summary>The SHA-256 of every file outside <c>.wolfden/</c>, by path, to show a rebuild changes none of them.</summary>
    public Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(Root, f).StartsWith(IndexBuilder.StateFolder, StringComparison.Ordinal))
            .ToDictionary(
                f => Path.GetRelativePath(Root, f).Replace('\\', '/'),
                f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

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

    private static TempDen Copy(string source)
    {
        var den = new TempDen(NewFolder());
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith(IndexBuilder.StateFolder, StringComparison.Ordinal))
            {
                continue;
            }

            var target = Path.Combine(den.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return den;
    }

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "wolfden-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
