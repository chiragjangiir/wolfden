using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Wolfden.Api;

/// <summary>
/// The editing endpoints of the web UI (#28; design.md §9): an entity's
/// source for an editor, and edits of its lead, its sections, or its whole
/// file. They're the MCP write tools' code (<see cref="DenTools"/>), so an
/// edit is checked as the den reads it, refused on a stale hash or in a
/// final book, and reports its warnings, effects, and the pinned canon it
/// touches, as the tools do.
/// </summary>
/// <remarks>
/// <para>
/// Edits are <c>POST</c> with a JSON body, in camelCase. A body must be sent
/// as <c>application/json</c>, which a plain HTML form can't send; a script
/// on another site that tries needs the browser's permission first (a CORS
/// preflight), which the server never gives, and the cross-site guard
/// (<see cref="WolfdenApi.CrossSiteGuard"/>) refuses its <c>Origin</c> anyway.
/// Each endpoint takes only its own members, each at most once.
/// </para>
/// <para>
/// Every edit carries <c>expectedHash</c>, the hash of the file it was
/// based on. An edit of a file that changed since is refused with
/// <c>stale_file</c>, and the error holds the file as it is now
/// (<c>current</c>), so the page can show both versions.
/// </para>
/// </remarks>
public static class WriteEndpoints
{
    /// <summary>The paths under <c>/api</c> that answer GET here.</summary>
    public static readonly IReadOnlyList<string> GetPaths = ["entity/source"];

    /// <summary>The paths under <c>/api</c> that answer POST.</summary>
    public static readonly IReadOnlyList<string> PostPaths =
        ["edit/lead", "edit/section", "edit/section/add", "edit/section/rename", "edit/section/remove", "edit/file"];

    /// <summary>
    /// The words the edits' messages use for what to do next. Their errors
    /// and effects are shown to the writer on the page, so they name what a
    /// writer can do (an editor, the search box, <c>wolfden check</c>), not
    /// MCP tools or API parameters as <see cref="ReadEndpoints.Words"/> does.
    /// </summary>
    public static readonly ToolWords Words = ReadEndpoints.Words with
    {
        FindEntity = "use the search box",
        CheckDen = "run 'wolfden check' in the den's folder",
        CheckTool = "run 'wolfden check' in the den's folder",
        CheckPath = "open '{0}' in your editor, or run 'wolfden check' in the den's folder,",
        NameLinks = "An AI assistant working through Wolfden can rename the note and rewrite those links.",
        AliasLinks = "An AI assistant working through Wolfden can change an alias and rewrite those links.",
    };

    /// <summary>Maps the editing endpoints onto <paramref name="api"/>.</summary>
    public static void Map(RouteGroupBuilder api, WolfdenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);

        // One for the server, so its writes are made one at a time.
        var tools = new DenTools(options.DenRoot)
        {
            WatcherWait = options.WatcherWait,
            PinnedUniverse = options.PinnedUniverse,
            Words = WriteEndpoints.Words,

            // Every save records itself in the server's ledger, so the checkpoint
            // dialog marks it "saved in Wolfden" (#30).
            Ledger = options.Ledger,
        };

        api.MapGet("/entity/source", (HttpContext c) => ReadEndpoints.Answer(
            c, ["entity", "universe"], q => tools.GetSource(Entity(q.Text("entity")), q.Text("universe"))));

        MapEdit(api, "/edit/lead", tools, ["text"], (b, entity, universe, hash) =>
            tools.ReplaceLead(entity, b.Text("text"), hash, universe));
        MapEdit(api, "/edit/section", tools, ["index", "name", "text"], (b, entity, universe, hash) =>
            tools.ReplaceSection(entity, b.Int("index"), b.Text("name"), b.Text("text"), hash, universe));
        MapEdit(api, "/edit/section/add", tools, ["name", "text", "beforeIndex", "beforeName"], (b, entity, universe, hash) =>
            tools.AddSectionAt(entity, b.Text("name"), b.RawText("text") ?? "", hash, universe, b.OptionalInt("beforeIndex"), b.OptionalText("beforeName")));
        MapEdit(api, "/edit/section/rename", tools, ["index", "name", "newName"], (b, entity, universe, hash) =>
            tools.RenameSectionAt(entity, b.Int("index"), b.Text("name"), b.Text("newName"), hash, universe));
        MapEdit(api, "/edit/section/remove", tools, ["index", "name"], (b, entity, universe, hash) =>
            tools.RemoveSection(entity, b.Int("index"), b.Text("name"), hash, universe));
        MapEdit(api, "/edit/file", tools, ["text"], (b, entity, universe, hash) =>
            tools.ReplaceFile(entity, b.Text("text"), hash, universe));
    }

    /// <summary>Maps <c>POST</c> on <paramref name="path"/> to an edit (<see cref="Edit"/>).</summary>
    private static void MapEdit(RouteGroupBuilder api, string path, DenTools tools, string[] members, Func<Body, string, string?, string, WriteResult> edit) =>
        api.MapPost(path, (Func<HttpContext, Task<IResult>>)(c => Edit(c, tools, members, edit)));

    private static string Entity(string? entity)
    {
        DenReader.CheckEntityName(entity);
        return entity!;
    }

    /// <summary>
    /// Reads an edit's JSON body (its <c>entity</c>, <c>universe</c>, and
    /// <c>expectedHash</c>, and the members in <paramref name="members"/>),
    /// makes the edit, and answers with its result, or its error. A
    /// <c>stale_file</c> error carries the file as it is now.
    /// </summary>
    private static async Task<IResult> Edit(
        HttpContext context, DenTools tools, string[] members, Func<Body, string, string?, string, WriteResult> edit)
    {
        if (!IsJson(context.Request.ContentType))
        {
            return Results.Json(
                new ApiError("unsupported_media_type", $"'{context.Request.Path}' takes a JSON body: send it with 'Content-Type: application/json'."),
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        string? entity = null;
        string? universe = null;
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

            var body = Body.Of(context, json, ["entity", "universe", "expectedHash", .. members]);
            entity = Entity(body.OptionalText("entity"));
            universe = body.OptionalText("universe");
            var hash = body.OptionalText("expectedHash") ?? throw new DenToolException(
                ToolErrorCodes.BadArgument,
                "Give 'expectedHash': the hash of the file the edit is based on, from /api/entity/source or the last edit's result. " +
                "The edit is refused if the file has changed since.");
            return Results.Json(edit(body, entity, universe, hash));
        }
        catch (DenToolException e) when (e.Code == ToolErrorCodes.StaleFile)
        {
            return Results.Json(new ApiError(e.Code, e.Message, Current: Current(tools, entity!, universe)), statusCode: ReadEndpoints.StatusOf(e.Code));
        }
        catch (DenToolException e)
        {
            return Results.Json(new ApiError(e.Code, e.Message, e.Candidates), statusCode: ReadEndpoints.StatusOf(e.Code));
        }
    }

    /// <summary>The file as it is now, for a <c>stale_file</c> error, or null if it can't be read (it was moved or deleted, or has errors).</summary>
    private static EntitySourceResult? Current(DenTools tools, string entity, string? universe)
    {
        try
        {
            return tools.GetSource(entity, universe);
        }
        catch (DenToolException)
        {
            return null;
        }
    }

    /// <summary>True for <c>application/json</c>, with or without parameters such as a charset.</summary>
    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var type) && type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>An edit's JSON body, checked against the members its endpoint takes.</summary>
    internal sealed class Body
    {
        private readonly Dictionary<string, JsonElement> _members;

        private Body(Dictionary<string, JsonElement> members)
        {
            _members = members;
        }

        /// <summary>The body <paramref name="json"/>, which must be an object holding only <paramref name="members"/>, each once.</summary>
        public static Body Of(HttpContext context, JsonElement json, string[] members)
        {
            if (json.ValueKind != JsonValueKind.Object)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, "The body must be a JSON object.");
            }

            var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var member in json.EnumerateObject())
            {
                if (!members.Contains(member.Name, StringComparer.Ordinal))
                {
                    throw new DenToolException(
                        ToolErrorCodes.BadArgument,
                        $"'{context.Request.Path}' doesn't take '{member.Name}'. It takes: {string.Join(", ", members)}.");
                }

                if (!found.TryAdd(member.Name, member.Value))
                {
                    throw new DenToolException(ToolErrorCodes.BadArgument, $"Give '{member.Name}' once.");
                }
            }

            return new Body(found);
        }

        /// <summary>A text member, as given; refused if it's missing or not a string.</summary>
        public string Text(string name) => OptionalText(name, trim: false)
            ?? throw new DenToolException(ToolErrorCodes.BadArgument, $"Give '{name}'.");

        /// <summary>A text member as given, or null if it's missing or null.</summary>
        public string? RawText(string name) => OptionalText(name, trim: false);

        /// <summary>A text member, or null if it's missing or null. A name-like member is trimmed, and an empty one counts as missing.</summary>
        public string? OptionalText(string name) => OptionalText(name, trim: true);

        private string? OptionalText(string name, bool trim)
        {
            if (!_members.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"'{name}' must be a string.");
            }

            var text = value.GetString()!;
            if (!trim)
            {
                return text;
            }

            text = text.Trim();
            return text.Length == 0 ? null : text;
        }

        /// <summary>A whole-number member; refused if it's missing.</summary>
        public int Int(string name) => OptionalInt(name) ?? throw new DenToolException(ToolErrorCodes.BadArgument, $"Give '{name}'.");

        /// <summary>A whole-number member, or null if it's missing or null.</summary>
        public int? OptionalInt(string name)
        {
            if (!_members.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number
                : throw new DenToolException(ToolErrorCodes.BadArgument, $"'{name}' must be a whole number.");
        }
    }
}
