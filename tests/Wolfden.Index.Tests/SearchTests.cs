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

                    A lighthouse keeper on the north coast.

                    ## Background

                    ### Early years
                    Grew up in a tidewater village.

                    ## Change log
                    - Renamed from Zoe Ark.

                    """,
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
