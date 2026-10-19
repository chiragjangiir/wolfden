using System.Text.Json;
using Wolfden.Core.Positions;

namespace Wolfden.Index;

/// <summary>
/// Canon-as-of views over the index (format.md §9.4, design.md §7). Ranges
/// are asked through <see cref="IndexQueries"/>, so "in effect at p" means
/// the same here as everywhere else, the end of the story included.
/// </summary>
internal static class CanonQuery
{
    private const string Draft = "draft";
    private const string Canon = "canon";

    /// <summary>F-VIEW-1: the canon-as-of view of <paramref name="scope"/> at <paramref name="position"/>, or at the end of the story.</summary>
    public static CanonView Run(IndexDbContext db, CanonScope scope, string? position)
    {
        var universe = db.Containers.Where(c => c.Kind == "universe" && c.Key == scope.UniverseKey).ToList() switch
        {
            [var only] => only,
            _ => throw new CanonViewException($"The den has no universe '{scope.UniverseKey}'."),
        };
        var containers = db.Containers.Where(c => c.UniverseId == universe.Id).ToDictionary(c => c.Id);
        var covered = CoveredScopes(Container(containers.Values, universe, scope), containers);
        var point = Point(db, containers.Values, universe, position);

        // F-ENT-5, F-ENT-6: in-canon entities of the scope chain that have entered canon by p.
        var entityQuery = db.Entities
            .Where(e => e.UniverseId == universe.Id && covered.Contains(e.ScopeId) && (e.Status == Draft || e.Status == Canon))
            .EnteredBy(point);
        var entityIds = entityQuery.Select(e => e.Id);
        var entities = entityQuery.ToList();

        // F-TMP-4: live entries only. A plain value is live when its entity is in canon.
        var values = db.FieldValues
            .Where(v => v.UniverseId == universe.Id && entityIds.Contains(v.EntityId) && v.IsLive)
            .InEffectAt(point)
            .ToList()
            .ToLookup(v => v.EntityId);

        // Where each entity writes each field, in effect or not, so a default
        // shown for an unset field keeps that field's place (F-VIEW-3).
        var written = db.FieldValues
            .Where(v => v.UniverseId == universe.Id && entityIds.Contains(v.EntityId))
            .Select(v => new { v.EntityId, v.Field, v.FieldOrder })
            .Distinct()
            .ToList()
            .ToLookup(v => v.EntityId);

        // F-SEC-8, F-SEC-9, F-SEC-12: in-canon, non-bookkeeping sections in effect at p.
        var sections = db.Sections
            .Where(s => s.UniverseId == universe.Id && entityIds.Contains(s.EntityId)
                && (s.Status == Draft || s.Status == Canon) && !s.IsBookkeeping)
            .InEffectAt(point)
            .ToList()
            .ToLookup(s => s.EntityId);

        var types = entities.Select(e => e.Type).Distinct().ToList();
        var defaults = db.FieldDefaults.Where(d => types.Contains(d.TypeKey)).ToList().ToLookup(d => d.TypeKey);

        var view = entities
            .OrderBy(e => ScopeRank(containers[e.ScopeId]))
            .ThenBy(e => e.Type, StringComparer.Ordinal)
            .ThenBy(e => e.NameNormalized, StringComparer.Ordinal)
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => new CanonEntity(
                e.Key, e.Ulid, e.Name, e.Type, e.Status, containers[e.ScopeId].Kind, containers[e.ScopeId].Key, e.Path,
                e.FromText, e.Title, e.Lead, e.LeadLine,
                Fields(e, values[e.Id], written[e.Id].Select(w => (w.Field, w.FieldOrder)), defaults[e.Type]),
                sections[e.Id].OrderBy(s => s.Index).Select(s => Section(e, s)).ToList()))
            .ToList();
        return new CanonView(scope, position, view);
    }

    /// <summary>
    /// F-TMP-5: of the live entries in effect, the one with the later
    /// <c>from</c>, then the one later in the file. A null value means the
    /// field is unset there (F-TMP-2), and the type's default shows instead
    /// (types.md F-FT-15).
    /// </summary>
    private static List<CanonFieldValue> Fields(
        EntityRecord entity, IEnumerable<FieldValueRecord> inEffect, IEnumerable<(string Field, int Order)> written, IEnumerable<FieldDefaultRecord> defaults)
    {
        var chosen = inEffect
            .GroupBy(v => v.FieldOrder)
            .Select(g => g.OrderByDescending(v => v.FromOrdinal).ThenByDescending(v => v.EntryIndex).First())
            .Where(v => v.ValueJson != "null")
            .ToDictionary(v => v.Field, StringComparer.Ordinal);
        var order = written.ToDictionary(w => w.Field, w => w.Order, StringComparer.Ordinal);

        var result = new List<(int Written, int TypeOrder, CanonFieldValue Value)>();
        foreach (var v in chosen.Values)
        {
            result.Add((v.FieldOrder, 0, new CanonFieldValue(
                v.Field, v.ValueJson, v.Kind, v.EntryIndex, v.Status, v.Pinned, v.FromText, v.UntilText, entity.Path, v.Line)));
        }

        foreach (var d in defaults.Where(d => !chosen.ContainsKey(d.Field)))
        {
            result.Add((order.TryGetValue(d.Field, out var at) ? at : int.MaxValue, d.FieldOrder, new CanonFieldValue(
                d.Field, d.DefaultJson, "default", null, null, false, null, null, d.Path, d.Line)));
        }

        return result.OrderBy(r => r.Written).ThenBy(r => r.TypeOrder).Select(r => r.Value).ToList();
    }

    private static CanonSection Section(EntityRecord entity, SectionRecord s) => new(
        s.Name, s.Level, JsonSerializer.Deserialize<string[]>(s.HeadingPathJson) ?? [], s.Body, s.Status, s.Pinned,
        s.FromText, s.UntilText, entity.Path, s.Line);

    /// <summary>
    /// F-VIEW-3: the universe first, then series and books in story order. A
    /// series starts where its first book does, so it comes just before it.
    /// </summary>
    private static (long Start, int Kind) ScopeRank(ContainerRecord c) => c.Kind switch
    {
        "universe" => (long.MinValue, 0),
        "series" => (c.StartOrdinal, 1),
        _ => (c.StartOrdinal, 2),
    };

    private static ContainerRecord Container(IEnumerable<ContainerRecord> containers, ContainerRecord universe, CanonScope scope)
    {
        if (scope.Kind == CanonScopeKind.Universe)
        {
            return universe;
        }

        var kind = scope.Kind == CanonScopeKind.Series ? "series" : "book";
        return containers.Where(c => c.Kind == kind && c.Key == scope.Key).ToList() switch
        {
            [var only] => only,
            [] => throw new CanonViewException($"Universe '{universe.Key}' has no {kind} '{scope.Key}'."),
            _ => throw new CanonViewException($"More than one {kind} of universe '{universe.Key}' has the key '{scope.Key}' (F-KEY-5)."),
        };
    }

    /// <summary>
    /// The scopes whose entities a view of <paramref name="container"/>
    /// covers. F-VIEW-1 read literally: the container and the ones that
    /// enclose it (design.md §2), not the ones it encloses, so a universe
    /// view leaves out series- and book-scope entities. This is the one
    /// place that decides it, so another reading changes only this method.
    /// </summary>
    private static List<int> CoveredScopes(ContainerRecord container, Dictionary<int, ContainerRecord> containers)
    {
        var chain = new List<int>();
        for (ContainerRecord? c = container; c is not null; c = c.ParentId is { } parent ? containers[parent] : null)
        {
            chain.Add(c.Id);
        }

        return chain;
    }

    /// <summary>
    /// The ordinal of <paramref name="position"/> in the universe's story
    /// order (F-POS-4, F-POS-7), or the end of the story for null (F-VIEW-1).
    /// </summary>
    private static long Point(IndexDbContext db, IEnumerable<ContainerRecord> containers, ContainerRecord universe, string? position)
    {
        if (position is null)
        {
            return IndexQueries.EndOfStory;
        }

        if (!StoryPosition.TryParse(position, out var parsed, out var error))
        {
            throw new CanonViewException(error.Message);
        }

        var book = containers.Where(c => c.Kind == "book" && c.Key == parsed.BookKey).ToList() switch
        {
            [var only] => only,
            [] => throw new CanonViewException($"Universe '{universe.Key}' has no book '{parsed.BookKey}'."),
            _ => throw new CanonViewException(
                $"More than one book of universe '{universe.Key}' has the key '{parsed.BookKey}' (F-KEY-5), so '{position}' doesn't resolve."),
        };
        if (parsed.ChapterKey is not { } chapterKey)
        {
            return book.StartOrdinal;
        }

        var chapter = db.Chapters.Where(c => c.BookId == book.Id && c.Key == chapterKey).Select(c => (long?)c.Ordinal).SingleOrDefault();
        return chapter ?? throw new CanonViewException($"Book '{book.Key}' has no chapter '{chapterKey}'.");
    }
}
