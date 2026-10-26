using System.Net;
using System.Text.Json;
using Wolfden.Mcp;

namespace Wolfden.Api.Tests;

/// <summary>
/// <c>/api/search</c> in field values, its <c>field</c> filter, and
/// <c>/api/fields</c> (#179); and the plain text and match ranges each hit
/// carries, so the page needn't guess which brackets are the search's.
/// </summary>
public class FieldSearchEndpointTests
{
    private const string MaraFile = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
    private const string PikeFile = "universes/starfall/series/long-dark/books/ash/entities/characters/halden-pike.md";

    /// <summary>Halden Pike's id. The reader meets him in <c>ash</c>.</summary>
    private const string PikeId = "01JA7Q8M4XK3V9R2D5W8HN6TQC";

    /// <summary>
    /// Starfall, with Mara (met at ember:1) given a home and a watch mate
    /// linked by id to Halden Pike, and Pike given a field of his own.
    /// </summary>
    private static TempDen Den()
    {
        var den = TempDen.Starfall();
        den.Write(MaraFile, den.Read(MaraFile).Replace(
            "  species: { value: human, from: \"ember:prologue\" }",
            $"  species: {{ value: human, from: \"ember:prologue\" }}\n  home: Saltmarsh Docks\n  watch_mate: \"[[{PikeId}]]\"",
            StringComparison.Ordinal));
        den.Write(PikeFile, den.Read(PikeFile).Replace("from: \"ash\"\n", "from: \"ash\"\nfields:\n  debts: three stations\n", StringComparison.Ordinal));
        return den;
    }

    private static List<JsonElement> Hits(JsonElement json) => json.GetProperty("hits").EnumerateArray().ToList();

    [Fact]
    [Trait("Scenario", "SC-WEB-24")]
    public async Task AFieldSearchGivesWhatTheToolGivesWithTextAndMatchRanges()
    {
        using var den = Den();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var tools = new DenTools(den.Root);

        var json = await server.GetJsonAsync("/api/search?universe=starfall&query=saltmarsh&field=home", HttpStatusCode.OK);
        SameAsMcp.Assert(tools.Search("saltmarsh", "starfall", field: "home"), json);
        var hit = Assert.Single(Hits(json));
        Assert.Equal("home", hit.GetProperty("field").GetString());
        Assert.Equal("Saltmarsh Docks", hit.GetProperty("text").GetString());
        var match = Assert.Single(hit.GetProperty("matches").EnumerateArray());
        Assert.Equal(0, match.GetProperty("start").GetInt32());
        Assert.Equal(9, match.GetProperty("length").GetInt32());

        // Ordinary search gives the same as the tool too, field hits included.
        SameAsMcp.Assert(tools.Search("docks", "starfall"), await server.GetJsonAsync("/api/search?universe=starfall&query=docks", HttpStatusCode.OK));

        // A field no note has is a bad argument.
        var error = await server.GetJsonAsync("/api/search?universe=starfall&query=saltmarsh&field=hoem", HttpStatusCode.BadRequest);
        Assert.Equal("bad_argument", error.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-24")]
    public async Task MatchRangesTellTheSearchsMarksFromTheNotesOwnBrackets()
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        // Tessaly's overview links [[Mara Voss]]; the snippet brackets "Mara" inside the link's own brackets.
        var hit = Hits(await server.GetJsonAsync("/api/search?universe=starfall&query=mara", HttpStatusCode.OK))
            .First(h => h.GetProperty("text").GetString()!.Contains("[[Mara Voss]]", StringComparison.Ordinal));
        Assert.Contains("[[[Mara] Voss]]", hit.GetProperty("snippet").GetString(), StringComparison.Ordinal);
        var text = hit.GetProperty("text").GetString()!;
        Assert.Contains("[[Mara Voss]]", text, StringComparison.Ordinal);
        var match = Assert.Single(hit.GetProperty("matches").EnumerateArray());
        Assert.Equal("Mara", text.Substring(match.GetProperty("start").GetInt32(), match.GetProperty("length").GetInt32()));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-25")]
    public async Task AsOfAPositionALinkMatchesByNameOnlyWhenTheReaderHasMetItsNote()
    {
        using var den = Den();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        // Before ash the reader hasn't met Pike: neither search nor the one-field search confirms his name.
        Assert.Empty(Hits(await server.GetJsonAsync("/api/search?universe=starfall&query=Halden%20Pike&asOf=ember:3", HttpStatusCode.OK)));
        Assert.Empty(Hits(await server.GetJsonAsync("/api/search?universe=starfall&query=pike&field=watch_mate&asOf=ember:3", HttpStatusCode.OK)));

        // The id as written is the writer's text in a note the reader has met, so it matches.
        Assert.Single(Hits(await server.GetJsonAsync($"/api/search?universe=starfall&query={PikeId}&field=watch_mate&asOf=ember:3", HttpStatusCode.OK)));

        // Once he's met, and in the full view, his name and alias match, and the snippet names him.
        foreach (var asOf in new[] { "&asOf=ash:1", "" })
        {
            var hit = Assert.Single(Hits(await server.GetJsonAsync($"/api/search?universe=starfall&query=pike&field=watch_mate{asOf}", HttpStatusCode.OK)));
            Assert.Equal($"[[{PikeId}]] → Halden Pike", hit.GetProperty("text").GetString());
            Assert.Equal($"[[{PikeId}]] → Halden [Pike]", hit.GetProperty("snippet").GetString());
        }

        // An entry counts only where it's in effect: Mara's posting to Kessan Reach starts at ash:4.
        Assert.Empty(Hits(await server.GetJsonAsync("/api/search?universe=starfall&query=kessan&field=posting&asOf=ash:3", HttpStatusCode.OK)));
        Assert.Single(Hits(await server.GetJsonAsync("/api/search?universe=starfall&query=kessan&field=posting&asOf=ash:4", HttpStatusCode.OK)));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-25")]
    public async Task TheFieldsAreThoseNotesGiveValuesForAndAsOfAPositionOnlyThoseInView()
    {
        using var den = Den();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        static List<string?> Fields(JsonElement json) => json.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToList();
        var all = Fields(await server.GetJsonAsync("/api/fields?universe=starfall", HttpStatusCode.OK));
        Assert.Contains("debts", all);
        Assert.Contains("home", all);
        Assert.Equal(all.Order(StringComparer.Ordinal), all);

        var early = await server.GetJsonAsync("/api/fields?universe=starfall&asOf=ember:3", HttpStatusCode.OK);
        Assert.Equal("ember:3", early.GetProperty("position").GetString());
        Assert.DoesNotContain("debts", Fields(early));
        Assert.Contains("home", Fields(early));
        Assert.Contains("debts", Fields(await server.GetJsonAsync("/api/fields?universe=starfall&asOf=ash:1", HttpStatusCode.OK)));

        Assert.Equal("bad_argument", (await server.GetJsonAsync("/api/fields?universe=starfall&as_of=ash:1", HttpStatusCode.BadRequest)).GetProperty("code").GetString());
    }
}
