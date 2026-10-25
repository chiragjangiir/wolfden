using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wolfden.Core.Checks;
using Wolfden.Core.Documents;
using Wolfden.Core.History;
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
public sealed partial class DenTools
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

    /// <summary>The <c>universe</c> value that asks <c>search</c> and <c>list_entities</c> for every universe of the den.</summary>
    public const string AllUniverses = "*";

    /// <summary>The longest a call waits for a running watcher to catch up before it rebuilds the index itself.</summary>
    public TimeSpan WatcherWait { get; init; } = IndexBuilder.WatcherWait;

    /// <summary>
    /// The universe this session is pinned to (<c>wolfden mcp --universe</c>),
    /// or null. Every call reads it, and a call that names another universe,
    /// or <see cref="AllUniverses"/>, is refused.
    /// </summary>
    public string? PinnedUniverse { get; init; }

    /// <summary>
    /// Finds the den's history provider, for <c>get_history</c>. It's asked
    /// on each call, so a den that becomes a repository while the server
    /// runs gets history without a restart. By default the den has none
    /// (<see cref="NoHistory"/>); <c>wolfden mcp</c> uses git (#19).
    /// </summary>
    public Func<IHistoryProvider> History { get; init; } = () => NoHistory.Instance;

    /// <summary>
    /// The checkpoints that changed one entity's file, newest first, a page
    /// at a time, and optionally what changed in it since a checkpoint (or
    /// since the latest one). The entity is named as in <see cref="GetEntity"/>,
    /// without a position. A den without history isn't an error: the result
    /// says history isn't available, and why.
    /// </summary>
    public GetHistoryResult GetHistory(
        string entity, string? universe = null, int limit = 20, string? before = null, bool diff = false, string? diffFrom = null)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Name the entity in 'entity': its name, an alias, its file name, its id, or its path.");
        }

        CheckLimit(limit, 100);
        var summary = WithIndex(index =>
        {
            var db = index.Context;
            var universes = db.Containers.Where(c => c.Kind == "universe").ToDictionary(c => c.Id, c => c.Key);
            var scopes = Scopes(db);
            var u = ChooseUniverse(db, universe, null, allowAll: false)!;
            var matches = Match(index, u.Id, entity.Trim(), null);
            return matches.Count switch
            {
                0 => throw UnknownEntity(entity, u.Key),
                > 1 => throw Ambiguous(entity, matches, universes, scopes),
                _ => Summary(matches[0], universes, scopes),
            };
        });

        try
        {
            var history = History();
            // One more than the limit says whether there's another page.
            var log = history.Log(new HistoryLogQuery(summary.Path, limit + 1, string.IsNullOrWhiteSpace(before) ? null : before.Trim()));
            if (!log.IsAvailable)
            {
                return new GetHistoryResult(ToolJson.Version, summary, history.Name, false, log.Reason, 0, false, null, null, null);
            }

            var entries = log.Value.Take(limit)
                .Select(e => new HistoryEntryView(e.Id, e.When, e.Author, e.Message, e.Changes.Select(Change).ToList()))
                .ToList();
            var truncated = log.Value.Count > limit;
            HistoryDiffView? diffView = null;
            string? note = null;
            if (diff || !string.IsNullOrWhiteSpace(diffFrom))
            {
                var changes = history.Diff(new HistoryDiffQuery(string.IsNullOrWhiteSpace(diffFrom) ? null : diffFrom.Trim(), null, summary.Path));
                if (changes.IsAvailable)
                {
                    diffView = new HistoryDiffView(
                        changes.Value.From,
                        changes.Value.To,
                        changes.Value.Files.Select(f => new HistoryFileDiffView(Change(f.Change), f.Patch)).ToList());
                }
                else
                {
                    note = changes.Reason;
                }
            }

            return new GetHistoryResult(
                ToolJson.Version, summary, history.Name, true, null, entries.Count, truncated, truncated ? entries[^1].Id : null, entries, diffView, note);
        }
        catch (HistoryRevisionException e)
        {
            // The caller's before or diff_from, not a failure of git.
            throw new DenToolException(ToolErrorCodes.UnknownCheckpoint, e.Message);
        }
        catch (HistoryException e)
        {
            throw new DenToolException(ToolErrorCodes.HistoryFailed, e.Message);
        }

        static HistoryChangeView Change(HistoryFileChange c) => new(c.Kind.ToString().ToLowerInvariant(), c.Path, c.OldPath);
    }

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
            var universeId = ChooseUniverse(db, universe, null, allowAll: true)?.Id;
            if (type is not null)
            {
                CheckTypes(db, universeId, [type]);
            }

            // Filters apply after the search, so ask for more when there are
            // any; one more than the limit says whether the hits were cut off.
            var filtered = (universeId is not null && universes.Count > 1) || type is not null || canonOnly;
            var fetch = filtered ? Math.Max(limit * 20, 1000) : limit + 1;
            var hits = index.Search(query, fetch);
            var paths = hits.Select(h => h.EntityPath).Distinct().ToList();
            var entities = db.Entities.Where(e => paths.Contains(e.Path)).ToList().ToDictionary(e => e.Path, StringComparer.Ordinal);
            var scopes = Scopes(db);
            var views = hits
                .Where(h => entities.ContainsKey(h.EntityPath))
                .Select(h => (Hit: h, Entity: entities[h.EntityPath]))
                .Where(x => universeId is null || x.Entity.UniverseId == universeId)
                .Where(x => type is null || x.Entity.Type == type)
                .Where(x => !canonOnly || x.Hit.IsInCanon)
                .Take(limit + 1)
                .Select(x => new SearchHitView(
                    Summary(x.Entity, universes, scopes), x.Hit.SectionName, x.Hit.SectionLine, x.Hit.Snippet, x.Hit.Status, x.Hit.IsInCanon, x.Hit.InComment))
                .ToList();
            // Filtered hits that reached the raw cap may have more beyond it.
            var truncated = views.Count > limit || (filtered && hits.Count == fetch);
            views = views.Take(limit).ToList();
            return new SearchResult(ToolJson.Version, query, views.Count, truncated, views);
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
            var u = ChooseUniverse(db, universe, asOf, allowAll: false)!;
            var name = entity.Trim();
            if (asOf is null)
            {
                var matches = Match(index, u.Id, name, null);
                if (matches.Count == 0)
                {
                    throw UnknownEntity(entity, u.Key);
                }

                if (matches.Count > 1)
                {
                    throw Ambiguous(entity, matches, universes, scopes);
                }

                return new GetEntityResult(
                    ToolJson.Version, "full", null, true, null, FullView(db, matches[0], universes, scopes) with { Hash = FileHash(db, matches[0]) });
            }

            // With a position, the name is matched, tier by tier, among the
            // entities the reader has met by then: an entity met later can't
            // shadow one met already, tie with it, or show in an error.
            var found = CanonView(index, u.Key, asOf, null);
            var met = Match(index, u.Id, name, found.Entities.Select(x => x.Path).ToHashSet(StringComparer.Ordinal));
            if (met.Count > 1)
            {
                throw Ambiguous(entity, met, universes, scopes);
            }

            if (met.Count == 0)
            {
                // Matching every entity only tells "not met yet" from "no such entity".
                if (Match(index, u.Id, name, null).Count == 0)
                {
                    throw UnknownEntity(entity, u.Key);
                }

                return new GetEntityResult(
                    ToolJson.Version, "canon_as_of", asOf, false,
                    $"'{entity}' isn't in canon as of {asOf}: the reader hasn't met it by then, or it isn't draft or canon.", null);
            }

            var inView = found.Entities.Single(x => x.Path == met[0].Path);
            var pinnedEntity = (found with { Entities = [inView] }).Pinned().Entities.SingleOrDefault();
            return new GetEntityResult(
                ToolJson.Version, "canon_as_of", asOf, true, null,
                CanonEntityView(inView, u.Key, includeContent: true) with
                {
                    Hash = FileHash(db, met[0]),
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

            var one = ChooseUniverse(db, universe, null, allowAll: true);
            if (one is null && filter is not null)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument, $"A series or book belongs to one universe, so it doesn't go with '{AllUniverses}'; name the universe.");
            }

            List<ContainerRecord> chosen = one is not null
                ? [one]
                : db.Containers.Where(c => c.Kind == "universe").ToList().OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
            if (type is not null)
            {
                CheckTypes(db, one?.Id, [type]);
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

            var shown = all.Take(limit).ToList();
            return new ListEntitiesResult(ToolJson.Version, shown.Count, all.Count, all.Count > limit, shown);
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
            var u = ChooseUniverse(db, universe, position, allowAll: false)!;
            if (type is not null)
            {
                CheckTypes(db, u.Id, [type], listUsed: position is null);
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
    /// files under one path. With a universe (or in a pinned session), only
    /// that universe's files and the den-level material every universe shares
    /// are checked (<see cref="DenCheck.Run(IDenSource, string?)"/>); without
    /// one, or with <see cref="AllUniverses"/>, the whole den, since check
    /// validates files rather than a story view.
    /// </summary>
    public CheckResult Check(string? severity = null, string? path = null, int limit = 200, string? universe = null)
    {
        CheckLimit(limit, 5000);
        if (severity is not null and not ("error" or "warning"))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, $"'{severity}' isn't a severity. Use 'error' or 'warning'.");
        }

        if (PinnedUniverse is not null)
        {
            if (universe == AllUniverses)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"This session is pinned to universe '{PinnedUniverse}' (wolfden mcp --universe), so it can't check the whole den.");
            }

            if (universe is not null && universe != PinnedUniverse)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"This session is pinned to universe '{PinnedUniverse}' (wolfden mcp --universe), so it can't check universe '{universe}'.");
            }

            universe = PinnedUniverse;
        }

        var scope = universe == AllUniverses ? null : universe;
        DenCheck check;
        string? note = null;
        try
        {
            if (scope is null)
            {
                check = DenCheck.Run(Root);
            }
            else
            {
                // The other universes' ids come from the index, so a duplicate across universes is found (F-ID-3).
                (check, note) = UniverseCheck.Run(Root, scope, WatcherWait);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DenToolException(ToolErrorCodes.DenUnreadable, $"Couldn't read the den's files: {e.Message}");
        }

        // A den.yaml that doesn't load is still reported: it's what's wrong.
        if (scope is not null && check.IsLoaded && check.Structure.FindUniverse(scope) is null)
        {
            var keys = check.Structure.Universes.Select(u => u.Key).Order(StringComparer.Ordinal).ToList();
            throw new DenToolException(
                ToolErrorCodes.UnknownUniverse,
                $"The den has no universe '{scope}'." + (keys.Count == 0 ? " It has no universes yet." : $" Its universes: {string.Join(", ", keys)}."));
        }

        var prefix = path?.Replace('\\', '/').Trim('/');
        var matching = check.Diagnostics
            .Where(d => severity is null || Severity(d.Diagnostic.Severity) == severity)
            .Where(d => string.IsNullOrEmpty(prefix) || IsAtOrUnder(d.Path, prefix))
            .Select(d => new DiagnosticView(d.Path, d.Diagnostic.Line, Severity(d.Diagnostic.Severity), d.Diagnostic.RuleId, d.Diagnostic.Message))
            .ToList();
        var shown = matching.Take(limit).ToList();
        return new CheckResult(
            ToolJson.Version, scope, check.IsLoaded, check.IsLoaded ? check.Structure.SpoilerChecks : null, check.Catalog.Entities.Count,
            check.ErrorCount, check.WarningCount, shown.Count, matching.Count, matching.Count > limit, shown, note);

        static string Severity(DiagnosticSeverity s) => s == DiagnosticSeverity.Error ? "error" : "warning";
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="prefix"/> or inside it, on a path-segment boundary: <c>books/a</c> isn't under <c>books/ab</c>.</summary>
    internal static bool IsAtOrUnder(string path, string prefix) =>
        path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);

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
    /// <summary>
    /// The universe a call is about (maintainer decision, 2026-10-23): the
    /// session's pinned universe, the one named, the den's only one, or the
    /// one a position's book belongs to (<see cref="UniverseChoice"/>). Null
    /// for <see cref="AllUniverses"/>, which only <paramref name="allowAll"/>
    /// tools accept, never with a position, and never in a pinned session.
    /// </summary>
    private ContainerRecord? ChooseUniverse(IndexDbContext db, string? universe, string? position, bool allowAll)
    {
        if (universe == AllUniverses)
        {
            if (!allowAll)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"'{AllUniverses}' (every universe) is only for search and list_entities; name one universe here.");
            }

            if (position is not null)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"'{AllUniverses}' doesn't go with a position: a position belongs to one universe.");
            }

            if (PinnedUniverse is not null)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"This session is pinned to universe '{PinnedUniverse}' (wolfden mcp --universe), so it can't read every universe.");
            }

            return null;
        }

        if (PinnedUniverse is not null)
        {
            if (universe is not null && universe != PinnedUniverse)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"This session is pinned to universe '{PinnedUniverse}' (wolfden mcp --universe), so it can't read universe '{universe}'.");
            }

            universe = PinnedUniverse;
        }

        string key;
        try
        {
            key = UniverseChoice.Choose(db, universe, position);
        }
        catch (UniverseChoiceException e)
        {
            throw new DenToolException(
                e.Problem switch
                {
                    UniverseChoiceProblem.NoUniverse => ToolErrorCodes.NoUniverse,
                    UniverseChoiceProblem.UnknownUniverse => ToolErrorCodes.UnknownUniverse,
                    UniverseChoiceProblem.BadPosition => ToolErrorCodes.BadPosition,
                    _ => ToolErrorCodes.UniverseRequired,
                },
                e.Problem == UniverseChoiceProblem.UniverseRequired
                    ? $"{e.Message} Give its key in 'universe'{(allowAll ? $", or '{AllUniverses}' for every universe" : "")}."
                    : e.Message);
        }

        return db.Containers.Single(c => c.Kind == "universe" && c.Key == key);
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

    /// <summary>
    /// Checks that each type is defined by an enabled pack or used by an
    /// entity (of the universe, if one is given). The error lists the types in
    /// use unless <paramref name="listUsed"/> is false: for a view at a
    /// position, where a type only later entities use would give them away.
    /// </summary>
    private void CheckTypes(IndexDbContext db, int? universeId, IEnumerable<string> types, bool listUsed = true)
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
                    $"There's no type '{type}': no enabled pack defines it, and no entity uses it." +
                    (listUsed ? $" Types in use: {string.Join(", ", used.Order(StringComparer.Ordinal))}." : ""));
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
    /// With <paramref name="amongPaths"/>, only those entities take part in
    /// either step.
    /// </summary>
    private static List<EntityRecord> Match(DenIndex index, int universeId, string name, IReadOnlySet<string>? amongPaths)
    {
        var path = name.Replace('\\', '/');
        var exact = index.Context.Entities
            .Where(e => e.UniverseId == universeId && (e.Key == name || e.Path == path))
            .ToList()
            .Where(e => amongPaths is null || amongPaths.Contains(e.Path))
            .ToList();
        if (exact.Count > 0)
        {
            return exact.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        }

        return index.MatchEntities(universeId, name, amongPaths).ToList();
    }

    private static DenToolException UnknownEntity(string name, string universe) => new(
        ToolErrorCodes.UnknownEntity,
        $"No entity in universe '{universe}' matches '{name}' by id, path, file name, name, or alias. " +
        "Use search or list_entities to find it.");

    private static DenToolException Ambiguous(
        string name, IReadOnlyList<EntityRecord> matches, Dictionary<int, string> universes, Dictionary<int, ContainerRecord> scopes) =>
        new(
            ToolErrorCodes.AmbiguousEntity,
            $"'{name}' matches {matches.Count} entities. Ask again with one's key or path, or name its universe.",
            matches.Select(m => Summary(m, universes, scopes)).ToList());

    private static Dictionary<int, ContainerRecord> Scopes(IndexDbContext db) => db.Containers.ToDictionary(c => c.Id);

    /// <summary>The SHA-256 of an entity's file as the index read it: the <c>expected_hash</c> a write takes.</summary>
    private static string? FileHash(IndexDbContext db, EntityRecord e) => db.Files.Where(f => f.Id == e.FileId).Select(f => f.Hash).SingleOrDefault();

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
