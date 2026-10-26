using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;
using static Wolfden.Core.Tests.LinkDens;

namespace Wolfden.Core.Tests;

/// <summary>Loading a den's entities (format.md §5.1, §7) and its settings (F-DEN-6, F-DEN-7, F-DEN-8).</summary>
public class EntityCatalogTests
{
    private static (string Path, string Rule, DiagnosticSeverity Severity)[] Rules(IEnumerable<DenDiagnostic> diagnostics) =>
        diagnostics.Select(d => (d.Path, d.Diagnostic.RuleId, d.Diagnostic.Severity)).ToArray();

    [Fact]
    public void EntitiesAreFoundUnderEveryLoadedContainer()
    {
        var source = Den()
            .Entity(Starfall, "kessan-reach", Character("Kessan Reach", "id: 01JADJ8FSSZ5AWSH8VHTPRE95B"), folder: "locations")
            .Entity(LongDark, "mara-voss", Character("Mara Voss", "id: 01JA7Q8M4XK3V9R2D5W8HN6TQB"))
            .Entity(Ash, "halden-pike", Character("Halden Pike", "id: 01JA7Q8M4XK3V9R2D5W8HN6TQC"), folder: "characters/crew")
            .Entity(Moon, "lantern", Character("Lantern", "id: 01JA7Q8M4XK3V9R2D5W8HN6TQE"), folder: "ships")
            .With($"{Starfall}/entities/.hidden/ghost.md", "---\ntype: note\nname: Ghost\n---\n")
            .With($"{Starfall}/entities/notes/.draft.md", "---\ntype: note\nname: Draft\n---\n")
            .With($"{Starfall}/entities/notes/map.png", "not markdown")
            .With($"{Starfall}/entities/notes/upper.MD", "---\ntype: note\nname: Upper\n---\n")
            .With($"{Starfall}/notes.md", "---\ntype: note\nname: Stray\n---\n")
            .With($"{Starfall}/series/broken/series.md", "---\nbooks: [x]\n---\n")
            .Entity($"{Starfall}/series/broken", "lost", Character("Lost"));

        var catalog = EntityCatalog.Load(source);

        Assert.Equal(
            [$"{Moon}/entities/ships/lantern.md", $"{Starfall}/entities/locations/kessan-reach.md",
             $"{Ash}/entities/characters/crew/halden-pike.md", $"{LongDark}/entities/characters/mara-voss.md"],
            catalog.Entities.Select(e => e.Path));
        Assert.Empty(catalog.Diagnostics);
        var pike = catalog.FindByPath($"{Ash}/entities/characters/crew/halden-pike.md")!;
        Assert.Equal("ash", pike.Scope.Key);
        Assert.Equal("halden-pike", pike.Slug);
        Assert.Equal("01JA7Q8M4XK3V9R2D5W8HN6TQC", pike.Key);
        Assert.Equal(["kessan-reach"], catalog.InUniverse(catalog.Structure.FindUniverse("starfall")!).Where(e => e.Scope.Kind == ContainerKind.Universe).Select(e => e.Slug));
    }

    [Theory]
    [InlineData("name: X\nstatus: canon", "F-ENT-2")]
    [InlineData("type: ''\nname: X", "F-ENT-2")]
    [InlineData("type: character\nstatus: canon", "F-ENT-3")]
    [InlineData("type: character\nname: '  '", "F-ENT-3")]
    [InlineData("type: character\nname: [X]", "F-ENT-3")]
    [InlineData("type: character\nname: X\naliases: Voss", "F-ENT-4")]
    [InlineData("type: character\nname: X\naliases: [Voss, ~]", "F-ENT-4")]
    [InlineData("type: character\nname: X\naliases: [\"\"]", "F-ENT-4")]
    [InlineData("type: character\nname: X\nstatus: maybe", "F-ENT-5")]
    [InlineData("type: character\nname: X\nid: 01ja7q8m4xk3v9r2d5w8hn6tqb", "F-ID-1")]
    [InlineData("type: character\nname: X\nfrom: \"ember:14.2\"", "F-POS-2")]
    [InlineData("type: character\nname: X\nfrom: \"Ember\"", "F-POS-3")]
    [InlineData("type: character\nname: X\nfields: [a]", "F-ENT-7")]
    [InlineData("type: character\nname: X\nfields:\n  Rank: x", "F-FLD-1")]
    [InlineData("type: character\nname: X\nfields:\n  rank: [a, { value: b }]", "F-TMP-1")]
    [InlineData("type: character\nname: X\nfields:\n  rank: { value: { value: b } }", "F-TMP-2")]
    [InlineData("type: character\nname: X\nfields:\n  rank: [{ value: a, status: maybe }]", "F-STAT-1")]
    [InlineData("type: character\nname: X\nfields:\n  rank: { value: a, pin: yes }", "F-PIN-1")]
    [InlineData("type: character\nname: X\nname: Y", "F-YAML-3")]
    public void EntitiesWithErrorsAreLeftOut(string frontmatter, string rule)
    {
        var catalog = EntityCatalog.Load(Den().Entity(Starfall, "x", frontmatter));

        Assert.Empty(catalog.Entities);
        Assert.Contains(catalog.Diagnostics, d => d.Diagnostic.RuleId == rule && d.Diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("type: character\nname: 1984", "1984")]
    [InlineData("type: character\nname: X\naliases: ~", "X")]
    [InlineData("type: character\nname: X\naliases:\nstatus: draft", "X")]
    [InlineData("type: character\nname: X\naliases: []", "X")]
    [InlineData("type: character\nname: X\nstatus: ~\nfrom:", "X")]
    [InlineData("type: unknown-type\nname: X\nfields:\n  anything: [{ value: a, from: \"ember:1\" }]", "X")]
    public void ValidEntitiesLoad(string frontmatter, string name)
    {
        var catalog = EntityCatalog.Load(Den().Entity(Starfall, "x", frontmatter));

        var entity = Assert.Single(catalog.Entities);
        Assert.Equal(name, entity.Name);
        Assert.Equal(FactStatus.Draft, entity.Status);
    }

    [Fact]
    public void MissingIdsAndBadSlugsAreWarnings()
    {
        var catalog = EntityCatalog.Load(Den().With($"{Starfall}/entities/notes/Mara Voss.md", "---\ntype: note\nname: Mara Voss\n---\n"));

        var entity = Assert.Single(catalog.Entities);
        Assert.Null(entity.Id);
        Assert.Null(entity.Slug);
        Assert.Equal(entity.Path, entity.Key);
        Assert.Equal(
            [($"{Starfall}/entities/notes/Mara Voss.md", "F-KEY-4", DiagnosticSeverity.Warning), ($"{Starfall}/entities/notes/Mara Voss.md", "F-ID-2", DiagnosticSeverity.Warning)],
            Rules(catalog.Diagnostics));
    }

    [Fact]
    public void DuplicateIdsAreWarningsOnEveryFile()
    {
        const string id = "01JA7Q8M4XK3V9R2D5W8HN6TQB";
        var source = Den()
            .Entity(Starfall, "mara-voss", Character("Mara Voss", $"id: {id}"))
            .Entity(Ash, "mara-voss", Character("Mara Voss", $"id: {id}"))
            .With($"{Moon}/book.md", $"---\nid: {id}\nname: A Quiet Moon\nchapters: [1]\n---\n");

        var catalog = EntityCatalog.Load(source);

        Assert.Equal(2, catalog.Entities.Count);
        Assert.All(catalog.Entities, e => Assert.True(catalog.HasDuplicateId(e)));
        Assert.Equal(
            [($"{Moon}/book.md", "F-ID-3", DiagnosticSeverity.Warning),
             ($"{Starfall}/entities/characters/mara-voss.md", "F-ID-3", DiagnosticSeverity.Warning),
             ($"{Ash}/entities/characters/mara-voss.md", "F-ID-3", DiagnosticSeverity.Warning)],
            Rules(catalog.Diagnostics));
    }

    [Theory]
    // F-ENT-6: without from, an entity enters at the start of its book, series, or the story.
    [InlineData(Starfall, null, "the start of the story")]
    [InlineData(LongDark, null, "the start of series 'long-dark'")]
    [InlineData(Ash, null, "the start of book 'ash'")]
    [InlineData(Ash, "ember:2", "ember:2")]
    public void EntitiesEnterCanonAtTheirFromOrTheStartOfTheirScope(string container, string? from, string description)
    {
        var frontmatter = from is null ? Character("X") : Character("X", $"from: \"{from}\"");
        var entity = Assert.Single(EntityCatalog.Load(Den().Entity(container, "x", frontmatter)).Entities);
        var starfall = entity.Universe;

        Assert.Equal(description, entity.EntersCanonDescription);
        var expected = from is not null ? starfall.Resolve(Positions.StoryPosition.Parse(from)).Resolved!.Point : entity.Scope.Start;
        Assert.Equal(expected, entity.EntersCanon);
    }

    [Fact]
    public void AnEntityWhoseFromDoesntResolveHasNoEntryPoint()
    {
        var entity = Assert.Single(EntityCatalog.Load(Den().Entity(Starfall, "x", Character("X", "from: \"ember:99\""))).Entities);

        Assert.False(entity.FromResolution!.IsResolved);
        Assert.Null(entity.EntersCanon);
    }

    [Theory]
    [InlineData("spoiler_checks: true", true, null)]
    [InlineData("spoiler_checks: True", true, null)]
    [InlineData("spoiler_checks: false", false, null)]
    [InlineData("spoiler_checks:", false, null)]
    [InlineData("", false, null)]
    [InlineData("spoiler_checks: yes", false, "F-DEN-7")]
    [InlineData("spoiler_checks: \"true\"", false, "F-DEN-7")]
    [InlineData("spoiler_checks: [true]", false, "F-DEN-7")]
    public void SpoilerChecksAreABoolean(string line, bool on, string? warning)
    {
        var den = DenStructure.Load(MemoryDenSource.Den().With("den.yaml", $"format: 1\n{line}\n"));

        Assert.True(den.IsLoaded);
        Assert.Equal(on, den.SpoilerChecks);
        Assert.Equal(warning is null ? [] : [("den.yaml", warning, DiagnosticSeverity.Warning)], Rules(den.Diagnostics));
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-37")]
    [InlineData("remote_images: true", true, null)]
    [InlineData("remote_images: TRUE", true, null)]
    [InlineData("remote_images: false", false, null)]
    [InlineData("remote_images:", false, null)]
    [InlineData("", false, null)]
    [InlineData("remote_images: yes", false, "F-DEN-8")]
    [InlineData("remote_images: \"true\"", false, "F-DEN-8")]
    [InlineData("remote_images: { https: true }", false, "F-DEN-8")]
    public void RemoteImagesAreABooleanThatDefaultsToOff(string line, bool on, string? warning)
    {
        var text = $"format: 1\n{line}\n";
        var den = DenStructure.Load(MemoryDenSource.Den().With("den.yaml", text));

        Assert.True(den.IsLoaded);
        Assert.Equal(on, den.RemoteImages);
        Assert.Equal(warning is null ? [] : [("den.yaml", warning, DiagnosticSeverity.Warning)], Rules(den.Diagnostics));

        // wolfden serve reads the setting straight from the file, the same way.
        Assert.Equal(on, DenStructure.ReadRemoteImages(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-37")]
    [InlineData("format: 1\nremote_images: true\nremote_images: true\n")]
    [InlineData("format: 1\nremote_images: [true\n")]
    [InlineData("remote_images: true\n---\nmore: 1\n")]
    [InlineData("remote_images: true\n")]
    [InlineData("format: 2\nremote_images: true\n")]
    [InlineData("format: 0\nremote_images: true\n")]
    [InlineData("format: one\nremote_images: true\n")]
    [InlineData("format: \"1\"\nremote_images: true\n")]
    public void ADenYamlWithoutAFormatItCanReadLeavesRemoteImagesOff(string text)
    {
        Assert.False(DenStructure.ReadRemoteImages(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.False(DenStructure.ReadRemoteImages([0xFF, 0xFE, 0x00]));
    }

    [Theory]
    [InlineData("bookkeeping_sections: [Scratch, \"* working notes\"]", true, null)]
    [InlineData("bookkeeping_sections:", true, null)]
    [InlineData("bookkeeping_sections: [\"* \"]", true, "F-DEN-6")]
    [InlineData("bookkeeping_sections: Scratch", false, "F-DEN-6")]
    [InlineData("bookkeeping_sections: [Scratch, \"\"]", false, "F-DEN-6")]
    [InlineData("bookkeeping_sections: [Scratch, ~]", false, "F-DEN-6")]
    [InlineData("bookkeeping_sections: [[Scratch]]", false, "F-DEN-6")]
    public void BookkeepingSectionsAreAListOfPatterns(string line, bool loaded, string? rule)
    {
        var den = DenStructure.Load(MemoryDenSource.Den().With("den.yaml", $"format: 1\n{line}\n"));

        Assert.Equal(loaded, den.IsLoaded);
        Assert.Equal(rule is null ? [] : [("den.yaml", rule, loaded ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error)], Rules(den.Diagnostics));
        if (rule is null)
        {
            Assert.Equal(line.Contains("Scratch", StringComparison.Ordinal) ? ["Scratch", "* working notes"] : [], den.BookkeepingSections);
        }
    }
}
