using System.Text.Json;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// <c>get_chapter_defaults</c> and <c>update_chapter_defaults</c> (#151)
/// over a copy of the invented Starfall den: a series of two books
/// (<c>ember</c>, <c>ash</c>), a standalone book (<c>a-quiet-moon</c>), and a
/// series with no books yet (<c>drift-war</c>).
/// </summary>
public sealed class ChapterDefaultsToolTests : IDisposable
{
    private const string Universe = "universes/starfall";
    private const string LongDark = Universe + "/series/long-dark";
    private const string Ember = LongDark + "/books/ember/book.md";
    private const string Ash = LongDark + "/books/ash/book.md";

    private readonly TempDen _den = TempDen.Starfall();

    public ChapterDefaultsToolTests()
    {
        // A second character, and two chapters of Ember that set their own POV.
        _den.Write(LongDark + "/entities/characters/tomas-reyes.md", """
            ---
            id: 01JB0000000000000000000T01
            type: character
            name: Tomas Reyes
            status: canon
            ---

            Pilot of the *Tessaly*.
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        _den.Write(Ember, """
            ---
            id: 01JAQKFBF5KZNWJ47TAN9ZT24M
            name: Ember
            status: draft
            chapters:
              - { key: prologue, title: Before the Dark, fields: { pov: [] } }
              - { key: "1", number: 1, title: Kessan Reach }
              - { key: "2", number: 2 }
              - { key: 2b, number: 3, title: The Long Watch }
              - { key: "3", number: 4, fields: { pov: ["[[tomas-reyes]]"] } }
            ---
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private DenTools Tools => new(_den.Root);

    public void Dispose() => _den.Dispose();

    [Fact]
    [Trait("Scenario", "SC-MCP-24")]
    public void ABooksDefaultPovReachesTheChaptersThatDontSetTheirOwn()
    {
        var read = Tools.GetChapterDefaults("starfall", book: "ember");
        Assert.Equal("book", read.Kind);
        Assert.Equal(Ember, read.Path);
        Assert.Empty(read.Fields);
        Assert.Equal(Tools.GetChapter("ember", universe: "starfall").Hash, read.Hash);
        var only = Assert.Single(read.Books);
        Assert.Equal(("ember", false, 5), (only.Key, only.Final, only.Chapters));

        var set = Tools.UpdateChapterDefaults(read.Hash, Json("""[{"op": "add", "field": "pov", "value": "[[mara-voss]]"}]"""), "starfall", book: "ember");

        Assert.True(set.Changed);
        Assert.Contains("chapter_defaults:\n  pov: [\"[[mara-voss]]\"]\n", _den.Read(Ember), StringComparison.Ordinal);
        Assert.Equal("Book 'ember' chapter_defaults now sets pov: [[mara-voss]].", set.Effects[0]);
        Assert.Equal("Book 'ember': chapters 1–2b now have pov [[mara-voss]]; chapters prologue, 3 keep their own.", set.Effects[1]);
        Assert.Equal(2, set.Effects.Count);
        var pov = Assert.Single(set.ChapterDefaults!.Fields);
        Assert.Equal("[\"[[mara-voss]]\"]", pov.Own!.Value.GetRawText());
        Assert.Equal("book", Tools.GetChapter("ember", "2", "starfall").Chapters[0].Fields.Single(f => f.Name == "pov").From);

        // Removing the last item leaves [], "none here"; clearing goes back to inheriting.
        var removed = Tools.UpdateChapterDefaults(set.Hash, Json("""[{"op": "remove", "field": "pov", "value": "[[Mara-Voss]]"}]"""), "starfall", book: "ember");
        Assert.Contains("chapter_defaults:\n  pov: []\n", _den.Read(Ember), StringComparison.Ordinal);
        Assert.Equal("Book 'ember' chapter_defaults now sets pov: [] (\"none here\"), so chapters below that don't set their own have no pov.", removed.Effects[0]);
        Assert.Equal("Book 'ember': chapters 1–2b now have no pov ([]); chapters prologue, 3 keep their own.", removed.Effects[1]);
        Assert.True(removed.ChapterDefaults!.Fields.Single().None);

        var cleared = Tools.UpdateChapterDefaults(removed.Hash, Json("""[{"op": "clear", "field": "pov"}]"""), "starfall", book: "ember");
        Assert.Equal("Book 'ember' chapter_defaults no longer sets pov, and no chapter_defaults above it does.", cleared.Effects[0]);
        Assert.Equal("Book 'ember': chapters 1–2b now have no pov: nothing sets one; chapters prologue, 3 keep their own.", cleared.Effects[1]);
        Assert.Empty(cleared.ChapterDefaults!.Fields);

        // Clearing what isn't set, an old hash, and a remove from a field that isn't a list are refused.
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.UpdateChapterDefaults(cleared.Hash, Json("""[{"op": "clear", "field": "pov"}]"""), "starfall", book: "ember")).Code);
        Assert.Equal(ToolErrorCodes.StaleFile, Assert.Throws<DenToolException>(() => Tools.UpdateChapterDefaults(set.Hash, Json("""[{"op": "set", "field": "tense", "value": "past"}]"""), "starfall", book: "ember")).Code);
        var tense = Tools.UpdateChapterDefaults(cleared.Hash, Json("""[{"op": "set", "field": "tense", "value": "past"}]"""), "starfall", book: "ember");
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.UpdateChapterDefaults(tense.Hash, Json("""[{"op": "remove", "field": "tense", "item": 0}]"""), "starfall", book: "ember")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.UpdateChapterDefaults(tense.Hash, Json("""[{"op": "set", "field": "key", "value": "x"}]"""), "starfall", book: "ember")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.GetChapterDefaults("starfall", series: "long-dark", book: "ember")).Code);
        Assert.Equal(ToolErrorCodes.UnknownScope, Assert.Throws<DenToolException>(() => Tools.GetChapterDefaults("starfall", series: "nope")).Code);

        // Asking for what it already says writes nothing.
        var same = Tools.UpdateChapterDefaults(tense.Hash, Json("""[{"op": "set", "field": "tense", "value": "past"}]"""), "starfall", book: "ember");
        Assert.False(same.Changed);
        Assert.Equal(tense.Hash, same.Hash);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-24")]
    public void ASeriesDefaultSaysWhichBooksAndChaptersItReaches()
    {
        _den.Write(Ash, "---\nid: 01JAQKFBF5KZNWJ47TAN9ZT24N\nname: Ash\nchapter_defaults: { narration: first }\nchapters: [1, 2, 3, 4, epilogue]\n---\n");
        var series = Tools.GetChapterDefaults("starfall", series: "long-dark");
        Assert.Equal(["ember", "ash"], series.Books.Select(b => b.Key));

        var result = Tools.UpdateChapterDefaults(
            series.Hash, Json("""[{"op": "set", "field": "pov", "value": ["[[mara-voss]]"]}, {"op": "set", "field": "narration", "value": "third-limited"}]"""),
            "starfall", series: "long-dark");

        Assert.Equal(
            [
                "Series 'long-dark' chapter_defaults now sets pov: [[mara-voss]].",
                "Book 'ember': chapters 1–2b now have pov [[mara-voss]]; chapters prologue, 3 keep their own.",
                "Book 'ash': chapters 1–epilogue now have pov [[mara-voss]].",
                "Series 'long-dark' chapter_defaults now sets narration: third-limited.",
                "Book 'ember': chapters prologue–3 now have narration third-limited.",
                "Book 'ash': chapters 1–epilogue keep first from book 'ash' chapter_defaults.",
            ],
            result.Effects);

        // The universe's default is shadowed by the series for its books, and reaches the standalone book.
        var universe = Tools.GetChapterDefaults("starfall");
        Assert.Equal(["ember", "ash", "a-quiet-moon"], universe.Books.Select(b => b.Key));
        var top = Tools.UpdateChapterDefaults(universe.Hash, Json("""[{"op": "set", "field": "narration", "value": "first"}]"""), "starfall");
        Assert.Equal("Universe 'starfall' chapter_defaults now sets narration: first.", top.Effects[0]);
        Assert.Equal("Book 'ember': chapters prologue–3 keep third-limited from series 'long-dark' chapter_defaults.", top.Effects[1]);
        Assert.Equal("Book 'ash': chapters 1–epilogue keep first from book 'ash' chapter_defaults.", top.Effects[2]);
        Assert.Equal("Book 'a-quiet-moon': chapters 1–2b now have narration first.", top.Effects[3]);

        // Clearing the series' value hands its books the universe's.
        var again = Tools.GetChapterDefaults("starfall", series: "long-dark");
        var narration = again.Fields.Single(f => f.Name == "narration");
        Assert.Equal(("universe", "starfall"), (narration.InheritedFrom, narration.InheritedFromKey));
        var cleared = Tools.UpdateChapterDefaults(again.Hash, Json("""[{"op": "clear", "field": "narration"}]"""), "starfall", series: "long-dark");
        Assert.Equal("Series 'long-dark' chapter_defaults no longer sets narration, so its books inherit first from universe 'starfall' chapter_defaults.", cleared.Effects[0]);
        Assert.Equal("Book 'ember': chapters prologue–3 now have narration first.", cleared.Effects[1]);

        // The same value as the universe's is said so, and changes no chapter.
        var same = Tools.UpdateChapterDefaults(cleared.Hash, Json("""[{"op": "set", "field": "narration", "value": "first"}]"""), "starfall", series: "long-dark");
        Assert.Equal("Series 'long-dark' chapter_defaults now sets narration: first, as universe 'starfall' chapter_defaults does.", same.Effects[0]);
        Assert.Equal("Book 'ember': no chapter's narration changes.", same.Effects[1]);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-25")]
    public void AFinalBookIsFoundByItsPathWhenAnotherBookSharesItsKey()
    {
        WriteUniverse("chapter_defaults: { tense: past }\n");
        _den.Write(Ash, "---\nid: 01JAQKFBF5KZNWJ47TAN9ZT24N\nname: Ash\nstatus: final\nchapters: [1, 2, 3, 4, epilogue]\n---\n");
        _den.Write(Universe + "/series/drift-war/series.md", "---\nid: 01JA3E28JT97KB6CQ643DZVMXY\nname: The Drift War\nbooks: [ash]\n---\n");
        _den.Write(Universe + "/series/drift-war/books/ash/book.md", "---\nid: 01JB0000000000000000000A02\nname: Ash of the Drift\nchapters: [1, 2]\n---\n");
        var before = _den.Read(Universe + "/universe.md");
        var universe = Tools.GetChapterDefaults("starfall");
        Assert.Equal(2, universe.Books.Count(b => b.Key == "ash"));

        var refused = Assert.Throws<DenToolException>(() => Tools.UpdateChapterDefaults(universe.Hash, Json("""[{"op": "clear", "field": "tense"}]"""), "starfall"));

        Assert.Equal(ToolErrorCodes.Frozen, refused.Code);
        Assert.Contains("book 'ash' (universes/starfall/series/long-dark/books/ash), tense of chapters 1–epilogue", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(Universe + "/universe.md"));

        // Unfrozen, the edit is made, and each 'ash' is named by its folder.
        _den.Write(Ash, "---\nid: 01JAQKFBF5KZNWJ47TAN9ZT24N\nname: Ash\nchapters: [1, 2, 3, 4, epilogue]\n---\n");
        var cleared = Tools.UpdateChapterDefaults(universe.Hash, Json("""[{"op": "clear", "field": "tense"}]"""), "starfall");
        Assert.Contains("Book 'ash' (universes/starfall/series/long-dark/books/ash): chapters 1–epilogue now have no tense: nothing sets one.", cleared.Effects);
        Assert.Contains("Book 'ash' (universes/starfall/series/drift-war/books/ash): chapters 1–2 now have no tense: nothing sets one.", cleared.Effects);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-25")]
    public void ALinkInADefaultIsComparedByTheEntityItReaches()
    {
        // A second Mara Voss at universe scope: [[mara-voss]] in the universe's defaults
        // reaches her, and in the series' defaults the series' Mara Voss (F-LINK-7).
        _den.Write(Universe + "/entities/characters/mara-voss.md", "---\nid: 01JB0000000000000000000M01\ntype: character\nname: Mara Voss\nstatus: canon\n---\n\nHer namesake.\n");
        WriteUniverse("chapter_defaults: { pov: [\"[[mara-voss]]\"] }\n");
        _den.Write(Ash, "---\nid: 01JAQKFBF5KZNWJ47TAN9ZT24N\nname: Ash\nstatus: final\nchapters: [1, 2, 3, 4, epilogue]\n---\n");
        var series = Tools.GetChapterDefaults("starfall", series: "long-dark");
        var change = Json("""[{"op": "set", "field": "pov", "value": ["[[mara-voss]]"]}]""");

        var refused = Assert.Throws<DenToolException>(() => Tools.UpdateChapterDefaults(series.Hash, change, "starfall", series: "long-dark"));
        Assert.Equal(ToolErrorCodes.Frozen, refused.Code);
        Assert.Contains("book 'ash', pov of chapters 1–epilogue", refused.Message, StringComparison.Ordinal);

        // Unfrozen, the effect lines say the chapters changed, though the text is the same.
        _den.Write(Ash, "---\nid: 01JAQKFBF5KZNWJ47TAN9ZT24N\nname: Ash\nchapters: [1, 2, 3, 4, epilogue]\n---\n");
        var made = Tools.UpdateChapterDefaults(series.Hash, change, "starfall", series: "long-dark");
        Assert.Contains("Book 'ash': chapters 1–epilogue now have pov [[mara-voss]].", made.Effects);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-25")]
    public void ABookFrozenWhileTheEditIsMadeStopsIt()
    {
        var series = Tools.GetChapterDefaults("starfall", series: "long-dark");
        var before = _den.Read(LongDark + "/series.md");
        var tools = new DenTools(_den.Root)
        {
            BeforeDefaultsSave = () => _den.Write(Ash, "---\nid: 01JAQKFBF5KZNWJ47TAN9ZT24N\nname: Ash\nstatus: final\nchapters: [1, 2, 3, 4, epilogue]\n---\n"),
        };

        var stale = Assert.Throws<DenToolException>(() =>
            tools.UpdateChapterDefaults(series.Hash, Json("""[{"op": "set", "field": "tense", "value": "past"}]"""), "starfall", series: "long-dark"));

        Assert.Equal(ToolErrorCodes.StaleFile, stale.Code);
        Assert.Contains(Ash, stale.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(LongDark + "/series.md"));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-25")]
    public void ABookLeftOutForErrorsThatSaysFinalStopsTheEdit()
    {
        // 'chapters' as a mapping is an error (F-BOOK-2), so the den leaves the book out.
        _den.Write(Ash, "---\nid: 01JAQKFBF5KZNWJ47TAN9ZT24N\nname: Ash\nstatus: final\nchapters: { one: 1 }\n---\n");
        var series = Tools.GetChapterDefaults("starfall", series: "long-dark");
        Assert.DoesNotContain(series.Books, b => b.Key == "ash");
        var change = Json("""[{"op": "set", "field": "tense", "value": "past"}]""");

        var refused = Assert.Throws<DenToolException>(() => Tools.UpdateChapterDefaults(series.Hash, change, "starfall", series: "long-dark"));
        Assert.Equal(ToolErrorCodes.Frozen, refused.Code);
        Assert.Contains(Ash, refused.Message, StringComparison.Ordinal);

        // A draft book with errors isn't frozen.
        _den.Write(Ash, "---\nid: 01JAQKFBF5KZNWJ47TAN9ZT24N\nname: Ash\nchapters: { one: 1 }\n---\n");
        Assert.True(Tools.UpdateChapterDefaults(series.Hash, change, "starfall", series: "long-dark").Changed);
    }

    private void WriteUniverse(string extra) =>
        _den.Write(
            Universe + "/universe.md",
            "---\nid: 01JAM9S346Q3D25VT4F5V37E3S\nname: Starfall\nauthors: [wren-calder]\norder: [long-dark, a-quiet-moon, drift-war]\n" + extra +
            "---\n\nThe Kessan Reach and the long war over it.\n");

    [Fact]
    [Trait("Scenario", "SC-MCP-25")]
    public void ADefaultThatWouldChangeAFinalBooksChaptersIsRefused()
    {
        _den.Write(Ash, "---\nid: 01JAQKFBF5KZNWJ47TAN9ZT24N\nname: Ash\nstatus: final\nchapter_defaults: { tense: past }\nchapters: [1, 2, 3, 4, epilogue]\n---\n");
        var before = _den.Read(LongDark + "/series.md");
        var series = Tools.GetChapterDefaults("starfall", series: "long-dark");
        Assert.True(series.Books.Single(b => b.Key == "ash").Final);

        var refused = Assert.Throws<DenToolException>(() =>
            Tools.UpdateChapterDefaults(series.Hash, Json("""[{"op": "add", "field": "pov", "value": "[[mara-voss]]"}]"""), "starfall", series: "long-dark"));
        Assert.Equal(ToolErrorCodes.Frozen, refused.Code);
        Assert.Contains("book 'ash', pov of chapters 1–epilogue", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(LongDark + "/series.md"));

        // The final book's own defaults can't be edited, nor the universe's where they reach it.
        var ash = Tools.GetChapterDefaults("starfall", book: "ash");
        Assert.True(ash.Final);
        Assert.Equal(ToolErrorCodes.Frozen, Assert.Throws<DenToolException>(() =>
            Tools.UpdateChapterDefaults(ash.Hash, Json("""[{"op": "clear", "field": "tense"}]"""), "starfall", book: "ash")).Code);
        var universe = Tools.GetChapterDefaults("starfall");
        Assert.Equal(ToolErrorCodes.Frozen, Assert.Throws<DenToolException>(() =>
            Tools.UpdateChapterDefaults(universe.Hash, Json("""[{"op": "set", "field": "narration", "value": "first"}]"""), "starfall")).Code);

        // A default the final book doesn't see, because its own defaults set the field, is made.
        var tense = Tools.UpdateChapterDefaults(universe.Hash, Json("""[{"op": "set", "field": "tense", "value": "present"}]"""), "starfall");
        Assert.True(tense.Changed);
        Assert.Contains("Book 'ash': chapters 1–epilogue keep past from book 'ash' chapter_defaults.", tense.Effects);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-24")]
    public void ASeriesWithNoBooksYetCanHaveDefaultsAndGetsAnId()
    {
        _den.Write(Universe + "/series/drift-war/series.md", "---\nname: The Drift War\n---\n\nPlanned. No books yet.\n");
        var series = Tools.GetChapterDefaults("starfall", series: "drift-war");
        Assert.Empty(series.Books);

        var result = Tools.UpdateChapterDefaults(series.Hash, Json("""[{"op": "set", "field": "tense", "value": "present"}]"""), "starfall", series: "drift-war");

        Assert.NotNull(result.IdAdded);
        Assert.Equal("No book below has chapters yet, so no chapter's tense changes now.", result.Effects[1]);
        Assert.StartsWith($"---\nid: {result.IdAdded}\nname: The Drift War\nchapter_defaults:\n  tense: present\n---\n", _den.Read(Universe + "/series/drift-war/series.md"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-24")]
    public void AValueThatDoesntFitTheFieldIsSavedWithAWarningAndSaysItsIgnored()
    {
        var book = Tools.GetChapterDefaults("starfall", book: "a-quiet-moon");

        var result = Tools.UpdateChapterDefaults(book.Hash, Json("""[{"op": "set", "field": "tense", "value": "sideways"}]"""), "starfall", book: "a-quiet-moon");

        Assert.Contains("doesn't fit the field, so it's ignored", result.Effects[0], StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Rule == "F-CHAP-3");
        Assert.Equal("Book 'a-quiet-moon': no chapter's tense changes.", result.Effects[1]);

        // get_chapter_defaults lists it as written but ignored.
        var tense = Tools.GetChapterDefaults("starfall", book: "a-quiet-moon").Fields.Single(f => f.Name == "tense");
        Assert.True(tense.Ignored);
        Assert.Equal("\"sideways\"", tense.Written!.Value.GetRawText());
        Assert.Null(tense.Own);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
