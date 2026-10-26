using System.Text.Json;
using Wolfden.Index;

namespace Wolfden.Api;

// The shapes of the read endpoints the web UI builds on (#24 to #27), as
// camelCase JSON. Where an endpoint answers what an MCP read tool answers,
// it returns the same record (Wolfden.Index.Reading), and pages add to it.
// Every result carries a version for its shape (design.md §7): within a
// version, members may be added, but never renamed, removed, or given a new
// meaning.

/// <summary>A universe, series, or book, as a page names it.</summary>
/// <param name="Kind"><c>universe</c>, <c>series</c>, or <c>book</c>.</param>
/// <param name="Key">Its key (its folder name).</param>
/// <param name="Name">Its display name.</param>
/// <param name="Id">Its id, or null if it has none yet.</param>
/// <param name="Path">Its folder, relative to the den root.</param>
public sealed record ContainerRef(string Kind, string Key, string Name, string? Id, string Path);

/// <summary>How many entities are filed at one scope, in all and by type.</summary>
/// <param name="Total">How many in all.</param>
/// <param name="ByType">How many of each type, by type key, in key order.</param>
public sealed record EntityCounts(int Total, IReadOnlyDictionary<string, int> ByType);

/// <summary>A chapter in <see cref="HierarchyResult"/>.</summary>
/// <param name="Key">The chapter key.</param>
/// <param name="Number">The display number, or null.</param>
/// <param name="Title">The title, or null.</param>
/// <param name="Position">The chapter's story position, <c>book:chapter</c>.</param>
public sealed record ChapterNode(string Key, long? Number, string? Title, string Position);

/// <summary>A book in <see cref="HierarchyResult"/>.</summary>
/// <param name="Key">Its key.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Id">Its id, or null.</param>
/// <param name="Path">Its folder, relative to the den root.</param>
/// <param name="Status"><c>draft</c> or <c>final</c> (format.md F-BOOK-1).</param>
/// <param name="Position">The position of its start: its bare key.</param>
/// <param name="Entities">The entities filed in the book itself.</param>
/// <param name="Chapters">Its chapters, in book order.</param>
public sealed record BookNode(
    string Key, string Name, string? Id, string Path, string Status, string Position, EntityCounts Entities, IReadOnlyList<ChapterNode> Chapters);

/// <summary>A series in <see cref="HierarchyResult"/>.</summary>
/// <param name="Key">Its key.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Id">Its id, or null.</param>
/// <param name="Path">Its folder, relative to the den root.</param>
/// <param name="Entities">The entities filed in the series itself, not in its books.</param>
/// <param name="Books">Its books, in series order.</param>
public sealed record SeriesNode(string Key, string Name, string? Id, string Path, EntityCounts Entities, IReadOnlyList<BookNode> Books);

/// <summary>A universe in <see cref="HierarchyResult"/>.</summary>
/// <param name="Key">Its key.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Id">Its id, or null.</param>
/// <param name="Path">Its folder, relative to the den root.</param>
/// <param name="Entities">The entities filed in the universe itself, not in its series or books.</param>
/// <param name="Series">Its series, in story order (a series with no books yet where the universe's order puts it).</param>
/// <param name="Books">Its standalone books, in story order.</param>
public sealed record UniverseNode(
    string Key, string Name, string? Id, string Path, EntityCounts Entities, IReadOnlyList<SeriesNode> Series, IReadOnlyList<BookNode> Books);

/// <summary>The result of <c>GET /api/hierarchy</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Position">The <c>asOf</c> position the counts are taken at, or null for every entity, whatever its status.</param>
/// <param name="Universe">The universe, its series and books, and their chapters.</param>
public sealed record HierarchyResult(int Version, string? Position, UniverseNode Universe);

/// <summary>A story position in <see cref="PositionsResult"/>.</summary>
/// <param name="Position">The position, as <c>asOf</c> takes it: <c>book:chapter</c>, or a bare book key for a book with no chapters yet.</param>
/// <param name="Book">The book's key.</param>
/// <param name="BookName">The book's display name.</param>
/// <param name="Series">The key of the book's series, or null for a standalone book.</param>
/// <param name="Chapter">The chapter key, or null for a book with no chapters yet.</param>
/// <param name="Number">The chapter's display number, or null.</param>
/// <param name="Title">The chapter's title, or null.</param>
public sealed record PositionView(string Position, string Book, string BookName, string? Series, string? Chapter, long? Number, string? Title);

/// <summary>The result of <c>GET /api/positions</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Universe">The universe's key.</param>
/// <param name="Count">How many positions there are.</param>
/// <param name="Positions">Every position in story order (format.md F-ORD-1, F-ORD-2).</param>
public sealed record PositionsResult(int Version, string Universe, int Count, IReadOnlyList<PositionView> Positions);

/// <summary>An entity a link reaches, or one a backlink comes from.</summary>
/// <param name="Key">What links store and <c>/api/entity</c> takes: its id, or its path while it has none.</param>
/// <param name="Id">Its id, or null.</param>
/// <param name="Name">Its name.</param>
/// <param name="Type">Its type key.</param>
public sealed record EntityRef(string Key, string? Id, string Name, string Type);

/// <summary>A link in an entity's lead, a field, or a section, resolved as the index resolved it (format.md §10).</summary>
/// <param name="Text">The link as written, such as <c>[[mina-harker|Mina]]</c>, to find in the text.</param>
/// <param name="Target">The target as written.</param>
/// <param name="Heading">The <c>#heading</c>, or null.</param>
/// <param name="Label">The label, or null.</param>
/// <param name="Line">The 1-based line in the entity file.</param>
/// <param name="Column">The 1-based column.</param>
/// <param name="Outcome"><c>resolved</c>, <c>ambiguous</c>, or <c>broken</c>.</param>
/// <param name="Entity">For a resolved link, the entity it reaches; null when it doesn't resolve, or, as of a position, when the reader hasn't met that entity.</param>
/// <param name="Section">For a resolved <c>#heading</c> link, the section it reaches, or null.</param>
/// <param name="Matches">For an ambiguous link, the entities it matches (as of a position, those the reader has met); otherwise null.</param>
/// <param name="Problem">Why it doesn't resolve, or null.</param>
/// <param name="InView">As of a position, false when it reaches an entity the reader hasn't met by then; otherwise null.</param>
public sealed record LinkView(
    string Text, string Target, string? Heading, string? Label, int Line, int Column, string Outcome, EntityRef? Entity, string? Section,
    IReadOnlyList<EntityRef>? Matches, string? Problem, bool? InView);

/// <summary>A field value on an entity page: <see cref="FieldView"/>, with its links.</summary>
/// <param name="Name">See <see cref="FieldView"/>.</param>
/// <param name="Value">See <see cref="FieldView"/>.</param>
/// <param name="Kind">See <see cref="FieldView"/>.</param>
/// <param name="Status">See <see cref="FieldView"/>.</param>
/// <param name="Pinned">See <see cref="FieldView"/>.</param>
/// <param name="From">See <see cref="FieldView"/>.</param>
/// <param name="Until">See <see cref="FieldView"/>.</param>
/// <param name="Line">See <see cref="FieldView"/>.</param>
/// <param name="EntryIndex">See <see cref="FieldView"/>.</param>
/// <param name="Live">See <see cref="FieldView"/>.</param>
/// <param name="Links">The links in this value or entry.</param>
public sealed record PageField(
    string Name, JsonElement Value, string Kind, string? Status, bool Pinned, string? From, string? Until, int Line, int? EntryIndex, bool? Live,
    IReadOnlyList<LinkView> Links);

/// <summary>A section on an entity page: <see cref="SectionView"/>, whose <c>body</c> is markdown, with its links.</summary>
/// <param name="Name">See <see cref="SectionView"/>.</param>
/// <param name="Level">See <see cref="SectionView"/>.</param>
/// <param name="HeadingPath">See <see cref="SectionView"/>.</param>
/// <param name="Status">See <see cref="SectionView"/>.</param>
/// <param name="Pinned">See <see cref="SectionView"/>.</param>
/// <param name="From">See <see cref="SectionView"/>.</param>
/// <param name="Until">See <see cref="SectionView"/>.</param>
/// <param name="Line">See <see cref="SectionView"/>.</param>
/// <param name="Body">The section's own markdown, up to the next heading.</param>
/// <param name="Bookkeeping">See <see cref="SectionView"/>.</param>
/// <param name="Links">The links in the section's own text, in file order.</param>
public sealed record PageSection(
    string Name, int Level, IReadOnlyList<string> HeadingPath, string Status, bool Pinned, string? From, string? Until, int Line, string Body,
    bool? Bookkeeping, IReadOnlyList<LinkView> Links);

/// <summary>
/// Where an entity enters canon (format.md F-ENT-6): its own <c>from</c>, or,
/// without one, the start of the scope it's filed in, which it inherits.
/// </summary>
/// <param name="Position">The position, as <c>asOf</c> takes it, or null when no position names the point: the start of the story, for an entity filed in the universe, or the place in story order of a series with no books yet (format.md F-ORD-3).</param>
/// <param name="Inherited">True if the entity has no <c>from</c>, so it enters where its scope starts.</param>
/// <param name="Description">The entry point in words, such as "the start of book 'ember'".</param>
public sealed record EntryView(string? Position, bool Inherited, string Description);

/// <summary>A link to an entity from elsewhere in its universe.</summary>
/// <param name="Source">The entity the link is in, or null for a link in a universe, series, or book file or an outline.</param>
/// <param name="Container">For a link in a universe, series, or book file or an outline, that container; otherwise null.</param>
/// <param name="Site">Where it's written: <c>entity_lead</c>, <c>entity_section</c>, <c>entity_field</c>, <c>entity_field_entry</c>, <c>container_body</c>, <c>chapter_field</c>, <c>chapter_default</c>, <c>outline_row</c>, or <c>outline_notes</c>.</param>
/// <param name="Path">The file, relative to the den root.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The 1-based column.</param>
/// <param name="Text">The link as written.</param>
/// <param name="Field">The field or chapter field it's in, or null.</param>
/// <param name="Section">The name of the section it's in, or null.</param>
/// <param name="Chapter">For a link in a chapter's fields, the chapter's position (<c>book:chapter</c>); otherwise null.</param>
/// <param name="OutlineRow">For a link in an outline row, the row's id; otherwise null.</param>
/// <param name="Status">The effective status of what holds it, or null where there's none.</param>
/// <param name="Bookkeeping">True for a link in a bookkeeping section.</param>
public sealed record BacklinkView(
    EntityRef? Source, ContainerRef? Container, string Site, string Path, int Line, int Column, string Text, string? Field, string? Section,
    string? Chapter, string? OutlineRow, string? Status, bool Bookkeeping);

/// <summary>
/// An entity's page: what <c>get_entity</c> gives (<see cref="EntityView"/>),
/// with links resolved, the scope's container chain, where it enters canon,
/// and its backlinks.
/// </summary>
/// <param name="Key">See <see cref="EntitySummary"/>.</param>
/// <param name="Id">See <see cref="EntitySummary"/>.</param>
/// <param name="Name">See <see cref="EntitySummary"/>.</param>
/// <param name="Type">See <see cref="EntitySummary"/>.</param>
/// <param name="Status">See <see cref="EntitySummary"/>.</param>
/// <param name="Universe">See <see cref="EntitySummary"/>.</param>
/// <param name="ScopeKind">See <see cref="EntitySummary"/>.</param>
/// <param name="ScopeKey">See <see cref="EntitySummary"/>.</param>
/// <param name="Path">See <see cref="EntitySummary"/>.</param>
/// <param name="From">See <see cref="EntitySummary"/>.</param>
/// <param name="Aliases">See <see cref="EntityView"/>: in the full view only.</param>
/// <param name="Lead">See <see cref="EntityView"/>: markdown.</param>
/// <param name="Pinned">See <see cref="EntityView"/>.</param>
/// <param name="Fields">See <see cref="EntityView"/>, each with its links.</param>
/// <param name="Sections">See <see cref="EntityView"/>, each with its links.</param>
/// <param name="Hash">See <see cref="EntityView"/>.</param>
/// <param name="Scope">The container chain of its scope, from the universe down to where it's filed.</param>
/// <param name="Entry">Where it enters canon.</param>
/// <param name="LeadLinks">The links in its lead.</param>
/// <param name="Backlinks">The links that reach it, by path and place in the file; as of a position, only those in what the reader has met.</param>
public sealed record EntityPage(
    string Key, string? Id, string Name, string Type, string Status, string Universe, string ScopeKind, string ScopeKey, string Path, string? From,
    IReadOnlyList<string>? Aliases, string? Lead, PinnedFacts? Pinned, IReadOnlyList<PageField> Fields, IReadOnlyList<PageSection> Sections,
    string? Hash, IReadOnlyList<ContainerRef> Scope, EntryView Entry, IReadOnlyList<LinkView> LeadLinks, IReadOnlyList<BacklinkView> Backlinks);

/// <summary>The result of <c>GET /api/entity</c>: <see cref="GetEntityResult"/>, with the page.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="View">See <see cref="GetEntityResult"/>.</param>
/// <param name="Position">See <see cref="GetEntityResult"/>.</param>
/// <param name="InView">See <see cref="GetEntityResult"/>.</param>
/// <param name="Note">See <see cref="GetEntityResult"/>.</param>
/// <param name="Entity">The page, or null when the entity isn't in the view.</param>
public sealed record EntityPageResult(int Version, string View, string? Position, bool InView, string? Note, EntityPage? Entity);

/// <summary>What changed in an entity's file, from <c>GET /api/history/diff</c>.</summary>
/// <param name="From">The checkpoint compared from, or null for a checkpoint's own diff when it's the first, compared with nothing.</param>
/// <param name="To">The checkpoint compared to, or null for the file as it is now.</param>
/// <param name="Files">The file's change, with its unified diff, or empty when nothing changed.</param>
public sealed record DiffView(string? From, string? To, IReadOnlyList<HistoryFileDiffView> Files);

/// <summary>The result of <c>GET /api/history/diff</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Entity">The entity whose file this is.</param>
/// <param name="Backend">The history backend: <c>git</c>, or <c>none</c> for a den without history.</param>
/// <param name="Available">False if there's nothing to compare: the den has no history, or nothing has been checkpointed yet. <paramref name="Reason"/> says why.</param>
/// <param name="Reason">Why the diff isn't available, in words for the writer, or null.</param>
/// <param name="Diff">The diff, or null when it isn't available.</param>
public sealed record HistoryDiffResult(int Version, EntitySummary Entity, string Backend, bool Available, string? Reason, DiffView? Diff);

/// <summary>
/// A search hit as the search page gets it: the MCP tool's hit, plus the
/// excerpt without marks and where its matches are (#179). A match range
/// can't be mistaken for a bracket in the note's own text.
/// </summary>
/// <param name="Entity">The entity.</param>
/// <param name="Section">The section the match is in, or null for the name, aliases, lead, or a field.</param>
/// <param name="Line">The section heading's line, or the field value's; null for the name, aliases, or lead.</param>
/// <param name="Snippet">The excerpt with each match in square brackets, as the MCP tool gives it.</param>
/// <param name="Status">The effective status of the matching section or field value, or of the entity.</param>
/// <param name="InCanon">True if the match is canon text (draft or canon, not bookkeeping).</param>
/// <param name="InComment">True if the match is in an HTML comment, such as a to-do note.</param>
/// <param name="Field">The field the match is in, or null.</param>
/// <param name="Text">The excerpt without marks.</param>
/// <param name="Matches">Where the matches are in <paramref name="Text"/>, in order, as offsets and lengths in UTF-16 code units (JavaScript string indexes).</param>
public sealed record SearchPageHit(
    EntitySummary Entity, string? Section, int? Line, string Snippet, string Status, bool InCanon, bool InComment, string? Field, string Text,
    IReadOnlyList<TextMatch> Matches);

/// <summary>A search's result as the search page gets it: the MCP tool's, with <see cref="SearchPageHit"/>s.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Query">The query.</param>
/// <param name="Count">How many hits this result holds.</param>
/// <param name="Truncated">True if more hits matched than <c>limit</c>.</param>
/// <param name="Hits">The hits, best first.</param>
public sealed record SearchPageResult(int Version, string Query, int Count, bool Truncated, IReadOnlyList<SearchPageHit> Hits);
