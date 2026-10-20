using System.Security.Cryptography;
using System.Text;

namespace Wolfden.Index;

/// <summary>
/// The den's tree as the files fingerprint sees it (<see cref="IndexBuilder.FilesFingerprint"/>):
/// every folder, and every <c>.md</c>, <c>.yaml</c>, and <c>.yml</c> file
/// with the SHA-256 of its bytes, leaving out names that start with
/// <c>.</c> at any depth (F-LAY-2). A file watcher (#14) keeps one up to
/// date as files change, so it can record the fingerprint of the files an
/// update read without hashing the whole den again.
/// </summary>
internal sealed class DenFileTree
{
    /// <summary>Folder links can loop; no real den is this deep.</summary>
    private const int MaxDepth = 64;

    private readonly Dictionary<string, string> _files;
    private readonly HashSet<string> _folders;

    private DenFileTree(Dictionary<string, string> files, HashSet<string> folders)
    {
        _files = files;
        _folders = folders;
    }

    /// <summary>The number of files.</summary>
    public int FileCount => _files.Count;

    /// <summary>Reads and hashes the tree of the den at <paramref name="root"/>.</summary>
    public static DenFileTree Scan(string root)
    {
        var tree = new DenFileTree(new Dictionary<string, string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        tree.Walk(root, "", 0, collect: null);
        return tree;
    }

    /// <summary>A copy, to change without changing this one.</summary>
    public DenFileTree Clone() => new(new Dictionary<string, string>(_files, StringComparer.Ordinal), new HashSet<string>(_folders, StringComparer.Ordinal));

    /// <summary>True if a file with this name counts: not hidden, and <c>.md</c>, <c>.yaml</c>, or <c>.yml</c>, in any case.</summary>
    public static bool Counts(string name)
    {
        var extension = Path.GetExtension(name);
        return !name.StartsWith('.') && (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The hash of the file at <paramref name="path"/>, or null if the tree has no such file.</summary>
    public string? Hash(string path) => _files.GetValueOrDefault(path);

    /// <summary>True if the tree has a folder at <paramref name="path"/>.</summary>
    public bool HasFolder(string path) => _folders.Contains(path);

    /// <summary>The paths of the files inside the folder at <paramref name="path"/>, at any depth.</summary>
    public List<string> FilesUnder(string path)
    {
        var prefix = path + "/";
        return _files.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    /// <summary>Sets or, with a null hash, removes the file at <paramref name="path"/>.</summary>
    public void SetFile(string path, string? hash)
    {
        if (hash is null)
        {
            _files.Remove(path);
        }
        else
        {
            _files[path] = hash;
        }
    }

    /// <summary>Removes the folder at <paramref name="path"/> and the folders in it. Files are left for the caller to remove with <see cref="SetFile"/>.</summary>
    public void RemoveFolder(string path)
    {
        var prefix = path + "/";
        _folders.RemoveWhere(f => f == path || f.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Adds the folder at <paramref name="path"/> and the folders in it, read
    /// from disk under <paramref name="root"/>, and returns the paths of the
    /// files in them that count, without adding or hashing them: the caller
    /// reads each one and sets it with <see cref="SetFile"/>.
    /// </summary>
    public List<string> AddFolder(string root, string path)
    {
        var files = new List<string>();
        var depth = path.Split('/').Length;
        if (depth <= MaxDepth + 1)
        {
            _folders.Add(path);
            Walk(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), path + "/", depth, files);
        }

        return files;
    }

    /// <summary>The fingerprint of the tree: the same as <see cref="IndexBuilder.FilesFingerprint"/> of the files it was read from.</summary>
    public string Fingerprint()
    {
        var entries = new List<string>(_files.Count + _folders.Count);
        entries.AddRange(_files.Select(f => $"f {f.Key} {f.Value}"));
        entries.AddRange(_folders.Select(d => $"d {d}"));
        entries.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries))));
    }

    /// <summary>The SHA-256 of <paramref name="bytes"/>, in lowercase hex, as the tree and the index record it.</summary>
    public static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Adds what's in <paramref name="folder"/>: files hashed, or with <paramref name="collect"/>, their paths collected instead.</summary>
    private void Walk(string folder, string relative, int depth, List<string>? collect)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (!Counts(name))
            {
                continue;
            }

            if (collect is null)
            {
                _files[$"{relative}{name}"] = HashOf(File.ReadAllBytes(file));
            }
            else
            {
                collect.Add($"{relative}{name}");
            }
        }

        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(sub);
            if (!name.StartsWith('.'))
            {
                _folders.Add($"{relative}{name}");
                Walk(sub, $"{relative}{name}/", depth + 1, collect);
            }
        }
    }
}
