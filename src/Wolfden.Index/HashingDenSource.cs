using System.Security.Cryptography;
using Wolfden.Core.Structure;

namespace Wolfden.Index;

/// <summary>
/// A den source that remembers each file it reads and the SHA-256 of its
/// bytes, so the index can record exactly what it was built from. It reads
/// each file once and serves later reads from memory, so every part of a
/// rebuild sees the same bytes even if a file changes mid-build. A file
/// that wasn't there or couldn't be read stays absent for the rest of the
/// build (#112).
/// </summary>
internal sealed class HashingDenSource(IDenSource inner) : IDenSource
{
    private readonly Dictionary<string, (byte[] Bytes, string Hash)> _read = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);

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

    public IEnumerable<string> GetDirectories(string path) => inner.GetDirectories(path);

    public IEnumerable<string> GetFiles(string path) =>
        inner.GetFiles(path).Where(name => !_missing.Contains(path.Length == 0 ? name : $"{path}/{name}"));

    public IEnumerable<string> GetFolderLinks(string path) => inner.GetFolderLinks(path);
}
