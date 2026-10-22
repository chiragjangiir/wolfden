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

    /// <summary>
    /// The bytes of the file at <paramref name="path"/>, or null if there's
    /// no file there or it can't be read: deleted since it was listed, a
    /// link to a file that's gone or that loops, held open by another
    /// program, or one the user may not read. The den treats such a file as
    /// absent (#112), so one bad file never stops a load. The den's walks
    /// read files with this or <see cref="TryReadListedFile"/>, never with
    /// <see cref="ReadFile"/> alone.
    /// </summary>
    byte[]? TryReadFile(string path)
    {
        if (!FileExists(path))
        {
            return null;
        }

        return TryReadListedFile(path);
    }

    /// <summary>
    /// <see cref="TryReadFile"/> for a path that came from <see cref="GetFiles"/>,
    /// so it needn't be looked up again: the read itself says whether the
    /// file is gone since or can't be read. On disk, looking a path up
    /// lists its folder, so reading a folder's files this way stays linear.
    /// </summary>
    byte[]? TryReadListedFile(string path)
    {
        try
        {
            return ReadFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // FileNotFoundException and DirectoryNotFoundException are IOExceptions too.
            return null;
        }
    }

    /// <summary>
    /// The names of the folders directly in <paramref name="path"/>, or none
    /// if it isn't a folder or can't be listed. A folder that can't be
    /// listed, because the user may not list it or it was deleted meanwhile,
    /// is absent to the den (#116): the listing methods give none for it
    /// rather than throwing, so nothing in it is found. Its name can still
    /// be in its parent's listing, so wherever a folder's name alone would
    /// be reported, as a container folder with no container file
    /// (<c>F-LAY-4</c>), the den asks <see cref="FolderExists"/> first.
    /// </summary>
    IEnumerable<string> GetDirectories(string path);

    /// <summary>The names of the files directly in <paramref name="path"/>, or none if it isn't a folder or can't be listed (#116).</summary>
    IEnumerable<string> GetFiles(string path);

    /// <summary>
    /// True if <paramref name="path"/> is a folder that can be listed now:
    /// it's in its parent's listing (<see cref="GetDirectories"/>), and, on
    /// disk, the user may list it and it wasn't deleted since (#116). The
    /// den asks only before it reports on a folder by its name, so a
    /// folder it can't look in is absent rather than broken.
    /// <see cref="GetDirectories"/> lists folders that can't be listed too,
    /// so this default can't tell; a source on disk, and a wrapper around
    /// one, must answer it from the disk or forward it.
    /// </summary>
    bool FolderExists(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0
            ? GetDirectories("").Contains(path, StringComparer.Ordinal)
            : GetDirectories(path[..slash]).Contains(path[(slash + 1)..], StringComparer.Ordinal);
    }

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
    /// <c>universe.md</c>, so a den loads the same everywhere. A broken link
    /// (<see cref="IsBrokenLink"/>) isn't a file.
    /// </summary>
    public bool FileExists(string path) => ExactPathExists(path, file: true);

    /// <inheritdoc />
    public byte[] ReadFile(string path) => File.ReadAllBytes(Full(path));

    /// <summary>
    /// The folders directly in <paramref name="path"/>, by their names on
    /// disk, leaving out folder links (<see cref="IsFolderLink"/>). The path
    /// itself must match in exact case, as for <see cref="FileExists"/>.
    /// A folder that can't be listed is in the listing, since finding out
    /// costs a look into each one, but listing it gives none; see
    /// <see cref="FolderExists"/>.
    /// </summary>
    public IEnumerable<string> GetDirectories(string path) =>
        ExactPathExists(path, file: false)
            ? Folders(Full(path)).Where(d => !IsFolderLink(d)).Select(d => d.Name).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    /// True if <paramref name="path"/> is a folder, with every name in exact
    /// case, that isn't a folder link and can be listed now (<see cref="CanList"/>, #116).
    /// </summary>
    public bool FolderExists(string path) => path.Length > 0 && ExactPathExists(path, file: false) && CanList(Full(path));

    /// <inheritdoc />
    public IEnumerable<string> GetFolderLinks(string path) =>
        ExactPathExists(path, file: false)
            ? Folders(Full(path)).Where(IsFolderLink).Select(d => d.Name).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    /// True if the folder at the full path <paramref name="full"/> can be
    /// listed now. One that can't, because the user may not list it or it
    /// was deleted, is absent to the den, as a file that can't be read is
    /// (#112, #116). That keeps an empty container folder, which is an
    /// error (F-LAY-4), apart from one that can't be looked in.
    /// </summary>
    public static bool CanList(string full)
    {
        ArgumentNullException.ThrowIfNull(full);
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // DirectoryNotFoundException is an IOException too.
            return false;
        }
    }

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
    /// True for a file that's a symbolic link whose target is missing, isn't
    /// a file, or loops back on itself. The den's walks leave such a link
    /// out, as if it weren't there (#112): there's nothing to read through
    /// it. A link to a file that's there is read as that file.
    /// </summary>
    /// <remarks>
    /// As for <see cref="IsFolderLink"/>, a cloud-sync placeholder is a
    /// reparse point without a link target, so it isn't a link here and is
    /// read as usual.
    /// </remarks>
    public static bool IsBrokenLink(FileSystemInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            // ReparsePoint first: it comes with the folder listing, so ordinary files cost nothing more.
            if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LinkTarget is null)
            {
                return false;
            }

            return file.ResolveLinkTarget(returnFinalTarget: true) is not FileInfo { Exists: true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A link that loops can't be resolved; one that's gone has nothing to read either.
            return true;
        }
    }

    /// <summary>
    /// The files directly in <paramref name="path"/>, by their names on
    /// disk, leaving out broken links (<see cref="IsBrokenLink"/>). The path
    /// itself must match in exact case, as for <see cref="FileExists"/>.
    /// </summary>
    public IEnumerable<string> GetFiles(string path) =>
        ExactPathExists(path, file: false)
            ? Files(Full(path)).Select(f => f.Name).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>The folders directly in the folder <paramref name="full"/>, links included, or none if it's gone or can't be listed.</summary>
    private static List<DirectoryInfo> Folders(string full)
    {
        try
        {
            return new DirectoryInfo(full).EnumerateDirectories().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Deleted or made unreadable since it was looked at: as if it were empty (#116).
            return [];
        }
    }

    /// <summary>
    /// True if the folder <paramref name="full"/> holds a folder named
    /// <paramref name="name"/> exactly that isn't a folder link, or false if
    /// it's gone or can't be listed. It stops at the match, so a path lookup
    /// doesn't list each folder on the way in full.
    /// </summary>
    private static bool HasFolder(string full, string name)
    {
        try
        {
            return new DirectoryInfo(full).EnumerateDirectories().Any(d => string.Equals(d.Name, name, StringComparison.Ordinal) && !IsFolderLink(d));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Deleted or made unreadable since it was looked at (#116).
            return false;
        }
    }

    /// <summary>The files directly in the folder <paramref name="full"/>, leaving out broken links, or none if it's gone or can't be listed.</summary>
    private static List<FileInfo> Files(string full)
    {
        try
        {
            return new DirectoryInfo(full).EnumerateFiles().Where(f => !IsBrokenLink(f)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Deleted or made unreadable since it was looked at: as if it were empty.
            return [];
        }
    }

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
                ? Files(current).Any(f => string.Equals(f.Name, segments[i], StringComparison.Ordinal))
                : HasFolder(current, segments[i]);
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
