using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wolfden.Mcp;

// The JSON shapes the MCP tools return (design.md §8). Names are snake_case,
// like the tools' arguments (as_of), and null members are left out, so a
// model reads only what applies. Every result carries a version for its
// shape: within a version, members may be added, but never renamed, removed,
// or given a new meaning.

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
public sealed record EntityView(
    string Key, string? Id, string Name, string Type, string Status, string Universe, string ScopeKind, string ScopeKey, string Path, string? From,
    IReadOnlyList<string>? Aliases, string? Lead, PinnedFacts? Pinned, IReadOnlyList<FieldView>? Fields, IReadOnlyList<SectionView>? Sections);

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
/// <param name="EntityCount">How many entities the view holds.</param>
/// <param name="Pinned">Pinned canon (format.md F-VIEW-2), first.</param>
/// <param name="Entities">Every entity in the view (F-VIEW-1), in F-VIEW-3 order.</param>
public sealed record CanonAsOfResult(
    int Version, string Universe, string? Position, string At, ScopeFilter? Filter, int EntityCount,
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
/// <param name="Count">How many entities match.</param>
/// <param name="Truncated">True if more matched than <c>limit</c>.</param>
/// <param name="Entities">The entities, by universe and then in F-VIEW-3 order.</param>
public sealed record ListEntitiesResult(int Version, int Count, bool Truncated, IReadOnlyList<EntitySummary> Entities);

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
/// <param name="Count">How many hits are returned.</param>
/// <param name="Hits">The hits, best first.</param>
public sealed record SearchResult(int Version, string Query, int Count, IReadOnlyList<SearchHitView> Hits);

/// <summary>A diagnostic from <c>check</c>.</summary>
/// <param name="Path">The file, relative to the den root.</param>
/// <param name="Line">The line, or null.</param>
/// <param name="Severity">error or warning.</param>
/// <param name="Rule">The rule id from format.md or types.md, such as F-LINK-8.</param>
/// <param name="Message">What's wrong.</param>
public sealed record DiagnosticView(string Path, int? Line, string Severity, string Rule, string Message);

/// <summary>The result of <c>check</c>.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Loaded">False if den.yaml doesn't load, so only it was checked.</param>
/// <param name="SpoilerChecks">True if the den turns on spoiler checks; null if den.yaml doesn't load.</param>
/// <param name="Entities">The entities checked.</param>
/// <param name="Errors">All errors in the den.</param>
/// <param name="Warnings">All warnings in the den.</param>
/// <param name="Count">How many diagnostics match the filters.</param>
/// <param name="Truncated">True if more matched than <c>limit</c>.</param>
/// <param name="Diagnostics">The diagnostics, by path and line.</param>
public sealed record CheckResult(
    int Version, bool Loaded, bool? SpoilerChecks, int Entities, int Errors, int Warnings, int Count, bool Truncated, IReadOnlyList<DiagnosticView> Diagnostics);
