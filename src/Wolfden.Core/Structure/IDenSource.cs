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

    /// <summary>
    /// The names of the folder links (symbolic links and junctions) directly
    /// in <paramref name="path"/>. <see cref="GetDirectories"/> leaves them
    /// out, so the den's walks never follow them; <c>wolfden check</c> lists
    /// them so the notes in them aren't dropped silently (F-LAY-2). None for
    /// a source that has no links.
    /// </summary>
    IEnumerable<string> GetFolderLinks(string path) => [];
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
    /// disk, leaving out folder links (<see cref="IsFolderLink"/>). The path
    /// itself must match in exact case, as for <see cref="FileExists"/>.
    /// </summary>
    public IEnumerable<string> GetDirectories(string path) =>
        ExactPathExists(path, file: false)
            ? new DirectoryInfo(Full(path)).EnumerateDirectories().Where(d => !IsFolderLink(d)).Select(d => d.Name).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <inheritdoc />
    public IEnumerable<string> GetFolderLinks(string path) =>
        ExactPathExists(path, file: false)
            ? new DirectoryInfo(Full(path)).EnumerateDirectories().Where(IsFolderLink).Select(d => d.Name).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    /// True for a folder that's a link to another folder: a symbolic link,
    /// or a junction on Windows. A den's walks skip them, as git does, so
    /// links can't loop, and every note is read from one path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both checks matter. Windows marks more than links as reparse points:
    /// the placeholders of cloud-synced folders (OneDrive, and Dropbox's
    /// online-only folders) are reparse points too, with a cloud-files tag
    /// instead of a link tag, so <see cref="FileSystemInfo.LinkTarget"/> is
    /// null for them. Testing <see cref="FileAttributes.ReparsePoint"/> alone
    /// would skip every folder of a den kept in a synced folder, silently.
    /// </para>
    /// <para>
    /// That case has no automated test: making a cloud placeholder needs a
    /// sync provider registered with Windows' Cloud Files API, which a test
    /// machine doesn't have, and <see cref="FileSystemInfo"/> can't be
    /// stubbed. The tests cover real symbolic links and junctions, and
    /// ordinary folders.
    /// </para>
    /// </remarks>
    public static bool IsFolderLink(FileSystemInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            // ReparsePoint first: it comes with the folder listing, so ordinary folders cost nothing more.
            return folder.Attributes.HasFlag(FileAttributes.ReparsePoint) && folder.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Gone or unreadable: whatever reads it next reports that.
            return false;
        }
    }

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

            var found = last && file
                ? Directory.EnumerateFiles(current).Any(e => string.Equals(Path.GetFileName(e), segments[i], StringComparison.Ordinal))
                : new DirectoryInfo(current).EnumerateDirectories().Any(d => string.Equals(d.Name, segments[i], StringComparison.Ordinal) && !IsFolderLink(d));
            if (!found)
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
