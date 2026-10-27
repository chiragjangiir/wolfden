using System.Text.Json;
using Wolfden.Core.Links;
using Wolfden.Index;

namespace Wolfden.Api;

/// <summary>
/// An entity's page (#24): what <c>get_entity</c> gives, with its links
/// resolved, the container chain of its scope, where it enters canon, and
/// its backlinks, all from the index (links are indexed resolved, format.md
/// §10). As of a position, the page is the canon-as-of view
/// (<see cref="DenReader.GetEntity(DenIndex, string, string?, string?)"/>),
/// and nothing on it gives away what the reader meets later: a link to an
/// entity met later doesn't say what it reaches, and only links in what the
/// reader has met count as backlinks.
/// </summary>
internal static class EntityPages
{
    private static readonly string[] InCanon = ["draft", "canon"];

    /// <summary>The page of <paramref name="entity"/>, named as <c>get_entity</c> names it, in the full view or as of <paramref name="asOf"/>.</summary>
    public static EntityPageResult Get(DenReader reader, string? entity, string? universe, string? asOf)
    {
        DenReader.CheckEntityName(entity);
        return reader.WithIndex(index =>
        {
            var found = reader.GetEntity(index, entity, universe, asOf, out var canonView);
            if (found.Entity is not { } view)
            {
                return new EntityPageResult(ReadEndpoints.ResultVersion, found.View, found.Position, found.InView, found.Note, null);
            }

            var db = index.Context;
            var record = db.Entities.Single(e => e.Path == view.Path);
            var containers = db.Containers.Where(c => c.UniverseId == record.UniverseId).ToList().ToDictionary(c => c.Id);
            var canon = canonView?.Entities.ToDictionary(e => e.Path, StringComparer.Ordinal);

            // The entity's own links, and every entity they reach or match.
            var links = db.Links.Where(l => l.SourceEntityId == record.Id).ToList().OrderBy(l => l.Line).ThenBy(l => l.Column).ToList();
            var keys = links.Select(l => l.TargetKey).OfType<string>().Concat(links.SelectMany(l => Matches(l.MatchesJson))).Distinct().ToList();
            var targets = db.Entities.Where(e => e.UniverseId == record.UniverseId && keys.Contains(e.Key)).ToList()
                .GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Path, StringComparer.Ordinal).First());
            var sectionLines = db.Sections.Where(s => s.EntityId == record.Id).Select(s => new { s.Id, s.Line }).ToList().ToDictionary(s => s.Id, s => s.Line);
            var entryLines = db.FieldValues.Where(v => v.EntityId == record.Id).Select(v => new { v.Field, v.Line }).ToList()
                .ToLookup(v => v.Field, v => v.Line);

            LinkView Link(LinkRecord l)
            {
                var target = l.TargetKey is { } key ? targets.GetValueOrDefault(key) : null;
                bool? inView = target is not null && canon is not null ? canon.ContainsKey(target.Path) : null;
                var reached = inView == false ? null : target;
                var all = Matches(l.MatchesJson).ToList();
                var matches = l.MatchesJson is null
                    ? null
                    : all.Select(k => targets.GetValueOrDefault(k)).OfType<EntityRecord>()
                        .Where(e => canon is null || canon.ContainsKey(e.Path)).Select(Ref).ToList();
                return new LinkView(
                    l.Text, l.Target, l.Heading, l.Label, l.Line, l.Column, l.Outcome, reached is null ? null : Ref(reached),
                    reached is null ? null : l.TargetSection, matches, Problem(l, view.Universe, canon is not null, reader.PinnedUniverse is not null, matches, all.Count), inView);
            }

            var bySection = links.Where(l => l.SourceSectionId is not null).ToLookup(l => sectionLines.GetValueOrDefault(l.SourceSectionId!.Value));
            var byEntry = links.Where(l => l.Field is not null && l.SourceSectionId is null)
                .ToLookup(l => (l.Field!, Line: Owner(entryLines[l.Field!], l.Line)));
            var page = new EntityPage(
                view.Key, view.Id, view.Name, view.Type, view.Status, view.Universe, view.ScopeKind, view.ScopeKey, view.Path, view.From,
                view.Aliases, view.Lead, view.Pinned,
                (view.Fields ?? []).Select(f => new PageField(
                    f.Name, f.Value, f.Kind, f.Status, f.Pinned, f.From, f.Until, f.Line, f.EntryIndex, f.Live,
                    f.Kind == "default" ? [] : byEntry[(f.Name, f.Line)].Select(Link).ToList())).ToList(),
                (view.Sections ?? []).Select(s => new PageSection(
                    s.Name, s.Level, s.HeadingPath, s.Status, s.Pinned, s.From, s.Until, s.Line, s.Body, s.Bookkeeping,
                    bySection[s.Line].Select(Link).ToList())).ToList(),
                view.Hash,
                Chain(containers, record.ScopeId),
                Entry(record, containers),
                links.Where(l => l.Site == "entity_lead").Select(Link).ToList(),
                Backlinks(index, record, containers, canon, asOf is null ? null : index.PointOf(view.Universe, asOf)),
                Frozen(record, containers));
            return new EntityPageResult(ReadEndpoints.ResultVersion, found.View, found.Position, found.InView, found.Note, page);
        });
    }

    /// <summary>
    /// Why a link doesn't resolve, as the page may say it. The index stores
    /// the message <c>wolfden check</c> gives, which names every match, and
    /// for a broken link may name an entity in another universe (F-LINK-8)
    /// or a file. As of a position (<paramref name="asOf"/>), the message is
    /// made again from what the reader has met: an ambiguous link names only
    /// the matches met by then, and says "and others not met yet" without
    /// naming them, and a broken one says only that it doesn't resolve. In a
    /// server pinned to one universe (<paramref name="pinned"/>), a broken
    /// link's message doesn't name another universe's entity.
    /// </summary>
    private static string? Problem(LinkRecord l, string universe, bool asOf, bool pinned, IReadOnlyList<EntityRef>? met, int matches)
    {
        if (l.Problem is not { } problem)
        {
            return null;
        }

        if (asOf)
        {
            return l.Outcome switch
            {
                "resolved" => null,
                "ambiguous" when met is { Count: > 0 } =>
                    $"'{l.Target}' is ambiguous: it matches {met.Count} {(met.Count == 1 ? "entity" : "entities")} the reader has met by now " +
                    $"({string.Join("; ", met.Select(m => $"'{m.Name}'"))}){(matches > met.Count ? ", and others not met yet" : "")}. Link by slug or id to pick one.",
                "ambiguous" => $"'{l.Target}' is ambiguous: it matches entities the reader hasn't met yet. Link by slug or id to pick one.",
                _ => $"'{l.Target}' doesn't reach an entity. Run 'wolfden check' to see why.",
            };
        }

        // LinkResolver's F-LINK-8 hint that names another universe's entity
        // is the only message that ends so. The message is made again without
        // it, rather than cut, so a name that holds the hint's own words
        // can't keep any of it.
        return pinned && l.Outcome == "broken" && problem.EndsWith(LinkResolver.CrossUniverseHintEnd, StringComparison.Ordinal)
            ? LinkResolver.BrokenMessageStart(universe, l.Target)
            : problem;
    }

    /// <summary>
    /// The links that reach <paramref name="entity"/>. As of a position
    /// (<paramref name="canon"/> and <paramref name="point"/> given), only
    /// those in what the reader has met: in the lead, a section, or a field
    /// value of an entity in the view that the view shows, or in a container
    /// file or outline at or before the position, in canon text.
    /// </summary>
    private static List<BacklinkView> Backlinks(
        DenIndex index, EntityRecord entity, Dictionary<int, ContainerRecord> containers, Dictionary<string, CanonEntity>? canon, long? point)
    {
        var db = index.Context;
        var links = index.Backlinks(entity.Key).Where(l => l.UniverseId == entity.UniverseId).ToList();
        var sourceIds = links.Select(l => l.SourceEntityId).OfType<int>().Distinct().ToList();
        var sources = db.Entities.Where(e => sourceIds.Contains(e.Id)).ToList().ToDictionary(e => e.Id);
        var sectionIds = links.Select(l => l.SourceSectionId).OfType<int>().Distinct().ToList();
        var sections = db.Sections.Where(s => sectionIds.Contains(s.Id)).Select(s => new { s.Id, s.Name, s.Line }).ToList().ToDictionary(s => s.Id);
        var chapterIds = links.Select(l => l.ChapterId).OfType<int>().Distinct().ToList();
        var chapters = db.Chapters.Where(c => chapterIds.Contains(c.Id)).ToList().ToDictionary(c => c.Id);
        var entryLines = db.FieldValues.Where(v => sourceIds.Contains(v.EntityId)).Select(v => new { v.EntityId, v.Field, v.Line }).ToList()
            .ToLookup(v => (v.EntityId, v.Field), v => v.Line);

        bool Met(LinkRecord l)
        {
            if (canon is null)
            {
                return true;
            }

            if (l.SourceEntityId is not { } sourceId)
            {
                return l.PositionOrdinal is { } at && at <= point && !l.IsBookkeeping && (l.Status is null || InCanon.Contains(l.Status));
            }

            if (!canon.TryGetValue(sources[sourceId].Path, out var seen))
            {
                return false;
            }

            return l.Site switch
            {
                "entity_lead" => true,
                "entity_section" => l.SourceSectionId is { } s && seen.Sections.Any(x => x.Line == sections[s].Line),
                _ when l.Field is { } field => seen.Fields.Any(f => f.Name == field && !f.IsDefault && f.Line == Owner(entryLines[(sourceId, field)], l.Line)),
                _ => false,
            };
        }

        return links.Where(Met)
            .Select(l => new BacklinkView(
                l.SourceEntityId is { } id ? Ref(sources[id]) : null,
                l.SourceEntityId is null ? Container(containers[l.ScopeId]) : null,
                l.Site, l.Path, l.Line, l.Column, l.Text, l.Field,
                l.SourceSectionId is { } s ? sections[s].Name : null,
                l.ChapterId is { } c ? $"{containers[chapters[c].BookId].Key}:{chapters[c].Key}" : null,
                l.OutlineRow, l.Status, l.IsBookkeeping))
            .ToList();
    }

    /// <summary>
    /// Why the entity can't be edited: it's filed in a final book (format.md
    /// F-BOOK-1), as the write tools refuse it (<c>frozen</c>). Null if it can be.
    /// </summary>
    private static string? Frozen(EntityRecord entity, Dictionary<int, ContainerRecord> containers) =>
        containers[entity.ScopeId] is { Kind: "book", BookStatus: "final" } book
            ? $"{entity.Path} is in book '{book.Key}', which is final (frozen), so it can't be edited until the book is unfrozen (F-BOOK-1)."
            : null;

    /// <summary>The container chain of a scope: the universe, then any series, then any book.</summary>
    private static List<ContainerRef> Chain(Dictionary<int, ContainerRecord> containers, int scopeId)
    {
        var chain = new List<ContainerRef>();
        for (ContainerRecord? c = containers[scopeId]; c is not null; c = c.ParentId is { } parent ? containers[parent] : null)
        {
            chain.Insert(0, Container(c));
        }

        return chain;
    }

    /// <summary>Where the entity enters canon: its own <c>from</c>, or the start of its scope (format.md F-ENT-6, F-ORD-3).</summary>
    private static EntryView Entry(EntityRecord entity, Dictionary<int, ContainerRecord> containers)
    {
        if (entity.FromText is { } from)
        {
            return new EntryView(from, false, $"'{from}', its own from");
        }

        var scope = containers[entity.ScopeId];
        switch (scope.Kind)
        {
            case "book":
                return new EntryView(scope.Key, true, $"the start of book '{scope.Key}', where it's filed");
            case "series":
                var first = containers.Values.Where(c => c.Kind == "book" && c.ParentId == scope.Id).OrderBy(c => c.StoryOrder).FirstOrDefault();
                return first is null
                    ? new EntryView(null, true, $"the start of series '{scope.Key}', where it's filed, which has no books yet")
                    : new EntryView(first.Key, true, $"the start of series '{scope.Key}', where it's filed: the start of its first book, '{first.Key}'");
            default:
                return new EntryView(null, true, $"the start of the story, since it's filed in universe '{scope.Key}'");
        }
    }

    /// <summary>
    /// The line of the field value or entry that holds a link at
    /// <paramref name="line"/>: the last of the field's values that starts at
    /// or before it. A field's entries are written one after another, so each
    /// link is in the nearest entry above it.
    /// </summary>
    private static int Owner(IEnumerable<int> entryLines, int line) => entryLines.Where(l => l <= line).DefaultIfEmpty(0).Max();

    private static IEnumerable<string> Matches(string? json) => json is null ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static EntityRef Ref(EntityRecord e) => new(e.Key, e.Ulid, e.Name, e.Type);

    private static ContainerRef Container(ContainerRecord c) => new(c.Kind, c.Key, c.Name, c.Ulid, c.Path);
}
