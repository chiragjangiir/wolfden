using System.Text.Json;
using Wolfden.Index.Reading;

namespace Wolfden.Index.Writing;

// The shapes of the write results and of `check` (design.md §8, §9): what
// the MCP write tools return, as snake_case JSON, and what the web API's
// write endpoints build on, as camelCase. The records carry no naming of
// their own; each surface serializes them with its own options. Every
// result carries a version for its shape: within a version, members may be
// added, but never renamed, removed, or given a new meaning.

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

/// <summary>
/// An entity's file as an editor needs it (the web UI's editing, #28): read
/// from the file, not the index, so its hash is the one an edit must give.
/// </summary>
/// <param name="Version">The shape version.</param>
/// <param name="Entity">The entity.</param>
/// <param name="Hash">The SHA-256 of the file's bytes, in lowercase hex: the hash an edit of it must give.</param>
/// <param name="Text">The whole file, with its line breaks as LF and without a byte order mark.</param>
/// <param name="LineEndings">The file's own line breaks: <c>lf</c>, <c>crlf</c>, or <c>mixed</c>. An edit writes them so.</param>
/// <param name="ByteOrderMark">True if the file starts with a byte order mark, which an edit keeps.</param>
/// <param name="Frozen">Why the file can't be edited (it's in a final book, F-BOOK-1), or null if it can.</param>
/// <param name="Lead">The lead.</param>
/// <param name="Sections">Every section, in file order, subsections included.</param>
/// <param name="StandardSections">The standard sections of the entity's type, in the type's order, or none for an untyped entity.</param>
/// <param name="Fields">Each field under the file's <c>fields:</c>, in file order, with its value as written.</param>
/// <param name="FieldTypes">The fields the entity's type defines, in field order (types.md F-FT-3), then the universal
/// <c>placeholder</c> (F-FT-17), which every entity has, typed or not. <c>row</c> (F-FT-16) isn't here: it's edited in the file.</param>
public sealed record EntitySourceResult(
    int Version, EntitySummary Entity, string Hash, string Text, string LineEndings, bool ByteOrderMark, string? Frozen, SourceLead Lead,
    IReadOnlyList<SourceSection> Sections, IReadOnlyList<string> StandardSections, IReadOnlyList<SourceField> Fields, IReadOnlyList<SourceFieldType> FieldTypes);

/// <summary>One field the entity's file sets (format.md §7.2), as a field editor needs it.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Line">The line its key is on.</param>
/// <param name="Value">
/// Its value as written, whatever its shape (a plain value, an entry, or a temporal list, format.md §7.3), as JSON:
/// a plain <c>true</c> or <c>false</c> as a boolean, a plain number as <c>{"$number": "2.50"}</c> with its text as
/// written, a null as null, lists and mappings as arrays and objects, and every other scalar, quoted or not, as a
/// string of its text. <c>/api/edit/field</c> takes numbers in that form too.
/// </param>
public sealed record SourceField(string Name, int Line, JsonElement Value);

/// <summary>A field an entity's type defines (types.md §5), as a field editor needs it.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Type">Its field type: <c>string</c>, <c>text</c>, <c>number</c>, <c>bool</c>, <c>date</c>, <c>enum</c>, <c>ref</c>, or <c>position</c>.</param>
/// <param name="List">True for a list field (F-FT-12).</param>
/// <param name="Temporal">True if its value can change over the story (F-FT-13).</param>
/// <param name="Required">True if it should have a value (F-FT-14).</param>
/// <param name="Values">For an enum, its values, in order; otherwise empty.</param>
/// <param name="To">For a ref, the types it links to, or null for any type.</param>
/// <param name="Description">Its description, or null.</param>
/// <param name="Default">The value views show when it has none (F-FT-15), as JSON, or null.</param>
public sealed record SourceFieldType(
    string Name, string Type, bool List, bool Temporal, bool Required, IReadOnlyList<string> Values, IReadOnlyList<string>? To, string? Description,
    JsonElement? Default);

/// <summary>The lead of an entity's file (format.md F-SEC-2): the text before its first section, title heading included.</summary>
/// <param name="Line">The line it starts at.</param>
/// <param name="Text">Its text as written, with line breaks as LF and without the blank lines at either end.</param>
public sealed record SourceLead(int Line, string Text);

/// <summary>One section of an entity's file, as an editor needs it.</summary>
/// <param name="Index">Its place among the file's sections, from 0, in file order, subsections included. An edit names it by this and its name.</param>
/// <param name="Name">Its name, the heading's text (F-SEC-3).</param>
/// <param name="Level">2 for a section, 3 to 6 for a subsection.</param>
/// <param name="Line">The line its heading starts at.</param>
/// <param name="Comment">Its section comment as written (<c>&lt;!-- wolfden: ... --&gt;</c>, format.md §9.2), or null. It isn't part of <paramref name="Body"/>, and an edit of the body keeps it.</param>
/// <param name="Body">Its own text as written, after the heading and section comment, up to the next heading (subsections aren't included), with line breaks as LF and without the blank lines at either end. Other HTML comments in it are part of it.</param>
/// <param name="Pinned">True if it's pinned canon (F-PIN-1), by its own comment or a parent's.</param>
/// <param name="Parent">The index of the section that holds it, or null for a level-2 section.</param>
public sealed record SourceSection(int Index, string Name, int Level, int Line, string? Comment, string Body, bool Pinned, int? Parent);
