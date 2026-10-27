using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wolfden.Mcp;

// The JSON shapes the MCP tools return (design.md §8). Names are snake_case,
// like the tools' arguments (as_of), and null members are left out, so a
// model reads only what applies. Every result carries a version for its
// shape: within a version, members may be added, but never renamed, removed,
// or given a new meaning.
//
// In every result, `count` is how many items this result holds (hits,
// entities, or diagnostics), and `truncated` is true when more matched than
// `limit` let through. Where the tool knows how many matched in all
// (list_entities, check), `total` says so.

/// <summary>The JSON settings for tool results.</summary>
public static class ToolJson
{
    /// <summary>The shape version every result carries.</summary>
    public const int Version = 1;

    /// <summary>Compact snake_case JSON, with nulls left out and text kept readable.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keep names and quotes readable ("Székely", 'x'). The JSON never goes into HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>A failed tool call: a stable code and a message that says what to do next.</summary>
/// <param name="Version">The shape version.</param>
/// <param name="Error">The error.</param>
public sealed record ToolErrorResult(int Version, ToolError Error);

/// <summary>An error.</summary>
/// <param name="Code">A stable code, one of <see cref="ToolErrorCodes"/>.</param>
/// <param name="Message">What went wrong, for the model and the writer.</param>
/// <param name="Candidates">For <c>ambiguous_entity</c>, the entities the name matches.</param>
public sealed record ToolError(string Code, string Message, IReadOnlyList<EntitySummary>? Candidates = null);

/// <summary>Tool errors as results.</summary>
public static class ToolErrors
{
    /// <summary>The error as a result.</summary>
    public static ToolErrorResult ToResult(this DenToolException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(ToolJson.Version, new ToolError(e.Code, e.Message, e.Candidates));
    }
}
