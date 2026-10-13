using System.Text.Json;
using Wolfden.Core.Links;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;

namespace Wolfden.Index.Tests;

/// <summary>
/// The index of the invented Starfall den: two universes, a series of two
/// books, a standalone book, a series with no books yet, an ambiguous link,
/// and a spoiler.
/// </summary>
public sealed class StarfallIndexTests : IClassFixture<StarfallIndexTests.Built>
{
    private const string Mara = "01JA7Q8M4XK3V9R2D5W8HN6TQB";
    private const string Pike = "01JA7Q8M4XK3V9R2D5W8HN6TQC";
    private const string Manifest = "01JA7Q8M4XK3V9R2D5W8HN6TQD";
    private const string Reach = "01JADJ8FSSZ5AWSH8VHTPRE95B";

    private readonly Built _built;

    public StarfallIndexTests(Built built)
    {
        _built = built;
    }

    private DenIndex Index => _built.Index;

    [Fact]
    public void ContainersAndChaptersAreInStoryOrder()
    {
        var db = Index.Context;
        var starfall = db.Containers.Single(c => c.Kind == "universe" && c.Key == "starfall");
        var books = db.Containers.Where(c => c.UniverseId == starfall.Id && c.Kind == "book").OrderBy(c => c.StoryOrder).ToList();

        Assert.Equal(["ember", "ash", "a-quiet-moon"], books.Select(b => b.Key));
        Assert.Equal(books.Select(b => b.StartOrdinal).Order(), books.Select(b => b.StartOrdinal));
        Assert.Equal("draft", books[0].BookStatus);

        var chapters = db.Chapters.Where(c => c.UniverseId == starfall.Id).ToList()
            .OrderBy(c => books.FindIndex(b => b.Id == c.BookId)).ThenBy(c => c.Index).ToList();
        Assert.Equal(chapters.Select(c => c.Ordinal).Order(), chapters.Select(c => c.Ordinal));
        Assert.Equal(13, chapters.Count);

        // The empty series sits after A Quiet Moon in story order.
        var driftWar = db.Containers.Single(c => c.Key == "drift-war");
        Assert.True(driftWar.StartOrdinal > chapters[^1].Ordinal);
        Assert.Null(driftWar.StoryOrder);

        Assert.Equal(2, db.Containers.Count(c => c.Kind == "universe"));
    }

    [Fact]
    public void TemporalEntriesHandOver()
    {
        var mara = Index.FindEntity(Mara)!;
        var values = Index.Context.FieldValues.Where(v => v.EntityId == mara.Id).OrderBy(v => v.FieldOrder).ThenBy(v => v.EntryIndex).ToList();

        var rank = values.Where(v => v.Field == "rank").ToList();
        Assert.Equal(["\"Lieutenant\"", "\"Commander\"", "\"Captain\""], rank.Select(v => v.ValueJson));
        Assert.Equal((P("ember:1"), P("ember:2b")), (rank[0].FromOrdinal, rank[0].UntilOrdinal));

        // Captain is a candidate, so it isn't live and Commander runs on.
        Assert.Equal((P("ember:2b"), long.MaxValue), (rank[1].FromOrdinal, rank[1].UntilOrdinal));
        Assert.True(rank[1].Pinned);
        Assert.False(rank[2].IsLive);
        Assert.Equal("candidate", rank[2].Status);

        var posting = values.Where(v => v.Field == "posting").ToList();
        Assert.Equal((P("ember:1"), P("ash:2")), (posting[0].FromOrdinal, posting[0].UntilOrdinal));
        Assert.Equal((P("ash:4"), long.MaxValue), (posting[1].FromOrdinal, posting[1].UntilOrdinal));

        var species = values.Single(v => v.Field == "species");
        Assert.Equal("entry", species.Kind);
        Assert.Equal(P("ember:prologue"), species.FromOrdinal);
    }

    [Fact]
    public void CanonAsOfIsARangeComparison()
    {
        // What #15 builds on: the rank in effect at a point is one indexed comparison.
        var mara = Index.FindEntity(Mara)!;
        string? RankAt(string position)
        {
            var p = P(position);
            return Index.Context.FieldValues
                .Where(v => v.EntityId == mara.Id && v.Field == "rank" && v.IsLive && v.FromOrdinal <= p && p < v.UntilOrdinal)
                .Select(v => v.ValueJson)
                .SingleOrDefault();
        }

        Assert.Null(RankAt("ember:prologue"));
        Assert.Equal("\"Lieutenant\"", RankAt("ember:2"));
        Assert.Equal("\"Commander\"", RankAt("ember:2b"));
        Assert.Equal("\"Commander\"", RankAt("a-quiet-moon:1"));
    }

    [Fact]
    public void SectionsHaveEffectiveRanges()
    {
        var mara = Index.FindEntity(Mara)!;
        var sections = Index.Context.Sections.Where(s => s.EntityId == mara.Id).OrderBy(s => s.Index).ToDictionary(s => s.Name);

        Assert.Equal((P("ember:1"), long.MaxValue), (sections["Overview"].FromOrdinal, sections["Overview"].UntilOrdinal));
        Assert.Equal((P("ember:3"), long.MaxValue), (sections["In command"].FromOrdinal, sections["In command"].UntilOrdinal));
        Assert.Equal((P("ash:1"), P("ash:3")), (sections["Recovery"].FromOrdinal, sections["Recovery"].UntilOrdinal));
        Assert.Equal(sections["Shoulder injury"].Id, sections["Recovery"].ParentId);
        Assert.Equal(["Shoulder injury", "Recovery"], JsonSerializer.Deserialize<string[]>(sections["Recovery"].HeadingPathJson)!);
        Assert.Equal("{\"from\":\"ash:1\",\"until\":\"ash:3\"}", sections["Recovery"].AttributesJson);

        var debts = Index.Context.Sections.Single(s => s.EntityId == Index.FindEntity(Pike)!.Id && s.Name == "Debts");
        Assert.Equal((P("ash:2"), P("ash:epilogue")), (debts.FromOrdinal, debts.UntilOrdinal));
    }

    [Fact]
    public void AThreadsPayoffEntersAtPaidOff()
    {
        var manifest = Index.FindEntity(Manifest)!;
        var payoff = Index.Context.Sections.Single(s => s.EntityId == manifest.Id && s.Name == "Payoff");

        Assert.Equal(P("a-quiet-moon:2b"), payoff.FromOrdinal);
        Assert.Null(payoff.FromText);
    }

    [Fact]
    public void LinksKeepTheirOutcomes()
    {
        var links = Index.Context.Links.ToList();
        var core = LinkIndex.Load(_built.Den.Root);

        Assert.Equal(core.Links.Count, links.Count);
        var ambiguous = links.Single(l => l.Outcome == "ambiguous");
        Assert.Equal("[[Lantern]]", ambiguous.Text);
        Assert.Null(ambiguous.TargetKey);
        Assert.Equal(2, JsonSerializer.Deserialize<string[]>(ambiguous.MatchesJson!)!.Length);
        Assert.NotNull(ambiguous.Problem);

        var outlineLink = links.Single(l => l.Text == @"[[mara-voss\|Voss]]");
        Assert.Equal("outline_row", outlineLink.Site);
        Assert.Equal("2", outlineLink.OutlineRow);
        Assert.Equal(Mara, outlineLink.TargetKey);
        Assert.Equal(P("ember:2"), outlineLink.PositionOrdinal);

        var sectionLink = links.First(l => l.Path.EndsWith("mara-voss.md", StringComparison.Ordinal) && l.SourceSectionId is not null);
        Assert.Equal(Reach, sectionLink.TargetKey);
        Assert.Equal("slug", sectionLink.MatchedBy);
    }

    [Fact]
    public void BacklinksFindEveryLinkToAnEntity()
    {
        var backlinks = Index.Backlinks(Reach);

        Assert.Contains(backlinks, l => l.Site == "outline_row");
        Assert.Contains(backlinks, l => l.Site == "entity_field_entry" && l.Field == "posting");
        Assert.Contains(backlinks, l => l.Path.EndsWith("ships/lantern.md", StringComparison.Ordinal) && l.Text == "[[the Reach]]");
        Assert.Equal(5, backlinks.Count);
    }

    [Fact]
    public void DiagnosticsMatchCore()
    {
        var core = LinkIndex.Load(_built.Den.Root);
        var expected = core.Catalog.Structure.Diagnostics.Concat(core.Catalog.Diagnostics).Concat(core.Diagnostics)
            .Select(d => (d.Path, d.Diagnostic.RuleId, d.Diagnostic.Line)).Order();

        Assert.Equal(expected, Index.Context.Diagnostics.ToList().Select(d => (d.Path, d.RuleId, d.Line)).Order());
        Assert.Contains(Index.Context.Diagnostics, d => d.RuleId == "F-LINK-13" && d.Severity == "warning");
    }

    [Fact]
    public void OutlineRowsUnescapeCellsAndMapChapters()
    {
        var rows = Index.Context.OutlineRows.OrderBy(r => r.Order).ToList();

        Assert.Equal(["1", "2", "3"], rows.Select(r => r.RowId));
        Assert.Equal("[[mara-voss|Voss]]", rows[1].Pov);
        Assert.Equal(P("ember:2b"), rows[2].ChapterOrdinal);
        Assert.Null(rows[2].Pov);
        Assert.Equal("draft", rows[0].Status);
        Assert.Equal("Act one", rows[0].Heading);
    }

    [Fact]
    public void AnEntityWithoutFromEntersAtItsScopesStart()
    {
        var pike = Index.FindEntity(Pike)!;

        Assert.Equal("ash", pike.FromText);
        Assert.Equal(P("ash"), pike.FromOrdinal);
        Assert.Equal(P("ash:1"), pike.FromOrdinal);
    }

    [Fact]
    public void SearchFindsAliasesAndSections()
    {
        Assert.Equal("Kessan Reach", Index.Search("reach").First().EntityName);
        Assert.Contains(Index.Search("flechette"), h => h.EntityName == "Mara Voss" && h.SectionName == "Shoulder injury");
        Assert.Contains(Index.Search("phys* therapy"), h => h.SectionName == "Recovery");
        Assert.Contains(Index.Search("commander"), h => h.EntityName == "Mara Voss" && h.SectionName is null);
    }

    private long P(string position) =>
        _built.Structure.FindUniverse("starfall")!.Resolve(StoryPosition.Parse(position)).Resolved!.Point.Ordinal;

    /// <summary>One index of a Starfall copy, shared by the tests in this class.</summary>
    public sealed class Built : IDisposable
    {
        public Built()
        {
            Den = TempDen.Starfall();
            Index = DenIndex.Open(Den.Root);
            Structure = DenStructure.Load(Den.Root);
        }

        public TempDen Den { get; }

        public DenIndex Index { get; }

        public DenStructure Structure { get; }

        public void Dispose()
        {
            Index.Dispose();
            Den.Dispose();
        }
    }
}
