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
    /// True if the build listed the folder that holds <paramref name="path"/>
    /// and didn't find a folder there by that name: it was deleted, or
    /// couldn't be listed, by then (#116). False if it was found, or if its
    /// folder was never listed. The last listing of a folder counts.
    /// </summary>
    public bool ListedWithoutFolder(string path) => ListedWithout(_folders, path);

    /// <summary>As <see cref="ListedWithoutFolder"/>, for a file.</summary>
    public bool ListedWithoutFile(string path) => ListedWithout(_files, path);

    private static bool ListedWithout(Dictionary<string, HashSet<string>> listings, string path)
    {
        var slash = path.LastIndexOf('/');
        var (folder, name) = slash < 0 ? ("", path) : (path[..slash], path[(slash + 1)..]);
        return listings.TryGetValue(folder, out var names) && !names.Contains(name);
    }

    public IEnumerable<string> GetDirectories(string path)
    {
        var names = inner.GetDirectories(path).ToList();
        _folders[path] = names.ToHashSet(StringComparer.Ordinal);
        return names;
    }

    public IEnumerable<string> GetFiles(string path)
    {
        var names = inner.GetFiles(path).ToList();
        _files[path] = names.ToHashSet(StringComparer.Ordinal);
        return names.Where(name => !_missing.Contains(path.Length == 0 ? name : $"{path}/{name}")).ToList();
    }

    public IEnumerable<string> GetFolderLinks(string path) => inner.GetFolderLinks(path);
}
