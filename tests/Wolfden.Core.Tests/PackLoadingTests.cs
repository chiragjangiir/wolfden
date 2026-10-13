using Wolfden.Core.Documents;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;

namespace Wolfden.Core.Tests;

/// <summary>
/// Den-local packs (types.md §2 to §4): enabling, shadowing, clashes, the
/// den winning over built-in definitions, and broken pack files degrading
/// without stopping the den from loading.
/// </summary>
public class PackLoadingTests
{
    private const string Ship = """
        name: Ship
        plural: ships
        fields:
          class: string
          captain: { type: ref, to: character, temporal: true }
          condition:
            type: enum
            values: [active, damaged, lost]
            default: active
            temporal: true
        sections:
          - Overview
          - Crew
          - Service history
        """;

    /// <summary>A den with one universe, <c>den.yaml</c> listing <paramref name="packs"/>, and the given extra files.</summary>
    private static MemoryDenSource Den(string packs, params (string Path, string Text)[] files)
    {
        var source = new MemoryDenSource()
            .With("den.yaml", $"format: 1\n{(packs.Length > 0 ? $"packs: [{packs}]\n" : "")}")
            .Universe("starfall");
        foreach (var (path, text) in files)
        {
            source.With(path, text);
        }

        return source;
    }

    private static (string Path, string Rule, DiagnosticSeverity Severity, int? Line)[] Rules(DenStructure den) =>
        den.Diagnostics.Select(d => (d.Path, d.Diagnostic.RuleId, d.Diagnostic.Severity, d.Diagnostic.Line)).ToArray();

    [Fact]
    public void ADenWithoutPacksHasTheCoreTypes()
    {
        var den = DenStructure.Load(Den(""));

        Assert.Empty(den.Diagnostics);
        Assert.Equal(DenTypes.BuiltIn.Types.Select(t => t.Key), den.Types.Types.Select(t => t.Key));
    }

    [Fact]
    public void ADenLocalPackAddsATypeAndExtendsACoreOne()
    {
        var den = DenStructure.Load(Den("starfall",
            ("packs/starfall/pack.yaml", "name: Starfall\nversion: 1.2\n"),
            ("packs/starfall/types/ship.yaml", Ship),
            ("packs/starfall/extensions/character.yaml", "fields:\n  rank: { type: string, temporal: true }\n  posting: { type: ref, to: ship, temporal: true }\nsections:\n  - Service record\n")));

        Assert.Empty(den.Diagnostics);
        Assert.Equal(["core", "starfall"], den.Types.Packs.Select(p => p.Key));
        Assert.Equal("1.2", den.Types.Packs[1].Version);

        // types.md §10: core's fields, then the extension's; sections end with Service record.
        var character = den.Types.Find("character")!;
        Assert.Equal(["role", "gender", "pronouns", "occupation", "skills", "heritage", "born", "died", "species", "factions", "rank", "posting"],
            character.Fields.Select(f => f.Name));
        Assert.Equal(["Open questions", "Service record"], character.Sections.TakeLast(2).Select(s => s.Name));
        Assert.Equal("packs/starfall/extensions/character.yaml", character.FindField("rank")!.Path);
        Assert.True(character.IsBuiltIn);

        var ship = den.Types.Find("ship")!;
        Assert.False(ship.IsBuiltIn);
        Assert.Equal("ships", ship.Plural);
        Assert.Equal("active", ship.FindField("condition")!.Default!.ToString());
        Assert.Equal(["character"], ship.FindField("captain")!.CheckedTo);
    }

    [Fact]
    public void AGeneratedTemplateListsTheStandardSections()
    {
        var den = DenStructure.Load(Den("starfall",
            ("packs/starfall/pack.yaml", "name: Starfall\n"),
            ("packs/starfall/types/ship.yaml", Ship)));

        var template = den.Types.Find("ship")!.Template;
        Assert.True(template.IsGenerated);
        Assert.Equal("---\nstatus: draft\n---\n\n# {{name}}\n\n## Overview\n\n## Crew\n\n## Service history\n\n", template.Text);
        Assert.Empty(template.File.Diagnostics);
    }

    [Fact]
    public void ATypeUsesTemplatesNamedForItsKey()
    {
        var den = DenStructure.Load(Den("starfall",
            ("packs/starfall/pack.yaml", "name: Starfall\n"),
            ("packs/starfall/types/ship.yaml", Ship),
            ("packs/starfall/templates/ship.md", "---\ntype: ship\nstatus: draft\nfields:\n  class:\n---\n\n# {{name}}\n")));

        Assert.Empty(den.Diagnostics);
        Assert.Equal("ship.md", den.Types.Find("ship")!.Template.FileName);
    }

    [Fact]
    public void ADenLocalTypeReplacesABuiltInOneWithAWarning()
    {
        var den = DenStructure.Load(Den("starfall",
            ("packs/starfall/pack.yaml", "name: Starfall\n"),
            ("packs/starfall/types/thread.yaml", "name: Plot thread\nplural: threads\nsections: [Payoff]\n")));

        // F-TYPE-4, F-PACK-11: the warning goes on the den-local file, since the ignored one is built in.
        var warning = Assert.Single(den.Diagnostics);
        Assert.Equal(("packs/starfall/types/thread.yaml", "F-TYPE-4", DiagnosticSeverity.Warning),
            (warning.Path, warning.Diagnostic.RuleId, warning.Diagnostic.Severity));
        Assert.Equal("type 'thread' is defined by both pack 'starfall' and built-in pack 'core'; using 'starfall'.", warning.Diagnostic.Message);
        var thread = den.Types.Find("thread")!;
        Assert.Equal("Plot thread", thread.Name);
        Assert.False(thread.IsCoreThread);
        Assert.True(thread.Template.IsGenerated);
    }

    [Fact]
    public void ADenLocalExtensionFieldReplacesABuiltInOneAtItsOwnPlace()
    {
        var den = DenStructure.Load(Den("starfall",
            ("packs/starfall/pack.yaml", "name: Starfall\n"),
            ("packs/starfall/extensions/character.yaml", "fields:\n  born: number\n  rank: string\nsections: [Appearance, Service record]\n")));

        var character = den.Types.Find("character")!;
        Assert.Equal(["role", "gender", "pronouns", "occupation", "skills", "heritage", "died", "species", "factions", "born", "rank"],
            character.Fields.Select(f => f.Name));
        Assert.Equal(FieldKind.Number, character.FindField("born")!.Kind);
        Assert.Equal(["Overview", "Backstory", "Want", "Wound", "Voice", "Relationships", "Arc", "Open questions", "Appearance", "Service record"],
            character.Sections.Select(s => s.Name));
        Assert.Equal(
            [
                ("packs/starfall/extensions/character.yaml", "F-TYPE-8", DiagnosticSeverity.Warning, (int?)2),
                ("packs/starfall/extensions/character.yaml", "F-TYPE-8", DiagnosticSeverity.Warning, 4),
            ],
            Rules(den));
    }

    [Fact]
    public void ADenLocalChapterFieldWins()
    {
        var den = DenStructure.Load(Den("starfall",
            ("packs/starfall/pack.yaml", "name: Starfall\n"),
            ("packs/starfall/chapter.yaml", "fields:\n  tense: { type: enum, values: [past, present, future, aorist] }\n  weather: text\n")));

        Assert.Equal(["pov", "narration", "when", "tense", "weather"], den.Types.ChapterFields.Select(f => f.Name));
        Assert.Equal("aorist", den.Types.FindChapterField("tense")!.MatchEnum("Aorist"));
        Assert.Equal([("packs/starfall/chapter.yaml", "F-CHAP-2", DiagnosticSeverity.Warning, (int?)2)], Rules(den));
    }

    [Fact]
    public void ExtensionsOnlyAdd()
    {
        var den = DenStructure.Load(Den("starfall",
            ("packs/starfall/pack.yaml", "name: Starfall\n"),
            ("packs/starfall/extensions/location.yaml", "name: Place\nfields:\n  climate: text\n"),
            ("packs/starfall/extensions/planet.yaml", "fields:\n  gravity: number\n")));

        // F-TYPE-6: an extension can't rename a type, so the file is excluded.
        // F-TYPE-7: an extension of a type that isn't enabled is ignored.
        Assert.Equal(
            [
                ("packs/starfall/extensions/location.yaml", "F-TYPE-6", DiagnosticSeverity.Error, (int?)1),
                ("packs/starfall/extensions/planet.yaml", "F-TYPE-7", DiagnosticSeverity.Warning, null),
            ],
            Rules(den));
        Assert.Null(den.Types.Find("location")!.FindField("climate"));
        Assert.Equal("Location", den.Types.Find("location")!.Name);
        Assert.Null(den.Types.Find("planet"));
        Assert.True(den.IsLoaded);
    }

    [Fact]
    public void ABuiltInExtensionNeverReplacesADenLocalDefinition()
    {
        // A second built-in pack, as a later milestone might ship (M5).
        var builtIn = BuiltInPacks.FromFiles(new Dictionary<string, string>
        {
            ["core/pack.yaml"] = "name: Core\n",
            ["core/types/character.yaml"] = "name: Character\nplural: characters\nfields:\n  born: date\n",
            ["space/pack.yaml"] = "name: Space opera\n",
            ["space/types/ship.yaml"] = "name: Ship\nplural: ships\nfields:\n  class: text\n",
            ["space/extensions/ship.yaml"] = "fields:\n  hull: text\n  captain: text\ntemplate: ship.md\n",
            ["space/extensions/character.yaml"] = "fields:\n  rank: text\n",
            ["space/templates/ship.md"] = "# {{name}}\n",
        });
        var source = Den("space, starfall",
            ("packs/starfall/pack.yaml", "name: Starfall\n"),
            ("packs/starfall/types/ship.yaml", Ship),
            ("packs/starfall/extensions/character.yaml", "fields:\n  rank: string\n"));

        var types = DenTypes.Load(source, ["space", "starfall"], builtIn);

        Assert.Equal(["core", "space", "starfall"], types.Packs.Select(p => p.Key));
        var ship = types.Find("ship")!;
        Assert.Equal("starfall", ship.Pack.Key);
        // The built-in extension still adds hull, but not its captain (the den's
        // own wins), and not its template, since the type is den-local.
        Assert.Equal(["class", "captain", "condition", "hull"], ship.Fields.Select(f => f.Name));
        Assert.Equal(FieldKind.Ref, ship.FindField("captain")!.Kind);
        Assert.True(ship.Template.IsGenerated);
        Assert.Equal(FieldKind.String, types.Find("character")!.FindField("rank")!.Kind);
        Assert.Equal(
            [
                ("packs/starfall/extensions/character.yaml", "F-TYPE-8"),
                ("packs/starfall/types/ship.yaml", "F-TYPE-4"),
                ("packs/starfall/types/ship.yaml", "F-TYPE-8"),
                ("packs/starfall/types/ship.yaml", "F-TYPE-8"),
            ],
            types.Diagnostics.Select(d => (d.Path, d.Diagnostic.RuleId)).OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.RuleId, StringComparer.Ordinal));
    }

    [Fact]
    public void TwoBuiltInPacksClashByPackOrder()
    {
        var builtIn = BuiltInPacks.FromFiles(new Dictionary<string, string>
        {
            ["core/pack.yaml"] = "name: Core\n",
            ["alpha/pack.yaml"] = "name: Alpha\n",
            ["alpha/types/ship.yaml"] = "name: Alpha ship\nplural: ships\n",
            ["beta/pack.yaml"] = "name: Beta\n",
            ["beta/types/ship.yaml"] = "name: Beta ship\nplural: ships\n",
        });

        var types = DenTypes.Load(Den("beta, alpha"), ["beta", "alpha"], builtIn);

        Assert.Equal("Beta ship", types.Find("ship")!.Name);
        var clash = Assert.Single(types.Diagnostics);
        Assert.Equal(("builtin:packs/alpha/types/ship.yaml", "F-TYPE-4"), (clash.Path, clash.Diagnostic.RuleId));
    }

    [Fact]
    public void APackThatShadowsCoreReplacesIt()
    {
        var den = DenStructure.Load(Den("",
            ("packs/core/pack.yaml", "name: My core\n"),
            ("packs/core/types/character.yaml", "name: Person\nplural: people\nsections: [Overview]\n")));

        Assert.Equal([("packs/core/pack.yaml", "F-PACK-10", DiagnosticSeverity.Warning, (int?)null)], Rules(den));
        Assert.Equal(["character"], den.Types.Types.Select(t => t.Key));
        Assert.Empty(den.Types.ChapterFields);
        var pack = Assert.Single(den.Types.Packs);
        Assert.Equal((false, true), (pack.IsBuiltIn, pack.Shadows));
    }

    [Fact]
    public void AShadowWithABrokenManifestLeavesNoCore()
    {
        // F-PACK-10: an error in pack.yaml never switches the den back to the built-in pack.
        var den = DenStructure.Load(Den("",
            ("packs/core/pack.yaml", "description: no name\n"),
            ("packs/core/types/character.yaml", "name: Person\nplural: people\n")));

        Assert.True(den.IsLoaded);
        Assert.Empty(den.Types.Packs);
        Assert.Empty(den.Types.Types);
        Assert.Contains(den.Diagnostics, d => d.Diagnostic.RuleId == "F-PACK-6" && d.Diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AnErrorInPackYamlExcludesThePack()
    {
        var den = DenStructure.Load(Den("starfall",
            ("packs/starfall/pack.yaml", "name: [Starfall]\n"),
            ("packs/starfall/types/ship.yaml", Ship)));

        Assert.True(den.IsLoaded);
        Assert.Null(den.Types.Find("ship"));
        Assert.Equal(["core"], den.Types.Packs.Select(p => p.Key));
        Assert.Equal([("packs/starfall/pack.yaml", "F-PACK-6", DiagnosticSeverity.Error, (int?)1)], Rules(den));
    }

    [Fact]
    public void FoldersThatArentPacksAreWarnings()
    {
        var den = DenStructure.Load(Den("",
            ("packs/drafts/notes.txt", "x"),
            ("packs/old/pack.yml", "name: Old\n"),
            ("packs/My Stuff/pack.yaml", "name: Mine\n"),
            ("packs/spare/pack.yaml", "name: Spare\n"),
            ("packs/.hidden/pack.yaml", "name: Hidden\n"),
            ("packs/README.md", "Packs live here.\n")));

        Assert.Equal(
            [
                ("packs/My Stuff", "F-PACK-5", DiagnosticSeverity.Warning, (int?)null),
                ("packs/drafts", "F-PACK-5", DiagnosticSeverity.Warning, null),
                ("packs/old", "F-PACK-5", DiagnosticSeverity.Warning, null),
                ("packs/spare/pack.yaml", "F-PACK-8", DiagnosticSeverity.Warning, null),
            ],
            Rules(den));
        Assert.Contains("rename it to pack.yaml", den.Diagnostics[2].Diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(["core"], den.Types.Packs.Select(p => p.Key));
    }

    [Theory]
    [InlineData("packs: starfall", "packs must be a list")]
    [InlineData("packs: [nowhere]", "no built-in pack 'nowhere'")]
    [InlineData("packs: [old]", "rename it to pack.yaml")]
    [InlineData("packs: [starfall, starfall]", "listed more than once")]
    [InlineData("packs: [[starfall]]", "must be a pack key")]
    public void ABadPacksListKeepsTheDenFromLoading(string packs, string message)
    {
        var source = Den("", ("packs/starfall/pack.yaml", "name: Starfall\n"), ("packs/old/pack.yml", "name: Old\n"))
            .With("den.yaml", $"format: 1\n{packs}\n");

        var den = DenStructure.Load(source);

        Assert.False(den.IsLoaded);
        var error = Assert.Single(den.Diagnostics, d => d.Path == "den.yaml");
        Assert.Equal(("F-DEN-4", DiagnosticSeverity.Error), (error.Diagnostic.RuleId, error.Diagnostic.Severity));
        Assert.Contains(message, error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListingCoreChangesNothing()
    {
        var den = DenStructure.Load(Den("core"));

        Assert.Empty(den.Diagnostics);
        Assert.Equal(["core"], den.Types.Packs.Select(p => p.Key));
    }

    [Fact]
    public void YmlFilesAreWarningsAndArentRead()
    {
        var den = DenStructure.Load(Den("starfall",
            ("packs/starfall/pack.yaml", "name: Starfall\n"),
            ("packs/starfall/chapter.yml", "fields:\n  weather: text\n"),
            ("packs/starfall/types/ship.yml", Ship)));

        Assert.Equal(
            [
                ("packs/starfall/chapter.yml", "F-PACK-3", DiagnosticSeverity.Warning, (int?)null),
                ("packs/starfall/types/ship.yml", "F-PACK-3", DiagnosticSeverity.Warning, null),
            ],
            Rules(den));
        Assert.Contains("did you mean ship.yaml?", den.Diagnostics[1].Diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(den.Types.Find("ship"));
        Assert.Null(den.Types.FindChapterField("weather"));
    }
}
