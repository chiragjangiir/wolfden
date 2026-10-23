using System.Text.Json;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// The read tools over the Dracula sample (#20): canon views keep later
/// reveals out, pinned canon comes first, and without a position a tool
/// gives the author's full view.
/// </summary>
public sealed class DraculaToolTests : IClassFixture<DraculaToolTests.Den>
{
    private const string Count = "01M4TF5EZZJ0FZ8PAAT1WY4GR4";
    private const string Lucy = "01M4TF5F08GDVZVWZ4JYQDBHZG";
    private const string Renfield = "01M4TF5F0TW3P7Z4MBYC9J8GAB";

    private readonly DenTools _tools;

    public DraculaToolTests(Den den)
    {
        _tools = den.Tools;
    }

    [Theory]
    [Trait("Scenario", "SC-MCP-1")]
    [InlineData("dracula")]
    [InlineData("dracula:1")]
    [InlineData("dracula:4")]
    public void CanonAsOfAnEarlyChapterLeaksNothingTheReaderMeetsLater(string position)
    {
        var result = _tools.CanonAsOf(position: position);

        Assert.Equal("dracula", result.Universe);
        Assert.Equal($"as of {position}", result.At);
        Assert.DoesNotContain(result.Entities, e => e.Id is Lucy or Renfield);
        var json = Json(result);
        foreach (var term in new[] { "Lucy", "Westenra", "Renfield", "Voivode", "vampire", "Un-Dead" })
        {
            Assert.DoesNotContain(term, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-1")]
    public void CanonAsOfMatchesTheIndexsViewEntityForEntity()
    {
        // The tool reuses the index's canon view; it doesn't make its own.
        foreach (var position in new[] { "dracula:1", "dracula:9", "dracula:18", "dracula:27" })
        {
            var result = _tools.CanonAsOf(position: position);
            using var index = Wolfden.Index.DenIndex.Open(_tools.Root);
            var view = index.CanonAsOf("dracula", position);
            Assert.Equal(view.Entities.Select(e => e.Key), result.Entities.Select(e => e.Key));
            Assert.Equal(view.Entities.Count, result.Count);
            Assert.Equal(
                view.Entities.SelectMany(e => e.Sections.Select(s => $"{e.Key}/{s.Line}")),
                result.Entities.SelectMany(e => e.Sections!.Select(s => $"{e.Key}/{s.Line}")));

            // Field names and values too, compared as JSON.
            Assert.Equal(
                view.Entities.SelectMany(e => e.Fields.Select(f => $"{e.Key}/{f.Name}={Compact(f.ValueJson)}/{f.Kind}")),
                result.Entities.SelectMany(e => e.Fields!.Select(f => $"{e.Key}/{f.Name}={f.Value.GetRawText()}/{f.Kind}")));
        }

        static string Compact(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetRawText();
        }
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-1")]
    public void CanonAsOfLeavesOutIdeasAndUndecidedQuestions()
    {
        var result = _tools.CanonAsOf();

        Assert.Equal("the end of the story: all canon so far", result.At);
        Assert.All(result.Entities, e => Assert.Contains(e.Status, new[] { "draft", "canon" }));
        Assert.All(result.Entities.SelectMany(e => e.Sections!), s => Assert.Contains(s.Status, new[] { "draft", "canon" }));
        Assert.DoesNotContain(result.Entities.SelectMany(e => e.Sections!), s => s.Name == "Open questions");
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-3")]
    public void CanonAsOfServesPinnedCanonFirst()
    {
        var result = _tools.CanonAsOf(position: "dracula:18");

        // Pinned canon comes before the rest, in the JSON a model reads too.
        var json = Json(result);
        Assert.True(json.IndexOf("\"pinned\":", StringComparison.Ordinal) < json.IndexOf("\"entities\":", StringComparison.Ordinal));

        var count = result.Pinned.Single(p => p.Key == Count);
        var species = Assert.Single(count.Fields);
        Assert.Equal("species", species.Name);
        Assert.Equal("vampire (Un-Dead)", species.Value.GetString());
        Assert.True(species.Pinned);
        Assert.Contains(result.Pinned, p => p.Name == "Rules of the Un-Dead");
        Assert.All(result.Pinned, p => Assert.All(p.Sections, s => Assert.True(s.Pinned)));

        // Pinned facts are also in place in the full view.
        Assert.Contains(result.Entities.Single(e => e.Id == Count).Fields!, f => f.Name == "species" && f.Pinned);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-3")]
    public void PinnedCanonKeepsToThePositionToo()
    {
        // The Count's species is pinned, but it's known from chapter 18.
        var early = _tools.CanonAsOf(position: "dracula:17");
        Assert.DoesNotContain(early.Pinned, p => p.Key == Count);
        Assert.DoesNotContain("Un-Dead", Json(early.Pinned), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-1")]
    public void CanonAsOfCanBeBriefAndNarrowedToAType()
    {
        var result = _tools.CanonAsOf(position: "dracula:9", type: "character", brief: true);

        Assert.NotEmpty(result.Entities);
        Assert.All(result.Entities, e =>
        {
            Assert.Equal("character", e.Type);
            Assert.Null(e.Fields);
            Assert.Null(e.Sections);
            Assert.Null(e.Lead);
        });
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-2")]
    public void GetEntityAsOfAChapterBeforeTheReaderMeetsItReturnsNothingAboutIt()
    {
        var result = _tools.GetEntity("Lucy", asOf: "dracula:4");

        Assert.False(result.InView);
        Assert.Null(result.Entity);
        Assert.Equal("canon_as_of", result.View);
        Assert.DoesNotContain("Westenra", Json(result), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-2")]
    public void GetEntityAsOfAChapterShowsOnlyWhatsKnownThen()
    {
        var lucy = _tools.GetEntity("Lucy", asOf: "dracula:5").Entity!;

        Assert.Equal(Lucy, lucy.Id);
        Assert.Equal("Mina's friend, courted by three men at once.", lucy.Fields!.Single(f => f.Name == "role").Value.GetString());
        Assert.DoesNotContain(lucy.Fields!, f => f.Name == "died");
        Assert.Equal(["Overview", "Appearance", "Want", "Relationships"], lucy.Sections!.Select(s => s.Name));

        var count = _tools.GetEntity("the Count", asOf: "dracula:17").Entity!;
        Assert.DoesNotContain(count.Fields!, f => f.Name is "species" or "born" or "died");
        Assert.Empty(count.Pinned!.Fields);
        Assert.DoesNotContain("Voivode", Json(count), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-3")]
    public void GetEntityServesPinnedFactsFirst()
    {
        var count = _tools.GetEntity("Count Dracula", asOf: "dracula:18").Entity!;
        Assert.Equal(["species"], count.Pinned!.Fields.Select(f => f.Name));

        var full = _tools.GetEntity(Count).Entity!;
        Assert.Equal(["species"], full.Pinned!.Fields.Select(f => f.Name));
        var json = Json(full);
        Assert.True(json.IndexOf("\"pinned\":", StringComparison.Ordinal) < json.IndexOf("\"fields\":", StringComparison.Ordinal));

        var mina = _tools.GetEntity("Mina").Entity!;
        Assert.NotEmpty(mina.Pinned!.Sections);
        Assert.All(mina.Pinned.Sections, s => Assert.True(s.Pinned));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-4")]
    public void GetEntityWithoutAPositionIsTheAuthorsFullView()
    {
        var result = _tools.GetEntity("the-counts-end");

        Assert.Equal("full", result.View);
        Assert.True(result.InView);
        var thread = result.Entity!;
        var questions = thread.Sections!.Where(s => s.HeadingPath[0] == "Open questions").ToList();
        Assert.Equal(["Open questions", "Destroyed", "Escaped"], questions.Select(s => s.Name));
        Assert.Equal(["undecided", "candidate", "candidate"], questions.Select(s => s.Status));

        // Every entry of a field that changes, each with its range.
        var lucy = _tools.GetEntity(Lucy).Entity!;
        var roles = lucy.Fields!.Where(f => f.Name == "role").ToList();
        Assert.Equal(["dracula:5", "dracula:8", "dracula:14", "dracula:16"], roles.Select(f => f.From));
        Assert.Contains(lucy.Fields!, f => f.Name == "died");
        Assert.Equal(["Lucy"], lucy.Aliases);
    }

    [Theory]
    [Trait("Scenario", "SC-MCP-4")]
    [InlineData("Count Dracula")]
    [InlineData("the Count")]
    [InlineData("count-dracula")]
    [InlineData(Count)]
    [InlineData("universes/dracula/books/dracula/entities/characters/count-dracula.md")]
    public void GetEntityTakesANameAnAliasAFileNameAnIdOrAPath(string name)
    {
        Assert.Equal(Count, _tools.GetEntity(name).Entity!.Id);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-5")]
    public void ListEntitiesFiltersByTypeStatusAndScope()
    {
        var all = _tools.ListEntities();
        var characters = _tools.ListEntities(type: "character");
        var places = _tools.ListEntities(type: "location");

        Assert.Equal(all.Count, all.Entities.Count);
        Assert.False(all.Truncated);
        Assert.All(characters.Entities, e => Assert.Equal("character", e.Type));
        Assert.Contains(characters.Entities, e => e.Id == Lucy && e.From == "dracula:5" && e.ScopeKind == "book");
        Assert.All(places.Entities, e => Assert.Equal("universe", e.ScopeKind));
        Assert.Equal(all.Count, _tools.ListEntities(book: "dracula").Count);

        // Every status, unless one is asked for.
        Assert.Equal(all.Count, all.Entities.Count(e => e.Status is "canon" or "draft") + all.Entities.Count(e => e.Status is not ("canon" or "draft")));
        Assert.All(_tools.ListEntities(status: "canon").Entities, e => Assert.Equal("canon", e.Status));

        var limited = _tools.ListEntities(limit: 3);
        Assert.Equal(3, limited.Entities.Count);
        Assert.True(limited.Truncated);
        Assert.Equal(3, limited.Count);
        Assert.Equal(all.Total, limited.Total);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-5")]
    public void SearchFindsNotesAndSaysWhatsCanon()
    {
        var result = _tools.Search("Voivode");

        Assert.NotEmpty(result.Hits);
        Assert.Contains(result.Hits, h => h.Entity.Id == Count && h.Section == "Who he was" && h.InCanon);
        Assert.Contains("[Voivode]", result.Hits.First(h => h.Entity.Id == Count).Snippet, StringComparison.Ordinal);

        // A word from an undecided question is found, but not as canon.
        var all = _tools.Search("Escaped", type: "thread");
        Assert.Contains(all.Hits, h => !h.InCanon && h.Status == "candidate");
        Assert.DoesNotContain(_tools.Search("Escaped", type: "thread", canonOnly: true).Hits, h => !h.InCanon);
        Assert.Empty(_tools.Search("Voivode", type: "location").Hits);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-8")]
    public void CheckReportsTheSamplesFindings()
    {
        var result = _tools.Check();

        Assert.True(result.Loaded);
        Assert.True(result.SpoilerChecks);
        Assert.Equal(0, result.Errors);
        Assert.True(result.Entities > 40);
        Assert.Equal(result.Errors + result.Warnings, result.Total);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ToolJson.Options);

    /// <summary>One copy of Dracula with a built index, shared by the tests in this class.</summary>
    public sealed class Den : IDisposable
    {
        private readonly TempDen _den = TempDen.Dracula();

        public Den()
        {
            Tools = new DenTools(_den.Root);
        }

        public DenTools Tools { get; }

        public void Dispose() => _den.Dispose();
    }
}
