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

/// <summary>Tool errors as results.</summary>
public static class ToolErrors
{
    /// <summary>The error as a result.</summary>
    public static ToolErrorResult ToResult(this DenToolException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(ToolJson.Version, new ToolError(e.Code, e.Message, e.Candidates));
    }
}



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


/// <summary>A file a write tool wrote.</summary>
/// <param name="Path">The file, relative to the den root.</param>
/// <param name="Hash">The SHA-256 of its new bytes, in lowercase hex: the <c>expected_hash</c> for the next write to it.</param>
/// <param name="Created">True for a file the write made.</param>
/// <param name="MovedFrom">For a file <c>move_entity</c> moved, its old path, which is gone now; otherwise null.</param>
public sealed record WrittenFileView(string Path, string Hash, bool Created = false, string? MovedFrom = null);

/// <summary>
/// The result of a write tool (<c>create_entity</c>, <c>update_fields</c>,
/// <c>append_section</c>, <c>add_idea</c>, <c>rename_section</c>,
/// <c>rename_entity</c>, <c>change_alias</c>, <c>copy_entity</c>, <c>move_entity</c>,
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
/// <param name="Ignored">True if this container writes the field but the value is null or doesn't fit the field, so it's ignored (types.md F-CHAP-3) and its books inherit as if it weren't there; null otherwise.</param>
/// <param name="Written">For an ignored field, the value as written, as JSON.</param>
public sealed record ChapterDefaultFieldView(
    string Name, JsonElement? Own, bool None, JsonElement? Inherited, string? InheritedFrom, string? InheritedFromKey, bool? Ignored = null, JsonElement? Written = null);

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
/// <param name="Path">Its <c>book.md</c>, relative to the den root, which tells apart books that share a key (F-KEY-5).</param>
public sealed record BookBelowView(string Key, bool Final, int Chapters, string Path);

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
