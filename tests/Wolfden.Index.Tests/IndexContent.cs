using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Wolfden.Index.Tests;

/// <summary>
/// An index's content without its row ids: every row of every table, with
/// each reference to another row written as that row's natural key (a
/// file's path, a section's entity path and index), sorted. A file watcher's
/// incremental update (#14) gives rows that came from single files other
/// ids than a full rebuild does, but must give exactly this content.
/// </summary>
internal static class IndexContent
{
    /// <summary>Which columns of which tables refer to which rows.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> References = new()
    {
        ["containers"] = new() { ["file_id"] = "files", ["universe_id"] = "containers", ["parent_id"] = "containers" },
        ["chapters"] = new() { ["book_id"] = "containers", ["universe_id"] = "containers" },
        ["entities"] = new() { ["file_id"] = "files", ["universe_id"] = "containers", ["scope_id"] = "containers" },
        ["aliases"] = new() { ["entity_id"] = "entities" },
        ["field_values"] = new() { ["entity_id"] = "entities", ["universe_id"] = "containers" },
        ["sections"] = new() { ["entity_id"] = "entities", ["universe_id"] = "containers", ["parent_id"] = "sections" },
        ["links"] = new()
        {
            ["file_id"] = "files", ["universe_id"] = "containers", ["scope_id"] = "containers", ["source_entity_id"] = "entities",
            ["source_section_id"] = "sections", ["chapter_id"] = "chapters",
        },
        ["outline_rows"] = new() { ["file_id"] = "files", ["book_id"] = "containers", ["universe_id"] = "containers" },
        ["search"] = new() { ["entity_id"] = "entities", ["section_id"] = "sections" },
    };

    /// <summary>The content of the index at <paramref name="indexPath"/>, one line per row, grouped by table.</summary>
    public static string Of(string indexPath)
    {
        using var connection = new SqliteConnection(IndexBuilder.ConnectionString(indexPath, SqliteOpenMode.ReadOnly));
        connection.Open();
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'search_%' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var rows = tables.ToDictionary(t => t, t => Read(connection, t));
        var keys = new Dictionary<string, Dictionary<long, string>>
        {
            ["files"] = rows["files"].ToDictionary(r => Id(r), r => Text(r["path"])),
            ["containers"] = rows["containers"].ToDictionary(r => Id(r), r => Text(r["path"])),
            ["entities"] = rows["entities"].ToDictionary(r => Id(r), r => Text(r["path"])),
        };
        keys["chapters"] = rows["chapters"].ToDictionary(r => Id(r), r => $"{keys["containers"][(long)r["book_id"]!]}:{Text(r["key"])}");
        keys["sections"] = rows["sections"].ToDictionary(r => Id(r), r => $"{keys["entities"][(long)r["entity_id"]!]}#{Text(r["index"])}");

        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            var references = References.GetValueOrDefault(table) ?? [];
            var lines = new List<string>();
            foreach (var row in rows[table])
            {
                if (table == "meta" && Text(row["key"]) == IndexSchema.WriteIdKey)
                {
                    continue;
                }

                var cells = new List<string>();
                foreach (var (column, value) in row)
                {
                    // Ids of per-file rows differ after updates; diagnostics keep theirs.
                    if (column is "id" or "rowid" && table != "diagnostics")
                    {
                        continue;
                    }

                    cells.Add(references.TryGetValue(column, out var target) && value is long id
                        ? $"{column}=>{keys[target][id]}"
                        : $"{column}={Text(value)}");
                }

                lines.Add(string.Join(" | ", cells));
            }

            lines.Sort(StringComparer.Ordinal);
            sb.Append("table ").Append(table).Append(" (").Append(lines.Count).AppendLine(" rows)");
            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    private static List<Dictionary<string, object?>> Read(SqliteConnection connection, string table)
    {
        var result = new List<Dictionary<string, object?>>();
        using var command = connection.CreateCommand();
        command.CommandText = table == "search" ? "SELECT rowid, * FROM search" : $"SELECT * FROM \"{table}\"";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            result.Add(row);
        }

        return result;
    }

    private static long Id(Dictionary<string, object?> row) => (long)row["id"]!;

    private static string Text(object? value) => value is null ? "NULL" : Convert.ToString(value, CultureInfo.InvariantCulture)!;
}
