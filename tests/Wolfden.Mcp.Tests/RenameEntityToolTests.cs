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
    [Trait("Scenario", "SC-MCP-24")]
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
    [Trait("Scenario", "SC-MCP-24")]
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
    [Trait("Scenario", "SC-MCP-24")]
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
    [Trait("Scenario", "SC-MCP-24")]
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
    [Trait("Scenario", "SC-MCP-24")]
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
    [Trait("Scenario", "SC-MCP-24")]
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
    [Trait("Scenario", "SC-MCP-25")]
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
    [Trait("Scenario", "SC-MCP-25")]
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
    [Trait("Scenario", "SC-MCP-24")]
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

    private void WriteNote(string text) =>
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\n" + text);

    private static System.Text.Json.JsonElement Json(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
