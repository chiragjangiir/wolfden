using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;
using static Wolfden.Core.Tests.LinkDens;

namespace Wolfden.Core.Tests;

/// <summary>
/// What loaded types change: standard sections hand over (format.md
/// F-SEC-8), <c>ref</c> fields are type-checked (F-LINK-11), a thread is
/// the core thread by its definition (types.md F-CORE-4), and chapter
/// fields are checked and inherited (F-CHAP-3, F-CHAP-5).
/// </summary>
public class TypeEffectsTests
{
    private const string Path = LongDark + "/entities/characters/mara-voss.md";

    private static EntityFacts Facts(string body, string extra = "", MemoryDenSource? source = null, string type = "character")
    {
        source ??= Den();
        source.Entity(LongDark, "mara-voss", $"type: {type}\nname: Mara Voss\nstatus: canon\nfrom: \"ember:1\"{extra}", body);
        var catalog = EntityCatalog.Load(source);
        return EntityFacts.For(catalog.FindByPath(Path)!, new Bookkeeping());
    }

    private static StoryPoint P(EntityFacts facts, string position) =>
        facts.Entity.Universe.Resolve(StoryPosition.Parse(position)).Resolved!.Point;

    private static (string Name, StoryPoint? From, StoryPoint? Until)[] Ranges(EntityFacts facts) =>
        facts.Sections.Select(s => (s.Section.Name, s.EffectiveFrom?.Point, s.EffectiveUntil)).ToArray();

    [Fact]
    public void StandardSectionsHandOverToTheNextOneWithTheSameName()
    {
        // format.md F-SEC-8's own example.
        var facts = Facts("""
            ## Appearance
            Silver-streaked hair.

            ## Appearance
            <!-- wolfden: from=ember:2 -->
            Her left arm in a brace.

            ### The brace
            Grey webbing.

            ## Appearance
            <!-- wolfden: from=ash:2 -->
            The brace is gone.
            """);
        var end = facts.Entity.Universe.EndOfStory;

        Assert.Equal(
            [
                ("Appearance", P(facts, "ember:1"), P(facts, "ember:2")),
                ("Appearance", P(facts, "ember:2"), P(facts, "ash:2")),
                ("The brace", P(facts, "ember:2"), P(facts, "ash:2")),
                ("Appearance", P(facts, "ash:2"), end),
            ],
            Ranges(facts));
        Assert.Equal([true, true, false, true], facts.Sections.Select(s => s.IsStandard));
    }

    [Fact]
    public void BookkeepingSectionsDontEndAnEarlierOne()
    {
        // format.md F-SEC-8: a bookkeeping section isn't live, since canon views leave it out.
        var facts = Facts("## Appearance\nFirst.\n\n## Appearance\n<!-- wolfden: from=ember:2 canon=false -->\nScratch.\n");
        var end = facts.Entity.Universe.EndOfStory;

        Assert.Equal([end, end], facts.Sections.Select(s => s.EffectiveUntil!.Value));
        Assert.Equal([true, false], facts.Sections.Select(s => s.IsLive));
    }

    [Fact]
    public void ASubsectionNeverInEffectIsntLive()
    {
        var facts = Facts("## Appearance\nOne.\n\n### Later\n<!-- wolfden: from=ash:1 -->\nAfter the handover.\n\n## Appearance\n<!-- wolfden: from=ember:2 -->\nTwo.\n");

        var sub = facts.Sections[1];
        Assert.Equal((P(facts, "ash:1"), P(facts, "ember:2")), (sub.EffectiveFrom!.Point, sub.EffectiveUntil!.Value));
        Assert.False(sub.IsLive);
        Assert.True(facts.Sections[0].IsLive);
    }

    [Fact]
    public void CustomSectionsDontHandOver()
    {
        var facts = Facts("## Session log\nOne.\n\n## Session log\n<!-- wolfden: from=ember:2 -->\nTwo.\n");
        var end = facts.Entity.Universe.EndOfStory;

        Assert.All(facts.Sections, s => Assert.Equal(end, s.EffectiveUntil));
        Assert.All(facts.Sections, s => Assert.False(s.IsStandard));
    }

    [Fact]
    public void OnlyLiveSectionsHandOver()
    {
        var facts = Facts("""
            ## Appearance
            Plain.

            ## Appearance
            <!-- wolfden: from=ember:2 status=candidate -->
            An option.

            ## Appearance
            <!-- wolfden: from=ash:99 -->
            Doesn't resolve.

            ## Appearance
            <!-- wolfden: from=ash:1 until=ash:1 -->
            Empty range.

            ## Appearance
            <!-- wolfden: from=ash:2 -->
            Later.
            """);
        var end = facts.Entity.Universe.EndOfStory;

        Assert.Equal(P(facts, "ash:2"), facts.Sections[0].EffectiveUntil);
        Assert.Equal(end, facts.Sections[1].EffectiveUntil);
        Assert.False(facts.Sections[1].IsLive);
        Assert.Null(facts.Sections[2].EffectiveUntil);
        Assert.False(facts.Sections[3].IsLive);
        Assert.Equal(end, facts.Sections[4].EffectiveUntil);
    }

    [Fact]
    public void AnExplicitUntilWinsAndSharedFromsOverlap()
    {
        var facts = Facts("""
            ## Appearance
            <!-- wolfden: until=ash:3 -->
            Runs past the next one.

            ## Appearance
            <!-- wolfden: from=ash:1 -->
            Shares a from.

            ## appearance
            <!-- wolfden: from=ash:1 -->
            Shares it too.
            """);
        var end = facts.Entity.Universe.EndOfStory;

        Assert.Equal([P(facts, "ash:3"), end, end], facts.Sections.Select(s => s.EffectiveUntil!.Value));
    }

    [Fact]
    public void UntypedEntitiesHaveNoStandardSections()
    {
        var facts = Facts("## Appearance\nOne.\n\n## Appearance\n<!-- wolfden: from=ember:2 -->\nTwo.\n", type: "charcter");

        Assert.Null(facts.Entity.Definition);
        Assert.All(facts.Sections, s => Assert.Equal(facts.Entity.Universe.EndOfStory, s.EffectiveUntil));
    }

    [Fact]
    public void ADenLocalExtensionSectionStartsHandingOver()
    {
        // types.md §2.4: a section name an extension adds makes repeats of it hand over.
        var source = Den(denYaml: "packs: [starfall]\n")
            .With("packs/starfall/pack.yaml", "name: Starfall\n")
            .With("packs/starfall/extensions/character.yaml", "sections: [Service record]\n");
        var facts = Facts("## Service record\nEnsign.\n\n## Service record\n<!-- wolfden: from=ash:1 -->\nCommander.\n", source: source);

        Assert.Equal(P(facts, "ash:1"), facts.Sections[0].EffectiveUntil);
    }

    [Fact]
    public void APayoffHandsOverFromPaidOff()
    {
        var facts = Facts(
            "## Payoff\nFirst telling.\n\n## Payoff\n<!-- wolfden: from=a-quiet-moon:2 -->\nRetold.\n",
            "\nfields:\n  paid_off: \"ash:2\"",
            type: "thread");

        Assert.Equal((P(facts, "ash:2"), P(facts, "a-quiet-moon:2")), (facts.Sections[0].EffectiveFrom!.Point, facts.Sections[0].EffectiveUntil!.Value));
    }

    [Fact]
    public void ADenLocalThreadDoesntTakePaidOff()
    {
        // types.md F-CORE-4: only the built-in core thread's Payoff takes paid_off as its from.
        var source = Den(denYaml: "packs: [starfall]\n")
            .With("packs/starfall/pack.yaml", "name: Starfall\n")
            .With("packs/starfall/types/thread.yaml", "name: Thread\nplural: threads\nfields:\n  paid_off: position\nsections: [Payoff]\n");
        var facts = Facts("## Payoff\nSold.\n", "\nfields:\n  paid_off: \"ash:2\"", source, type: "thread");

        Assert.False(facts.Entity.Definition!.IsCoreThread);
        Assert.Equal(P(facts, "ember:1"), facts.Sections[0].EffectiveFrom!.Point);
    }

    [Fact]
    public void RefFieldsAreTypeChecked()
    {
        var source = Den()
            .Entity(Starfall, "reach", "type: location\nname: Reach\nstatus: canon")
            .Entity(Starfall, "compact", "type: faction\nname: Compact\nstatus: canon", folder: "factions")
            .Entity(Starfall, "lamp", "type: item\nname: Lamp\nstatus: canon", folder: "items")
            .Entity(Starfall, "stranger", "type: alien\nname: Stranger\nstatus: canon")
            .Entity(Starfall, "voss", Character("Voss", """
                fields:
                  factions: ["[[compact]]", "[[reach|wrong plain]]"]
                  heritage: "[[reach|not a ref]]"
                  rank: "[[reach|undefined field]]"
                """))
            .Entity(Starfall, "lamp-owner", "type: item\nname: Lamp owner\nstatus: canon\nfields:\n  owner:\n    - { value: \"[[compact]]\" }\n    - { value: \"[[stranger|wrong entry]]\", from: \"ash:1\" }", folder: "items")
            .Entity(Starfall, "musing", "type: note\nname: Musing\nstatus: canon\nfields:\n  about: [\"[[lamp]]\", \"[[reach]]\"]", folder: "notes")
            .Entity(Starfall, "odd", "type: alien\nname: Odd\nstatus: canon\nfields:\n  factions: \"[[reach|untyped holder]]\"");

        var index = LinkIndex.Load(source);

        Assert.Equal(
            [
                (Starfall + "/entities/characters/voss.md", "F-LINK-11", (int?)6),
                (Starfall + "/entities/items/lamp-owner.md", "F-LINK-11", 8),
            ],
            index.Rules());
        var message = index.Diagnostics[0].Diagnostic.Message;
        Assert.Equal("[[reach|wrong plain]]: factions links to 'Reach', whose type is location, but factions is a ref to faction.", message);
    }

    [Fact]
    public void ChapterFieldRefsAreTypeChecked()
    {
        var source = Den()
            .Entity(Starfall, "reach", "type: location\nname: Reach\nstatus: canon")
            .Entity(Starfall, "voss", Character("Voss"))
            .With($"{Ember}/book.md", """
                ---
                name: Ember
                chapter_defaults: { pov: "[[reach|default pov]]" }
                chapters:
                  - { key: "1", fields: { pov: ["[[voss]]", "[[reach|own pov]]"] } }
                  - { key: "2", fields: { pov: { value: "[[reach|ignored]]" } } }
                ---
                """);

        var index = LinkIndex.Load(source);

        Assert.Equal([3, 5], index.Diagnostics.Where(d => d.Diagnostic.RuleId == "F-LINK-11").Select(d => d.Diagnostic.Line!.Value));
    }

    [Fact]
    public void ToNamingATypeThatIsntEnabledSkipsIt()
    {
        var source = Den(denYaml: "packs: [starfall]\n")
            .With("packs/starfall/pack.yaml", "name: Starfall\n")
            .With("packs/starfall/extensions/character.yaml", "fields:\n  posting: { type: ref, to: [ship, location] }\n  ride: { type: ref, to: starship }\n")
            .Entity(Starfall, "reach", "type: location\nname: Reach\nstatus: canon")
            .Entity(Starfall, "compact", "type: faction\nname: Compact\nstatus: canon", folder: "factions")
            .Entity(Starfall, "voss", Character("Voss", "fields:\n  posting: \"[[compact]]\"\n  ride: \"[[compact|any ride]]\""));

        var index = LinkIndex.Load(source);

        Assert.Equal(
            [
                ("packs/starfall/extensions/character.yaml", "F-FT-9", (int?)2),
                ("packs/starfall/extensions/character.yaml", "F-FT-9", 3),
            ],
            index.Catalog.Structure.Diagnostics.Select(d => (d.Path, d.Diagnostic.RuleId, d.Diagnostic.Line)));
        // posting still checks location; ride checks nothing.
        var warning = Assert.Single(index.Diagnostics);
        Assert.Contains("posting is a ref to location.", warning.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChapterDefaultsInheritFieldByField()
    {
        var source = Den()
            .Entity(Starfall, "voss", Character("Voss"))
            .Entity(Starfall, "pike", Character("Pike"))
            .With($"{Starfall}/universe.md", "---\nname: Starfall\norder: [long-dark, a-quiet-moon]\nchapter_defaults: { tense: past, narration: third-omniscient }\n---\n")
            .With($"{LongDark}/series.md", "---\nname: The Long Dark\nbooks: [ember, ash]\nchapter_defaults: { narration: third-limited, pov: \"[[voss]]\" }\n---\n")
            .With($"{Ember}/book.md", """
                ---
                name: Ember
                chapter_defaults: { pov: ["[[pike]]"] }
                chapters:
                  - { key: prologue, fields: { pov: [], narration: Third-Omniscient } }
                  - { key: "1", fields: { tense: present } }
                  - { key: "2", fields: { pov: ~ } }
                ---
                """);

        var den = DenStructure.Load(source);
        Assert.Empty(den.Diagnostics);
        var universe = den.FindUniverse("starfall")!;
        var ember = universe.FindBook("ember")!;
        var ash = universe.FindBook("ash")!;
        var moon = universe.FindBook("a-quiet-moon")!;
        string? Text(Chapter c, string field) => c.Fields.TryGetValue(field, out var v) ? v.IsNone ? "none" : string.Join(",", v.Items) : null;

        var prologue = ember.FindChapter("prologue")!;
        Assert.Equal(("none", "Third-Omniscient", "past"), (Text(prologue, "pov"), Text(prologue, "narration"), Text(prologue, "tense")));
        Assert.Null(prologue.Fields["pov"].DefaultFrom);

        var one = ember.FindChapter("1")!;
        Assert.Equal(("[[pike]]", "third-limited", "present"), (Text(one, "pov"), Text(one, "narration"), Text(one, "tense")));
        Assert.Equal(ember, one.Fields["pov"].DefaultFrom);
        Assert.Equal(den.FindUniverse("starfall")!.Series.Single(), one.Fields["narration"].DefaultFrom);

        // A null own value inherits.
        Assert.Equal("[[pike]]", Text(ember.FindChapter("2")!, "pov"));
        Assert.Equal(("[[voss]]", "third-limited"), (Text(ash.FindChapter("1")!, "pov"), Text(ash.FindChapter("1")!, "narration")));
        Assert.Equal(("past", "third-omniscient", null), (Text(moon.FindChapter("1")!, "tense"), Text(moon.FindChapter("1")!, "narration"), Text(moon.FindChapter("1")!, "pov")));
    }

    [Fact]
    public void AnEmptyListInChapterDefaultsStopsInheritance()
    {
        var source = Den()
            .Entity(Starfall, "voss", Character("Voss"))
            .With($"{LongDark}/series.md", "---\nname: The Long Dark\nbooks: [ember, ash]\nchapter_defaults: { pov: \"[[voss]]\" }\n---\n")
            .With($"{Ember}/book.md", "---\nname: Ember\nchapter_defaults: { pov: [] }\nchapters: [1]\n---\n");

        var chapter = DenStructure.Load(source).FindUniverse("starfall")!.FindBook("ember")!.FindChapter("1")!;

        Assert.True(chapter.Fields["pov"].IsNone);
    }

    [Fact]
    public void ChapterValuesThatDontMatchAreIgnoredWithAWarning()
    {
        var source = Den()
            .Entity(Starfall, "voss", Character("Voss"))
            .With($"{LongDark}/series.md", "---\nname: The Long Dark\nbooks: [ember, ash]\nchapter_defaults: { tense: past, pov: \"[[voss]]\", narration: third_limited }\n---\n")
            .With($"{Ember}/book.md", """
                ---
                name: Ember
                chapters:
                  - { key: "1", fields: { tense: futur } }
                  - { key: "2", fields: { tense: [] } }
                  - { key: "3", fields: { pov: ["[[voss]]", "Voss"] } }
                  - { key: "4", fields: { pov: ["Pike"] } }
                  - { key: "5", fields: { weather: rain } }
                  - { key: "6", fields: [tense] }
                  - { key: "7", fields: { when: { value: "2291" } } }
                  - { key: "8", fields: { when: { text: spring, sort: [2291, 4] } } }
                ---
                """);

        var den = DenStructure.Load(source);
        var ember = den.FindUniverse("starfall")!.FindBook("ember")!;
        string? Text(string chapter, string field) => ember.FindChapter(chapter)!.Fields.TryGetValue(field, out var v) ? string.Join(",", v.Items) : null;

        Assert.Equal(("past", "past"), (Text("1", "tense"), Text("2", "tense")));
        Assert.Equal("[[voss]]", Text("3", "pov"));
        Assert.Same(ember.FindChapter("3")!.Fields["pov"], ember.FindChapter("3")!.OwnFields["pov"]);
        // A list left with no items counts as not set, so chapter 4 inherits.
        Assert.Equal("[[voss]]", Text("4", "pov"));
        Assert.Equal(ember.Series, ember.FindChapter("4")!.Fields["pov"].DefaultFrom);
        Assert.Equal("rain", Text("5", "weather"));
        Assert.Null(ember.FindChapter("5")!.Fields["weather"].Definition);
        Assert.Null(Text("7", "when"));
        Assert.NotNull(Text("8", "when"));
        Assert.Null(Text("1", "narration"));

        Assert.Equal(
            [
                ($"{Ember}/book.md", (int?)4),
                ($"{Ember}/book.md", 5),
                ($"{Ember}/book.md", 6),
                ($"{Ember}/book.md", 7),
                ($"{Ember}/book.md", 8),
                ($"{Ember}/book.md", 9),
                ($"{Ember}/book.md", 10),
                ($"{LongDark}/series.md", 4),
            ],
            den.Diagnostics.Select(d => (d.Path, d.Diagnostic.Line)));
        Assert.All(den.Diagnostics, d => Assert.Equal("F-CHAP-3", d.Diagnostic.RuleId));
        Assert.Contains("Did you mean 'third-limited'?", den.Diagnostics[^1].Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADateWithACalendarStaysValid()
    {
        // types.md F-FT-11: calendar and unknown keys are advisory warnings, so
        // the chapter keeps its own when rather than inheriting the default.
        var source = Den()
            .With($"{Ember}/book.md", """
                ---
                name: Ember
                chapter_defaults: { when: "2290" }
                chapters:
                  - { key: "1", fields: { when: { text: late spring, sort: [2291, 5], calendar: reach, era: second } } }
                ---
                """);

        var den = DenStructure.Load(source);
        var when = den.FindUniverse("starfall")!.FindBook("ember")!.FindChapter("1")!.Fields["when"];

        Assert.Null(when.DefaultFrom);
        Assert.Equal(new[] { 2291L, 5 }, Types.FieldValues.ReadDate(when.Value)!.SortKey);
        Assert.Equal(2, den.Diagnostics.Count);
        Assert.All(den.Diagnostics, d => Assert.Equal(("F-CHAP-3", 5), (d.Diagnostic.RuleId, d.Diagnostic.Line!.Value)));
        Assert.Contains("Custom calendars aren't supported yet", den.Diagnostics[0].Diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(den.Diagnostics, d => d.Diagnostic.Message.Contains("ignored, so the chapter inherits", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnquotedPovGetsTheQuotingHint()
    {
        var source = Den()
            .Entity(Starfall, "pike", Character("Pike"))
            .With($"{Ember}/book.md", "---\nname: Ember\nchapters:\n  - { key: \"1\", fields: { pov: [[pike]] } }\n---\n");

        var warning = Assert.Single(DenStructure.Load(source).Diagnostics);

        Assert.Contains("write \"[[pike]]\" (F-FLD-6)", warning.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinksInIgnoredChapterValuesHaveNoPosition()
    {
        var source = Den()
            .Entity(LongDark, "late", Character("Late", "from: \"ash:2\""))
            .With($"{Ember}/book.md", """
                ---
                name: Ember
                chapter_defaults: { tense: "[[late|enum default]]" }
                chapters:
                  - { key: "1", fields: { pov: ["[[late|kept]]", "[[late|dropped]] and more"] } }
                ---
                """);

        var index = LinkIndex.Load(source);
        LinkOccurrence At(string label) => index.Links.Single(l => l.Occurrence.Link.Label == label).Occurrence;

        Assert.True(At("enum default").IsIgnoredValue);
        Assert.True(At("dropped").IsIgnoredValue);
        Assert.False(At("kept").IsIgnoredValue);
        Assert.Equal(["[[late|kept]]"], index.Diagnostics.Where(d => d.Diagnostic.RuleId == "F-LINK-13").Select(d => d.Diagnostic.Message[..13]));
    }
}
