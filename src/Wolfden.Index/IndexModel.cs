namespace Wolfden.Index;

// The index's tables (design.md §7). Every row is derived from the den's
// files and can be rebuilt from them at any time: nothing here exists only in
// the database. Rows hang off the file they came from, directly or through an
// entity or container, and cascade from it, so one file's rows can be
// replaced on their own (#14).
//
// Row ids are handles within one index file, not data. A full rebuild gives
// them out in a fixed order, but a file watcher's update keeps the ids of
// rows that didn't change and gives new rows new ids (IndexIds), so ids of
// rows that come from single files don't follow path order after updates.
// Queries order by columns, never by id. Containers, chapters, field
// defaults, and diagnostics always have a full rebuild's ids.
//
// Story positions are stored as ordinals (StoryPoint.Ordinal): one integer
// per point that sorts in story order within a universe, with the start and
// end of the story as long.MinValue and long.MaxValue. A fact holds at point
// p when from_ordinal <= p < until_ordinal, except at the end of the story
// itself (an omitted position, F-VIEW-1), where that comparison matches
// nothing: use IndexQueries.InEffectAt, which handles it. A null ordinal
// means the position doesn't resolve (format.md F-POS-4), and the fact is
// left out of canon views. Ordinals are only comparable within one
// universe_id.
//
// Statuses, kinds, and outcomes are stored as the lowercase words format.md
// uses ("canon", "temporal", "broken"), so the database reads well in SQL.

/// <summary>A key and value about the index itself, such as its schema version.</summary>
public sealed class MetaRecord
{
    /// <summary>The key, such as <c>schema_version</c>.</summary>
    public string Key { get; set; } = "";

    /// <summary>The value, as text.</summary>
    public string Value { get; set; } = "";
}

/// <summary>The den: one row, from <c>den.yaml</c> (format.md §5.2).</summary>
public sealed class DenRecord
{
    /// <summary>Always 1.</summary>
    public int Id { get; set; }

    /// <summary>The display name (F-DEN-2).</summary>
    public string Name { get; set; } = "";

    /// <summary>The den's <c>format</c>, or null if <c>den.yaml</c> didn't load.</summary>
    public int? Format { get; set; }

    /// <summary>True if <c>den.yaml</c> loaded (F-DEN-1).</summary>
    public bool IsLoaded { get; set; }

    /// <summary><c>spoiler_checks</c> (F-DEN-7).</summary>
    public bool SpoilerChecks { get; set; }

    /// <summary>The den's own <c>bookkeeping_sections</c> (F-DEN-6), as a JSON array.</summary>
    public string BookkeepingSectionsJson { get; set; } = "[]";
}

/// <summary>A file the index was built from, with a hash of its bytes.</summary>
public sealed class FileRecord
{
    /// <summary>Row id.</summary>
    public int Id { get; set; }

    /// <summary>The path relative to the den root, with <c>/</c>.</summary>
    public string Path { get; set; } = "";

    /// <summary><c>den</c>, <c>pack</c> (a den-local pack file, types.md §2), <c>container</c>, <c>entity</c>, <c>outline</c>, or <c>other</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The SHA-256 of the file's bytes, in lowercase hex.</summary>
    public string Hash { get; set; } = "";

    /// <summary>
    /// True if the file loaded. False if errors left it out (F-GEN-2): its
    /// diagnostics are indexed, but nothing else from it is.
    /// </summary>
    public bool IsLoaded { get; set; }
}

/// <summary>A universe, series, or book (format.md §6), with its place in story order.</summary>
public sealed class ContainerRecord
{
    /// <summary>Row id.</summary>
    public int Id { get; set; }

    /// <summary>The container file.</summary>
    public int FileId { get; set; }

    /// <summary><c>universe</c>, <c>series</c>, or <c>book</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The universe's row id; a universe's own id for a universe.</summary>
    public int UniverseId { get; set; }

    /// <summary>The series for a book in a series, the universe for a series or a standalone book, or null for a universe.</summary>
    public int? ParentId { get; set; }

    /// <summary>The key: the folder name (F-KEY-3).</summary>
    public string Key { get; set; } = "";

    /// <summary>The display name (F-CTR-1).</summary>
    public string Name { get; set; } = "";

    /// <summary>The container's id, or null if it has none yet.</summary>
    public string? Ulid { get; set; }

    /// <summary>The container folder, relative to the den root.</summary>
    public string Path { get; set; } = "";

    /// <summary>For a book, its 0-based place among the universe's books in story order (F-ORD-1); for a series, its first book's, or null.</summary>
    public int? StoryOrder { get; set; }

    /// <summary>The ordinal of the container's start (F-ORD-3).</summary>
    public long StartOrdinal { get; set; }

    /// <summary>For a book, <c>draft</c> or <c>final</c> (F-BOOK-1); otherwise null.</summary>
    public string? BookStatus { get; set; }
}

/// <summary>A chapter of a book (F-BOOK-3).</summary>
public sealed class ChapterRecord
{
    /// <summary>Row id.</summary>
    public int Id { get; set; }

    /// <summary>The book's container row.</summary>
    public int BookId { get; set; }

    /// <summary>The universe's container row.</summary>
    public int UniverseId { get; set; }

    /// <summary>The chapter key, which positions name.</summary>
    public string Key { get; set; } = "";

    /// <summary>The 0-based place in the book's <c>chapters</c> list.</summary>
    public int Index { get; set; }

    /// <summary>The display number, or null.</summary>
    public long? Number { get; set; }

    /// <summary>The title, or null.</summary>
    public string? Title { get; set; }

    /// <summary>The 1-based line in <c>book.md</c>.</summary>
    public int Line { get; set; }

    /// <summary>The chapter's ordinal in story order.</summary>
    public long Ordinal { get; set; }
}

/// <summary>An entity (format.md §7) that loaded without errors.</summary>
public sealed class EntityRecord
{
    /// <summary>Row id.</summary>
    public int Id { get; set; }

    /// <summary>The entity file.</summary>
    public int FileId { get; set; }

    /// <summary>The universe's container row.</summary>
    public int UniverseId { get; set; }

    /// <summary>The scope's container row (F-LAY-5).</summary>
    public int ScopeId { get; set; }

    /// <summary>The <c>id</c> from frontmatter, or null if it has none yet (F-ID-2).</summary>
    public string? Ulid { get; set; }

    /// <summary>What a link to it stores: its id, or its path while it has none (F-LINK-10).</summary>
    public string Key { get; set; } = "";

    /// <summary>The file's path, relative to the den root.</summary>
    public string Path { get; set; } = "";

    /// <summary>The slug, or null if the file name isn't a valid key (F-KEY-4).</summary>
    public string? Slug { get; set; }

    /// <summary>The name as written.</summary>
    public string Name { get; set; } = "";

    /// <summary>The name after F-LINK-4 normalization.</summary>
    public string NameNormalized { get; set; } = "";

    /// <summary>The type key as written.</summary>
    public string Type { get; set; } = "";

    /// <summary>The entity's status (F-ENT-5).</summary>
    public string Status { get; set; } = "";

    /// <summary>The <c>from</c> as written, or null.</summary>
    public string? FromText { get; set; }

    /// <summary>Where it enters canon (F-ENT-6), or null if its <c>from</c> doesn't resolve.</summary>
    public long? FromOrdinal { get; set; }

    /// <summary>True if another file has the same id (F-ID-3), so links don't resolve to it.</summary>
    public bool HasDuplicateId { get; set; }

    /// <summary>The first level-1 heading in the lead, or null.</summary>
    public string? Title { get; set; }

    /// <summary>The lead: the body before the first section (F-SEC-2).</summary>
    public string Lead { get; set; } = "";

    /// <summary>The 1-based line where the lead starts: the line after the frontmatter.</summary>
    public int LeadLine { get; set; }
}

/// <summary>One of an entity's aliases (F-ENT-4).</summary>
public sealed class AliasRecord
{
    /// <summary>Row id.</summary>
    public int Id { get; set; }

    /// <summary>The entity.</summary>
    public int EntityId { get; set; }

    /// <summary>The 0-based place in <c>aliases</c>.</summary>
    public int Order { get; set; }

    /// <summary>The alias as written.</summary>
    public string Alias { get; set; } = "";

    /// <summary>The alias after F-LINK-4 normalization.</summary>
    public string AliasNormalized { get; set; } = "";
}

/// <summary>
/// A field's value (format.md §7.2, §7.3): one row for a plain value, or
/// one per entry. A plain value holds from the start to the end of the story
/// with its entity's status.
/// </summary>
public sealed class FieldValueRecord
{
    /// <summary>Row id.</summary>
    public int Id { get; set; }

    /// <summary>The entity.</summary>
    public int EntityId { get; set; }

    /// <summary>The universe's container row.</summary>
    public int UniverseId { get; set; }

    /// <summary>The field name.</summary>
    public string Field { get; set; } = "";

    /// <summary>The field's 0-based place in <c>fields</c>.</summary>
    public int FieldOrder { get; set; }

    /// <summary><c>plain</c>, <c>entry</c>, or <c>temporal</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The entry's 0-based place in its field, or null for a plain value.</summary>
    public int? EntryIndex { get; set; }

    /// <summary>The 1-based line in the entity file: the entry's, or for a plain value the field name's.</summary>
    public int Line { get; set; }

    /// <summary>The value as JSON: strings stay strings (YAML scalars are kept as written), lists are arrays, and mappings are objects.</summary>
    public string ValueJson { get; set; } = "null";

    /// <summary>The entry's <c>from</c> as written, or null.</summary>
    public string? FromText { get; set; }

    /// <summary>The entry's <c>until</c> as written, or null.</summary>
    public string? UntilText { get; set; }

    /// <summary>The status written on the entry, or its entity's (F-STAT-2).</summary>
    public string OwnStatus { get; set; } = "";

    /// <summary>The effective status (F-STAT-3).</summary>
    public string Status { get; set; } = "";

    /// <summary>True for <c>pin: true</c> (F-PIN-1).</summary>
    public bool Pinned { get; set; }

    /// <summary>True if the entry is live (F-TMP-4). Always true for a plain value of an in-canon entity.</summary>
    public bool IsLive { get; set; }

    /// <summary>Where it starts, or null if a position doesn't resolve.</summary>
    public long? FromOrdinal { get; set; }

    /// <summary>
    /// Where it ends (exclusive), after the F-TMP-4 handover, or null if a
    /// position doesn't resolve. <see cref="IndexQueries.EndOfStory"/> if it
    /// has no end; query with <see cref="IndexQueries.InEffectAt(IQueryable{FieldValueRecord}, long)"/>.
    /// </summary>
    public long? UntilOrdinal { get; set; }
}

/// <summary>
/// A field <c>default</c> of an enabled type (types.md F-FT-15): the value
/// views show where an entity of that type has no value for the field. Only
/// fields with a default have a row. Types come from the built-in packs and
/// the den's enabled packs (types.md F-PACK-7), so these rows hang off no one
/// file: a change to any pack file rebuilds them all.
/// </summary>
public sealed class FieldDefaultRecord
{
    /// <summary>Row id.</summary>
    public int Id { get; set; }

    /// <summary>The type key, as entities write it in <c>type</c>.</summary>
    public string TypeKey { get; set; } = "";

    /// <summary>The field name.</summary>
    public string Field { get; set; } = "";

    /// <summary>The field's 0-based place in the type's field order (types.md F-FT-3).</summary>
    public int FieldOrder { get; set; }

    /// <summary>The default as JSON, in the same form as <see cref="FieldValueRecord.ValueJson"/>.</summary>
    public string DefaultJson { get; set; } = "null";

    /// <summary>The pack file that defines the field, as diagnostics name it.</summary>
    public string Path { get; set; } = "";

    /// <summary>The 1-based line of the field's name in that file.</summary>
    public int Line { get; set; }
}

/// <summary>A section of an entity (format.md §9), with its effective range.</summary>
public sealed class SectionRecord
{
    /// <summary>Row id.</summary>
    public int Id { get; set; }

    /// <summary>The entity.</summary>
    public int EntityId { get; set; }

    /// <summary>The universe's container row.</summary>
    public int UniverseId { get; set; }

    /// <summary>The section that holds this one, or null for a level-2 section.</summary>
    public int? ParentId { get; set; }

    /// <summary>The 0-based place among the file's sections, in file order.</summary>
    public int Index { get; set; }

    /// <summary>2 for an entity section, 3 to 6 for a subsection.</summary>
    public int Level { get; set; }

    /// <summary>The section name (F-SEC-3).</summary>
    public string Name { get; set; } = "";

    /// <summary>The name after F-LINK-4 normalization.</summary>
    public string NameNormalized { get; set; } = "";

    /// <summary>The names from the level-2 section down to this one, as a JSON array.</summary>
    public string HeadingPathJson { get; set; } = "[]";

    /// <summary>The heading's 1-based line.</summary>
    public int Line { get; set; }

    /// <summary>The section's own text: after its heading and comment, up to the next heading.</summary>
    public string Body { get; set; } = "";

    /// <summary>The section comment's attributes as a JSON object, or null if it has no comment.</summary>
    public string? AttributesJson { get; set; }

    /// <summary>The status written in the comment, or what it takes from its holder (F-STAT-2).</summary>
    public string OwnStatus { get; set; } = "";

    /// <summary>The effective status (F-STAT-3).</summary>
    public string Status { get; set; } = "";

    /// <summary>True if it or a section holding it has <c>pin=true</c> (F-PIN-1, F-PIN-2).</summary>
    public bool Pinned { get; set; }

    /// <summary>True for a bookkeeping section (F-SEC-12), which canon views leave out.</summary>
    public bool IsBookkeeping { get; set; }

    /// <summary>The comment's <c>from</c> as written, or null.</summary>
    public string? FromText { get; set; }

    /// <summary>The comment's <c>until</c> as written, or null.</summary>
    public string? UntilText { get; set; }

    /// <summary>The effective <c>from</c> (F-SEC-8, F-SEC-9), or null if a position doesn't resolve.</summary>
    public long? FromOrdinal { get; set; }

    /// <summary>
    /// The effective <c>until</c> (exclusive), or null if a position doesn't
    /// resolve. <see cref="IndexQueries.EndOfStory"/> if it has no end; query
    /// with <see cref="IndexQueries.InEffectAt(IQueryable{SectionRecord}, long)"/>.
    /// A standard section of the entity's type hands over to the next live
    /// one with the same name (F-SEC-8): see <see cref="Wolfden.Core.Entities.SectionFact.EffectiveUntil"/>.
    /// </summary>
    public long? UntilOrdinal { get; set; }
}

/// <summary>A link (format.md §10) and what it resolved to.</summary>
public sealed class LinkRecord
{
    /// <summary>Row id.</summary>
    public int Id { get; set; }

    /// <summary>The file the link is in.</summary>
    public int FileId { get; set; }

    /// <summary>The universe's container row.</summary>
    public int UniverseId { get; set; }

    /// <summary>The file's path, relative to the den root.</summary>
    public string Path { get; set; } = "";

    /// <summary>1-based line.</summary>
    public int Line { get; set; }

    /// <summary>1-based column.</summary>
    public int Column { get; set; }

    /// <summary>The link as written, such as <c>[[mara-voss|Voss]]</c>.</summary>
    public string Text { get; set; } = "";

    /// <summary>The target, trimmed.</summary>
    public string Target { get; set; } = "";

    /// <summary>The <c>#heading</c>, or null.</summary>
    public string? Heading { get; set; }

    /// <summary>The label, or null.</summary>
    public string? Label { get; set; }

    /// <summary>Where it's written, such as <c>entity_section</c> or <c>outline_row</c>.</summary>
    public string Site { get; set; } = "";

    /// <summary>The scope it resolves from (F-LINK-7): a container row.</summary>
    public int ScopeId { get; set; }

    /// <summary>The entity it's in, or null.</summary>
    public int? SourceEntityId { get; set; }

    /// <summary>The section it's in, or null.</summary>
    public int? SourceSectionId { get; set; }

    /// <summary>The field or chapter field it's in, or null.</summary>
    public string? Field { get; set; }

    /// <summary>The chapter, for a link in a chapter's fields, or null.</summary>
    public int? ChapterId { get; set; }

    /// <summary>The outline row id, for a link in an outline row, or null.</summary>
    public string? OutlineRow { get; set; }

    /// <summary>The effective status of what holds it, or null where there's none (F-LINK-12).</summary>
    public string? Status { get; set; }

    /// <summary>True for a link in a bookkeeping section.</summary>
    public bool IsBookkeeping { get; set; }

    /// <summary><c>resolved</c>, <c>ambiguous</c>, or <c>broken</c>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>The tier that matched (<c>id</c>, <c>slug</c>, <c>name</c>, <c>alias</c>), or null.</summary>
    public string? MatchedBy { get; set; }

    /// <summary>The target entity's key (its id, or its path while it has none, F-LINK-10), or null if it didn't resolve.</summary>
    public string? TargetKey { get; set; }

    /// <summary>For a <c>#heading</c> link, the name of the section it matched (F-LINK-9), or null.</summary>
    public string? TargetSection { get; set; }

    /// <summary>For an ambiguous link, the keys of the matches as a JSON array; otherwise null.</summary>
    public string? MatchesJson { get; set; }

    /// <summary>Why it didn't resolve, or null.</summary>
    public string? Problem { get; set; }

    /// <summary>
    /// The link's position for F-LINK-13, or null if it has none. A link in
    /// <c>chapter_defaults</c> has one per inheriting chapter, and this is
    /// the earliest.
    /// </summary>
    public long? PositionOrdinal { get; set; }
}

/// <summary>A row of an outline table (format.md §11).</summary>
public sealed class OutlineRowRecord
{
    /// <summary>Row id.</summary>
    public int Id { get; set; }

    /// <summary>The <c>outline.md</c> file.</summary>
    public int FileId { get; set; }

    /// <summary>The book's container row.</summary>
    public int BookId { get; set; }

    /// <summary>The universe's container row.</summary>
    public int UniverseId { get; set; }

    /// <summary>The row id, such as <c>69a</c>.</summary>
    public string RowId { get; set; } = "";

    /// <summary>The row id's number, or null if it's too long to hold.</summary>
    public long? Number { get; set; }

    /// <summary>The row id's letter suffix, or an empty string.</summary>
    public string Suffix { get; set; } = "";

    /// <summary>The 0-based place among the outline's rows, in file order.</summary>
    public int Order { get; set; }

    /// <summary>The 1-based line.</summary>
    public int Line { get; set; }

    /// <summary>The heading of the outline section it's in, or null.</summary>
    public string? Heading { get; set; }

    /// <summary>The <c>beat</c> cell.</summary>
    public string Beat { get; set; } = "";

    /// <summary>The <c>chapter</c> cell, or null.</summary>
    public string? Chapter { get; set; }

    /// <summary>The chapter's ordinal, or null if the cell is empty or names no chapter of the book.</summary>
    public long? ChapterOrdinal { get; set; }

    /// <summary>The <c>pov</c> cell, or null.</summary>
    public string? Pov { get; set; }

    /// <summary>The row's status (F-OUT-9).</summary>
    public string Status { get; set; } = "";

    /// <summary>The <c>notes</c> cell, or null.</summary>
    public string? Notes { get; set; }

    /// <summary>Every non-empty cell by normalized header, as a JSON object.</summary>
    public string CellsJson { get; set; } = "{}";
}

/// <summary>
/// A diagnostic from loading the den (format.md §1.3). It's keyed by
/// <see cref="Path"/>, not by a file row, since some diagnostics are about
/// folders or files that were never read (F-LAY-4, F-DEN-1).
/// </summary>
public sealed class DiagnosticRecord
{
    /// <summary>Row id, in path and line order.</summary>
    public int Id { get; set; }

    /// <summary>The file or folder, relative to the den root.</summary>
    public string Path { get; set; } = "";

    /// <summary>The rule, such as <c>F-LINK-8</c>.</summary>
    public string RuleId { get; set; } = "";

    /// <summary><c>error</c> or <c>warning</c>.</summary>
    public string Severity { get; set; } = "";

    /// <summary>The 1-based line, or null.</summary>
    public int? Line { get; set; }

    /// <summary>The message.</summary>
    public string Message { get; set; } = "";
}
