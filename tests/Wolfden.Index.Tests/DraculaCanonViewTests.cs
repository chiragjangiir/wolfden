using Wolfden.Core.Structure;

namespace Wolfden.Index.Tests;

/// <summary>
/// Canon-as-of views of the Dracula sample keep later reveals out of early
/// chapters (format.md F-VIEW-1, §9.5): what the sample tags with a
/// <c>from</c> stays out of every view before it.
/// </summary>
public sealed class DraculaCanonViewTests : IClassFixture<DraculaCanonViewTests.Built>
{
    private const string Count = "01M4TF5EZZJ0FZ8PAAT1WY4GR4";
    private const string Lucy = "01M4TF5F08GDVZVWZ4JYQDBHZG";
    private const string Renfield = "01M4TF5F0TW3P7Z4MBYC9J8GAB";

    private readonly Built _built;

    public DraculaCanonViewTests(Built built)
    {
        _built = built;
    }

    [Theory]
    [InlineData("dracula")]
    [InlineData("dracula:1")]
    [InlineData("dracula:2")]
    [InlineData("dracula:3")]
    [InlineData("dracula:4")]
    public void AnEarlyViewLeaksNothingAboutLucyRenfieldOrWhatTheCountIs(string position)
    {
        var view = View(position);

        // Lucy and Renfield enter canon at chapter 5, and what the Count is
        // comes out in chapter 18. No entity of the view, book or universe
        // scope, mentions them before that.
        Assert.DoesNotContain(view.Entities, e => e.Id is Lucy or Renfield);
        foreach (var term in new[] { "Lucy", "lucy-westenra", "Westenra", "Renfield", "renfield", "Voivode", "vampire", "Vampire", "Un-Dead" })
        {
            Assert.DoesNotContain(term, Text(view), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("dracula:1")]
    [InlineData("dracula:4")]
    [InlineData("dracula:17")]
    public void AnEarlyViewLeaksNothingAboutTheCountsNature(string position)
    {
        var count = View(position).Entities.Single(e => e.Id == Count);

        // species, born, and the Voivode subsection are known from chapter
        // 18, and died from chapter 27 (F-TMP-8).
        Assert.DoesNotContain(count.Fields, f => f.Name is "species" or "born" or "died");
        Assert.DoesNotContain(count.Sections, s => s.Name == "Who he was");
        Assert.DoesNotContain("Voivode", Text(count), StringComparison.Ordinal);
        Assert.DoesNotContain("Un-Dead", Text(count), StringComparison.Ordinal);
        Assert.DoesNotContain("vampire", Text(count), StringComparison.Ordinal);
        Assert.Equal(["role", "gender", "pronouns", "skills", "heritage"], count.Fields.Select(f => f.Name));
    }

    [Fact]
    public void MinasMarriageAndVanHelsingStayOutUntilChapterNine()
    {
        const string mina = "01M4TF5F0B7KDTSAS8D2NYT37J";
        foreach (var chapter in Enumerable.Range(1, 8))
        {
            var text = Text(View($"dracula:{chapter}").Entities.Single(e => e.Id == mina));
            foreach (var term in new[] { "married", "marries", "husband", "Madam Mina", "Van Helsing", "Buda-Pesth" })
            {
                Assert.DoesNotContain(term, text, StringComparison.Ordinal);
            }
        }

        Assert.Contains("She marries Jonathan", Text(View("dracula:9").Entities.Single(e => e.Id == mina)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCountsNatureArrivesWhereTheSampleTagsIt()
    {
        var at18 = View("dracula:18").Entities.Single(e => e.Id == Count);
        var species = at18.Fields.Single(f => f.Name == "species");
        Assert.Equal("\"vampire (Un-Dead)\"", species.ValueJson);
        Assert.True(species.Pinned);
        Assert.Contains(at18.Fields, f => f.Name == "born");
        Assert.DoesNotContain(at18.Fields, f => f.Name == "died");
        Assert.Contains("Voivode", at18.Sections.Single(s => s.Name == "Who he was").Body, StringComparison.Ordinal);

        var atEnd = View(null).Entities.Single(e => e.Id == Count);
        Assert.Contains(atEnd.Fields, f => f.Name == "died");
    }

    [Fact]
    public void LucyAppearsAtChapterFiveWithoutHerFate()
    {
        var lucy = View("dracula:5").Entities.Single(e => e.Id == Lucy);

        Assert.Equal("\"Mina's friend, courted by three men at once.\"", lucy.Fields.Single(f => f.Name == "role").ValueJson);
        Assert.DoesNotContain(lucy.Fields, f => f.Name == "died");
        Assert.DoesNotContain(lucy.Sections, s => s.Name is "Arc" or "Death" or "The Un-Dead" or "Rest" or "Decline");
        Assert.Equal(["Overview", "Appearance", "Want", "Relationships"], lucy.Sections.Select(s => s.Name));
    }

    [Fact]
    public void LucysUnDeadSubsectionEndsAtItsUntil()
    {
        string[] Arc(string position) => View(position).Entities.Single(e => e.Id == Lucy)
            .Sections.Where(s => s.HeadingPath[0] == "Arc").Select(s => s.Name).ToArray();

        Assert.Equal(["Arc", "Death"], Arc("dracula:13"));
        Assert.Equal(["Arc", "Death", "The Un-Dead"], Arc("dracula:14"));
        Assert.Equal(["Arc", "Death", "The Un-Dead", "Rest"], Arc("dracula:16"));
        Assert.Equal(["Arc", "Death", "Rest"], Arc("dracula:17"));
        Assert.Equal(["Arc", "Death", "Rest"], Arc("dracula:27"));
    }

    [Fact]
    public void NoViewHoldsAnythingThatStartsAfterItsPosition()
    {
        var universe = _built.Structure.FindUniverse("dracula")!;
        foreach (var filter in CanonCrossCheckTests.Filters(universe))
        {
            foreach (var position in CanonCrossCheckTests.Positions(universe))
            {
                CanonCrossCheckTests.NothingStartsAfter(universe, _built.Index.CanonAsOf("dracula", position, filter));
            }
        }
    }

    [Fact]
    public void TheWholeUniverseViewHoldsTheBooksEntities()
    {
        // F-VIEW-1: a view covers every scope, so the one book's entities
        // are in the unfiltered view, after the universe's places.
        var all = View(null);
        var book = _built.Index.CanonAsOf("dracula", null, CanonFilter.ForBook("dracula"));

        Assert.Contains(all.Entities, e => e.Name == "London" && e.ScopeKind == "universe");
        Assert.Contains(all.Entities, e => e.Id == Count && e.ScopeKind == "book");
        Assert.Equal(["universe", "book"], all.Entities.Select(e => e.ScopeKind).Distinct());
        Assert.Equal(all.Entities.Select(e => e.Key), book.Entities.Select(e => e.Key));
    }

    private CanonView View(string? position) => _built.Index.CanonAsOf("dracula", position);

    private static string Text(CanonView view) => string.Join("\n", view.Entities.Select(Text));

    private static string Text(CanonEntity e) => string.Join("\n",
        new[] { e.Name, e.Title ?? "", e.Lead }
            .Concat(e.Fields.Select(f => f.ValueJson))
            .Concat(e.Sections.SelectMany(s => s.HeadingPath.Append(s.Body))));

    /// <summary>One index of a Dracula copy, shared by the tests in this class.</summary>
    public sealed class Built : IDisposable
    {
        public Built()
        {
            Den = TempDen.Dracula();
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
