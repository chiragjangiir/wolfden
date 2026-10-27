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
    [InlineData("cafe", "Café", true)]
    [InlineData("resume", "re\u0301sume\u0301", true)]
    [InlineData("ab", "a\u0301b", true)]
    [InlineData("ab", "a\u0305b", false)]
    [InlineData("a b", "a\u0305b", true)]
    [InlineData("ab", "a\u0336b", false)]
    [InlineData("a", "a\u0336b", true)]
    [InlineData("ab", "a\u0331b", true)]
    [InlineData("ab", "a\u0332b", false)]
    [InlineData("o", "\u01A1", true)]
    [InlineData("senor", "SEÑOR", true)]
    [InlineData("nguyen", "Nguyễn", true)]
    [InlineData("елка", "ёлка", false)]
    [InlineData("ЁЛКА", "ёлка", true)]
    [InlineData("иод", "йод", false)]
    [InlineData("αλφα", "άλφα", false)]
    [InlineData("か", "が", false)]
    [InlineData("שלום", "שָׁלוֹם", false)]
    [InlineData("ह", "हिन्दी", true)]
    [InlineData("हिन्दी", "हिन्दी", true)]
    public void SearchWordsMatchFieldTextAsTheSearchTableDoes(string query, string text, bool matches)
    {
        var words = SearchWords.Parse(query);

        Assert.Equal(matches, words.Find(text).All(found => found.Count > 0));
        Assert.Equal(matches, Fts5Matches(query, text));
    }

    [Theory]
    [InlineData("lighthouses", "")]
    [InlineData("lighthouse", "")]
    [InlineData("lighthouses", "x")]
    public void ALongFieldValueIsCutWithoutSplittingASurrogatePair(string word, string before)
    {
        // No spaces to cut at, so the cut falls where it may: after a word of odd or even length.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F56F", 120));
        var text = before + emoji + "-" + word + emoji;
        var at = text.IndexOf(word, StringComparison.Ordinal);

        var cut = Wolfden.Index.Reading.FieldSearch.Cut(text, [new TextMatch(at, word.Length)]);

        for (var i = 0; i < cut.Text.Length; i++)
        {
            Assert.False(char.IsLowSurrogate(cut.Text[i]) && (i == 0 || !char.IsHighSurrogate(cut.Text[i - 1])), $"A lone low surrogate at {i}.");
            Assert.False(char.IsHighSurrogate(cut.Text[i]) && (i == cut.Text.Length - 1 || !char.IsLowSurrogate(cut.Text[i + 1])), $"A lone high surrogate at {i}.");
        }

        var match = Assert.Single(cut.Matches);
        Assert.Equal(word, cut.Text.Substring(match.Start, match.Length));
    }

    /// <summary>Whether SQLite's own FTS5 table, with the index's tokenizer, finds <paramref name="query"/> in <paramref name="text"/>, read as DenIndex.Search reads it.</summary>
    /// <remarks>
    /// The parity cases were written against the SQLite bundled with
    /// Microsoft.EntityFrameworkCore.Sqlite 10.0.12 (SQLitePCLRaw's
    /// e_sqlite3): SQLite 3.53.3. They run against whatever SQLite is
    /// bundled, so an upgrade that changes the tokenizer fails here.
    /// </remarks>
    private static bool Fts5Matches(string query, string text)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE t USING fts5(x, tokenize = 'unicode61 remove_diacritics 2'); INSERT INTO t VALUES ($text);";
        command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT count(*) FROM t WHERE t MATCH $query";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$query", SearchQuery.ToFts5(query));
        return (long)command.ExecuteScalar()! > 0;
    }

    [Fact]
    public void PrefixesMatchWithAStar()
    {
        Assert.Single(_index.Search("tidew*"));
        Assert.Empty(_index.Search("tidew"));
    }

    [Theory]
    [InlineData("\"silver key\"", new[] { "Old Lantern" })]
    [InlineData("silver key", new[] { "Old Lantern", "Rusted Chain" })]
    [InlineData("\"key silver\"", new string[0])]
    [InlineData("\"SÍLVER KEY\"", new[] { "Old Lantern" })]
    [InlineData("\"silver key\" iron", new[] { "Old Lantern" })]
    [InlineData("iron \"silver key\"", new[] { "Old Lantern" })]
    [InlineData("\"silver key\" chain", new string[0])]
    [InlineData("\"silver key\"\"iron gate\"", new[] { "Old Lantern" })]
    [InlineData("\"silver ke\"", new string[0])]
    [InlineData("\"silver ke*\"", new[] { "Old Lantern" })]
    [InlineData("\"silver ke\"*", new[] { "Old Lantern" })]
    [InlineData("\"silver ke * \"", new[] { "Old Lantern" })]
    [InlineData("\"silver* key\"", new[] { "Old Lantern" })]
    [InlineData("\"rusted key", new[] { "Rusted Chain" })]
    [InlineData("\"rusted key\"", new string[0])]
    [InlineData("\"\" chain", new[] { "Rusted Chain" })]
    [InlineData("chain \"", new[] { "Rusted Chain" })]
    [InlineData("\"key, rusted\"", new[] { "Rusted Chain" })]
    public void AQuotedQueryIsAPhrase(string query, string[] names)
    {
        // "Old Lantern" has "silver-key", side by side; "Rusted Chain" has both words, apart (#185).
        Assert.Equal(names, _index.Search(query).Select(h => h.EntityName).Distinct().Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    [InlineData("\"*\"")]
    [InlineData("\"-\"")]
    [InlineData("\"\"\"")]
    public void EmptyQuotesFindNothing(string query)
    {
        Assert.Empty(_index.Search(query));
    }

    [Fact]
    public void ASnippetMarksThePhrase()
    {
        var hit = Assert.Single(_index.Search("\"silver key\""));

        Assert.Contains("[silver-key]", hit.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("[silver]", hit.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("[key]", hit.Snippet, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"silver key\"", "a silver-key", true)]
    [InlineData("\"silver key\"", "a key of silver", false)]
    [InlineData("\"silver ke*\"", "Silver Keys", true)]
    [InlineData("\"silver ke\"*", "Silver Keys", true)]
    [InlineData("\"silver ke\"", "Silver Keys", false)]
    [InlineData("\"cafe noir\" x", "Café Noir, x", true)]
    [InlineData("\"silver key", "key of silver", true)]
    [InlineData("\"\" silver", "key of silver", true)]
    public void PhrasesMatchFieldTextAsTheSearchTableDoes(string query, string text, bool matches)
    {
        var words = SearchWords.Parse(query);

        Assert.Equal(matches, words.Find(text).All(found => found.Count > 0));
        Assert.Equal(matches, Fts5Matches(query, text));
    }

    [Fact]
    public void AFieldPhraseMatchIsOneRange()
    {
        var words = SearchWords.Parse("\"silver key\" gate");
        var found = words.Find("the silver key, the gate, a key of silver");

        Assert.Equal(2, words.Count);
        Assert.Equal([new TextMatch(4, 10)], found[0]);
        Assert.Equal([new TextMatch(20, 4)], found[1]);
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

                // Phrase search (#185): the words side by side, and the same words apart.
                ["universes/coast/entities/items/old-lantern.md"] =
                    "---\ntype: item\nname: Old Lantern\n---\n\nIts silver-key opens the iron gate. A key of silver hangs by it.\n",
                ["universes/coast/entities/items/rusted-chain.md"] =
                    "---\ntype: item\nname: Rusted Chain\n---\n\nA key, rusted, on a silver chain.\n",
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
