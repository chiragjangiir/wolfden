using System.Globalization;
using Microsoft.Data.Sqlite;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;
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
    /// <exception cref="IOException">The old index stayed open in a query or another process, so it couldn't be replaced (Windows).</exception>
    /// <exception cref="UnauthorizedAccessException">As for <see cref="IOException"/>: Windows reports some sharing violations this way.</exception>
    public static void Rebuild(string denRoot) => Rebuild(denRoot, beforeSwap: null);

    /// <summary>
    /// <see cref="Rebuild(string)"/>, calling <paramref name="beforeSwap"/>
    /// with the temporary file's path once it's built, so tests can fail a
    /// rebuild at the last moment.
    /// </summary>
    internal static void Rebuild(string denRoot, Action<string>? beforeSwap)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        var root = Path.GetFullPath(denRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The den folder '{root}' doesn't exist.");
        }

        var folder = Path.Combine(root, StateFolder);
        Directory.CreateDirectory(folder);
        WriteGitignore(folder);
        RemoveStaleTempFiles(folder);

        var target = Path.Combine(folder, IndexFileName);
        var temp = Path.Combine(folder, $"{IndexFileName}.{Guid.NewGuid():N}{TempSuffix}");
        try
        {
            Build(root, temp);
            beforeSwap?.Invoke(temp);
            Swap(temp, target);
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

    /// <summary>
    /// Says whether the den's index can be used as it is. It opens the file
    /// read-only and never changes it.
    /// </summary>
    public static IndexStatus Check(string denRoot)
    {
        var path = IndexPath(denRoot);
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
                check.CommandText = "PRAGMA quick_check";
                if (check.ExecuteScalar() as string != "ok")
                {
                    return IndexStatus.Corrupt;
                }
            }

            meta = ReadMeta(connection);
        }
        catch (SqliteException)
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

        return meta.GetValueOrDefault(IndexSchema.DenFormatKey) == DenFormatStamp(Path.GetFullPath(denRoot))
            && meta.GetValueOrDefault(IndexSchema.SupportedFormatKey) == DenStructure.SupportedFormat.ToString(CultureInfo.InvariantCulture)
            ? IndexStatus.Current
            : IndexStatus.FormatChanged;
    }

    /// <summary>
    /// Rebuilds the index if it isn't <see cref="IndexStatus.Current"/>:
    /// missing, corrupt (for example after a sync conflict), or built by
    /// another schema version or for another den format.
    /// </summary>
    /// <returns>The status before any rebuild.</returns>
    public static IndexStatus EnsureCurrent(string denRoot)
    {
        var status = Check(denRoot);
        if (status != IndexStatus.Current)
        {
            Rebuild(denRoot);
        }

        return status;
    }

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

    private static void Build(string root, string temp)
    {
        using (var connection = new SqliteConnection(ConnectionString(temp, SqliteOpenMode.ReadWriteCreate)))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = MEMORY");
            Execute(connection, "PRAGMA synchronous = OFF");
            using (var db = new IndexDbContext(connection))
            {
                db.Database.EnsureCreated();
                Execute(connection, IndexSchema.CreateSearchTable);
                IndexWriter.Write(root, db, connection);
            }

            Execute(connection, "PRAGMA optimize");
        }

        // synchronous = OFF skips SQLite's flushes; flush once here so the
        // file is on disk before it replaces the old index.
        using var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
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
            meta[reader.GetString(0)] = reader.GetString(1);
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

    private static string DenFormatStamp(string root)
    {
        var path = Path.Combine(root, "den.yaml");
        return DenFormatStamp(File.Exists(path) ? File.ReadAllBytes(path) : null);
    }

    /// <summary>
    /// <c>.wolfden/</c> SHOULD be gitignored (F-LAY-2). A <c>.gitignore</c>
    /// of <c>*</c> inside it ignores the folder without touching the den's
    /// own <c>.gitignore</c>.
    /// </summary>
    private static void WriteGitignore(string folder)
    {
        var path = Path.Combine(folder, ".gitignore");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "# Wolfden's index and local state: derived from the den's files, never committed.\n*\n");
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
