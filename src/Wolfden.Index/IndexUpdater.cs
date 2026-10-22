using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;

namespace Wolfden.Index;

/// <summary>
/// Keeps a den's index up to date from batches of changed paths (#14),
/// reading only what changed where that's safe and rebuilding where it
/// isn't. It's the part of <see cref="DenWatcher"/> that doesn't depend on
/// file events, so tests can drive it with batches of their own.
/// </summary>
/// <remarks>
/// <para>
/// What's safe to do incrementally: an entity file or an <c>outline.md</c>
/// that's added, changed, deleted, or renamed, and a folder under an
/// <c>entities/</c> folder. Files the index doesn't read
/// (<see cref="DenFileTree.IsRead"/>) change nothing, not even the files
/// fingerprint. The changed files are
/// read again and the den-wide steps that depend on other files are
/// redone: duplicate ids (F-ID-3), and resolving and checking every link.
/// What isn't: <c>den.yaml</c>, a pack file, a container file, or a folder
/// added, moved, or deleted where it can change the structure (anywhere
/// under <c>universes/</c> outside an <c>entities/</c> folder, or under
/// <c>packs/</c>). Those change what every file means (types, scopes,
/// story order, positions), so they get a full rebuild, as do lost events,
/// very large batches, and an index someone else rebuilt meanwhile.
/// </para>
/// <para>
/// Either way the index ends up as a full rebuild of the same files would
/// make it, apart from the ids of rows that came from single files
/// (<see cref="IndexIds"/>). Each batch is written in one transaction, so
/// readers see the index before the batch or after it, never part of it.
/// </para>
/// </remarks>
internal sealed class IndexUpdater
{
    private readonly string _root;
    private readonly Dictionary<string, int> _unreadable = new(StringComparer.Ordinal);
    private IndexState? _state;

    /// <summary>A batch that changes more indexed files than this, and than <see cref="RebuildShare"/> of them, is rebuilt instead.</summary>
    internal int RebuildFiles { get; init; } = 200;

    /// <summary>See <see cref="RebuildFiles"/>.</summary>
    internal double RebuildShare { get; init; } = 0.25;

    /// <summary>
    /// How many batches in a row a file may fail to read, as while an editor
    /// writes it, before the batch rebuilds the index instead. A full load
    /// leaves out a file it can't read (#112), so the index then matches a
    /// full rebuild again; a later event brings the file in once it can be
    /// read. A file that's gone, or a broken link, is absent at once.
    /// </summary>
    internal int ReadAttempts { get; init; } = 10;

    /// <param name="denRoot">The den's folder.</param>
    public IndexUpdater(string denRoot)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        _root = Path.GetFullPath(denRoot);
    }

    /// <summary>What the index holds now, or null before the first build.</summary>
    internal IndexState? State => _state;

    /// <summary>Rebuilds the index from scratch, as the first build or after a failure.</summary>
    public IndexUpdate Rebuild(string reason)
    {
        var watch = Stopwatch.StartNew();
        _state = null;
        _unreadable.Clear();
        var state = IndexBuilder.Rebuild(_root, beforeSwap: null);
        _state = state;
        return new IndexUpdate(IndexUpdateKind.Rebuilt, reason, [], state.Rows.Count, watch.Elapsed, Summary(state));
    }

    /// <summary>Forgets what the index holds, so the next batch rebuilds it, as after a failure.</summary>
    public void Invalidate() => _state = null;

    /// <summary>
    /// Brings the index up to date with the files after <paramref name="batch"/>.
    /// Returns what it did, or null if nothing the index holds changed (the
    /// files fingerprint is still brought up to date). <paramref name="retry"/>
    /// gets the paths of files that couldn't be read, such as files an editor
    /// was still writing; the caller should try them again shortly.
    /// </summary>
    public IndexUpdate? Apply(ChangeBatch batch, out IReadOnlyList<string> retry)
    {
        ArgumentNullException.ThrowIfNull(batch);
        retry = [];
        if (_state is not { } state)
        {
            return Rebuild("the index wasn't known to be up to date");
        }

        if (batch.Overflowed)
        {
            return Rebuild("too many changes at once for the file watcher to follow");
        }

        var watch = Stopwatch.StartNew();
        var listing = new ExactListing(_root);
        var tree = state.Tree.Clone();
        var treeChanged = false;

        // Folders that were added, moved, or deleted: look at what's in them.
        // The folders above each changed path are looked at too, parents
        // first (in ordinal order a folder comes before what's in it): a
        // folder can stop being one, or become one again, without an event
        // of its own, as when its permissions change so it can't be listed
        // (#116). Where the tree and the disk agree on them, as they almost
        // always do, that costs only listings this batch makes anyway.
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in batch.Paths)
        {
            files.Add(path);
            folders.Add(path);
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            {
                folders.Add(path[..slash]);
            }
        }

        foreach (var path in folders)
        {
            if (!DenFileTree.Tracks(path))
            {
                // No file the index reads can be in it (a drafts/ folder at the den root, say).
                continue;
            }

            var wasFolder = tree.HasFolder(path);
            var isFolder = listing.IsFolder(path);
            if (!wasFolder && !isFolder || wasFolder && isFolder && !batch.Moved.Contains(path))
            {
                // Not a folder, or one whose contents changed: those files have events of their own.
                continue;
            }

            if (ChangesStructure(path))
            {
                return Rebuild($"the folder {path} was added, moved, or deleted");
            }

            if (wasFolder)
            {
                files.UnionWith(tree.FilesUnder(path));
                tree.RemoveFolder(path);
            }

            if (isFolder)
            {
                files.UnionWith(tree.AddFolder(_root, path));
            }

            treeChanged = true;
        }

        var overlay = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var entities = new HashSet<string>(StringComparer.Ordinal);
        var outlines = new HashSet<string>(StringComparer.Ordinal);
        var unread = new List<string>();
        var outlinePaths = state.Structure.Universes.SelectMany(u => u.Books).Select(b => $"{b.Path}/outline.md").ToHashSet(StringComparer.Ordinal);
        foreach (var path in files)
        {
            if (!DenFileTree.IsRead(path))
            {
                // Not read by the index, so not in its tree either: nothing to do.
                continue;
            }

            byte[]? bytes = null;
            if (listing.IsFile(path))
            {
                var full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    bytes = File.ReadAllBytes(full);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException || e is IOException && FileSystemDenSource.IsBrokenLink(new FileInfo(full)))
                {
                    // Deleted since it was listed, or a link to a file that's
                    // gone or loops: absent now, as a full load would find it (#112).
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Still being written, say: look at it next time, a few
                    // times, and then rebuild, which leaves it out if it
                    // still can't be read.
                    var attempts = _unreadable[path] = _unreadable.GetValueOrDefault(path) + 1;
                    if (attempts > ReadAttempts)
                    {
                        return Rebuild($"{path} couldn't be read");
                    }

                    unread.Add(path);
                    continue;
                }
            }

            _unreadable.Remove(path);

            var hash = bytes is null ? null : DenFileTree.HashOf(bytes);
            if (tree.Hash(path) == hash)
            {
                continue;
            }

            tree.SetFile(path, hash);
            treeChanged = true;
            overlay[path] = bytes;
            if (IsStructureFile(path))
            {
                return Rebuild($"{path} {(hash is null ? "was deleted" : "changed")}");
            }

            if (EntityCatalog.EntityFileScope(state.Structure, path) is not null)
            {
                entities.Add(path);
            }
            else if (outlinePaths.Contains(path))
            {
                outlines.Add(path);
            }
        }

        retry = unread;
        var changed = entities.Count + outlines.Count;
        if (changed > RebuildFiles && changed > RebuildShare * state.FileHashes.Count)
        {
            return Rebuild($"{changed} files changed at once");
        }

        if (!treeChanged)
        {
            return null;
        }

        IndexState next;
        if (changed == 0)
        {
            if (state.Rows.Meta.GetValueOrDefault(IndexSchema.FilesFingerprintKey) == tree.Fingerprint())
            {
                // Only a folder the fingerprint leaves out, such as a book's
                // drafts/: keep following it, but there's nothing to write.
                _state = WithRows(state, state.Rows, tree, state.WriteId);
                return null;
            }

            // Nothing the index holds changed, only the files fingerprint.
            next = WithRows(state, IndexRows.SameAs(state.Rows), tree, Guid.NewGuid().ToString("N"));
        }
        else
        {
            var source = new OverlayDenSource(new FileSystemDenSource(_root), overlay);
            next = IndexWriter.Update(state, source, entities, outlines, tree);
        }

        int written;
        try
        {
            written = Write(state, next);
        }
        catch (StaleIndexException e)
        {
            return Rebuild(e.Message);
        }

        _state = next;
        if (changed == 0)
        {
            return null;
        }

        var paths = entities.Concat(outlines).Order(StringComparer.Ordinal).ToList();
        return new IndexUpdate(IndexUpdateKind.Updated, null, paths, written, watch.Elapsed, Summary(next));
    }

    /// <summary>What <paramref name="state"/> holds.</summary>
    internal static IndexSummary Summary(IndexState state)
    {
        var diagnostics = state.Rows.Table(typeof(DiagnosticRecord)).Values.Cast<DiagnosticRecord>().ToList();
        return new IndexSummary(
            state.Structure.IsLoaded,
            state.FileHashes.Count,
            state.Structure.Universes.Count,
            state.Catalog.Entities.Count,
            state.Links.Links.Count,
            diagnostics.Count(d => d.Severity == "error"),
            diagnostics.Count(d => d.Severity == "warning"));
    }

    /// <summary>
    /// True for a folder whose arrival, move, or removal can change the
    /// den's structure: <c>packs/</c> and anything in it, and the folders
    /// the den loader lists (format.md F-LAY-4): <c>universes/</c>, a
    /// universe, its <c>series/</c> and <c>books/</c>, a series or book in
    /// them, and a series' <c>books/</c> and the books in it. An empty one
    /// of those still matters: a container folder without its container
    /// file is an error. Other folders, such as <c>entities/</c> and those
    /// in it, or a book's <c>drafts/</c>, only hold files, which are looked
    /// at one by one.
    /// </summary>
    internal static bool ChangesStructure(string folder)
    {
        var names = folder.Split('/');
        if (names[0] == "packs")
        {
            return true;
        }

        if (names[0] != "universes")
        {
            return false;
        }

        return names.Length switch
        {
            1 or 2 => true,
            3 or 4 => names[2] is "series" or "books",
            5 or 6 => names[2] == "series" && names[4] == "books",
            _ => false,
        };
    }

    /// <summary>
    /// True for a file that's part of the den's structure: <c>den.yaml</c>,
    /// a pack file, or a container file where the den loader reads one
    /// (<c>universes/u/universe.md</c>, <c>…/series/s/series.md</c>,
    /// <c>…/books/b/book.md</c>). An entity note that happens to be called
    /// <c>book.md</c> isn't one.
    /// </summary>
    internal static bool IsStructureFile(string path)
    {
        if (path == "den.yaml" || path.StartsWith("packs/", StringComparison.Ordinal))
        {
            return true;
        }

        var names = path.Split('/');
        if (names[0] != "universes")
        {
            return false;
        }

        return names.Length switch
        {
            3 => names[2] == "universe.md",
            5 => names[2] == "series" && names[4] == "series.md" || names[2] == "books" && names[4] == "book.md",
            7 => names[2] == "series" && names[4] == "books" && names[6] == "book.md",
            _ => false,
        };
    }

    private static IndexState WithRows(IndexState state, IndexRows rows, DenFileTree tree, string writeId)
    {
        rows.Meta[IndexSchema.FilesFingerprintKey] = tree.Fingerprint();
        rows.Meta[IndexSchema.WriteIdKey] = writeId;
        return new IndexState
        {
            Root = state.Root,
            Structure = state.Structure,
            Catalog = state.Catalog,
            Links = state.Links,
            Outlines = state.Outlines,
            FileHashes = state.FileHashes,
            Rows = rows,
            Ids = state.Ids,
            Entities = state.Entities,
            LinkRows = state.LinkRows,
            ContainerIds = state.ContainerIds,
            ChapterIds = state.ChapterIds,
            Tree = tree,
            WriteId = writeId,
        };
    }

    /// <summary>
    /// Writes the difference between <paramref name="before"/> and
    /// <paramref name="after"/> in one transaction, if the index still holds
    /// what <paramref name="before"/> wrote.
    /// </summary>
    /// <exception cref="StaleIndexException">The index isn't the one <paramref name="before"/> wrote, or can't be opened.</exception>
    private int Write(IndexState before, IndexState after)
    {
        var path = IndexBuilder.IndexPath(_root);
        lock (IndexLock.For(_root))
        {
            try
            {
                using var connection = new SqliteConnection(IndexBuilder.ConnectionString(path, SqliteOpenMode.ReadWrite));
                connection.Open();
                // BEGIN IMMEDIATE: take the write lock before reading the write id, so no other writer can slip in between.
                using var transaction = connection.BeginTransaction(deferred: false);
                using (var check = connection.CreateCommand())
                {
                    check.Transaction = transaction;
                    check.CommandText = "SELECT value FROM meta WHERE key = $key";
                    check.Parameters.AddWithValue("$key", IndexSchema.WriteIdKey);
                    if (check.ExecuteScalar() as string != before.WriteId)
                    {
                        throw new StaleIndexException("another command rebuilt the index meanwhile");
                    }
                }

                int written;
                using (var db = new IndexDbContext(connection))
                {
                    written = after.Rows.ApplyChanges(before.Rows, db, connection, transaction);
                }

                transaction.Commit();
                transaction.Dispose();
                connection.Dispose();

                AfterCommit?.Invoke();

                // On Linux, another process's rebuild can move its new index
                // over this one while the transaction waits or runs (#106):
                // the batch then went into the replaced file. Look again at
                // the file that's there now, and if it isn't the one just
                // written, the caller rebuilds.
                if (ReadWriteId(path) != after.WriteId)
                {
                    throw new StaleIndexException("another command rebuilt the index meanwhile");
                }

                return written;
            }
            catch (SqliteException e)
            {
                throw new StaleIndexException($"the index couldn't be updated ({e.Message.TrimEnd('.')})", e);
            }
        }
    }

    /// <summary>Called after a batch is committed, before the index is looked at again. Tests use it to replace the index at that moment.</summary>
    internal Action? AfterCommit { get; init; }

    private static string? ReadWriteId(string path)
    {
        using var connection = new SqliteConnection(IndexBuilder.ConnectionString(path, SqliteOpenMode.ReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", IndexSchema.WriteIdKey);
        return command.ExecuteScalar() as string;
    }

    /// <summary>The index can't be updated in place, so it must be rebuilt.</summary>
    private sealed class StaleIndexException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>
    /// A den source that serves the bytes this batch already read for the
    /// changed files, so a file is parsed exactly as it was hashed.
    /// </summary>
    private sealed class OverlayDenSource(IDenSource inner, IReadOnlyDictionary<string, byte[]?> overlay) : IDenSource
    {
        public bool FileExists(string path) => overlay.TryGetValue(path, out var bytes) ? bytes is not null : inner.FileExists(path);

        public byte[] ReadFile(string path) =>
            overlay.TryGetValue(path, out var bytes)
                ? (byte[])(bytes ?? throw new FileNotFoundException($"'{path}' was deleted.", path)).Clone()
                : inner.ReadFile(path);

        public IEnumerable<string> GetDirectories(string path) => inner.GetDirectories(path);

        public IEnumerable<string> GetFiles(string path) => inner.GetFiles(path);

        public IEnumerable<string> GetFolderLinks(string path) => inner.GetFolderLinks(path);

        public bool FolderExists(string path) => inner.FolderExists(path);
    }

    /// <summary>
    /// Whether paths exist with every name in exactly that case, as the den
    /// loader matches them (<see cref="FileSystemDenSource"/>), listing
    /// each folder once per batch. Folder links aren't folders here, as the
    /// loader skips them, so nothing under one is a file either. Nor is a
    /// folder that can't be listed, which the loader leaves out too (#116).
    /// </summary>
    private sealed class ExactListing(string root)
    {
        private readonly Dictionary<string, (HashSet<string> Files, HashSet<string> Folders)?> _folders = new(StringComparer.Ordinal);

        public bool IsFile(string path)
        {
            var (parent, name) = Split(path);
            return List(parent)?.Files.Contains(name) == true;
        }

        public bool IsFolder(string path) => List(path) is not null;

        /// <summary>What's in <paramref name="folder"/>, or null if it isn't a folder in its parent's listing or can't be listed itself.</summary>
        private (HashSet<string> Files, HashSet<string> Folders)? List(string folder)
        {
            if (_folders.TryGetValue(folder, out var listing))
            {
                return listing;
            }

            listing = null;
            var (parent, name) = Split(folder);
            if (folder.Length == 0 || List(parent)?.Folders.Contains(name) == true)
            {
                var full = folder.Length == 0 ? root : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    listing = (
                        Directory.EnumerateFiles(full).Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.Ordinal),
                        new DirectoryInfo(full).EnumerateDirectories().Where(d => !FileSystemDenSource.IsFolderLink(d)).Select(d => d.Name).ToHashSet(StringComparer.Ordinal));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Deleted while we looked, or one the user may not list: as if it weren't there (#116).
                }
            }

            _folders[folder] = listing;
            return listing;
        }

        private static (string Parent, string Name) Split(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? ("", path) : (path[..slash], path[(slash + 1)..]);
        }
    }
}
