using Wolfden.Index.Reading;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// <c>search</c> in field values (#179, maintainer decision 2026-10-26):
/// ordinary search finds a field's words, <c>field</c> searches one field,
/// and a link matches its note's name and aliases as well as the text as written.
/// </summary>
public sealed class FieldSearchToolTests : IDisposable
{
    private const string MaraFile = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
    private const string ReachFile = "universes/starfall/entities/locations/kessan-reach.md";

    /// <summary>Kessan Reach's id, so a link written with it holds none of its name.</summary>
    private const string ReachId = "01JADJ8FSSZ5AWSH8VHTPRE95B";

    private readonly TempDen _den = TempDen.Starfall();

    public FieldSearchToolTests()
    {
        // Mara gets a plain home, a list of habits, and a berth that links Kessan Reach by id.
        _den.Write(MaraFile, _den.Read(MaraFile).Replace(
            "  species: { value: human, from: \"ember:prologue\" }",
            $"  species: {{ value: human, from: \"ember:prologue\" }}\n  home: Saltmarsh Docks\n  habits: [chess, \"night watches\"]\n  berth: \"[[{ReachId}]]\"",
            StringComparison.Ordinal));
    }

    public void Dispose() => _den.Dispose();

    private DenTools Tools => new(_den.Root);

    [Fact]
    [Trait("Scenario", "SC-MCP-30")]
    public void OrdinarySearchFindsWordsInFieldValuesAndSaysWhichField()
    {
        var hit = Assert.Single(Tools.Search("Lieutenant", "starfall").Hits);

        Assert.Equal("Mara Voss", hit.Entity.Name);
        Assert.Equal("rank", hit.Field);
        Assert.Null(hit.Section);
        Assert.Equal(10, hit.Line); // The entry's line.
        Assert.Equal("[Lieutenant]", hit.Snippet);
        Assert.True(hit.InCanon);

        // A candidate entry is found, but not as canon.
        var captain = Assert.Single(Tools.Search("captain", "starfall").Hits);
        Assert.Equal("candidate", captain.Status);
        Assert.False(captain.InCanon);
        Assert.Empty(Tools.Search("captain", "starfall", canonOnly: true).Hits);

        // A list's items are its words.
        Assert.Equal("chess, [night] [watches]", Assert.Single(Tools.Search("night watches", "starfall").Hits).Snippet);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-30")]
    public void OneFieldSearchMatchesTheValuesWordsIgnoringCase()
    {
        var hit = Assert.Single(Tools.Search("saltmarsh", "starfall", field: "home").Hits);
        Assert.Equal("[Saltmarsh] Docks", hit.Snippet);
        Assert.Equal("home", hit.Field);

        Assert.Single(Tools.Search("DOCKS saltm*", "starfall", field: "home").Hits);
        Assert.Empty(Tools.Search("saltmarsh harbour", "starfall", field: "home").Hits);
        Assert.Empty(Tools.Search("saltmarsh", "starfall", field: "rank").Hits);

        // Only that field: Mara's name and sections hold "Voss" and "Tessaly", her home doesn't.
        Assert.Empty(Tools.Search("Voss", "starfall", field: "home").Hits);
        Assert.All(Tools.Search("tessaly", "starfall", field: "posting").Hits, h => Assert.Equal("posting", h.Field));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-30")]
    public void ALinkMatchesItsNotesNameAndAliasesAndTheTextAsWritten()
    {
        // The berth is written by id: the name and the alias find it, and the snippet says which.
        var byName = Assert.Single(Tools.Search("kessan reach", "starfall", field: "berth").Hits);
        Assert.Equal($"[[{ReachId}]] → [Kessan] [Reach]", byName.Snippet);
        var byAlias = Assert.Single(Tools.Search("the reach", "starfall", field: "berth").Hits);
        Assert.Equal($"[[{ReachId}]] → [the] [Reach]", byAlias.Snippet);
        Assert.Single(Tools.Search(ReachId, "starfall", field: "berth").Hits);

        // A posting written by slug matches as written and by name.
        Assert.Contains(Tools.Search("kessan", "starfall", field: "posting").Hits, h => h.Snippet.StartsWith("[[[kessan]-reach]]", StringComparison.Ordinal));

        // Ordinary search finds the berth by name too.
        Assert.Contains(Tools.Search("Kessan", "starfall").Hits, h => h.Field == "berth");

        // After a rename, the new name matches without the linking note changing.
        _den.Write(ReachFile, _den.Read(ReachFile).Replace("name: Kessan Reach", "name: Old Kessan", StringComparison.Ordinal));
        Assert.Equal($"[[{ReachId}]] → [Old] [Kessan]", Assert.Single(Tools.Search("old kessan", "starfall", field: "berth").Hits).Snippet);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-30")]
    public void AFieldNoNoteHasIsABadArgument()
    {
        var e = Assert.Throws<DenToolException>(() => Tools.Search("saltmarsh", "starfall", field: "hoem"));
        Assert.Equal(ToolErrorCodes.BadArgument, e.Code);
        Assert.Contains("'hoem'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-30")]
    public void TheToolsJsonNamesTheFieldAndKeepsBracketedSnippets()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Tools.Search("saltmarsh", "starfall", field: "home"), ToolJson.Options);

        Assert.Contains("\"field\":\"home\"", json, StringComparison.Ordinal);
        Assert.Contains("\"snippet\":\"[Saltmarsh] Docks\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("matches", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\"", json, StringComparison.Ordinal);
    }
}
