using Wolfden.Core.Structure;

namespace Wolfden.Core.Editing;

/// <summary>
/// A den as it would be after some writes: <see cref="Files"/> over the den
/// read through another source. A writer loads the den through it to check
/// files before it saves them (<see cref="DenWriteCheck"/>), so nothing is
/// written until the result is known to load.
/// </summary>
public sealed class OverlayDenSource : IDenSource
{
    private readonly IDenSource _den;

    /// <summary>The den read through <paramref name="den"/>, with <paramref name="files"/> written over it, by den-relative path.</summary>
    public OverlayDenSource(IDenSource den, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(den);
        ArgumentNullException.ThrowIfNull(files);
        _den = den;
        Files = files;
    }

    /// <summary>The files written over the den, by den-relative path.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <inheritdoc />
    public bool FileExists(string path) => Files.ContainsKey(path) || _den.FileExists(path);

    /// <inheritdoc />
    public byte[] ReadFile(string path) => Files.TryGetValue(path, out var bytes) ? (byte[])bytes.Clone() : _den.ReadFile(path);

    /// <inheritdoc />
    public byte[]? TryReadFile(string path) => Files.TryGetValue(path, out var bytes) ? (byte[])bytes.Clone() : _den.TryReadFile(path);

    /// <inheritdoc />
    public byte[]? TryReadListedFile(string path) => Files.TryGetValue(path, out var bytes) ? (byte[])bytes.Clone() : _den.TryReadListedFile(path);

    /// <inheritdoc />
    public IEnumerable<string> GetDirectories(string path) =>
        _den.GetDirectories(path).Union(Below(path).Where(rest => rest.Contains('/')).Select(rest => rest[..rest.IndexOf('/')]), StringComparer.Ordinal);

    /// <inheritdoc />
    public IEnumerable<string> GetFiles(string path) =>
        _den.GetFiles(path).Union(Below(path).Where(rest => !rest.Contains('/')), StringComparer.Ordinal);

    /// <inheritdoc />
    public bool FolderExists(string path) => _den.FolderExists(path) || Below(path).Any();

    /// <inheritdoc />
    public IEnumerable<string> GetFolderLinks(string path) => _den.GetFolderLinks(path);

    /// <summary>The overlay's paths under folder <paramref name="path"/>, relative to it.</summary>
    private IEnumerable<string> Below(string path)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        return Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.Length > prefix.Length).Select(k => k[prefix.Length..]);
    }
}
