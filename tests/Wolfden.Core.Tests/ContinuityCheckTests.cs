using Wolfden.Core.Checks;
using Wolfden.Core.Documents;

namespace Wolfden.Core.Tests;

/// <summary>
/// <c>wolfden check</c>'s rules (design.md §7), through <see cref="DenCheck"/>:
/// the diagnostics loading already reports (links, ids, spoilers, errors),
/// and the continuity checks only <c>check</c> runs (F-ENT-2, F-FLD-3,
/// F-SEC-5, F-POS-4, F-TMP-3, F-TMP-5, F-SEC-8, F-SEC-4). The den uses the
/// keys of the scenarios' example world (docs/scenarios.md): universe
/// <c>vael</c>, series <c>a</c> with books <c>a1</c> to <c>a5</c>, and the
/// spin-off series <c>b</c> with <c>b1</c>.
/// </summary>
public class ContinuityCheckTests
{
    private const string A = "universes/vael/series/a";
    private const string A1 = A + "/books/a1";
    private const string A2 = A + "/books/a2";
    private const string A3 = A + "/books/a3";
    private const string A4 = A + "/books/a4";
    private const string Copy = "01JC5CHEK00000000000000001";

    /// <summary>The example world, every book with chapters 1 to 24, and no entities yet.</summary>
    internal static MemoryDenSource Vael(bool spoilerChecks = false, string denYaml = "")
    {
        var chapters = "chapters: [" + string.Join(", ", Enumerable.Range(1, 24)) + "]";
        var source = new MemoryDenSource()
            .With("den.yaml", $"format: 1\nspoiler_checks: {(spoilerChecks ? "true" : "false")}\n{denYaml}")
            .Universe("vael", "order: [a, b]")
            .Series("vael", "a", "books: [a1, a2, a3, a4, a5]")
            .Series("vael", "b", "books: [b1]");
        foreach (var book in new[] { "a1", "a2", "a3", "a4", "a5" })
        {
            source.Book("vael", "a", book, chapters);
        }

        return source.Book("vael", "b", "b1", chapters);
    }

    /// <summary>Adds an entity with an id (so F-ID-2 stays quiet) at <c>{container}/entities/{folder}/{slug}.md</c>.</summary>
    private static MemoryDenSource Note(MemoryDenSource source, string container, string slug, string frontmatter, string body = "",
        string folder = "characters", string? id = null)
    {
        id ??= "01JC5CHEK" + ((uint)StringComparer.Ordinal.GetHashCode($"{container}/{slug}") % 100_000_000).ToString("D17", System.Globalization.CultureInfo.InvariantCulture);
        return source.With($"{container}/entities/{folder}/{slug}.md", $"---\nid: {id}\n{frontmatter}\n---\n{body}");
    }

    private static string Character(string name, params string[] extra) =>
        string.Join("\n", new[] { "type: character", $"name: {name}", "status: canon" }.Concat(extra));

    /// <summary>The diagnostics as (path, rule, line), without F-ID-2.</summary>
    private static (string Path, string Rule, int? Line)[] Found(DenCheck check) =>
        check.Diagnostics.Where(d => d.Diagnostic.RuleId != "F-ID-2")
            .Select(d => (d.Path, d.Diagnostic.RuleId, d.Diagnostic.Line)).ToArray();

    private static Diagnostic Only(DenCheck check, string rule) =>
        Assert.Single(check.Diagnostics, d => d.Diagnostic.RuleId == rule).Diagnostic;

    [Fact]
    public void TheExampleWorldIsClean()
    {
        var check = DenCheck.Run(Note(Vael(), A1, "orrin-fell", Character("Orrin Fell")));

        Assert.True(check.IsLoaded);
        Assert.Empty(check.Diagnostics);
    }

    [Fact]
    public void TheDraculaSampleHasNoErrorsOrWarnings()
    {
        // samples/dracula turns spoiler checks on, so this runs every rule.
        var check = DenCheck.Run(MemoryDenSource.DraculaRoot);

        Assert.True(check.IsLoaded);
        Assert.True(check.Structure.SpoilerChecks);
        Assert.Empty(check.Diagnostics);
        Assert.Equal(50, check.Catalog.Entities.Count);
    }

    // ----- Links (reused from LinkIndex) -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-1")]
    public void RenamingACharacterWithoutAnAliasBreaksTheOldLinks()
    {
        // The writer renamed mara-voss.md to mara-ellery.md and changed her name.
        var source = Vael();
        Note(source, A1, "mara-ellery", Character("Mara Ellery"));
        Note(source, A2, "the-trial", "type: event\nname: The trial\nstatus: canon", "\n## Overview\n[[mara-voss]] testifies. [[Mara Voss]] lies.\n", folder: "events");

        var check = DenCheck.Run(source);

        Assert.Equal([($"{A2}/entities/events/the-trial.md", "F-LINK-8", 9), ($"{A2}/entities/events/the-trial.md", "F-LINK-8", 9)], Found(check));
        Assert.Equal(0, check.ErrorCount);

        // Keeping the old name as an alias fixes the name link; the slug link still needs rewriting.
        Note(source, A1, "mara-ellery", Character("Mara Ellery", "aliases: [Mara Voss]"));
        var fixedCheck = DenCheck.Run(source);
        var left = Assert.Single(Found(fixedCheck));
        Assert.Equal("F-LINK-8", left.Rule);
        Assert.Contains("[[mara-voss]]", Only(fixedCheck, "F-LINK-8").Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-3")]
    public void ALinkThatMatchesTwoCharactersIsAmbiguous()
    {
        var source = Vael();
        Note(source, A1, "hale", Character("Officer Hale"));
        Note(source, A2, "hale", Character("Officer Hale"));
        Note(source, A3, "the-raid", "type: event\nname: The raid\nstatus: canon", "\n## Overview\n[[Officer Hale]] leads it.\n", folder: "events");

        var check = DenCheck.Run(source);

        Assert.Equal([($"{A3}/entities/events/the-raid.md", "F-LINK-7", 9)], Found(check));
        var message = Only(check, "F-LINK-7").Message;
        Assert.Contains($"{A1}/entities/characters/hale.md", message, StringComparison.Ordinal);
        Assert.Contains($"{A2}/entities/characters/hale.md", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkToAnotherUniverseSaysLinksDontCrossUniverses()
    {
        var source = Vael().Universe("drift");
        Note(source, "universes/drift", "tamsin", Character("Tamsin"));
        Note(source, A1, "orrin-fell", Character("Orrin Fell"), "\n## Overview\nMeets [[Tamsin]].\n");

        var check = DenCheck.Run(source);

        Assert.Contains("links don't cross universes", Only(check, "F-LINK-8").Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Scenario", "SC-CHECK-2")]
    [InlineData(true)]
    [InlineData(false)]
    public void ABookTwoNoteThatNamesABookFourCharacterIsASpoilerOnlyWhenTheDenAsks(bool spoilerChecks)
    {
        var source = Vael(spoilerChecks);
        Note(source, A4, "the-stranger", Character("The Stranger"));
        Note(source, A2, "harbour", "type: location\nname: The harbour\nstatus: canon", "\n## Overview\n[[The Stranger]] waits here.\n", folder: "locations");

        var check = DenCheck.Run(source);

        if (spoilerChecks)
        {
            Assert.Equal([($"{A2}/entities/locations/harbour.md", "F-LINK-13", 9)], Found(check));
            Assert.Contains("enters canon at the start of book 'a4'", Only(check, "F-LINK-13").Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(Found(check));
        }
    }

    // ----- Duplicate ids (reused from EntityCatalog) -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-4")]
    public void ACopiedFileWithTheSameIdIsCaughtOnBothCopies()
    {
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell"), id: Copy);
        Note(source, A1, "orrin-fell-copy", Character("Dessa Fell"), id: Copy);
        Note(source, A1, "the-wreck", "type: event\nname: The wreck\nstatus: canon", "\n## Overview\n[[orrin-fell]] survives. [[orrin-fell-copy]] doesn't.\n", folder: "events");

        var check = DenCheck.Run(source);

        // Links to either copy are broken until one gets a new id.
        Assert.Equal(
        [
            ($"{A1}/entities/characters/orrin-fell-copy.md", "F-ID-3", 2),
            ($"{A1}/entities/characters/orrin-fell.md", "F-ID-3", 2),
            ($"{A1}/entities/events/the-wreck.md", "F-LINK-8", 9),
            ($"{A1}/entities/events/the-wreck.md", "F-LINK-8", 9),
        ], Found(check));
        var broken = check.Diagnostics.Where(d => d.Diagnostic.RuleId == "F-LINK-8").Select(d => d.Diagnostic.Message).ToList();
        Assert.Contains(broken, m => m.StartsWith("[[orrin-fell]]", StringComparison.Ordinal));
        Assert.Contains(broken, m => m.StartsWith("[[orrin-fell-copy]]", StringComparison.Ordinal));
        Assert.Equal(0, check.ErrorCount);
    }

    // ----- Errors -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-9")]
    public void ANoteThatDoesntParseIsAnErrorAndIsLeftOut()
    {
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "from: a1: 3"));

        var check = DenCheck.Run(source);

        var error = Assert.Single(check.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Diagnostic.Severity);
        Assert.Equal("F-YAML-1", error.Diagnostic.RuleId);
        Assert.Equal($"{A1}/entities/characters/orrin-fell.md", error.Path);
        Assert.Equal(1, check.ErrorCount);
        Assert.Empty(check.Catalog.Entities);
    }

    [Fact]
    public void ADenWhoseDenYamlDoesntLoadIsNotLoaded()
    {
        var check = DenCheck.Run(Vael().With("den.yaml", "format: 2\n"));

        Assert.False(check.IsLoaded);
        Assert.Equal("F-DEN-1", Assert.Single(check.Diagnostics).Diagnostic.RuleId);
        Assert.Empty(check.Catalog.Entities);
    }

    // ----- F-ENT-2 -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-8")]
    public void ATypoInATypeIsAnUnknownType()
    {
        var source = Note(Vael(), A1, "orrin-fell", "type: charcter\nname: Orrin Fell\nstatus: canon");
        Note(source, A1, "the-wreck", "type: event\nname: The wreck\nstatus: canon", "\n## Overview\n[[Orrin Fell]] survives.\n", folder: "events");

        var check = DenCheck.Run(source);

        // Only the type is warned: the untyped note loads, and the link to it resolves.
        Assert.Equal([($"{A1}/entities/characters/orrin-fell.md", "F-ENT-2", 3)], Found(check));
        Assert.Contains(check.Links.Links, l => l.Target?.Name == "Orrin Fell");
        Assert.Contains("unknown type 'charcter'", Only(check, "F-ENT-2").Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Warning, Only(check, "F-ENT-2").Severity);
    }

    // ----- F-FLD-3 and F-SEC-5 -----

    private const string Ship = """
        name: Ship
        plural: ships
        fields:
          class: { type: text, required: true }
          crew: { type: number, required: true, temporal: true }
          motto: text
        sections:
          - Overview
          - { name: Crew, required: true }
        """;

    private static MemoryDenSource ShipDen() =>
        Vael(denYaml: "packs: [fleet]\n")
            .With("packs/fleet/pack.yaml", "name: Fleet\n")
            .With("packs/fleet/types/ship.yaml", Ship);

    [Theory]
    [InlineData("packs/fleet/pack.yaml")]
    [InlineData("packs/fleet/types/ship.yaml")]
    public void ATypeWhosePackFileHasAnErrorSaysSoInsteadOfAskingToEnableIt(string broken)
    {
        // types.md F-PACK-2: the pack is enabled, but an error keeps the type from loading.
        var source = ShipDen().With(broken, "name: [Fleet\n");
        Note(source, A1, "kestrel", "type: ship\nname: Kestrel\nstatus: canon", folder: "ships");

        var check = DenCheck.Run(source);

        var message = Only(check, "F-ENT-2").Message;
        Assert.StartsWith($"unknown type 'ship': {broken} has an error, so the type didn't load", message, StringComparison.Ordinal);
        Assert.DoesNotContain("enable the pack", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeNoPackDefinesAsksToCheckTheSpellingAndThePack()
    {
        // A broken pack that has no file for the type doesn't explain it.
        var source = ShipDen().With("packs/fleet/pack.yaml", "name: [Fleet\n");
        Note(source, A1, "gadget", "type: gadget\nname: Gadget\nstatus: canon", folder: "gadgets");

        var message = Only(DenCheck.Run(source), "F-ENT-2").Message;
        Assert.Contains("no enabled type has that key", message, StringComparison.Ordinal);
        Assert.Contains("Check the spelling. If a pack defines it, check that den.yaml enables the pack and that the pack loads without errors.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeDroppedForAPluralClashNamesTheFileInsteadOfAskingToEnableThePack()
    {
        // types.md F-TYPE-5: the den's own 'person' takes core's plural 'characters', so core's character is ignored.
        var source = Vael(denYaml: "packs: [people]\n")
            .With("packs/people/pack.yaml", "name: People\n")
            .With("packs/people/types/person.yaml", "name: Person\nplural: characters\n");
        Note(source, A1, "orrin-fell", Character("Orrin Fell"));

        var message = Only(DenCheck.Run(source), "F-ENT-2").Message;
        Assert.StartsWith("unknown type 'character': packs/people/types/person.yaml defines it, but that definition is ignored", message, StringComparison.Ordinal);
        Assert.DoesNotContain("enable", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACoreTypeMissingBecauseADenLocalCoreDoesntLoadSaysSo()
    {
        // types.md F-PACK-10: packs/core shadows the built-in core even with an error in its pack.yaml.
        var source = Vael().With("packs/core/pack.yaml", "name: [Core\n");
        Note(source, A1, "orrin-fell", Character("Orrin Fell"));

        var message = Only(DenCheck.Run(source), "F-ENT-2").Message;
        Assert.StartsWith("unknown type 'character': packs/core/pack.yaml has an error, so the type didn't load", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-7")]
    public void AShipNoteWithoutItsRequiredFieldsAndSectionIsWarned()
    {
        var source = ShipDen();
        Note(source, A1, "kestrel", "type: ship\nname: Kestrel\nstatus: canon\nfields:\n  motto: Onward", "\n## Overview\nA cutter.\n", folder: "ships");

        var check = DenCheck.Run(source);

        const string path = A1 + "/entities/ships/kestrel.md";
        Assert.Equal([(path, "F-FLD-3", 3), (path, "F-FLD-3", 3), (path, "F-SEC-5", 3)], Found(check));
        Assert.Contains(check.Diagnostics, d => d.Diagnostic.Message.StartsWith("class is required", StringComparison.Ordinal));
        Assert.Contains(check.Diagnostics, d => d.Diagnostic.Message.StartsWith("crew is required", StringComparison.Ordinal));
        Assert.Contains("'## Crew' is required", Only(check, "F-SEC-5").Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class: cutter\n  crew: 40", false)]
    [InlineData("class: cutter\n  crew:\n    - { value: 40, from: \"a1:3\" }", false)]
    [InlineData("class: cutter\n  crew:\n    - { value: ~, from: \"a1:3\" }\n    - { value: 12, from: \"a2:1\", status: idea }", false)]
    [InlineData("class: cutter\n  crew: { value: 40 }", false)]
    [InlineData("class: cutter\n  crew: ~", true)]
    [InlineData("class: cutter\n  crew: []", true)]
    [InlineData("class: cutter\n  crew: { value: ~ }", true)]
    [InlineData("class: cutter\n  crew:\n    - { value: ~, from: \"a1:3\" }", true)]
    public void ARequiredFieldIsMissingOnlyWithoutANonNullValue(string fields, bool missing)
    {
        // F-FT-14: status and positions don't matter, only whether a non-null value is written.
        var source = ShipDen();
        Note(source, A1, "kestrel", $"type: ship\nname: Kestrel\nstatus: canon\nfields:\n  {fields}", "\n## Crew\nForty hands.\n", folder: "ships");

        var check = DenCheck.Run(source);

        var found = check.Diagnostics.Where(d => d.Diagnostic.RuleId == "F-FLD-3").ToList();
        if (missing)
        {
            var d = Assert.Single(found);
            Assert.StartsWith("crew is required", d.Diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal(8, d.Diagnostic.Line);
        }
        else
        {
            Assert.Empty(found);
        }
    }

    [Fact]
    public void ARequiredSectionIsPresentWhateverItsStatusOrCase()
    {
        // F-TYPE-11: any level-2 section with the name, matched as in F-SEC-3.
        var source = ShipDen();
        Note(source, A1, "kestrel", "type: ship\nname: Kestrel\nstatus: canon\nfields:\n  class: cutter\n  crew: 40",
            "\n## crew\n<!-- wolfden: status=idea from=a3:1 -->\nTBD.\n", folder: "ships");

        Assert.Empty(Found(DenCheck.Run(source)));
    }

    [Fact]
    public void ARequiredSectionOnlyAsASubsectionIsMissing()
    {
        var source = ShipDen();
        Note(source, A1, "kestrel", "type: ship\nname: Kestrel\nstatus: canon\nfields:\n  class: cutter\n  crew: 40",
            "\n## Overview\n### Crew\nForty hands.\n", folder: "ships");

        Assert.Equal("F-SEC-5", Assert.Single(Found(DenCheck.Run(source))).Rule);
    }

    // ----- F-POS-4 -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-5")]
    public void AFactThatPointsAtACutChapterIsWarned()
    {
        // a2 had 30 chapters; the writer cut it to 24 and a fact still says a2:27.
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "fields:\n  role:\n    - { value: deckhand }\n    - { value: captain, from: \"a2:27\" }"));

        var check = DenCheck.Run(source);

        Assert.Equal([($"{A1}/entities/characters/orrin-fell.md", "F-POS-4", 9)], Found(check));
        Assert.Contains("a2:27", Only(check, "F-POS-4").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKindOfPositionThatDoesntResolveIsWarned()
    {
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "from: \"a9:1\"", "fields:\n  role:\n    - { value: deckhand, from: \"a1:1\", until: \"a1:99\" }"),
            "\n## Scar\n<!-- wolfden: from=a3:40 -->\nLeft cheek.\n\n## Fate\n<!-- wolfden: until=zz -->\nLost.\n");
        Note(source, A1, "the-debt", "type: thread\nname: The debt\nstatus: canon\nfields:\n  planted: [\"a1:2\", \"a1:77\"]\n  paid_off: \"q:1\"", folder: "threads");
        source.With($"{A2}/book.md", "---\nname: a2\nchapters: [1, 2]\n---\n\n## Notes\n<!-- wolfden: from=a2:5 -->\nRewrite.\n");

        var check = DenCheck.Run(source);

        Assert.Equal(
        [
            ($"{A1}/entities/characters/orrin-fell.md", "F-POS-4", 6),
            ($"{A1}/entities/characters/orrin-fell.md", "F-POS-4", 9),
            ($"{A1}/entities/characters/orrin-fell.md", "F-POS-4", 13),
            ($"{A1}/entities/characters/orrin-fell.md", "F-POS-4", 17),
            ($"{A1}/entities/threads/the-debt.md", "F-POS-4", 7),
            ($"{A1}/entities/threads/the-debt.md", "F-POS-4", 8),
            ($"{A2}/book.md", "F-POS-4", 7),
        ], Found(check));
        Assert.All(check.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Diagnostic.Severity));
    }

    [Fact]
    public void ABareBookPositionResolvesEvenWithoutChapters()
    {
        var source = Vael().Book("vael", "a", "a5", "chapters: []");
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "fields:\n  role:\n    - { value: deckhand }\n    - { value: captain, from: a5 }"));

        Assert.Empty(Found(DenCheck.Run(source)));
    }

    // ----- F-TMP-3 and F-TMP-5 -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-10")]
    public void AnEntryWhoseUntilIsNoLongerAfterItsFromIsNeverInEffect()
    {
        // The writer moved chapter 12 of a1 before chapter 5, so "5 until 12" now ends before it starts.
        var source = Vael().Book("vael", "a", "a1", "chapters: [1, 2, 3, 4, 12, 5, 6, 7, 8, 9, 10, 11]");
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "fields:\n  role:\n    - { value: deckhand, from: \"a1:5\", until: \"a1:12\" }"));

        var check = DenCheck.Run(source);

        Assert.Equal([($"{A1}/entities/characters/orrin-fell.md", "F-TMP-3", 8)], Found(check));
        Assert.Contains("until a1:12 isn't after from a1:5", Only(check, "F-TMP-3").Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-6")]
    public void TwoPostingsThatClaimTheSameChaptersOverlap()
    {
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", """
            fields:
              occupation:
                - { value: [deckhand], from: "a1:1", until: "a2:10" }
                - { value: [harbourmaster], from: "a2:5" }
            """));

        var check = DenCheck.Run(source);

        Assert.Equal([($"{A1}/entities/characters/orrin-fell.md", "F-TMP-5", 8)], Found(check));
        Assert.Contains("until a2:10, past the from of the one on line 9 (a2:5)", Only(check, "F-TMP-5").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EntriesThatShareAFromOverlap()
    {
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", """
            fields:
              role:
                - { value: deckhand }
                - { value: stowaway }
                - { value: captain, from: "a2:1" }
                - { value: admiral, from: "a2:1" }
            """));

        var check = DenCheck.Run(source);

        Assert.Equal(
        [
            ($"{A1}/entities/characters/orrin-fell.md", "F-TMP-5", 9),
            ($"{A1}/entities/characters/orrin-fell.md", "F-TMP-5", 11),
        ], Found(check));
        Assert.Contains("both start at the start of the story", check.Diagnostics[0].Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EntriesThatHandOverOrArentLiveDontOverlap()
    {
        // F-TMP-4: no until means up to the next live entry. Candidates may share a from,
        // and an entry that doesn't resolve isn't live (its F-POS-4 is the only warning).
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", """
            fields:
              role:
                - { value: deckhand }
                - { value: captain, from: "a2:1" }
                - { value: admiral, from: "a2:1", status: candidate }
                - { value: pirate, from: "a2:1", status: candidate }
                - { value: ghost, from: "a1:1", until: "a9:1" }
                - { value: mate, from: "a1:3", until: "a2:1" }
            """));

        Assert.Equal([($"{A1}/entities/characters/orrin-fell.md", "F-POS-4", 12)], Found(DenCheck.Run(source)));
    }

    // ----- F-SEC-8 and F-SEC-4 -----

    [Fact]
    public void ASectionWhoseUntilIsntAfterItsFromIsNeverInEffect()
    {
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "from: \"a1:10\""),
            "\n## Scar\n<!-- wolfden: from=a2:3 until=a2:3 -->\nLeft cheek.\n\n## Limp\n<!-- wolfden: until=a1:4 -->\nFrom the wreck.\n\n## Fine\n<!-- wolfden: until=a1:11 -->\nOk.\n");

        var check = DenCheck.Run(source);

        Assert.Equal(
        [
            ($"{A1}/entities/characters/orrin-fell.md", "F-SEC-8", 10),
            ($"{A1}/entities/characters/orrin-fell.md", "F-SEC-8", 14),
        ], Found(check));
        Assert.Contains("until a1:4 isn't after from a1:10", check.Diagnostics[1].Diagnostic.Message, StringComparison.Ordinal);

        // The rule reads SectionFact.IsEmptyRange, as F-TMP-3 reads EntryFact.IsEmptyRange.
        var facts = Entities.EntityFacts.For(Assert.Single(check.Catalog.Entities), new Entities.Bookkeeping());
        Assert.Equal([true, true, false], facts.Sections.Select(s => s.IsEmptyRange));
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-11")]
    public void ASecondAppearanceWithoutAFromOverlapsTheFirst()
    {
        // The writer pasted a new Appearance for the time-skip and forgot its from.
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell"),
            "\n## Appearance\nYoung, clean-shaven.\n\n## Appearance\nGrey-bearded, one eye.\n");

        var check = DenCheck.Run(source);

        Assert.Equal([($"{A1}/entities/characters/orrin-fell.md", "F-SEC-4", 11)], Found(check));
        Assert.Contains("'Appearance': this and the one on line 8 both start at the start of book 'a1'", Only(check, "F-SEC-4").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardSectionsOverlapWhenAnUntilRunsPastTheNextFrom()
    {
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell"),
            "\n## Appearance\n<!-- wolfden: until=a2:9 -->\nYoung.\n\n## Appearance\n<!-- wolfden: from=a2:3 -->\nGrey.\n");

        Assert.Equal([($"{A1}/entities/characters/orrin-fell.md", "F-SEC-4", 9)], Found(DenCheck.Run(source)));
    }

    [Fact]
    public void CustomOutOfCanonAndHandedOverSectionsDontOverlap()
    {
        var source = Vael();
        Note(source, A1, "orrin-fell", Character("Orrin Fell"), """

            ## Additions
            One.

            ## Additions
            Two.

            ## Appearance
            Young.

            ## Appearance
            <!-- wolfden: status=idea -->
            Maybe grey.

            ## Appearance
            <!-- wolfden: from=a3:1 -->
            Grey.
            """);

        Assert.Empty(Found(DenCheck.Run(source)));
    }

    // ----- Order -----

    [Fact]
    public void DiagnosticsAreSortedByPathThenLineThenRule()
    {
        var source = Vael();
        Note(source, A2, "zed", "type: charcter\nname: Zed\nstatus: canon", "\n## Overview\n[[nobody]]\n");
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "from: \"a1:99\""), "\n## Overview\n[[nobody]] [[nobody else]]\n");

        var check = DenCheck.Run(source);

        Assert.Equal(
        [
            ($"{A1}/entities/characters/orrin-fell.md", "F-POS-4", 6),
            ($"{A1}/entities/characters/orrin-fell.md", "F-LINK-8", 10),
            ($"{A1}/entities/characters/orrin-fell.md", "F-LINK-8", 10),
            ($"{A2}/entities/characters/zed.md", "F-ENT-2", 3),
            ($"{A2}/entities/characters/zed.md", "F-LINK-8", 9),
        ], Found(check));
        Assert.Equal(check.Diagnostics.Select(d => d.ToString()), DenCheck.Run(source).Diagnostics.Select(d => d.ToString()));
    }
}
