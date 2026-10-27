using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolfden.Core.History;
using Wolfden.Index;

namespace Wolfden.Api;

/// <summary>
/// The read endpoints the web UI builds on (#24 to #27; design.md §9): the
/// hierarchy, story positions, entity lists and pages, canon as of a
/// position, search, and history. They answer from the den's shared read
/// queries (<see cref="DenReader"/>), as the MCP read tools do, so the same
/// question gets the same answer. Each request brings the index up to date
/// first.
/// </summary>
/// <remarks>
/// <para>
/// Parameters are query parameters, in camelCase. Each endpoint takes only
/// its own (a misspelt one, such as <c>as_of</c>, is <c>bad_argument</c>
/// rather than quietly ignored), each at most once, and an empty value is
/// the same as leaving it out.
/// </para>
/// <para>
/// <c>asOf</c> is a story position, as the MCP tools' <c>as_of</c>:
/// <c>book:chapter</c>, or a bare book key for the start of that book. Every
/// endpoint that shows canon takes it, with the MCP tools' meaning; without
/// it, the author's full view.
/// </para>
/// </remarks>
public static class ReadEndpoints
{
    /// <summary>The shape version of the results made here.</summary>
    public const int ResultVersion = 1;

    /// <summary>The paths under <c>/api</c> this maps, all GET.</summary>
    public static readonly IReadOnlyList<string> Paths = ["hierarchy", "positions", "entities", "entity", "canon", "search", "fields", "history", "history/diff"];

    /// <summary>The words the API's messages use for what to do next (<see cref="ToolWords"/>).</summary>
    public static readonly ToolWords Words = new(
        "add allowRelinks=true", "in 'newName'", "slug", "in 'universe'", "use /api/search or /api/entities", "run 'wolfden check'",
        "run 'wolfden check'", "run 'wolfden check' and look at '{0}'", "This server is pinned to universe '{0}' (wolfden serve --universe)",
        "/api/search and /api/entities", "Ask again with one of the candidates' keys.");

    /// <summary>Maps the read endpoints onto <paramref name="api"/>.</summary>
    public static void Map(RouteGroupBuilder api, WolfdenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        var reader = new DenReader(options.DenRoot)
        {
            WatcherWait = options.WatcherWait,
            PinnedUniverse = options.PinnedUniverse,
            History = options.History,
            Words = Words,
        };

        api.MapGet("/hierarchy", (HttpContext c) => Answer(c, ["universe", "asOf"], q => Hierarchy(reader, q.Text("universe"), q.Position("asOf"))));
        api.MapGet("/positions", (HttpContext c) => Answer(c, ["universe"], q => Positions(reader, q.Text("universe"))));
        api.MapGet("/entities", (HttpContext c) => Answer(
            c,
            ["universe", "type", "status", "series", "book", "asOf", "limit"],
            q => reader.ListEntities(
                q.Text("universe"), q.Text("type"), q.Text("status"), q.Text("series"), q.Text("book"), q.Int("limit", 200), q.Position("asOf"))));
        api.MapGet("/entity", (HttpContext c) => Answer(
            c, ["entity", "universe", "asOf"], q => EntityPages.Get(reader, q.Text("entity"), q.Text("universe"), q.Position("asOf"))));
        api.MapGet("/canon", (HttpContext c) => Answer(
            c,
            ["universe", "asOf", "series", "book", "type", "brief"],
            q => reader.CanonAsOf(q.Text("universe"), q.Position("asOf"), q.Text("series"), q.Text("book"), q.Text("type"), q.Bool("brief"))));
        api.MapGet("/search", (HttpContext c) => Answer(
            c,
            ["query", "universe", "type", "status", "series", "book", "canonOnly", "asOf", "limit", "field"],
            q => SearchPage(reader.Search(
                q.Text("query") ?? "", q.Text("universe"), q.Text("type"), q.Bool("canonOnly"), q.Int("limit", 20),
                q.Text("status"), q.Text("series"), q.Text("book"), q.Position("asOf"), q.Text("field")))));
        api.MapGet("/fields", (HttpContext c) => Answer(c, ["universe", "asOf"], q => reader.FieldNames(q.Text("universe"), q.Position("asOf"))));
        api.MapGet("/history", (HttpContext c) => Answer(
            c, ["entity", "universe", "limit", "before"], q => History(reader, q.Text("entity"), q.Text("universe"), q.Int("limit", 20), q.Text("before"))));
        api.MapGet("/history/diff", (HttpContext c) => Answer(
            c, ["entity", "universe", "checkpoint", "from", "to"],
            q => HistoryDiff(reader, q.Text("entity"), q.Text("universe"), q.Text("checkpoint"), q.Text("from"), q.Text("to"))));
    }

    /// <summary>
    /// A search's result as the API gives it: the tool's, with each hit's
    /// text and match ranges added (#179), so the page can mark matches
    /// without guessing which brackets are the search's.
    /// </summary>
    internal static SearchPageResult SearchPage(SearchResult result) => new(
        result.Version, result.Query, result.Count, result.Truncated,
        result.Hits.Select(h => new SearchPageHit(h.Entity, h.Section, h.Line, h.Snippet, h.Status, h.InCanon, h.InComment, h.Field, h.Text, h.Matches))
            .ToList());

    /// <summary>The longest part of a revision a message repeats.</summary>
    private const int RevisionShown = 80;

    /// <summary>An entity's checkpoints, a page at a time, as <c>get_history</c> gives them.</summary>
    internal static GetHistoryResult History(DenReader reader, string? entity, string? universe, int limit, string? before)
    {
        CheckRevision("before", before);
        try
        {
            return reader.GetHistory(entity ?? "", universe, limit, before);
        }
        catch (DenToolException e) when (e.Code == ToolErrorCodes.UnknownCheckpoint && before is { Length: > RevisionShown })
        {
            throw new DenToolException(e.Code, e.Message.Replace(before, Clip(before), StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Refuses a revision that can't be a checkpoint id or tag name (one
    /// that starts with <c>-</c>, or holds a space or a control character)
    /// as <c>bad_argument</c>, rather than as a checkpoint that isn't there.
    /// </summary>
    private static void CheckRevision(string name, string? revision)
    {
        if (revision is not null && (revision.StartsWith('-') || revision.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
        {
            throw new DenToolException(
                ToolErrorCodes.BadArgument,
                $"'{name}' must be a checkpoint's id or a tag's name, which can't start with '-' or hold spaces: not '{Clip(revision)}'.");
        }
    }

    /// <summary>A revision cut to <see cref="RevisionShown"/> characters for a message.</summary>
    private static string Clip(string revision) => revision.Length <= RevisionShown ? revision : revision[..RevisionShown] + "…";

    /// <summary>The HTTP status for an error code.</summary>
    internal static int StatusOf(string code) => code switch
    {
        ToolErrorCodes.DenNotLoaded or ToolErrorCodes.DenUnreadable => StatusCodes.Status503ServiceUnavailable,
        ToolErrorCodes.NoUniverse or ToolErrorCodes.UnknownUniverse or ToolErrorCodes.UnknownScope or ToolErrorCodes.UnknownEntity
            or ToolErrorCodes.UnknownCheckpoint => StatusCodes.Status404NotFound,
        ToolErrorCodes.UnknownSection => StatusCodes.Status404NotFound,
        ToolErrorCodes.AmbiguousEntity or ToolErrorCodes.AmbiguousSection or ToolErrorCodes.StaleFile or ToolErrorCodes.Frozen
            or ToolErrorCodes.AlreadyExists => StatusCodes.Status409Conflict,
        ToolErrorCodes.InvalidEdit or ToolErrorCodes.UnsupportedEdit => StatusCodes.Status422UnprocessableEntity,
        ToolErrorCodes.HistoryFailed or ToolErrorCodes.Internal or ToolErrorCodes.WriteFailed => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>Reads the query, runs <paramref name="read"/>, and answers with its result as JSON, or its error.</summary>
    internal static IResult Answer<T>(HttpContext context, string[] parameters, Func<ApiQuery, T> read)
    {
        try
        {
            return Results.Json(read(ApiQuery.Of(context, parameters)));
        }
        catch (DenToolException e)
        {
            // The reader's messages are in the API's own words (Words).
            return Results.Json(new ApiError(e.Code, e.Message, e.Candidates), statusCode: StatusOf(e.Code));
        }
    }

    /// <summary>A universe, its series and books in order, their chapters, and how many entities each scope holds.</summary>
    internal static HierarchyResult Hierarchy(DenReader reader, string? universe, string? asOf) => reader.WithIndex(index =>
    {
        var db = index.Context;
        var u = reader.ChooseUniverse(db, universe, asOf, allowAll: false)!;
        var containers = db.Containers.Where(c => c.UniverseId == u.Id).ToList();
        var chapters = db.Chapters.Where(c => c.UniverseId == u.Id).ToList().ToLookup(c => c.BookId);
        var entities = db.Entities.Where(e => e.UniverseId == u.Id).Select(e => new { e.ScopeId, e.Type, e.Path }).ToList();
        if (asOf is not null)
        {
            // As of a position, only what the reader has met: the canon view's entities.
            var inView = reader.CanonView(index, u.Key, asOf, null).Entities.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
            entities = entities.Where(e => inView.Contains(e.Path)).ToList();
        }

        var byScope = entities.ToLookup(e => e.ScopeId);
        EntityCounts Counts(ContainerRecord c) => new(
            byScope[c.Id].Count(),
            new SortedDictionary<string, int>(byScope[c.Id].GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal));
        BookNode Book(ContainerRecord b) => new(
            b.Key, b.Name, b.Ulid, b.Path, b.BookStatus ?? "draft", b.Key, Counts(b),
            chapters[b.Id].OrderBy(c => c.Index).Select(c => new ChapterNode(c.Key, c.Number, c.Title, $"{b.Key}:{c.Key}")).ToList());
        List<BookNode> BooksIn(ContainerRecord parent) =>
            containers.Where(c => c.Kind == "book" && c.ParentId == parent.Id).OrderBy(c => c.StoryOrder).ThenBy(c => c.Key, StringComparer.Ordinal).Select(Book).ToList();

        var series = containers.Where(c => c.Kind == "series")
            .OrderBy(c => c.StartOrdinal).ThenBy(c => c.StoryOrder ?? int.MaxValue).ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(s => new SeriesNode(s.Key, s.Name, s.Ulid, s.Path, Counts(s), BooksIn(s)))
            .ToList();
        return new HierarchyResult(ResultVersion, asOf, new UniverseNode(u.Key, u.Name, u.Ulid, u.Path, Counts(u), series, BooksIn(u)));
    });

    /// <summary>Every story position of a universe, in story order.</summary>
    internal static PositionsResult Positions(DenReader reader, string? universe) => reader.WithIndex(index =>
    {
        var db = index.Context;
        var u = reader.ChooseUniverse(db, universe, null, allowAll: false)!;
        var containers = db.Containers.Where(c => c.UniverseId == u.Id).ToList().ToDictionary(c => c.Id);
        var chapters = db.Chapters.Where(c => c.UniverseId == u.Id).ToList().ToLookup(c => c.BookId);
        var positions = new List<PositionView>();
        foreach (var book in containers.Values.Where(c => c.Kind == "book").OrderBy(c => c.StoryOrder).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var series = book.ParentId is { } parent && containers[parent].Kind == "series" ? containers[parent].Key : null;
            var own = chapters[book.Id].OrderBy(c => c.Index).ToList();
            if (own.Count == 0)
            {
                // A book with no chapters yet still has its start (format.md F-ORD-3).
                positions.Add(new PositionView(book.Key, book.Key, book.Name, series, null, null, null));
                continue;
            }

            positions.AddRange(own.Select(c => new PositionView($"{book.Key}:{c.Key}", book.Key, book.Name, series, c.Key, c.Number, c.Title)));
        }

        return new PositionsResult(ResultVersion, u.Key, positions.Count, positions);
    });

    /// <summary>What changed in an entity's file: in one checkpoint, between two, or since one (by default the latest) up to now.</summary>
    internal static HistoryDiffResult HistoryDiff(DenReader reader, string? entity, string? universe, string? checkpoint, string? from, string? to)
    {
        DenReader.CheckEntityName(entity);
        if (checkpoint is not null && (from is not null || to is not null))
        {
            throw new DenToolException(
                ToolErrorCodes.BadArgument,
                "Give 'checkpoint' for what one checkpoint changed, or 'from' and 'to' to compare two, not both.");
        }

        if (to is not null && from is null)
        {
            throw new DenToolException(
                ToolErrorCodes.BadArgument,
                "Give 'from' with 'to': the older checkpoint to compare from. Leave both out for what changed since the latest checkpoint.");
        }

        CheckRevision("checkpoint", checkpoint);
        CheckRevision("from", from);
        CheckRevision("to", to);
        var summary = reader.HistoryEntity(entity, universe);
        try
        {
            var history = reader.History();
            var diff = checkpoint is not null
                ? history.DiffCheckpoint(checkpoint, summary.Path)
                : history.Diff(new HistoryDiffQuery(from, to, summary.Path));
            if (!diff.IsAvailable)
            {
                return new HistoryDiffResult(ResultVersion, summary, history.Name, false, diff.Reason, null);
            }

            var view = DenReader.DiffView(diff.Value);
            return new HistoryDiffResult(
                ResultVersion, summary, history.Name, true, null, new DiffView(view.From.Length == 0 ? null : view.From, view.To, view.Files));
        }
        catch (HistoryRevisionException e)
        {
            throw new DenToolException(
                ToolErrorCodes.UnknownCheckpoint,
                e.Revision.Length > RevisionShown ? e.Message.Replace(e.Revision, Clip(e.Revision), StringComparison.Ordinal) : e.Message);
        }
        catch (HistoryException e)
        {
            throw new DenToolException(ToolErrorCodes.HistoryFailed, e.Message);
        }
    }

    /// <summary>A request's query parameters, checked against the ones its endpoint takes.</summary>
    internal sealed class ApiQuery
    {
        private readonly IQueryCollection _query;

        private ApiQuery(IQueryCollection query)
        {
            _query = query;
        }

        /// <summary>The query of <paramref name="context"/>'s request, which may hold only <paramref name="parameters"/>, each once.</summary>
        public static ApiQuery Of(HttpContext context, string[] parameters)
        {
            var query = context.Request.Query;
            foreach (var (name, values) in query)
            {
                if (!parameters.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new DenToolException(
                        ToolErrorCodes.BadArgument,
                        $"'{context.Request.Path}' doesn't take '{name}'. It takes: {string.Join(", ", parameters)}.");
                }

                if (values.Count > 1)
                {
                    throw new DenToolException(ToolErrorCodes.BadArgument, $"Give '{name}' once, not {values.Count} times.");
                }
            }

            return new ApiQuery(query);
        }

        /// <summary>A text parameter, trimmed, or null if it's missing or empty.</summary>
        public string? Text(string name)
        {
            var value = _query[name].ToString().Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>
        /// A story position parameter, or null if it's missing. Unlike other
        /// parameters, an empty one is refused rather than left out: left
        /// out, it gives the author's full view, spoilers and all, so a page
        /// that meant to send a position and sent nothing fails closed.
        /// </summary>
        public string? Position(string name)
        {
            if (!_query.ContainsKey(name))
            {
                return null;
            }

            return Text(name) ?? throw new DenToolException(
                ToolErrorCodes.BadArgument,
                $"'{name}' is empty. Give a story position, such as 'book:chapter' or a bare book key, or leave '{name}' out for the full view.");
        }

        /// <summary>A whole-number parameter, or <paramref name="otherwise"/> if it's missing.</summary>
        public int Int(string name, int otherwise)
        {
            if (Text(name) is not { } text)
            {
                return otherwise;
            }

            return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new DenToolException(ToolErrorCodes.BadArgument, $"'{name}' must be a whole number, not '{text}'.");
        }

        /// <summary>A true-or-false parameter: <c>true</c> or <c>false</c>, ignoring case; false if it's missing.</summary>
        public bool Bool(string name) => Text(name) switch
        {
            null => false,
            var t when t.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            var t when t.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            var t => throw new DenToolException(ToolErrorCodes.BadArgument, $"'{name}' must be true or false, not '{t}'."),
        };
    }
}
