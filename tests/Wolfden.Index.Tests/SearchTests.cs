using Microsoft.Data.Sqlite;

namespace Wolfden.Index.Tests;

/// <summary>FTS5 full-text search over entity names, aliases, leads, and sections.</summary>
public sealed class SearchTests : IClassFixture<SearchTests.Built>
{
    private readonly DenIndex _index;

    public SearchTests(Built built)
    {
        _index = built.Index;
    }

    [Fact]
    public void TheBundledSqliteHasFts5()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_compileoption_used('ENABLE_FTS5')";

        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void AccentsAndCaseDontMatter()
    {
        // The name, and the change log's "Zoe Ark".
        Assert.Equal([null, "Change log"], _index.Search("zoe").Select(h => h.SectionName));
        Assert.Equal([null, "Change log"], _index.Search("ZOË").Select(h => h.SectionName));
        Assert.All(_index.Search("zoe"), h => Assert.Equal("Zoë Arkwright", h.EntityName));
    }

    [Fact]
    public void AliasesAreSearchable()
    {
        var hit = Assert.Single(_index.Search("Quartermaster"));

        Assert.Equal("Zoë Arkwright", hit.EntityName);
        Assert.Null(hit.SectionName);
    }

    [Fact]
    public void EveryWordMustMatch()
    {
        Assert.Single(_index.Search("lighthouse keeper"));
        Assert.Empty(_index.Search("lighthouse submarine"));
    }

    [Fact]
    public void BookkeepingSectionsAreSearchable()
    {
        var hit = Assert.Single(_index.Search("renamed"));

        Assert.Equal("Change log", hit.SectionName);
    }

    [Fact]
    public void SubsectionsAreFoundWithTheirLines()
    {
        var hit = Assert.Single(_index.Search("tidewater"));

        Assert.Equal("Early years", hit.SectionName);
        Assert.Equal(13, hit.SectionLine);
        Assert.Contains("[tidewater]", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    public void EmptyQueriesFindNothing(string query)
    {
        Assert.Empty(_index.Search(query));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("AND")]
    [InlineData("title:zoe")]
    [InlineData("(lighthouse")]
    [InlineData("NEAR(")]
    [InlineData("\0")]
    [InlineData("zoe\0")]
    [InlineData("\u0001\u001f\u007f")]
    public void QuerySyntaxIsSearchedAsText(string query)
    {
        // No exception: Search quotes every word.
        _ = _index.Search(query);
    }

    [Fact]
    public void SearchMatchTakesFts5Syntax()
    {
        Assert.Single(_index.SearchMatch("title:quartermaster"));
        Assert.Equal(2, _index.SearchMatch("lighthouse OR tidewater").Count);
        Assert.Throws<SqliteException>(() => _index.SearchMatch("(unbalanced"));
    }

    [Fact]
    public void ControlCharactersAreSpaces()
    {
        Assert.Single(_index.Search("\0lighthouse\0keeper"));
    }

    [Fact]
    public void HitsSayWhetherTheyreCanon()
    {
        var lead = Assert.Single(_index.Search("lighthouse"));
        Assert.Equal("canon", lead.Status);
        Assert.True(lead.IsInCanon);
        Assert.False(lead.InComment);

        var idea = Assert.Single(_index.Search("smuggler"));
        Assert.Equal(("Ideas", "idea"), (idea.SectionName, idea.Status));
        Assert.False(idea.IsInCanon);

        var log = Assert.Single(_index.Search("renamed"));
        Assert.True(log.IsBookkeeping);
        Assert.False(log.IsInCanon);
    }

    [Fact]
    public void HtmlCommentsAreSearchableAndFlagged()
    {
        var hit = Assert.Single(_index.Search("harbour"));

        Assert.True(hit.InComment);
        Assert.Null(hit.SectionName);
        Assert.Empty(_index.SearchMatch("body:harbour"));
        Assert.Single(_index.SearchMatch("comments:harbour"));
    }

    [Fact]
    public void SnippetsDontStartWithTheLeadsBlankLine()
    {
        Assert.StartsWith("A [lighthouse]", Assert.Single(_index.Search("lighthouse")).Snippet, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-24")]
    public void MatchesAreRangesInTheTextSoTheNotesOwnBracketsAndControlCharactersCantPassForThem()
    {
        var hit = Assert.Single(_index.Search("watch"));

        Assert.Equal("Rung on the [[the Gull|keeper]]'s  watch  [twice].\n", hit.Text);
        var match = Assert.Single(hit.Matches);
        Assert.Equal("watch", hit.Text.Substring(match.Start, match.Length));
        Assert.Equal("Rung on the [[the Gull|keeper]]'s  [watch]  [twice].\n", hit.Snippet);

        // A word in the note's own brackets is a match like any other.
        var twice = Assert.Single(_index.Search("twice"));
        Assert.Equal("twice", twice.Text.Substring(twice.Matches[0].Start, twice.Matches[0].Length));
        Assert.Contains("[[twice]]", twice.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkedTextReadsMarksAndJoinsMatchesThatTouch()
    {
        var marked = MarkedText.FromMarks("a \u0001b\u0002\u0001c\u0002 [d]", MarkedText.Open, MarkedText.Close);

        Assert.Equal("a bc [d]", marked.Text);
        Assert.Equal([new TextMatch(2, 2)], marked.Matches);
        Assert.Equal("a [bc] [d]", marked.Bracketed());
        Assert.Equal("x\ty \nz", MarkedText.WithoutMarks("x\ty\u0001\nz"));
    }

    [Theory]
    [Trait("Scenario", "SC-MCP-30")]
    [InlineData("zoe", "Zoë Arkwright", true)]
    [InlineData("ARKW*", "Zoë Arkwright", true)]
    [InlineData("arkw", "Zoë Arkwright", false)]
    [InlineData("salt-marsh", "the Salt Marsh docks", true)]
    [InlineData("marsh-salt", "the Salt Marsh docks", false)]
    [InlineData("docks salt", "the Salt Marsh docks", true)]
    [InlineData("[[x]]", "[[x]]", true)]
    public void SearchWordsMatchFieldTextAsTheSearchTableDoes(string query, string text, bool matches)
    {
        var words = SearchWords.Parse(query);

        Assert.Equal(matches, words.Find(text).All(found => found.Count > 0));
    }

    [Fact]
    public void PrefixesMatchWithAStar()
    {
        Assert.Single(_index.Search("tidew*"));
        Assert.Empty(_index.Search("tidew"));
    }

    /// <summary>A small invented den, indexed once.</summary>
    public sealed class Built : IDisposable
    {
        public Built()
        {
            Den = TempDen.FromFiles(new Dictionary<string, string>
            {
                ["den.yaml"] = "format: 1\nname: Search Test\n",
                ["universes/coast/universe.md"] = "---\nname: Coast\n---\n",
                ["universes/coast/entities/characters/zoe-arkwright.md"] = """
                    ---
                    id: 01JB0000000000000000000001
                    type: character
                    name: Zoë Arkwright
                    aliases: [the Quartermaster]
                    status: canon
                    ---

                    A lighthouse keeper on the north coast. <!-- todo: check the harbour name -->

                    ## Background

                    ### Early years
                    Grew up in a tidewater village.

                    ## Change log
                    - Renamed from Zoe Ark.

                    ## Ideas
                    <!-- wolfden: status=idea -->
                    Maybe she was a smuggler.

                    """,
                // Brackets of its own, and the control characters a snippet marks matches with (#179).
                ["universes/coast/entities/items/brass-bell.md"] =
                    "---\ntype: item\nname: Brass Bell\n---\n\nRung on the [[the Gull|keeper]]'s \u0001watch\u0002 [twice].\n",
            });
            Index = DenIndex.Open(Den.Root);
        }

        public TempDen Den { get; }

        public DenIndex Index { get; }

        public void Dispose()
        {
            Index.Dispose();
            Den.Dispose();
        }
    }
}
