using Wolfden.Core.Links;

namespace Wolfden.Core.Tests;

/// <summary>
/// Small invented dens for the link tests: Starfall, with a series of two
/// books (ember, ash) and a standalone book (a-quiet-moon), and a second
/// universe, Drift.
/// </summary>
internal static class LinkDens
{
    public const string Starfall = "universes/starfall";
    public const string LongDark = Starfall + "/series/long-dark";
    public const string Ember = LongDark + "/books/ember";
    public const string Ash = LongDark + "/books/ash";
    public const string Moon = Starfall + "/books/a-quiet-moon";
    public const string Drift = "universes/drift";

    public static MemoryDenSource Den(bool spoilerChecks = true, string denYaml = "") =>
        new MemoryDenSource()
            .With("den.yaml", $"format: 1\nspoiler_checks: {(spoilerChecks ? "true" : "false")}\n{denYaml}")
            .Universe("starfall", "order: [long-dark, a-quiet-moon]")
            .Series("starfall", "long-dark", "books: [ember, ash]")
            .Book("starfall", "long-dark", "ember", "chapters: [prologue, 1, 2, 3]")
            .Book("starfall", "long-dark", "ash", "chapters: [1, 2, 3]")
            .Book("starfall", null, "a-quiet-moon", "chapters: [1, 2]")
            .Universe("drift")
            .Book("drift", null, "tide", "chapters: [1]");

    /// <summary>Adds an entity file at <c>{container}/entities/{folder}/{slug}.md</c>.</summary>
    public static MemoryDenSource Entity(this MemoryDenSource source, string container, string slug, string frontmatter, string body = "", string folder = "characters") =>
        source.With($"{container}/entities/{folder}/{slug}.md", $"---\n{frontmatter}\n---\n{body}");

    /// <summary>Frontmatter for a canon character, plus any extra lines.</summary>
    public static string Character(string name, params string[] extra) =>
        string.Join("\n", new[] { "type: character", $"name: {name}" }
            .Concat(extra.Any(e => e.StartsWith("status:", StringComparison.Ordinal)) ? [] : ["status: canon"])
            .Concat(extra));

    /// <summary>The link with this text in the file at <paramref name="path"/>.</summary>
    public static ResolvedLink Link(this LinkIndex index, string path, string text) =>
        Assert.Single(index.Links, l => l.Occurrence.Path == path && l.Occurrence.Link.Text == text);

    /// <summary>The link diagnostics as (path, rule, line).</summary>
    public static (string Path, string Rule, int? Line)[] Rules(this LinkIndex index) =>
        index.Diagnostics.Select(d => (d.Path, d.Diagnostic.RuleId, d.Diagnostic.Line)).ToArray();
}
