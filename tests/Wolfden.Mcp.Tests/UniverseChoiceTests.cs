using System.Text.Json;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// How a call chooses its universe in a den with several (maintainer
/// decision, 2026-10-23): it names one, or a position's book settles it;
/// '*' is for search and list_entities only; a pinned session reads only its
/// own. Starfall has two universes, starfall and drift; these tests give
/// drift a Lantern of its own, a book whose key Starfall also has, and a
/// broken link, so a check finds something in each universe.
/// </summary>
public sealed class UniverseChoiceTests : IDisposable
{
    private const string AshLantern = "01JA7Q8M4XK3V9R2D5W8HN6TQG";
    private const string DriftLantern = "universes/drift/entities/items/lantern.md";

    private readonly TempDen _den = TempDen.Starfall();
    private readonly DenTools _tools;

    public UniverseChoiceTests()
    {
        _den.Write(DriftLantern, "---\ntype: item\nname: Lantern\nstatus: canon\n---\n\nThe drift's own lantern. See [[Nobody At All]].\n");
        _den.Write("universes/drift/books/a-quiet-moon/book.md", "---\nid: 01JAQKFBF5KZNWJ47TAN9ZT24R\nname: A Quiet Moon\nchapters: [1]\n---\n");
        _tools = new DenTools(_den.Root);
    }

    public void Dispose() => _den.Dispose();

    [Fact]
    [Trait("Scenario", "SC-MCP-11")]
    public void WithoutAUniverseOrPositionEveryToolButCheckAsksForOne()
    {
        foreach (var call in new Func<object>[]
        {
            () => _tools.CanonAsOf(), () => _tools.GetEntity("Lantern"), () => _tools.ListEntities(), () => _tools.Search("Lantern"),
        })
        {
            var e = Assert.Throws<DenToolException>(call);
            Assert.Equal(ToolErrorCodes.UniverseRequired, e.Code);
            Assert.Null(e.Candidates);
            Assert.Contains("(drift, starfall)", e.Message, StringComparison.Ordinal);
        }

        Assert.Contains("'*'", Assert.Throws<DenToolException>(() => _tools.Search("Lantern")).Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'*'", Assert.Throws<DenToolException>(() => _tools.CanonAsOf()).Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-11")]
    public void APositionsBookSettlesTheUniverse()
    {
        Assert.Equal("starfall", _tools.CanonAsOf(position: "ember:1").Universe);
        Assert.Equal("drift", _tools.CanonAsOf(position: "tide:1").Universe);

        // At ash:2 the reader of Starfall has met the Long Dark's lamp only;
        // the Quiet Moon's ship comes later, and the Drift's isn't in this universe.
        var lantern = _tools.GetEntity("Lantern", asOf: "ash:2");
        Assert.Equal(AshLantern, lantern.Entity!.Id);
        Assert.Equal("starfall", lantern.Entity.Universe);

        // Before then, nothing is met: not an error, and nothing about either.
        var early = _tools.GetEntity("Lantern", asOf: "ember:1");
        Assert.False(early.InView);
        Assert.DoesNotContain("a-quiet-moon", JsonSerializer.Serialize(early, ToolJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-11")]
    public void ABookKeyInTwoUniversesOrInNoneDoesntSettleIt()
    {
        var shared = Assert.Throws<DenToolException>(() => _tools.GetEntity("Lantern", asOf: "a-quiet-moon:1"));
        Assert.Equal(ToolErrorCodes.UniverseRequired, shared.Code);
        Assert.Null(shared.Candidates);
        Assert.Contains("More than one universe has a book 'a-quiet-moon'", shared.Message, StringComparison.Ordinal);

        var nowhere = Assert.Throws<DenToolException>(() => _tools.CanonAsOf(position: "dune:1"));
        Assert.Equal(ToolErrorCodes.BadPosition, nowhere.Code);

        // Named, the shared key is fine.
        Assert.Equal("drift", _tools.CanonAsOf(universe: "drift", position: "a-quiet-moon:1").Universe);
        Assert.Equal(ToolErrorCodes.UnknownUniverse, Assert.Throws<DenToolException>(() => _tools.CanonAsOf(universe: "nowhere")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-11")]
    public void StarReadsEveryUniverseInSearchAndListOnly()
    {
        var hits = _tools.Search("Lantern", universe: "*");
        Assert.Equal(["drift", "starfall"], hits.Hits.Select(h => h.Entity.Universe).Distinct().Order(StringComparer.Ordinal));
        var all = _tools.ListEntities(universe: "*");
        Assert.Equal(["drift", "starfall"], all.Entities.Select(e => e.Universe).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["starfall"], _tools.ListEntities(universe: "starfall").Entities.Select(e => e.Universe).Distinct());

        foreach (var call in new Func<object>[]
        {
            () => _tools.CanonAsOf(universe: "*"),
            () => _tools.GetEntity("Lantern", universe: "*"),
            () => _tools.GetEntity("Lantern", universe: "*", asOf: "ash:1"),
            () => _tools.ListEntities(universe: "*", book: "ash"),
        })
        {
            Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(call).Code);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-12")]
    public void APinnedSessionReadsOnlyItsUniverse()
    {
        var pinned = new DenTools(_den.Root) { PinnedUniverse = "drift" };

        Assert.Equal("drift", pinned.CanonAsOf().Universe);
        Assert.Equal(DriftLantern, pinned.GetEntity("Lantern").Entity!.Path);
        Assert.All(pinned.ListEntities().Entities, e => Assert.Equal("drift", e.Universe));
        Assert.All(pinned.Search("Lantern").Hits, h => Assert.Equal("drift", h.Entity.Universe));
        Assert.Equal("drift", pinned.CanonAsOf(universe: "drift").Universe);

        foreach (var call in new Func<object>[]
        {
            () => pinned.CanonAsOf(universe: "starfall"),
            () => pinned.GetEntity("Lantern", universe: "starfall"),
            () => pinned.Search("Lantern", universe: "*"),
            () => pinned.ListEntities(universe: "*"),
            () => pinned.Check(universe: "*"),
            () => pinned.Check(universe: "starfall"),
        })
        {
            var e = Assert.Throws<DenToolException>(call);
            Assert.Equal(ToolErrorCodes.BadArgument, e.Code);
            Assert.Contains("pinned to universe 'drift'", e.Message, StringComparison.Ordinal);
        }

        // The client is told when it connects.
        Assert.Contains("pinned to universe 'drift'", WolfdenMcpServer.Options(pinned, "test").ServerInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("pinned", WolfdenMcpServer.Options(_tools, "test").ServerInstructions, StringComparison.Ordinal);

        // Its check is of its universe and the den-level files.
        var check = pinned.Check();
        Assert.Equal("drift", check.Universe);
        Assert.Contains(check.Diagnostics, d => d.Path == DriftLantern);
        Assert.DoesNotContain(check.Diagnostics, d => d.Path.StartsWith("universes/starfall/", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-13")]
    public void CheckOfAUniverseLeavesOutTheOthers()
    {
        var starfall = _tools.Check(universe: "starfall");
        var drift = _tools.Check(universe: "drift");
        var whole = _tools.Check();

        Assert.Equal("starfall", starfall.Universe);
        Assert.Null(whole.Universe);
        Assert.DoesNotContain(starfall.Diagnostics, d => d.Path.StartsWith("universes/drift/", StringComparison.Ordinal));
        Assert.DoesNotContain(drift.Diagnostics, d => d.Path.StartsWith("universes/starfall/", StringComparison.Ordinal));
        Assert.Contains(drift.Diagnostics, d => d.Path == DriftLantern);
        Assert.Contains(whole.Diagnostics, d => d.Path == DriftLantern);

        // Real scoping: the other universe's notes aren't even read.
        Assert.Equal(1, drift.Entities);
        Assert.Equal(whole.Entities, starfall.Entities + drift.Entities);

        // '*' is the whole den, as no universe is; the path filter still applies.
        Assert.Equal(whole.Diagnostics, _tools.Check(universe: "*").Diagnostics);
        Assert.All(_tools.Check(universe: "drift", path: "universes/drift/entities").Diagnostics,
            d => Assert.StartsWith("universes/drift/entities/", d.Path, StringComparison.Ordinal));
        Assert.Equal(ToolErrorCodes.UnknownUniverse, Assert.Throws<DenToolException>(() => _tools.Check(universe: "nowhere")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-13")]
    public void TheWholeDenIsTheUnionOfItsUniversesAndTheDenLevel()
    {
        AssertWholeIsTheUnion();
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-13")]
    public void ACheckOfOneUniverseFindsAnIdSharedWithAnotherUniverse()
    {
        // Mara Voss copied from Starfall into the Drift, id and all, and a
        // Drift note that links to her.
        const string starfallMara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
        const string driftMara = "universes/drift/entities/characters/mara-voss.md";
        const string driftNote = "universes/drift/entities/notes/crew.md";
        _den.Write(driftMara, _den.Read(starfallMara));
        _den.Write(driftNote, "---\ntype: note\nname: Crew\nstatus: canon\n---\n\nAboard: [[Mara Voss]].\n");

        var whole = _tools.Check(limit: 5000);
        var drift = _tools.Check(universe: "drift", limit: 5000);

        // The whole den: F-ID-3 on both copies, and the link breaks.
        Assert.Contains(whole.Diagnostics, d => d.Path == starfallMara && d.Rule == "F-ID-3");
        Assert.Contains(whole.Diagnostics, d => d.Path == driftMara && d.Rule == "F-ID-3");
        Assert.Contains(whole.Diagnostics, d => d.Path == driftNote && d.Rule == "F-LINK-8");

        // The Drift alone: the same findings on its files, naming Starfall's copy.
        Assert.Null(drift.Note);
        var dup = Assert.Single(drift.Diagnostics, d => d.Path == driftMara && d.Rule == "F-ID-3");
        Assert.Contains(starfallMara, dup.Message, StringComparison.Ordinal);
        Assert.Equal(
            whole.Diagnostics.Where(d => d.Rule == "F-ID-3" && d.Path.StartsWith("universes/drift/", StringComparison.Ordinal)),
            drift.Diagnostics.Where(d => d.Rule == "F-ID-3"));

        // F-LINK-8 too, but for the one accepted difference: a scoped check
        // can't add the hint that names a match in another universe.
        Assert.Contains(drift.Diagnostics, d => d.Path == driftNote && d.Rule == "F-LINK-8");
        Assert.Equal(
            whole.Diagnostics.Where(d => d.Rule == "F-LINK-8" && d.Path.StartsWith("universes/drift/", StringComparison.Ordinal)).Select(WithoutHint),
            drift.Diagnostics.Where(d => d.Rule == "F-LINK-8").Select(WithoutHint));

        static DiagnosticView WithoutHint(DiagnosticView d) =>
            d with { Message = d.Message.Split(" It matches only ", StringSplitOptions.None)[0] };
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-13")]
    public void WhenTheIndexCantBeBuiltTheScopedCheckSaysTheIdComparisonWasSkipped()
    {
        _den.Write("den.yaml", "format: 1\nname: [broken\n");

        var drift = _tools.Check(universe: "drift");

        Assert.False(drift.Loaded);
        Assert.Contains("weren't checked", drift.Note!, StringComparison.Ordinal);
        Assert.Null(_tools.Check().Note);
    }

    /// <summary>The whole den's findings are each universe's own plus the den-level ones, each reported once.</summary>
    private void AssertWholeIsTheUnion()
    {
        var whole = _tools.Check(limit: 5000).Diagnostics;
        var starfall = _tools.Check(universe: "starfall", limit: 5000).Diagnostics;
        var drift = _tools.Check(universe: "drift", limit: 5000).Diagnostics;

        static bool In(DiagnosticView d, string universe) => d.Path.StartsWith($"universes/{universe}/", StringComparison.Ordinal);
        var denLevel = starfall.Where(d => !In(d, "starfall")).ToList();
        Assert.Equal(denLevel, drift.Where(d => !In(d, "drift")).ToList());
        var parts = starfall.Where(d => In(d, "starfall"))
            .Concat(drift.Where(d => In(d, "drift")))
            .Concat(denLevel)
            .OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Line ?? 0).ThenBy(d => d.Rule, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(whole.Count, whole.Distinct().Count());
        Assert.Equal(parts.Count, parts.Distinct().Count());
        Assert.Equal(whole, parts);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-13")]
    public void ABrokenPackOrDenYamlIsReportedWhenAUniverseIsGiven()
    {
        _den.Write("packs/starfall/types/ship.yaml", "key: ship\nfields: [unclosed\n");
        var pack = _tools.Check(universe: "drift");
        Assert.Contains(pack.Diagnostics, d => d.Path == "packs/starfall/types/ship.yaml" && d.Severity == "error");

        _den.Write("den.yaml", "format: 1\nname: [broken\n");
        var den = _tools.Check(universe: "drift");
        Assert.False(den.Loaded);
        Assert.Contains(den.Diagnostics, d => d.Path == "den.yaml" && d.Severity == "error");
    }
}
