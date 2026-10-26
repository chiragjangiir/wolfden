using System.Globalization;

namespace Wolfden.Index.Reading;

/// <summary>
/// The words the MCP tools and <see cref="DenReader"/> use when their errors
/// and effects advise what to do next: the names of parameters and of the
/// tools to call. An MCP client gets the tools' own (<see cref="Mcp"/>); a
/// command line that runs the same tools, such as <c>wolfden copy</c>, gives
/// its options and commands instead, and the web API its own words, so their
/// messages never name a parameter they don't have.
/// </summary>
/// <param name="AllowRelinks">How to make a change that relinks anyway, after "or": <c>pass allow_relinks: true</c>.</param>
/// <param name="NewName">Where a new name goes, after "Give the copy's name": <c>in 'new_name'</c>.</param>
/// <param name="Slug">What sets a file name: <c>slug</c>.</param>
/// <param name="Universe">Where a universe key goes, after "Give its key": <c>in 'universe'</c>.</param>
/// <param name="FindEntity">How to find an entity, lowercase: <c>use search or list_entities</c>.</param>
/// <param name="CheckDen">How to see more of what check reports, lowercase: <c>call check</c>.</param>
/// <param name="CheckTool">How to see why the den doesn't load, lowercase: <c>call the check tool</c>.</param>
/// <param name="CheckPath">How to see one file's errors, lowercase, with <c>{0}</c> for its path: <c>call check with path '{0}'</c>.</param>
/// <param name="PinnedTo">
/// What pins every read to one universe, with <c>{0}</c> for its key, to
/// start a sentence: <c>This session is pinned to universe '{0}' (wolfden mcp --universe)</c>.
/// </param>
public sealed record ToolWords(
    string AllowRelinks, string NewName, string Slug, string Universe, string FindEntity, string CheckDen, string CheckTool, string CheckPath,
    string PinnedTo = "This session is pinned to universe '{0}' (wolfden mcp --universe)")
{
    /// <summary>The MCP tools' own words, the default.</summary>
    public static ToolWords Mcp { get; } = new(
        "pass allow_relinks: true", "in 'new_name'", "slug", "in 'universe'", "use search or list_entities", "call check", "call the check tool",
        "call check with path '{0}'");

    /// <summary><see cref="CheckPath"/> for <paramref name="path"/>.</summary>
    public string CheckFile(string path) => string.Format(CultureInfo.InvariantCulture, CheckPath, path);

    /// <summary><see cref="PinnedTo"/> for the universe <paramref name="universe"/>.</summary>
    public string Pinned(string universe) => string.Format(CultureInfo.InvariantCulture, PinnedTo, universe);

    /// <summary><paramref name="words"/> with its first letter capitalized, to start a sentence.</summary>
    public static string Cap(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
}
