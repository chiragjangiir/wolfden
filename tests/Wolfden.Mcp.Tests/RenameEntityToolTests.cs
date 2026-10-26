namespace Wolfden.Mcp.Tests;

/// <summary>
/// <c>rename_entity</c> and <c>change_alias</c> (#150) over a copy of the
/// Dracula sample: the links that used the old text follow, in any file, or
/// the old text stays as an alias and they're left alone (format.md F-RT-5).
/// </summary>
public sealed class RenameEntityToolTests : IDisposable
{
    private const string Book = "universes/dracula/books/dracula";
    private const string Swales = Book + "/entities/characters/mr-swales.md";
    private const string Count = Book + "/entities/characters/count-dracula.md";
    private const string BookFile = Book + "/book.md";
    private const string Outline = Book + "/outline.md";
    private const string Note = "universes/dracula/entities/notes/whitby-dialect.md";

    private readonly TempDen _den = TempDen.Dracula();

    private DenTools Tools => new(_den.Root);

    public void Dispose() => _den.Dispose();

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void RenamingAnEntityRewritesTheLinksThatUsedTheOldName()
    {
        _den.Write(Note,
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\nfields:\n  about: [\"[[Mr. Swales#Voice]]\"]\n---\n\n" +
            "# Whitby dialect\n\nSee [[Mr. Swales]], [[ mr.  swales # voice |how he talks]], and [[mr-swales]].\n");
        _den.Write(BookFile, _den.Read(BookFile).Replace(
            "      pov: [\"[[mina-harker]]\", \"[[john-seward]]\"]\n", "      pov: [\"[[mina-harker]]\", \"[[Mr. Swales]]\"]\n", StringComparison.Ordinal));
        _den.Write(Outline, _den.Read(Outline).Replace(
            "eyes open and lips red. | |", "eyes open and lips red. | [[Mr. Swales\\|Swales]] |", StringComparison.Ordinal));
        _den.Write(Swales, _den.Read(Swales).Replace(
            "as she hears it.\n", "as she hears it. See [[Mr. Swales#Arc]].\n", StringComparison.Ordinal));
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.RenameEntity("Mr. Swales", "Old Swales", hash);

        Assert.True(result.Changed);
        var swales = _den.Read(Swales);
        Assert.Contains("\nname: Old Swales\n", swales, StringComparison.Ordinal);
        Assert.Contains("\n# Old Swales\n", swales, StringComparison.Ordinal);
        Assert.Contains("as she hears it. See [[Old Swales#Arc]].\n", swales, StringComparison.Ordinal);
        Assert.DoesNotContain("aliases", swales, StringComparison.Ordinal);
        var note = _den.Read(Note);
        Assert.Contains("  about: [\"[[Old Swales#Voice]]\"]\n", note, StringComparison.Ordinal);
        Assert.Contains("See [[Old Swales]], [[ Old Swales # voice |how he talks]], and [[mr-swales]].", note, StringComparison.Ordinal);
        Assert.Contains("      pov: [\"[[mina-harker]]\", \"[[Old Swales]]\"]\n", _den.Read(BookFile), StringComparison.Ordinal);
        Assert.Contains("| [[Old Swales\\|Swales]] |", _den.Read(Outline), StringComparison.Ordinal);

        Assert.Equal([Swales, BookFile, Outline, Note], result.Files!.Select(f => f.Path));
        // Two chapters had that pov line, so seven links: one in Swales, three in the note, two in book.md, one in the outline.
        Assert.Contains(result.Effects, e => e.Contains("rewrote 7 link(s)", StringComparison.Ordinal));
        Assert.Contains(result.Effects, e => e.Contains("keeps its name, mr-swales.md", StringComparison.Ordinal));
        Assert.Contains(result.Effects, e => e.Contains("'# Mr. Swales', now reads '# Old Swales'", StringComparison.Ordinal));
        Assert.DoesNotContain(Tools.Check().Diagnostics, d => d.Rule is "F-LINK-8" or "F-LINK-7");
        Assert.Equal(result.Hash, Tools.GetEntity("Old Swales").Entity!.Hash);
        Assert.Equal(result.Files![3].Hash, Tools.GetEntity("Whitby dialect").Entity!.Hash);

        // The old hash is stale now, and an id is still never changed.
        Assert.Equal(ToolErrorCodes.StaleFile, Assert.Throws<DenToolException>(() => Tools.RenameEntity("Old Swales", "Swales", hash)).Code);
        Assert.Equal(
            ToolErrorCodes.BadArgument,
            Assert.Throws<DenToolException>(() => Tools.UpdateFields("Old Swales", result.Hash, Json("""[{"op": "set", "path": "id", "value": "01M4TF5F0EKGGWD5FNVBAEY8ZZ"}]"""))).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void KeepingTheOldNameAsAnAliasLeavesTheLinksAlone()
    {
        const string Text = "See [[Mr. Swales]].\n";
        WriteNote(Text);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.RenameEntity("Mr. Swales", "Old Swales", hash, keepOldName: true);

        var swales = _den.Read(Swales);
        Assert.Contains("\nname: Old Swales\naliases: [Mr. Swales]\n", swales, StringComparison.Ordinal);
        Assert.EndsWith(Text, _den.Read(Note), StringComparison.Ordinal);
        Assert.Equal([Swales], result.Files!.Select(f => f.Path));
        Assert.Contains(result.Effects, e => e.Contains("kept 'Mr. Swales' as an alias", StringComparison.Ordinal));
        Assert.DoesNotContain(Tools.Check().Diagnostics, d => d.Rule is "F-LINK-8" or "F-LINK-7");

        // A change of case or spacing only needs no link to change.
        var again = Tools.RenameEntity("Old Swales", "old swales", result.Hash);
        Assert.Equal([Swales], again.Files!.Select(f => f.Path));
        Assert.Contains(again.Effects, e => e.Contains("F-LINK-4", StringComparison.Ordinal));
        Assert.False(Tools.RenameEntity("old swales", "old swales", again.Hash).Changed);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void ALinkInAFrozenBookStopsTheRewriteButNotKeepingTheOldName()
    {
        _den.Write("universes/dracula/books/aftermath/book.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z2\nname: Aftermath\nstatus: final\n---\n");
        _den.Write("universes/dracula/books/aftermath/entities/notes/memories.md",
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z3\ntype: note\nname: Memories\nstatus: draft\n---\n\nRecall [[Mr. Swales]].\n");
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.RenameEntity("Mr. Swales", "Old Swales", hash));

        Assert.Equal(ToolErrorCodes.Frozen, refused.Code);
        Assert.Contains("aftermath/entities/notes/memories.md", refused.Message, StringComparison.Ordinal);
        Assert.Contains("keep_old_name", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(Swales));

        var kept = Tools.RenameEntity("Mr. Swales", "Old Swales", hash, keepOldName: true);
        Assert.True(kept.Changed);
        Assert.Contains("Recall [[Mr. Swales]].", _den.Read("universes/dracula/books/aftermath/entities/notes/memories.md"), StringComparison.Ordinal);

        // An entity in a final book isn't renamed at all.
        _den.Write(BookFile, _den.Read(BookFile).Replace("status: draft", "status: final", StringComparison.Ordinal));
        Assert.Equal(ToolErrorCodes.Frozen, Assert.Throws<DenToolException>(() => Tools.RenameEntity("Old Swales", "Swales", kept.Hash)).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void AKeptAliasThatAnotherEntityHasIsSaidToBeAmbiguous()
    {
        // Another character in the book goes by "Mr. Swales" too; the note is in the universe, so scope can't choose.
        _den.Write(Book + "/entities/characters/young-swales.md",
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z4\ntype: character\nname: Young Swales\naliases: [Mr. Swales]\nstatus: draft\n---\n");
        WriteNote("See [[Mr. Swales]].\n");
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.RenameEntity("mr-swales", "Old Swales", hash, keepOldName: true);

        Assert.Contains(result.Effects, e => e.Contains("also the alias of 'Young Swales'", StringComparison.Ordinal) && e.Contains("F-LINK-7", StringComparison.Ordinal));
        Assert.Contains(
            result.Effects,
            e => e.Contains($"[[Mr. Swales]] in {Note} (line 8) is now ambiguous (F-LINK-7)", StringComparison.Ordinal)
                && e.Contains("before, it reached 'Mr. Swales'", StringComparison.Ordinal));
        Assert.Contains(Tools.Check().Diagnostics, d => d.Rule == "F-LINK-7" && d.Path == Note);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void ANewNameThatWouldSendARewrittenLinkElsewhereIsRefused()
    {
        WriteNote("See [[Mr. Swales]].\n");
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        // "Renfield" is R. M. Renfield's file name, and a file name matches before a name.
        var elsewhere = Assert.Throws<DenToolException>(() => Tools.RenameEntity("Mr. Swales", "Renfield", hash));
        Assert.Equal(ToolErrorCodes.BadArgument, elsewhere.Code);
        Assert.Contains("would reach 'R. M. Renfield'", elsewhere.Message, StringComparison.Ordinal);

        // A link can't hold a #.
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.RenameEntity("Mr. Swales", "Swales #1", hash)).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.RenameEntity("Mr. Swales", " ", hash)).Code);
        Assert.Equal(before, _den.Read(Swales));

        // update_fields points at the rename tools.
        var refused = Assert.Throws<DenToolException>(() => Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "set", "path": "name", "value": "Swales"}]""")));
        Assert.Contains("rename_entity", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void AFileWithErrorsThatUsesTheOldNameIsNamed()
    {
        const string Broken = "universes/dracula/entities/notes/broken.md";
        _den.Write(Broken, "---\ntype: note\nname: Broken\nstatus: maybe\n---\n\nSee [[Mr. Swales]].\n");
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.RenameEntity("Mr. Swales", "Old Swales", hash);

        Assert.Contains(result.Effects, e => e.Contains(Broken, StringComparison.Ordinal) && e.Contains("by hand", StringComparison.Ordinal));
        Assert.Contains("[[Mr. Swales]]", _den.Read(Broken), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-27")]
    public void ChangingOrRemovingAnAliasRewritesItsLinks()
    {
        WriteNote("Beware [[the Count]], [[The  count|him]], [[Count de Ville]], and [[Dracula]].\n");
        var hash = Tools.GetEntity("Count Dracula").Entity!.Hash!;

        var changed = Tools.ChangeAlias("Count Dracula", "the count", hash, newAlias: "the Voivode");

        Assert.Contains("\naliases: [Dracula, the Voivode, Count de Ville]\n", _den.Read(Count), StringComparison.Ordinal);
        Assert.EndsWith("Beware [[the Voivode]], [[the Voivode|him]], [[Count de Ville]], and [[Dracula]].\n", _den.Read(Note), StringComparison.Ordinal);
        Assert.Contains(changed.Effects, e => e.Contains("rewrote 2 link(s)", StringComparison.Ordinal));

        var removed = Tools.ChangeAlias("Count Dracula", "Count de Ville", changed.Hash);
        Assert.Contains("\naliases: [Dracula, the Voivode]\n", _den.Read(Count), StringComparison.Ordinal);
        Assert.Contains("[[Count Dracula]], and [[Dracula]]", _den.Read(Note), StringComparison.Ordinal);
        Assert.Contains(removed.Effects, e => e.Contains("Removed the alias 'Count de Ville'", StringComparison.Ordinal) && e.Contains("its name", StringComparison.Ordinal));

        var kept = Tools.ChangeAlias("Count Dracula", "the Voivode", removed.Hash, newAlias: "the Count", keepOldAlias: true);
        Assert.Contains("\naliases: [Dracula, the Voivode, the Count]\n", _den.Read(Count), StringComparison.Ordinal);
        Assert.Equal([Count], kept.Files!.Select(f => f.Path));
        Assert.DoesNotContain(Tools.Check().Diagnostics, d => d.Rule is "F-LINK-8" or "F-LINK-7");

        // An alias it doesn't have, one it already goes by, and keeping an alias that's removed are refused.
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.ChangeAlias("Count Dracula", "Nosferatu", kept.Hash, newAlias: "x")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.ChangeAlias("Count Dracula", "the Count", kept.Hash, newAlias: "Count Dracula")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.ChangeAlias("Count Dracula", "the Count", kept.Hash, keepOldAlias: true)).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.ChangeAlias("Count Dracula", "the Count", kept.Hash, newAlias: "a\u0001b")).Code);

        // update_fields points at change_alias.
        var refused = Assert.Throws<DenToolException>(() => Tools.UpdateFields("Count Dracula", kept.Hash, Json("""[{"op": "remove", "path": "aliases.0"}]""")));
        Assert.Contains("change_alias", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-27")]
    public void RemovingAnAliasALinkInAFrozenBookUsesIsRefused()
    {
        _den.Write("universes/dracula/books/aftermath/book.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z2\nname: Aftermath\nstatus: final\n---\n");
        _den.Write("universes/dracula/books/aftermath/entities/notes/memories.md",
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z3\ntype: note\nname: Memories\nstatus: draft\n---\n\nRecall [[Count de Ville]].\n");
        var before = _den.Read(Count);
        var hash = Tools.GetEntity("Count Dracula").Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.ChangeAlias("Count Dracula", "Count de Ville", hash));

        Assert.Equal(ToolErrorCodes.Frozen, refused.Code);
        Assert.Equal(before, _den.Read(Count));
        Assert.Contains("keep_old_alias", Assert.Throws<DenToolException>(() => Tools.ChangeAlias("Count Dracula", "Count de Ville", hash, newAlias: "de Ville")).Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void ARenameThatFailsPartWayPutsBackWhatItWrote()
    {
        WriteNote("See [[Mr. Swales]].\n");
        var before = (Swales: _den.Read(Swales), Note: _den.Read(Note));
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        // Swales's file sorts first, so it's written before the note's write fails.
        var tools = new DenTools(_den.Root) { Store = new WriteToolTests.FailingStore(_den.Root, Note) };
        var failed = Assert.Throws<DenToolException>(() => tools.RenameEntity("Mr. Swales", "Old Swales", hash));

        Assert.Equal(ToolErrorCodes.WriteFailed, failed.Code);
        Assert.Equal(before.Swales, _den.Read(Swales));
        Assert.Equal(before.Note, _den.Read(Note));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void RenamingToOneOfItsOwnAliasesTakesThatAliasOut()
    {
        WriteNote("Beware [[Dracula]].\n");
        var hash = Tools.GetEntity("Count Dracula").Entity!.Hash!;

        var result = Tools.RenameEntity("Count Dracula", "Dracula", hash);

        var count = _den.Read(Count);
        Assert.Contains("\nname: Dracula\naliases: [the Count, Count de Ville]\n", count, StringComparison.Ordinal);
        Assert.Contains(result.Effects, e => e.Contains("'Dracula' was one of its aliases", StringComparison.Ordinal));
        Assert.EndsWith("Beware [[Dracula]].\n", _den.Read(Note), StringComparison.Ordinal);
        Assert.DoesNotContain(Tools.Check().Diagnostics, d => d.Rule is "F-LINK-8" or "F-LINK-7");
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void ANewNameThatCollidesIsSaidEvenWhenTheOldNameIsKept()
    {
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.RenameEntity("Mr. Swales", "Renfield", hash, keepOldName: true);

        Assert.Contains(result.Effects, e => e.Contains("'Renfield' is also the file name of 'R. M. Renfield'", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-27")]
    public void RemovingAnAliasWhoseLinksWouldReachAnotherEntityIsRefusedWithItsOwnHint()
    {
        // A legend note in the universe is also named Count Dracula: from the universe, [[Count Dracula]] reaches it.
        _den.Write("universes/dracula/entities/notes/the-legend.md",
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z5\ntype: note\nname: Count Dracula\nstatus: draft\n---\n");
        WriteNote("Beware [[the Count]].\n");
        var before = _den.Read(Count);
        var hash = Tools.GetEntity(Count).Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.ChangeAlias(Count, "the Count", hash));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains("wouldn't reach 'Count Dracula'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("keep the alias, or change those links to the entity's id or file name first", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unfreeze", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(Count));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void ABookEntityWithTheNewNameShadowsItForLinksInTheBook()
    {
        const string Sailor = "universes/dracula/entities/characters/the-sailor.md";
        const string BookNote = Book + "/entities/notes/harbour.md";
        _den.Write(Sailor, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z6\ntype: character\nname: The Sailor\nstatus: draft\n---\n");
        _den.Write(BookNote, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z7\ntype: note\nname: Harbour\nstatus: draft\n---\n\nSee [[The Sailor]].\n");
        WriteNote("See [[The Sailor]].\n");
        var hash = Tools.GetEntity(Sailor).Entity!.Hash!;

        // In the book, [[Mr. Swales]] reaches Swales, the nearer scope (F-LINK-7); in the universe, the sailor.
        var refused = Assert.Throws<DenToolException>(() => Tools.RenameEntity(Sailor, "Mr. Swales", hash));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains($"[[Mr. Swales]] in {BookNote}", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Note, refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("See [[The Sailor]].\n", _den.Read(BookNote), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void LinksInCodeCommentsAndEmbedsAreLeftAlone()
    {
        const string Text = "Code `[[Mr. Swales]]`, <!-- [[Mr. Swales]] -->, ![[Mr. Swales]], \\[[Mr. Swales]].\n\n```\n[[Mr. Swales]]\n```\n\nBut [[Mr. Swales]].\n";
        WriteNote(Text);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.RenameEntity("Mr. Swales", "Old Swales", hash);

        Assert.EndsWith(Text.Replace("But [[Mr. Swales]]", "But [[Old Swales]]", StringComparison.Ordinal), _den.Read(Note), StringComparison.Ordinal);
        Assert.Contains(result.Effects, e => e.Contains("rewrote 1 link(s)", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void LinksInFrontmatterValuesKeepTheirStyleOrAreQuotedAnew()
    {
        _den.Write(Note,
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\nfields:\n" +
            "  x_block: |\n    Read [[Mr. Swales]] aloud.\n" +
            "  x_folded: >\n    Folded [[Mr. Swales]]\n    text.\n" +
            "  x_quoted: 'it''s [[Mr. Swales]]'\n" +
            "  x_plain: see [[Mr. Swales]] now\n---\n\n# Whitby dialect\n");
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        // A colon and space would end a plain scalar's key, so that value is written anew, quoted.
        Tools.RenameEntity("Mr. Swales", "Swales: the elder", hash);

        var text = _den.Read(Note);
        Assert.Contains("  x_block: |\n    Read [[Swales: the elder]] aloud.\n", text, StringComparison.Ordinal);
        Assert.Contains("  x_folded: >\n    Folded [[Swales: the elder]]\n    text.\n", text, StringComparison.Ordinal);
        Assert.Contains("  x_quoted: 'it''s [[Swales: the elder]]'\n", text, StringComparison.Ordinal);
        Assert.Contains("  x_plain: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("  x_plain: see [[Swales: the elder]] now\n", text, StringComparison.Ordinal);
        Assert.Contains("see [[Swales: the elder]] now", text, StringComparison.Ordinal);
        Assert.Equal(0, Tools.Check(path: Note).Errors);
        Assert.DoesNotContain(Tools.Check().Diagnostics, d => d.Rule is "F-LINK-8" or "F-LINK-7");
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void AContainerFileChangedWhileTheRenameIsMadeStopsIt()
    {
        _den.Write(BookFile, _den.Read(BookFile).Replace(
            "      pov: [\"[[mina-harker]]\", \"[[john-seward]]\"]\n", "      pov: [\"[[mina-harker]]\", \"[[Mr. Swales]]\"]\n", StringComparison.Ordinal));
        var before = (Swales: _den.Read(Swales), Book: _den.Read(BookFile));
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;
        var theirs = before.Book + "# Saved in an editor.\n";

        // The writer saves book.md after the rename is planned, just as it's about to be written.
        var tools = new DenTools(_den.Root) { Store = new ReadHookStore(_den.Root, Swales, 1, () => _den.Write(BookFile, theirs)) };
        var stale = Assert.Throws<DenToolException>(() => tools.RenameEntity("Mr. Swales", "Old Swales", hash));

        Assert.Equal(ToolErrorCodes.StaleFile, stale.Code);
        Assert.Contains(BookFile, stale.Message, StringComparison.Ordinal);
        Assert.Equal(before.Swales, _den.Read(Swales));
        Assert.Equal(theirs, _den.Read(BookFile));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void ANameThatTakesAnotherEntitysLinksIsRefusedUnlessAllowed()
    {
        WriteNote("Beware [[the Count]].\n");
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        // A name matches before an alias, so [[the Count]] would reach Swales.
        var refused = Assert.Throws<DenToolException>(() => Tools.RenameEntity("Mr. Swales", "the Count", hash));
        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains($"[[the Count]] in {Note} (line 8) reaches 'Count Dracula'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("allow_relinks", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(Swales));

        // Keeping the old name doesn't help: the new name still takes the links.
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.RenameEntity("Mr. Swales", "the Count", hash, keepOldName: true)).Code);

        var allowed = Tools.RenameEntity("Mr. Swales", "the Count", hash, allowRelinks: true);
        Assert.Contains(
            allowed.Effects,
            e => e.Contains($"[[the Count]] in {Note} (line 8) now reaches 'the Count' ({Swales}); before, it reached 'Count Dracula' ({Count})", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void ARelinkInAFinalBookIsListedAndAllowedOnlyWithTheFlag()
    {
        const string Memories = "universes/dracula/books/aftermath/entities/notes/memories.md";
        _den.Write("universes/dracula/books/aftermath/book.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z2\nname: Aftermath\nstatus: final\n---\n");
        _den.Write(Memories, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z3\ntype: note\nname: Memories\nstatus: draft\n---\n\nRecall [[the Count]].\n");
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.RenameEntity("Mr. Swales", "the Count", hash));
        Assert.Contains($"[[the Count]] in {Memories} (line 8) (in a final book)", refused.Message, StringComparison.Ordinal);

        // For now (maintainer decision, 2026-10-26) the flag covers final books too; the frozen file isn't written.
        var allowed = Tools.RenameEntity("Mr. Swales", "the Count", hash, allowRelinks: true);
        Assert.Equal([Swales], allowed.Files!.Select(f => f.Path));
        Assert.EndsWith("Recall [[the Count]].\n", _den.Read(Memories), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-27")]
    public void AnAliasThatTakesAnotherEntitysLinksIsRefusedUnlessAllowed()
    {
        WriteNote("See [[Lucy]].\n");
        var hash = Tools.GetEntity("Count Dracula").Entity!.Hash!;

        // Lucy Westenra goes by "Lucy" too: the link would become ambiguous.
        var refused = Assert.Throws<DenToolException>(() => Tools.ChangeAlias("Count Dracula", "Count de Ville", hash, newAlias: "Lucy"));
        Assert.Contains("reaches 'Lucy Westenra'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(
            ToolErrorCodes.BadArgument,
            Assert.Throws<DenToolException>(() => Tools.ChangeAlias("Count Dracula", "Count de Ville", hash, newAlias: "Lucy", keepOldAlias: true)).Code);

        var allowed = Tools.ChangeAlias("Count Dracula", "Count de Ville", hash, newAlias: "Lucy", allowRelinks: true);
        Assert.Contains(allowed.Effects, e => e.Contains("is now ambiguous (F-LINK-7)", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void AKeptNameThatLosesItsLinksToAnotherEntitysNameIsRefusedUnlessAllowed()
    {
        // A sailor in the universe is also named Mr. Swales. From the book, [[Mr. Swales]] reaches the book's Swales,
        // the nearer scope; kept only as his alias, the old name loses to the sailor's name (F-LINK-6).
        const string Sailor = "universes/dracula/entities/characters/the-sailor.md";
        const string BookNote = Book + "/entities/notes/harbour.md";
        _den.Write(Sailor, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z6\ntype: character\nname: Mr. Swales\nstatus: draft\n---\n");
        _den.Write(BookNote, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z7\ntype: note\nname: Harbour\nstatus: draft\n---\n\nSee [[Mr. Swales]].\n");
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity(Swales).Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.RenameEntity(Swales, "Old Swales", hash, keepOldName: true));
        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains($"[[Mr. Swales]] in {BookNote} (line 8) reaches 'Mr. Swales' ({Swales}) and would reach 'Mr. Swales' ({Sailor})", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(Swales));

        var allowed = Tools.RenameEntity(Swales, "Old Swales", hash, keepOldName: true, allowRelinks: true);
        Assert.Contains(allowed.Effects, e => e.Contains("by alias now, except those listed below", StringComparison.Ordinal));
        Assert.Contains(allowed.Effects, e => e.Contains($"[[Mr. Swales]] in {BookNote} (line 8) now reaches 'Mr. Swales' ({Sailor})", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-27")]
    public void AnAmbiguousLinkThatNowResolvesIsReportedNotRefused()
    {
        // Swales and Young Swales both go by "the sailor", and neither is in the universe note's scope chain (F-LINK-7).
        _den.Write(Swales, _den.Read(Swales).Replace("name: Mr. Swales\n", "name: Mr. Swales\naliases: [the sailor]\n", StringComparison.Ordinal));
        _den.Write(Book + "/entities/characters/young-swales.md",
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z4\ntype: character\nname: Young Swales\naliases: [the sailor]\nstatus: draft\n---\n");
        WriteNote("See [[the sailor]].\n");
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.ChangeAlias("Mr. Swales", "the sailor", hash, newAlias: "the old sailor");

        Assert.EndsWith("See [[the sailor]].\n", _den.Read(Note), StringComparison.Ordinal);
        Assert.Contains(
            result.Effects,
            e => e.Contains($"[[the sailor]] in {Note} (line 8) now reaches 'Young Swales'", StringComparison.Ordinal) && e.Contains("before, it was ambiguous", StringComparison.Ordinal));
    }

    private void WriteNote(string text) =>
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\n" + text);

    private static System.Text.Json.JsonElement Json(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A den store on disk that runs <paramref name="hook"/> once, on the <paramref name="call"/>th read of <paramref name="path"/>.</summary>
    private sealed class ReadHookStore(string root, string path, int call, Action hook) : Core.Structure.IDenStore
    {
        private readonly Core.Structure.FileSystemDenStore _disk = new(root);
        private int _reads;

        public bool IsReadOnly => false;

        public bool FileExists(string p) => _disk.FileExists(p);

        public byte[] ReadFile(string p) => _disk.ReadFile(p);

        public byte[]? TryReadFile(string p)
        {
            if (p == path && ++_reads == call)
            {
                hook();
            }

            return _disk.TryReadFile(p);
        }

        public byte[]? TryReadListedFile(string p) => _disk.TryReadListedFile(p);

        public IEnumerable<string> GetDirectories(string p) => _disk.GetDirectories(p);

        public IEnumerable<string> GetFiles(string p) => _disk.GetFiles(p);

        public bool FolderExists(string p) => _disk.FolderExists(p);

        public void WriteFile(string p, byte[] bytes, Core.Structure.DenWriteMode mode = Core.Structure.DenWriteMode.CreateOrReplace) => _disk.WriteFile(p, bytes, mode);

        public void DeleteFile(string p) => _disk.DeleteFile(p);

        public void MoveFile(string from, string to) => _disk.MoveFile(from, to);

        public void MoveFolder(string from, string to) => _disk.MoveFolder(from, to);

        public void DeleteFolder(string p) => _disk.DeleteFolder(p);
    }
}
