namespace Wolfden.Mcp.Tests;

/// <summary>Each way a tool call can go wrong has its own code and a message that says what to do next.</summary>
public sealed class ToolErrorTests : IClassFixture<DraculaToolTests.Den>
{
    private readonly DenTools _tools;

    public ToolErrorTests(DraculaToolTests.Den den)
    {
        _tools = den.Tools;
    }

    [Theory]
    [Trait("Scenario", "SC-MCP-6")]
    [InlineData("dracula:99", "has no chapter '99'")]
    [InlineData("ember:1", "has no book 'ember'")]
    [InlineData("dracula:14.2", "scenes are not supported yet")]
    [InlineData("Dracula 5", "invalid story position")]
    public void ABadPositionIsBadPosition(string position, string message)
    {
        var canon = Assert.Throws<DenToolException>(() => _tools.CanonAsOf(position: position));
        Assert.Equal(ToolErrorCodes.BadPosition, canon.Code);
        Assert.Contains(message, canon.Message, StringComparison.Ordinal);
        Assert.Contains("Books of universe 'dracula' in story order: dracula.", canon.Message, StringComparison.Ordinal);

        var entity = Assert.Throws<DenToolException>(() => _tools.GetEntity("Mina", asOf: position));
        Assert.Equal(ToolErrorCodes.BadPosition, entity.Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-6")]
    public void AnUnknownEntityIsUnknownEntity()
    {
        var e = Assert.Throws<DenToolException>(() => _tools.GetEntity("Sherlock Holmes"));
        Assert.Equal(ToolErrorCodes.UnknownEntity, e.Code);
        Assert.Contains("search or list_entities", e.Message, StringComparison.Ordinal);

        // Even with a position: it doesn't exist, which isn't the same as not met yet.
        Assert.Equal(ToolErrorCodes.UnknownEntity, Assert.Throws<DenToolException>(() => _tools.GetEntity("Sherlock Holmes", asOf: "dracula:3")).Code);
    }

    [Theory]
    [Trait("Scenario", "SC-MCP-6")]
    [InlineData("universe")]
    [InlineData("series")]
    [InlineData("book")]
    [InlineData("type")]
    [InlineData("status")]
    [InlineData("limit")]
    [InlineData("both")]
    public void AnArgumentThatNamesNothingSaysSo(string which)
    {
        var e = Assert.Throws<DenToolException>(() => which switch
        {
            "universe" => (object)_tools.CanonAsOf(universe: "middle-earth"),
            "series" => _tools.CanonAsOf(series: "dracula"),
            "book" => _tools.ListEntities(book: "carmilla"),
            "type" => _tools.CanonAsOf(type: "spaceship"),
            "status" => _tools.ListEntities(status: "maybe"),
            "limit" => _tools.Search("Mina", limit: 0),
            _ => _tools.CanonAsOf(series: "a", book: "b"),
        });

        Assert.Equal(
            which switch
            {
                "universe" => ToolErrorCodes.UnknownUniverse,
                "series" or "book" => ToolErrorCodes.UnknownScope,
                "type" => ToolErrorCodes.UnknownType,
                _ => ToolErrorCodes.BadArgument,
            },
            e.Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-6")]
    public void AnEmptyQueryOrNameIsABadArgument()
    {
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => _tools.Search("  ")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => _tools.GetEntity("")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => _tools.Check(severity: "fatal")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-6")]
    public void AnErrorResultCarriesItsCodeAsJson()
    {
        var result = WolfdenTools.Respond<object>(() => _tools.CanonAsOf(position: "dracula:99"));

        Assert.True(result.IsError);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.StartsWith("{\"version\":1,\"error\":{\"code\":\"bad_position\",\"message\":", text, StringComparison.Ordinal);
    }
}

/// <summary>Errors that need a den other than Dracula.</summary>
public sealed class OtherDenErrorTests
{
    [Fact]
    [Trait("Scenario", "SC-MCP-6")]
    public void ANameThatMatchesTwoEntitiesListsThemWithoutSpoilingEarlierViews()
    {
        using var den = TempDen.Dracula();
        // A second Lucy Westenra, met only at chapter 20.
        den.Write(
            "universes/dracula/entities/characters/lucy-westenra.md",
            """
            ---
            type: character
            name: Lucy Westenra
            status: canon
            from: "dracula:20"
            ---

            # Lucy Westenra

            A namesake.
            """);
        var tools = new DenTools(den.Root);

        var e = Assert.Throws<DenToolException>(() => tools.GetEntity("Lucy Westenra"));
        Assert.Equal(ToolErrorCodes.AmbiguousEntity, e.Code);
        Assert.Equal(2, e.Candidates!.Count);

        // At chapter 5 the reader has met only one of them, so that's the one.
        var early = tools.GetEntity("Lucy Westenra", asOf: "dracula:5");
        Assert.Equal("01M4TF5F08GDVZVWZ4JYQDBHZG", early.Entity!.Id);

        // At chapter 20, both: the error lists both.
        var late = Assert.Throws<DenToolException>(() => tools.GetEntity("Lucy Westenra", asOf: "dracula:20"));
        Assert.Equal(2, late.Candidates!.Count);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-6")]
    public void ADenWithSeveralUniversesNeedsOneNamed()
    {
        using var den = TempDen.Starfall();
        var tools = new DenTools(den.Root);

        var e = Assert.Throws<DenToolException>(() => tools.CanonAsOf());
        Assert.Equal(ToolErrorCodes.UniverseRequired, e.Code);
        Assert.Contains("drift, starfall", e.Message, StringComparison.Ordinal);
        Assert.NotEmpty(tools.CanonAsOf(universe: "starfall").Entities);

        // Listing and searching the whole den need no universe.
        Assert.Contains(tools.ListEntities().Entities, x => x.Universe == "starfall");
        Assert.Empty(tools.ListEntities(universe: "drift").Entities);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-9")]
    public void ADenWhoseSettingsDontLoadIsDenNotLoadedButCheckSaysWhy()
    {
        using var den = TempDen.Dracula();
        den.Write("den.yaml", "format: 1\nname: [broken\n");
        var tools = new DenTools(den.Root);

        foreach (var call in new Func<object>[]
        {
            () => tools.CanonAsOf(), () => tools.GetEntity("Mina"), () => tools.ListEntities(), () => tools.Search("Mina"),
        })
        {
            var e = Assert.Throws<DenToolException>(call);
            Assert.Equal(ToolErrorCodes.DenNotLoaded, e.Code);
            Assert.Contains("check tool", e.Message, StringComparison.Ordinal);
        }

        var check = tools.Check();
        Assert.False(check.Loaded);
        Assert.Null(check.SpoilerChecks);
        Assert.True(check.Errors > 0);
        Assert.Contains(check.Diagnostics, d => d.Path == "den.yaml" && d.Severity == "error");

        // Fixed, the same tools work again.
        den.Write("den.yaml", "format: 1\nname: Dracula\n");
        Assert.NotEmpty(tools.CanonAsOf().Entities);
    }
}
