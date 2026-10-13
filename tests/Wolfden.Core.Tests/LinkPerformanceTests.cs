using System.Diagnostics;
using System.Text;
using Wolfden.Core.Documents;
using Wolfden.Core.Links;
using static Wolfden.Core.Tests.LinkDens;

namespace Wolfden.Core.Tests;

/// <summary>
/// Smoke tests that the link code stays near-linear: each case was
/// quadratic once. The bounds are generous, to catch a return to quadratic
/// time, not to measure speed.
/// </summary>
public class LinkPerformanceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Fact]
    public void ManyCodeSpansAndLinksInOneFile()
    {
        var body = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
        {
            body.Append("`code` [[x]]\n");
        }

        var file = MarkdownFile.Parse(TestFiles.Utf8("---\ntype: note\nname: N\n---\n" + body), requireFrontmatter: true);
        var watch = Stopwatch.StartNew();
        var links = BodyLinkScanner.ScanBody(file);

        Assert.Equal(20_000, links.Count);
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void ManyLinksToALargeCollisionAndTheirBacklinks()
    {
        var source = Den();
        for (var i = 0; i < 1_000; i++)
        {
            source.Entity(Starfall, $"same-{i}", Character("Same"));
        }

        source.Entity(Starfall, "note", Character("Note"), string.Concat(Enumerable.Repeat("[[Same]] [[same-1]]\n", 10_000)));
        var watch = Stopwatch.StartNew();
        var index = LinkIndex.Load(source);
        var backlinks = index.Catalog.Entities.Sum(e => index.LinksTo(e).Count);

        Assert.Equal(10_000, backlinks);
        Assert.Equal(10_000, index.Diagnostics.Count);
        Assert.All(index.Diagnostics.Take(10), d => Assert.True(d.Diagnostic.Message.Length < 1_000));
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void FindLinksInALongText()
    {
        var text = string.Concat(Enumerable.Repeat("line [[x]]\n", 50_000));
        var watch = Stopwatch.StartNew();
        var links = LinkSyntax.FindLinks(text);

        Assert.Equal(50_000, links.Count);
        Assert.Equal((50_000, 6), (links[^1].Line, links[^1].Column));
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed}.");
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(0, 3, true)]
    [InlineData(4, 5, true)]
    [InlineData(9, 10, true)]
    [InlineData(10, 12, false)]
    [InlineData(14, 16, true)]
    [InlineData(20, 30, false)]
    public void RangeSetsFindOverlapsAmongNestedAndUnsortedRanges(int start, int end, bool overlaps)
    {
        // [2,10) holds [4,6); [15,16) comes first in the input.
        var set = new RangeSet([(15, 16), (2, 10), (4, 6)]);

        Assert.Equal(overlaps, set.Overlaps(start, end));
    }
}
