using Wolfden.Core.Types;

namespace Wolfden.Core.Tests;

/// <summary>
/// The built-in packs are embedded from <c>packs/</c> at build time
/// (types.md F-PACK-4), and the core pack loads cleanly with the types,
/// fields, sections, chapter fields, and templates types.md §9 lists.
/// </summary>
public class BuiltInPacksTests
{
    private static DenTypes Core => DenTypes.BuiltIn;

    [Fact]
    public void TheEmbeddedPacksMatchTheFilesOnDisk()
    {
        var packs = Path.Combine(TestFiles.RepoRoot, "packs");
        var onDisk = Directory.EnumerateDirectories(packs)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(packs, f).Replace('\\', '/'))
            .Where(f => !f.Split('/').Any(part => part.StartsWith('.')))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(onDisk, BuiltInPacks.Instance.Files);
        Assert.All(onDisk, f => Assert.Equal(File.ReadAllBytes(Path.Combine(packs, f)), BuiltInPacks.Instance.ReadFile(f)));
        Assert.Equal(["core"], BuiltInPacks.Instance.Keys);
    }

    [Fact]
    public void TheCorePackLoadsWithNoDiagnostics()
    {
        Assert.Empty(Core.Diagnostics);
        var pack = Assert.Single(Core.Packs);
        Assert.Equal(("core", "Core", true), (pack.Key, pack.Name, pack.IsBuiltIn));
        Assert.Equal("builtin:packs/core", pack.Folder);
    }

    [Fact]
    public void TheCorePackDefinesTheEightTypes()
    {
        Assert.Equal(["character", "event", "faction", "idea", "item", "location", "note", "thread"], Core.Types.Select(t => t.Key));
        Assert.Equal(["characters", "events", "factions", "ideas", "items", "locations", "notes", "threads"], Core.Types.Select(t => t.Plural));
        Assert.All(Core.Types, t => Assert.Equal($"builtin:packs/core/types/{t.Key}.yaml", t.Path));
    }

    [Fact]
    public void CharacterFieldsAndSectionsAreInOrder()
    {
        var character = Core.Find("character")!;

        Assert.Equal(["role", "gender", "pronouns", "occupation", "skills", "heritage", "born", "died", "species", "factions"],
            character.Fields.Select(f => f.Name));
        Assert.Equal(["Overview", "Appearance", "Backstory", "Want", "Wound", "Voice", "Relationships", "Arc", "Open questions"],
            character.Sections.Select(s => s.Name));
        Assert.Equal(["role", "gender", "pronouns", "occupation", "skills", "heritage", "factions"],
            character.Fields.Where(f => f.IsTemporal).Select(f => f.Name));

        var factions = character.FindField("factions")!;
        Assert.Equal((FieldKind.Ref, true), (factions.Kind, factions.IsList));
        Assert.Equal(["faction"], factions.To);
        Assert.Equal(["faction"], factions.CheckedTo);
        Assert.Equal(FieldKind.Date, character.FindField("born")!.Kind);
        Assert.Null(character.FindField("rank"));
    }

    [Fact]
    public void SectionsMatchUnderNormalization()
    {
        var character = Core.Find("character")!;

        Assert.True(character.IsStandardSection("appearance"));
        Assert.True(character.IsStandardSection("  Open   QUESTIONS "));
        Assert.False(character.IsStandardSection("Shoulder injury"));
    }

    [Fact]
    public void RefTargetsAreAsTypesMdSays()
    {
        Assert.Equal(["character", "faction"], Core.Find("item")!.FindField("owner")!.To);
        Assert.Equal(["character", "faction"], Core.Find("event")!.FindField("participants")!.To);
        Assert.Null(Core.Find("note")!.FindField("about")!.To);
        Assert.True(Core.Find("note")!.FindField("about")!.Accepts("anything"));
        Assert.False(Core.Find("faction")!.FindField("leader")!.Accepts("location"));
    }

    [Fact]
    public void TheThreadIsTheCoreThread()
    {
        var thread = Core.Find("thread")!;

        Assert.True(thread.IsCoreThread);
        Assert.Equal(["paid_off", "planted"], thread.PositionFields.Order(StringComparer.Ordinal));
        Assert.False(Core.Find("character")!.IsCoreThread);
    }

    [Fact]
    public void TheCoreChapterFields()
    {
        Assert.Equal(["pov", "narration", "tense", "when"], Core.ChapterFields.Select(f => f.Name));
        Assert.Equal(["first", "second", "third-limited", "third-omniscient", "third-objective"], Core.FindChapterField("narration")!.Values);
        Assert.Equal(["past", "present", "future"], Core.FindChapterField("tense")!.Values);
        var pov = Core.FindChapterField("pov")!;
        Assert.Equal((FieldKind.Ref, true, false), (pov.Kind, pov.IsList, pov.IsTemporal));
    }

    [Fact]
    public void EveryCoreTypeHasItsOwnTemplate()
    {
        Assert.All(Core.Types, t =>
        {
            Assert.False(t.Template.IsGenerated);
            Assert.Equal($"{t.Key}.md", t.Template.FileName);
            Assert.Contains("# {{name}}", t.Template.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheCorePackHasNoRequiredFieldsOrSections()
    {
        // F-CORE-1: a hand-written note never gets a "missing" warning from core.
        Assert.All(Core.Types.SelectMany(t => t.Fields), f => Assert.False(f.IsRequired));
        Assert.All(Core.Types.SelectMany(t => t.Sections), s => Assert.False(s.IsRequired));
    }

    [Theory]
    [InlineData("past", "past")]
    [InlineData("Past", "past")]
    [InlineData("PAST", "past")]
    [InlineData("pAsT", "past")]
    [InlineData(" past", null)]
    [InlineData("passed", null)]
    public void EnumValuesMatchCaseInsensitively(string written, string? expected)
    {
        Assert.Equal(expected, Core.FindChapterField("tense")!.MatchEnum(written));
    }
}
