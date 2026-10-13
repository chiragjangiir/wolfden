namespace Wolfden.Index;

/// <summary>Versions and raw SQL for the index's schema.</summary>
public static class IndexSchema
{
    /// <summary>
    /// The index's schema version. Bump it whenever the tables, or what goes
    /// into them, change: an index built with another version is rebuilt
    /// rather than migrated, since it holds nothing that isn't in the files.
    /// </summary>
    public const int Version = 1;

    /// <summary>The <c>meta</c> key for <see cref="Version"/>.</summary>
    public const string SchemaVersionKey = "schema_version";

    /// <summary>The <c>meta</c> key for the den's <c>format</c> as written in <c>den.yaml</c> (empty if it has none).</summary>
    public const string DenFormatKey = "den_format";

    /// <summary>The <c>meta</c> key for the highest den format the indexing code read (<c>DenStructure.SupportedFormat</c>).</summary>
    public const string SupportedFormatKey = "supported_format";

    /// <summary>
    /// The FTS5 table: one row per entity (its name and aliases, and its
    /// lead) and one per section (its name, and its own text). Bookkeeping
    /// sections are searchable too (F-SEC-12). <c>unicode61</c> with
    /// <c>remove_diacritics 2</c> folds case and accents, so "zoe" finds "Zoë".
    /// </summary>
    internal const string CreateSearchTable =
        "CREATE VIRTUAL TABLE search USING fts5(" +
        "title, body, kind UNINDEXED, entity_id UNINDEXED, section_id UNINDEXED, " +
        "tokenize = 'unicode61 remove_diacritics 2')";

    internal const string InsertSearchRow =
        "INSERT INTO search (title, body, kind, entity_id, section_id) VALUES ($title, $body, $kind, $entity, $section)";
}
