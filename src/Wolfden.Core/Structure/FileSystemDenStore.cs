using System.Text;

namespace Wolfden.Core.Structure;

/// <summary>
/// A den in a folder on disk, read, written, and watched: the v0 den store
/// (#121). Reads are those of <see cref="FileSystemDenSource"/>. Writes are
/// atomic and keep bytes exactly as given, and every path a write names is
/// checked first, so nothing outside the den root is ever touched (see
/// <see cref="IDenStore"/> and <see cref="DenPaths"/>).
/// </summary>
/// <remarks>
/// <para>
/// Names are matched in exact case, as reads match them, even on a
/// case-insensitive file system. A write whose path differs only in case,
/// or in Unicode form, from a file or folder that's there (<c>Mara.md</c>
/// beside <c>mara.md</c>) is refused with <see cref="DenStoreError.CaseConflict"/>
/// on every OS: on Windows and macOS the two are one file, so the write
/// would change a note other than the one named, and a den with both
/// doesn't clone onto those systems. So is a name that's another name for
/// one that's there, such as a Windows short name (<c>UNIVER~1</c>). A
/// write never goes through a folder link (F-LAY-2), so a link can't lead
/// it out of the den.
/// </para>
/// <para>
/// A file is written to a hidden temporary file in its folder and then
/// moved over the old one, so a reader, or a crash, sees the old file or
/// the new one and never part of either. The temporary file is hidden, so
/// the den, and the index's watcher, ignore it (F-LAY-2).
/// </para>
/// <para>
/// A store lives as long as its host, so each read looks at the disk
/// afresh: a folder renamed meanwhile, by the store or anything else, is
/// read under its new name. A whole-den load, such as a check, reads
/// through <see cref="Snapshot"/>, which keeps the names it finds in each
/// folder for the load, as <see cref="FileSystemDenSource"/> does (#119).
/// </para>
/// </remarks>
public sealed class FileSystemDenStore : IDenStore, IDenChangeFeed
{
    /// <summary>How the temporary files of atomic writes start, so they can be told apart. They're hidden (F-LAY-2).</summary>
    public const string TempPrefix = ".wolfden-write-";

    private const int Attempts = 6;

    private static readonly bool CaseInsensitiveSystem = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>The den whose root folder is <paramref name="root"/>.</summary>
    public FileSystemDenStore(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>The den root's full path.</summary>
    public string Root { get; }

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <summary>Called with the temporary file's full path once it's written, before it replaces the file. Tests use it to fail a write part way.</summary>
    internal Action<string>? BeforeReplace { get; init; }

    /// <summary>Moves a folder: <see cref="Directory.Move"/>. Tests replace it to fail a step of a rename.</summary>
    internal Action<string, string> DirectoryMove { get; init; } = Directory.Move;

    /// <summary>True if the file system takes names that differ only in case as one name. Tests set it to rename as on Windows anywhere.</summary>
    internal bool OneNameForAllCases { get; init; } = CaseInsensitiveSystem;

    /// <summary>
    /// A read-only view of the den for one load, such as a check or a
    /// rebuild: a <see cref="FileSystemDenSource"/>, which keeps the names
    /// it finds in each folder so the load stays linear (#119). Make a new
    /// one for each load.
    /// </summary>
    public FileSystemDenSource Snapshot() => new(Root);

    private IDenSource Reads => Snapshot();

    /// <inheritdoc />
    public bool FileExists(string path) => Reads.FileExists(path);

    /// <inheritdoc />
    public byte[] ReadFile(string path) => Reads.ReadFile(path);

    /// <inheritdoc />
    public byte[]? TryReadFile(string path) => Reads.TryReadFile(path);

    /// <inheritdoc />
    public byte[]? TryReadListedFile(string path) => Reads.TryReadListedFile(path);

    /// <inheritdoc />
    public IEnumerable<string> GetDirectories(string path) => Reads.GetDirectories(path);

    /// <inheritdoc />
    public IEnumerable<string> GetFiles(string path) => Reads.GetFiles(path);

    /// <inheritdoc />
    public bool FolderExists(string path) => Reads.FolderExists(path);

    /// <inheritdoc />
    public IEnumerable<string> GetFolderLinks(string path) => Reads.GetFolderLinks(path);

    /// <inheritdoc />
    public void WriteFile(string path, byte[] bytes, DenWriteMode mode = DenWriteMode.CreateOrReplace)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        DenPaths.CheckWritable(path);
        switch (Lookup(path))
        {
            case Entry.Folder or Entry.FolderLink:
                throw new DenStoreException(DenStoreError.NotAFile, path, $"'{path}' can't be written: it's a folder.");
            case Entry.File when mode == DenWriteMode.Create:
                throw AlreadyExists(path);
            case Entry.Missing when mode == DenWriteMode.Replace:
                throw NotFound(path, "file");
        }

        var full = Full(path);
        var folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $"{TempPrefix}{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            BeforeReplace?.Invoke(temp);
            try
            {
                Run(path, full, "written", () => File.Move(temp, full, overwrite: mode != DenWriteMode.Create));
            }
            catch (IOException e) when (e is not DenStoreException && mode == DenWriteMode.Create && (File.Exists(full) || Directory.Exists(full)))
            {
                // Made by someone else between the look and the move: left alone.
                throw AlreadyExists(path);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        DenPaths.CheckWritable(path);
        switch (Lookup(path))
        {
            case Entry.Missing:
                throw NotFound(path, "file");
            case Entry.Folder or Entry.FolderLink:
                throw new DenStoreException(DenStoreError.NotAFile, path, $"'{path}' can't be deleted as a file: it's a folder.");
        }

        var full = Full(path);
        Run(path, full, "deleted", () => File.Delete(full));
    }

    /// <inheritdoc />
    public void MoveFile(string from, string to)
    {
        DenPaths.CheckWritable(from);
        DenPaths.CheckWritable(to);
        switch (Lookup(from))
        {
            case Entry.Missing:
                throw NotFound(from, "file");
            case Entry.Folder or Entry.FolderLink:
                throw new DenStoreException(DenStoreError.NotAFile, from, $"'{from}' can't be moved as a file: it's a folder.");
        }

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return;
        }

        CheckMoveTarget(from, to);
        var source = Full(from);
        var full = Full(to);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Run(from, source, "moved", () => File.Move(source, full, overwrite: false));
    }

    /// <inheritdoc />
    public void MoveFolder(string from, string to)
    {
        DenPaths.CheckWritable(from);
        DenPaths.CheckWritable(to);
        switch (Lookup(from))
        {
            case Entry.Missing:
                throw NotFound(from, "folder");
            case Entry.File:
                throw new DenStoreException(DenStoreError.NotAFile, from, $"'{from}' can't be moved as a folder: it's a file.");
            case Entry.FolderLink:
                throw FolderLink(from, from);
        }

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return;
        }

        var caseOnly = IsCaseOnlyRename(from, to);
        if (!caseOnly && DenPaths.IsAtOrInside(to, from))
        {
            throw new DenStoreException(DenStoreError.InvalidPath, to, $"'{from}' can't be moved to '{to}': that's inside it.");
        }

        CheckMoveTarget(from, to);
        var source = Full(from);
        var full = Full(to);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (caseOnly && OneNameForAllCases)
        {
            RenameCaseOnly(from, source, full);
        }
        else
        {
            Run(from, source, "moved", () => DirectoryMove(source, full));
        }
    }

    /// <inheritdoc />
    public void DeleteFolder(string path)
    {
        DenPaths.CheckWritable(path);
        switch (Lookup(path))
        {
            case Entry.Missing:
                throw NotFound(path, "folder");
            case Entry.File:
                throw new DenStoreException(DenStoreError.NotAFile, path, $"'{path}' can't be deleted as a folder: it's a file.");
            case Entry.FolderLink:
                throw FolderLink(path, path);
        }

        var full = Full(path);
        var entries = Directory.EnumerateFileSystemEntries(full).Select(e => Path.GetFileName(e)!).Order(StringComparer.Ordinal).ToList();
        if (entries.Count > 0)
        {
            var message = entries.All(e => e.StartsWith('.'))
                ? $"'{path}' can't be deleted: it holds '{entries[0]}', which is hidden, so the den doesn't show it, but it would be lost."
                : $"'{path}' can't be deleted: the folder isn't empty.";
            throw new DenStoreException(DenStoreError.NotEmpty, path, message);
        }

        Run(path, full, "deleted", () => Directory.Delete(full, recursive: false));
    }

    /// <summary>
    /// Watches the den folder and everything in it with a
    /// <see cref="FileSystemWatcher"/>. Paths are reported relative to the
    /// den root, hidden ones included (<see cref="ToDenChange"/>). A full
    /// event buffer is reported as <see cref="IDenChangeObserver.Lost"/>;
    /// any other error of the watcher as <see cref="IDenChangeObserver.Broken"/>,
    /// after which the watch reports nothing more.
    /// </summary>
    public IDisposable Watch(IDenChangeObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new FolderWatch(this, observer);
    }

    /// <summary>
    /// The change a <see cref="FileSystemWatcher"/> event reports, with
    /// den-relative paths, or null if it names nothing in the den. A rename
    /// from outside the den, or from the den root itself, is a creation, and
    /// a rename to outside it is a deletion of the old path.
    /// </summary>
    /// <param name="root">The den root's full path.</param>
    /// <param name="kind">The event's kind.</param>
    /// <param name="fullPath">The path the event names (the new one, for a rename).</param>
    /// <param name="oldFullPath">The old path of a rename, or null.</param>
    public static DenChange? ToDenChange(string root, WatcherChangeTypes kind, string fullPath, string? oldFullPath)
    {
        var changeKind = kind switch
        {
            WatcherChangeTypes.Created => DenChangeKind.Created,
            WatcherChangeTypes.Deleted => DenChangeKind.Deleted,
            WatcherChangeTypes.Renamed => DenChangeKind.Renamed,
            _ => DenChangeKind.Changed,
        };
        var path = DenPath(root, fullPath);
        var old = changeKind == DenChangeKind.Renamed && oldFullPath is not null ? DenPath(root, oldFullPath) : null;
        return (path, old) switch
        {
            (null, null) => null,
            (null, { } gone) => new DenChange(DenChangeKind.Deleted, gone),
            ({ } made, null) when changeKind == DenChangeKind.Renamed => new DenChange(DenChangeKind.Created, made),
            ({ } p, var o) => new DenChange(changeKind, p, o),
        };
    }

    /// <summary>
    /// <paramref name="fullPath"/> relative to the den root, with <c>/</c>,
    /// or null if it's the root itself or outside it. Only the OS's own
    /// separators separate: on Linux a <c>\</c> is part of a name.
    /// </summary>
    public string? DenPath(string fullPath) => DenPath(Root, fullPath);

    /// <summary><see cref="DenPath(string)"/> for the den at <paramref name="root"/>.</summary>
    public static string? DenPath(string root, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(fullPath);
        var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != '/')
        {
            relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
        {
            return null;
        }

        return relative;
    }

    /// <summary>True if <paramref name="full"/> is inside the folder <paramref name="root"/>, which may be a drive or file system root.</summary>
    internal static bool IsInside(string root, string full)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.Length > prefix.Length
            && full.StartsWith(prefix, CaseInsensitiveSystem ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private enum Entry
    {
        Missing,
        File,
        Folder,
        FolderLink,
    }

    /// <summary>
    /// What's at the writable path <paramref name="path"/>, after checking
    /// every folder on the way: each must be a folder, in exact case, and
    /// not a folder link, or be missing (and then so is the rest). A name
    /// that differs only in case or Unicode form from one that's there, or
    /// that's another name for it, is a <see cref="DenStoreError.CaseConflict"/>,
    /// except, as the last name, <paramref name="renaming"/>: the name of
    /// the file or folder being renamed to it.
    /// </summary>
    private Entry Lookup(string path, string? renaming = null)
    {
        var names = path.Split('/');
        var current = Root;
        var walked = "";
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            walked = walked.Length == 0 ? name : $"{walked}/{name}";
            var last = i == names.Length - 1;
            var entries = Entries(current);
            var exact = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            var next = Path.Combine(current, name);
            if (exact is null)
            {
                var variants = entries.Where(e => SameName(e.Name, name)).ToList();
                var other = variants.FirstOrDefault(v => !(last && string.Equals(v.Name, renaming, StringComparison.Ordinal)));
                if (other is not null)
                {
                    var there = Parent(walked) is { Length: > 0 } p ? $"{p}/{other.Name}" : other.Name;
                    throw new DenStoreException(DenStoreError.CaseConflict, path,
                        $"'{path}' can't be written: '{there}' is already there, and names that differ only in case or Unicode form are one file on Windows and macOS.");
                }

                if (variants.Count == 0 && (File.Exists(next) || Directory.Exists(next)))
                {
                    throw new DenStoreException(DenStoreError.CaseConflict, path,
                        $"'{path}' can't be written: '{walked}' is another name for a file or folder that's there under a different name.");
                }

                return Entry.Missing;
            }

            var kind = exact switch
            {
                DirectoryInfo d when FileSystemDenSource.IsFolderLink(d) => Entry.FolderLink,
                DirectoryInfo => Entry.Folder,
                _ => Entry.File,
            };
            if (last)
            {
                return kind;
            }

            switch (kind)
            {
                case Entry.File:
                    throw new DenStoreException(DenStoreError.NotAFile, path, $"'{path}' can't be written: '{walked}' is a file, not a folder.");
                case Entry.FolderLink:
                    throw FolderLink(path, walked);
            }

            current = next;
        }

        return Entry.Missing;
    }

    /// <summary>
    /// Refuses a move to <paramref name="to"/> if anything is there: a file
    /// or folder of that name, or one whose name differs only in case or
    /// Unicode form, unless it's <paramref name="from"/> itself being renamed.
    /// </summary>
    private void CheckMoveTarget(string from, string to)
    {
        var renaming = IsCaseOnlyRename(from, to) ? DenPaths.Name(from) : null;
        if (Lookup(to, renaming) is not Entry.Missing)
        {
            throw AlreadyExists(to);
        }
    }

    /// <summary>
    /// Renames the folder at <paramref name="source"/> to <paramref name="full"/>,
    /// a name that differs only in case, on a file system where the two are
    /// one name. It tries the plain move first. If that doesn't change the
    /// name, it moves the folder to a hidden name and then to the new one,
    /// and if that second step fails, moves it back, so the folder is never
    /// left under the hidden name, out of the den.
    /// </summary>
    private void RenameCaseOnly(string from, string source, string full)
    {
        var name = Path.GetFileName(full);
        try
        {
            Run(from, source, "renamed", () => DirectoryMove(source, full));
            if (NameOnDisk(full) == name)
            {
                return;
            }
        }
        catch (IOException e) when (e is not DenStoreException)
        {
            // Some systems refuse a rename that changes only the case: go through a hidden name.
        }

        var step = Path.Combine(Path.GetDirectoryName(full)!, $"{TempPrefix}{Guid.NewGuid():N}");
        Run(from, source, "renamed", () => DirectoryMove(source, step));
        try
        {
            Run(from, step, "renamed", () => DirectoryMove(step, full));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                Run(from, step, "renamed", () => DirectoryMove(step, source));
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                throw new DenStoreException(DenStoreError.InUse, from,
                    $"'{from}' couldn't be renamed ({e.Message.TrimEnd('.')}), nor put back: it's now the hidden folder '{Path.GetFileName(step)}' beside it. Rename that folder back to '{Path.GetFileName(source)}'.", back);
            }

            throw;
        }
    }

    /// <summary>The name, as it is on disk, of the file or folder at <paramref name="full"/>, matched without case, or null if it's gone.</summary>
    private static string? NameOnDisk(string full)
    {
        var name = Path.GetFileName(full);
        return Entries(Path.GetDirectoryName(full)!).Select(e => e.Name).FirstOrDefault(n => SameName(n, name));
    }

    private static string Parent(string path) => DenPaths.Parent(path);

    private static List<FileSystemInfo> Entries(string full)
    {
        var folder = new DirectoryInfo(full);
        return folder.Exists ? folder.EnumerateFileSystemInfos().ToList() : [];
    }

    /// <summary>True if two names are one name on Windows or macOS: the same but for case or Unicode form.</summary>
    private static bool SameName(string a, string b) =>
        string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);

    private static string Normal(string name)
    {
        try
        {
            return name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not valid UTF-16, as a name on Linux can be: compared as it is.
            return name;
        }
    }

    private static bool IsCaseOnlyRename(string from, string to) =>
        string.Equals(DenPaths.Parent(from), DenPaths.Parent(to), StringComparison.Ordinal)
        && SameName(DenPaths.Name(from), DenPaths.Name(to));

    /// <summary>The full path of the checked path <paramref name="path"/>, which is inside the den root.</summary>
    private string Full(string path)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(Root, full))
        {
            // DenPaths.CheckWritable rules this out; this is the last line of defence.
            throw new DenStoreException(DenStoreError.InvalidPath, path, $"'{path}' can't be written: it's outside the den.");
        }

        return full;
    }

    private static DenStoreException AlreadyExists(string path) =>
        new(DenStoreError.AlreadyExists, path, $"'{path}' can't be written: something is already there.");

    private static DenStoreException NotFound(string path, string what) =>
        new(DenStoreError.NotFound, path, $"There's no {what} at '{path}'.");

    private static DenStoreException FolderLink(string path, string link) =>
        new(DenStoreError.InvalidPath, path, $"'{path}' can't be written: '{link}' is a folder link, and the den doesn't follow links (F-LAY-2).");

    /// <summary>
    /// Runs <paramref name="action"/> on the file or folder at
    /// <paramref name="full"/> (den path <paramref name="path"/>), trying
    /// again while another program holds it for a moment, and turns access
    /// that stays denied, or a file that stays in use, into a
    /// <see cref="DenStoreException"/>.
    /// </summary>
    /// <remarks>
    /// On Windows, virus scanners, indexers, sync clients, and readers such
    /// as the index's watcher hold files briefly, and a file that's open
    /// can't be replaced or deleted: the system says it's in use, or denies
    /// access. So those are tried again, up to <see cref="Attempts"/> times,
    /// for about 300 ms in all. A read-only file isn't: it won't change.
    /// </remarks>
    private static void Run(string path, string full, string what, Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (attempt < Attempts && OperatingSystem.IsWindows() && IsBusy(e) && !IsReadOnlyFile(full))
            {
                Thread.Sleep(20 * attempt);
            }
            catch (UnauthorizedAccessException e)
            {
                var why = IsReadOnlyFile(full) ? "the file is read-only" : "access is denied, or another program has it open";
                throw new DenStoreException(DenStoreError.AccessDenied, path, $"'{path}' can't be {what}: {why}.", e);
            }
            catch (IOException e) when (e is not DenStoreException && IsSharingViolation(e))
            {
                throw new DenStoreException(DenStoreError.InUse, path, $"'{path}' can't be {what}: another program has it open. Close it there and try again.", e);
            }
        }
    }

    private static bool IsBusy(Exception e) => e is UnauthorizedAccessException || (e is IOException io && IsSharingViolation(io));

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION.
    private static bool IsSharingViolation(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;

    private static bool IsReadOnlyFile(string full)
    {
        try
        {
            return File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReadOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string full)
    {
        try
        {
            File.Delete(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is hidden, so the den ignores it.
        }
    }

    /// <summary>One watch of the den folder (<see cref="Watch"/>).</summary>
    /// <remarks>
    /// The observer is called without a lock held here, so it can take its
    /// own locks, and dispose the watch from them, without a deadlock. An
    /// event already on its way when the watch is disposed may still
    /// arrive; observers take that as they take any late event.
    /// </remarks>
    private sealed class FolderWatch : IDisposable
    {
        private readonly string _root;
        private readonly IDenChangeObserver _observer;
        private readonly FileSystemWatcher _watcher;
        private int _stopped;
        private int _disposed;

        public FolderWatch(FileSystemDenStore store, IDenChangeObserver observer)
        {
            _root = store.Root;
            _observer = observer;
            _watcher = new FileSystemWatcher(store.Root);
            try
            {
                _watcher.IncludeSubdirectories = true;
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                _watcher.InternalBufferSize = 64 * 1024;
                _watcher.Created += (_, e) => Report(e.ChangeType, e.FullPath, null);
                _watcher.Changed += (_, e) => Report(e.ChangeType, e.FullPath, null);
                _watcher.Deleted += (_, e) => Report(e.ChangeType, e.FullPath, null);
                _watcher.Renamed += (_, e) => Report(WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath);
                _watcher.Error += (_, e) => OnError(e.GetException());
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
                _watcher.Dispose();
                throw;
            }
        }

        private bool Stopped => Volatile.Read(ref _stopped) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref _stopped, 1);
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        private void Report(WatcherChangeTypes kind, string fullPath, string? oldFullPath)
        {
            if (!Stopped && ToDenChange(_root, kind, fullPath, oldFullPath) is { } change)
            {
                _observer.Changed(change);
            }
        }

        private void OnError(Exception error)
        {
            if (Stopped)
            {
                return;
            }

            if (error is InternalBufferOverflowException)
            {
                _observer.Lost();
                return;
            }

            // On Windows the watcher stops for good; on Linux new folders may go unwatched.
            // Either way this watch is over: the observer starts a new one.
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _observer.Broken(error);
            }
        }
    }
}
