using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wolfden.Core.Checks;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;
using Wolfden.Index;

namespace Wolfden.Mcp;

/// <summary>
/// The read tools of the MCP server (design.md §8, #20), over one den. Each
/// call brings the index up to date with the files first
/// (<see cref="IndexBuilder.EnsureUpToDate(string, TimeSpan)"/>), so an
/// answer never lags behind an edit, and then asks the index; <c>check</c>
/// reads the files, as <c>wolfden check</c> does. Nothing here writes to the
/// den's own files.
/// </summary>
/// <remarks>
/// <para>
/// Without a position, a tool gives the author's full view: every entity
/// and fact, in or out of canon, each marked with its status, pin, and
/// range. A position is opt-in, and gives the canon-as-of view
/// (format.md F-VIEW-1), which never holds anything the reader meets after
/// it. Pinned canon (F-VIEW-2) comes ahead of the rest.
/// </para>
/// <para>
/// Every failure is a <see cref="DenToolException"/> with a code from
/// <see cref="ToolErrorCodes"/>.
/// </para>
/// </remarks>
public sealed class DenTools
{
    private static readonly string[] Statuses = ["idea", "undecided", "candidate", "draft", "canon", "retconned"];

    /// <summary>Tools for the den at <paramref name="denRoot"/>.</summary>
    public DenTools(string denRoot)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        Root = Path.GetFullPath(denRoot);
    }

    /// <summary>The den's folder, as a full path.</summary>
    public string Root { get; }

    /// <summary>The longest a call waits for a running watcher to catch up before it rebuilds the index itself.</summary>
    public TimeSpan WatcherWait { get; init; } = IndexBuilder.WatcherWait;

    /// <summary>
    /// Full-text search over names, aliases, leads, and section text, every
    /// status included. Words match whole, in any order, ignoring case and
    /// accents; a word ending in <c>*</c> matches as a prefix.
    /// </summary>
    public SearchResult Search(string query, string? universe = null, string? type = null, bool canonOnly = false, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give some words to search for in 'query'.");
        }

        CheckLimit(limit, 200);
        return WithIndex(index =>
        {
            var db = index.Context;
            var universes = db.Containers.Where(c => c.Kind == "universe").ToDictionary(c => c.Id, c => c.Key);
            int? universeId = universe is null ? null : Universe(db, universe).Id;
            if (type is not null)
            {
                CheckTypes(db, universeId, [type]);
            }

            // Filters apply after the search, so ask for more when there are any.
            var filtered = universe is not null || type is not null || canonOnly;
            var hits = index.Search(query, filtered ? Math.Max(limit * 20, 1000) : limit);
            var paths = hits.Select(h => h.EntityPath).Distinct().ToList();
            var entities = db.Entities.Where(e => paths.Contains(e.Path)).ToList().ToDictionary(e => e.Path, StringComparer.Ordinal);
            var scopes = Scopes(db);
            var views = hits
                .Where(h => entities.ContainsKey(h.EntityPath))
                .Select(h => (Hit: h, Entity: entities[h.EntityPath]))
                .Where(x => universeId is null || x.Entity.UniverseId == universeId)
                .Where(x => type is null || x.Entity.Type == type)
                .Where(x => !canonOnly || x.Hit.IsInCanon)
                .Take(limit)
                .Select(x => new SearchHitView(
                    Summary(x.Entity, universes, scopes), x.Hit.SectionName, x.Hit.SectionLine, x.Hit.Snippet, x.Hit.Status, x.Hit.IsInCanon, x.Hit.InComment))
                .ToList();
            return new SearchResult(ToolJson.Version, query, views.Count, views);
        });
    }

    /// <summary>
    /// One entity, named as a link names it (id, file name, name, or alias)
    /// or by its key or path. Without <paramref name="asOf"/>, the full view:
    /// every field entry and section, in or out of canon. With it, the
    /// entity as the canon-as-of view at that position shows it, or a note
    /// that it isn't in canon there.
    /// </summary>
    public GetEntityResult GetEntity(string entity, string? universe = null, string? asOf = null)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Name the entity in 'entity': its name, an alias, its file name, its id, or its path.");
        }

        return WithIndex(index =>
        {
            var db = index.Context;
            var universes = db.Containers.Where(c => c.Kind == "universe").ToDictionary(c => c.Id, c => c.Key);
            var scopes = Scopes(db);
            var within = universe is null ? universes.Keys.ToList() : [Universe(db, universe).Id];
            var matches = Match(index, within, entity.Trim());
            if (matches.Count == 0)
            {
                throw new DenToolException(
                    ToolErrorCodes.UnknownEntity,
                    $"No entity {(universe is null ? "in the den" : $"in universe '{universe}'")} matches '{entity}' by id, path, file name, name, or alias. " +
                    "Use search or list_entities to find it.");
            }

            if (matches.Select(m => m.UniverseId).Distinct().Count() > 1 || (asOf is null && matches.Count > 1))
            {
                throw Ambiguous(entity, matches, universes, scopes);
            }

            if (asOf is null)
            {
                return new GetEntityResult(ToolJson.Version, "full", null, true, null, FullView(db, matches[0], universes, scopes));
            }

            var universeKey = universes[matches[0].UniverseId];
            var view = CanonView(index, universeKey, asOf, null);
            var paths = matches.Select(m => m.Path).ToHashSet(StringComparer.Ordinal);
            var inView = view.Entities.Where(e => paths.Contains(e.Path)).ToList();
            switch (inView.Count)
            {
                case 0:
                    return new GetEntityResult(
                        ToolJson.Version, "canon_as_of", asOf, false,
                        $"'{entity}' isn't in canon as of {asOf}: the reader hasn't met it by then, or it isn't draft or canon.", null);
                case > 1:
                    // Only the matches the reader has met by then, so the error gives nothing away.
                    throw Ambiguous(entity, matches.Where(m => inView.Any(e => e.Path == m.Path)).ToList(), universes, scopes);
            }

            var e = inView[0];
            var pinned = view with { Entities = inView };
            var pinnedEntity = pinned.Pinned().Entities.SingleOrDefault();
            return new GetEntityResult(
                ToolJson.Version, "canon_as_of", asOf, true, null,
                CanonEntityView(e, universeKey, includeContent: true) with
                {
                    Pinned = new PinnedFacts(
                        pinnedEntity?.Fields.Select(CanonField).ToList() ?? [],
                        pinnedEntity?.Sections.Select(CanonSection).ToList() ?? []),
                });
        });
    }

    /// <summary>
    /// The entities of the den, or of one universe, of every status, as
    /// one-line summaries. <paramref name="series"/> or <paramref name="book"/>
    /// narrows the list as they narrow a canon view (F-VIEW-1): the
    /// universe's entities, the series' or book's, and those of the scopes
    /// inside it or holding it.
    /// </summary>
    public ListEntitiesResult ListEntities(
        string? universe = null, string? type = null, string? status = null, string? series = null, string? book = null, int limit = 200)
    {
        CheckLimit(limit, 1000);
        if (status is not null && !Statuses.Contains(status))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, $"'{status}' isn't a status. Use one of: {string.Join(", ", Statuses)}.");
        }

        var filter = Filter(series, book);
        return WithIndex(index =>
        {
            var db = index.Context;
            var universes = db.Containers.Where(c => c.Kind == "universe").ToDictionary(c => c.Id, c => c.Key);
            var scopes = Scopes(db);

            // A series or book belongs to one universe, so it needs one.
            List<ContainerRecord> chosen = universe is not null || filter is not null
                ? [Universe(db, universe)]
                : db.Containers.Where(c => c.Kind == "universe").ToList().OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
            if (type is not null)
            {
                CheckTypes(db, universe is null && filter is null ? null : chosen[0].Id, [type]);
            }

            var all = new List<EntitySummary>();
            foreach (var u in chosen)
            {
                CheckFilter(db, u, filter);
                all.AddRange(index.Entities(u.Key, filter)
                    .Where(e => type is null || e.Type == type)
                    .Where(e => status is null || e.Status == status)
                    .Select(e => Summary(e, universes, scopes)));
            }

            return new ListEntitiesResult(ToolJson.Version, all.Count, all.Count > limit, all.Take(limit).ToList());
        });
    }

    /// <summary>
    /// The canon-as-of view of a universe at <paramref name="position"/>, or
    /// at the end of the story (all canon so far): pinned canon first
    /// (F-VIEW-2), then every entity the reader has met by then with the
    /// field values and sections in effect (F-VIEW-1).
    /// </summary>
    public CanonAsOfResult CanonAsOf(
        string? universe = null, string? position = null, string? series = null, string? book = null, string? type = null, bool brief = false)
    {
        var filter = Filter(series, book);
        return WithIndex(index =>
        {
            var db = index.Context;
            var u = Universe(db, universe);
            if (type is not null)
            {
                CheckTypes(db, u.Id, [type]);
            }

            var view = CanonView(index, u.Key, position, filter);
            if (type is not null)
            {
                view = view with { Entities = view.Entities.Where(e => e.Type == type).ToList() };
            }

            var pinned = view.Pinned().Entities
                .Select(e => new PinnedEntity(e.Key, e.Name, e.Type, e.Fields.Select(CanonField).ToList(), e.Sections.Select(CanonSection).ToList()))
                .ToList();
            return new CanonAsOfResult(
                ToolJson.Version,
                u.Key,
                position,
                position is null ? "the end of the story: all canon so far" : $"as of {position}",
                filter is null ? null : new ScopeFilter(filter.Kind == CanonFilterKind.Series ? "series" : "book", filter.Key),
                view.Entities.Count,
                pinned,
                view.Entities.Select(e => CanonEntityView(e, u.Key, includeContent: !brief)).ToList());
        });
    }

    /// <summary>
    /// Runs every check on the den's files, as <c>wolfden check</c> does,
    /// and returns the diagnostics, optionally only one severity or the
    /// files under one path.
    /// </summary>
    public CheckResult Check(string? severity = null, string? path = null, int limit = 200)
    {
        CheckLimit(limit, 5000);
        if (severity is not null and not ("error" or "warning"))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, $"'{severity}' isn't a severity. Use 'error' or 'warning'.");
        }

        DenCheck check;
        try
        {
            check = DenCheck.Run(Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DenToolException(ToolErrorCodes.DenUnreadable, $"Couldn't read the den's files: {e.Message}");
        }

        var prefix = path?.Replace('\\', '/').TrimStart('/');
        var matching = check.Diagnostics
            .Where(d => severity is null || Severity(d.Diagnostic.Severity) == severity)
            .Where(d => string.IsNullOrEmpty(prefix) || d.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(d => new DiagnosticView(d.Path, d.Diagnostic.Line, Severity(d.Diagnostic.Severity), d.Diagnostic.RuleId, d.Diagnostic.Message))
            .ToList();
        return new CheckResult(
            ToolJson.Version, check.IsLoaded, check.IsLoaded ? check.Structure.SpoilerChecks : null, check.Catalog.Entities.Count,
            check.ErrorCount, check.WarningCount, matching.Count, matching.Count > limit, matching.Take(limit).ToList());

        static string Severity(DiagnosticSeverity s) => s == DiagnosticSeverity.Error ? "error" : "warning";
    }

    /// <summary>Brings the index up to date, opens it, and runs <paramref name="query"/>, turning failures into tool errors.</summary>
    private T WithIndex<T>(Func<DenIndex, T> query)
    {
        try
        {
            IndexBuilder.EnsureUpToDate(Root, WatcherWait);
            using var index = DenIndex.Open(Root);
            if (!index.Context.Dens.Single().IsLoaded)
            {
                throw new DenToolException(
                    ToolErrorCodes.DenNotLoaded,
                    "den.yaml doesn't load, so nothing in the den can be read. Call the check tool to see why.");
            }

            return query(index);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw new DenToolException(ToolErrorCodes.DenUnreadable, $"Couldn't read or rebuild the den's index: {e.Message}");
        }
    }

    /// <summary>The universe named, or the den's only one.</summary>
    private static ContainerRecord Universe(IndexDbContext db, string? key)
    {
        var universes = db.Containers.Where(c => c.Kind == "universe").ToList().OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        var keys = string.Join(", ", universes.Select(u => u.Key));
        if (key is not null)
        {
            return universes.FirstOrDefault(u => u.Key == key)
                ?? throw new DenToolException(
                    ToolErrorCodes.UnknownUniverse,
                    universes.Count == 0 ? $"The den has no universe '{key}'; it has no universes yet." : $"The den has no universe '{key}'. Its universes: {keys}.");
        }

        return universes.Count switch
        {
            1 => universes[0],
            0 => throw new DenToolException(ToolErrorCodes.NoUniverse, "The den has no universes yet, so there's nothing to read."),
            _ => throw new DenToolException(ToolErrorCodes.UniverseRequired, $"The den has {universes.Count} universes ({keys}); name one in 'universe'."),
        };
    }

    private static CanonFilter? Filter(string? series, string? book) => (series, book) switch
    {
        (not null, not null) => throw new DenToolException(ToolErrorCodes.BadArgument, "Give 'series' or 'book', not both."),
        (not null, null) => CanonFilter.ForSeries(series),
        (null, not null) => CanonFilter.ForBook(book),
        _ => null,
    };

    /// <summary>Checks that the filter names one series or book of the universe, so any other view failure is the position's.</summary>
    private static void CheckFilter(IndexDbContext db, ContainerRecord universe, CanonFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        var kind = filter.Kind == CanonFilterKind.Series ? "series" : "book";
        var found = db.Containers.Where(c => c.UniverseId == universe.Id && c.Kind == kind).Select(c => c.Key).ToList();
        var count = found.Count(k => k == filter.Key);
        if (count == 1)
        {
            return;
        }

        throw new DenToolException(
            ToolErrorCodes.UnknownScope,
            count == 0
                ? $"Universe '{universe.Key}' has no {kind} '{filter.Key}'." +
                  (found.Count == 0 ? "" : $" Its {(kind == "series" ? "series" : "books")}: {string.Join(", ", found.Order(StringComparer.Ordinal))}.")
                : $"More than one {kind} of universe '{universe.Key}' has the key '{filter.Key}' (F-KEY-5); run the check tool.");
    }

    /// <summary>The canon view, with a bad filter or position as a tool error.</summary>
    private static CanonView CanonView(DenIndex index, string universeKey, string? position, CanonFilter? filter)
    {
        var db = index.Context;
        var universe = db.Containers.Single(c => c.Kind == "universe" && c.Key == universeKey);
        CheckFilter(db, universe, filter);
        try
        {
            return index.CanonAsOf(universeKey, position, filter);
        }
        catch (CanonViewException e)
        {
            var books = db.Containers.Where(c => c.UniverseId == universe.Id && c.Kind == "book").ToList()
                .OrderBy(c => c.StartOrdinal).Select(c => c.Key).ToList();
            throw new DenToolException(
                ToolErrorCodes.BadPosition,
                $"{e.Message} A position is 'book:chapter', such as '{books.FirstOrDefault() ?? "book"}:1', or a bare book key for the start of that book." +
                (books.Count == 0 ? "" : $" Books of universe '{universeKey}' in story order: {string.Join(", ", books)}."));
        }
    }

    /// <summary>Checks that each type is defined by an enabled pack or used by an entity (of the universe, if one is given).</summary>
    private void CheckTypes(IndexDbContext db, int? universeId, IEnumerable<string> types)
    {
        var used = db.Entities.Where(e => universeId == null || e.UniverseId == universeId).Select(e => e.Type).Distinct().ToList();
        HashSet<string>? known = null;
        foreach (var type in types)
        {
            if (used.Contains(type))
            {
                continue;
            }

            known ??= DenStructure.Load(Root).Types.Types.Select(t => t.Key).Concat(used).ToHashSet(StringComparer.Ordinal);
            if (!known.Contains(type))
            {
                throw new DenToolException(
                    ToolErrorCodes.UnknownType,
                    $"There's no type '{type}': no enabled pack defines it, and no entity uses it. Types in use: {string.Join(", ", used.Order(StringComparer.Ordinal))}.");
            }
        }
    }

    private static void CheckLimit(int limit, int max)
    {
        if (limit < 1 || limit > max)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, $"'limit' must be from 1 to {max}.");
        }
    }

    /// <summary>
    /// The entities <paramref name="name"/> names in the given universes: an
    /// exact key or path first, then as a link would (<see cref="DenIndex.MatchEntities"/>).
    /// </summary>
    private static List<EntityRecord> Match(DenIndex index, IReadOnlyCollection<int> universeIds, string name)
    {
        var path = name.Replace('\\', '/');
        var exact = index.Context.Entities
            .Where(e => universeIds.Contains(e.UniverseId) && (e.Key == name || e.Path == path))
            .ToList();
        if (exact.Count > 0)
        {
            return exact.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        }

        return universeIds.SelectMany(u => index.MatchEntities(u, name)).ToList();
    }

    private static DenToolException Ambiguous(
        string name, IReadOnlyList<EntityRecord> matches, Dictionary<int, string> universes, Dictionary<int, ContainerRecord> scopes) =>
        new(
            ToolErrorCodes.AmbiguousEntity,
            $"'{name}' matches {matches.Count} entities. Ask again with one's key or path, or name its universe.",
            matches.Select(m => Summary(m, universes, scopes)).ToList());

    private static Dictionary<int, ContainerRecord> Scopes(IndexDbContext db) => db.Containers.ToDictionary(c => c.Id);

    private static EntitySummary Summary(EntityRecord e, Dictionary<int, string> universes, Dictionary<int, ContainerRecord> scopes) => new(
        e.Key, e.Ulid, e.Name, e.Type, e.Status, universes[e.UniverseId], scopes[e.ScopeId].Kind, scopes[e.ScopeId].Key, e.Path, e.FromText);

    /// <summary>The author's full view of an entity: every field entry and section, whatever its status or range.</summary>
    private static EntityView FullView(
        IndexDbContext db, EntityRecord e, Dictionary<int, string> universes, Dictionary<int, ContainerRecord> scopes)
    {
        var aliases = db.Aliases.Where(a => a.EntityId == e.Id).OrderBy(a => a.Order).Select(a => a.Alias).ToList();
        var fields = db.FieldValues.Where(v => v.EntityId == e.Id).ToList()
            .OrderBy(v => v.FieldOrder).ThenBy(v => v.EntryIndex ?? -1)
            .Select(v => new FieldView(
                v.Field, Json(v.ValueJson), v.Kind, v.Status, v.Pinned, v.FromText, v.UntilText, v.Line, v.EntryIndex, v.IsLive))
            .ToList();
        var sections = db.Sections.Where(s => s.EntityId == e.Id).ToList()
            .OrderBy(s => s.Index)
            .Select(s => new SectionView(
                s.Name, s.Level, JsonSerializer.Deserialize<string[]>(s.HeadingPathJson) ?? [], s.Status, s.Pinned, s.FromText, s.UntilText, s.Line,
                Text(s.Body), s.IsBookkeeping))
            .ToList();

        // Pinned canon: pinned facts in canon (F-PIN-3 warns about the others), first.
        var pinned = new PinnedFacts(
            fields.Where(f => f.Pinned && f.Live == true && f.Status is "draft" or "canon").ToList(),
            sections.Where(s => s.Pinned && s.Status is "draft" or "canon" && s.Bookkeeping != true).ToList());
        var summary = Summary(e, universes, scopes);
        return new EntityView(
            summary.Key, summary.Id, summary.Name, summary.Type, summary.Status, summary.Universe, summary.ScopeKind, summary.ScopeKey, summary.Path,
            summary.From, aliases, Text(e.Lead), pinned, fields, sections);
    }

    private static EntityView CanonEntityView(CanonEntity e, string universe, bool includeContent) => new(
        e.Key, e.Id, e.Name, e.Type, e.Status, universe, e.ScopeKind, e.ScopeKey, e.Path, e.From,
        null,
        includeContent ? Text(e.Lead) : null,
        null,
        includeContent ? e.Fields.Select(CanonField).ToList() : null,
        includeContent ? e.Sections.Select(CanonSection).ToList() : null);

    private static FieldView CanonField(CanonFieldValue f) => new(f.Name, Json(f.ValueJson), f.Kind, f.Status, f.Pinned, f.From, f.Until, f.Line);

    private static SectionView CanonSection(CanonSection s) =>
        new(s.Name, s.Level, s.HeadingPath, s.Status, s.Pinned, s.From, s.Until, s.Line, Text(s.Body));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Text with LF line breaks and without blank lines or trailing spaces at either end.</summary>
    private static string Text(string text) => text.ReplaceLineEndings("\n").Trim('\n').TrimEnd();
}
