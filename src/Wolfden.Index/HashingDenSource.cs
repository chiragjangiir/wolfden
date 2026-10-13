using System.Security.Cryptography;
using Wolfden.Core.Structure;

namespace Wolfden.Index;

/// <summary>
/// A den source that remembers each file it reads and the SHA-256 of its
/// bytes, so the index can record exactly what it was built from. It reads
/// each file once and serves later reads from memory, so every part of a
/// rebuild sees the same bytes even if a file changes mid-build.
/// </summary>
internal sealed class HashingDenSource(IDenSource inner) : IDenSource
{
    private readonly Dictionary<string, (byte[] Bytes, string Hash)> _read = new(StringComparer.Ordinal);

    /// <summary>The files read so far, by path, with their hashes.</summary>
    public IEnumerable<(string Path, string Hash)> Files => _read.Select(kv => (kv.Key, kv.Value.Hash));

    public bool FileExists(string path) => _read.ContainsKey(path) || inner.FileExists(path);

    public byte[] ReadFile(string path)
    {
        if (!_read.TryGetValue(path, out var file))
        {
            var bytes = inner.ReadFile(path);
            file = (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            _read[path] = file;
        }

        return (byte[])file.Bytes.Clone();
    }

    public IEnumerable<string> GetDirectories(string path) => inner.GetDirectories(path);

    public IEnumerable<string> GetFiles(string path) => inner.GetFiles(path);
}
