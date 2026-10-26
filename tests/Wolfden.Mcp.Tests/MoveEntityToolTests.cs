namespace Wolfden.Mcp.Tests;

/// <summary>
/// <c>move_entity</c> (#89) over a copy of the invented Starfall den: the
/// series The Long Dark (Ember, then Ash), the standalone A Quiet Moon, and
/// The Drift War, a series with no books yet. A move keeps where the entity
/// enters canon, and links to it that would stop reaching it follow by id
/// (format.md F-RT-5, F-ENT-6).
/// </summary>
public sealed class MoveEntityToolTests : IDisposable
{
    private const string Universe = "starfall";
    private const string Root = "universes/starfall";
    private const string Series = Root + "/series/long-dark";
    private const string Ember = Series + "/books/ember";
    private const string Ash = Series + "/books/ash";
    private const string Moon = Root + "/books/a-quiet-moon";
    private const string Mara = Series + "/entities/characters/mara-voss.md";
    private const string Tessaly = Series + "/entities/ships/tessaly.md";
    private const string Pike = Ash + "/entities/characters/halden-pike.md";
    private const string LanternItem = Ash + "/entities/items/lantern.md";
    private const string LanternShip = Moon + "/entities/ships/lantern.md";
    private const string EmberOutline = Ember + "/outline.md";
    private const string TessalyId = "01JA7Q8M4XK3V9R2D5W8HN6TQE";

    private readonly TempDen _den = TempDen.Starfall();

    private DenTools Tools => new(_den.Root);

    public void Dispose() => _den.Dispose();

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void ABookEntityWithNoFromMovedUpGetsTheEntryPointItHadAsItsFrom()
    {
        const string Old = Ash + "/entities/characters/ilse-brandt.md";
        const string New = Series + "/entities/characters/ilse-brandt.md";
        _den.Write(Old, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: character\nname: Ilse Brandt\nstatus: canon\n---\n\n## Overview\nA medic on the [[Tessaly]].\n");
        Assert.False(Tools.GetEntity("Ilse Brandt", Universe, "ember:3").InView);
        var hash = Tools.GetEntity("Ilse Brandt", Universe).Entity!.Hash!;

        var result = Tools.MoveEntity("Ilse Brandt", hash, toSeries: "long-dark", universe: Universe);

        Assert.True(result.Changed);
        Assert.Equal(New, result.Path);
        Assert.False(File.Exists(Path.Combine(_den.Root, Old)));
        Assert.Equal(
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: character\nname: Ilse Brandt\nstatus: canon\nfrom: ash\n---\n\n## Overview\nA medic on the [[Tessaly]].\n",
            _den.Read(New));
        var moved = Assert.Single(result.Files!);
        Assert.Equal((New, true, Old), (moved.Path, moved.Created, moved.MovedFrom));
        Assert.Equal("series", result.Entity!.ScopeKind);
        Assert.Contains(result.Effects, e => e.Contains("entered canon at the start of book 'ash'", StringComparison.Ordinal) && e.Contains("from: \"ash\"", StringComparison.Ordinal));

        // Every canon view is as it was: not in Ember, in from the start of Ash.
        Assert.False(Tools.GetEntity("Ilse Brandt", Universe, "ember:3").InView);
        Assert.True(Tools.GetEntity("Ilse Brandt", Universe, "ash:1").InView);
        Assert.Equal(result.Hash, Tools.GetEntity("Ilse Brandt", Universe).Entity!.Hash);

        // The old hash is stale now.
        Assert.Equal(ToolErrorCodes.StaleFile, Assert.Throws<DenToolException>(() => Tools.MoveEntity("Ilse Brandt", hash, universe: Universe)).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void AMoveThatStartsAtTheSamePointWritesNoFrom()
    {
        const string Old = Series + "/entities/characters/oren-tal.md";
        const string Text = "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z2\ntype: character\nname: Oren Tal\nstatus: canon\n---\n\n## Overview\nThe Tessaly's pilot.\n";
        _den.Write(Old, Text);
        var hash = Tools.GetEntity("Oren Tal", Universe).Entity!.Hash!;

        // The start of the series is the start of its first book.
        var result = Tools.MoveEntity("Oren Tal", hash, toBook: "ember", universe: Universe);

        Assert.Equal(Text, _den.Read(Ember + "/entities/characters/oren-tal.md"));
        Assert.Contains(result.Effects, e => e.Contains("still enters canon at the same point", StringComparison.Ordinal));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void ANarrowingMoveOutsideTheEntryPointIsRefusedNamingBothWaysForward()
    {
        var before = _den.Read(Mara);
        var hash = Tools.GetEntity("Mara Voss", Universe).Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.MoveEntity("Mara Voss", hash, toBook: "ash", universe: Universe));

        Assert.Equal(ToolErrorCodes.EntryOutsideScope, refused.Code);
        Assert.Contains("ember:1", refused.Message, StringComparison.Ordinal);
        Assert.Contains("already met it", refused.Message, StringComparison.Ordinal);
        Assert.Contains("keep_entry", refused.Message, StringComparison.Ordinal);
        Assert.Contains("enter_at_new_scope", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(Mara));
        Assert.Equal(
            ToolErrorCodes.BadArgument,
            Assert.Throws<DenToolException>(() => Tools.MoveEntity("Mara Voss", hash, toBook: "ash", keepEntry: true, enterAtNewScope: true, universe: Universe)).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void KeepEntryKeepsTheFromAndItsWarning()
    {
        var hash = Tools.GetEntity("Mara Voss", Universe).Entity!.Hash!;

        var result = Tools.MoveEntity("Mara Voss", hash, toBook: "ash", keepEntry: true, universe: Universe);

        var moved = Ash + "/entities/characters/mara-voss.md";
        Assert.Equal(moved, result.Path);
        Assert.Contains("\nfrom: \"ember:1\"\n", _den.Read(moved), StringComparison.Ordinal);
        Assert.Contains(result.Effects, e => e.Contains("keep_entry", StringComparison.Ordinal) && e.Contains("F-ENT-6", StringComparison.Ordinal));
        Assert.Contains(Tools.Check(path: moved, universe: Universe).Diagnostics, d => d.Rule == "F-ENT-6");
        Assert.Empty(result.PinnedCanon);
        Assert.True(Tools.GetEntity("Mara Voss", Universe, "ember:1").InView);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void EnterAtNewScopeTakesTheFromOutAndFlagsPinnedCanon()
    {
        var hash = Tools.GetEntity("Mara Voss", Universe).Entity!.Hash!;

        var result = Tools.MoveEntity("Mara Voss", hash, toBook: "ash", enterAtNewScope: true, universe: Universe);

        Assert.DoesNotContain("\nfrom:", _den.Read(result.Path), StringComparison.Ordinal);
        Assert.Contains(result.Effects, e => e.Contains("now enters canon at the start of book 'ash'", StringComparison.Ordinal));
        Assert.NotEmpty(result.PinnedCanon);
        Assert.False(Tools.GetEntity("Mara Voss", Universe, "ember:3").InView);
        Assert.True(Tools.GetEntity("Mara Voss", Universe, "ash:1").InView);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void ASeriesEntityWithNoFromMovedIntoItsSecondBookWritesTheSeriesStartWithKeepEntry()
    {
        _den.Write(Series + "/entities/characters/oren-tal.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z2\ntype: character\nname: Oren Tal\nstatus: canon\n---\n");
        var hash = Tools.GetEntity("Oren Tal", Universe).Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.MoveEntity("Oren Tal", hash, toBook: "ash", universe: Universe));
        Assert.Equal(ToolErrorCodes.EntryOutsideScope, refused.Code);
        Assert.Contains("from: \"ember\"", refused.Message, StringComparison.Ordinal);

        var result = Tools.MoveEntity("Oren Tal", hash, toBook: "ash", keepEntry: true, universe: Universe);
        Assert.Contains("\nfrom: ember\n", _den.Read(result.Path), StringComparison.Ordinal);
        Assert.True(Tools.GetEntity("Oren Tal", Universe, "ember:1").InView);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void AnEntryPointNoPositionCanNameNeedsEnterAtNewScope()
    {
        // The Drift War has no books, and none comes after it.
        _den.Write(Root + "/series/drift-war/entities/characters/sel-amar.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z3\ntype: character\nname: Sel Amar\nstatus: canon\n---\n");
        var hash = Tools.GetEntity("Sel Amar", Universe).Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.MoveEntity("Sel Amar", hash, universe: Universe));
        Assert.Equal(ToolErrorCodes.EntryOutsideScope, refused.Code);
        Assert.Contains("no position can name", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ToolErrorCodes.EntryOutsideScope, Assert.Throws<DenToolException>(() => Tools.MoveEntity("Sel Amar", hash, keepEntry: true, universe: Universe)).Code);

        var result = Tools.MoveEntity("Sel Amar", hash, enterAtNewScope: true, universe: Universe);
        Assert.Equal(Root + "/entities/characters/sel-amar.md", result.Path);
        Assert.True(Tools.GetEntity("Sel Amar", Universe, "ember:1").InView);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void LinksThatWouldStopReachingItAreRewrittenToItsId()
    {
        // A namesake in A Quiet Moon: links from the Long Dark reach the series' Tessaly only because it's nearer.
        _den.Write(Moon + "/entities/ships/tessaly.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z4\ntype: ship\nname: Tessaly\nstatus: canon\nfrom: a-quiet-moon\n---\n");
        var hash = Tools.GetEntity(Tessaly, Universe).Entity!.Hash!;

        var result = Tools.MoveEntity(Tessaly, hash, toBook: "ash", keepEntry: true, universe: Universe);

        var moved = Ash + "/entities/ships/tessaly.md";
        Assert.Equal(moved, result.Path);
        Assert.Contains($"| 2 | 2 | [[mara-voss\\|Voss]] | The away team leaves the [[{TessalyId}\\|Tessaly]]. |", _den.Read(EmberOutline), StringComparison.Ordinal);
        Assert.Contains($"- {{ value: \"[[{TessalyId}|tessaly]]\", from: \"ember:1\", until: \"ash:2\" }}", _den.Read(Mara), StringComparison.Ordinal);

        // A link from Ash reaches it there as it is, so it's left alone.
        Assert.Contains("[[Tessaly]]", _den.Read(LanternItem), StringComparison.Ordinal);
        Assert.Equal([moved, EmberOutline, Mara], result.Files!.Select(f => f.Path));
        Assert.Contains(result.Effects, e => e.Contains("2 link(s) to it would have stopped reaching it", StringComparison.Ordinal));
        Assert.DoesNotContain(Tools.Check(universe: Universe).Diagnostics, d => d.Rule == "F-LINK-7" && d.Message.Contains("Tessaly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void ALinkToRewriteInAFinalBookStopsTheMove()
    {
        _den.Write(Moon + "/entities/ships/tessaly.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z4\ntype: ship\nname: Tessaly\nstatus: canon\nfrom: a-quiet-moon\n---\n");
        _den.Write(Ember + "/book.md", _den.Read(Ember + "/book.md").Replace("status: draft", "status: final", StringComparison.Ordinal));
        var before = _den.Read(Tessaly);
        var hash = Tools.GetEntity(Tessaly, Universe).Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.MoveEntity(Tessaly, hash, toBook: "ash", keepEntry: true, universe: Universe));

        Assert.Equal(ToolErrorCodes.Frozen, refused.Code);
        Assert.Contains(EmberOutline, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(Tessaly));
        Assert.False(File.Exists(Path.Combine(_den.Root, Ash + "/entities/ships/tessaly.md")));

        // Nothing moves into a final book, or out of one.
        var mara = Tools.GetEntity("Mara Voss", Universe).Entity!.Hash!;
        Assert.Equal(ToolErrorCodes.Frozen, Assert.Throws<DenToolException>(() => Tools.MoveEntity("Mara Voss", mara, toBook: "ember", universe: Universe)).Code);
        _den.Write(Ember + "/entities/notes/watch-log.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z5\ntype: note\nname: Watch log\nstatus: draft\n---\n");
        var log = Tools.GetEntity("Watch log", Universe).Entity!.Hash!;
        Assert.Equal(ToolErrorCodes.Frozen, Assert.Throws<DenToolException>(() => Tools.MoveEntity("Watch log", log, toSeries: "long-dark", universe: Universe)).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void ALinkThatWouldReachAnotherEntityIsRefusedUnlessAllowed()
    {
        var before = _den.Read(LanternShip);
        var hash = Tools.GetEntity(LanternShip, Universe).Entity!.Hash!;

        // In Ash, Pike's [[Lantern]] reaches the item; with the ship in Ash too, it would be a tie.
        var refused = Assert.Throws<DenToolException>(() => Tools.MoveEntity(LanternShip, hash, toBook: "ash", keepEntry: true, universe: Universe));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains(Pike, refused.Message, StringComparison.Ordinal);
        Assert.Contains("allow_relinks", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Link those by id or file name first, or pass allow_relinks: true to move it anyway.", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("check --fix", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(LanternShip));

        var result = Tools.MoveEntity(LanternShip, hash, toBook: "ash", keepEntry: true, universe: Universe, allowRelinks: true);
        Assert.Contains(result.Effects, e => e.Contains(Pike, StringComparison.Ordinal) && e.Contains("is now ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void TheMovedFilesOwnLinksThatWouldBeAmbiguousAreRewrittenToTheirTargetsIds()
    {
        // Moved to the universe, Pike's [[Lantern]] links no longer have the Ash item in their scope chain,
        // so they'd tie between the item and the ship: each is rewritten to the item's id, with its text,
        // its section anchor, and its display text kept.
        const string Item = "01JA7Q8M4XK3V9R2D5W8HN6TQG";
        _den.Write(Pike, _den.Read(Pike).Replace(
            "Owes money to the Compact.\n", "Owes money to the Compact. See [[Lantern#Overview]] and [[Lantern|the lamp]].\n", StringComparison.Ordinal));
        var before = Tools.GetEntity("Halden Pike", Universe, "ash:2").Entity!;
        var hash = Tools.GetEntity("Halden Pike", Universe).Entity!.Hash!;

        var result = Tools.MoveEntity("Halden Pike", hash, universe: Universe);

        var moved = Root + "/entities/characters/halden-pike.md";
        Assert.Equal(moved, result.Path);
        var text = _den.Read(moved);
        Assert.Contains($"[[{Item}|Lantern]] in the hold", text, StringComparison.Ordinal);
        Assert.Contains($"See [[{Item}#Overview|Lantern#Overview]] and [[{Item}|the lamp]].", text, StringComparison.Ordinal);
        Assert.Equal(3, result.Effects.Count(e => e.StartsWith("Its own link", StringComparison.Ordinal) && e.Contains(LanternItem, StringComparison.Ordinal)));
        Assert.DoesNotContain(Tools.Check(path: moved, universe: Universe).Diagnostics, d => d.Rule is "F-LINK-7" or "F-LINK-8");

        // The canon view is as it was, with the same links reaching the same entity.
        var after = Tools.GetEntity("Halden Pike", Universe, "ash:2").Entity!;
        Assert.Equal(before.Lead, after.Lead?.Replace($"{Item}|", "", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void TheMovedFilesOwnLinkThatWouldReachAnotherEntityKeepsItsTarget()
    {
        const string Old = Ash + "/entities/notes/signal-log.md";
        const string Text = "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z9\ntype: note\nname: Signal log\nstatus: draft\nfrom: \"a-quiet-moon:1\"\n---\n\nThe [[Lantern]] signals. [[Nobody we know]] answers.\n";
        _den.Write(Old, Text);
        var hash = Tools.GetEntity("Signal log", Universe).Entity!.Hash!;

        // In A Quiet Moon, [[Lantern]] would reach the ship there, not the Ash item it reaches now.
        var result = Tools.MoveEntity("Signal log", hash, toBook: "a-quiet-moon", universe: Universe);

        Assert.EndsWith("The [[01JA7Q8M4XK3V9R2D5W8HN6TQG|Lantern]] signals. [[Nobody we know]] answers.\n", _den.Read(result.Path), StringComparison.Ordinal);
        Assert.Contains(
            result.Effects,
            e => e.StartsWith("Its own link [[Lantern]] (line 9 before the move)", StringComparison.Ordinal) && e.Contains(LanternItem, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void WithAllowRelinksTheMovedFilesOwnLinksFollowTheNewScope()
    {
        const string Old = Ash + "/entities/notes/signal-log.md";
        _den.Write(Old, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z9\ntype: note\nname: Signal log\nstatus: draft\nfrom: \"a-quiet-moon:1\"\n---\n\nThe [[Lantern]] signals.\n");
        var hash = Tools.GetEntity("Signal log", Universe).Entity!.Hash!;

        var result = Tools.MoveEntity("Signal log", hash, toBook: "a-quiet-moon", universe: Universe, allowRelinks: true);

        Assert.EndsWith("The [[Lantern]] signals.\n", _den.Read(result.Path), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Effects, e => e.StartsWith("Its own link", StringComparison.Ordinal));
        Assert.Contains(result.Effects, e => e.Contains($"now reaches 'Lantern' ({LanternShip})", StringComparison.Ordinal) && e.Contains(LanternItem, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void AnOwnLinkToATargetWithNoIdRefusesTheMoveAndSaysWhatAllowRelinksDoes()
    {
        // The Ash beacon has no id, so the signal log's [[Beacon]] can't be kept on it by id;
        // in A Quiet Moon, it would reach the ship there instead.
        _den.Write(Ash + "/entities/items/beacon.md", "---\ntype: item\nname: Beacon\nstatus: canon\n---\n");
        _den.Write(Moon + "/entities/ships/beacon.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z7\ntype: ship\nname: Beacon\nstatus: canon\n---\n");
        const string Old = Ash + "/entities/notes/signal-log.md";
        _den.Write(Old, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z9\ntype: note\nname: Signal log\nstatus: draft\nfrom: \"a-quiet-moon:1\"\n---\n\nThe [[Beacon]] and the [[Lantern]] signal.\n");
        var before = _den.Read(Old);
        var hash = Tools.GetEntity("Signal log", Universe).Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.MoveEntity("Signal log", hash, toBook: "a-quiet-moon", universe: Universe));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains("They're its own link(s) to entities with no id", refused.Message, StringComparison.Ordinal);
        Assert.Contains("wolfden check --fix", refused.Message, StringComparison.Ordinal);
        Assert.Contains("allow_relinks", refused.Message, StringComparison.Ordinal);
        Assert.Contains("also stops the rewriting of its other own links", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Link those by id", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(Old));

        // With allow_relinks, both own links follow the new scope, the one that had an id included.
        var result = Tools.MoveEntity("Signal log", hash, toBook: "a-quiet-moon", universe: Universe, allowRelinks: true);
        Assert.EndsWith("The [[Beacon]] and the [[Lantern]] signal.\n", _den.Read(result.Path), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void AnOwnLinkToATargetWithNoIdAndAnotherFilesLinkAreAdvisedTogether()
    {
        // The ship's own [[Beacon]] reaches the id-less beacon of A Quiet Moon; in Ash, it would reach
        // the Ash beacon. Pike's [[Lantern]] there would tie with the ship. The ship has no other own
        // links to rewrite, so allow_relinks isn't said to stop any.
        _den.Write(Moon + "/entities/items/beacon.md", "---\ntype: item\nname: Beacon\nstatus: canon\n---\n");
        _den.Write(Ash + "/entities/items/beacon.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z7\ntype: item\nname: Beacon\nstatus: canon\n---\n");
        _den.Write(LanternShip, _den.Read(LanternShip) + "\nIts [[Beacon]] burns.\n");
        var hash = Tools.GetEntity(LanternShip, Universe).Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.MoveEntity(LanternShip, hash, toBook: "ash", keepEntry: true, universe: Universe));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains(Pike, refused.Message, StringComparison.Ordinal);
        Assert.Contains("1 of them is its own link(s) to entities with no id", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Run 'wolfden check --fix' to give those entities ids, and link the other links by id or file name, then move it again.", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Or pass allow_relinks: true to move it anyway.", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("also stops the rewriting", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void TheMovedFilesOwnRewrittenLinksAreListedUpToTheCapWithTheirLineBeforeTheMove()
    {
        // Pike has one [[Lantern]]; twelve more make thirteen, three past the cap of ten.
        var lines = string.Concat(Enumerable.Range(1, 12).Select(i => $"Lamp {i}: [[Lantern]].\n"));
        _den.Write(Pike, _den.Read(Pike) + "\n" + lines);
        var hash = Tools.GetEntity("Halden Pike", Universe).Entity!.Hash!;

        var result = Tools.MoveEntity("Halden Pike", hash, universe: Universe);

        var own = result.Effects.Where(e => e.StartsWith("Its own link", StringComparison.Ordinal)).ToList();
        Assert.Equal(10, own.Count);
        Assert.All(own, e => Assert.Matches(@"\(line \d+ before the move\)", e));
        Assert.Contains(result.Effects, e => e.StartsWith("And 3 more of its own links were rewritten", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void AMoveWhoseOwnLinksWereRewrittenRollsBackWhole()
    {
        var before = _den.Read(Pike);
        var hash = Tools.GetEntity("Halden Pike", Universe).Entity!.Hash!;

        var tools = new DenTools(_den.Root) { Store = new StepStore(_den.Root, failMove: true) };
        Assert.Equal(ToolErrorCodes.WriteFailed, Assert.Throws<DenToolException>(() => tools.MoveEntity("Halden Pike", hash, universe: Universe)).Code);

        Assert.Equal(before, _den.Read(Pike));
        Assert.False(File.Exists(Path.Combine(_den.Root, Root + "/entities/characters/halden-pike.md")));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void AnAmbiguousLinkThatNowResolvesIsReported()
    {
        // The Reach's [[Lantern]] is ambiguous between the ship and the item; with the ship in the universe, it reaches the ship.
        var hash = Tools.GetEntity(LanternShip, Universe).Entity!.Hash!;

        var result = Tools.MoveEntity(LanternShip, hash, universe: Universe);

        Assert.Equal(Root + "/entities/ships/lantern.md", result.Path);
        Assert.Contains(
            result.Effects,
            e => e.Contains("kessan-reach.md", StringComparison.Ordinal) && e.Contains("now reaches 'Lantern'", StringComparison.Ordinal) && e.Contains("was ambiguous", StringComparison.Ordinal));
        Assert.Contains("\nfrom: \"a-quiet-moon:1\"\n", _den.Read(result.Path), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void AFileAlreadyThereStopsTheMove()
    {
        const string Taken = Root + "/entities/characters/mara-voss.md";
        _den.Write(Taken, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z6\ntype: character\nname: Mara Voss (first draft)\nstatus: retconned\n---\n");
        var before = _den.Read(Mara);
        var hash = Tools.GetEntity(Mara, Universe).Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.MoveEntity(Mara, hash, universe: Universe));

        Assert.Equal(ToolErrorCodes.AlreadyExists, refused.Code);
        Assert.Equal(before, _den.Read(Mara));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void AMoveThatFailsPartWayPutsBackWhatItWrote()
    {
        _den.Write(Moon + "/entities/ships/tessaly.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z4\ntype: ship\nname: Tessaly\nstatus: canon\nfrom: a-quiet-moon\n---\n");
        var before = (Tessaly: _den.Read(Tessaly), Outline: _den.Read(EmberOutline), Mara: _den.Read(Mara));
        var hash = Tools.GetEntity(Tessaly, Universe).Entity!.Hash!;
        var moved = Ash + "/entities/ships/tessaly.md";

        // Moving the file fails, after the links to it are rewritten.
        var tools = new DenTools(_den.Root) { Store = new StepStore(_den.Root, failMove: true) };
        var failed = Assert.Throws<DenToolException>(() => tools.MoveEntity(Tessaly, hash, toBook: "ash", keepEntry: true, universe: Universe));

        Assert.Equal(ToolErrorCodes.WriteFailed, failed.Code);
        Assert.Contains("put back", failed.Message, StringComparison.Ordinal);
        Assert.Equal(before.Tessaly, _den.Read(Tessaly));
        Assert.Equal(before.Outline, _den.Read(EmberOutline));
        Assert.Equal(before.Mara, _den.Read(Mara));
        Assert.False(File.Exists(Path.Combine(_den.Root, moved)));

        // A link rewrite that fails puts back the file rewritten in place, and the one written before it.
        tools = new DenTools(_den.Root) { Store = new WriteToolTests.FailingStore(_den.Root, Mara) };
        Assert.Equal(ToolErrorCodes.WriteFailed, Assert.Throws<DenToolException>(() => tools.MoveEntity(Tessaly, hash, toBook: "ash", keepEntry: true, universe: Universe)).Code);
        Assert.False(File.Exists(Path.Combine(_den.Root, moved)));
        Assert.Equal(before.Outline, _den.Read(EmberOutline));
        Assert.Equal(before.Tessaly, _den.Read(Tessaly));

        // The last write, at the new path (enter_at_new_scope takes 'from' out there), fails: the file moves back.
        var maraHash = Tools.GetEntity(Mara, Universe).Entity!.Hash!;
        var maraMoved = Ash + "/entities/characters/mara-voss.md";
        tools = new DenTools(_den.Root) { Store = new StepStore(_den.Root, failWrite: maraMoved) };
        Assert.Equal(ToolErrorCodes.WriteFailed, Assert.Throws<DenToolException>(() => tools.MoveEntity(Mara, maraHash, toBook: "ash", enterAtNewScope: true, universe: Universe)).Code);
        Assert.Equal(before.Mara, _den.Read(Mara));
        Assert.False(File.Exists(Path.Combine(_den.Root, maraMoved)));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void TheDenNeverHoldsTwoCopiesOrNoneWhileItMoves()
    {
        var hash = Tools.GetEntity(Mara, Universe).Entity!.Hash!;
        var moved = Ash + "/entities/characters/mara-voss.md";
        var store = new StepStore(_den.Root, watch: (Mara, moved));

        // Rewritten in place, then moved whole, then its 'from' taken out at the new path.
        var result = new DenTools(_den.Root) { Store = store }.MoveEntity(Mara, hash, toBook: "ash", enterAtNewScope: true, universe: Universe);

        Assert.Equal(moved, result.Path);
        Assert.False(store.SawTwoOrNone);
        Assert.False(File.Exists(Path.Combine(_den.Root, Mara)));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void EnterAtNewScopeForAnEntryAfterItSaysItEntersEarlier()
    {
        _den.Write(Ash + "/entities/characters/sash-orel.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z8\ntype: character\nname: Sash Orel\nstatus: canon\nfrom: \"ash:2\"\n---\n");
        var hash = Tools.GetEntity("Sash Orel", Universe).Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.MoveEntity("Sash Orel", hash, toBook: "ember", universe: Universe));
        Assert.Equal(ToolErrorCodes.EntryOutsideScope, refused.Code);
        Assert.Contains("after book 'ember'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("be warned: it would then appear in canon views from the start of book 'ember' on, earlier than readers now meet it", refused.Message, StringComparison.Ordinal);
        Assert.Contains("keep_entry", refused.Message, StringComparison.Ordinal);
        Assert.Contains("enter_at_new_scope", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("before then leave it out", refused.Message, StringComparison.Ordinal);
        Assert.Contains("earlier than readers now meet it (ash:2).", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(ash:2 (", refused.Message, StringComparison.Ordinal);

        var result = Tools.MoveEntity("Sash Orel", hash, toBook: "ember", enterAtNewScope: true, universe: Universe);
        Assert.Contains(result.Effects, e => e.Contains("now include it, earlier than the reader met it before", StringComparison.Ordinal));
        Assert.True(Tools.GetEntity("Sash Orel", Universe, "ember:1").InView);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void AnEntityWithoutAnIdGetsOneAndOtherArgumentsAreChecked()
    {
        _den.Write(Ash + "/entities/notes/cargo-list.md", "---\ntype: note\nname: Cargo list\nstatus: draft\nfrom: \"ash:2\"\n---\n");
        var hash = Tools.GetEntity("Cargo list", Universe).Entity!.Hash!;

        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.MoveEntity("Cargo list", hash, toSeries: "long-dark", toBook: "ash", universe: Universe)).Code);
        Assert.Equal(ToolErrorCodes.UnknownScope, Assert.Throws<DenToolException>(() => Tools.MoveEntity("Cargo list", hash, toBook: "tide", universe: Universe)).Code);
        var same = Tools.MoveEntity("Cargo list", hash, toBook: "ash", universe: Universe);
        Assert.False(same.Changed);

        var result = Tools.MoveEntity("Cargo list", hash, toSeries: "long-dark", universe: Universe);

        Assert.NotNull(result.IdAdded);
        Assert.StartsWith($"---\nid: {result.IdAdded}\ntype: note\n", _den.Read(result.Path), StringComparison.Ordinal);
        Assert.Contains(result.Effects, e => e.Contains("given " + result.IdAdded, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    public void RowIdsThatNowResolveElsewhereAreSaid()
    {
        _den.Write(Ash + "/entities/events/the-ambush.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z7\ntype: event\nname: The ambush\nstatus: canon\nfrom: \"ash:1\"\nfields:\n  row: \"3\"\n---\n");
        var hash = Tools.GetEntity("The ambush", Universe).Entity!.Hash!;

        // Within the series, the same outlines; in the universe, every outline.
        var series = Tools.MoveEntity("The ambush", hash, toSeries: "long-dark", universe: Universe);
        Assert.DoesNotContain(series.Effects, e => e.Contains("'row'", StringComparison.Ordinal));
        var universe = Tools.MoveEntity("The ambush", series.Hash, universe: Universe);
        Assert.Contains(universe.Effects, e => e.Contains("'row' ids now resolve among the outlines of every book in the universe", StringComparison.Ordinal));
    }

    /// <summary>
    /// A den store that can fail a move, or a write to one path, and that
    /// notes whether any step left both or neither of two paths there.
    /// </summary>
    private sealed class StepStore(string root, bool failMove = false, string? failWrite = null, (string A, string B)? watch = null) : Core.Structure.IDenStore
    {
        private readonly Core.Structure.FileSystemDenStore _disk = new(root);

        public bool SawTwoOrNone { get; private set; }

        public bool IsReadOnly => false;

        public bool FileExists(string path) => _disk.FileExists(path);

        public byte[] ReadFile(string path) => _disk.ReadFile(path);

        public byte[]? TryReadFile(string path) => _disk.TryReadFile(path);

        public byte[]? TryReadListedFile(string path) => _disk.TryReadListedFile(path);

        public IEnumerable<string> GetDirectories(string path) => _disk.GetDirectories(path);

        public IEnumerable<string> GetFiles(string path) => _disk.GetFiles(path);

        public bool FolderExists(string path) => _disk.FolderExists(path);

        public void WriteFile(string path, byte[] bytes, Core.Structure.DenWriteMode mode = Core.Structure.DenWriteMode.CreateOrReplace)
        {
            if (path == failWrite)
            {
                throw new Core.Structure.DenStoreException(Core.Structure.DenStoreError.AccessDenied, path, $"'{path}' is read-only.");
            }

            _disk.WriteFile(path, bytes, mode);
            Look();
        }

        public void DeleteFile(string path)
        {
            _disk.DeleteFile(path);
            Look();
        }

        public void MoveFile(string from, string to)
        {
            if (failMove)
            {
                throw new Core.Structure.DenStoreException(Core.Structure.DenStoreError.InUse, from, $"'{from}' is open in another program.");
            }

            _disk.MoveFile(from, to);
            Look();
        }

        public void MoveFolder(string from, string to) => _disk.MoveFolder(from, to);

        public void DeleteFolder(string path) => _disk.DeleteFolder(path);

        private void Look()
        {
            if (watch is { } w && _disk.FileExists(w.A) == _disk.FileExists(w.B))
            {
                SawTwoOrNone = true;
            }
        }
    }
}
