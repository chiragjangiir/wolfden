using Wolfden.Core.Entities;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Links;

/// <summary>Which tier of F-LINK-6 a link matched in.</summary>
public enum LinkMatchKind
{
    /// <summary>The target is the entity's id.</summary>
    Id,

    /// <summary>The target matches the entity's slug.</summary>
    Slug,

    /// <summary>The target matches the entity's name.</summary>
    Name,

    /// <summary>The target matches one of the entity's aliases.</summary>
    Alias,
}

/// <summary>What became of a link (format.md §10.2).</summary>
public enum LinkOutcome
{
    /// <summary>The link resolves to one entity.</summary>
    Resolved,

    /// <summary>Several entities match and none is nearest in the scope chain (F-LINK-7).</summary>
    Ambiguous,

    /// <summary>No entity matches (F-LINK-8).</summary>
    Broken,
}

/// <summary>The result of resolving a link target from a scope.</summary>
public sealed class TargetResolution
{
    internal TargetResolution(LinkOutcome outcome, CatalogEntity? entity, LinkMatchKind? matchedBy, IReadOnlyList<CatalogEntity> matches, string? problem)
    {
        Outcome = outcome;
        Entity = entity;
        MatchedBy = matchedBy;
        Matches = matches;
        Problem = problem;
    }

    /// <summary>Resolved, ambiguous, or broken.</summary>
    public LinkOutcome Outcome { get; }

    /// <summary>The entity the link resolves to, or null.</summary>
    public CatalogEntity? Entity { get; }

    /// <summary>The tier that matched, or null for a broken link.</summary>
    public LinkMatchKind? MatchedBy { get; }

    /// <summary>Every match in the winning tier, by path. For an ambiguous link, the candidates.</summary>
    public IReadOnlyList<CatalogEntity> Matches { get; }

    /// <summary>Why the link doesn't resolve, for the F-LINK-7 or F-LINK-8 warning, or null if it resolves.</summary>
    public string? Problem { get; }
}

/// <summary>
/// Resolves link targets to entities (format.md §10.2): by id, then slug,
/// then name, then alias, among the entities of the linking file's
/// universe, with the nearest scope in the scope chain breaking ties.
/// </summary>
public sealed class LinkResolver
{
    private readonly EntityCatalog _catalog;
    private readonly Dictionary<Universe, Candidates> _targets = [];
    private readonly Dictionary<Universe, Candidates> _duplicates = [];

    /// <summary>A resolver over the entities of <paramref name="catalog"/>.</summary>
    public LinkResolver(EntityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        foreach (var universe in catalog.Structure.Universes)
        {
            var entities = catalog.InUniverse(universe).ToList();
            // F-ID-3: entities with a duplicate id are left out of resolution.
            _targets[universe] = new Candidates(entities.Where(e => !e.HasDuplicateId));
            _duplicates[universe] = new Candidates(entities.Where(e => e.HasDuplicateId));
        }
    }

    /// <summary>
    /// Resolves <paramref name="target"/>, as written in a link, from a file
    /// whose scope is <paramref name="scope"/> (F-LINK-5 to F-LINK-8).
    /// </summary>
    public TargetResolution Resolve(Container scope, string target)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(target);
        target = target.Trim();
        var universe = scope.Universe;
        var (kind, matches) = _targets.TryGetValue(universe, out var candidates) ? candidates.Match(target) : (null, []);
        if (kind is not { } matchedBy)
        {
            return new TargetResolution(LinkOutcome.Broken, null, null, [], BrokenMessage(universe, target));
        }

        if (matches.Count == 1)
        {
            return new TargetResolution(LinkOutcome.Resolved, matches[0], matchedBy, matches, null);
        }

        // F-LINK-7: the match in the nearest scope of the scope chain wins.
        var chain = scope.ScopeChain;
        var nearest = matches
            .Select(m => (Entity: m, Distance: IndexOf(chain, m.Scope)))
            .Where(m => m.Distance >= 0)
            .GroupBy(m => m.Distance)
            .OrderBy(g => g.Key)
            .FirstOrDefault()?
            .Select(m => m.Entity)
            .ToList();

        if (nearest is [var winner])
        {
            return new TargetResolution(LinkOutcome.Resolved, winner, matchedBy, matches, null);
        }

        var listed = string.Join("; ", matches.Select(m => $"'{m.Name}' ({m.Path})"));
        var why = nearest is null
            ? "and none of them is in this file's scope chain"
            : $"and {nearest.Count} of them are in the nearest scope, {Describe(nearest[0].Scope)}";
        return new TargetResolution(LinkOutcome.Ambiguous, null, matchedBy, matches,
            $"'{target}' is ambiguous: it matches {matches.Count} entities by {matchedBy.ToString().ToLowerInvariant()} ({listed}), {why}. Link by slug or id to pick one.");
    }

    private static int IndexOf(IReadOnlyList<Container> chain, Container scope)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            if (chain[i] == scope)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Describe(Container c) => $"{c.Kind.ToString().ToLowerInvariant()} '{c.Key}'";

    /// <summary>The F-LINK-8 message, with a hint when the target matches something links can't reach.</summary>
    private string BrokenMessage(Universe universe, string target)
    {
        var message = $"'{target}' matches no entity in universe '{universe.Key}'.";
        if (_duplicates.TryGetValue(universe, out var duplicates) && duplicates.Match(target) is (not null, var dup))
        {
            return $"{message} It would match '{dup[0].Name}' ({dup[0].Path}), but that entity's id is used by more than one file (F-ID-3), so links don't reach it until the id is unique.";
        }

        foreach (var other in _catalog.Structure.Universes.Where(u => u != universe))
        {
            if (_targets[other].Match(target) is (not null, var elsewhere))
            {
                return $"{message} It matches only '{elsewhere[0].Name}' in universe '{other.Key}'; links don't cross universes.";
            }
        }

        var normalized = LinkText.Normalize(target);
        var container = universe.Series.Cast<Container>().Concat(universe.Books)
            .FirstOrDefault(c => LinkText.Normalize(c.Key) == normalized || LinkText.Normalize(c.Name) == normalized);
        if (container is not null)
        {
            return $"{message} It matches {Describe(container)} ('{container.Name}'), but containers aren't link targets yet.";
        }

        return message;
    }

    /// <summary>The entities links can match in one universe, by tier.</summary>
    private sealed class Candidates
    {
        private readonly Dictionary<string, List<CatalogEntity>> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CatalogEntity>> _bySlug = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CatalogEntity>> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CatalogEntity>> _byAlias = new(StringComparer.Ordinal);

        public Candidates(IEnumerable<CatalogEntity> entities)
        {
            foreach (var entity in entities)
            {
                if (entity.Id is { } id)
                {
                    Add(_byId, id, entity);
                }

                if (entity.NormalizedSlug is { } slug)
                {
                    Add(_bySlug, slug, entity);
                }

                Add(_byName, entity.NormalizedName, entity);
                foreach (var alias in entity.NormalizedAliases)
                {
                    Add(_byAlias, alias, entity);
                }
            }
        }

        /// <summary>The first tier with any match (F-LINK-6), and its matches, or null if none matches.</summary>
        public (LinkMatchKind? Kind, IReadOnlyList<CatalogEntity> Matches) Match(string target)
        {
            if (Keys.IsId(target) && _byId.TryGetValue(target, out var byId))
            {
                return (LinkMatchKind.Id, byId);
            }

            var normalized = LinkText.Normalize(target);
            if (_bySlug.TryGetValue(normalized, out var bySlug))
            {
                return (LinkMatchKind.Slug, bySlug);
            }

            if (_byName.TryGetValue(normalized, out var byName))
            {
                return (LinkMatchKind.Name, byName);
            }

            return _byAlias.TryGetValue(normalized, out var byAlias) ? (LinkMatchKind.Alias, byAlias) : (null, []);
        }

        private static void Add(Dictionary<string, List<CatalogEntity>> index, string key, CatalogEntity entity)
        {
            if (!index.TryGetValue(key, out var list))
            {
                index[key] = list = [];
            }

            // One entity counts once, even if two of its aliases normalize alike.
            if (!list.Contains(entity))
            {
                list.Add(entity);
            }
        }
    }
}
