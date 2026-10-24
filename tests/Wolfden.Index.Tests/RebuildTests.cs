using System.Text;
using Microsoft.Data.Sqlite;
using Wolfden.Core.Types;

namespace Wolfden.Index.Tests;

/// <summary>
/// How the index file is built, swapped in, checked, and rebuilt when it's
/// missing, corrupt, or out of date (design.md §7).
/// </summary>
public sealed class RebuildTests
{
    [Fact]
    public void RebuildingTwiceGivesIdenticalContent()
    {
        using var den = TempDen.Starfall();

        IndexBuilder.Rebuild(den.Root);
        var first = Dump(den.IndexPath);
        IndexBuilder.Rebuild(den.Root);
        var second = Dump(den.IndexPath);

        Assert.Equal(first, second);
        Assert.Contains("table entities", first);
        Assert.Contains("table search", first);
    }

    [Fact]
    public void RebuildWritesOnlyInsideTheStateFolder()
    {
        using var den = TempDen.Starfall();
        var before = den.Snapshot();

        IndexBuilder.Rebuild(den.Root);

        Assert.Equal(before, den.Snapshot());
        var state = Path.Combine(den.Root, IndexBuilder.StateFolder);
        Assert.Equal([".gitignore", "index.db"], Directory.EnumerateFiles(state).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("*", File.ReadAllLines(Path.Combine(state, ".gitignore")));
    }

    [Fact]
    public void AMissingIndexIsBuiltOnOpen()
    {
        using var den = TempDen.Starfall();
        Assert.Equal(IndexStatus.Missing, IndexBuilder.Check(den.Root));

        using (var index = DenIndex.Open(den.Root))
        {
            Assert.NotNull(index.FindEntity("01JA7Q8M4XK3V9R2D5W8HN6TQB"));
        }

        Assert.Equal(IndexStatus.Current, IndexBuilder.Check(den.Root));
    }

    [Fact]
    public void AGarbageIndexIsRebuilt()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);
        File.WriteAllText(den.IndexPath, "this is not a database, it's a sync conflict");

        Assert.Equal(IndexStatus.Corrupt, IndexBuilder.Check(den.Root));
        Assert.Equal(IndexStatus.Corrupt, IndexBuilder.EnsureCurrent(den.Root));
        Assert.Equal(IndexStatus.Current, IndexBuilder.Check(den.Root));
    }

    [Fact]
    public void ATruncatedIndexIsRebuilt()
    {
        using var den = TempDen.Dracula();
        IndexBuilder.Rebuild(den.Root);
        var bytes = File.ReadAllBytes(den.IndexPath);
        File.WriteAllBytes(den.IndexPath, bytes[..(bytes.Length / 2)]);

        Assert.Equal(IndexStatus.Corrupt, IndexBuilder.Check(den.Root));
        using var index = DenIndex.Open(den.Root);
        Assert.Equal(50, index.Context.Entities.Count());
    }

    [Fact]
    public void AnEmptyIndexFileIsRebuilt()
    {
        using var den = TempDen.Starfall();
        Directory.CreateDirectory(Path.GetDirectoryName(den.IndexPath)!);
        File.WriteAllBytes(den.IndexPath, []);

        // An empty file opens as an empty database, with no meta table.
        Assert.Equal(IndexStatus.Corrupt, IndexBuilder.Check(den.Root));
        using var index = DenIndex.Open(den.Root);
        Assert.NotEmpty(index.Context.Entities);
    }

    [Fact]
    public void ASchemaVersionChangeTriggersARebuild()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);
        SetMeta(den.IndexPath, IndexSchema.SchemaVersionKey, "0");

        Assert.Equal(IndexStatus.SchemaChanged, IndexBuilder.Check(den.Root));
        using var index = DenIndex.Open(den.Root);
        Assert.Equal(IndexSchema.Version.ToString(), index.Context.Meta.Single(m => m.Key == IndexSchema.SchemaVersionKey).Value);
    }

    [Fact]
    public void ABuiltInPacksChangeTriggersARebuild()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);
        Assert.Equal(BuiltInPacks.Instance.Hash, ReadMeta(den.IndexPath, IndexSchema.BuiltInPacksKey));

        // As if the index were built by a release with another core pack.
        SetMeta(den.IndexPath, IndexSchema.BuiltInPacksKey, "0000");

        Assert.Equal(IndexStatus.PacksChanged, IndexBuilder.Check(den.Root));
        Assert.Equal(IndexStatus.PacksChanged, IndexBuilder.EnsureCurrent(den.Root));
        Assert.Equal(IndexStatus.Current, IndexBuilder.Check(den.Root));
        Assert.Equal(BuiltInPacks.Instance.Hash, ReadMeta(den.IndexPath, IndexSchema.BuiltInPacksKey));
    }

    [Fact]
    public void ADenFormatChangeTriggersARebuild()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);
        SetMeta(den.IndexPath, IndexSchema.DenFormatKey, "0");

        Assert.Equal(IndexStatus.FormatChanged, IndexBuilder.Check(den.Root));
        IndexBuilder.EnsureCurrent(den.Root);
        Assert.Equal(IndexStatus.Current, IndexBuilder.Check(den.Root));

        SetMeta(den.IndexPath, IndexSchema.SupportedFormatKey, "0");
        Assert.Equal(IndexStatus.FormatChanged, IndexBuilder.Check(den.Root));
    }

    [Fact]
    public void AFailedRebuildLeavesTheOldIndexInPlace()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);
        var before = File.ReadAllBytes(den.IndexPath);
        den.Write("universes/starfall/entities/locations/new-place.md", "---\ntype: location\nname: New Place\n---\n");

        string? temp = null;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            IndexBuilder.Rebuild(den.Root, path =>
            {
                temp = path;
                Assert.True(File.Exists(path));
                throw new InvalidOperationException("crash before the swap");
            }));

        Assert.Equal("crash before the swap", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(den.IndexPath));
        Assert.False(File.Exists(temp));
        Assert.Equal(IndexStatus.Current, IndexBuilder.Check(den.Root));
    }

    [Fact]
    public void ARebuildSwapsUnderAnOpenIndex()
    {
        using var den = TempDen.Starfall();
        using var index = DenIndex.Open(den.Root);
        Assert.Equal(7, index.Context.Entities.Count());
        Assert.Empty(index.Search("lighthouse"));

        // The open index holds no file between queries, so even on Windows
        // the swap succeeds, and the next query reads the new file.
        den.Write("universes/starfall/entities/locations/beacon.md", "---\ntype: location\nname: Beacon\n---\nA lighthouse.\n");
        IndexBuilder.Rebuild(den.Root);

        Assert.Equal(8, index.Context.Entities.Count());
        Assert.Equal("Beacon", Assert.Single(index.Search("lighthouse")).EntityName);
    }

    [Fact]
    public void AnotherSqliteFileIsntTakenForAnIndex()
    {
        using var den = TempDen.Starfall();
        Directory.CreateDirectory(Path.GetDirectoryName(den.IndexPath)!);
        using (var connection = new SqliteConnection(IndexBuilder.ConnectionString(den.IndexPath, SqliteOpenMode.ReadWriteCreate)))
        {
            // A lookalike with a matching meta table, but no application_id.
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO meta VALUES ('{IndexSchema.SchemaVersionKey}', '{IndexSchema.Version}'), ('{IndexSchema.DenFormatKey}', '1'),
                    ('{IndexSchema.SupportedFormatKey}', '1');
                """;
            command.ExecuteNonQuery();
        }

        Assert.Equal(IndexStatus.Corrupt, IndexBuilder.Check(den.Root));
        using var index = DenIndex.Open(den.Root);
        Assert.NotEmpty(index.Search("reach"));
    }

    [Fact]
    public void ANullMetaValueIsCorrupt()
    {
        using var den = TempDen.Starfall();
        Directory.CreateDirectory(Path.GetDirectoryName(den.IndexPath)!);
        using (var connection = new SqliteConnection(IndexBuilder.ConnectionString(den.IndexPath, SqliteOpenMode.ReadWriteCreate)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                PRAGMA application_id = {IndexSchema.ApplicationId};
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO meta VALUES ('{IndexSchema.SchemaVersionKey}', NULL);
                """;
            command.ExecuteNonQuery();
        }

        Assert.Equal(IndexStatus.Corrupt, IndexBuilder.Check(den.Root));
    }

    [Fact]
    public void AStateFolderThatIsAFileIsReportedClearly()
    {
        using var den = TempDen.Starfall();
        den.Write(IndexBuilder.StateFolder, "not a folder");

        Assert.Equal(IndexStatus.Missing, IndexBuilder.Check(den.Root));
        var ex = Assert.Throws<IOException>(() => IndexBuilder.Rebuild(den.Root));
        Assert.Contains("is a file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIndexThatIsAFolderIsReportedClearly()
    {
        using var den = TempDen.Starfall();
        Directory.CreateDirectory(den.IndexPath);

        Assert.Equal(IndexStatus.Corrupt, IndexBuilder.Check(den.Root));
        var ex = Assert.Throws<IOException>(() => IndexBuilder.Rebuild(den.Root));
        Assert.Contains("is a folder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABusyIndexGivesAClearErrorOnWindows()
    {
        // Elsewhere, a file can be replaced while it's open.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace an open file.");
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);
        var before = File.ReadAllBytes(den.IndexPath);

        using (var reader = new SqliteConnection(IndexBuilder.ConnectionString(den.IndexPath, SqliteOpenMode.ReadOnly)))
        {
            reader.Open();
            var ex = Assert.Throws<IndexInUseException>(() => IndexBuilder.Rebuild(den.Root));
            Assert.Contains("is in use", ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal(before, File.ReadAllBytes(den.IndexPath));
    }

    [Fact]
    public void StaleTempFilesAreCleanedUp()
    {
        using var den = TempDen.Starfall();
        var folder = Path.Combine(den.Root, IndexBuilder.StateFolder);
        Directory.CreateDirectory(folder);
        var stale = Path.Combine(folder, "index.db.0123456789abcdef.tmp");
        var fresh = Path.Combine(folder, "index.db.fedcba9876543210.tmp");
        File.WriteAllText(stale, "left by a crash");
        File.WriteAllText(fresh, "another rebuild, still running");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-1));

        IndexBuilder.Rebuild(den.Root);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void ADenThatDoesntLoadIsIndexedWithItsDiagnostics()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string>
        {
            ["den.yaml"] = "format: 1\nbookkeeping_sections: 7\n",
        });

        using (var index = DenIndex.Open(den.Root))
        {
            var row = index.Context.Dens.Single();
            Assert.False(row.IsLoaded);
            Assert.Equal(1, row.Format); // format parsed, so it's kept though the den didn't load
            Assert.Contains(index.Context.Diagnostics, d => d.Path == "den.yaml" && d.Severity == "error");
        }

        // The format stamp reads den.yaml the same way each time, so an
        // unloadable den doesn't trigger a rebuild on every open.
        Assert.Equal(IndexStatus.Current, IndexBuilder.Check(den.Root));
    }

    [Fact]
    public void AFolderWithoutDenYamlStillGetsAnIndex()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string> { ["README.md"] = "Not a den.\n" });

        using var index = DenIndex.Open(den.Root);

        Assert.Equal(Path.GetFileName(den.Root), index.Context.Dens.Single().Name);
        Assert.Contains(index.Context.Diagnostics, d => d.RuleId == "F-DEN-1");
        Assert.Empty(index.Context.Files);
    }

    [Fact]
    public void AMissingDenFolderThrows()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wolfden-index-tests", Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() => IndexBuilder.Rebuild(missing));
    }

    [Fact]
    public void FilesAreRecordedWithHashesAndLoadState()
    {
        using var den = TempDen.Starfall();
        den.Write("universes/starfall/entities/locations/broken.md", "---\nname: No Type\n---\n");

        using var index = DenIndex.Open(den.Root);
        var files = index.Context.Files.ToDictionary(f => f.Path);

        Assert.Equal("den", files["den.yaml"].Kind);
        Assert.Equal("container", files["universes/starfall/universe.md"].Kind);
        Assert.Equal("outline", files["universes/starfall/series/long-dark/books/ember/outline.md"].Kind);
        var broken = files["universes/starfall/entities/locations/broken.md"];
        Assert.Equal("entity", broken.Kind);
        Assert.False(broken.IsLoaded);
        Assert.Equal(64, broken.Hash.Length);
        Assert.Contains(index.Context.Diagnostics, d => d.Path == broken.Path && d.RuleId == "F-ENT-2");
        Assert.DoesNotContain(index.Context.Entities, e => e.Path == broken.Path);
    }

    private static void SetMeta(string indexPath, string key, string value)
    {
        using var connection = new SqliteConnection(IndexBuilder.ConnectionString(indexPath, SqliteOpenMode.ReadWrite));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE meta SET value = $value WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static string? ReadMeta(string indexPath, string key)
    {
        using var connection = new SqliteConnection(IndexBuilder.ConnectionString(indexPath, SqliteOpenMode.ReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Every table's schema and rows, in a fixed order, as text.</summary>
    internal static string Dump(string indexPath)
    {
        using var connection = new SqliteConnection(IndexBuilder.ConnectionString(indexPath, SqliteOpenMode.ReadOnly));
        connection.Open();
        var tables = new List<(string Name, string Sql)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'search_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var sb = new StringBuilder();
        foreach (var (name, sql) in tables)
        {
            sb.Append("table ").AppendLine(name).AppendLine(sql);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{name}\" ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // New on every write, so a file watcher can tell who wrote last (#14).
                if (name == "meta" && reader.GetString(0) == IndexSchema.WriteIdKey)
                {
                    continue;
                }

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    sb.Append(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
