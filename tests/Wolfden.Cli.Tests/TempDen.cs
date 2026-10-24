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

    /// <summary>The folder every temporary den goes in, outside the repo.</summary>
    public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "wolfden-cli-tests");

    /// <summary>
    /// Stops the den search at <see cref="TempRoot"/> for every test, so a
    /// den.yaml somewhere above the system's temporary folder can't change
    /// what "no den here" or "inside another den" means in a test.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void IsolateDenSearch() => DenLocation.Ceiling = TempRoot;

    /// <summary>The repo root: the nearest folder above the test binaries that holds Wolfden.slnx.</summary>
    public static string RepoRoot => RepoRootValue.Value;

    /// <summary>The Dracula sample, in place. <c>wolfden check</c> never writes, so tests can read it directly.</summary>
    public static string DraculaRoot => Path.Combine(RepoRoot, "samples", "dracula");

    /// <summary>The den root.</summary>
    public string Root { get; }

    /// <summary>A den made of the given files, by den-relative path.</summary>
    public static TempDen FromFiles(IReadOnlyDictionary<string, string> files)
    {
        var den = Empty();
        foreach (var (file, text) in files)
        {
            den.Write(file, text);
        }

        return den;
    }

    /// <summary>An empty temporary folder, for commands that create a den or write an index.</summary>
    public static TempDen Empty()
    {
        var path = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDen(path);
    }

    /// <summary>A copy of the Dracula sample, for commands that write (the index, <c>--fix</c>).</summary>
    public static TempDen DraculaCopy()
    {
        var den = Empty();
        foreach (var file in Directory.EnumerateFiles(DraculaRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(DraculaRoot, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.')))
            {
                continue;
            }

            var target = Path.Combine(den.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return den;
    }

    /// <summary>Reads a file in the den.</summary>
    public string Read(string path) => File.ReadAllText(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>True if the file exists in the den.</summary>
    public bool Exists(string path) => File.Exists(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));

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
            // A repository's objects are read-only.
            foreach (var git in Directory.EnumerateDirectories(Root, ".git", SearchOption.AllDirectories))
            {
                TestGit.MakeDeletable(git);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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
