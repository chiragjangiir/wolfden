using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>
/// Every link in <c>samples/dracula</c> resolves, with spoiler checks on,
/// and nothing about the links or entities is reported.
/// </summary>
public class DraculaLinkTests
{
    private static readonly Lazy<LinkIndex> Index = new(() => LinkIndex.Load(MemoryDenSource.DraculaRoot));

    [Fact]
    public void TheSampleTurnsOnSpoilerChecks()
    {
        Assert.True(Index.Value.Catalog.Structure.SpoilerChecks);
    }

    [Fact]
    public void EveryEntityLoadsWithNoDiagnostics()
    {
        var catalog = Index.Value.Catalog;
        Assert.Empty(catalog.Structure.Diagnostics);
        Assert.Empty(catalog.Diagnostics);

        // Every entity file in the sample: 12 characters, 15 events, 2 factions,
        // 3 items, 5 threads, 11 locations, and 2 notes.
        Assert.Equal(50, catalog.Entities.Count);
        Assert.All(catalog.Entities, e => Assert.NotNull(e.Id));
        Assert.All(catalog.Entities, e => Assert.NotNull(e.EntersCanon));
    }

    [Fact]
    public void EveryLinkResolvesWithNoDiagnostics()
    {
        var index = Index.Value;

        Assert.Empty(index.Diagnostics);
        Assert.All(index.Links, l => Assert.Equal(LinkOutcome.Resolved, l.Outcome));
    }

    [Fact]
    public void EveryLinkInTheSampleIsFound()
    {
        // Every [[...]] in the sample's files: none is in code or a comment.
        // Update this when the sample changes.
        var written = TestFiles.RepoFiles("samples/dracula/universes")
            .Sum(f => System.Text.RegularExpressions.Regex.Matches(TestFiles.Text(TestFiles.ReadRepoFile(f)), @"\[\[[^\]]*\]\]").Count);

        Assert.Equal(372, written);
        Assert.Equal(written, Index.Value.Links.Count);
    }

    [Fact]
    public void LinksAreFoundInEverySiteTheSampleUses()
    {
        var sites = Index.Value.Links.Select(l => l.Occurrence.Site).ToHashSet();

        Assert.Contains(LinkSite.EntitySection, sites);
        Assert.Contains(LinkSite.EntityField, sites);
        Assert.Contains(LinkSite.EntityFieldEntry, sites);
        Assert.Contains(LinkSite.ContainerBody, sites);
        Assert.Contains(LinkSite.ChapterField, sites);
        Assert.Contains(LinkSite.OutlineRow, sites);
    }

    [Fact]
    public void EscapedTableLinksKeepTheirLabels()
    {
        var link = Index.Value.Links.First(l => l.Occurrence.Link.Text == @"[[mina-harker\|Mina]]");

        Assert.Equal(LinkSite.OutlineRow, link.Occurrence.Site);
        Assert.Equal("mina-harker", link.Occurrence.Link.Target);
        Assert.Equal("Mina", link.Occurrence.Link.Label);
        Assert.Equal("Mina Harker", link.Target!.Name);
        Assert.Equal("10", link.Occurrence.OutlineRow);
    }

    [Fact]
    public void ThePayoffOfAThreadIsPositionedAtItsPaidOff()
    {
        // types.md F-CORE-4: the Count's end pays off at dracula:27, where
        // the link to his death is no spoiler.
        var link = Index.Value.Links.Single(l =>
            l.Occurrence.Path.EndsWith("the-counts-end.md", StringComparison.Ordinal) && l.Occurrence.Section?.Name == "Payoff");

        Assert.Equal("dracula:27", Assert.Single(link.Occurrence.Positions).Description);
        Assert.Equal("Death of the Count", link.Target!.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASpoilerInTheSampleWouldBeCaught(bool spoilerChecks)
    {
        // Move Renfield's entry into canon to chapter 6: the outline row of
        // chapter 5 that names him now runs ahead of the reader.
        const string renfield = "universes/dracula/books/dracula/entities/characters/renfield.md";
        var source = MemoryDenSource.FromDirectory(MemoryDenSource.DraculaRoot);
        var text = TestFiles.Text(source.ReadFile(renfield)).Replace("from: \"dracula:5\"\nfields", "from: \"dracula:6\"\nfields", StringComparison.Ordinal);
        source.With(renfield, text);
        if (!spoilerChecks)
        {
            source.With("den.yaml", TestFiles.Text(source.ReadFile("den.yaml")).Replace("spoiler_checks: true", "spoiler_checks: false", StringComparison.Ordinal));
        }

        var index = LinkIndex.Load(source);

        if (!spoilerChecks)
        {
            Assert.Empty(index.Diagnostics);
            return;
        }

        Assert.All(index.Diagnostics, d => Assert.Equal("F-LINK-13", d.Diagnostic.RuleId));
        var outline = Assert.Single(index.Diagnostics, d => d.Path.EndsWith("outline.md", StringComparison.Ordinal));
        Assert.Equal(27, outline.Diagnostic.Line);
        Assert.Contains("dracula:6, after this link's position, dracula:5", outline.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BacklinksFindEveryLinkToAnEntity()
    {
        var index = Index.Value;
        var seward = index.Catalog.Entities.Single(e => e.Slug == "john-seward");

        Assert.Equal(63, index.LinksTo(seward).Count());
    }
}
