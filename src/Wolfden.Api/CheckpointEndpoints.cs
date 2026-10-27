using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolfden.Core.History;
using Wolfden.Core.Structure;

namespace Wolfden.Api;

/// <summary>
/// Checkpoints from the web UI (#30; design.md §6, §9): <c>GET
/// /api/checkpoint</c> says whether the den has history and which den files
/// changed since the latest checkpoint, and <c>POST /api/checkpoint</c>
/// makes one with the writer's message. Saving never makes a checkpoint
/// (#28); a checkpoint is made only when the writer asks, or by
/// <c>auto_checkpoint</c> (format.md F-DEN-9).
/// </summary>
/// <remarks>
/// <para>
/// <b>The same checkpoint as the MCP tool's.</b> Both go through
/// <see cref="CheckpointLedger"/> and the den's <see cref="IHistoryProvider"/>,
/// so a checkpoint records only den files and leaves whatever else the
/// writer staged alone. The writer is the author; there's no co-author.
/// </para>
/// <para>
/// <b>Not from another site.</b> On top of the API's guard (<see
/// cref="WolfdenApi.CrossSiteGuard"/>), a POST must be
/// <c>application/json</c>, which a page on another site can't send
/// without a CORS preflight this server never answers.
/// </para>
/// <para>
/// <b>A pinned server checkpoints the whole den.</b> A checkpoint covers
/// the den (design.md §6), as the MCP tool's does in a pinned session, so
/// <c>serve --universe</c> records every den file too. It doesn't name
/// files in other universes, as for the rest of a pinned server (images,
/// link hints): it lists the pinned universe's files and the den-level
/// ones, and counts the rest in <c>otherUniverses</c>.
/// </para>
/// </remarks>
public static class CheckpointEndpoints
{
    /// <summary>The shape version of the results made here.</summary>
    public const int ResultVersion = 1;

    /// <summary>The paths under <c>/api</c> this maps, answering GET and POST.</summary>
    public static readonly IReadOnlyList<string> Paths = ["checkpoint"];

    /// <summary>The longest message a checkpoint takes, in characters.</summary>
    public const int LongestMessage = 5000;

    /// <summary>The largest request body a POST takes.</summary>
    private const int LargestBody = 64 * 1024;

    /// <summary>What to do for a den without history, for the page.</summary>
    internal const string EnableHistory =
        "To keep history, make the den's folder a git repository: run 'git init' in it, or start a new den with 'wolfden init --git'. " +
        "Don't keep a git repository in a synced folder (Dropbox, OneDrive, iCloud): use a private remote for backup instead. " +
        "Checkpoints work as soon as the repository is there, without restarting Wolfden.";

    /// <summary>Maps the checkpoint endpoints onto <paramref name="api"/>.</summary>
    public static void Map(RouteGroupBuilder api, WolfdenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        api.MapGet("/checkpoint", (HttpContext c) => Answer(() =>
        {
            ReadEndpoints.ApiQuery.Of(c, []);
            return Results.Json(Status(options));
        }));
        api.MapPost("/checkpoint", async (HttpContext c) =>
        {
            try
            {
                ReadEndpoints.ApiQuery.Of(c, []);
            }
            catch (DenToolException e)
            {
                return Error(StatusCodes.Status400BadRequest, e.Code, e.Message);
            }

            if (!c.Request.HasJsonContentType())
            {
                return Error(
                    StatusCodes.Status415UnsupportedMediaType,
                    "unsupported_media_type",
                    "Send the checkpoint as JSON (Content-Type: application/json), such as {\"message\": \"Notes pass after drafting ch 12\"}.");
            }

            var body = await ReadBody(c.Request).ConfigureAwait(false);
            if (body is null)
            {
                return Error(StatusCodes.Status413PayloadTooLarge, "too_large", $"A checkpoint request is at most {LargestBody / 1024} KB.");
            }

            return Answer(() => Make(options, Message(body)));
        });
    }

    /// <summary>Whether the den has history, the latest checkpoint, and what a checkpoint now would record.</summary>
    internal static CheckpointStatusResult Status(WolfdenApiOptions options)
    {
        var auto = AutoView(options);
        var history = options.History();
        if (!history.Capabilities.HasFlag(HistoryCapabilities.Checkpoint))
        {
            var why = history.Checkpoint(new CheckpointRequest("status")).Reason ?? "This den's history can't make checkpoints.";
            return new CheckpointStatusResult(
                ResultVersion, history.Name, false, why, history is NoHistory ? EnableHistory : null, null, 0, [], 0, auto);
        }

        try
        {
            var pending = history.Pending();
            if (!pending.IsAvailable)
            {
                return new CheckpointStatusResult(ResultVersion, history.Name, false, pending.Reason, null, null, 0, [], 0, auto);
            }

            var log = history.Log(new HistoryLogQuery(Limit: 1));
            var latest = log.IsAvailable && log.Value.Count > 0 ? Summary(log.Value[0]) : null;
            var (shown, hidden) = Split(options, pending.Value, options.Ledger.IsOwn);
            return new CheckpointStatusResult(ResultVersion, history.Name, true, null, null, latest, shown.Count + hidden, shown, hidden, auto);
        }
        catch (HistoryException e)
        {
            throw new DenToolException(ToolErrorCodes.HistoryFailed, e.Message);
        }
    }

    /// <summary>Makes a checkpoint of the whole den with <paramref name="message"/>.</summary>
    internal static IResult Make(WolfdenApiOptions options, string message)
    {
        var history = options.History();
        LedgerCheckpoint done;
        try
        {
            done = options.Ledger.Checkpoint(history, new CheckpointRequest(message));
        }
        catch (ArgumentException e)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, e.Message);
        }
        catch (HistoryException e)
        {
            throw new DenToolException(ToolErrorCodes.HistoryFailed, e.Message);
        }

        if (!done.Result.IsAvailable)
        {
            var enable = history is NoHistory ? $" {EnableHistory}" : "";
            return Error(StatusCodes.Status409Conflict, "history_unavailable", done.Result.Reason + enable);
        }

        if (done.Result.Value is not { } entry)
        {
            return Results.Json(new CheckpointMadeResult(ResultVersion, history.Name, false, null, [], 0));
        }

        var elsewhere = done.ChangedElsewhere.ToHashSet(StringComparer.Ordinal);
        var mine = done.Written.Where(p => !elsewhere.Contains(p)).ToHashSet(StringComparer.Ordinal);
        var (shown, hidden) = Split(options, entry.Changes, c => mine.Contains(c.Path));
        return Results.Json(new CheckpointMadeResult(ResultVersion, history.Name, true, Summary(entry), shown, hidden));
    }

    /// <summary>
    /// The changes to list, in path order, and how many are in universes a
    /// pinned server doesn't serve. A rename counts as another universe's
    /// if either side is.
    /// </summary>
    private static (List<PendingChange> Shown, int Hidden) Split(
        WolfdenApiOptions options, IEnumerable<HistoryFileChange> changes, Func<HistoryFileChange, bool> savedHere)
    {
        var shown = new List<PendingChange>();
        var hidden = 0;
        foreach (var change in changes.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            var universe = UniverseOf(change.Path);
            if (options.PinnedUniverse is { } pinned
                && ((universe is not null && universe != pinned) || (change.OldPath is { } old && UniverseOf(old) is { } was && was != pinned)))
            {
                hidden++;
                continue;
            }

            shown.Add(new PendingChange(change.Kind.ToString().ToLowerInvariant(), change.Path, change.OldPath, universe, savedHere(change)));
        }

        return (shown, hidden);
    }

    /// <summary>The universe a den path is in (<c>universes/&lt;key&gt;/…</c>, F-KEY-3), or null for a den-level file.</summary>
    internal static string? UniverseOf(string path)
    {
        var parts = path.Split('/');
        return parts.Length > 2 && parts[0] == "universes" ? parts[1] : null;
    }

    private static CheckpointSummary Summary(HistoryEntry e) => new(e.Id, e.When, e.Author, e.Message, e.Changes.Count);

    private static AutoCheckpointView AutoView(WolfdenApiOptions options)
    {
        var setting = AutoCheckpointer.ReadSetting(options.DenRoot);
        return new AutoCheckpointView(setting.Every is { } every ? AutoCheckpoint.Format(every) : null, setting.Problem, options.AutoCheckpoints);
    }

    /// <summary>The message from a POST's JSON body, checked.</summary>
    private static string Message(string body)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "The request isn't valid JSON. Send {\"message\": \"…\"}.");
        }

        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, "Send the checkpoint as a JSON object: {\"message\": \"…\"}.");
            }

            string? message = null;
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (property.Name != "message")
                {
                    throw new DenToolException(ToolErrorCodes.BadArgument, $"A checkpoint takes only 'message', not '{property.Name}'.");
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new DenToolException(ToolErrorCodes.BadArgument, "'message' must be text.");
                }

                message = property.Value.GetString();
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    "Name the checkpoint in 'message', such as 'Notes pass after drafting ch 12', so you can find it in the history later.");
            }

            message = message.Trim();
            if (message.Length > LongestMessage)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"A checkpoint's message is at most {LongestMessage} characters, not {message.Length}.");
            }

            if (message.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, "A checkpoint's message can't hold control characters other than line breaks and tabs.");
            }

            return message;
        }
    }

    /// <summary>The request's body as text, or null if it's larger than <see cref="LargestBody"/>.</summary>
    private static async Task<string?> ReadBody(HttpRequest request)
    {
        if (request.ContentLength > LargestBody)
        {
            return null;
        }

        var buffer = new byte[LargestBody + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await request.Body.ReadAsync(buffer.AsMemory(read), request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
        {
            read += n;
        }

        return read > LargestBody ? null : new UTF8Encoding(false).GetString(buffer, 0, read);
    }

    private static IResult Answer(Func<IResult> answer)
    {
        try
        {
            return answer();
        }
        catch (DenToolException e)
        {
            return Error(ReadEndpoints.StatusOf(e.Code), e.Code, e.Message);
        }
    }

    private static IResult Error(int status, string code, string message) => Results.Json(new ApiError(code, message), statusCode: status);
}

/// <summary>What <c>GET /api/checkpoint</c> answers.</summary>
/// <param name="Version">The shape of this result.</param>
/// <param name="Backend">The history backend's name: <c>git</c>, or <c>none</c>.</param>
/// <param name="Available">True if the den has history that can make checkpoints.</param>
/// <param name="Reason">Why it can't, in words for the writer, or null.</param>
/// <param name="Enable">For a den without history, how to turn it on; otherwise null.</param>
/// <param name="Latest">The latest checkpoint of the den, or null if there's none yet.</param>
/// <param name="Count">How many den files a checkpoint now would record, those in <paramref name="Changes"/> and <paramref name="OtherUniverses"/>.</param>
/// <param name="Changes">The changed den files a checkpoint now would record, in path order (not those in <paramref name="OtherUniverses"/>).</param>
/// <param name="OtherUniverses">For a pinned server, how many changed files are in other universes; they're recorded too, but not named.</param>
/// <param name="AutoCheckpoint">What <c>auto_checkpoint</c> says (F-DEN-9).</param>
public sealed record CheckpointStatusResult(
    int Version, string Backend, bool Available, string? Reason, string? Enable, CheckpointSummary? Latest, int Count,
    IReadOnlyList<PendingChange> Changes, int OtherUniverses, AutoCheckpointView AutoCheckpoint);

/// <summary>A den file that changed since the latest checkpoint, or that a checkpoint recorded.</summary>
/// <param name="Kind">added, modified, deleted, or renamed.</param>
/// <param name="Path">The file, relative to the den root, after the change.</param>
/// <param name="OldPath">For a rename, where it was before; otherwise null.</param>
/// <param name="Universe">The universe it's in, or null for a den-level file.</param>
/// <param name="SavedHere">
/// True if this server's web UI saved it and nothing else changed it since;
/// false for a change made some other way: in an editor, by an AI agent
/// through MCP, or before the server started.
/// </param>
public sealed record PendingChange(string Kind, string Path, string? OldPath, string? Universe, bool SavedHere);

/// <summary>One checkpoint, briefly.</summary>
/// <param name="Id">Its id (a commit hash, for git).</param>
/// <param name="When">When it was recorded.</param>
/// <param name="Author">Who recorded it.</param>
/// <param name="Message">Its message.</param>
/// <param name="Files">How many den files it changed.</param>
public sealed record CheckpointSummary(string Id, DateTimeOffset When, string Author, string Message, int Files);

/// <summary>The <c>auto_checkpoint</c> setting (F-DEN-9), as this server sees it.</summary>
/// <param name="Every">The wait, as written in the setting's own form (<c>30m</c>, <c>1h30m</c>), or null when it's off.</param>
/// <param name="Problem">Why the value in <c>den.yaml</c> was ignored, or null.</param>
/// <param name="Runs">True if this server makes automatic checkpoints when the setting is on (<c>wolfden serve</c> does).</param>
public sealed record AutoCheckpointView(string? Every, string? Problem, bool Runs);

/// <summary>What <c>POST /api/checkpoint</c> answers when the den has history.</summary>
/// <param name="Version">The shape of this result.</param>
/// <param name="Backend">The history backend's name.</param>
/// <param name="Recorded">True if a checkpoint was made; false if nothing had changed since the last one.</param>
/// <param name="Checkpoint">The checkpoint made, or null.</param>
/// <param name="Changes">The files it recorded, in path order (not those in <paramref name="OtherUniverses"/>).</param>
/// <param name="OtherUniverses">For a pinned server, how many recorded files are in other universes.</param>
public sealed record CheckpointMadeResult(
    int Version, string Backend, bool Recorded, CheckpointSummary? Checkpoint, IReadOnlyList<PendingChange> Changes, int OtherUniverses);
