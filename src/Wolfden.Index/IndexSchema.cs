namespace Wolfden.Index;

/// <summary>Versions and raw SQL for the index's schema.</summary>
public static class IndexSchema
{
    /// <summary>
    /// The index's schema version. Bump it whenever the tables, or what goes
    /// into them, change: an index built with another version is rebuilt
    /// rather than migrated, since it holds nothing that isn't in the files.
    /// Version 2 (#84) loads types and packs: standard sections hand over
    /// (F-SEC-8), <c>ref</c> fields are type-checked (F-LINK-11), and
    /// chapter-field values that don't match their definitions are ignored
    /// (F-CHAP-3). Version 3 (#15) adds what canon views (format.md §9.4)
    /// need: field defaults (types.md F-FT-15), and the line of each field
    /// value and lead, so a view can point at its source. Version 4 (#17)
    /// records a hash of the den's files (<see cref="FilesFingerprintKey"/>).
    /// Version 5 (#14) records a <see cref="WriteIdKey"/>, and gives search
    /// rows explicit rowids, so a file watcher can update the index in place.
    /// Version 6 (#179) keeps control characters out of the search table, so
    /// a snippet's match marks can't clash with a note's text.
    /// </summary>
    public const int Version = 6;

    /// <summary>The <c>meta</c> key for <see cref="Version"/>.</summary>
    public const string SchemaVersionKey = "schema_version";

    /// <summary>
    /// The <c>meta</c> key for the hash of the built-in packs the index was
    /// built with (<c>BuiltInPacks.Hash</c>). Which sections hand over, which
    /// refs are checked, and which enum values match depend on them, so an
    /// index built with other built-in packs is rebuilt.
    /// </summary>
    public const string BuiltInPacksKey = "builtin_packs";

    /// <summary>
    /// The <c>meta</c> key for a hash of the den's files when the index was
    /// built (<see cref="IndexBuilder.CheckFiles"/>), so a one-off command
    /// can tell whether they've changed since. An index without it counts
    /// as stale there.
    /// </summary>
    public const string FilesFingerprintKey = "files_fingerprint";

    /// <summary>
    /// The <c>meta</c> key for a random id that's new each time the index is
    /// written, by a rebuild or by a file watcher's update (#14). A watcher
    /// remembers the one it wrote, and if it finds another, someone else
    /// rebuilt the index under it, so it rebuilds instead of updating rows
    /// it no longer knows.
    /// </summary>
    public const string WriteIdKey = "write_id";

    /// <summary>The <c>meta</c> key for the den's <c>format</c> as written in <c>den.yaml</c> (empty if it has none).</summary>
    public const string DenFormatKey = "den_format";

    /// <summary>The <c>meta</c> key for the highest den format the indexing code read (<c>DenStructure.SupportedFormat</c>).</summary>
    public const string SupportedFormatKey = "supported_format";

    /// <summary>
    /// The SQLite <c>application_id</c> of a Wolfden index ("WDNX" in
    /// ASCII), so a different SQLite file in its place is never mistaken
    /// for one.
    /// </summary>
    public const int ApplicationId = 0x57444E58;

    /// <summary>
    /// The FTS5 table: one row per entity (its name and aliases in
    /// <c>title</c>, its lead in <c>body</c>) and one per section (its name,
    /// and its own text). HTML comments in the lead or section (F-SEC-11)
    /// go in <c>comments</c> rather than <c>body</c>, so a hit can say it
    /// came from one. Bookkeeping and out-of-canon material is searchable
    /// too (F-SEC-12, F-STAT-4); <see cref="DenIndex.SearchMatch"/> reports
    /// each hit's status so callers can filter. <c>unicode61</c> with
    /// <c>remove_diacritics 2</c> folds case and accents, so "zoe" finds "Zoë".
    /// </summary>
    internal const string CreateSearchTable =
        "CREATE VIRTUAL TABLE search USING fts5(" +
        "title, body, comments, kind UNINDEXED, entity_id UNINDEXED, section_id UNINDEXED, " +
        "tokenize = 'unicode61 remove_diacritics 2')";

    internal const string InsertSearchRow =
        "INSERT INTO search (rowid, title, body, comments, kind, entity_id, section_id) VALUES ($rowid, $title, $body, $comments, $kind, $entity, $section)";
}
