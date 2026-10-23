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
/// case-insensitive file system. A write whose path differs only in case
/// from a file or folder that's there (<c>Mara.md</c> beside
/// <c>mara.md</c>) is refused with <see cref="DenStoreError.CaseConflict"/>
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
/// </remarks>
public sealed class FileSystemDenStore : IDenStore, IDenChangeFeed
{
    /// <summary>How the temporary files of atomic writes start, so they can be told apart. They're hidden (F-LAY-2).</summary>
    public const string TempPrefix = ".wolfden-write-";

    private static readonly StringComparison NameCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private FileSystemDenSource _reads;

    /// <summary>The den whose root folder is <paramref name="root"/>.</summary>
    public FileSystemDenStore(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _reads = new FileSystemDenSource(Root);
    }

    /// <summary>The den root's full path.</summary>
    public string Root { get; }

    /// <inheritdoc />
    public bool IsReadOnly => false;

    private IDenSource Reads => _reads;

    /// <summary>Called with the temporary file's full path once it's written, before it replaces the file. Tests use it to fail a write part way.</summary>
    internal Action<string>? BeforeReplace { get; init; }

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
        var target = Lookup(path);
        switch (target)
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
                Retry(() => File.Move(temp, full, overwrite: mode != DenWriteMode.Create));
            }
            catch (IOException) when (mode == DenWriteMode.Create && (File.Exists(full) || Directory.Exists(full)))
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

        Retry(() => File.Delete(Full(path)));
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

        var caseOnly = IsCaseOnlyRename(from, to);
        if (!caseOnly && Lookup(to) is not Entry.Missing)
        {
            throw AlreadyExists(to);
        }

        var full = Full(to);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Retry(() => File.Move(Full(from), full, overwrite: false));
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

        if (!caseOnly && Lookup(to) is not Entry.Missing)
        {
            throw AlreadyExists(to);
        }

        var full = Full(to);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (caseOnly && NameCase == StringComparison.OrdinalIgnoreCase)
        {
            // One folder to the system: rename it through a hidden name.
            var step = Path.Combine(Path.GetDirectoryName(full)!, $"{TempPrefix}{Guid.NewGuid():N}");
            Retry(() => Directory.Move(Full(from), step));
            Retry(() => Directory.Move(step, full));
        }
        else
        {
            Retry(() => Directory.Move(Full(from), full));
        }

        // The read half keeps the names it found in each folder (#119); they changed.
        _reads = new FileSystemDenSource(Root);
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
        if (Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new DenStoreException(DenStoreError.NotEmpty, path, $"'{path}' can't be deleted: the folder isn't empty.");
        }

        Retry(() => Directory.Delete(full, recursive: false));
        _reads = new FileSystemDenSource(Root);
    }

    /// <summary>
    /// Watches the den folder and everything in it with a
    /// <see cref="FileSystemWatcher"/>. Paths are reported relative to the
    /// den root, hidden ones included. A full event buffer is reported as
    /// <see cref="IDenChangeObserver.Lost"/>; any other error of the
    /// watcher as <see cref="IDenChangeObserver.Broken"/>, after which the
    /// watch reports nothing more.
    /// </summary>
    public IDisposable Watch(IDenChangeObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new FolderWatch(this, observer);
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
    /// that differs only in case from one that's there, or that's another
    /// name for it, is a <see cref="DenStoreError.CaseConflict"/>.
    /// </summary>
    private Entry Lookup(string path)
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
                var variant = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (variant is not null)
                {
                    var there = Parent(walked) is { Length: > 0 } p ? $"{p}/{variant.Name}" : variant.Name;
                    throw new DenStoreException(DenStoreError.CaseConflict, path,
                        $"'{path}' can't be written: '{there}' is already there, and names that differ only in case are one file on Windows and macOS.");
                }

                if (File.Exists(next) || Directory.Exists(next))
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

    private static string Parent(string path) => DenPaths.Parent(path);

    private static List<FileSystemInfo> Entries(string full)
    {
        var folder = new DirectoryInfo(full);
        return folder.Exists ? folder.EnumerateFileSystemInfos().ToList() : [];
    }

    private static bool IsCaseOnlyRename(string from, string to) =>
        string.Equals(DenPaths.Parent(from), DenPaths.Parent(to), StringComparison.Ordinal)
        && string.Equals(from, to, StringComparison.OrdinalIgnoreCase);

    /// <summary>The full path of the checked path <paramref name="path"/>, which is inside the den root.</summary>
    private string Full(string path)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, NameCase))
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
    /// Runs <paramref name="action"/>, trying again a few times while
    /// another program holds the file for a moment. On Windows, virus
    /// scanners, indexers, sync clients, and readers such as the index's
    /// watcher hold files briefly, and a file that's open can't be replaced
    /// or deleted: the system says the file is in use, or denies access.
    /// Access that stays denied is reported after the last try.
    /// </summary>
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (attempt < Attempts && OperatingSystem.IsWindows()
                && (e is UnauthorizedAccessException || (e is IOException io && IsSharingViolation(io))))
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    private const int Attempts = 6;

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION.
    private static bool IsSharingViolation(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;

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
        private readonly FileSystemDenStore _store;
        private readonly IDenChangeObserver _observer;
        private readonly FileSystemWatcher _watcher;
        private int _stopped;
        private int _disposed;

        public FolderWatch(FileSystemDenStore store, IDenChangeObserver observer)
        {
            _store = store;
            _observer = observer;
            _watcher = new FileSystemWatcher(store.Root);
            try
            {
                _watcher.IncludeSubdirectories = true;
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                _watcher.InternalBufferSize = 64 * 1024;
                _watcher.Created += (_, e) => Report(DenChangeKind.Created, e.FullPath, null);
                _watcher.Changed += (_, e) => Report(DenChangeKind.Changed, e.FullPath, null);
                _watcher.Deleted += (_, e) => Report(DenChangeKind.Deleted, e.FullPath, null);
                _watcher.Renamed += (_, e) => Report(DenChangeKind.Renamed, e.FullPath, e.OldFullPath);
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

        private void Report(DenChangeKind kind, string fullPath, string? oldFullPath)
        {
            if (Stopped)
            {
                return;
            }

            var path = _store.DenPath(fullPath);
            var old = oldFullPath is null ? null : _store.DenPath(oldFullPath);
            var change = (path, old) switch
            {
                (null, null) => null,
                (null, { } gone) => new DenChange(DenChangeKind.Deleted, gone),
                ({ } made, null) when kind == DenChangeKind.Renamed => new DenChange(DenChangeKind.Created, made),
                ({ } p, var o) => new DenChange(kind, p, o),
            };
            if (change is not null)
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
