using Wolfden.Core.Checks;
using Wolfden.Core.Documents;
using Wolfden.Core.History;
using Wolfden.Index.Reading;

namespace Wolfden.Index.Writing;

/// <summary>
/// The tools of the MCP server (design.md §8, #20), over one den. The read
/// tools are the den's shared read queries (<see cref="DenReader"/>), which
/// the web API answers from too, so both give the same answer; <c>check</c>
/// reads the files, as <c>wolfden check</c> does. The write tools are in the
/// other parts of this class.
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
    private DenReader? _reader;

    /// <summary>Tools for the den at <paramref name="denRoot"/>.</summary>
    public DenTools(string denRoot)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        Root = Path.GetFullPath(denRoot);
    }

    /// <summary>The den's folder, as a full path.</summary>
    public string Root { get; }

    /// <summary>The <c>universe</c> value that asks <c>search</c> and <c>list_entities</c> for every universe of the den.</summary>
    public const string AllUniverses = DenReader.AllUniverses;

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
    /// The words errors and effects use to advise what to do next: by
    /// default the MCP tools' parameters and tools, or a command line's
    /// options and commands (<see cref="ToolWords"/>).
    /// </summary>
    public ToolWords Words { get; init; } = ToolWords.Mcp;

    /// <summary>The shared read queries, with this session's settings.</summary>
    private DenReader Reader => _reader ??= new DenReader(Root)
    {
        WatcherWait = WatcherWait,
        PinnedUniverse = PinnedUniverse,
        History = History,
        Words = Words,
    };

    /// <summary>
    /// The checkpoints that changed one entity's file, newest first, a page
    /// at a time, and optionally what changed in it since a checkpoint (or
    /// since the latest one). See <see cref="DenReader.GetHistory"/>.
    /// </summary>
    public GetHistoryResult GetHistory(
        string entity, string? universe = null, int limit = 20, string? before = null, bool diff = false, string? diffFrom = null) =>
        Reader.GetHistory(entity, universe, limit, before, diff, diffFrom);

    /// <summary>Full-text search. See <see cref="DenReader.Search"/>.</summary>
    public SearchResult Search(
        string query, string? universe = null, string? type = null, bool canonOnly = false, int limit = 20, string? field = null) =>
        Reader.Search(query, universe, type, canonOnly, limit, field: field);

    /// <summary>One entity, in the full view or as of a position. See <see cref="DenReader.GetEntity(string, string?, string?)"/>.</summary>
    public GetEntityResult GetEntity(string entity, string? universe = null, string? asOf = null) => Reader.GetEntity(entity, universe, asOf);

    /// <summary>The entities of the den or of one universe. See <see cref="DenReader.ListEntities"/>.</summary>
    public ListEntitiesResult ListEntities(
        string? universe = null, string? type = null, string? status = null, string? series = null, string? book = null, int limit = 200) =>
        Reader.ListEntities(universe, type, status, series, book, limit);

    /// <summary>The canon-as-of view of a universe. See <see cref="DenReader.CanonAsOf"/>.</summary>
    public CanonAsOfResult CanonAsOf(
        string? universe = null, string? position = null, string? series = null, string? book = null, string? type = null, bool brief = false) =>
        Reader.CanonAsOf(universe, position, series, book, type, brief);

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
        DenReader.CheckLimit(limit, 5000);
        if (severity is not null and not ("error" or "warning"))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, $"'{severity}' isn't a severity. Use 'error' or 'warning'.");
        }

        if (PinnedUniverse is not null)
        {
            if (universe == AllUniverses)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"{Words.Pinned(PinnedUniverse)}, so it can't check the whole den.");
            }

            if (universe is not null && universe != PinnedUniverse)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"{Words.Pinned(PinnedUniverse)}, so it can't check universe '{universe}'.");
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
            DenReader.ResultVersion, scope, check.IsLoaded, check.IsLoaded ? check.Structure.SpoilerChecks : null, check.Catalog.Entities.Count,
            check.ErrorCount, check.WarningCount, shown.Count, matching.Count, matching.Count > limit, shown, note);

        static string Severity(DiagnosticSeverity s) => s == DiagnosticSeverity.Error ? "error" : "warning";
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="prefix"/> or inside it, on a path-segment boundary: <c>books/a</c> isn't under <c>books/ab</c>.</summary>
    internal static bool IsAtOrUnder(string path, string prefix) =>
        path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);

    // The shared read helpers the write tools use too (DenReader).

    /// <summary>Brings the index up to date, opens it, and runs <paramref name="query"/>, turning failures into tool errors.</summary>
    private T WithIndex<T>(Func<DenIndex, T> query) => Reader.WithIndex(query);

    /// <summary>The universe a call is about. See <see cref="DenReader.ChooseUniverse"/>.</summary>
    private ContainerRecord? ChooseUniverse(IndexDbContext db, string? universe, string? position, bool allowAll) =>
        Reader.ChooseUniverse(db, universe, position, allowAll);

    private static CanonFilter? Filter(string? series, string? book) => DenReader.Filter(series, book);

    /// <summary>Checks that the filter names one series or book of the universe.</summary>
    private static void CheckFilter(IndexDbContext db, ContainerRecord universe, CanonFilter? filter, ToolWords? words = null) =>
        DenReader.CheckFilter(db, universe, filter, words);

    private static List<EntityRecord> Match(DenIndex index, int universeId, string name, IReadOnlySet<string>? amongPaths) =>
        DenReader.Match(index, universeId, name, amongPaths);

    private DenToolException UnknownEntity(string name, string universe) => Reader.UnknownEntity(name, universe);

    private static DenToolException Ambiguous(
        string name, IReadOnlyList<EntityRecord> matches, Dictionary<int, string> universes, Dictionary<int, ContainerRecord> scopes) =>
        DenReader.Ambiguous(name, matches, universes, scopes);

    private static Dictionary<int, ContainerRecord> Scopes(IndexDbContext db) => DenReader.Scopes(db);

    private static EntitySummary Summary(EntityRecord e, Dictionary<int, string> universes, Dictionary<int, ContainerRecord> scopes) =>
        DenReader.Summary(e, universes, scopes);

    /// <summary>Text with LF line breaks and without blank lines or trailing spaces at either end.</summary>
    private static string Text(string text) => DenReader.Text(text);
}
