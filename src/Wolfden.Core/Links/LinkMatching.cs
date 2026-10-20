using Wolfden.Core.Structure;

namespace Wolfden.Core.Links;

/// <summary>
/// The tiers of F-LINK-6, in one place: a target matches by id, then by
/// slug, then by name, then by alias, each compared under F-LINK-4, and
/// the first tier with any match wins. <see cref="LinkResolver"/> uses it
/// over the entity catalog, and the CLI over the index, so "matched as a
/// link is" means the same everywhere.
/// </summary>
public static class LinkMatching
{
    /// <summary>
    /// The first tier in which <paramref name="target"/> matches anything,
    /// and its matches, or a null kind and no matches. Each lookup gets the
    /// key to look up: the target itself for ids (only when it is an id,
    /// F-ID-1), and the target normalized (<see cref="LinkText.Normalize"/>)
    /// for slugs, names, and aliases, so the lookups compare against
    /// normalized values.
    /// </summary>
    public static (LinkMatchKind? Kind, IReadOnlyList<T> Matches) FirstTier<T>(
        string target,
        Func<string, IReadOnlyList<T>> byId,
        Func<string, IReadOnlyList<T>> bySlug,
        Func<string, IReadOnlyList<T>> byName,
        Func<string, IReadOnlyList<T>> byAlias)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(byId);
        ArgumentNullException.ThrowIfNull(bySlug);
        ArgumentNullException.ThrowIfNull(byName);
        ArgumentNullException.ThrowIfNull(byAlias);
        target = target.Trim();
        if (Keys.IsId(target) && byId(target) is { Count: > 0 } ids)
        {
            return (LinkMatchKind.Id, ids);
        }

        var normalized = LinkText.Normalize(target);
        if (bySlug(normalized) is { Count: > 0 } slugs)
        {
            return (LinkMatchKind.Slug, slugs);
        }

        if (byName(normalized) is { Count: > 0 } names)
        {
            return (LinkMatchKind.Name, names);
        }

        return byAlias(normalized) is { Count: > 0 } aliases ? (LinkMatchKind.Alias, aliases) : (null, []);
    }
}
