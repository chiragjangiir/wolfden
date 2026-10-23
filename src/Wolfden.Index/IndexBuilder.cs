using System.Globalization;
using Microsoft.Data.Sqlite;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;
using Wolfden.Core.Yaml;

namespace Wolfden.Index;

/// <summary>The state of a den's index file, from <see cref="IndexBuilder.Check"/>.</summary>
public enum IndexStatus
{
    /// <summary>The index exists, opens, and was built by this schema for the den's current format.</summary>
    Current,

    /// <summary>There's no index file yet.</summary>
    Missing,

    /// <summary>The file can't be opened or read as an index: corrupt, truncated, or not a database.</summary>
    Corrupt,

    /// <summary>The index was built with a different schema version (<see cref="IndexSchema.Version"/>).</summary>
    SchemaChanged,

    /// <summary>The den's <c>format</c>, or the format this version of Wolfden reads, changed since the index was built.</summary>
    FormatChanged,

    /// <summary>The index was built with other built-in packs (<c>packs/core</c> changed in a Wolfden release).</summary>
    PacksChanged,

    /// <summary>
    /// The index is otherwise current, but the den's files changed since it
    /// was built. Only <see cref="IndexBuilder.CheckFiles"/> reports this.
    /// </summary>
    FilesChanged,
}

/// <summary>
/// Builds a den's SQLite index at <c>.wolfden/index.db</c> (design.md §7)
/// from its files. The index is derived data: a full rebuild from the files
/// is always possible, and an index that's missing, unreadable, or from
/// another schema version is rebuilt rather than repaired or migrated.
/// </summary>
public static class IndexBuilder
{
    /// <summary>The folder at the den root for the index and local state (format.md F-LAY-2).</summary>
    public const string StateFolder = ".wolfden";

    /// <summary>The index file's name in <see cref="StateFolder"/>.</summary>
    public const string IndexFileName = "index.db";

    private const string TempSuffix = ".tmp";

    /// <summary>SQLite's side files, which belong to one database file and must not outlive it.</summary>
    private static readonly string[] SideFiles = ["-journal", "-wal", "-shm"];

    /// <summary>The index file's full path for the den at <paramref name="denRoot"/>.</summary>
    public static string IndexPath(string denRoot)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        return Path.Combine(Path.GetFullPath(denRoot), StateFolder, IndexFileName);
    }

    /// <summary>
    /// Rebuilds the index from the den's files. It builds into a temporary
    /// file next to the index and then moves it over the old one, so a
    /// crash or an error never leaves a half-built index in place: readers
    /// see the old index or the new one. Never writes to the den's own files
    /// (F-ID-6); it writes only inside <c>.wolfden/</c>.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The den root doesn't exist.</exception>
    /// <exception cref="IOException">
    /// The old index stayed open in a query, in this process or another
    /// Wolfden process, so it couldn't be replaced (Windows); or
    /// <c>.wolfden</c> or <c>.wolfden/index.db</c> isn't what it should be,
    /// such as a file where the folder belongs. The message says which.
    /// </exception>
    public static void Rebuild(string denRoot) => Rebuild(denRoot, beforeSwap: null);

    /// <summary>
    /// <see cref="Rebuild(string)"/>, calling <paramref name="beforeSwap"/>
    /// with the temporary file's path once it's built, so tests can fail a
    /// rebuild at the last moment. Returns what the index was built from,
    /// for a file watcher to update it from (#14).
    /// </summary>
    /// <summary>For tests: how many full rebuilds each den root (a full path) has had in this process.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> RebuildCounts = new(StringComparer.Ordinal);

    internal static IndexState Rebuild(string denRoot, Action<string>? beforeSwap)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        var root = Path.GetFullPath(denRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The den folder '{root}' doesn't exist.");
        }

        RebuildCounts.AddOrUpdate(root, 1, (_, n) => n + 1);
        var folder = Path.Combine(root, StateFolder);
        if (File.Exists(folder))
        {
            throw new IOException(
                $"'{folder}' is a file, but Wolfden keeps its index in a folder by that name. Move or rename the file, then try again.");
        }

        var target = Path.Combine(folder, IndexFileName);
        var temp = Path.Combine(folder, $"{IndexFileName}.{Guid.NewGuid():N}{TempSuffix}");
        lock (IndexLock.For(root))
        {
            // Inside the lock, so two rebuilds in this process (such as an MCP
            // server's parallel first calls on a den with no index) don't
            // write the folder's .gitignore at the same time.
            Directory.CreateDirectory(folder);
            WriteGitignore(folder);
            RemoveStaleTempFiles(folder);
            if (Directory.Exists(target))
            {
                throw new IOException(
                    $"'{target}' is a folder, but it should be Wolfden's index file. Move or delete the folder (the index is rebuilt from the den's files), then try again.");
            }

            try
            {
                // Taken before the build reads anything, so an edit made during
                // the build makes the new index look stale, never fresh.
                var tree = DenFileTree.Scan(root);
                var state = Build(root, temp, tree);
                beforeSwap?.Invoke(temp);
                Swap(temp, target);
                return state;
            }
            finally
            {
                TryDelete(temp);
                foreach (var side in SideFiles)
                {
                    TryDelete(temp + side);
                }
            }
        }
    }

    /// <summary>
    /// Builds a full index of the den at <paramref name="denRoot"/> into
    /// <paramref name="path"/>, leaving the den's own index alone, so tests
    /// can compare an updated index with a full rebuild of the same files.
    /// </summary>
    internal static void BuildInto(string denRoot, string path)
    {
        var root = Path.GetFullPath(denRoot);
        Build(root, path, DenFileTree.Scan(root));
    }

    /// <summary>
    /// Says whether the den's index can be used as it is. It opens the file
    /// read-only and never changes it.
    /// </summary>
    public static IndexStatus Check(string denRoot)
    {
        var path = IndexPath(denRoot);
        if (Directory.Exists(path))
        {
            // Not an index; Rebuild reports it clearly.
            return IndexStatus.Corrupt;
        }

        if (!File.Exists(path))
        {
            return IndexStatus.Missing;
        }

        Dictionary<string, string> meta;
        try
        {
            using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly));
            connection.Open();
            using (var check = connection.CreateCommand())
            {
                // Another SQLite file in the index's place isn't an index.
                check.CommandText = "PRAGMA application_id";
                if (check.ExecuteScalar() is not long id || id != IndexSchema.ApplicationId)
                {
                    return IndexStatus.Corrupt;
                }

                check.CommandText = "PRAGMA quick_check";
                if (check.ExecuteScalar() as string != "ok")
                {
                    return IndexStatus.Corrupt;
                }
            }

            meta = ReadMeta(connection);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or InvalidCastException)
        {
            return IndexStatus.Corrupt;
        }

        if (!meta.TryGetValue(IndexSchema.SchemaVersionKey, out var version))
        {
            return IndexStatus.Corrupt;
        }

        if (version != IndexSchema.Version.ToString(CultureInfo.InvariantCulture))
        {
            return IndexStatus.SchemaChanged;
        }

        if (meta.GetValueOrDefault(IndexSchema.BuiltInPacksKey) != BuiltInPacks.Instance.Hash)
        {
            return IndexStatus.PacksChanged;
        }

        return meta.GetValueOrDefault(IndexSchema.DenFormatKey) == DenFormatStamp(Path.GetFullPath(denRoot))
            && meta.GetValueOrDefault(IndexSchema.SupportedFormatKey) == DenStructure.SupportedFormat.ToString(CultureInfo.InvariantCulture)
            ? IndexStatus.Current
            : IndexStatus.FormatChanged;
    }

    /// <summary>
    /// Rebuilds the index if it isn't <see cref="IndexStatus.Current"/>:
    /// missing, corrupt (for example after a sync conflict), or built by
    /// another schema version, with other built-in packs, or for another
    /// den format.
    /// </summary>
    /// <returns>The status before any rebuild.</returns>
    public static IndexStatus EnsureCurrent(string denRoot)
    {
        var status = Check(denRoot);
        if (status != IndexStatus.Current)
        {
            RebuildUnless(denRoot, () => Check(denRoot) == IndexStatus.Current);
        }

        return status;
    }

    /// <summary>
    /// <see cref="Check"/>, and then, for an index that's otherwise current,
    /// whether the den's files changed since it was built:
    /// <see cref="IndexStatus.FilesChanged"/> if they did. It reads and
    /// hashes the files the index reads, which is much faster than a
    /// rebuild, so a one-off command such as <c>wolfden query</c> can use it
    /// where no file watcher (#14) keeps the index up to date.
    /// </summary>
    public static IndexStatus CheckFiles(string denRoot) => CheckFiles(denRoot, out _);

    /// <summary><see cref="CheckFiles(string)"/>, also giving the files fingerprint it worked out, or null if it didn't get that far.</summary>
    private static IndexStatus CheckFiles(string denRoot, out string? fingerprint)
    {
        fingerprint = null;
        var status = Check(denRoot);
        if (status != IndexStatus.Current)
        {
            return status;
        }

        if (RecordedFingerprint(denRoot) is not { } recorded)
        {
            return IndexStatus.Corrupt;
        }

        fingerprint = FilesFingerprint(Path.GetFullPath(denRoot));
        return recorded == fingerprint ? IndexStatus.Current : IndexStatus.FilesChanged;
    }

    /// <summary>The files fingerprint the index records, "" if it records none, or null if the index can't be read.</summary>
    private static string? RecordedFingerprint(string denRoot)
    {
        try
        {
            using var connection = new SqliteConnection(ConnectionString(IndexPath(denRoot), SqliteOpenMode.ReadOnly));
            connection.Open();
            return ReadMeta(connection).GetValueOrDefault(IndexSchema.FilesFingerprintKey) ?? "";
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The longest <see cref="EnsureUpToDate(string)"/> waits for a running watcher to catch up before it rebuilds the index itself.</summary>
    public static readonly TimeSpan WatcherWait = TimeSpan.FromSeconds(1);

    /// <summary>How often <see cref="EnsureUpToDate(string, TimeSpan)"/> looks again while it waits for a watcher.</summary>
    private static readonly TimeSpan WatcherPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Rebuilds the index unless <see cref="CheckFiles"/> says it's
    /// current: usable, and built from the files as they are now. If only
    /// the files changed and a watcher is running (<see cref="DenWatcher.IsWatching"/>),
    /// it first waits up to <see cref="WatcherWait"/> for the watcher to
    /// apply them, since a save is usually in the index well within that
    /// (#108). A full rebuild of a large den takes far longer, and the
    /// watcher would then rebuild again too.
    /// </summary>
    /// <returns>The status before any rebuild or wait.</returns>
    public static IndexStatus EnsureUpToDate(string denRoot) => EnsureUpToDate(denRoot, WatcherWait);

    /// <summary><see cref="EnsureUpToDate(string)"/>, waiting at most <paramref name="watcherWait"/> for a running watcher.</summary>
    /// <returns>The status before any rebuild or wait.</returns>
    public static IndexStatus EnsureUpToDate(string denRoot, TimeSpan watcherWait)
    {
        var status = CheckFiles(denRoot, out var current);
        if (status == IndexStatus.FilesChanged && watcherWait > TimeSpan.Zero && DenWatcher.IsWatching(denRoot))
        {
            // Hashing the den again on every poll would cost more than the
            // wait saves, so poll only the fingerprint the index records,
            // until the watcher has written the one the files have now. Then
            // look at the files once more, in case they changed meanwhile.
            var waited = System.Diagnostics.Stopwatch.StartNew();
            for (var left = watcherWait; left > TimeSpan.Zero; left = watcherWait - waited.Elapsed)
            {
                Thread.Sleep(left < WatcherPoll ? left : WatcherPoll);
                if (RecordedFingerprint(denRoot) == current)
                {
                    if (CheckFiles(denRoot) == IndexStatus.Current)
                    {
                        return status;
                    }

                    break;
                }
            }
        }

        if (status != IndexStatus.Current)
        {
            RebuildUnless(denRoot, () => CheckFiles(denRoot) == IndexStatus.Current);
        }

        return status;
    }

    /// <summary>
    /// Rebuilds the index unless <paramref name="isCurrent"/> says, once the
    /// den's <see cref="IndexLock"/> is held, that it's current after all:
    /// another caller in this process rebuilt it while this one waited for
    /// the lock. So N calls that find the same stale index at once rebuild
    /// it once, not N times.
    /// </summary>
    private static void RebuildUnless(string denRoot, Func<bool> isCurrent)
    {
        lock (IndexLock.For(Path.GetFullPath(denRoot)))
        {
            if (!isCurrent())
            {
                Rebuild(denRoot);
            }
        }
    }

    /// <summary>
    /// A hash of the den's tree as <see cref="DenFileTree"/> sees it: every
    /// file the index can read (<see cref="DenFileTree.IsRead"/>) with the
    /// SHA-256 of its bytes, and the folders that can change the structure,
    /// leaving out names that start with <c>.</c> at any depth, as the
    /// loader does (F-LAY-2). While the hash is the same, a rebuild would
    /// read the same bytes. Other files, such as images or a
    /// <c>README.md</c> at the den root, never change the index, so they
    /// don't change the hash either (#103).
    /// </summary>
    internal static string FilesFingerprint(string root) => DenFileTree.Scan(root).Fingerprint();

    /// <summary>
    /// The connection string for an index file. Pooling is off so that
    /// closing a connection releases the file at once, which the swap needs
    /// on Windows.
    /// </summary>
    internal static string ConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

    /// <summary>Loads the den and writes a full index into the new file <paramref name="temp"/>, returning what it was built from.</summary>
    private static IndexState Build(string root, string temp, DenFileTree tree)
    {
        var state = IndexWriter.Load(root, tree, tree.Fingerprint());
        using (var connection = new SqliteConnection(ConnectionString(temp, SqliteOpenMode.ReadWriteCreate)))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = MEMORY");
            Execute(connection, "PRAGMA synchronous = OFF");
            Execute(connection, $"PRAGMA application_id = {IndexSchema.ApplicationId}");
            using (var db = new IndexDbContext(connection))
            {
                db.Database.EnsureCreated();
                Execute(connection, IndexSchema.CreateSearchTable);

                // Building an index once, after the rows are in, is faster
                // than updating it row by row, so the secondary indexes come
                // off for the load and go back on afterwards, unchanged.
                var indexes = IndexDefinitions(connection);
                foreach (var (name, _) in indexes)
                {
                    Execute(connection, $"DROP INDEX \"{name}\"");
                }

                state.Rows.Insert(db, connection);

                foreach (var (_, sql) in indexes)
                {
                    Execute(connection, sql);
                }
            }

            Execute(connection, "PRAGMA optimize");
        }

        // synchronous = OFF skips SQLite's flushes; flush once here so the
        // file is on disk before it replaces the old index.
        using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Flush(flushToDisk: true);
        }

        return state;
    }

    /// <summary>The model's secondary indexes, by name, with the SQL that creates each one, in a fixed order.</summary>
    private static List<(string Name, string Sql)> IndexDefinitions(SqliteConnection connection)
    {
        var result = new List<(string, string)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL ORDER BY name";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    /// <summary>Moves the new index over the old one, retrying briefly while another process has the old one open.</summary>
    private static void Swap(string temp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A side file of the old index would be read as belonging to
                // the new one, so it goes first.
                foreach (var side in SideFiles)
                {
                    if (File.Exists(target + side))
                    {
                        File.Delete(target + side);
                    }
                }

                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                {
                    throw new IOException(
                        $"The index '{target}' is in use, by a query in this process or by another Wolfden process, so the rebuilt index couldn't replace it. Try again when it's idle.",
                        ex);
                }

                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static Dictionary<string, string> ReadMeta(SqliteConnection connection)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM meta";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
            {
                meta[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return meta;
    }

    /// <summary>
    /// The den's <c>format</c> as written in <c>den.yaml</c>, or an empty
    /// string if there's no <c>den.yaml</c> or no plain <c>format</c> in it.
    /// The index records it when it's built and compares it when it's
    /// opened, so it reads the value the same way both times, whatever else
    /// is wrong with <c>den.yaml</c>.
    /// </summary>
    internal static string DenFormatStamp(byte[]? denYaml) =>
        denYaml is not null && YamlFile.Parse(denYaml).Root?["format"] is YamlScalar { IsNull: false, Style: YamlScalarStyle.Plain } format
            ? format.Text
            : "";

    private static string DenFormatStamp(string root) => DenFormatStamp(((IDenSource)new FileSystemDenSource(root)).TryReadFile("den.yaml"));

    /// <summary>
    /// <c>.wolfden/</c> SHOULD be gitignored (F-LAY-2). A <c>.gitignore</c>
    /// of <c>*</c> inside it ignores the folder without touching the den's
    /// own <c>.gitignore</c>.
    /// </summary>
    private static void WriteGitignore(string folder)
    {
        var path = Path.Combine(folder, ".gitignore");
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            // CreateNew, so a rebuild in another process writing it at the same moment doesn't fail this one.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write("# Wolfden's index and local state: derived from the den's files, never committed.\n*\n"u8);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process wrote it first.
        }
    }

    /// <summary>Removes temporary files that a crashed rebuild left behind, if they're old enough not to be another rebuild's.</summary>
    private static void RemoveStaleTempFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, $"{IndexFileName}.*{TempSuffix}*"))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
            {
                TryDelete(file);
            }
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temporary file is removed by a later rebuild.
        }
    }
}
