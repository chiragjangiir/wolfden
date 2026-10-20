using System.Text;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>A den held in memory, for structure and position tests.</summary>
internal sealed class MemoryDenSource : IDenSource
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public static string StarfallRoot => Path.Combine(TestFiles.RepoRoot, "tests", "Wolfden.Core.Tests", "Dens", "starfall");

    public static string DraculaRoot => Path.Combine(TestFiles.RepoRoot, "samples", "dracula");

    /// <summary>Every file of a den on disk, copied into memory.</summary>
    public static MemoryDenSource FromDirectory(string root)
    {
        var source = new MemoryDenSource();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            source._files[Path.GetRelativePath(root, path).Replace('\\', '/')] = File.ReadAllBytes(path);
        }

        return source;
    }

    /// <summary>The paths of every file, in ASCII order.</summary>
    public IEnumerable<string> Files => _files.Keys.Order(StringComparer.Ordinal);

    /// <summary>Adds or replaces a file.</summary>
    public MemoryDenSource With(string path, string text)
    {
        _files[path] = Encoding.UTF8.GetBytes(text);
        return this;
    }

    /// <summary>Adds or replaces a file with exact bytes, such as invalid UTF-8.</summary>
    public MemoryDenSource WithBytes(string path, byte[] bytes)
    {
        _files[path] = bytes;
        return this;
    }

    /// <summary>Removes a file.</summary>
    public MemoryDenSource Without(string path)
    {
        _files.Remove(path);
        return this;
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public byte[] ReadFile(string path) => _files[path];

    public IEnumerable<string> GetDirectories(string path)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        return _files.Keys
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => f[prefix.Length..].Split('/'))
            .Where(parts => parts.Length > 1)
            .Select(parts => parts[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public IEnumerable<string> GetFiles(string path)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        return _files.Keys
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && !f[prefix.Length..].Contains('/'))
            .Select(f => f[prefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A minimal valid den.yaml.</summary>
    public static MemoryDenSource Den() => new MemoryDenSource().With("den.yaml", "format: 1\n");

    /// <summary>
    /// Adds a container file with the given frontmatter lines after its name,
    /// then an id made from its folder, so F-ID-2 stays quiet and the lines
    /// above it don't move. A line that sets <c>id</c> replaces it: pass
    /// <c>"id:"</c> for a container with no id.
    /// </summary>
    public MemoryDenSource Container(string folder, string fileName, params string[] frontmatter)
    {
        var name = folder.Split('/')[^1];
        var id = frontmatter.Any(l => l.StartsWith("id:", StringComparison.Ordinal)) ? "" : $"id: {ContainerId(folder)}\n";
        var body = string.Join("", frontmatter.Select(l => l + "\n"));
        return With($"{folder}/{fileName}", $"---\nname: {name}\n{body}{id}---\n");
    }

    /// <summary>The id <see cref="Container"/> gives a container folder: a valid ULID made from the path.</summary>
    public static string ContainerId(string folder)
    {
        // FNV-1a, so the id is the same in every run.
        var hash = 14695981039346656037UL;
        foreach (var c in folder)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return "01JCNTR" + (hash % 10_000_000_000_000_000_000UL).ToString("D19", System.Globalization.CultureInfo.InvariantCulture);
    }

    public MemoryDenSource Universe(string key, params string[] frontmatter) =>
        Container($"universes/{key}", "universe.md", frontmatter);

    public MemoryDenSource Series(string universe, string key, params string[] frontmatter) =>
        Container($"universes/{universe}/series/{key}", "series.md", frontmatter);

    /// <summary>A book in a series, or standalone when <paramref name="series"/> is null.</summary>
    public MemoryDenSource Book(string universe, string? series, string key, params string[] frontmatter) =>
        Container(series is null ? $"universes/{universe}/books/{key}" : $"universes/{universe}/series/{series}/books/{key}", "book.md", frontmatter);
}
