using System.Text;

namespace Wolfden.Core.Tests;

/// <summary>Finds repo files at test time, and compares edited text.</summary>
internal static class TestFiles
{
    private static readonly Lazy<string> Root = new(FindRepoRoot);

    /// <summary>The repo root: the nearest folder above the test binaries that holds Wolfden.slnx.</summary>
    public static string RepoRoot => Root.Value;

    public static string FixturesDir => Path.Combine(RepoRoot, "tests", "Wolfden.Core.Tests", "Fixtures");

    public static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixturesDir, name));

    /// <summary>Every .md and .yaml file under the given repo folders, as repo-relative paths with '/'.</summary>
    public static IEnumerable<string> RepoFiles(params string[] folders) =>
        folders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(RepoRoot, folder), "*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".md", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    public static byte[] ReadRepoFile(string relativePath) => File.ReadAllBytes(Path.Combine(RepoRoot, relativePath));

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>
    /// The part of <paramref name="before"/> that changed: the offset where
    /// the texts first differ, and the end of the changed part in each text
    /// (common prefix and suffix removed).
    /// </summary>
    public static (int Start, int OldEnd, int NewEnd) Diff(string before, string after)
    {
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start])
        {
            start++;
        }

        int oldEnd = before.Length, newEnd = after.Length;
        while (oldEnd > start && newEnd > start && before[oldEnd - 1] == after[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }

        return (start, oldEnd, newEnd);
    }

    /// <summary>Asserts that everything outside [<paramref name="from"/>, <paramref name="to"/>) of <paramref name="before"/> survived the edit unchanged.</summary>
    public static void AssertChangedOnlyWithin(string before, string after, int from, int to)
    {
        var (start, oldEnd, _) = Diff(before, after);
        if (start == before.Length && before.Length == after.Length)
        {
            return;
        }

        Assert.True(start >= from && oldEnd <= to,
            $"The edit changed [{start}, {oldEnd}) of the old text, outside the allowed [{from}, {to}).");
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
