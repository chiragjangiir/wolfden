using Wolfden.Core.Links;

namespace Wolfden.Core.Tests;

/// <summary>
/// Every link in the invented Starfall den (<c>Dens/starfall</c>), across a
/// series of two books, a standalone book, and universe scope. The den has
/// two deliberate problems: an ambiguous link and a spoiler.
/// </summary>
public class StarfallLinkTests
{
    private static readonly Lazy<LinkIndex> Index = new(() => LinkIndex.Load(MemoryDenSource.StarfallRoot));

    private const string Universe = "universes/starfall";
    private const string Ash = Universe + "/series/long-dark/books/ash";

    [Fact]
    public void TheDenHasExactlyTheDeliberateProblems()
    {
        var index = Index.Value;

        Assert.Empty(index.Catalog.Structure.Diagnostics);
        Assert.Empty(index.Catalog.Diagnostics);
        Assert.Equal(
            [
                (Universe + "/entities/locations/kessan-reach.md", "F-LINK-7", (int?)15),
                (Universe + "/series/long-dark/books/ember/outline.md", "F-LINK-13", 9),
            ],
            index.Rules());
    }

    [Fact]
    public void TheNearestLanternWinsFromAsh()
    {
        // Two entities are named Lantern: a ship in A Quiet Moon and an item
        // in Ash. From Ash, Ash's own wins.
        var link = Index.Value.Link(Ash + "/entities/characters/halden-pike.md", "[[Lantern]]");

        Assert.Equal(Ash + "/entities/items/lantern.md", link.Target!.Path);
    }

    [Fact]
    public void FromTheUniverseTheLanternIsAmbiguous()
    {
        var link = Index.Value.Link(Universe + "/entities/locations/kessan-reach.md", "[[Lantern]]");

        Assert.Equal(LinkOutcome.Ambiguous, link.Outcome);
        Assert.Equal(
            [Universe + "/books/a-quiet-moon/entities/ships/lantern.md", Ash + "/entities/items/lantern.md"],
            link.Resolution.Matches.Select(m => m.Path));
    }

    [Fact]
    public void ASeriesEntityReachesABookEntityAcrossScopes()
    {
        // Tessaly is at series scope; Pike is in Ash. Its link to him sits in
        // a subsection from=ash, so it's no spoiler.
        var link = Index.Value.Link(Universe + "/series/long-dark/entities/ships/tessaly.md", "[[Pike]]");

        Assert.Equal("Halden Pike", link.Target!.Name);
        Assert.Equal(LinkMatchKind.Alias, link.Resolution.MatchedBy);
    }

    [Fact]
    public void ThePayoffLinkIsCheckedAtPaidOff()
    {
        // The thread enters at ember:2, before Pike, but its Payoff is at
        // paid_off, a-quiet-moon:2b (types.md F-CORE-4).
        var link = Index.Value.Link(Universe + "/entities/threads/the-missing-manifest.md", "[[Halden Pike]]");

        Assert.Equal("a-quiet-moon:2b", Assert.Single(link.Occurrence.Positions).Description);
    }

    [Fact]
    public void FrontmatterLinksResolve()
    {
        var link = Index.Value.Links.Single(l => l.Occurrence.Link.Text == "[[tessaly]]");

        Assert.Equal(LinkSite.EntityFieldEntry, link.Occurrence.Site);
        Assert.Equal("posting", link.Occurrence.Field);
        Assert.Equal("Tessaly", link.Target!.Name);
        Assert.Equal("ember:1", Assert.Single(link.Occurrence.Positions).Description);
    }
}
