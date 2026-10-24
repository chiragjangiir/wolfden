using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wolfden.Mcp;

/// <summary>
/// The MCP tool surface (design.md §8): each method is one tool, with a
/// description written for an AI client. The work is done by
/// <see cref="DenTools"/>; these methods turn its results into JSON, and its
/// errors into error results with a code and a message.
/// </summary>
/// <remarks>
/// Argument names are snake_case, as MCP tools' usually are, so the C#
/// parameter names are too.
/// </remarks>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "MCP argument names are snake_case.")]
public sealed class WolfdenTools
{
    private const string PositionHelp =
        "A story position is 'book:chapter' using the keys from the den (such as 'dracula:5'), or a bare book key for the start of that book. " +
        "Positions follow reading order across the whole universe.";

    private const string UniverseHelp =
        "The universe (its key). A den with one universe uses it; in a den with several, name one, unless a story position " +
        "names a book only one universe has.";

    private readonly DenTools _tools;

    /// <summary>The tools over <paramref name="tools"/>' den.</summary>
    public WolfdenTools(DenTools tools)
    {
        _tools = tools;
    }

    /// <summary>The <c>search</c> tool.</summary>
    [McpServerTool(Name = "search", Title = "Search the den", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Full-text search over the den's notes: entity names, aliases, leads, and section text. Words match whole, in any order, " +
        "ignoring case and accents; end a word with * to match it as a prefix. Results include every status (ideas, undecided " +
        "questions, retconned material) unless canon_only is true, and each hit says its status and whether it's canon text. " +
        "Search has no story position, so it can show material the reader meets later: use canon_as_of or get_entity with as_of " +
        "for spoiler-safe reading.")]
    public CallToolResult Search(
        [Description("The words to search for.")] string query,
        [Description(UniverseHelp + " Here '*' searches every universe.")] string? universe = null,
        [Description("Only entities of this type, such as 'character' or 'location'.")] string? type = null,
        [Description("If true, only canon text: draft or canon material, not ideas, retconned material, or bookkeeping sections.")] bool canon_only = false,
        [Description("The most hits to return, 1 to 200.")] int limit = 20) =>
        Respond(() => _tools.Search(query, universe, type, canon_only, limit));

    /// <summary>The <c>get_entity</c> tool.</summary>
    [McpServerTool(Name = "get_entity", Title = "Read one entity", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Reads one entity (a character, location, event, item, faction, thread, note, or other type) with its fields and sections. " +
        "Name it as a link would: by name, alias, file name, or id; or by its key or path. Pinned facts (the hard canon to check " +
        "before writing) come first, under 'pinned', and are also listed in place. Without as_of you get the author's full view: " +
        "every field entry and section, whatever its status (idea, undecided, candidate, draft, canon, retconned), with the " +
        "positions where each one starts and ends. With as_of you get only what's canon at that point in the story, as a reader " +
        "would know it then: the name is matched only among the entities the reader has met by then, " +
        "and if the reader hasn't met the entity yet, in_view is false and nothing about it is returned. " +
        PositionHelp)]
    public CallToolResult GetEntity(
        [Description("The entity: its name, an alias, its file name, its id, its key, or its path.")] string entity,
        [Description(UniverseHelp + " With as_of, the position's book settles it.")] string? universe = null,
        [Description("A story position, such as 'dracula:5', to read the entity as canon stands there. Leave it out for the full view.")] string? as_of = null) =>
        Respond(() => _tools.GetEntity(entity, universe, as_of));

    /// <summary>The <c>list_entities</c> tool.</summary>
    [McpServerTool(Name = "list_entities", Title = "List entities", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Lists entities as one-line summaries (key, name, type, status, where it's filed, and where it enters canon), of every " +
        "status unless status is given. A den holds universes; a universe holds series of books and standalone books. Each entity " +
        "is filed at one scope: the universe, a series, or a book. A series or book filter keeps the entities of that series or " +
        "book, of the scopes inside it, and of the scopes that hold it (as canon_as_of's filter does). Use get_entity to read one.")]
    public CallToolResult ListEntities(
        [Description(UniverseHelp + " Here '*' lists every universe (not with series or book).")] string? universe = null,
        [Description("Only this type, such as 'character'.")] string? type = null,
        [Description("Only this status: idea, undecided, candidate, draft, canon, or retconned.")] string? status = null,
        [Description("Only this series (its key) and what it holds.")] string? series = null,
        [Description("Only this book (its key) and the scopes that hold it.")] string? book = null,
        [Description("The most entities to return, 1 to 1000.")] int limit = 200) =>
        Respond(() => _tools.ListEntities(universe, type, status, series, book, limit));

    /// <summary>The <c>canon_as_of</c> tool.</summary>
    [McpServerTool(Name = "canon_as_of", Title = "Canon as of a story position", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "What's canon at a point in the story: every entity the reader has met by then, at any scope of the universe, with the " +
        "field values and sections in effect there. Ideas, undecided questions, retconned material, and bookkeeping sections are " +
        "left out, and draft material is marked draft. Nothing the reader meets later is included, so it's safe for drafting a " +
        "chapter without spoilers. Pinned canon (the hard facts) comes first, under 'pinned', and is also listed in place. " +
        "Without position, the view is the end of the story: all canon so far. " + PositionHelp)]
    public CallToolResult CanonAsOf(
        [Description("A story position, such as 'dracula:5'. Leave it out for the end of the story.")] string? position = null,
        [Description(UniverseHelp + " With position, the position's book settles it.")] string? universe = null,
        [Description("Only entities filed in this series, its books, or the universe.")] string? series = null,
        [Description("Only entities filed in this book, its series, or the universe.")] string? book = null,
        [Description("Only entities of this type, such as 'character'.")] string? type = null,
        [Description("If true, list the entities without their fields and sections (pinned canon is still given in full).")] bool brief = false) =>
        Respond(() => _tools.CanonAsOf(universe, position, series, book, type, brief));

    /// <summary>The <c>check</c> tool.</summary>
    [McpServerTool(Name = "check", Title = "Check the den", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Runs Wolfden's continuity checks on the den's files, as 'wolfden check' does, and returns the findings: broken or " +
        "ambiguous links, positions that don't resolve, missing required fields or sections, overlapping values, duplicate ids, " +
        "and (when the den turns them on) spoiler checks. Each finding has a path, a line, a severity (error or warning), a rule " +
        "id from Wolfden's format spec, and a message. Errors keep a file out of the index; warnings don't. It never changes a file. " +
        "Unlike the other tools it checks the whole den by default, every universe included, since it validates files rather than " +
        "a story view; give a universe to check only that universe's files and the den-level ones (den.yaml and packs). Ids are " +
        "still compared with the other universes' notes, so a duplicate id across universes is found; if that comparison couldn't " +
        "run, 'note' says why.")]
    public CallToolResult Check(
        [Description("Only 'error' or only 'warning'.")] string? severity = null,
        [Description("Only files under this path, relative to the den root, such as 'universes/dracula/books'.")] string? path = null,
        [Description("The most findings to return, 1 to 5000. The counts cover everything checked.")] int limit = 200,
        [Description("Check only this universe (its key) and the den-level files. Leave it out, or give '*', for the whole den.")] string? universe = null) =>
        Respond(() => _tools.Check(severity, path, limit, universe));

    /// <summary>The <c>get_history</c> tool.</summary>
    [McpServerTool(Name = "get_history", Title = "An entity's history", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "The version history of one entity's file: the checkpoints (git commits) that changed it, newest first, each with its id, " +
        "time, author, message, and the files it changed. Name the entity as for get_entity. Results come a page at a time: when " +
        "'truncated' is true, ask again with before set to 'next_before' for older checkpoints. With diff true, the result also " +
        "has what changed in the file since the latest checkpoint (edits not yet checkpointed); with diff_from set to a checkpoint's " +
        "id, what changed since that checkpoint. The history follows the file's current path, so it starts where the file was last " +
        "moved or renamed. If the den has no history (it isn't in a git repository, or git isn't installed), the result isn't an " +
        "error: 'available' is false and 'reason' says why. History isn't a story view, so it can show material the reader meets later.")]
    public CallToolResult GetHistory(
        [Description("The entity: its name, an alias, its file name, its id, its key, or its path.")] string entity,
        [Description(UniverseHelp)] string? universe = null,
        [Description("The most checkpoints to return, 1 to 100.")] int limit = 20,
        [Description("Only checkpoints older than this one (an id from an earlier result's 'next_before'), for the next page.")] string? before = null,
        [Description("If true, also show what changed in the file since the latest checkpoint.")] bool diff = false,
        [Description("Show what changed in the file since this checkpoint (an id from the history), up to the file as it is now.")] string? diff_from = null) =>
        Respond(() => _tools.GetHistory(entity, universe, limit, before, diff, diff_from));

    /// <summary>
    /// An error result in Wolfden's shape for one the SDK made itself. Every
    /// failure inside a tool comes back from <see cref="Respond{T}"/> as JSON
    /// with a code, so an error result that isn't is the SDK's own: the
    /// arguments didn't bind to the tool's parameters (a missing required
    /// argument, or a value of the wrong type). It becomes <c>bad_argument</c>,
    /// keeping the SDK's message. Any other result passes through.
    /// </summary>
    internal static CallToolResult AsBadArgument(CallToolResult result, string? tool)
    {
        if (result.IsError != true || IsWolfdenError(result))
        {
            return result;
        }

        return BadArgument(tool, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));

        static bool IsWolfdenError(CallToolResult result)
        {
            if (result.Content is not [TextContentBlock { Text: var text }])
            {
                return false;
            }

            try
            {
                using var json = JsonDocument.Parse(text);
                return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>A <c>bad_argument</c> error result for arguments that didn't bind, keeping what the SDK said about them.</summary>
    internal static CallToolResult BadArgument(string? tool, string? detail)
    {
        detail = detail?.Trim() ?? "";
        return Respond<object>(() => throw new DenToolException(
            ToolErrorCodes.BadArgument,
            $"The arguments for '{tool}' don't fit its input schema: check that each required argument is given and each has the type the schema " +
            $"says (text, number, or true/false).{(detail.Length == 0 ? "" : $" The server said: {detail}")}"));
    }

    /// <summary>Runs a tool and returns its result as JSON, or its error as an error result.</summary>
    internal static CallToolResult Respond<T>(Func<T> run)
    {
        object result;
        var isError = false;
        try
        {
            result = run()!;
        }
        catch (DenToolException e)
        {
            result = e.ToResult();
            isError = true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result = new ToolErrorResult(
                ToolJson.Version,
                new ToolError(ToolErrorCodes.Internal, $"Unexpected failure ({e.GetType().Name}: {e.Message}). This is a bug in Wolfden; please report it."));
            isError = true;
        }

        var json = JsonSerializer.SerializeToElement(result, result.GetType(), ToolJson.Options);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json.GetRawText() }],
            StructuredContent = json,
            IsError = isError,
        };
    }
}
