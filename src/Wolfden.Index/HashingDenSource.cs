using System.Security.Cryptography;
using Wolfden.Core.Structure;

namespace Wolfden.Index;

/// <summary>
/// A den source that remembers each file it reads and the SHA-256 of its
/// bytes, so the index can record exactly what it was built from. It reads
/// each file once and serves later reads from memory, so every part of a
/// rebuild sees the same bytes even if a file changes mid-build. A file
/// that wasn't there or couldn't be read stays absent for the rest of the
/// build (#112). It also remembers what each folder it listed held, so the
/// index can leave out of its files fingerprint a folder the build couldn't
/// list (#116).
/// </summary>
internal sealed class HashingDenSource(IDenSource inner) : IDenSource
{
    private readonly Dictionary<string, (byte[] Bytes, string Hash)> _read = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _absentFolders = new(StringComparer.Ordinal);

    /// <summary>The files read so far, by path, with their hashes.</summary>
    public IEnumerable<(string Path, string Hash)> Files => _read.Select(kv => (kv.Key, kv.Value.Hash));

    /// <summary>
    /// The paths the build tried to read and found nothing at: no file, or
    /// one that couldn't be read, which the build left out. The files
    /// fingerprint mustn't count them either, or the index would look
    /// current while it lacks them.
    /// </summary>
    public IEnumerable<string> Missing => _missing;

    public bool FileExists(string path) => _read.ContainsKey(path) || !_missing.Contains(path) && inner.FileExists(path);

    public byte[] ReadFile(string path) =>
        TryReadListedFile(path) ?? throw new FileNotFoundException($"'{path}' doesn't exist or couldn't be read.", path);

    public byte[]? TryReadFile(string path) => Read(path, inner.TryReadFile);

    public byte[]? TryReadListedFile(string path) => Read(path, inner.TryReadListedFile);

    private byte[]? Read(string path, Func<string, byte[]?> read)
    {
        if (!_read.TryGetValue(path, out var file))
        {
            if (_missing.Contains(path) || read(path) is not { } bytes)
            {
                _missing.Add(path);
                return null;
            }

            file = (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            _read[path] = file;
        }

        return (byte[])file.Bytes.Clone();
    }

    /// <summary>
    /// True if the build didn't see the folder at <paramref name="path"/>:
    /// it asked about it and found it absent (<see cref="IDenSource.FolderExists"/>),
    /// or it listed the folder that holds it and no listing had it, because
    /// it was deleted or couldn't be listed by then (#116). False if any
    /// listing had it, so a folder deleted between two listings, after the
    /// build read it, still counts, and the index looks stale rather than
    /// current; or if its folder was never listed.
    /// </summary>
    public bool DidntSeeFolder(string path) => _absentFolders.Contains(path) || ListedWithout(_folders, path);

    /// <summary>As <see cref="DidntSeeFolder"/>, for a file: no listing of its folder had it.</summary>
    public bool DidntSeeFile(string path) => ListedWithout(_files, path);

    private static bool ListedWithout(Dictionary<string, HashSet<string>> listings, string path)
    {
        var slash = path.LastIndexOf('/');
        var (folder, name) = slash < 0 ? ("", path) : (path[..slash], path[(slash + 1)..]);
        return listings.TryGetValue(folder, out var names) && !names.Contains(name);
    }

    public bool FolderExists(string path)
    {
        if (inner.FolderExists(path))
        {
            return true;
        }

        _absentFolders.Add(path);
        return false;
    }

    public IEnumerable<string> GetDirectories(string path)
    {
        var names = inner.GetDirectories(path).ToList();
        Remember(_folders, path, names);
        return names;
    }

    public IEnumerable<string> GetFiles(string path)
    {
        var names = inner.GetFiles(path).ToList();
        Remember(_files, path, names);
        return names.Where(name => !_missing.Contains(path.Length == 0 ? name : $"{path}/{name}")).ToList();
    }

    /// <summary>Adds a listing to every earlier one of the same folder: a name counts as seen if any listing had it.</summary>
    private static void Remember(Dictionary<string, HashSet<string>> listings, string path, List<string> names)
    {
        if (listings.TryGetValue(path, out var seen))
        {
            seen.UnionWith(names);
        }
        else
        {
            listings[path] = names.ToHashSet(StringComparer.Ordinal);
        }
    }

    public IEnumerable<string> GetFolderLinks(string path) => inner.GetFolderLinks(path);
}
