using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Wolfden.Api;

/// <summary>
/// Creating notes from the web UI (#29; design.md §9): <c>GET /api/types</c>
/// lists the den's enabled types, and <c>POST /api/entity/create</c> makes a
/// new entity from its type's template (<see cref="DenTools.CreateEntity"/>,
/// the MCP tool <c>create_entity</c>'s code).
/// </summary>
/// <remarks>
/// <para>
/// A create takes a JSON body, <c>{type, name, universe, series, book, from, allowRelinks}</c>,
/// sent as <c>application/json</c>, as the editing endpoints do. It writes
/// through the server's one set of write tools, so it runs one write at a
/// time and lands in the server's checkpoint ledger. It's refused in a final
/// book (<c>frozen</c>), with a type no enabled pack defines
/// (<c>unknown_type</c>), and, on a server pinned to a universe, in any
/// other. A name that would take links from the entity they reach now is
/// refused (<c>bad_argument</c>, naming the links) unless
/// <c>allowRelinks</c> is true, as for a copy (maintainer decision on #29,
/// 2026-10-28). The answer is the write's result: the new file's path, and the
/// entity, whose key the page navigates to.
/// </para>
/// <para>
/// There's no slug, status, or field values here: the form asks only for the
/// type, the name, and where it goes (maintainer decision on #29,
/// 2026-10-28), and the writer fills in the rest on the new note's page.
/// </para>
/// </remarks>
public static class CreateEndpoints
{
    /// <summary>The paths under <c>/api</c> that answer GET.</summary>
    public static readonly IReadOnlyList<string> GetPaths = ["types"];

    /// <summary>The paths under <c>/api</c> that answer POST.</summary>
    public static readonly IReadOnlyList<string> PostPaths = ["entity/create"];

    private static readonly string[] Members = ["type", "name", "universe", "series", "book", "from", "allowRelinks"];

    /// <summary>Maps the endpoints onto <paramref name="api"/>, writing through <paramref name="tools"/>, the server's one set of write tools.</summary>
    internal static void Map(RouteGroupBuilder api, DenTools tools)
    {
        api.MapGet("/types", (HttpContext c) => ReadEndpoints.Answer(c, [], _ => tools.ListTypes()));
        api.MapPost("/entity/create", (Func<HttpContext, Task<IResult>>)(c => Create(c, tools)));
    }

    private static async Task<IResult> Create(HttpContext context, DenTools tools)
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

            var body = WriteEndpoints.Body.Of(context, json, Members);
            var type = body.OptionalText("type") ?? throw new DenToolException(ToolErrorCodes.BadArgument, "Give 'type': the type's key, such as 'character'.");
            var name = body.OptionalText("name") ?? throw new DenToolException(ToolErrorCodes.BadArgument, "Give the new note a name.");
            var series = body.OptionalText("series");
            var book = body.OptionalText("book");
            if (series is not null && book is not null)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, "Give 'series' or 'book', not both: a note goes in one place.");
            }

            var result = tools.CreateEntity(
                type, name, body.OptionalText("universe"), series, book, from: body.OptionalText("from"), allowRelinks: body.OptionalBool("allowRelinks") ?? false);
            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        }
        catch (DenToolException e)
        {
            return Results.Json(new ApiError(e.Code, e.Message, e.Candidates, Relinks: e.Relinks), statusCode: ReadEndpoints.StatusOf(e.Code));
        }
    }
}
