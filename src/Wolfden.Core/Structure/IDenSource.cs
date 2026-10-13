namespace Wolfden.Core.Structure;

/// <summary>
/// Read-only access to a den's files. Paths are relative to the den root and
/// use <c>/</c> as the separator; the root itself is the empty string.
/// </summary>
public interface IDenSource
{
    /// <summary>True if a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>The bytes of the file at <paramref name="path"/>.</summary>
    byte[] ReadFile(string path);

    /// <summary>The names of the folders directly in <paramref name="path"/>, or none if it isn't a folder.</summary>
    IEnumerable<string> GetDirectories(string path);

    /// <summary>The names of the files directly in <paramref name="path"/>, or none if it isn't a folder.</summary>
    IEnumerable<string> GetFiles(string path);
}

/// <summary>A den on disk.</summary>
public sealed class FileSystemDenSource : IDenSource
{
    /// <summary>Reads the den whose root folder is <paramref name="root"/>.</summary>
    public FileSystemDenSource(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The den root's full path.</summary>
    public string Root { get; }

    /// <summary>
    /// True if a file exists at <paramref name="path"/>, with every name on
    /// the path in exactly that case. Names are matched as on Linux even on a
    /// case-insensitive file system (Windows, macOS): <c>Universe.md</c> isn't
    /// <c>universe.md</c>, so a den loads the same everywhere.
    /// </summary>
    public bool FileExists(string path) => ExactPathExists(path, file: true);

    /// <inheritdoc />
    public byte[] ReadFile(string path) => File.ReadAllBytes(Full(path));

    /// <summary>
    /// The folders directly in <paramref name="path"/>, by their names on
    /// disk. The path itself must match in exact case, as for <see cref="FileExists"/>.
    /// </summary>
    public IEnumerable<string> GetDirectories(string path) =>
        ExactPathExists(path, file: false)
            ? Directory.EnumerateDirectories(Full(path)).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    /// The files directly in <paramref name="path"/>, by their names on
    /// disk. The path itself must match in exact case, as for <see cref="FileExists"/>.
    /// </summary>
    public IEnumerable<string> GetFiles(string path) =>
        ExactPathExists(path, file: false)
            ? Directory.EnumerateFiles(Full(path)).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToList()
            : [];

    private bool ExactPathExists(string path, bool file)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = Root;
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            if (!Directory.Exists(current))
            {
                return false;
            }

            var entries = last && file ? Directory.EnumerateFiles(current) : Directory.EnumerateDirectories(current);
            if (!entries.Any(e => string.Equals(Path.GetFileName(e), segments[i], StringComparison.Ordinal)))
            {
                return false;
            }

            current = Path.Combine(current, segments[i]);
        }

        return file ? segments.Length > 0 : Directory.Exists(current);
    }

    private string Full(string path) =>
        path.Length == 0 ? Root : Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
}
