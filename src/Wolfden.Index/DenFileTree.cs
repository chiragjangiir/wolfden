using System.Security.Cryptography;
using System.Text;
using Wolfden.Core.Structure;

namespace Wolfden.Index;

/// <summary>
/// The den's tree as the files fingerprint sees it (<see cref="IndexBuilder.FilesFingerprint"/>):
/// the files the index can read (<see cref="IsRead"/>), each with the
/// SHA-256 of its bytes, and the folders under <c>packs/</c> and
/// <c>universes/</c> (<see cref="Tracks"/>). It leaves out names that start
/// with <c>.</c> at any depth (F-LAY-2) and folder links, which the den
/// loader skips too (<see cref="FileSystemDenSource.IsFolderLink"/>), and
/// files that can't be read, which the loader leaves out too (#112). A
/// file watcher (#14) keeps one up to date as files change, so it can
/// record the fingerprint of the files an update read without hashing the
/// whole den again.
/// </summary>
/// <remarks>
/// The tree must hold every file and folder whose change can change what
/// a full rebuild writes; anything more only costs needless rebuilds. A new
/// rule that makes the index read another file, or report on one (a
/// layout warning for a stray file, say), must widen <see cref="IsRead"/>
/// or <see cref="Tracks"/> to match. <c>FingerprintTests</c> checks that
/// every file a rebuild reads is in the tree and that files left out don't
/// change a rebuild.
/// </remarks>
internal sealed class DenFileTree
{
    /// <summary>No real den is this deep; a last guard, as folder links are skipped.</summary>
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
    private static bool Counts(string name)
    {
        var extension = Path.GetExtension(name);
        return !name.StartsWith('.') && (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True for a folder the tree holds: <c>packs/</c>, <c>universes/</c>,
    /// and the folders in them. Every file the index reads apart from
    /// <c>den.yaml</c> is in one of them.
    /// </summary>
    public static bool Tracks(string folder) =>
        folder is "packs" or "universes"
        || folder.StartsWith("packs/", StringComparison.Ordinal)
        || folder.StartsWith("universes/", StringComparison.Ordinal);

    /// <summary>
    /// True for a file the index can read, by its path alone: <c>den.yaml</c>,
    /// any file under <c>packs/</c>, a container file, a <c>.md</c> file under
    /// an <c>entities/</c> folder where a container's would be (format.md
    /// F-LAY-5), and a book's <c>outline.md</c>, with no hidden name on the
    /// way (F-LAY-2). It's a little wider than what a load reads, since it
    /// doesn't know which containers loaded, and that's safe. Other files,
    /// such as a <c>README.md</c> at the den root, a book's <c>drafts/</c>,
    /// or an image, never change the index.
    /// </summary>
    public static bool IsRead(string path)
    {
        var names = path.Split('/');
        if (!Counts(names[^1]) || names.Any(n => n.Length == 0 || n.StartsWith('.')))
        {
            return false;
        }

        if (path == "den.yaml" || names[0] == "packs" && names.Length > 1)
        {
            return true;
        }

        if (names[0] != "universes" || names.Length < 3)
        {
            return false;
        }

        if (IndexUpdater.IsStructureFile(path))
        {
            return true;
        }

        var series = names[2] == "series";
        var inWork = series || names[2] == "books";
        var entity = names[^1].EndsWith(".md", StringComparison.OrdinalIgnoreCase) && (
            names.Length >= 4 && names[2] == "entities"
            || names.Length >= 6 && inWork && names[4] == "entities"
            || names.Length >= 8 && series && names[4] == "books" && names[6] == "entities");
        var outline = names.Length == 5 && names[2] == "books" && names[4] == "outline.md"
            || names.Length == 7 && series && names[4] == "books" && names[6] == "outline.md";
        return entity || outline;
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
        if (depth <= MaxDepth + 1 && Tracks(path))
        {
            _folders.Add(path);
            Walk(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), path + "/", depth, files);
        }

        return files;
    }

    /// <summary>
    /// The fingerprint of the tree: the same as <see cref="IndexBuilder.FilesFingerprint"/>
    /// of the files it was read from. Of the folders, only those whose
    /// arrival or removal can change the structure count
    /// (<see cref="IndexUpdater.ChangesStructure"/>), such as an empty
    /// container folder, which is an error (F-LAY-4); the others hold files
    /// and matter only through them.
    /// </summary>
    public string Fingerprint()
    {
        var entries = new List<string>(_files.Count + _folders.Count);
        entries.AddRange(_files.Select(f => $"f {f.Key} {f.Value}"));
        entries.AddRange(_folders.Where(IndexUpdater.ChangesStructure).Select(d => $"d {d}"));
        entries.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries))));
    }

    /// <summary>The SHA-256 of <paramref name="bytes"/>, in lowercase hex, as the tree and the index record it.</summary>
    public static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// The bytes of the file at the full path <paramref name="file"/>, or
    /// null if it can't be read: deleted since it was listed, a link to a
    /// file that's gone or that loops, held open by another program, or one
    /// the user may not read. The tree, like the den loader
    /// (<see cref="IDenSource.TryReadFile"/>), leaves such a file out (#112).
    /// </summary>
    private static byte[]? TryRead(string file)
    {
        try
        {
            return File.ReadAllBytes(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Adds what's in <paramref name="folder"/>: files hashed, or with <paramref name="collect"/>, their paths collected instead.</summary>
    private void Walk(string folder, string relative, int depth, List<string>? collect)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        List<string> files;
        List<DirectoryInfo> subs;
        try
        {
            files = Directory.EnumerateFiles(folder).ToList();
            subs = new DirectoryInfo(folder).EnumerateDirectories().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Deleted or made unreadable since its parent was listed: as if it were empty.
            return;
        }

        foreach (var file in files)
        {
            var path = $"{relative}{Path.GetFileName(file)}";
            if (!IsRead(path))
            {
                continue;
            }

            if (collect is null)
            {
                // A file that can't be read is absent, as it is to the den loader (#112).
                if (TryRead(file) is { } bytes)
                {
                    _files[path] = HashOf(bytes);
                }
            }
            else
            {
                collect.Add(path);
            }
        }

        foreach (var sub in subs)
        {
            var name = sub.Name;
            if (!name.StartsWith('.') && Tracks($"{relative}{name}") && !FileSystemDenSource.IsFolderLink(sub))
            {
                _folders.Add($"{relative}{name}");
                Walk(sub.FullName, $"{relative}{name}/", depth + 1, collect);
            }
        }
    }
}
