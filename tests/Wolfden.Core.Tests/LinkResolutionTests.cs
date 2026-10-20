using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using static Wolfden.Core.Tests.LinkDens;

namespace Wolfden.Core.Tests;

/// <summary>Resolving links by id, slug, name, and alias, through the scope chain (format.md §10.2).</summary>
public class LinkResolutionTests
{
    private const string MaraId = "01JA7Q8M4XK3V9R2D5W8HN6TQB";
    private const string Note = Starfall + "/entities/notes/links.md";

    /// <summary>A den with Mara Voss, a station whose slug is "voss", and a note holding <paramref name="links"/>.</summary>
    private static LinkIndex Tiers(string links)
    {
        var source = Den()
            .Entity(LongDark, "mara-voss", Character("Mara Voss", $"id: {MaraId}", "aliases: [Voss, \"the Commander\"]"),
                "## Shoulder injury\nHit.\n\n### Recovery\nSlow.\n")
            .Entity(Starfall, "voss", "type: location\nname: Voss Relay\nstatus: canon", folder: "locations")
            .Entity(Starfall, "decoy", "type: note\nname: " + "01JA7Q8M4XK3V9R2D5W8HN6TQB".Replace("B", "C", StringComparison.Ordinal) + "\nstatus: canon", folder: "notes")
            .Entity(Starfall, "links", "type: note\nname: Links\nstatus: canon", links, folder: "notes");
        return LinkIndex.Load(source);
    }

    [Theory]
    [InlineData("[[mara-voss]]", "Mara Voss", LinkMatchKind.Slug)]
    [InlineData("[[MARA-VOSS]]", "Mara Voss", LinkMatchKind.Slug)]
    [InlineData("[[Mara Voss]]", "Mara Voss", LinkMatchKind.Name)]
    [InlineData("[[mara   voss]]", "Mara Voss", LinkMatchKind.Name)]
    [InlineData("[[the commander]]", "Mara Voss", LinkMatchKind.Alias)]
    [InlineData("[[" + MaraId + "]]", "Mara Voss", LinkMatchKind.Id)]
    [InlineData("[[Voss]]", "Voss Relay", LinkMatchKind.Slug)]
    [InlineData("[[voss relay]]", "Voss Relay", LinkMatchKind.Name)]
    [InlineData("[[mara-voss|Voss]]", "Mara Voss", LinkMatchKind.Slug)]
    public void TheFirstTierWithAMatchWins(string link, string name, LinkMatchKind kind)
    {
        var index = Tiers(link + "\n");
        var resolved = index.Link(Note, link);

        Assert.Equal(LinkOutcome.Resolved, resolved.Outcome);
        Assert.Equal(name, resolved.Target!.Name);
        Assert.Equal(kind, resolved.Resolution.MatchedBy);
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public void AnIdBeatsANameThatLooksLikeAnId()
    {
        // The decoy note is named with Mara's id, but with its last letter changed.
        var decoyName = MaraId.Replace("B", "C", StringComparison.Ordinal);
        var index = Tiers($"[[{MaraId}]] [[{decoyName}]] [[{MaraId.ToLowerInvariant()}]]\n");

        Assert.Equal("Mara Voss", index.Link(Note, $"[[{MaraId}]]").Target!.Name);
        Assert.Equal(LinkMatchKind.Name, index.Link(Note, $"[[{decoyName}]]").Resolution.MatchedBy);
        // A lowercase id isn't an id (F-ID-1), and matches nothing else.
        Assert.Equal(LinkOutcome.Broken, index.Link(Note, $"[[{MaraId.ToLowerInvariant()}]]").Outcome);
    }

    [Fact]
    public void HyphensAndSpacesStayDistinct()
    {
        // [[mara voss]] is the name, not the slug, and [[voss-relay]] is neither.
        var index = Tiers("[[mara voss]] [[voss-relay]]\n");

        Assert.Equal(LinkMatchKind.Name, index.Link(Note, "[[mara voss]]").Resolution.MatchedBy);
        Assert.Equal(LinkOutcome.Broken, index.Link(Note, "[[voss-relay]]").Outcome);
    }

    [Fact]
    public void HeadingsMatchSectionsAtAnyLevel()
    {
        var index = Tiers("[[mara-voss#shoulder  INJURY]] [[mara-voss#Recovery]] [[mara-voss#Missing]]\n");

        Assert.Equal("Shoulder injury", index.Link(Note, "[[mara-voss#shoulder  INJURY]]").TargetSection!.Name);
        Assert.Equal("Recovery", index.Link(Note, "[[mara-voss#Recovery]]").TargetSection!.Name);
        var missing = index.Link(Note, "[[mara-voss#Missing]]");
        Assert.Equal(LinkOutcome.Resolved, missing.Outcome);
        Assert.Null(missing.TargetSection);
        Assert.Equal([(Note, "F-LINK-9", 6)], index.Rules());
    }

    [Fact]
    public void EmptyLinksAreWarnings()
    {
        var index = Tiers("[[]] and [[ |Voss]]\n");

        Assert.Empty(index.Links);
        Assert.Equal([(Note, "F-LINK-3", 6), (Note, "F-LINK-3", 6)], index.Rules());
    }

    /// <summary>
    /// Two characters named Pike: one at universe scope, one in the book Ash.
    /// Two named Lantern: one in Ember, one in Ash. Two named Twin, both at
    /// universe scope. One named Ember Only, in Ember.
    /// </summary>
    private static LinkIndex Scopes()
    {
        var links = "[[Pike]] [[Lantern]] [[Twin]] [[Ember Only]]\n";
        var source = Den()
            .Entity(Starfall, "pike-elder", Character("Pike"))
            .Entity(Ash, "pike-younger", Character("Pike"))
            .Entity(Ember, "lantern-ember", Character("Lantern"))
            .Entity(Ash, "lantern-ash", Character("Lantern"))
            .Entity(Starfall, "twin-a", Character("Twin"))
            .Entity(Starfall, "twin-b", Character("Twin"))
            .Entity(Ember, "ember-only", Character("Ember Only"))
            .Entity(Ash, "in-ash", Character("In Ash"), links)
            .Entity(Ember, "in-ember", Character("In Ember"), links)
            .Entity(LongDark, "in-series", Character("In Series"), links)
            .Entity(Moon, "in-moon", Character("In Moon"), links)
            .Entity(Starfall, "in-universe", Character("In Universe"), links)
            .With($"{Starfall}/universe.md", "---\nname: Starfall\norder: [long-dark, a-quiet-moon]\n---\n" + links)
            .With($"{Ash}/outline.md", "| row | beat |\n|---|---|\n| 1 | " + links.Trim() + " |\n");
        return LinkIndex.Load(source);
    }

    [Theory]
    // From Ash, the nearest Pike is Ash's own, and Ash's Lantern beats Ember's.
    [InlineData(Ash + "/entities/characters/in-ash.md", "[[Pike]]", "pike-younger")]
    [InlineData(Ash + "/entities/characters/in-ash.md", "[[Lantern]]", "lantern-ash")]
    [InlineData(Ash + "/outline.md", "[[Pike]]", "pike-younger")]
    [InlineData(Ash + "/outline.md", "[[Lantern]]", "lantern-ash")]
    // From Ember, the universe's Pike is the only one in the scope chain.
    [InlineData(Ember + "/entities/characters/in-ember.md", "[[Pike]]", "pike-elder")]
    [InlineData(Ember + "/entities/characters/in-ember.md", "[[Lantern]]", "lantern-ember")]
    [InlineData(LongDark + "/entities/characters/in-series.md", "[[Pike]]", "pike-elder")]
    [InlineData(Moon + "/entities/characters/in-moon.md", "[[Pike]]", "pike-elder")]
    [InlineData(Starfall + "/entities/characters/in-universe.md", "[[Pike]]", "pike-elder")]
    [InlineData(Starfall + "/universe.md", "[[Pike]]", "pike-elder")]
    // A single match resolves from anywhere in the universe, even a sibling book (design.md §2).
    [InlineData(Ash + "/entities/characters/in-ash.md", "[[Ember Only]]", "ember-only")]
    [InlineData(Moon + "/entities/characters/in-moon.md", "[[Ember Only]]", "ember-only")]
    [InlineData(Starfall + "/universe.md", "[[Ember Only]]", "ember-only")]
    public void TheNearestScopeInTheChainWins(string path, string link, string slug)
    {
        var resolved = Scopes().Link(path, link);

        Assert.Equal(LinkOutcome.Resolved, resolved.Outcome);
        Assert.Equal(slug, resolved.Target!.Slug);
    }

    [Theory]
    // Neither Lantern is in the chain of the series, the standalone book, or the universe.
    [InlineData(LongDark + "/entities/characters/in-series.md", "[[Lantern]]", "none of them is in this file's scope chain")]
    [InlineData(Moon + "/entities/characters/in-moon.md", "[[Lantern]]", "none of them is in this file's scope chain")]
    [InlineData(Starfall + "/universe.md", "[[Lantern]]", "none of them is in this file's scope chain")]
    // Both Twins are at universe scope, so they tie from everywhere.
    [InlineData(Ash + "/entities/characters/in-ash.md", "[[Twin]]", "2 of them are in the nearest scope, universe 'starfall'")]
    [InlineData(Starfall + "/entities/characters/in-universe.md", "[[Twin]]", "2 of them are in the nearest scope, universe 'starfall'")]
    public void OtherwiseTheLinkIsAmbiguous(string path, string link, string why)
    {
        var index = Scopes();
        var resolved = index.Link(path, link);

        Assert.Equal(LinkOutcome.Ambiguous, resolved.Outcome);
        Assert.Null(resolved.Target);
        Assert.Equal(2, resolved.Resolution.Matches.Count);
        var warning = Assert.Single(index.Diagnostics, d => d.Path == path && d.Diagnostic.Message.StartsWith(link, StringComparison.Ordinal));
        Assert.Equal("F-LINK-7", warning.Diagnostic.RuleId);
        Assert.Contains(why, warning.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("by name", warning.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATieInAnEarlierTierIsntBrokenByALaterOne()
    {
        // Both Twins match by name, so the alias on a third entity never counts.
        var source = Den()
            .Entity(Starfall, "twin-a", Character("Twin"))
            .Entity(Starfall, "twin-b", Character("Twin"))
            .Entity(Ember, "other", Character("Other", "aliases: [Twin]"), "[[Twin]]\n");
        var link = LinkIndex.Load(source).Link($"{Ember}/entities/characters/other.md", "[[Twin]]");

        Assert.Equal(LinkOutcome.Ambiguous, link.Outcome);
        Assert.Equal(LinkMatchKind.Name, link.Resolution.MatchedBy);
    }

    [Fact]
    public void TwoAliasesOfOneEntityAreOneMatch()
    {
        var source = Den()
            .Entity(Starfall, "mara-voss", Character("Mara Voss", "aliases: [Voss, VOSS]"))
            .Entity(Starfall, "note", Character("Note"), "[[voss]]\n");

        Assert.Equal("mara-voss", LinkIndex.Load(source).Link($"{Starfall}/entities/characters/note.md", "[[voss]]").Target!.Slug);
    }

    [Theory]
    [InlineData("[[nobody]]", "'nobody' matches no entity in universe 'starfall'.")]
    [InlineData("[[Wren Tide]]", "It matches only 'Wren Tide' in universe 'drift'; links don't cross universes.")]
    [InlineData("[[ember]]", "It matches book 'ember' ('ember'), but containers aren't link targets yet.")]
    [InlineData("[[long-dark]]", "It matches series 'long-dark'")]
    [InlineData("[[Copied]]", "id is used by more than one file (F-ID-3)")]
    [InlineData("[[Has Errors]]", "It would match universes/starfall/entities/characters/has-errors.md, but that file has errors, so it isn't indexed (F-GEN-2).")]
    [InlineData("[[has-errors]]", "but that file has errors")]
    public void BrokenLinksSayWhy(string link, string message)
    {
        const string id = "01JA7Q8M4XK3V9R2D5W8HN6TQD";
        var source = Den()
            .Entity(Drift, "wren-tide", Character("Wren Tide"))
            .Entity(Starfall, "copied", Character("Copied", $"id: {id}"))
            .Entity(Ember, "copied", Character("Copied", $"id: {id}"))
            .Entity(Starfall, "has-errors", Character("Has Errors", "status: maybe"))
            .Entity(Starfall, "note", Character("Note"), link + "\n");
        var index = LinkIndex.Load(source);

        Assert.Equal(LinkOutcome.Broken, index.Link($"{Starfall}/entities/characters/note.md", link).Outcome);
        var warning = Assert.Single(index.Diagnostics).Diagnostic;
        Assert.Equal("F-LINK-8", warning.RuleId);
        Assert.Contains(message, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntityWithABadFileNameIsReachedByNameOnly()
    {
        var source = Den()
            .With($"{Starfall}/entities/characters/Mara Voss.md", "---\ntype: character\nname: Mara Voss\n---\n")
            .Entity(Starfall, "note", Character("Note"), "[[Mara Voss]] [[mara-voss]]\n");
        var index = LinkIndex.Load(source);

        Assert.Null(Assert.Single(index.Catalog.Entities, e => e.Name == "Mara Voss").Slug);
        Assert.Contains(index.Catalog.Diagnostics, d => d.Diagnostic.RuleId == "F-KEY-4");
        Assert.Equal(LinkOutcome.Resolved, index.Link($"{Starfall}/entities/characters/note.md", "[[Mara Voss]]").Outcome);
        Assert.Equal(LinkOutcome.Broken, index.Link($"{Starfall}/entities/characters/note.md", "[[mara-voss]]").Outcome);
    }

    [Fact]
    public void FilesWithErrorsAreSkipped()
    {
        // F-GEN-2: an entity with an error isn't indexed, so its own links aren't either.
        var source = Den().Entity(Starfall, "broken", Character("Broken", "status: maybe"), "[[nobody]]\n");
        var index = LinkIndex.Load(source);

        Assert.Empty(index.Links);
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public void TheResolverResolvesAnyTargetFromAnyScope()
    {
        var index = Scopes();
        var ash = index.Catalog.Structure.FindUniverse("starfall")!.FindBook("ash")!;

        Assert.Equal("pike-younger", index.Resolver.Resolve(ash, "  pike ").Entity!.Slug);
        Assert.Equal(LinkOutcome.Broken, index.Resolver.Resolve(ash, "nobody").Outcome);
    }

    [Fact]
    public void LinksToAnEntityAreItsBacklinks()
    {
        var index = Scopes();
        var elder = index.Catalog.Entities.Single(e => e.Slug == "pike-elder");

        string[] expected =
        [
            $"{LongDark}/entities/characters/in-series.md", $"{Moon}/entities/characters/in-moon.md",
            $"{Ember}/entities/characters/in-ember.md", $"{Starfall}/entities/characters/in-universe.md", $"{Starfall}/universe.md",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), index.LinksTo(elder).Select(l => l.Occurrence.Path));
        Assert.Equal(elder.Path, elder.Key);
    }

    [Fact]
    public void ASlugWithStatusIdeaStillResolves()
    {
        // F-LINK-5: every status is a candidate.
        var source = Den()
            .Entity(Starfall, "maybe", Character("Maybe", "status: idea"))
            .Entity(Starfall, "note", "type: note\nname: Note\nstatus: idea", "[[maybe]]\n");
        var index = LinkIndex.Load(source);

        Assert.Equal(FactStatus.Idea, index.Link($"{Starfall}/entities/characters/note.md", "[[maybe]]").Target!.Status);
    }

    [Fact]
    public void AnIdBeatsANameEqualToIt()
    {
        var source = Den()
            .Entity(Starfall, "mara-voss", Character("Mara Voss", $"id: {MaraId}"))
            .Entity(Starfall, "decoy", Character(MaraId))
            .Entity(Starfall, "note", Character("Note"), $"[[{MaraId}]]\n");

        var link = LinkIndex.Load(source).Link($"{Starfall}/entities/characters/note.md", $"[[{MaraId}]]");

        Assert.Equal("mara-voss", link.Target!.Slug);
        Assert.Equal(LinkMatchKind.Id, link.Resolution.MatchedBy);
    }

    [Fact]
    public void ADuplicateIdWinnerBreaksTheLinkInsteadOfFallingThroughATier()
    {
        // F-ID-3: [[alpha]] would reach Alpha by name, so it's broken, not
        // resolved to Gamma by alias.
        const string id = "01JA7Q8M4XK3V9R2D5W8HN6TQB";
        var source = Den()
            .Entity(Starfall, "a", Character("Alpha", $"id: {id}"))
            .Entity(Starfall, "b", Character("Beta", $"id: {id}"))
            .Entity(Starfall, "c", Character("Gamma", "aliases: [alpha]"))
            .Entity(Starfall, "note", Character("Note"), "[[alpha]]\n");
        var index = LinkIndex.Load(source);

        var link = index.Link($"{Starfall}/entities/characters/note.md", "[[alpha]]");
        Assert.Equal(LinkOutcome.Broken, link.Outcome);
        Assert.Null(link.Target);
        var warning = Assert.Single(index.Diagnostics).Diagnostic;
        Assert.Equal("F-LINK-8", warning.RuleId);
        Assert.Contains("would match 'Alpha' (universes/starfall/entities/characters/a.md), but that entity's id is used by more than one file (F-ID-3)",
            warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateIdWinnerBreaksTheLinkInsteadOfFallingThroughAScope()
    {
        // The book's own Lantern has a duplicated id, so [[Lantern]] from the
        // book is broken, not resolved to the universe's Lantern.
        const string id = "01JA7Q8M4XK3V9R2D5W8HN6TQB";
        var source = Den()
            .Entity(Ash, "lantern", Character("Lantern", $"id: {id}"))
            .Entity(Moon, "copy", Character("Copy", $"id: {id}"))
            .Entity(Starfall, "lantern", Character("Lantern"))
            .Entity(Ash, "note", Character("Note"), "[[Lantern]]\n");
        var index = LinkIndex.Load(source);

        Assert.Equal(LinkOutcome.Broken, index.Link($"{Ash}/entities/characters/note.md", "[[Lantern]]").Outcome);
        Assert.Contains("(F-ID-3)", Assert.Single(index.Diagnostics).Diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Scenario", "SC-CHECK-4")]
    [InlineData("[[Alpha]]", "name")]
    [InlineData("[[01JA7Q8M4XK3V9R2D5W8HN6TQB]]", "id")]
    public void TiedCopiesWithTheSameIdBreakTheLinkAndSayToGiveThemNewIds(string text, string matchedBy)
    {
        // Both copies are at the same scope with the same name and id, so they
        // tie. Linking by slug or id can't pick one (F-ID-3), so the link is
        // broken, and the warning says what fixes it (#82).
        const string id = "01JA7Q8M4XK3V9R2D5W8HN6TQB";
        var source = Den()
            .Entity(Starfall, "first", Character("Alpha", $"id: {id}"))
            .Entity(Starfall, "second", Character("Alpha", $"id: {id}"))
            .Entity(Starfall, "note", Character("Note"), text + "\n");
        var index = LinkIndex.Load(source);

        var link = index.Link($"{Starfall}/entities/characters/note.md", text);
        Assert.Equal(LinkOutcome.Broken, link.Outcome);
        Assert.Null(link.Target);
        var warning = Assert.Single(index.Diagnostics).Diagnostic;
        Assert.Equal("F-LINK-8", warning.RuleId);
        Assert.Equal(
            $"{text}: '{text[2..^2]}' would match 2 entities by {matchedBy} ('Alpha' (universes/starfall/entities/characters/first.md); 'Alpha' (universes/starfall/entities/characters/second.md)), " +
            "but each of their ids is used by more than one file (F-ID-3), so links don't reach them until the ids are unique. Give each copy a new id.",
            warning.Message);
    }

    [Fact]
    public void ATieWithOneUniqueIdIsStillAmbiguous()
    {
        // Gamma's id is unique, so linking by its slug or id does pick it.
        const string id = "01JA7Q8M4XK3V9R2D5W8HN6TQB";
        var source = Den()
            .Entity(Starfall, "first", Character("Alpha", $"id: {id}"))
            .Entity(Starfall, "b", Character("Beta", $"id: {id}"))
            .Entity(Starfall, "c", Character("Alpha"))
            .Entity(Starfall, "note", Character("Note"), "[[Alpha]]\n");
        var index = LinkIndex.Load(source);

        Assert.Equal(LinkOutcome.Ambiguous, index.Link($"{Starfall}/entities/characters/note.md", "[[Alpha]]").Outcome);
        Assert.EndsWith("Link by slug or id to pick one.", Assert.Single(index.Diagnostics).Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousMessagesListAtMostFiveMatches()
    {
        var source = Den();
        for (var i = 0; i < 8; i++)
        {
            source.Entity(Starfall, $"same-{i}", Character("Same"));
        }

        source.Entity(Starfall, "note", Character("Note"), "[[Same]]\n");
        var index = LinkIndex.Load(source);

        Assert.Equal(8, index.Link($"{Starfall}/entities/characters/note.md", "[[Same]]").Resolution.Matches.Count);
        var message = Assert.Single(index.Diagnostics).Diagnostic.Message;
        Assert.Contains("same-4.md); and 3 more), and 8 of them", message, StringComparison.Ordinal);
        Assert.DoesNotContain("same-5.md", message, StringComparison.Ordinal);
    }
}
