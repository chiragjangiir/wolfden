using Wolfden.Core.Structure;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// <c>copy_entity</c> (#129) over a copy of the Dracula sample: the copy gets
/// a new id, name, and file, keeps everything else, and never takes another
/// entity's links unasked.
/// </summary>
public sealed class CopyEntityToolTests : IDisposable
{
    private const string Book = "universes/dracula/books/dracula";
    private const string Swales = Book + "/entities/characters/mr-swales.md";
    private const string Count = Book + "/entities/characters/count-dracula.md";
    private const string Note = "universes/dracula/entities/notes/whitby-dialect.md";
    private const string Aftermath = "universes/dracula/books/aftermath";

    private readonly TempDen _den = TempDen.Dracula();

    private DenTools Tools => new(_den.Root);

    public void Dispose() => _den.Dispose();

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void ACopyGetsANewIdNameAndFileAndKeepsTheRest()
    {
        var original = _den.Read(Count);
        var ids = Tools.Check();
        Assert.DoesNotContain(ids.Diagnostics, d => d.Rule == "F-ID-3");

        var result = Tools.CopyEntity("Count Dracula", "Count Varga");

        const string Copy = Book + "/entities/characters/count-varga.md";
        Assert.True(result.Changed);
        Assert.Equal(Copy, result.Path);
        Assert.True(Assert.Single(result.Files!).Created);
        var copy = _den.Read(Copy);
        var id = copy.Split('\n')[1]["id: ".Length..];
        Assert.True(Keys.IsId(id), id);
        Assert.NotEqual("01M4TF5EZZJ0FZ8PAAT1WY4GR4", id);

        // Only the id, the name, the aliases, and the title differ.
        var expected = original
            .Replace("id: 01M4TF5EZZJ0FZ8PAAT1WY4GR4\n", $"id: {id}\n", StringComparison.Ordinal)
            .Replace("name: Count Dracula\naliases: [Dracula, the Count, Count de Ville]\n", "name: Count Varga\n", StringComparison.Ordinal)
            .Replace("\n# Count Dracula\n", "\n# Count Varga\n", StringComparison.Ordinal);
        Assert.Equal(expected, copy);
        Assert.Equal(original, _den.Read(Count));

        Assert.Contains(result.Effects, e => e.Contains($"with the new id {id}", StringComparison.Ordinal) && e.Contains("beside it", StringComparison.Ordinal));
        Assert.Contains(result.Effects, e => e.Contains("aliases ('Dracula', 'the Count', 'Count de Ville') weren't copied", StringComparison.Ordinal));
        Assert.Contains(result.Effects, e => e.Contains("title heading reads '# Count Varga'", StringComparison.Ordinal));
        Assert.Equal(result.Hash, Tools.GetEntity("Count Varga").Entity!.Hash);
        var check = Tools.Check();
        Assert.DoesNotContain(check.Diagnostics, d => d.Rule is "F-ID-3" or "F-LINK-7" or "F-LINK-8");

        // Another copy of the same name gets -2; a slug asked for that's taken is refused.
        Assert.Equal(Book + "/entities/characters/count-varga-2.md", Tools.CopyEntity("Count Dracula", "Count Varga").Path);
        Assert.Equal(ToolErrorCodes.AlreadyExists, Assert.Throws<DenToolException>(() => Tools.CopyEntity("Count Dracula", "Varga", slug: "count-varga")).Code);
        Assert.Equal(Book + "/entities/characters/varga.md", Tools.CopyEntity("Count Dracula", "Count Varga III", slug: "varga").Path);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void LinksInTheCopyAreLeftAsTheyAreAndThoseToTheOriginalAreSaid()
    {
        _den.Write(Swales, _den.Read(Swales).Replace("as she hears it.\n", "as she hears it. See [[Mr. Swales#Arc]].\n", StringComparison.Ordinal));

        var result = Tools.CopyEntity("Mr. Swales", "Old Gale");

        var copy = _den.Read(result.Path);
        Assert.Contains("as she hears it. See [[Mr. Swales#Arc]].\n", copy, StringComparison.Ordinal);
        Assert.Contains("A very old man of [[whitby]]", copy, StringComparison.Ordinal);
        Assert.Contains(result.Effects, e => e.Contains("1 link(s) in the copy reach the original, 'Mr. Swales'", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Effects, e => e.Contains("don't reach what they reach", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void ACopyIntoAnotherScopeKeepsItsTypeFolderAndSaysWhereItEntersCanon()
    {
        WriteAftermath("draft");

        // Swales enters at dracula:6, which is outside the new book.
        var swales = Tools.CopyEntity("Mr. Swales", "Young Swales", toBook: "aftermath");

        Assert.Equal(Aftermath + "/entities/characters/young-swales.md", swales.Path);
        Assert.Contains("from: \"dracula:6\"\n", _den.Read(swales.Path), StringComparison.Ordinal);
        Assert.Contains(swales.Effects, e => e.Contains("in book 'aftermath' of universe 'dracula'", StringComparison.Ordinal));
        Assert.DoesNotContain(swales.Effects, e => e.Contains("Its 'from' is still dracula:6", StringComparison.Ordinal));
        Assert.Contains(swales.Effects, e => e.Contains("is in book 'dracula', outside book 'aftermath'", StringComparison.Ordinal) && e.Contains("F-ENT-6", StringComparison.Ordinal));
        Assert.Contains(Tools.Check().Diagnostics, d => d.Rule == "F-ENT-6" && d.Path == swales.Path);

        // A note without 'from' enters at the start of where it's filed now.
        var dates = Tools.CopyEntity("Dates and the year", "Dates after the story", toBook: "aftermath");

        Assert.Equal(Aftermath + "/entities/notes/dates-after-the-story.md", dates.Path);
        Assert.Contains(
            dates.Effects,
            e => e.Contains("no 'from', so it enters canon at the start of book 'aftermath' (F-ENT-6); the original enters at the start of the story", StringComparison.Ordinal));

        // Into the universe, with to_universe alone; a book or series it lacks, or both, are refused.
        var up = Tools.CopyEntity("Mr. Swales", "Swales the elder", toUniverse: "dracula");
        Assert.Equal("universes/dracula/entities/characters/swales-the-elder.md", up.Path);
        Assert.Contains(up.Effects, e => e.Contains("in universe 'dracula'", StringComparison.Ordinal));
        Assert.Equal(ToolErrorCodes.UnknownScope, Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", "X", toBook: "sequel")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", "X", toSeries: "a", toBook: "aftermath")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void NothingIsCopiedIntoAFinalBookButACopyOutOfOneIsFine()
    {
        WriteAftermath("final");
        var before = DenFiles();

        var refused = Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", "Young Swales", toBook: "aftermath"));

        Assert.Equal(ToolErrorCodes.Frozen, refused.Code);
        Assert.Contains("'aftermath' is final", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_den.Root, Aftermath, "entities", "characters", "young-swales.md")));

        // The Dracula book frozen: Swales can be copied out of it, but not beside himself.
        _den.Write(Book + "/book.md", _den.Read(Book + "/book.md").Replace("status: draft", "status: final", StringComparison.Ordinal));
        Assert.Equal(ToolErrorCodes.Frozen, Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", "Young Swales")).Code);
        var copy = Tools.CopyEntity("Mr. Swales", "Young Swales", toUniverse: "dracula");
        Assert.Equal("universes/dracula/entities/characters/young-swales.md", copy.Path);
        Assert.Equal(before + 1, DenFiles());
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void ACopyIntoAnotherUniverseListsTheLinksThatBreak()
    {
        _den.Write("universes/legend/universe.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A1\nname: Legend\n---\n");

        var result = Tools.CopyEntity("Mr. Swales", "Mr. Swales", universe: "dracula", toUniverse: "legend");

        Assert.Equal("universes/legend/entities/characters/mr-swales.md", result.Path);
        Assert.Contains(result.Effects, e => e.Contains("It's in universe 'legend' now", StringComparison.Ordinal));
        Assert.Contains(result.Effects, e => e.Contains("1 link(s) in the copy don't reach what they reach in the original, 1 of them broken (F-LINK-8)", StringComparison.Ordinal));
        Assert.Contains(
            result.Effects,
            e => e.Contains($"[[whitby]] in {result.Path}", StringComparison.Ordinal) && e.Contains("is now broken", StringComparison.Ordinal)
                && e.Contains("in the original, it reached 'Whitby'", StringComparison.Ordinal));
        Assert.Contains(result.Effects, e => e.Contains("2 position(s) in it don't resolve where it is now", StringComparison.Ordinal)
            && e.Contains("dracula:6, dracula:7", StringComparison.Ordinal));
        Assert.Contains(Tools.Check().Diagnostics, d => d.Rule == "F-LINK-8" && d.Path == result.Path);

        // The same name in another universe takes no link from the original.
        Assert.DoesNotContain(Tools.Check().Diagnostics, d => d.Rule is "F-ID-3" or "F-LINK-7");
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void ACopyThatWouldTakeAnotherEntitysLinksIsRefusedUnlessAllowed()
    {
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A2\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\nSee [[the Count]].\n");
        var before = DenFiles();

        // A name matches before an alias, so a copy named "the Count" would take the note's link.
        var refused = Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", "the Count"));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains($"[[the Count]] in {Note} (line 8) reaches 'Count Dracula'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("allow_relinks", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, DenFiles());

        var allowed = Tools.CopyEntity("Mr. Swales", "the Count", allowRelinks: true);

        Assert.Contains(allowed.Effects, e => e.Contains($"[[the Count]] in {Note} (line 8) now reaches 'the Count' ({allowed.Path})", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void ANameThatMakesALinkAmbiguousIsRefusedUnlessAllowedButMendingABrokenOneIsReported()
    {
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A2\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\nSee [[Mr. Swales]] and [[Old Gale]].\n");
        var before = DenFiles();

        // A second Mr. Swales beside the first would leave the note's link reaching neither (F-LINK-7).
        var refused = Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", "Mr. Swales"));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.StartsWith("1 link(s) would change which entity they reach: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"[[Mr. Swales]] in {Note} (line 8) reaches 'Mr. Swales' ({Swales}) and would be ambiguous", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, DenFiles());

        var same = Tools.CopyEntity("Mr. Swales", "Mr. Swales", allowRelinks: true);

        Assert.Equal(Book + "/entities/characters/mr-swales-2.md", same.Path);
        Assert.Contains(same.Effects, e => e.Contains("also the name of 'Mr. Swales'", StringComparison.Ordinal) && e.Contains("F-LINK-7", StringComparison.Ordinal));
        Assert.Contains(same.Effects, e => e.Contains($"[[Mr. Swales]] in {Note} (line 8) is now ambiguous (F-LINK-7)", StringComparison.Ordinal));

        var mended = Tools.CopyEntity("mr-swales", "Old Gale");

        Assert.Contains(mended.Effects, e => e.Contains($"[[Old Gale]] in {Note} (line 8) now reaches 'Old Gale'", StringComparison.Ordinal) && e.Contains("was broken", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void ANoteWithoutAnIdGetsOneAndAStaleOrBadCopyIsRefused()
    {
        _den.Write(Note, "---\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\nNotes.\n");

        var copy = Tools.CopyEntity("Whitby dialect", "Yorkshire dialect");

        var text = _den.Read(copy.Path);
        Assert.StartsWith("---\nid: ", text, StringComparison.Ordinal);
        Assert.True(Keys.IsId(text.Split('\n')[1]["id: ".Length..]));
        Assert.DoesNotContain("id:", _den.Read(Note), StringComparison.Ordinal);

        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;
        Assert.True(Tools.CopyEntity("Mr. Swales", "Old Gale", expectedHash: hash).Changed);
        _den.Write(Swales, _den.Read(Swales) + "\nMore.\n");
        Assert.Equal(ToolErrorCodes.StaleFile, Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", "Old Gale", expectedHash: hash)).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", " ")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", "X", slug: "Not A Slug")).Code);
        Assert.Equal(ToolErrorCodes.UnknownEntity, Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Nobody", "X")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void APinnedSessionCantCopyIntoAnotherUniverse()
    {
        _den.Write("universes/legend/universe.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A1\nname: Legend\n---\n");
        var pinned = new DenTools(_den.Root) { PinnedUniverse = "dracula" };

        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => pinned.CopyEntity("Mr. Swales", "X", toUniverse: "legend")).Code);
        Assert.True(pinned.CopyEntity("Mr. Swales", "X").Changed);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void ANewIdThatClashesIsTriedAgain()
    {
        var ids = new Queue<string>(["01M4TF5EZZJ0FZ8PAAT1WY4GR4", "01M4TF5F0EKGGWD5FNVBAEY9A9"]);
        var tools = new DenTools(_den.Root) { NewId = ids.Dequeue };

        var result = tools.CopyEntity("Mr. Swales", "Old Gale");

        Assert.StartsWith("---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A9\n", _den.Read(result.Path), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void AFileNameThatTiesInTheSameScopeIsRefusedUnlessAllowed()
    {
        // A universe note filed as notes/whitby.md would tie with the location whitby.md for every [[whitby]].
        var before = DenFiles();

        var refused = Assert.Throws<DenToolException>(() => Tools.CopyEntity("Dates and the year", "Whitby notes", slug: "whitby"));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains($"[[whitby]] in {Swales} (line 17) reaches 'Whitby' (universes/dracula/entities/locations/whitby.md) and would be ambiguous", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, DenFiles());

        var allowed = Tools.CopyEntity("Dates and the year", "Whitby notes", slug: "whitby", allowRelinks: true);

        Assert.Equal("universes/dracula/entities/notes/whitby.md", allowed.Path);
        Assert.Contains(allowed.Effects, e => e.Contains($"[[whitby]] in {Swales} (line 17) is now ambiguous (F-LINK-7)", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void AFileNameInANearerScopeThatShadowsAnotherIsRefusedUnlessAllowed()
    {
        // Filed in the book as whitby.md, the copy would take the book's [[whitby]] links from the universe's Whitby.
        var refused = Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", "Whitby Swales", slug: "whitby"));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains($"[[whitby]] in {Swales} (line 17) reaches 'Whitby' (universes/dracula/entities/locations/whitby.md) and would reach 'Whitby Swales'", refused.Message, StringComparison.Ordinal);

        var allowed = Tools.CopyEntity("Mr. Swales", "Whitby Swales", slug: "whitby", allowRelinks: true);
        Assert.Contains(allowed.Effects, e => e.Contains($"[[whitby]] in {Swales} (line 17) now reaches 'Whitby Swales' ({allowed.Path})", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void ACopyKeepsLineEndingsAndAByteOrderMark()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var text = "---\r\nid: 01M4TF5F0EKGGWD5FNVBAEY9A3\r\ntype: note\r\nname: Whitby dialect\r\naliases: [Dialect]\r\nstatus: draft\r\n---\r\n\r\n# Whitby dialect\r\n\r\nSee [[whitby]].";
        var full = Path.Combine(_den.Root, Note.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [.. bom, .. System.Text.Encoding.UTF8.GetBytes(text)]);

        var result = Tools.CopyEntity("Whitby dialect", "Yorkshire dialect");

        var bytes = File.ReadAllBytes(Path.Combine(_den.Root, result.Path.Replace('/', Path.DirectorySeparatorChar)));
        var id = result.Entity!.Id!;
        var expected = text.Replace("01M4TF5F0EKGGWD5FNVBAEY9A3", id, StringComparison.Ordinal)
            .Replace("name: Whitby dialect\r\naliases: [Dialect]\r\n", "name: Yorkshire dialect\r\n", StringComparison.Ordinal)
            .Replace("# Whitby dialect", "# Yorkshire dialect", StringComparison.Ordinal);
        Assert.Equal([.. bom, .. System.Text.Encoding.UTF8.GetBytes(expected)], bytes);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void OnlyEntitiesCanBeCopied()
    {
        foreach (var path in new[] { Book + "/book.md", Book + "/outline.md", "universes/dracula/universe.md", "den.yaml" })
        {
            Assert.Equal(ToolErrorCodes.UnknownEntity, Assert.Throws<DenToolException>(() => Tools.CopyEntity(path, "Copy")).Code);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void ACopyIntoASeriesGoesInItsTypeFolder()
    {
        _den.Write("universes/dracula/series/sequels/series.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A4\nname: Sequels\n---\n");

        var result = Tools.CopyEntity("Count Dracula", "Count Varga", toSeries: "sequels");

        Assert.Equal("universes/dracula/series/sequels/entities/characters/count-varga.md", result.Path);
        Assert.Contains(result.Effects, e => e.Contains("in series 'sequels' of universe 'dracula'", StringComparison.Ordinal));
        Assert.Contains(result.Effects, e => e.Contains("Its 'from' is still dracula:1", StringComparison.Ordinal));
        Assert.Equal("series", Tools.GetEntity("Count Varga").Entity!.ScopeKind);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void SlugsAreMadeAsTheTemplateRuleSaysAndCompareIgnoringCase()
    {
        // A file whose name differs only in case takes the slug (F-TPL-6).
        _den.Write(Book + "/entities/characters/Old-Gale.md", _den.Read(Swales).Replace("01M4TF5F0EKGGWD5FNVBAEY8X7", "01M4TF5F0EKGGWD5FNVBAEY9A5", StringComparison.Ordinal)
            .Replace("name: Mr. Swales", "name: Gale the elder", StringComparison.Ordinal));

        Assert.Equal(Book + "/entities/characters/old-gale-2.md", Tools.CopyEntity("Mr. Swales", "Old Gale").Path);
        Assert.Equal(ToolErrorCodes.AlreadyExists, Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", "Old Gale", slug: "old-gale")).Code);
        Assert.Equal(Book + "/entities/characters/aerin-sovik.md", Tools.CopyEntity("Mr. Swales", "Ærin Søvik").Path);

        // A name with nothing to make a slug from uses the type key.
        Assert.Equal(Book + "/entities/characters/character.md", Tools.CopyEntity("Mr. Swales", "東京").Path);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void CommentLinesThatGoWithTheAliasesAreSaid()
    {
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A2\ntype: note\nname: Whitby dialect\naliases:\n  - Dialect\n  # - Yorkshire talk\nstatus: draft\n---\n\nNotes.\n");

        var result = Tools.CopyEntity("Whitby dialect", "Yorkshire dialect");

        Assert.DoesNotContain("Yorkshire talk", _den.Read(result.Path), StringComparison.Ordinal);
        Assert.Contains("Removed 1 comment line inside aliases, which went with them.", result.Effects);

        // Without comments in it, nothing more is said.
        var plain = Tools.CopyEntity("Count Dracula", "Count Varga");
        Assert.DoesNotContain(plain.Effects, e => e.Contains("comment line", StringComparison.Ordinal));
    }

    private int DenFiles() =>
        Directory.GetFiles(_den.Root, "*", SearchOption.AllDirectories).Count(f => !Path.GetRelativePath(_den.Root, f).StartsWith(".wolfden", StringComparison.Ordinal));

    private void WriteAftermath(string status) =>
        _den.Write(Aftermath + "/book.md", $"---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A0\nname: Aftermath\nstatus: {status}\nchapters: [1, 2]\n---\n");
}
