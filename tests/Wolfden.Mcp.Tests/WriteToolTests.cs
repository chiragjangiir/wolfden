using System.Text.Json;
using Wolfden.Core.Editing;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// The write tools (#21) over a copy of the Dracula sample: each edit is
/// checked as the den would read it, refuses a stale or frozen file, and
/// says what it means.
/// </summary>
public sealed class WriteToolTests : IDisposable
{
    private const string Book = "universes/dracula/books/dracula";
    private const string Swales = Book + "/entities/characters/mr-swales.md";
    private const string BookFile = Book + "/book.md";

    private readonly TempDen _den = TempDen.Dracula();

    private DenTools Tools => new(_den.Root);

    public void Dispose() => _den.Dispose();

    [Fact]
    [Trait("Scenario", "SC-MCP-15")]
    public void ANewEntityComesFromItsTemplateWithAnIdNoFileUses()
    {
        var used = Tools.GetEntity("Mr. Swales").Entity!.Id!;
        var ids = new Queue<string>([used, "01M4TF5F0EKGGWD5FNVBAEY8ZZ"]);
        var tools = new DenTools(_den.Root) { NewId = () => ids.Dequeue() };

        var result = tools.CreateEntity(
            "character", "Sister Agatha", book: "dracula", from: "dracula:8", aliases: ["the nun"],
            fields: Json("""{"occupation": ["nun"], "role": "Writes to Mina from Buda-Pesth about Jonathan's illness."}"""));

        Assert.True(result.Changed);
        var path = Book + "/entities/characters/sister-agatha.md";
        Assert.Equal(path, result.Path);
        Assert.Equal("01M4TF5F0EKGGWD5FNVBAEY8ZZ", result.Entity!.Id);
        Assert.Equal("draft", result.Entity.Status);
        Assert.Equal("book", result.Entity.ScopeKind);
        var text = _den.Read(path);
        Assert.StartsWith(
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8ZZ\ntype: character\nname: Sister Agatha\naliases: [the nun]\nstatus: draft\nfrom: \"dracula:8\"\nfields:\n" +
            "  role: Writes to Mina from Buda-Pesth about Jonathan's illness.\n  gender:\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("  occupation: [nun]\n", text, StringComparison.Ordinal);
        Assert.Contains("\n# Sister Agatha\n\n## Overview\n<!-- Who they are", text, StringComparison.Ordinal);
        Assert.Contains("## Open questions\n<!-- wolfden: status=undecided -->", text, StringComparison.Ordinal);
        Assert.Equal(result.Hash, Tools.GetEntity("Sister Agatha").Entity!.Hash);
        Assert.Equal(0, Tools.Check(path: path).Errors);

        // A slug made from the name gets -2; one asked for that's taken is refused.
        var again = Tools.CreateEntity("character", "Sister Agatha", book: "dracula", slug: null);
        Assert.Equal(Book + "/entities/characters/sister-agatha-2.md", again.Path);
        Assert.Equal(ToolErrorCodes.AlreadyExists, Assert.Throws<DenToolException>(() => Tools.CreateEntity("character", "Agatha", book: "dracula", slug: "sister-agatha")).Code);
        Assert.Equal(ToolErrorCodes.UnknownType, Assert.Throws<DenToolException>(() => Tools.CreateEntity("starship", "Demeter")).Code);
        Assert.Equal(ToolErrorCodes.UnknownScope, Assert.Throws<DenToolException>(() => Tools.CreateEntity("character", "X", book: "nope")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-15")]
    public void ANewEntitysFromMustResolveAndOneInAnotherBookIsAWarning()
    {
        var before = Directory.GetFiles(_den.Root, "*.md", SearchOption.AllDirectories).Length;

        // F-POS-4: a 'from' the universe doesn't have would leave the entity out of every view.
        foreach (var from in new[] { "dracul:5", "dracula:999", "dracula 5" })
        {
            var refused = Assert.Throws<DenToolException>(() => Tools.CreateEntity("character", "Someone", from: from));
            Assert.Equal(ToolErrorCodes.BadPosition, refused.Code);
            Assert.Contains("from", refused.Message, StringComparison.Ordinal);
        }

        Assert.Contains("doesn't resolve", Assert.Throws<DenToolException>(() => Tools.CreateEntity("character", "Someone", from: "dracula:999")).Message, StringComparison.Ordinal);
        Assert.Equal(before, Directory.GetFiles(_den.Root, "*.md", SearchOption.AllDirectories).Length);

        // A bare book key is a position (F-POS-7).
        Assert.Equal("dracula", Tools.CreateEntity("character", "From the start", from: "dracula").Entity!.From);

        // F-ENT-6: a book's entity entering in another book is made, with the warning.
        _den.Write("universes/dracula/books/aftermath/book.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A0\nname: Aftermath\nstatus: draft\nchapters: [1]\n---\n");
        var elsewhere = Tools.CreateEntity("character", "Young Swales", book: "aftermath", from: "dracula:8");
        var warning = Assert.Single(elsewhere.Warnings, w => w.Rule == "F-ENT-6");
        Assert.Equal(elsewhere.Path, warning.Path);
        Assert.Contains("filed in book 'aftermath'", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Tools.CreateEntity("character", "At home", book: "dracula", from: "dracula:8").Warnings, w => w.Rule == "F-ENT-6");
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-15")]
    public void ANewEntityThatWouldTakeLinksFromAnotherIsRefusedUnlessAllowed()
    {
        const string Stub = "universes/dracula/entities/characters/jonathan-harker.md";
        var before = Directory.GetFiles(_den.Root, "*.md", SearchOption.AllDirectories).Length;

        // At universe scope, a second Jonathan Harker is nearer to the universe's notes that link to the book's (F-LINK-7).
        var refused = Assert.Throws<DenToolException>(() => Tools.CreateEntity("character", "Jonathan Harker"));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains("link(s) would change which entity they reach", refused.Message, StringComparison.Ordinal);
        Assert.Contains("reaches 'Jonathan Harker' (universes/dracula/books/dracula/entities/characters/jonathan-harker.md) and would reach 'Jonathan Harker' (" + Stub + ")", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was created.", refused.Message, StringComparison.Ordinal);
        Assert.Contains("pass allow_relinks: true to create it with those links moving to it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Directory.GetFiles(_den.Root, "*.md", SearchOption.AllDirectories).Length);

        // In the book, beside the original, it's a -2 file that no link names, so nothing moves.
        Assert.Equal(Book + "/entities/characters/jonathan-harker-2.md", Tools.CreateEntity("character", "Jonathan Harker", book: "dracula").Path);
        Assert.True(File.Exists(Path.Combine(_den.Root, Book.Replace('/', Path.DirectorySeparatorChar), "entities", "characters", "jonathan-harker-2.md")));

        // With allow_relinks, it's made, and the effects list the links that moved.
        var made = Tools.CreateEntity("character", "Jonathan Harker", allowRelinks: true);
        Assert.Equal(Stub, made.Path);
        Assert.Contains(made.Effects, e => e.Contains($"now reaches 'Jonathan Harker' ({Stub}); before, it reached 'Jonathan Harker' ({Book}/entities/characters/jonathan-harker.md)", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-15")]
    public void ANewIdeaThatWouldTakeLinksIsRefusedUnlessAllowed()
    {
        const string Asylum = "universes/dracula/entities/locations/seward-asylum.md";
        const string Idea = "universes/dracula/entities/ideas/renfield.md";

        // Seward's asylum, a universe note, links [[renfield]] to the book's Renfield; a universe-scope idea of that name is nearer (F-LINK-7).
        var refused = Assert.Throws<DenToolException>(() => Tools.AddIdea("Renfield as a recurring figure in later books.", name: "Renfield"));

        Assert.Equal(ToolErrorCodes.BadArgument, refused.Code);
        Assert.Contains(Asylum, refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was created.", refused.Message, StringComparison.Ordinal);
        Assert.Contains("pass allow_relinks: true", refused.Message, StringComparison.Ordinal);
        Assert.NotNull(refused.Relinks);
        Assert.False(File.Exists(Path.Combine(_den.Root, Idea)));

        var made = Tools.AddIdea("Renfield as a recurring figure in later books.", name: "Renfield", allowRelinks: true);

        Assert.Equal(Idea, made.Path);
        Assert.Equal("idea", made.Entity!.Status);
        Assert.Contains(made.Effects, e => e.Contains(Asylum, StringComparison.Ordinal) && e.Contains($"now reaches 'Renfield' ({Idea})", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-15")]
    public void AnEmptyFromIsNoFrom()
    {
        foreach (var from in new[] { "", "   " })
        {
            var made = Tools.CreateEntity("note", $"Empty from {from.Length}", from: from);
            Assert.Null(made.Entity!.From);
            Assert.DoesNotContain("\nfrom:", _den.Read(made.Path), StringComparison.Ordinal);
            Assert.Contains(made.Effects, e => e.Contains("It has no 'from'", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-15")]
    public void ANameIsOneLineOfAtMost200Characters()
    {
        // U+2028 and U+2029 would split the '# name' heading; a control character too; and a name runs on past 200.
        foreach (var name in new[] { "x\u2028## Injected", "x\u2029y", "x\ty", new string('a', NewEntity.MaxNameLength + 1) })
        {
            Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.CreateEntity("character", name)).Code);
            Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.AddIdea("text", name: name)).Code);
            Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.CopyEntity("Mr. Swales", name)).Code);
            var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;
            Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.RenameEntity("Mr. Swales", name, hash)).Code);
        }

        Assert.Contains("at most 200", Assert.Throws<DenToolException>(() => Tools.CreateEntity("character", new string('a', 201))).Message, StringComparison.Ordinal);
        var longest = Tools.CreateEntity("character", new string('a', NewEntity.MaxNameLength));
        Assert.Equal(NewEntity.MaxNameLength, longest.Entity!.Name.Length);
        Assert.Equal(new string('a', 64), Path.GetFileNameWithoutExtension(longest.Path));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-67")]
    public void TheTypesANewEntityCanBeMadeFromAreTheEnabledOnesByName()
    {
        var types = Tools.ListTypes().Types;

        Assert.Equal(
            ["Character", "Event", "Faction", "Idea", "Item", "Location", "Note", "Thread"], types.Select(t => t.Name));
        var thread = types.Single(t => t.Key == "thread");
        Assert.Equal("threads", thread.Plural);
        Assert.Equal("core", thread.Pack);
        Assert.True(thread.BuiltIn);
        Assert.False(string.IsNullOrEmpty(thread.Description));

        // Every one of them can be created.
        foreach (var type in types)
        {
            Assert.True(Tools.CreateEntity(type.Key, $"A new {type.Key}").Changed);
        }

        _den.Write("den.yaml", "format: [\n");
        Assert.Equal(ToolErrorCodes.DenNotLoaded, Assert.Throws<DenToolException>(() => Tools.ListTypes()).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-15")]
    public void ASlugIsMadeFromTheNameAsTheTemplateRuleSays()
    {
        Assert.Equal("aerin-sovik", Core.Editing.NewEntity.Slugify("Ærin Søvik"));
        Assert.Equal("mr-swales", Core.Editing.NewEntity.Slugify("Mr. Swales"));
        Assert.Equal("", Core.Editing.NewEntity.Slugify("!!!"));

        var made = Tools.CreateEntity("location", "!!!");
        Assert.Equal("universes/dracula/entities/locations/location.md", made.Path);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-16")]
    public void FieldEditsKeepTheRestAndSayWhatTheyMean()
    {
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.UpdateFields("Mr. Swales", hash, Json("""
            [
              {"op": "add", "path": "aliases", "value": "the old sailor"},
              {"op": "set", "path": "fields.occupation", "value": ["sailor"]},
              {"op": "set", "path": "status", "value": "idea"}
            ]
            """));

        Assert.True(result.Changed);
        var after = _den.Read(Swales);
        Assert.Equal(
            before.Replace("name: Mr. Swales\n", "name: Mr. Swales\naliases: [the old sailor]\n", StringComparison.Ordinal)
                .Replace("status: canon", "status: idea", StringComparison.Ordinal)
                .Replace("    from: \"dracula:7\"\n", "    from: \"dracula:7\"\n  occupation: [sailor]\n", StringComparison.Ordinal),
            after);
        Assert.Contains(result.Effects, e => e.Contains("canon views leave it out", StringComparison.Ordinal));
        Assert.Empty(result.PinnedCanon);

        // The old hash is stale now; the result's hash isn't.
        Assert.Equal(ToolErrorCodes.StaleFile, Assert.Throws<DenToolException>(() => Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "set", "path": "status", "value": "canon"}]"""))).Code);
        Tools.UpdateFields("Mr. Swales", result.Hash, Json("""[{"op": "set", "path": "status", "value": "canon"}]"""));

        // Ids never change, and names and aliases that links may match aren't changed here.
        var now = Tools.GetEntity("Mr. Swales").Entity!.Hash!;
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.UpdateFields("Mr. Swales", now, Json("""[{"op": "set", "path": "id", "value": "01M4TF5F0EKGGWD5FNVBAEY8ZZ"}]"""))).Code);
        Assert.Equal(ToolErrorCodes.UnsupportedEdit, Assert.Throws<DenToolException>(() => Tools.UpdateFields("Mr. Swales", now, Json("""[{"op": "set", "path": "name", "value": "Swales"}]"""))).Code);
        Assert.Equal(ToolErrorCodes.UnsupportedEdit, Assert.Throws<DenToolException>(() => Tools.UpdateFields("Mr. Swales", now, Json("""[{"op": "remove", "path": "aliases.0"}]"""))).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-16")]
    public void AnEditThatWouldLeaveAnErrorIsRefusedAndNothingIsWritten()
    {
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var bad = Assert.Throws<DenToolException>(() => Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "set", "path": "status", "value": "maybe"}]""")));
        Assert.Equal(ToolErrorCodes.InvalidEdit, bad.Code);
        Assert.Contains("F-ENT-5", bad.Message, StringComparison.Ordinal);

        var badFrom = Assert.Throws<DenToolException>(() => Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "set", "path": "from", "value": "dracula:7.2"}]""")));
        Assert.Equal(ToolErrorCodes.InvalidEdit, badFrom.Code);

        // A number too large to be one (types.md F-FT-6); one that isn't whole is written plain.
        var huge = Assert.Throws<DenToolException>(() => Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "set", "path": "fields.x_count", "value": 1e400}]""")));
        Assert.Equal(ToolErrorCodes.InvalidEdit, huge.Code);
        Assert.Equal(before, _den.Read(Swales));
        Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "set", "path": "fields.x_count", "value": 2.50}]"""));
        Assert.Contains("  x_count: 2.50\n", _den.Read(Swales), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-16")]
    public void AnEditOfPinnedCanonIsMadeButFlagged()
    {
        var hash = Tools.GetEntity("Count Dracula").Entity!.Hash!;

        var result = Tools.UpdateFields("Count Dracula", hash, Json("""[{"op": "set", "path": "fields.species.from", "value": "dracula:17"}]"""));

        Assert.True(result.Changed);
        Assert.Contains("species: { value: \"vampire (Un-Dead)\", from: \"dracula:17\", pin: true }", _den.Read(Book + "/entities/characters/count-dracula.md"), StringComparison.Ordinal);
        Assert.Contains(result.PinnedCanon, p => p.Contains("fields.species", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-17")]
    public void TextGoesIntoASectionOrANewOneInItsPlace()
    {
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var voice = Tools.AppendSection("Mr. Swales", "voice", "He calls the gravestones \"lies\".", hash);
        Assert.Contains("Mina tries to write down as she hears it.\n\nHe calls the gravestones \"lies\".\n\n## Arc", _den.Read(Swales), StringComparison.Ordinal);

        // A standard section goes in the type's order: Appearance before Voice.
        var looks = Tools.AppendSection("Mr. Swales", "Appearance", "Very old, with a face all wrinkles.", voice.Hash);
        Assert.Contains("the gravestones, many of which stand over empty graves of men lost at sea.\n\n## Appearance\nVery old, with a face all wrinkles.\n\n## Voice", _den.Read(Swales), StringComparison.Ordinal);

        // A subsection with its own position, under Arc.
        var last = Tools.AppendSection("Mr. Swales", "The night of the storm", "He is on the seat when the ship comes in.", looks.Hash, under: "Arc", from: "dracula:7");
        Assert.EndsWith("look of horror on his face.\n\n### The night of the storm\n<!-- wolfden: from=dracula:7 -->\nHe is on the seat when the ship comes in.\n", _den.Read(Swales), StringComparison.Ordinal);
        Assert.Contains("Added 'The night of the storm' as a subsection of 'Arc'.", last.Effects);

        // Attributes don't change a section that's there.
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.AppendSection("Mr. Swales", "Voice", "x", last.Hash, status: "idea")).Code);
        Assert.Equal(ToolErrorCodes.UnknownSection, Assert.Throws<DenToolException>(() => Tools.AppendSection("Mr. Swales", "x", "y", last.Hash, under: "Nope")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-17")]
    public void ATemplateSectionIsFilledNotRepeated()
    {
        var made = Tools.CreateEntity("character", "Sister Agatha", book: "dracula");

        Tools.AppendSection("Sister Agatha", "Overview", "A nun of the Hospital of St. Joseph and Ste. Mary.", made.Hash);

        var text = _den.Read(made.Path);
        Assert.Single(text.Split("## Overview"), s => s.Length > 0 && !s.StartsWith("---", StringComparison.Ordinal));
        Assert.Contains("## Overview\n<!-- Who they are, in a paragraph", text, StringComparison.Ordinal);
        Assert.Contains("-->\n\nA nun of the Hospital of St. Joseph and Ste. Mary.\n\n## Appearance", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-18")]
    public void AnIdeaIsRecordedOutOfCanon()
    {
        var idea = Tools.AddIdea("What if the Count kept a diary too?", name: "The Count's diary", fields: Json("""{"about": ["[[count-dracula]]"]}"""));
        Assert.Equal("universes/dracula/entities/ideas/the-count-s-diary.md", idea.Path);
        Assert.Equal("idea", idea.Entity!.Status);
        Assert.Contains("\n# The Count's diary\n\n<!-- The idea in a sentence or two.", _den.Read(idea.Path), StringComparison.Ordinal);
        Assert.EndsWith("-->\n\nWhat if the Count kept a diary too?\n", _den.Read(idea.Path), StringComparison.Ordinal);

        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;
        var section = Tools.AddIdea("Perhaps he saw the dog leap ashore.", entity: "Mr. Swales", section: "What he saw", expectedHash: hash);
        Assert.EndsWith("\n## What he saw\n<!-- wolfden: status=idea -->\nPerhaps he saw the dog leap ashore.\n", _den.Read(Swales), StringComparison.Ordinal);
        Assert.Contains(section.Effects, e => e.Contains("canon views leave it out", StringComparison.Ordinal));
        var view = Tools.CanonAsOf(position: "dracula:8", type: "character");
        Assert.DoesNotContain(view.Entities.Single(e => e.Name == "Mr. Swales").Sections!, s => s.Name == "What he saw");
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-19")]
    public void RenamingASectionRewritesTheLinksToIt()
    {
        const string Note = "universes/dracula/entities/notes/whitby-dialect.md";
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\nfields:\n  about: [\"[[mr-swales#Voice]]\"]\n---\n\n# Whitby dialect\n\nSee [[Mr. Swales # voice |how he talks]] and [[mr-swales#Arc]].\n");
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.RenameSection("Mr. Swales", "Voice", "Dialect", hash);

        Assert.Contains("\n## Dialect\nThick Yorkshire dialect", _den.Read(Swales), StringComparison.Ordinal);
        var note = _den.Read(Note);
        Assert.Contains("  about: [\"[[mr-swales#Dialect]]\"]\n", note, StringComparison.Ordinal);
        Assert.Contains("See [[Mr. Swales # Dialect |how he talks]] and [[mr-swales#Arc]].", note, StringComparison.Ordinal);
        Assert.Equal([Swales, Note], result.Files!.Select(f => f.Path));
        Assert.DoesNotContain(Tools.Check().Diagnostics, d => d.Rule == "F-LINK-9");
        Assert.Equal(result.Files![1].Hash, Tools.GetEntity("Whitby dialect").Entity!.Hash);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-19")]
    public void RenamingASectionToANameTheNoteHasSaysWhichOneALinkReaches()
    {
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        // No link names Voice or Arc, so the rename is made, and the effect says a link to 'Arc' reaches the first one (F-LINK-9).
        var result = Tools.RenameSection("Mr. Swales", "Voice", "Arc", hash);

        Assert.Contains("\n## Arc\nThick Yorkshire dialect", _den.Read(Swales), StringComparison.Ordinal);
        Assert.Contains(result.Effects, e => e.Contains("already has a section 'Arc'", StringComparison.Ordinal) && e.Contains("F-LINK-9", StringComparison.Ordinal));

        // Renamed back, then the later one renamed to the earlier name: a link reaches the earlier one.
        var back = Tools.RenameSection("Mr. Swales", "Arc", "Voice", result.Hash, sectionLine: 20);
        var earlier = Tools.RenameSection("Mr. Swales", "Arc", "Overview", back.Hash);
        Assert.Contains(earlier.Effects, e => e.Contains("An earlier section, 'Overview'", StringComparison.Ordinal) && e.Contains("F-LINK-9", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-19")]
    public void ALinkInAFrozenBookStopsTheRename()
    {
        _den.Write("universes/dracula/books/aftermath/book.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z2\nname: Aftermath\nstatus: final\n---\n");
        _den.Write("universes/dracula/books/aftermath/entities/notes/memories.md",
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z3\ntype: note\nname: Memories\nstatus: draft\n---\n\nRecall [[mr-swales#Voice]].\n");
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var refused = Assert.Throws<DenToolException>(() => Tools.RenameSection("Mr. Swales", "Voice", "Dialect", hash));

        Assert.Equal(ToolErrorCodes.Frozen, refused.Code);
        Assert.Contains("aftermath/entities/notes/memories.md", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(Swales));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-20")]
    public void RemovingAChaptersLastPovSaysWhatItNoLongerInherits()
    {
        _den.Write(BookFile, _den.Read(BookFile).Replace("  tense: past\n", "  tense: past\n  pov: [\"[[mina-harker]]\"]\n", StringComparison.Ordinal));
        var chapter = Tools.GetChapter("dracula", "1");
        var pov = chapter.Chapters[0].Fields.Single(f => f.Name == "pov");
        Assert.Equal("chapter", pov.From);
        Assert.Equal("book", pov.DefaultFrom);

        var removed = Tools.UpdateChapterFields("dracula", "1", chapter.Hash, Json("""[{"op": "remove", "field": "pov", "item": 0}]"""));

        Assert.Contains("      pov: []\n", _den.Read(BookFile), StringComparison.Ordinal);
        var effect = Assert.Single(removed.Effects);
        Assert.Contains("now has no pov", effect, StringComparison.Ordinal);
        Assert.Contains("[[mina-harker]] from book 'dracula' chapter_defaults", effect, StringComparison.Ordinal);
        Assert.True(removed.Chapter!.Fields.Single(f => f.Name == "pov").None);

        // Clearing the field goes back to the default.
        var cleared = Tools.UpdateChapterFields("dracula", "1", removed.Hash, Json("""[{"op": "clear", "field": "pov"}]"""));
        Assert.Contains("now inherits pov: [[mina-harker]]", Assert.Single(cleared.Effects), StringComparison.Ordinal);
        var now = cleared.Chapter!.Fields.Single(f => f.Name == "pov");
        Assert.Equal("book", now.From);
        Assert.Null(now.Own);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.UpdateChapterFields("dracula", "1", cleared.Hash, Json("""[{"op": "clear", "field": "pov"}]"""))).Code);
        Assert.Equal(ToolErrorCodes.UnknownChapter, Assert.Throws<DenToolException>(() => Tools.GetChapter("dracula", "99")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-21")]
    public void NothingInAFinalBookIsWritten()
    {
        _den.Write(BookFile, _den.Read(BookFile).Replace("status: draft", "status: final", StringComparison.Ordinal));
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        Assert.Equal(ToolErrorCodes.Frozen, Assert.Throws<DenToolException>(() => Tools.AppendSection("Mr. Swales", "Voice", "More.", hash)).Code);
        Assert.Equal(ToolErrorCodes.Frozen, Assert.Throws<DenToolException>(() => Tools.CreateEntity("character", "Sister Agatha", book: "dracula")).Code);
        var chapter = Tools.GetChapter("dracula", "1");
        Assert.True(chapter.Final);
        Assert.Equal(ToolErrorCodes.Frozen, Assert.Throws<DenToolException>(() => Tools.UpdateChapterFields("dracula", "1", chapter.Hash, Json("""[{"op": "set", "field": "tense", "value": "present"}]"""))).Code);
        Assert.Equal(before, _den.Read(Swales));

        // The universe isn't frozen.
        Assert.True(Tools.CreateEntity("location", "Whitby Abbey").Changed);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-22")]
    public void WithoutHistoryACheckpointSaysSoAndTheEditsStay()
    {
        var made = Tools.CreateEntity("location", "Whitby Abbey");

        var result = Tools.Checkpoint("Add Whitby Abbey", "Claude <noreply@anthropic.com>");

        Assert.False(result.Available);
        Assert.False(result.Recorded);
        Assert.Contains("still saved in the files", result.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_den.Root, made.Path)));
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.Checkpoint("x", "Claude")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-23")]
    public void APinnedSessionChecksNewIdsAcrossTheWholeDen()
    {
        using var den = TempDen.Starfall();
        var other = new DenTools(den.Root).ListEntities(universe: "starfall").Entities.First(e => e.Id is not null).Id!;
        var ids = new Queue<string>([other, "01M4TF5F0EKGGWD5FNVBAEY8Z9"]);
        var tools = new DenTools(den.Root) { PinnedUniverse = "drift", NewId = () => ids.Dequeue() };

        var made = tools.CreateEntity("note", "Drift log");

        Assert.Equal("01M4TF5F0EKGGWD5FNVBAEY8Z9", made.Entity!.Id);
        Assert.Equal("drift", made.Entity.Universe);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => tools.CreateEntity("note", "X", universe: "starfall")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-19")]
    public void ARenameThatFailsPartWayPutsBackWhatItWrote()
    {
        const string Note = "universes/dracula/entities/notes/whitby-dialect.md";
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\nSee [[mr-swales#Voice]].\n");
        var before = (Swales: _den.Read(Swales), Note: _den.Read(Note));
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        // Swales's file sorts first, so it's written before the note's write fails.
        var tools = new DenTools(_den.Root) { Store = new FailingStore(_den.Root, Note) };
        var failed = Assert.Throws<DenToolException>(() => tools.RenameSection("Mr. Swales", "Voice", "Dialect", hash));

        Assert.Equal(ToolErrorCodes.WriteFailed, failed.Code);
        Assert.Contains("were put back as they were", failed.Message, StringComparison.Ordinal);
        Assert.Equal(before.Swales, _den.Read(Swales));
        Assert.Equal(before.Note, _den.Read(Note));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-19")]
    public void ARenameStoppedByAnyFailureIsPutBackThenRethrown()
    {
        const string Note = "universes/dracula/entities/notes/whitby-dialect.md";
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\nSee [[mr-swales#Voice]].\n");
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;
        var tools = new DenTools(_den.Root) { Store = new FailingStore(_den.Root, Note, () => new OperationCanceledException("stopped")) };

        Assert.Throws<OperationCanceledException>(() => tools.RenameSection("Mr. Swales", "Voice", "Dialect", hash));

        Assert.Equal(before, _den.Read(Swales));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-19")]
    public void ARollbackLeavesAFileSomeoneElseSavedMeanwhile()
    {
        const string Note = "universes/dracula/entities/notes/whitby-dialect.md";
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\nSee [[mr-swales#Voice]].\n");
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;
        var theirs = "";
        var tools = new DenTools(_den.Root)
        {
            // The writer saves Swales in an editor just after Wolfden wrote it.
            Store = new FailingStore(_den.Root, Note, before: () => _den.Write(Swales, theirs = _den.Read(Swales) + "\nSaved in an editor.\n")),
        };

        var failed = Assert.Throws<DenToolException>(() => tools.RenameSection("Mr. Swales", "Voice", "Dialect", hash));

        Assert.Equal(ToolErrorCodes.WriteFailed, failed.Code);
        Assert.Contains($"couldn't be put back, and hold the edit: {Swales}", failed.Message, StringComparison.Ordinal);
        Assert.Equal(theirs, _den.Read(Swales));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-16")]
    public void APinnedSubsectionUnderAnUnpinnedSectionIsCounted()
    {
        _den.Write(Swales, _den.Read(Swales) + "\n### The verdict\n<!-- wolfden: pin=true -->\nDied of fright.\n");
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "set", "path": "status", "value": "draft"}]"""));

        Assert.Contains(result.PinnedCanon, p => p.Contains("section 'The verdict'", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-19")]
    public void ALinkInAFrontmatterValueIsSplicedSoTheValueKeepsItsStyle()
    {
        const string Note = "universes/dracula/entities/notes/whitby-dialect.md";
        _den.Write(Note,
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\nfields:\n" +
            "  x_summary: |\n    Read [[mr-swales#Voice]] aloud.\n    Then again.\n" +
            "  x_motto: \"Caf\\u00e9 talk, see [[mr-swales#Voice]]\"\n---\n\n# Whitby dialect\n");
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        Tools.RenameSection("Mr. Swales", "Voice", "Dialect", hash);

        var text = _den.Read(Note);
        Assert.Contains("  x_summary: |\n    Read [[mr-swales#Dialect]] aloud.\n    Then again.\n", text, StringComparison.Ordinal);
        Assert.Contains("  x_motto: \"Caf\\u00e9 talk, see [[mr-swales#Dialect]]\"\n", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-19")]
    public void ANameThatWouldMoveExistingLinksIsRefused()
    {
        const string Note = "universes/dracula/entities/notes/whitby-dialect.md";
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\nSee [[mr-swales#Arc]] and [[mr-swales#Voice]].\n");
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        // Voice comes before Arc: renamed Arc, it would take the links to Arc.
        var later = Assert.Throws<DenToolException>(() => Tools.RenameSection("Mr. Swales", "Voice", "Arc", hash));
        Assert.Equal(ToolErrorCodes.BadArgument, later.Code);
        Assert.Contains("F-LINK-9", later.Message, StringComparison.Ordinal);

        // Arc renamed Overview: its links would reach the earlier Overview.
        var earlier = Assert.Throws<DenToolException>(() => Tools.RenameSection("Mr. Swales", "Arc", "Overview", hash));
        Assert.Equal(ToolErrorCodes.BadArgument, earlier.Code);
        Assert.Equal(before, _den.Read(Swales));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-19")]
    public void AFileWithErrorsThatLinksToTheSectionIsNamed()
    {
        const string Broken = "universes/dracula/entities/notes/broken.md";
        _den.Write(Broken, "---\ntype: note\nname: Broken\nstatus: maybe\n---\n\nSee [[Mr. Swales#Voice]].\n");
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.RenameSection("Mr. Swales", "Voice", "Dialect", hash);

        Assert.Contains(result.Effects, e => e.Contains(Broken, StringComparison.Ordinal) && e.Contains("by hand", StringComparison.Ordinal));
        Assert.Contains("[[Mr. Swales#Voice]]", _den.Read(Broken), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-16")]
    public void AStatusChangeOnAnEntityWithPinnedFactsIsFlagged()
    {
        var hash = Tools.GetEntity("Count Dracula").Entity!.Hash!;

        var result = Tools.UpdateFields("Count Dracula", hash, Json("""[{"op": "set", "path": "status", "value": "draft"}]"""));

        Assert.Contains(result.PinnedCanon, p => p.Contains("'status'", StringComparison.Ordinal) && p.Contains("fields.species", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-16")]
    public void AnEditThatChangesNothingAddsNoId()
    {
        _den.Write(Swales, _den.Read(Swales).Replace("id: 01M4TF5F0EKGGWD5FNVBAEY8X7\n", "", StringComparison.Ordinal));
        var before = _den.Read(Swales);
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var same = Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "set", "path": "status", "value": "canon"}]"""));
        Assert.False(same.Changed);
        Assert.Null(same.IdAdded);
        Assert.Equal(before, _den.Read(Swales));

        // A real change adds one (F-ID-6).
        var changed = Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "set", "path": "status", "value": "draft"}]"""));
        Assert.NotNull(changed.IdAdded);
        Assert.StartsWith($"---\nid: {changed.IdAdded}\n", _den.Read(Swales), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-17")]
    public void ANoteSavedOnWindowsKeepsItsLineEndingsAndByteOrderMark()
    {
        var path = Path.Combine(_den.Root, Swales.Replace('/', Path.DirectorySeparatorChar));
        var crlf = _den.Read(Swales).ReplaceLineEndings("\r\n");
        File.WriteAllText(path, crlf, new System.Text.UTF8Encoding(true));
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;

        var result = Tools.AppendSection("Mr. Swales", "Voice", "He calls them lies.", hash);
        var fields = Tools.UpdateFields("Mr. Swales", result.Hash, Json("""[{"op": "add", "path": "aliases", "value": "old Swales"}]"""));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = System.Text.Encoding.UTF8.GetString(bytes[3..]);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("hears it.\r\n\r\nHe calls them lies.\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name: Mr. Swales\r\naliases: [old Swales]\r\n", text, StringComparison.Ordinal);
        Assert.True(fields.Changed);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-17")]
    public void TextAddedToASectionGoesBeforeItsSubsections()
    {
        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;
        var sub = Tools.AppendSection("Mr. Swales", "Afterwards", "The coroner's verdict.", hash, under: "Arc");

        Tools.AppendSection("Mr. Swales", "Arc", "Mina is shaken.", sub.Hash);

        Assert.EndsWith("look of horror on his face.\n\nMina is shaken.\n\n### Afterwards\nThe coroner's verdict.\n", _den.Read(Swales), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-20")]
    public void ChapterListItemsGoInAtAnIndexAndComeOutByValue()
    {
        var chapter = Tools.GetChapter("dracula", "6");

        var added = Tools.UpdateChapterFields("dracula", "6", chapter.Hash, Json("""[{"op": "add", "field": "pov", "value": "[[lucy-westenra]]", "index": 1}]"""));
        Assert.Contains("pov: [\"[[mina-harker]]\", \"[[lucy-westenra]]\", \"[[john-seward]]\"]", _den.Read(BookFile), StringComparison.Ordinal);

        var removed = Tools.UpdateChapterFields("dracula", "6", added.Hash, Json("""[{"op": "remove", "field": "pov", "value": "[[John-Seward]]"}]"""));
        Assert.Contains("pov: [\"[[mina-harker]]\", \"[[lucy-westenra]]\"]\n", _den.Read(BookFile), StringComparison.Ordinal);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.UpdateChapterFields("dracula", "6", removed.Hash, Json("""[{"op": "remove", "field": "pov", "value": "[[Mina]]"}]"""))).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.UpdateChapterFields("dracula", "6", removed.Hash, Json("""[{"op": "set", "field": "key", "value": "6b"}]"""))).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-16")]
    public void BadNamesAndAliasesAreRefusedAndAnAliasThatCollidesSaysSo()
    {
        var con = Tools.CreateEntity("location", "Con");
        Assert.Equal("universes/dracula/entities/locations/con-2.md", con.Path);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.CreateEntity("location", "X", slug: "nul")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.CreateEntity("location", "Y", aliases: ["bad\u0007alias"])).Code);

        var hash = Tools.GetEntity("Mr. Swales").Entity!.Hash!;
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "add", "path": "aliases", "value": "a\u0001b"}]"""))).Code);
        var collide = Tools.UpdateFields("Mr. Swales", hash, Json("""[{"op": "add", "path": "aliases", "value": "Renfield"}]"""));
        Assert.Contains(collide.Effects, e => e.Contains("also the slug of 'R. M. Renfield'", StringComparison.Ordinal));
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.Checkpoint("x", "Claude\u0007 <noreply@anthropic.com>")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => Tools.Checkpoint("x", "Claude <noreply@anthropic.com>", ["  "])).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-16")]
    public void EmptyingAMappingOrListSaysWhichCommentLinesWentWithIt()
    {
        // Comment lines inside a block mapping or list that loses its last key or item go with it (#157), and
        // the effects say so, since one may have been a commented-out value.
        _den.Write(Swales, _den.Read(Swales).Replace("status: canon\n", "status: canon\nx-notes:\n  # for the reread\n  reread: yes\n", StringComparison.Ordinal));
        var fields = Tools.UpdateFields("Mr. Swales", Tools.GetEntity("Mr. Swales").Entity!.Hash!, Json("""[{"op": "remove", "path": "x-notes.reread"}]"""));
        Assert.Contains("\nx-notes:\nfrom:", _den.Read(Swales), StringComparison.Ordinal);
        Assert.Contains("Removed 1 comment line inside x-notes, which the change emptied.", fields.Effects);

        _den.Write(BookFile, _den.Read(BookFile)
            .Replace("chapter_defaults:\n  narration: first\n  tense: past\n", "chapter_defaults:\n  # always first person\n  narration: first\n  pov:\n    # the usual\n    - \"[[mina-harker]]\"\n", StringComparison.Ordinal)
            .Replace(
                "    fields:\n      pov: [\"[[jonathan-harker]]\"]\n      when: { text: \"3–5 May\", sort: [0, 5, 3] }\n",
                "    fields:\n      # from his journal\n      pov:\n        # Jonathan writes it all\n        - \"[[jonathan-harker]]\"\n",
                StringComparison.Ordinal));
        var removed = Tools.UpdateChapterFields("dracula", "1", Tools.GetChapter("dracula", "1").Hash, Json("""[{"op": "remove", "field": "pov", "item": 0}]"""));
        Assert.Contains("Removed 1 comment line inside pov of chapter '1', which the change emptied.", removed.Effects);
        var cleared = Tools.UpdateChapterFields("dracula", "1", removed.Hash, Json("""[{"op": "clear", "field": "pov"}]"""));
        Assert.Contains("Removed 1 comment line inside the fields of chapter '1', which the change emptied.", cleared.Effects);
        Assert.Contains("    title: Jonathan Harker's Journal\n    fields:\n  - key: 2\n", _den.Read(BookFile), StringComparison.Ordinal);

        var item = Tools.UpdateChapterDefaults(cleared.Hash, Json("""[{"op": "remove", "field": "pov", "item": 0}]"""), book: "dracula");
        Assert.Contains("Removed 1 comment line inside chapter_defaults.pov, which the change emptied.", item.Effects);
        var defaults = Tools.UpdateChapterDefaults(
            item.Hash, Json("""[{"op": "clear", "field": "pov"}, {"op": "clear", "field": "narration"}]"""), book: "dracula");
        Assert.Single(defaults.Effects, e => e.StartsWith("Removed", StringComparison.Ordinal));
        Assert.Contains("Removed 1 comment line inside chapter_defaults, which the change emptied.", defaults.Effects);
        Assert.Contains("\nchapter_defaults:\nchapters:\n", _den.Read(BookFile), StringComparison.Ordinal);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A den store on disk whose writes to one path fail, as a full disk or a locked file would.</summary>
    internal sealed class FailingStore(string root, string failing, Func<Exception>? failure = null, Action? before = null) : Core.Structure.IDenStore
    {
        private readonly Core.Structure.FileSystemDenStore _disk = new(root);

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
            if (path == failing)
            {
                before?.Invoke();
                throw failure?.Invoke() ?? new Core.Structure.DenStoreException(Core.Structure.DenStoreError.AccessDenied, path, $"'{path}' is read-only.");
            }

            _disk.WriteFile(path, bytes, mode);
        }

        public void DeleteFile(string path) => _disk.DeleteFile(path);

        public void MoveFile(string from, string to) => _disk.MoveFile(from, to);

        public void MoveFolder(string from, string to) => _disk.MoveFolder(from, to);

        public void DeleteFolder(string path) => _disk.DeleteFolder(path);
    }
}
