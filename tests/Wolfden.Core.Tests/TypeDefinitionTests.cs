using Wolfden.Core.Documents;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;

namespace Wolfden.Core.Tests;

/// <summary>
/// Problems inside pack files (types.md §3 to §8): each is reported with its
/// rule id and line, and drops only what it has to, never the den.
/// </summary>
public class TypeDefinitionTests
{
    private static DenStructure Load(params (string Path, string Text)[] files)
    {
        var source = new MemoryDenSource()
            .With("den.yaml", "format: 1\npacks: [starfall]\n")
            .With("packs/starfall/pack.yaml", "name: Starfall\n")
            .Universe("starfall");
        foreach (var (path, text) in files)
        {
            source.With($"packs/starfall/{path}", text);
        }

        return DenStructure.Load(source);
    }

    private static (string Path, string Rule, DiagnosticSeverity Severity, int? Line)[] Rules(DenStructure den) =>
        den.Diagnostics.Select(d => (d.Path["packs/starfall/".Length..], d.Diagnostic.RuleId, d.Diagnostic.Severity, d.Diagnostic.Line)).ToArray();

    [Theory]
    [InlineData("types/ship.yaml", "plural: ships\n", "F-TYPE-3", 1)]
    [InlineData("types/ship.yaml", "name: Ship\n", "F-TYPE-3", 1)]
    [InlineData("types/ship.yaml", "name: Ship\nplural: Star Ships\n", "F-TYPE-3", 2)]
    [InlineData("types/ship.yaml", "name: Ship\nplural: ships\nfields: [class]\n", "F-TYPE-3", 3)]
    [InlineData("types/ship.yaml", "name: Ship\nplural: ships\nsections: Overview\n", "F-TYPE-3", 3)]
    [InlineData("types/ship.yaml", "name: Ship\nplural: ships\ndescription: [a, b]\n", "F-TYPE-3", 3)]
    [InlineData("types/ship.yaml", "", "F-TYPE-3", 1)]
    [InlineData("types/ship.yaml", "- name: Ship\n", "F-YAML-2", 1)]
    [InlineData("types/ship.yaml", "name: Ship\nname: Boat\nplural: ships\n", "F-YAML-3", 2)]
    [InlineData("types/Ship.yaml", "name: Ship\nplural: ships\n", "F-TYPE-1", null)]
    [InlineData("types/book.yaml", "name: Ship\nplural: ships\n", "F-TYPE-2", null)]
    public void ABrokenTypeFileIsExcluded(string path, string text, string rule, int? line)
    {
        var den = Load((path, text));

        Assert.True(den.IsLoaded);
        Assert.Contains((path, rule, DiagnosticSeverity.Error, line), Rules(den));
        Assert.Null(den.Types.Find("ship"));
        Assert.NotNull(den.Types.Find("character"));
    }

    [Fact]
    public void ABrokenFieldDefinitionDropsOnlyThatField()
    {
        const string ship = """
            name: Ship
            plural: ships
            fields:
              class: strng
              crew: { type: number, list: yes }
              tonnage: { type: number }
              hull: { list: true }
              row: text
              Mast: text
              mood: enum
              state: { type: enum, values: [] }
              colour: { type: enum, values: [Red, red, "dark blue", green] }
              flag: { type: text, to: faction, values: [a], description: [x], colour: blue }
              pilot: { type: ref, to: "Some Type" }
              escort: { type: ref, to: [ship, starbase] }
            """;

        var den = Load(("types/ship.yaml", ship));
        var type = den.Types.Find("ship")!;

        Assert.Equal(["tonnage", "colour", "flag", "escort"], type.Fields.Select(f => f.Name));
        Assert.Equal(["red", "green"], type.FindField("colour")!.Values);
        Assert.Null(type.FindField("flag")!.Description);
        Assert.Equal(["ship", "starbase"], type.FindField("escort")!.To);
        Assert.Equal(["ship"], type.FindField("escort")!.CheckedTo);
        Assert.Equal(
            [
                ("types/ship.yaml", "F-FT-2", DiagnosticSeverity.Warning, (int?)4),
                ("types/ship.yaml", "F-FT-2", DiagnosticSeverity.Warning, 5),
                ("types/ship.yaml", "F-FT-2", DiagnosticSeverity.Warning, 7),
                ("types/ship.yaml", "F-FT-1", DiagnosticSeverity.Warning, 8),
                ("types/ship.yaml", "F-FT-1", DiagnosticSeverity.Warning, 9),
                ("types/ship.yaml", "F-FT-8", DiagnosticSeverity.Warning, 10),
                ("types/ship.yaml", "F-FT-8", DiagnosticSeverity.Warning, 11),
                ("types/ship.yaml", "F-FT-8", DiagnosticSeverity.Warning, 12),
                ("types/ship.yaml", "F-FT-8", DiagnosticSeverity.Warning, 12),
                ("types/ship.yaml", "F-FT-8", DiagnosticSeverity.Warning, 12),
                ("types/ship.yaml", "F-FT-2", DiagnosticSeverity.Warning, 13),
                ("types/ship.yaml", "F-FT-2", DiagnosticSeverity.Warning, 13),
                ("types/ship.yaml", "F-FT-2", DiagnosticSeverity.Warning, 13),
                ("types/ship.yaml", "F-FT-2", DiagnosticSeverity.Warning, 13),
                ("types/ship.yaml", "F-FT-2", DiagnosticSeverity.Warning, 14),
                ("types/ship.yaml", "F-FT-9", DiagnosticSeverity.Warning, 15),
            ],
            Rules(den));
    }

    [Fact]
    public void DefaultsAreCheckedAgainstTheirFields()
    {
        const string ship = """
            name: Ship
            plural: ships
            fields:
              condition: { type: enum, values: [active, lost], default: Active }
              crew: { type: number, default: "many" }
              captain: { type: ref, default: "[[mara-voss]]" }
              class: { type: string, required: true, default: frigate }
            """;

        var den = Load(("types/ship.yaml", ship));
        var type = den.Types.Find("ship")!;

        Assert.Equal("Active", type.FindField("condition")!.Default!.ToString());
        Assert.Null(type.FindField("crew")!.Default);
        Assert.Null(type.FindField("captain")!.Default);
        Assert.Equal(
            [
                ("types/ship.yaml", "F-FT-15", DiagnosticSeverity.Warning, (int?)5),
                ("types/ship.yaml", "F-FT-15", DiagnosticSeverity.Warning, 6),
                ("types/ship.yaml", "F-FT-14", DiagnosticSeverity.Warning, 7),
            ],
            Rules(den));
    }

    [Fact]
    public void BadSectionItemsAreDropped()
    {
        const string ship = """
            name: Ship
            plural: ships
            sections:
              - Overview
              - { name: Crew, required: true, description: Who serves aboard. }
              - { name: Log, required: maybe }
              - [Nested]
              - "  "
              - overview
              - { name: Refits, colour: blue }
            """;

        var den = Load(("types/ship.yaml", ship));
        var type = den.Types.Find("ship")!;

        Assert.Equal(["Overview", "Crew", "Refits"], type.Sections.Select(s => s.Name));
        Assert.True(type.Sections[1].IsRequired);
        Assert.Equal("Who serves aboard.", type.Sections[1].Description);
        Assert.Equal(
            [
                ("types/ship.yaml", "F-TYPE-9", DiagnosticSeverity.Warning, (int?)6),
                ("types/ship.yaml", "F-TYPE-9", DiagnosticSeverity.Warning, 7),
                ("types/ship.yaml", "F-TYPE-9", DiagnosticSeverity.Warning, 8),
                ("types/ship.yaml", "F-TYPE-9", DiagnosticSeverity.Warning, 9),
                ("types/ship.yaml", "F-TYPE-9", DiagnosticSeverity.Warning, 10),
            ],
            Rules(den));
    }

    [Fact]
    public void UnknownKeysAreKeptWithAWarning()
    {
        var den = Load(("types/ship.yaml", "name: Ship\nplural: ships\ncolour: teal\n"), ("chapter.yaml", "fields: {}\nnotes: x\n"));

        Assert.Equal(
            [
                ("chapter.yaml", "F-CHAP-1", DiagnosticSeverity.Warning, (int?)2),
                ("types/ship.yaml", "F-TYPE-3", DiagnosticSeverity.Warning, 3),
            ],
            Rules(den));
        Assert.NotNull(den.Types.Find("ship"));
    }

    [Fact]
    public void PluralsMustBeUnique()
    {
        var den = Load(
            ("types/ship.yaml", "name: Ship\nplural: vessels\n"),
            ("types/boat.yaml", "name: Boat\nplural: vessels\n"),
            ("types/muse.yaml", "name: Muse\nplural: ideas\n"));

        // F-TYPE-5: within a pack the earlier key wins; against core, the den's own type wins.
        Assert.NotNull(den.Types.Find("boat"));
        Assert.Null(den.Types.Find("ship"));
        Assert.NotNull(den.Types.Find("muse"));
        Assert.Null(den.Types.Find("idea"));
        Assert.Equal(
            [
                ("types/muse.yaml", "F-TYPE-5", DiagnosticSeverity.Warning, (int?)null),
                ("types/ship.yaml", "F-TYPE-5", DiagnosticSeverity.Warning, null),
            ],
            Rules(den));
    }

    [Fact]
    public void ChapterFieldsHaveNoTemporalRequiredOrDefault()
    {
        const string chapter = """
            fields:
              weather: { type: text, temporal: true, required: true, default: fair }
              title: text
              mood: { type: enum, values: [grim, light] }
            """;

        var den = Load(("chapter.yaml", chapter));
        var weather = den.Types.FindChapterField("weather")!;

        Assert.Equal((false, false, null), (weather.IsTemporal, weather.IsRequired, weather.Default));
        Assert.Null(den.Types.FindChapterField("title"));
        Assert.NotNull(den.Types.FindChapterField("mood"));
        Assert.Equal(
            [
                ("chapter.yaml", "F-CHAP-1", DiagnosticSeverity.Warning, (int?)2),
                ("chapter.yaml", "F-CHAP-1", DiagnosticSeverity.Warning, 2),
                ("chapter.yaml", "F-CHAP-1", DiagnosticSeverity.Warning, 2),
                ("chapter.yaml", "F-CHAP-1", DiagnosticSeverity.Warning, 3),
            ],
            Rules(den));
    }

    [Fact]
    public void BadTemplateValuesAreWarnings()
    {
        var den = Load(
            ("types/ship.yaml", "name: Ship\nplural: ships\ntemplate: templates/ship.md\n"),
            ("types/boat.yaml", "name: Boat\nplural: boats\ntemplate: boat.txt\n"),
            ("types/raft.yaml", "name: Raft\nplural: rafts\ntemplate: missing.md\n"),
            ("templates/spare.md", "# {{name}}\n"));

        Assert.All(["ship", "boat", "raft"], key => Assert.True(den.Types.Find(key)!.Template.IsGenerated));
        Assert.Equal(
            [
                ("templates/spare.md", "F-TPL-2", DiagnosticSeverity.Warning, (int?)null),
                ("types/boat.yaml", "F-TPL-1", DiagnosticSeverity.Warning, 3),
                ("types/raft.yaml", "F-TPL-1", DiagnosticSeverity.Warning, 3),
                ("types/ship.yaml", "F-TPL-1", DiagnosticSeverity.Warning, 3),
            ],
            Rules(den));
    }

    [Theory]
    [InlineData("---\nid: 01JA7Q8M4XK3V9R2D5W8HN6TQB\n---\n# {{name}}\n", "F-TPL-3", 2)]
    [InlineData("---\nstatus: maybe\n---\n# {{name}}\n", "F-STAT-1", 2)]
    [InlineData("---\naliases: Voss\n---\n# {{name}}\n", "F-ENT-4", 2)]
    [InlineData("---\nfrom: ember:1.2\n---\n# {{name}}\n", "F-POS-2", 2)]
    [InlineData("---\nfields:\n  Bad Name: x\n---\n# {{name}}\n", "F-FLD-1", 3)]
    [InlineData("---\nstatus: draft\n", "F-FM-1", 1)]
    [InlineData("# {{name}}\n\n## Crew\n<!-- wolfden: status=maybe -->\n", "F-STAT-1", 4)]
    public void ATemplateWithAnErrorIsReplacedByTheGeneratedOne(string template, string rule, int line)
    {
        var den = Load(("types/ship.yaml", "name: Ship\nplural: ships\nsections: [Crew]\n"), ("templates/ship.md", template));

        Assert.Contains(("templates/ship.md", rule, DiagnosticSeverity.Error, (int?)line), Rules(den));
        Assert.True(den.Types.Find("ship")!.Template.IsGenerated);
    }

    [Fact]
    public void ATemplateIsCheckedAgainstTheTypeUsingIt()
    {
        var den = Load(
            ("types/ship.yaml", "name: Ship\nplural: ships\nfields:\n  class: string\n"),
            ("templates/ship.md", "---\ntype: boat\nname: ignored\nfields:\n  class:\n  hull:\n  row:\n---\n# {{name}}\n"));

        Assert.Equal(
            [
                ("templates/ship.md", "F-TPL-3", DiagnosticSeverity.Warning, (int?)2),
                ("templates/ship.md", "F-FLD-2", DiagnosticSeverity.Warning, 6),
            ],
            Rules(den));
        Assert.Equal("ship.md", den.Types.Find("ship")!.Template.FileName);
    }

    [Fact]
    public void ATemplateWithoutFrontmatterIsAllBody()
    {
        var den = Load(("types/ship.yaml", "name: Ship\nplural: ships\n"), ("templates/ship.md", "# {{name}}\n\n## Crew\n"));

        Assert.Empty(den.Diagnostics);
        Assert.Equal("# {{name}}\n\n## Crew\n", den.Types.Find("ship")!.Template.Text);
    }

    [Theory]
    [InlineData("pack.yaml")]
    [InlineData("chapter.yaml")]
    [InlineData("types/ship.yaml")]
    [InlineData("extensions/character.yaml")]
    [InlineData("templates/ship.md")]
    public void NoPackFileCanCrashTheLoad(string path)
    {
        string[] junk =
        [
            "", "~", "- a\n- b\n", "[unclosed", "name: &a x\nplural: *a\n", "\t\tname: x\n", "name: x\r\nplural: y\n\n\r",
            "name:\n  - [ { a: [ { } ] } ]\n", "fields:\n  a: { type: { nested: true } }\nsections: [ { name: [x] } ]\n",
            "---\n---\n---\n", "﻿name: Ship\nplural: ships\n", "name: Ship\nplural: ships\ntemplate: [a]\nfields: { x: { type: enum, values: { a: b } } }\n",
        ];

        foreach (var text in junk)
        {
            var source = new MemoryDenSource()
                .With("den.yaml", "format: 1\npacks: [starfall]\n")
                .With("packs/starfall/pack.yaml", "name: Starfall\n")
                .With("packs/starfall/types/ship.yaml", "name: Ship\nplural: ships\n")
                .With($"packs/starfall/{path}", text)
                .Universe("starfall");
            var den = DenStructure.Load(source);
            Assert.True(den.IsLoaded, text);
        }

        var invalid = new MemoryDenSource()
            .With("den.yaml", "format: 1\npacks: [starfall]\n")
            .With("packs/starfall/pack.yaml", "name: Starfall\n")
            .Universe("starfall");
        invalid.WithBytes($"packs/starfall/{path}", [0xC3, 0x28, 0x0A]);
        Assert.Contains(DenStructure.Load(invalid).Diagnostics, d => d.Diagnostic.RuleId == "F-ENC-1");
    }
}
