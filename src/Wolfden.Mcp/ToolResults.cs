using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wolfden.Mcp;

// The JSON shapes the MCP tools return (design.md §8). Names are snake_case,
// like the tools' arguments (as_of), and null members are left out, so a
// model reads only what applies. Every result carries a version for its
// shape: within a version, members may be added, but never renamed, removed,
// or given a new meaning.
//
// In every result, `count` is how many items this result holds (hits,
// entities, or diagnostics), and `truncated` is true when more matched than
// `limit` let through. Where the tool knows how many matched in all
// (list_entities, check), `total` says so.

/// <summary>The JSON settings for tool results.</summary>
public static class ToolJson
{
    /// <summary>The shape version every result carries.</summary>
    public const int Version = 1;

    /// <summary>Compact snake_case JSON, with nulls left out and text kept readable.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keep names and quotes readable ("Székely", 'x'). The JSON never goes into HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>A failed tool call: a stable code and a message that says what to do next.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Error">The error.</param>
public sealed record ToolErrorResult(int Version, ToolError Error);

/// <summary>An error.</summary>
/// <param name="Code">A stable code, one of <see cref="ToolErrorCodes"/>.</param>
/// <param name="Message">What went wrong, for the model and the writer.</param>
/// <param name="Candidates">For <c>ambiguous_entity</c>, the entities the name matches.</param>
public sealed record ToolError(string Code, string Message, IReadOnlyList<EntitySummary>? Candidates = null);

/// <summary>The codes a tool error can have.</summary>
public static class ToolErrorCodes
{
    /// <summary><c>den.yaml</c> doesn't load, so nothing in the den can be read. The <c>check</c> tool says why.</summary>
    public const string DenNotLoaded = "den_not_loaded";

    /// <summary>The den's index couldn't be read or rebuilt, or the den's files couldn't be read.</summary>
    public const string DenUnreadable = "den_unreadable";

    /// <summary>The den has no universes yet.</summary>
    public const string NoUniverse = "no_universe";

    /// <summary>The den has several universes and the call didn't name one.</summary>
    public const string UniverseRequired = "universe_required";

    /// <summary>The named universe doesn't exist.</summary>
    public const string UnknownUniverse = "unknown_universe";

    /// <summary>The named series or book doesn't exist in the universe.</summary>
    public const string UnknownScope = "unknown_scope";

    /// <summary>The position doesn't parse, or names a book or chapter that doesn't exist.</summary>
    public const string BadPosition = "bad_position";

    /// <summary>No entity matches the name.</summary>
    public const string UnknownEntity = "unknown_entity";

    /// <summary>More than one entity matches the name; the error lists them.</summary>
    public const string AmbiguousEntity = "ambiguous_entity";

    /// <summary>No enabled pack defines the type and no entity uses it.</summary>
    public const string UnknownType = "unknown_type";

    /// <summary>An argument is missing, out of range, or conflicts with another.</summary>
    public const string BadArgument = "bad_argument";

    /// <summary>A bug in Wolfden.</summary>
    public const string Internal = "internal_error";

    /// <summary>The den's history backend (git) failed; the message says what it said.</summary>
    public const string HistoryFailed = "history_failed";

    /// <summary>A <c>before</c> or <c>diff_from</c> that isn't a checkpoint of the history asked for.</summary>
    public const string UnknownCheckpoint = "unknown_checkpoint";

    /// <summary>A write's <c>expected_hash</c> isn't the file's hash now: it changed since it was read. Read it again.</summary>
    public const string StaleFile = "stale_file";

    /// <summary>A write would change something frozen: an entity, outline, or <c>book.md</c> in a final book (format.md F-BOOK-1).</summary>
    public const string Frozen = "frozen";

    /// <summary>A write would leave a file with an error, so the den would leave it out (format.md §1.3); the message lists the errors.</summary>
    public const string InvalidEdit = "invalid_edit";

    /// <summary>The edit can't be made in place without changing other text, or isn't one the write tools make.</summary>
    public const string UnsupportedEdit = "unsupported_edit";

    /// <summary>The entity has no section with that name (under that parent, or at that line).</summary>
    public const string UnknownSection = "unknown_section";

    /// <summary>More than one section has that name; the message lists them, to pick one by its line.</summary>
    public const string AmbiguousSection = "ambiguous_section";

    /// <summary>The book has no chapter with that key.</summary>
    public const string UnknownChapter = "unknown_chapter";

    /// <summary>A file of that name is already there, or an id is already used in the den.</summary>
    public const string AlreadyExists = "already_exists";

    /// <summary>The den's files couldn't be written; the message says what the system said.</summary>
    public const string WriteFailed = "write_failed";
}

/// <summary>A tool call can't be answered. <see cref="DenTools"/> throws it, and the server turns it into an error result.</summary>
public sealed class DenToolException : Exception
{
    /// <summary>An error with a code from <see cref="ToolErrorCodes"/> and a message.</summary>
    public DenToolException(string code, string message, IReadOnlyList<EntitySummary>? candidates = null)
        : base(message)
    {
        Code = code;
        Candidates = candidates;
    }

    /// <summary>An error with no code or message.</summary>
    public DenToolException()
        : this(ToolErrorCodes.Internal, "")
    {
    }

    /// <summary>An error with a message.</summary>
    public DenToolException(string message)
        : this(ToolErrorCodes.Internal, message)
    {
    }

    /// <summary>An error with a message and its cause.</summary>
    public DenToolException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = ToolErrorCodes.Internal;
    }

    /// <summary>The code, one of <see cref="ToolErrorCodes"/>.</summary>
    public string Code { get; } = ToolErrorCodes.Internal;

    /// <summary>For <c>ambiguous_entity</c>, the matches.</summary>
    public IReadOnlyList<EntitySummary>? Candidates { get; }

    /// <summary>The error as a result.</summary>
    public ToolErrorResult ToResult() => new(ToolJson.Version, new ToolError(Code, Message, Candidates));
}

/// <summary>One line about an entity: enough to tell it apart and to ask for it.</summary>
/// <param name="Key">What links store and <c>get_entity</c> takes: its id, or its path while it has none.</param>
/// <param name="Id">The <c>id</c> from frontmatter, or null.</param>
/// <param name="Name">The name.</param>
/// <param name="Type">The type key, such as <c>character</c>.</param>
/// <param name="Status">One of idea, undecided, candidate, draft, canon, retconned.</param>
/// <param name="Universe">The universe's key.</param>
/// <param name="ScopeKind">Where it's filed: universe, series, or book.</param>
/// <param name="ScopeKey">The key of that universe, series, or book.</param>
/// <param name="Path">The file, relative to the den root.</param>
/// <param name="From">Where it enters canon, as written, or null for the start of its scope.</param>
public sealed record EntitySummary(
    string Key, string? Id, string Name, string Type, string Status, string Universe, string ScopeKind, string ScopeKey, string Path, string? From);

/// <summary>A field value: a plain value, one entry of a field that changes over time, or a type's default.</summary>
/// <param name="Name">The field name.</param>
/// <param name="Value">The value as JSON: text as written, lists as arrays, mappings as objects.</param>
/// <param name="Kind">plain, entry, temporal, or default.</param>
/// <param name="Status">The effective status; null for a default.</param>
/// <param name="Pinned">True for pinned canon.</param>
/// <param name="From">Where the entry starts, as written.</param>
/// <param name="Until">Where the entry ends (exclusive), as written.</param>
/// <param name="Line">The line in the entity file (or, for a default, in the pack file).</param>
/// <param name="EntryIndex">In the full view, the entry's place in its field.</param>
/// <param name="Live">In the full view, false for an entry that no view shows (out of canon, or a position that doesn't resolve).</param>
public sealed record FieldView(
    string Name, JsonElement Value, string Kind, string? Status, bool Pinned, string? From, string? Until, int Line,
    int? EntryIndex = null, bool? Live = null);

/// <summary>A section or subsection.</summary>
/// <param name="Name">The heading.</param>
/// <param name="Level">2 for a section, 3 to 6 for a subsection.</param>
/// <param name="HeadingPath">The headings from the level-2 section down to this one.</param>
/// <param name="Status">The effective status.</param>
/// <param name="Pinned">True for pinned canon (a pinned section pins its subsections).</param>
/// <param name="From">Where it starts, as written.</param>
/// <param name="Until">Where it ends (exclusive), as written.</param>
/// <param name="Line">The heading's line.</param>
/// <param name="Body">The section's own text, up to the next heading.</param>
/// <param name="Bookkeeping">In the full view, true for a bookkeeping section (a change log or to-do list), which canon views leave out.</param>
public sealed record SectionView(
    string Name, int Level, IReadOnlyList<string> HeadingPath, string Status, bool Pinned, string? From, string? Until, int Line, string Body,
    bool? Bookkeeping = null);

/// <summary>The pinned facts of an entity.</summary>
/// <param name="Fields">Pinned field values.</param>
/// <param name="Sections">Pinned sections.</param>
public sealed record PinnedFacts(IReadOnlyList<FieldView> Fields, IReadOnlyList<SectionView> Sections);

/// <summary>An entity with its content.</summary>
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
/// <param name="Aliases">Other names links can use; in the full view only.</param>
/// <param name="Lead">The text before the first section, title heading included.</param>
/// <param name="Pinned">Pinned canon, ahead of the rest; also listed in place in <paramref name="Fields"/> and <paramref name="Sections"/>.</param>
/// <param name="Fields">Field values, in frontmatter order.</param>
/// <param name="Sections">Sections, in file order.</param>
/// <param name="Hash">From <c>get_entity</c>, the SHA-256 of the file's bytes, in lowercase hex: the <c>expected_hash</c> a write tool takes.</param>
public sealed record EntityView(
    string Key, string? Id, string Name, string Type, string Status, string Universe, string ScopeKind, string ScopeKey, string Path, string? From,
    IReadOnlyList<string>? Aliases, string? Lead, PinnedFacts? Pinned, IReadOnlyList<FieldView>? Fields, IReadOnlyList<SectionView>? Sections,
    string? Hash = null);

/// <summary>The pinned canon of one entity in a canon view.</summary>
/// <param name="Key">The entity's key.</param>
/// <param name="Name">Its name.</param>
/// <param name="Type">Its type.</param>
/// <param name="Fields">Its pinned field values in effect.</param>
/// <param name="Sections">Its pinned sections in effect.</param>
public sealed record PinnedEntity(string Key, string Name, string Type, IReadOnlyList<FieldView> Fields, IReadOnlyList<SectionView> Sections);

/// <summary>The series or book a view or list is narrowed to.</summary>
/// <param name="Kind">series or book.</param>
/// <param name="Key">Its key.</param>
public sealed record ScopeFilter(string Kind, string Key);

/// <summary>The result of <c>canon_as_of</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Universe">The universe's key.</param>
/// <param name="Position">The position as given, or null for the end of the story.</param>
/// <param name="At">The position in words.</param>
/// <param name="Filter">The series or book the view is narrowed to, or null.</param>
/// <param name="Count">How many entities the view holds (every one is returned).</param>
/// <param name="Pinned">Pinned canon (format.md F-VIEW-2), first.</param>
/// <param name="Entities">Every entity in the view (F-VIEW-1), in F-VIEW-3 order.</param>
public sealed record CanonAsOfResult(
    int Version, string Universe, string? Position, string At, ScopeFilter? Filter, int Count,
    IReadOnlyList<PinnedEntity> Pinned, IReadOnlyList<EntityView> Entities);

/// <summary>The result of <c>get_entity</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="View"><c>full</c> (every fact, in or out of canon) or <c>canon_as_of</c>.</param>
/// <param name="Position">For <c>canon_as_of</c>, the position.</param>
/// <param name="InView">For <c>canon_as_of</c>, false if the entity isn't in canon at the position.</param>
/// <param name="Note">Why the entity is missing, when it is.</param>
/// <param name="Entity">The entity, or null when it isn't in the view.</param>
public sealed record GetEntityResult(int Version, string View, string? Position, bool InView, string? Note, EntityView? Entity);

/// <summary>The result of <c>list_entities</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Count">How many entities this result holds.</param>
/// <param name="Total">How many entities match in all.</param>
/// <param name="Truncated">True if more matched than <c>limit</c>.</param>
/// <param name="Entities">The entities, by universe and then in F-VIEW-3 order.</param>
public sealed record ListEntitiesResult(int Version, int Count, int Total, bool Truncated, IReadOnlyList<EntitySummary> Entities);

/// <summary>A search hit.</summary>
/// <param name="Entity">The entity.</param>
/// <param name="Section">The section the match is in, or null for the name, aliases, or lead.</param>
/// <param name="Line">The section heading's line, or null.</param>
/// <param name="Snippet">An excerpt with each match in square brackets.</param>
/// <param name="Status">The effective status of the matching section, or of the entity.</param>
/// <param name="InCanon">True if the match is canon text (draft or canon, not bookkeeping).</param>
/// <param name="InComment">True if the match is in an HTML comment, such as a to-do note.</param>
public sealed record SearchHitView(EntitySummary Entity, string? Section, int? Line, string Snippet, string Status, bool InCanon, bool InComment);

/// <summary>The result of <c>search</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Query">The query.</param>
/// <param name="Count">How many hits this result holds.</param>
/// <param name="Truncated">True if more hits matched than <c>limit</c>.</param>
/// <param name="Hits">The hits, best first.</param>
public sealed record SearchResult(int Version, string Query, int Count, bool Truncated, IReadOnlyList<SearchHitView> Hits);

/// <summary>A diagnostic from <c>check</c>.</summary>
/// <param name="Path">The file, relative to the den root.</param>
/// <param name="Line">The line, or null.</param>
/// <param name="Severity">error or warning.</param>
/// <param name="Rule">The rule id from format.md or types.md, such as F-LINK-8.</param>
/// <param name="Message">What's wrong.</param>
public sealed record DiagnosticView(string Path, int? Line, string Severity, string Rule, string Message);

/// <summary>The result of <c>check</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Universe">The universe checked (its files and the den-level ones), or null for the whole den.</param>
/// <param name="Loaded">False if den.yaml doesn't load, so only it was checked.</param>
/// <param name="SpoilerChecks">True if the den turns on spoiler checks; null if den.yaml doesn't load.</param>
/// <param name="Entities">The entities checked.</param>
/// <param name="Errors">All errors in what was checked.</param>
/// <param name="Warnings">All warnings in what was checked.</param>
/// <param name="Count">How many diagnostics this result holds.</param>
/// <param name="Total">How many diagnostics match the filters in all.</param>
/// <param name="Truncated">True if more matched than <c>limit</c>.</param>
/// <param name="Diagnostics">The diagnostics, by path and line.</param>
/// <param name="Note">For a check of one universe, set when the comparison of ids with other universes (F-ID-3) was skipped, and why.</param>
public sealed record CheckResult(
    int Version, string? Universe, bool Loaded, bool? SpoilerChecks, int Entities, int Errors, int Warnings, int Count, int Total, bool Truncated,
    IReadOnlyList<DiagnosticView> Diagnostics, string? Note = null);

/// <summary>The result of <c>get_history</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Entity">The entity whose file's history this is.</param>
/// <param name="Backend">The history backend: <c>git</c>, or <c>none</c> for a den without history.</param>
/// <param name="Available">False if the den's history can't be read; <paramref name="Reason"/> says why, and there are no entries.</param>
/// <param name="Reason">Why history isn't available, in words for the writer, or null.</param>
/// <param name="Count">How many entries this result holds.</param>
/// <param name="Truncated">True if there are older entries: ask again with <c>before</c> set to <paramref name="NextBefore"/>.</param>
/// <param name="NextBefore">The <c>before</c> value for the next, older page, or null if there's none.</param>
/// <param name="Entries">The checkpoints that changed the entity's file, newest first.</param>
/// <param name="Diff">With <c>diff</c> or <c>diff_from</c>, what changed in the entity's file since then, up to the file as it is now.</param>
/// <param name="Note">Set when a diff was asked for but couldn't be made, and why (for example, nothing has been checkpointed yet).</param>
public sealed record GetHistoryResult(
    int Version, EntitySummary Entity, string Backend, bool Available, string? Reason, int Count, bool Truncated, string? NextBefore,
    IReadOnlyList<HistoryEntryView>? Entries, HistoryDiffView? Diff, string? Note = null);

/// <summary>One checkpoint.</summary>
/// <param name="Id">The checkpoint's id (a git commit hash), for <c>before</c> and <c>diff_from</c>.</param>
/// <param name="When">When it was recorded.</param>
/// <param name="Author">Who recorded it.</param>
/// <param name="Message">Its message, with any <c>Co-Authored-By</c> lines.</param>
/// <param name="Changes">The den files it changed.</param>
public sealed record HistoryEntryView(string Id, DateTimeOffset When, string Author, string Message, IReadOnlyList<HistoryChangeView> Changes);

/// <summary>How a checkpoint or diff changed one den file.</summary>
/// <param name="Kind">added, modified, deleted, or renamed.</param>
/// <param name="Path">The file, relative to the den root, after the change.</param>
/// <param name="OldPath">For a rename, where it was before.</param>
public sealed record HistoryChangeView(string Kind, string Path, string? OldPath);

/// <summary>What changed between a checkpoint and the files as they are now.</summary>
/// <param name="From">The checkpoint compared from.</param>
/// <param name="To">The checkpoint compared to, or null for the files as they are now.</param>
/// <param name="Files">Each file that differs, with its unified diff. Empty when nothing changed.</param>
public sealed record HistoryDiffView(string From, string? To, IReadOnlyList<HistoryFileDiffView> Files);

/// <summary>How one file differs.</summary>
/// <param name="Change">How it changed.</param>
/// <param name="Patch">The change as a unified diff.</param>
public sealed record HistoryFileDiffView(HistoryChangeView Change, string Patch);

/// <summary>A file a write tool wrote.</summary>
/// <param name="Path">The file, relative to the den root.</param>
/// <param name="Hash">The SHA-256 of its new bytes, in lowercase hex: the <c>expected_hash</c> for the next write to it.</param>
/// <param name="Created">True for a file the write made.</param>
public sealed record WrittenFileView(string Path, string Hash, bool Created = false);

/// <summary>
/// The result of a write tool (<c>create_entity</c>, <c>update_fields</c>,
/// <c>append_section</c>, <c>add_idea</c>, <c>rename_section</c>,
/// <c>update_chapter_fields</c>, <c>update_chapter_defaults</c>). Writes go to the files only: nothing is
/// committed until <c>checkpoint</c>.
/// </summary>
/// <param name="Version">The shape version.</param>
/// <param name="Changed">False if the edit asked for what the file already says, so nothing was written.</param>
/// <param name="Path">The file the edit was for, relative to the den root.</param>
/// <param name="Hash">The SHA-256 of that file's bytes now, in lowercase hex: the <c>expected_hash</c> for the next write to it.</param>
/// <param name="Entity">The entity as it is now, for writes to an entity.</param>
/// <param name="Effects">What the edit means, in words, where that depends on more than the bytes written: a chapter that now has no POV and the default it no longer inherits, a type's default that now shows, an entity that left canon.</param>
/// <param name="PinnedCanon">The pinned facts (hard canon, format.md F-PIN-1) the edit changed, added, or removed. Empty if none. Check such an edit with the writer.</param>
/// <param name="Warnings">Warnings about the file as it is now, as <c>check</c> reports them. Errors are never written: such an edit is refused.</param>
/// <param name="Files">Every file written, when there's more than one (a section rename rewrites links in other files).</param>
/// <param name="Chapter">For <c>update_chapter_fields</c>, the chapter as it is now.</param>
/// <param name="IdAdded">The id given to a file that had none (format.md F-ID-6), or null.</param>
/// <param name="ChapterDefaults">For <c>update_chapter_defaults</c>, the container's <c>chapter_defaults</c> as they are now.</param>
public sealed record WriteResult(
    int Version, bool Changed, string Path, string Hash, EntitySummary? Entity, IReadOnlyList<string> Effects, IReadOnlyList<string> PinnedCanon,
    IReadOnlyList<DiagnosticView> Warnings, IReadOnlyList<WrittenFileView>? Files = null, ChapterView? Chapter = null, string? IdAdded = null,
    ChapterDefaultsView? ChapterDefaults = null);

/// <summary>One chapter field of a chapter: its own value, and what it inherits (types.md F-CHAP-5).</summary>
/// <param name="Name">The field name, such as <c>pov</c>.</param>
/// <param name="Value">The effective value, as JSON, or null when nothing sets it.</param>
/// <param name="From">Where the effective value comes from: <c>chapter</c> (its own), <c>book</c>, <c>series</c>, or <c>universe</c> (that container's <c>chapter_defaults</c>), or null when nothing sets it.</param>
/// <param name="FromKey">The key of the container whose <c>chapter_defaults</c> gives it, or null.</param>
/// <param name="None">True for a list field set to an empty list, "none here", which stops inheritance.</param>
/// <param name="Own">The chapter's own value as JSON, or null if it doesn't set the field (and inherits).</param>
/// <param name="Default">What the chapter would inherit without its own value, as JSON, or null if nothing above sets it.</param>
/// <param name="DefaultFrom">Where that default comes from: <c>book</c>, <c>series</c>, or <c>universe</c>, or null.</param>
public sealed record ChapterFieldView(
    string Name, JsonElement? Value, string? From, string? FromKey, bool None, JsonElement? Own, JsonElement? Default, string? DefaultFrom);

/// <summary>A chapter and its fields.</summary>
/// <param name="Book">The book's key.</param>
/// <param name="Key">The chapter key, which positions name (<c>book:key</c>).</param>
/// <param name="Number">The display number, or null.</param>
/// <param name="Title">The title, or null.</param>
/// <param name="Line">The chapter's line in <c>book.md</c>.</param>
/// <param name="Fields">Each chapter field the chapter sets or inherits, in name order.</param>
public sealed record ChapterView(string Book, string Key, long? Number, string? Title, int Line, IReadOnlyList<ChapterFieldView> Fields);

/// <summary>The result of <c>get_chapter</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Universe">The universe's key.</param>
/// <param name="Book">The book's key.</param>
/// <param name="Path">The book's <c>book.md</c>, relative to the den root.</param>
/// <param name="Hash">The SHA-256 of <c>book.md</c>'s bytes, in lowercase hex: the <c>expected_hash</c> for <c>update_chapter_fields</c>.</param>
/// <param name="Final">True if the book is final (frozen, format.md F-BOOK-1), so it can't be edited.</param>
/// <param name="Count">How many chapters this result holds.</param>
/// <param name="Chapters">The chapter asked for, or every chapter, in book order.</param>
public sealed record GetChapterResult(int Version, string Universe, string Book, string Path, string Hash, bool Final, int Count, IReadOnlyList<ChapterView> Chapters);

/// <summary>One field of a container's <c>chapter_defaults</c> (types.md F-CHAP-5).</summary>
/// <param name="Name">The field name, such as <c>pov</c>.</param>
/// <param name="Own">The value this container's <c>chapter_defaults</c> sets, as JSON, or null if it doesn't set the field (and its books inherit it from above).</param>
/// <param name="None">True for a list field this container sets to an empty list, "none here", which stops inheritance from above.</param>
/// <param name="Inherited">What the container's books would get from the containers above without this container's value, as JSON, or null if none of them sets it.</param>
/// <param name="InheritedFrom">Where that comes from: <c>series</c> or <c>universe</c>, or null.</param>
/// <param name="InheritedFromKey">The key of that container, or null.</param>
public sealed record ChapterDefaultFieldView(string Name, JsonElement? Own, bool None, JsonElement? Inherited, string? InheritedFrom, string? InheritedFromKey);

/// <summary>A container's <c>chapter_defaults</c>.</summary>
/// <param name="Kind"><c>universe</c>, <c>series</c>, or <c>book</c>.</param>
/// <param name="Key">The container's key.</param>
/// <param name="Path">Its file (<c>universe.md</c>, <c>series.md</c>, or <c>book.md</c>), relative to the den root.</param>
/// <param name="Fields">Each chapter field it or a container above it sets, in name order.</param>
public sealed record ChapterDefaultsView(string Kind, string Key, string Path, IReadOnlyList<ChapterDefaultFieldView> Fields);

/// <summary>A book whose chapters a container's <c>chapter_defaults</c> reach.</summary>
/// <param name="Key">The book's key.</param>
/// <param name="Final">True if the book is final (frozen): an edit that would change what its chapters have is refused.</param>
/// <param name="Chapters">How many chapters it has.</param>
public sealed record BookBelowView(string Key, bool Final, int Chapters);

/// <summary>The result of <c>get_chapter_defaults</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Universe">The universe's key.</param>
/// <param name="Kind"><c>universe</c>, <c>series</c>, or <c>book</c>.</param>
/// <param name="Key">The container's key.</param>
/// <param name="Path">Its file, relative to the den root.</param>
/// <param name="Hash">The SHA-256 of that file's bytes, in lowercase hex: the <c>expected_hash</c> for <c>update_chapter_defaults</c>.</param>
/// <param name="Final">True for a final (frozen) book, whose defaults can't be edited.</param>
/// <param name="Fields">Each chapter field it or a container above it sets, in name order.</param>
/// <param name="Books">The books whose chapters its defaults reach, in story order.</param>
public sealed record GetChapterDefaultsResult(
    int Version, string Universe, string Kind, string Key, string Path, string Hash, bool Final, IReadOnlyList<ChapterDefaultFieldView> Fields, IReadOnlyList<BookBelowView> Books);

/// <summary>The result of <c>checkpoint</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Backend">The history backend: <c>git</c>, or <c>none</c> for a den without history.</param>
/// <param name="Available">False if the den has no history to record into; <paramref name="Reason"/> says why. The edits are still saved in the files.</param>
/// <param name="Reason">Why a checkpoint isn't available, in words for the writer, or null.</param>
/// <param name="Recorded">True if a checkpoint was recorded; false if nothing changed since the last one.</param>
/// <param name="Checkpoint">The checkpoint recorded, or null.</param>
/// <param name="WrittenBySession">Of the files recorded, the ones this session's write tools saved since the last checkpoint that recorded them.</param>
/// <param name="ChangedOutsideSession">Of the files recorded, the ones that changed some other way, such as in the writer's editor: tell the writer they're in the checkpoint too.</param>
public sealed record CheckpointResult(
    int Version, string Backend, bool Available, string? Reason, bool Recorded, HistoryEntryView? Checkpoint,
    IReadOnlyList<string>? WrittenBySession = null, IReadOnlyList<string>? ChangedOutsideSession = null);
