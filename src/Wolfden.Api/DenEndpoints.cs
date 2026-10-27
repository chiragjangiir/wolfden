using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Wolfden.Index;

namespace Wolfden.Api;

/// <summary>Wolfden's own health, from <c>GET /api/health</c>.</summary>
/// <param name="Status">Always <c>ok</c>: the server is up.</param>
/// <param name="Wolfden">Wolfden's version.</param>
public sealed record HealthResult(string Status, string Wolfden);

/// <summary>The den and its universes, from <c>GET /api/den</c>.</summary>
/// <param name="Version">The shape of this result, as for <c>--json</c> (design.md §7).</param>
/// <param name="Name">The den's display name (format.md F-DEN-2).</param>
/// <param name="PinnedUniverse">The universe the server is pinned to, or null.</param>
/// <param name="Universes">The universes served, by key.</param>
/// <param name="RemoteImages">
/// True if <c>den.yaml</c> lets the UI load images from websites in notes
/// (<c>remote_images: true</c>, format.md F-DEN-8); by default it doesn't (#180).
/// </param>
public sealed record DenResult(int Version, string Name, string? PinnedUniverse, IReadOnlyList<UniverseResult> Universes, bool RemoteImages);

/// <summary>One universe in <see cref="DenResult"/>.</summary>
/// <param name="Key">Its key (its folder name).</param>
/// <param name="Name">Its display name.</param>
/// <param name="Id">Its id, or null if it has none yet.</param>
/// <param name="Series">How many series it holds.</param>
/// <param name="Books">How many books it holds, in series or standalone.</param>
/// <param name="Entities">How many entities are filed in it, at any scope.</param>
public sealed record UniverseResult(string Key, string Name, string? Id, int Series, int Books, int Entities);

/// <summary>
/// An error from the API: a stable <paramref name="Code"/>, the same codes
/// the MCP tools use where they mean the same thing (design.md §8), and a
/// message that says what to do next.
/// </summary>
/// <param name="Code">The code.</param>
/// <param name="Message">What went wrong, and what to do next.</param>
/// <param name="Candidates">For <c>ambiguous_entity</c>, the entities the name matches; left out otherwise.</param>
public sealed record ApiError(
    string Code, string Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<EntitySummary>? Candidates = null);

/// <summary>
/// The API's endpoints, under <c>/api</c> (#23). They read the index the
/// way the MCP tools do: each request first brings it up to date with the
/// files (<see cref="IndexBuilder.EnsureUpToDate(string, TimeSpan)"/>), so an
/// answer never lags behind an edit.
/// </summary>
public static class DenEndpoints
{
    /// <summary>The shape of <see cref="DenResult"/>.</summary>
    public const int DenResultVersion = 1;

    /// <summary>
    /// Maps <c>GET /api/health</c>, <c>GET /api/den</c>, the read endpoints
    /// (<see cref="ReadEndpoints"/>), and a JSON 404 for anything else under <c>/api</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapWolfdenApi(this IEndpointRouteBuilder endpoints, WolfdenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        var api = endpoints.MapGroup("/api");
        api.MapGet("/health", () => new HealthResult("ok", options.Version));
        api.MapGet("/den", () => Den(options));
        ReadEndpoints.Map(api, options);
        CheckpointEndpoints.Map(api, options);

        // Anything else under /api: a known path with the wrong method, or no endpoint at all.
        api.Map("/{**rest}", (HttpContext context, string? rest) =>
        {
            if (CheckpointEndpoints.Paths.Contains(rest?.TrimEnd('/') ?? "", StringComparer.OrdinalIgnoreCase))
            {
                context.Response.Headers.Allow = "GET, POST";
                return Error(
                    StatusCodes.Status405MethodNotAllowed,
                    "method_not_allowed",
                    $"'/api/{rest}' answers GET and POST, not {context.Request.Method}.");
            }

            if (GetPaths.Contains(rest?.TrimEnd('/') ?? "", StringComparer.OrdinalIgnoreCase))
            {
                context.Response.Headers.Allow = "GET";
                return Error(
                    StatusCodes.Status405MethodNotAllowed,
                    "method_not_allowed",
                    $"'/api/{rest}' answers GET, not {context.Request.Method}.");
            }

            return Error(StatusCodes.Status404NotFound, "not_found", $"There's no API endpoint '/api/{rest}'.");
        });
        return endpoints;
    }

    /// <summary>The paths under <c>/api</c> that answer GET.</summary>
    private static readonly string[] GetPaths = ["health", "den", .. ReadEndpoints.Paths];

    /// <summary>The den's name and its universes, or the pinned one.</summary>
    internal static IResult Den(WolfdenApiOptions options)
    {
        try
        {
            IndexBuilder.EnsureUpToDate(options.DenRoot, options.WatcherWait);
            using var index = DenIndex.Open(options.DenRoot);
            var db = index.Context;
            var den = db.Dens.Single();
            if (!den.IsLoaded)
            {
                return Error(
                    StatusCodes.Status503ServiceUnavailable,
                    "den_not_loaded",
                    "den.yaml doesn't load, so nothing in the den can be read. Run 'wolfden check' to see why.");
            }

            var containers = db.Containers.ToList();
            var entities = db.Entities.GroupBy(e => e.UniverseId).Select(g => new { UniverseId = g.Key, Count = g.Count() })
                .ToDictionary(g => g.UniverseId, g => g.Count);
            var universes = containers
                .Where(c => c.Kind == "universe" && (options.PinnedUniverse is null || c.Key == options.PinnedUniverse))
                .OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(u => new UniverseResult(
                    u.Key,
                    u.Name,
                    u.Ulid,
                    containers.Count(c => c.UniverseId == u.Id && c.Kind == "series"),
                    containers.Count(c => c.UniverseId == u.Id && c.Kind == "book"),
                    entities.GetValueOrDefault(u.Id)))
                .ToList();
            return Results.Json(new DenResult(DenResultVersion, den.Name, options.PinnedUniverse, universes, WolfdenApi.RemoteImages(options.DenRoot)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "den_unreadable", $"Couldn't read or rebuild the den's index: {e.Message}");
        }
    }

    private static IResult Error(int status, string code, string message) => Results.Json(new ApiError(code, message), statusCode: status);
}
