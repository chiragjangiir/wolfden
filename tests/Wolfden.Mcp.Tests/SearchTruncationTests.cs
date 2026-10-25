using System.Globalization;
using System.Text;
using Wolfden.Index;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// <c>search</c> with a filter fetches up to a raw cap of hits (1,000 for a
/// small limit) and filters them. It fetches one more than the cap, so
/// <c>truncated</c> is set only when there were raw hits past it (#130).
/// </summary>
public sealed class SearchTruncationTests : IDisposable
{
    private const string Word = "quillwhistle";
    private const int RawCap = 1000;

    private readonly TempDen _den = TempDen.Dracula();

    public void Dispose() => _den.Dispose();

    [Theory]
    [InlineData(RawCap, false)]
    [InlineData(RawCap + 1, true)]
    public void AFilteredSearchIsCutShortOnlyWhenRawHitsPassTheCap(int rawHits, bool truncated)
    {
        // One section of a character matches, and every section of a location:
        // rawHits in all, of which the type filter keeps the character's.
        var location = new StringBuilder("---\ntype: location\nname: Hall of Echoes\n---\n");
        for (var i = 1; i < rawHits; i++)
        {
            location.Append(CultureInfo.InvariantCulture, $"\n## Echo {i}\n\nA {Word} sounds.\n");
        }

        _den.Write("universes/dracula/entities/locations/hall-of-echoes.md", location.ToString());
        _den.Write("universes/dracula/entities/characters/piper.md", $"---\ntype: character\nname: Piper\n---\n\n## Habits\n\nPlays the {Word}.\n");
        var tools = new DenTools(_den.Root);

        var result = tools.Search(Word, type: "character", limit: 20);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("Piper", hit.Entity.Name);
        Assert.Equal(truncated, result.Truncated);
        using var index = DenIndex.Open(_den.Root);
        Assert.Equal(rawHits, index.Search(Word, 5000).Count);
    }
}
