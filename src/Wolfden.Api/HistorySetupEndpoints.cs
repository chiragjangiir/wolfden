using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolfden.Core.History;

namespace Wolfden.Api;

/// <summary>
/// Turning history on from the checkpoint dialog (#195): <c>POST
/// /api/history/turn-on</c> does what <c>wolfden init --git</c> does for
/// an existing den (<see cref="IHistorySetup"/>), and <c>GET
/// /api/checkpoint</c> says, for a den without history, whether it can and
/// whether git needs a name or email first (<see cref="TurnOnView"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not from another site.</b> As for the checkpoint POST, on top of the
/// API's guard (<see cref="WolfdenApi.CrossSiteGuard"/>), the request must
/// be <c>application/json</c>, which a page on another site can't send
/// without a CORS preflight this server never answers.
/// </para>
/// <para>
/// <b>A pinned server may turn it on.</b> History covers the whole den
/// (design.md §6), as a pinned server's checkpoints already do, so <c>serve
/// --universe</c> turns it on for the den, and the first checkpoint records
/// every den file. Its result names no files, only how many.
/// </para>
/// <para>
/// <b>The running server notices.</b> The server asks for the den's history
/// provider on each request (<see cref="WolfdenApiOptions.History"/>), so
/// checkpoints, the History tab, freezing, and automatic checkpoints work
/// as soon as history is on, without a restart.
/// </para>
/// </remarks>
public static class HistorySetupEndpoints
{
    /// <summary>The shape version of the results made here.</summary>
    public const int ResultVersion = 1;

    /// <summary>The paths under <c>/api</c> that answer POST only.</summary>
    public static readonly IReadOnlyList<string> PostPaths = ["history/turn-on"];

    /// <summary>What a server without a way to turn history on says.</summary>
    internal const string CantHere = "This server can't turn history on. Run 'wolfden init --git' in the den's folder instead.";

    /// <summary>Maps the endpoint onto <paramref name="api"/>.</summary>
    public static void Map(RouteGroupBuilder api, WolfdenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        api.MapPost("/history/turn-on", async (HttpContext c) =>
        {
            if (!c.Request.HasJsonContentType())
            {
                return Error(
                    StatusCodes.Status415UnsupportedMediaType,
                    "unsupported_media_type",
                    "Send the request as JSON (Content-Type: application/json), such as {\"firstCheckpoint\": true}.");
            }

            try
            {
                ReadEndpoints.ApiQuery.Of(c, []);
                var text = await CheckpointEndpoints.ReadBody(c.Request).ConfigureAwait(false);
                if (text is null)
                {
                    return Error(StatusCodes.Status413PayloadTooLarge, "too_large", $"A request to turn history on is at most {CheckpointEndpoints.LargestBody / 1024} KB.");
                }

                return TurnOn(options, Request(c, text));
            }
            catch (DenToolException e)
            {
                return Error(ReadEndpoints.StatusOf(e.Code), e.Code, e.Message);
            }
        });
    }

    /// <summary>For the status of a den without history: whether this server can turn it on, and what git still needs.</summary>
    internal static TurnOnView View(WolfdenApiOptions options)
    {
        if (options.HistorySetup is not { } setup)
        {
            return new TurnOnView(false, CantHere, false, false);
        }

        var check = setup.Check();
        return new TurnOnView(check.Possible, check.Reason, check.NeedsName, check.NeedsEmail);
    }

    private static IResult TurnOn(WolfdenApiOptions options, HistorySetupRequest request)
    {
        if (options.HistorySetup is not { } setup)
        {
            return Error(StatusCodes.Status409Conflict, "history_unavailable", CantHere);
        }

        HistorySetupResult result;
        lock (options.TurnOnLock)
        {
            // The first checkpoint goes through the server's ledger, as its other checkpoints do.
            result = setup.TurnOn(request with { Checkpoint = (history, checkpoint) => options.Ledger.Checkpoint(history, checkpoint).Result });
        }

        if (result.Status == HistorySetupStatus.Done)
        {
            return Results.Json(new HistoryTurnedOnResult(
                ResultVersion,
                "git",
                result.Steps,
                result.RepositoryMade,
                result.GitIgnore,
                result.IdentitySaved,
                result.Checkpoint is { } first ? CheckpointEndpoints.Summary(first) : null));
        }

        var status = result.Code switch
        {
            HistorySetupCodes.BadArgument or HistorySetupCodes.IdentityNeeded when result.Status == HistorySetupStatus.Refused => StatusCodes.Status400BadRequest,
            HistorySetupCodes.InRepository or HistorySetupCodes.HasRepository or HistorySetupCodes.GitNotInstalled => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };

        // Part way: what was done comes first, then what wasn't, and how to finish.
        var message = result.Status == HistorySetupStatus.Incomplete
            ? $"{string.Join(" ", result.Steps)} {result.Problem}"
            : result.Problem!;
        return Error(status, result.Code!, message);
    }

    /// <summary>The request from a POST's JSON body, checked.</summary>
    private static HistorySetupRequest Request(HttpContext context, string text)
    {
        JsonElement json;
        try
        {
            using var document = JsonDocument.Parse(text);
            json = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "The request isn't valid JSON. Send {\"firstCheckpoint\": true}, with 'authorName' and 'authorEmail' if git needs them.");
        }

        var body = WriteEndpoints.Body.Of(context, json, ["authorName", "authorEmail", "firstCheckpoint"]);
        var name = body.RawText("authorName");
        var email = body.RawText("authorEmail");
        foreach (var (what, value) in new[] { ("name", name), ("email", email) })
        {
            if (value is not null && HistorySetup.IdentityProblem(what, value.Trim()) is { } problem)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, problem);
            }
        }

        return new HistorySetupRequest(
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            body.OptionalBool("firstCheckpoint") ?? true);
    }

    private static IResult Error(int status, string code, string message) => Results.Json(new ApiError(code, message), statusCode: status);
}

/// <summary>For a den without history, whether this server can turn it on (#195).</summary>
/// <param name="Possible">True if it can, given a name and email when they're needed.</param>
/// <param name="Reason">Why it can't, in words for the writer, or null.</param>
/// <param name="NeedsName">True if git has no name for the den, so the writer must give one.</param>
/// <param name="NeedsEmail">True if git has no email for the den, so the writer must give one (a placeholder will do).</param>
public sealed record TurnOnView(bool Possible, string? Reason, bool NeedsName, bool NeedsEmail);

/// <summary>What <c>POST /api/history/turn-on</c> answers when history is on.</summary>
/// <param name="Version">The shape of this result.</param>
/// <param name="Backend">The history backend's name: <c>git</c>.</param>
/// <param name="Steps">What was done, in order, in words for the writer.</param>
/// <param name="RepositoryMade">True if a repository was made; false if the den's own empty one was used.</param>
/// <param name="GitIgnore"><c>created</c>, <c>added</c>, or <c>present</c>: what happened to the den's <c>.gitignore</c>.</param>
/// <param name="IdentitySaved">The settings saved for the den's repository only: <c>user.name</c>, <c>user.email</c>, both, or none.</param>
/// <param name="Checkpoint">The first checkpoint, briefly (no file names), or null if none was made.</param>
public sealed record HistoryTurnedOnResult(
    int Version, string Backend, IReadOnlyList<string> Steps, bool RepositoryMade, string? GitIgnore, IReadOnlyList<string> IdentitySaved,
    CheckpointSummary? Checkpoint);
