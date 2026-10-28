using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolfden.Core.History;

namespace Wolfden.Api;

/// <summary>
/// Freezing and unfreezing a book from the web UI (#31; design.md §5):
/// <c>GET /api/book/freeze</c> says whether a book is final, with the hash
/// of its <c>book.md</c> and what a freeze would add to the den's history,
/// and <c>POST /api/book/freeze</c> and <c>POST /api/book/unfreeze</c> do
/// it (<see cref="DenTools.FreezeBook"/>, <see cref="DenTools.UnfreezeBook"/>).
/// </summary>
/// <remarks>
/// <para>
/// A POST takes a JSON body, <c>{book, universe, expectedHash}</c>, sent as
/// <c>application/json</c>, as the editing endpoints do; a freeze based on
/// a <c>book.md</c> that has changed since is refused with
/// <c>stale_file</c>. A step in the den's history that fails (the
/// checkpoint, or the tag) isn't an error: the status change stands, and
/// the result's <c>snapshot</c> and <c>message</c> say what happened and
/// what to do. There are no MCP tools for this; it's the writer's.
/// </para>
/// </remarks>
public static class FreezeEndpoints
{
    /// <summary>The paths under <c>/api</c> that answer GET and POST.</summary>
    public static readonly IReadOnlyList<string> GetPostPaths = ["book/freeze"];

    /// <summary>The paths under <c>/api</c> that answer POST only.</summary>
    public static readonly IReadOnlyList<string> PostPaths = ["book/unfreeze"];

    /// <summary>Maps the endpoints onto <paramref name="api"/>, writing through <paramref name="tools"/>, the server's one set of write tools.</summary>
    internal static void Map(RouteGroupBuilder api, DenTools tools)
    {
        api.MapGet("/book/freeze", (HttpContext c) => ReadEndpoints.Answer(
            c, ["book", "universe"], q => tools.GetBookFreeze(q.Text("book") ?? "", q.Text("universe"))));
        api.MapPost("/book/freeze", (Func<HttpContext, Task<IResult>>)(c => Change(c, (book, hash, universe) => tools.FreezeBook(book, hash, universe))));
        api.MapPost("/book/unfreeze", (Func<HttpContext, Task<IResult>>)(c => Change(c, (book, hash, universe) => tools.UnfreezeBook(book, hash, universe))));
    }

    private static async Task<IResult> Change(HttpContext context, Func<string, string, string?, FreezeResult> change)
    {
        if (!context.Request.HasJsonContentType())
        {
            return Results.Json(
                new ApiError("unsupported_media_type", $"'{context.Request.Path}' takes a JSON body: send it with 'Content-Type: application/json'."),
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        try
        {
            if (context.Request.Query.Count > 0)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"'{context.Request.Path}' takes its arguments in the JSON body, not the query.");
            }

            JsonElement json;
            try
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                json = document.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"The body isn't JSON: {e.Message}");
            }

            var body = WriteEndpoints.Body.Of(context, json, ["book", "universe", "expectedHash"]);
            var book = body.OptionalText("book") ?? throw new DenToolException(ToolErrorCodes.BadArgument, "Give 'book': the book's key.");
            var hash = body.OptionalText("expectedHash") ?? throw new DenToolException(
                ToolErrorCodes.BadArgument,
                "Give 'expectedHash': the hash of the book's book.md, from /api/book/freeze. The change is refused if the file has changed since.");
            return Results.Json(change(book, hash, body.OptionalText("universe")));
        }
        catch (DenToolException e)
        {
            return Results.Json(new ApiError(e.Code, e.Message, e.Candidates), statusCode: ReadEndpoints.StatusOf(e.Code));
        }
    }
}
