namespace Wolfden.Index;

/// <summary>The kind of container a canon view is filtered to.</summary>
public enum CanonFilterKind
{
    /// <summary>A series: its entities, its books', and its universe's.</summary>
    Series,

    /// <summary>A book: its entities, its series' (if it's in one), and its universe's.</summary>
    Book,
}

/// <summary>
/// A filter on a canon view (format.md F-VIEW-1): a series or book of the
/// view's universe, named by key. The filtered view covers the entities
/// filed in that container's scope chain and in the containers inside it.
/// A filter only narrows what's covered; where each entity enters canon
/// still comes from story order across the universe.
/// </summary>
/// <param name="Kind">Series or book.</param>
/// <param name="Key">The series or book key (its folder name).</param>
public sealed record CanonFilter(CanonFilterKind Kind, string Key)
{
    /// <summary>A series filter.</summary>
    public static CanonFilter ForSeries(string seriesKey) => new(CanonFilterKind.Series, seriesKey);

    /// <summary>A book filter, for a book in a series or a standalone book.</summary>
    public static CanonFilter ForBook(string bookKey) => new(CanonFilterKind.Book, bookKey);

    /// <inheritdoc />
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()} '{Key}'";
}

/// <summary>
/// A canon-as-of view (format.md §9.4, F-VIEW-1): the entities of a
/// universe, at every scope or only those a filter covers, that are in
/// canon at a story position, each with its lead, the field values in
/// effect there, and the in-canon sections in effect there. Nothing that
/// enters canon after the position is in it. Entities and their parts come
/// in the fixed order of F-VIEW-3, so two views of the same files compare
/// equal.
/// </summary>
/// <param name="Universe">The universe's key.</param>
/// <param name="Position">The position as given, or null for the end of the story: all canon so far (F-VIEW-1).</param>
/// <param name="Filter">The series or book the view is filtered to, or null for every scope of the universe.</param>
/// <param name="Entities">The entities in F-VIEW-3 order.</param>
public sealed record CanonView(string Universe, string? Position, CanonFilter? Filter, IReadOnlyList<CanonEntity> Entities)
{
    /// <summary>True if the view is as of the end of the story.</summary>
    public bool IsEndOfStory => Position is null;

    /// <summary>
    /// The pinned canon view (F-VIEW-2): the pinned field entries and
    /// sections of this view, grouped by entity, without leads or defaults
    /// (types.md F-FT-15). Entities with nothing pinned are left out.
    /// </summary>
    public CanonView Pinned() => this with
    {
        Entities = Entities
            .Select(e => e with
            {
                Lead = "",
                Fields = e.Fields.Where(f => f.Pinned).ToList(),
                Sections = e.Sections.Where(s => s.Pinned).ToList(),
            })
            .Where(e => e.Fields.Count > 0 || e.Sections.Count > 0)
            .ToList(),
    };
}

/// <summary>An entity in a canon view.</summary>
/// <param name="Key">What a link to it stores: its id, or its path while it has none (F-LINK-10).</param>
/// <param name="Id">The <c>id</c> from frontmatter, or null.</param>
/// <param name="Name">The name as written.</param>
/// <param name="Type">The type key as written.</param>
/// <param name="Status"><c>draft</c> or <c>canon</c>: a view marks draft material as draft (F-ENT-5).</param>
/// <param name="ScopeKind"><c>universe</c>, <c>series</c>, or <c>book</c>: the scope the entity lives at.</param>
/// <param name="ScopeKey">The key of that container.</param>
/// <param name="Path">The entity file, relative to the den root.</param>
/// <param name="From">The entity's <c>from</c> as written, or null if it enters at the start of its scope (F-ENT-6).</param>
/// <param name="Title">The level-1 heading in the lead, or null.</param>
/// <param name="Lead">The lead: the body before the first section (F-SEC-2).</param>
/// <param name="LeadLine">The 1-based line where the lead starts.</param>
/// <param name="Fields">The field values in effect, in frontmatter order, then defaults for fields the file doesn't write, in the type's field order.</param>
/// <param name="Sections">The sections in effect, subsections included, in file order.</param>
public sealed record CanonEntity(
    string Key, string? Id, string Name, string Type, string Status, string ScopeKind, string ScopeKey, string Path,
    string? From, string? Title, string Lead, int LeadLine, IReadOnlyList<CanonFieldValue> Fields, IReadOnlyList<CanonSection> Sections)
{
    /// <summary>True if the entity is <c>draft</c>, not settled canon.</summary>
    public bool IsDraft => Status == "draft";
}

/// <summary>
/// A field's value in a canon view: the plain value, the live entry in
/// effect (F-TMP-4, F-TMP-5), or the type's default where the entity has
/// none (types.md F-FT-15).
/// </summary>
/// <param name="Name">The field name.</param>
/// <param name="ValueJson">The value as JSON, as the index stores it: scalars are strings as written, lists are arrays, and mappings are objects.</param>
/// <param name="Kind"><c>plain</c>, <c>entry</c>, <c>temporal</c>, or <c>default</c>.</param>
/// <param name="EntryIndex">The entry's 0-based place in its field, or null for a plain value or a default.</param>
/// <param name="Status">The effective status (F-STAT-3), <c>draft</c> or <c>canon</c>; null for a default, which isn't a fact.</param>
/// <param name="Pinned">True for a pinned entry (F-PIN-1).</param>
/// <param name="From">The entry's <c>from</c> as written, or null.</param>
/// <param name="Until">The entry's <c>until</c> as written, or null.</param>
/// <param name="Path">The file to jump to: the entity file, or for a default the pack file that defines it.</param>
/// <param name="Line">The 1-based line in that file.</param>
public sealed record CanonFieldValue(
    string Name, string ValueJson, string Kind, int? EntryIndex, string? Status, bool Pinned, string? From, string? Until, string Path, int Line)
{
    /// <summary>True for a type's default, shown because the entity has no value here (types.md F-FT-15).</summary>
    public bool IsDefault => Kind == "default";
}

/// <summary>A section in a canon view (format.md §9).</summary>
/// <param name="Name">The section name (F-SEC-3).</param>
/// <param name="Level">2 for an entity section, 3 to 6 for a subsection.</param>
/// <param name="HeadingPath">The names from the level-2 section down to this one.</param>
/// <param name="Body">The section's own text, after its heading and comment, up to the next heading.</param>
/// <param name="Status">The effective status (F-STAT-3), <c>draft</c> or <c>canon</c>.</param>
/// <param name="Pinned">True if it or a section holding it is pinned (F-PIN-1, F-PIN-2).</param>
/// <param name="From">The comment's <c>from</c> as written, or null.</param>
/// <param name="Until">The comment's <c>until</c> as written, or null.</param>
/// <param name="Path">The entity file.</param>
/// <param name="Line">The heading's 1-based line.</param>
public sealed record CanonSection(
    string Name, int Level, IReadOnlyList<string> HeadingPath, string Body, string Status, bool Pinned, string? From, string? Until,
    string Path, int Line);

/// <summary>A canon view can't be made: its universe or filter doesn't exist, or its position doesn't parse or resolve.</summary>
public sealed class CanonViewException : Exception
{
    /// <summary>An exception with a message for the writer.</summary>
    public CanonViewException(string message)
        : base(message)
    {
    }

    /// <summary>An exception with no message.</summary>
    public CanonViewException()
    {
    }

    /// <summary>An exception with a message and the exception that caused it.</summary>
    public CanonViewException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
