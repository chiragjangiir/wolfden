using Wolfden.Core.Documents;
using Wolfden.Core.Links;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;

namespace Wolfden.Core.Entities;

/// <summary>
/// An entity file that loaded without errors (format.md §7, F-GEN-2): what
/// links resolve to and what views list. It's an in-memory record, built
/// from the file each time, that an index can persist.
/// </summary>
public sealed class CatalogEntity
{
    private readonly IReadOnlyList<string> _normalizedAliases;

    internal CatalogEntity(string path, MarkdownFile file, Container scope, string? id, string? slug, string name,
        IReadOnlyList<string> aliases, string type, FactStatus status, StoryPosition? from, PositionResolution? fromResolution)
    {
        Path = path;
        File = file;
        Scope = scope;
        Id = id;
        Slug = slug;
        Name = name;
        Aliases = aliases;
        Type = type;
        Status = status;
        From = from;
        FromResolution = fromResolution;
        NormalizedSlug = slug is null ? null : LinkText.Normalize(slug);
        NormalizedName = LinkText.Normalize(name);
        _normalizedAliases = aliases.Select(LinkText.Normalize).ToList();
    }

    /// <summary>The file's path, relative to the den root, with <c>/</c>.</summary>
    public string Path { get; }

    /// <summary>The parsed file.</summary>
    public MarkdownFile File { get; }

    /// <summary>The container the entity belongs to (F-LAY-5).</summary>
    public Container Scope { get; }

    /// <summary>The entity's universe.</summary>
    public Universe Universe => Scope.Universe;

    /// <summary>The id, or null if the file has none yet (F-ID-2).</summary>
    public string? Id { get; }

    /// <summary>The slug: the file name without <c>.md</c>, or null if that isn't a valid key (F-KEY-4).</summary>
    public string? Slug { get; }

    /// <summary>The name as written (F-ENT-3, F-YAML-5).</summary>
    public string Name { get; }

    /// <summary>The aliases as written, in order (F-ENT-4).</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>The type key as written (F-ENT-2).</summary>
    public string Type { get; }

    /// <summary>
    /// The combined definition of the entity's type (types.md F-TYPE-8), or
    /// null if the type isn't enabled in the den: an unknown type, so the
    /// entity is untyped (F-ENT-2).
    /// </summary>
    public TypeDefinition? Definition { get; internal init; }

    /// <summary>The entity's status, <c>draft</c> if it has none (F-ENT-5).</summary>
    public FactStatus Status { get; }

    /// <summary>The entity's <c>from</c>, or null if it has none (F-ENT-6).</summary>
    public StoryPosition? From { get; }

    /// <summary>Where <see cref="From"/> resolves, or null if there's no <c>from</c>.</summary>
    public PositionResolution? FromResolution { get; }

    /// <summary>
    /// Where the entity enters canon (F-ENT-6): its <c>from</c>, or without
    /// one the start of its book, its series, or the story. Null if its
    /// <c>from</c> doesn't resolve (F-POS-4).
    /// </summary>
    public StoryPoint? EntersCanon => From is null ? Scope.Start : FromResolution?.Resolved?.Point;

    /// <summary>Describes <see cref="EntersCanon"/> for messages: the <c>from</c> as written, or the start it defaults to.</summary>
    public string EntersCanonDescription => From?.ToString() ?? Scope.StartDescription;

    /// <summary>
    /// True if another entity or container file has the same id (F-ID-3).
    /// The entity is still listed, but links don't resolve to it.
    /// </summary>
    public bool HasDuplicateId { get; internal set; }

    /// <summary>What the index stores for a link to this entity: its id, or its path while it has none (F-LINK-10).</summary>
    public string Key => Id ?? Path;

    internal string? NormalizedSlug { get; }

    internal string NormalizedName { get; }

    internal IReadOnlyList<string> NormalizedAliases => _normalizedAliases;

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Path})";
}
