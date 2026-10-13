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

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(Full(path));

    /// <inheritdoc />
    public byte[] ReadFile(string path) => File.ReadAllBytes(Full(path));

    /// <inheritdoc />
    public IEnumerable<string> GetDirectories(string path)
    {
        var full = Full(path);
        return Directory.Exists(full)
            ? Directory.EnumerateDirectories(full).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal).ToList()
            : [];
    }

    private string Full(string path) =>
        path.Length == 0 ? Root : Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
}
