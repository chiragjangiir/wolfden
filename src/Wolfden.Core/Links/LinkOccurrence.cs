using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Links;

/// <summary>Where in a den a link is written (format.md F-LINK-1, F-LINK-13).</summary>
public enum LinkSite
{
    /// <summary>An entity's lead: the body before its first section (F-SEC-2).</summary>
    EntityLead,

    /// <summary>A section of an entity (§9).</summary>
    EntitySection,

    /// <summary>A plain value under an entity's <c>fields</c> (F-FLD-5).</summary>
    EntityField,

    /// <summary>A field entry under an entity's <c>fields</c> (F-TMP-1).</summary>
    EntityFieldEntry,

    /// <summary>A universe, series, or book file's body (F-CTR-3).</summary>
    ContainerBody,

    /// <summary>A chapter item's <c>fields</c> in <c>book.md</c> (types.md F-CHAP-3).</summary>
    ChapterField,

    /// <summary>A container's <c>chapter_defaults</c> (types.md F-CHAP-5).</summary>
    ChapterDefault,

    /// <summary>A row of an outline table (F-OUT-9).</summary>
    OutlineRow,

    /// <summary>Anything else in <c>outline.md</c>: free-form notes, other tables, and rows with no valid row id.</summary>
    OutlineNotes,
}

/// <summary>
/// A link's position for the spoiler check (F-LINK-13), with a description
/// for messages.
/// </summary>
/// <param name="Point">The place in story order.</param>
/// <param name="Description">The position as written, or the start it stands for.</param>
/// <param name="Chapter">For a link in <c>chapter_defaults</c>, the chapter that inherits it.</param>
public sealed record LinkPosition(StoryPoint Point, string Description, Chapter? Chapter = null);

/// <summary>A link found in a den file, with what it takes to resolve and check it.</summary>
public sealed class LinkOccurrence
{
    internal LinkOccurrence(string path, WikiLink link, LinkSite site, Container scope)
    {
        Path = path;
        Link = link;
        Site = site;
        Scope = scope;
    }

    /// <summary>The file the link is in, relative to the den root.</summary>
    public string Path { get; }

    /// <summary>The link as written, with its line and column.</summary>
    public WikiLink Link { get; }

    /// <summary>Where in the file the link is.</summary>
    public LinkSite Site { get; }

    /// <summary>The scope the link resolves from (F-LINK-7): the entity's scope, the container itself, or an outline's book.</summary>
    public Container Scope { get; }

    /// <summary>The entity the link is in, for links in entity files.</summary>
    public CatalogEntity? Entity { get; internal init; }

    /// <summary>The section the link is in, for <see cref="LinkSite.EntitySection"/>.</summary>
    public Section? Section { get; internal init; }

    /// <summary>The field or chapter field the link is in.</summary>
    public string? Field { get; internal init; }

    /// <summary>The chapter, for <see cref="LinkSite.ChapterField"/>.</summary>
    public Chapter? Chapter { get; internal init; }

    /// <summary>The row id, for <see cref="LinkSite.OutlineRow"/>.</summary>
    public string? OutlineRow { get; internal init; }

    /// <summary>
    /// The effective status (F-STAT-3) of what holds the link: the section,
    /// entry, or outline row, or the entity for its lead and plain fields.
    /// Null where there's none: container files and outline notes.
    /// </summary>
    public FactStatus? Status { get; internal init; }

    /// <summary>True for a link in a bookkeeping section (F-SEC-12), which skips F-LINK-12 and F-LINK-13.</summary>
    public bool IsBookkeeping { get; internal init; }

    /// <summary>
    /// The link's positions for F-LINK-13: usually one, several for a link
    /// in <c>chapter_defaults</c> (one per chapter that inherits it), and
    /// none for a container body, outline notes, or a position that doesn't
    /// resolve.
    /// </summary>
    public IReadOnlyList<LinkPosition> Positions { get; internal init; } = [];

    /// <summary>
    /// True if the link is in an in-canon fact, for F-LINK-12: a site with
    /// an in-canon status, or a chapter field, which is a fact about its
    /// chapter with no status of its own.
    /// </summary>
    public bool IsInCanonFact => IsIgnoredValue ? false
        : Site is LinkSite.ChapterField or LinkSite.ChapterDefault || Status?.IsInCanon() == true;

    /// <summary>
    /// True for a link in a chapter-field value that types.md F-CHAP-3
    /// ignores, such as an entry. The link still resolves, but it has no
    /// position and isn't a fact, so F-LINK-12 and F-LINK-13 skip it.
    /// </summary>
    public bool IsIgnoredValue { get; internal init; }

    /// <inheritdoc />
    public override string ToString() => $"{Path}:{Link.Line}:{Link.Column}: {Link.Text}";
}
