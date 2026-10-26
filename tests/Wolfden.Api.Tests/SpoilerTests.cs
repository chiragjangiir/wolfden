using System.Net;
using System.Text.Json;
using Wolfden.Mcp;

namespace Wolfden.Api.Tests;

/// <summary>
/// As of a position, nothing the API answers names an entity the reader
/// hasn't met (#26): not its name, an alias, its key or id, or its path.
/// Only text the writer wrote in what the reader has met can hold such a
/// name (a link as written, a section's body, a field's value), and it's
/// left out of the check.
/// </summary>
public class SpoilerTests
{
    private const string MinaFile = "universes/dracula/books/dracula/entities/characters/mina-harker.md";
    private const string QuinceyFile = "universes/dracula/books/dracula/entities/characters/quincey-harker.md";
    private const string MaraFile = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
    private const string PikeFile = "universes/starfall/series/long-dark/books/ash/entities/characters/halden-pike.md";

    /// <summary>Members whose text the writer wrote, or (chapter titles, #26 M2) are listed whatever the position.</summary>
    private static readonly HashSet<string> Authored = ["text", "target", "heading", "label", "body", "lead", "value", "snippet", "query", "title"];

    /// <summary>
    /// The B1 case: Jonathan Harker (met) and Quincey Harker (met only in the
    /// note at the end) share the alias "Harker", and Mina's scar section
    /// links [[Harker]].
    /// </summary>
    private static TempDen DraculaWithASharedAlias()
    {
        var den = TempDen.Dracula();
        den.Write(QuinceyFile, den.Read(QuinceyFile).Replace("aliases: [little Quincey]", "aliases: [little Quincey, Harker]", StringComparison.Ordinal));
        den.Write(MinaFile, den.Read(MinaFile).Replace("It fades as the Count dies.", "It fades as the Count dies. [[Harker]] sees it go.", StringComparison.Ordinal));
        return den;
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-6")]
    public async Task AnAmbiguousLinksProblemNamesOnlyTheMatchesTheReaderHasMet()
    {
        using var den = DraculaWithASharedAlias();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        static JsonElement Link(JsonElement page) => page.GetProperty("entity").GetProperty("sections").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "The scar").GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("text").GetString() == "[[Harker]]");

        // The author's view names both.
        var full = Link(await server.GetJsonAsync("/api/entity?entity=Mina%20Harker", HttpStatusCode.OK));
        Assert.Equal("ambiguous", full.GetProperty("outcome").GetString());
        Assert.Contains("Quincey Harker", full.GetProperty("problem").GetString(), StringComparison.Ordinal);

        var early = Link(await server.GetJsonAsync("/api/entity?entity=Mina%20Harker&asOf=dracula:25", HttpStatusCode.OK));
        Assert.Equal("ambiguous", early.GetProperty("outcome").GetString());
        var problem = early.GetProperty("problem").GetString()!;
        Assert.DoesNotContain("Quincey", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("quincey", problem, StringComparison.Ordinal);
        Assert.Contains("'Jonathan Harker'", problem, StringComparison.Ordinal);
        Assert.Contains("others not met yet", problem, StringComparison.Ordinal);
        Assert.Equal(["Jonathan Harker"], early.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-6")]
    [InlineData("dracula", "dracula:1")]
    [InlineData("dracula", "dracula:5")]
    [InlineData("dracula", "dracula:12")]
    [InlineData("dracula", "dracula:25")]
    [InlineData("starfall", "ember:1")]
    [InlineData("starfall", "ember:2b")]
    [InlineData("starfall", "ash:1")]
    [InlineData("starfall", "ash:4")]
    public async Task NothingAsOfAPositionNamesAnEntityTheReaderHasntMet(string universe, string asOf)
    {
        using var den = universe == "dracula" ? DraculaWithASharedAlias() : TempDen.Starfall();
        if (universe == "starfall")
        {
            // A link by id to an entity met later (Halden Pike, from ash), with a label that doesn't name him.
            den.Write(MaraFile, den.Read(MaraFile).Replace(
                "Executive officer of the *Tessaly*.", "Executive officer of the *Tessaly*. Distrusts [[01JA7Q8M4XK3V9R2D5W8HN6TQC|the new hand]].", StringComparison.Ordinal)
                // And a field that links him by id (#179): search matches a link's name only once he's met.
                .Replace("  species: { value: human, from: \"ember:prologue\" }", "  species: { value: human, from: \"ember:prologue\" }\n  watch_mate: \"[[01JA7Q8M4XK3V9R2D5W8HN6TQC]]\"", StringComparison.Ordinal));
            // And a field only Pike has, so before ash only a note the reader hasn't met has it.
            den.Write(PikeFile, den.Read(PikeFile).Replace("from: \"ash\"\n", "from: \"ash\"\nfields:\n  debts: three stations\n", StringComparison.Ordinal));
        }

        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var tools = new DenTools(den.Root);
        var met = tools.CanonAsOf(universe, asOf, brief: true).Entities.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var all = tools.ListEntities(universe, limit: 1000).Entities;
        // A name or alias a met entity has too (two Lanterns; Jonathan and a later Harker) gives nothing away.
        var names = all.ToDictionary(e => e.Path, e => (IReadOnlyList<string>)[e.Name, .. tools.GetEntity(e.Path, universe).Entity!.Aliases ?? []]);
        var known = all.Where(e => met.Contains(e.Path)).SelectMany(e => names[e.Path]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmet = all.Where(e => !met.Contains(e.Path))
            .Select(e => (Entity: e, Names: names[e.Path].Where(n => !known.Contains(n)).ToList()))
            .ToList();
        Assert.NotEmpty(unmet);

        var answers = new List<(string Path, JsonElement Json)>();
        async Task Ask(string path) => answers.Add((path, await server.GetJsonAsync(path, HttpStatusCode.OK)));
        await Ask($"/api/hierarchy?universe={universe}&asOf={asOf}");
        await Ask($"/api/entities?universe={universe}&asOf={asOf}&limit=1000");
        await Ask($"/api/canon?universe={universe}&asOf={asOf}");
        foreach (var path in met)
        {
            await Ask($"/api/entity?universe={universe}&asOf={asOf}&entity={Uri.EscapeDataString(path)}");
        }

        // Search covers field values too (#179); the watch mate is the field that links someone met later.
        await Ask($"/api/fields?universe={universe}&asOf={asOf}");
        foreach (var (entity, unmetNames) in unmet)
        {
            await Ask($"/api/search?universe={universe}&asOf={asOf}&limit=200&query={Uri.EscapeDataString(entity.Name)}");
            foreach (var name in universe == "starfall" ? unmetNames : [])
            {
                // A name only a note the reader hasn't met has finds nothing in the field that links that note.
                var path = $"/api/search?universe={universe}&asOf={asOf}&limit=200&field=watch_mate&query={Uri.EscapeDataString(name)}";
                await Ask(path);
                Assert.True(answers[^1].Json.GetProperty("hits").GetArrayLength() == 0, $"{path} finds '{name}', whom the reader hasn't met by {asOf}.");
            }
        }

        // A field only notes the reader hasn't met have is refused just as a made-up one is (#179).
        var metFields = (await server.GetJsonAsync($"/api/fields?universe={universe}&asOf={asOf}", HttpStatusCode.OK))
            .GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToHashSet(StringComparer.Ordinal);
        var unmetFields = (await server.GetJsonAsync($"/api/fields?universe={universe}", HttpStatusCode.OK))
            .GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).Where(f => !metFields.Contains(f)).ToList();
        if (universe == "starfall" && asOf.StartsWith("ember", StringComparison.Ordinal))
        {
            Assert.Contains("debts", unmetFields);
        }

        var madeUp = await AnswerTo(server, $"/api/search?universe={universe}&asOf={asOf}&query=three&field=no_such_field");
        Assert.Equal(HttpStatusCode.BadRequest, madeUp.Status);
        foreach (var field in unmetFields)
        {
            var answer = await AnswerTo(server, $"/api/search?universe={universe}&asOf={asOf}&query=three&field={Uri.EscapeDataString(field)}");
            Assert.Equal(madeUp.Status, answer.Status);
            var (expected, actual) = (JsonDocument.Parse(madeUp.Body).RootElement, JsonDocument.Parse(answer.Body).RootElement);
            Assert.Equal(expected.GetProperty("code").GetString(), actual.GetProperty("code").GetString());
            Assert.Equal(
                expected.GetProperty("message").GetString(),
                actual.GetProperty("message").GetString()!.Replace($"'{field}'", "'no_such_field'", StringComparison.Ordinal));
        }

        foreach (var (path, json) in answers)
        {
            foreach (var (at, text) in Strings(json, "$"))
            {
                foreach (var (entity, unmetNames) in unmet)
                {
                    var leaked = unmetNames.Any(n => text == n || text.Contains($"'{n}'", StringComparison.Ordinal))
                        || text == entity.Key
                        || text.Contains(entity.Path, StringComparison.Ordinal)
                        || (entity.Id is { } id && text.Contains(id, StringComparison.Ordinal));
                    Assert.False(leaked, $"{path} at {at} names '{entity.Name}', whom the reader hasn't met by {asOf}: {text}");
                }
            }

            // A field hit's snippet is the value as written (the writer's), then the names of linked notes that matched (#179), which aren't.
            var hits = json.TryGetProperty("hits", out var h) ? h.EnumerateArray().ToList() : [];
            foreach (var hit in hits.Where(x => x.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String))
            {
                foreach (var text in new[] { hit.GetProperty("text").GetString()!, hit.GetProperty("snippet").GetString()! })
                {
                    var linkedNames = text.Split(" → ").Skip(1).Select(n => n.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal));
                    foreach (var (entity, unmetNames) in unmet)
                    {
                        Assert.False(
                            linkedNames.Any(n => unmetNames.Contains(n, StringComparer.Ordinal)),
                            $"{path}: a hit in field '{hit.GetProperty("field").GetString()}' names '{entity.Name}', whom the reader hasn't met by {asOf}: {text}");
                    }
                }
            }
        }

        if (universe == "starfall")
        {
            // The check above has something to check: once Pike is met, his name follows the watch mate's id.
            var after = await server.GetJsonAsync("/api/search?universe=starfall&asOf=ash:1&field=watch_mate&query=Halden%20Pike", HttpStatusCode.OK);
            Assert.EndsWith("→ Halden Pike", Assert.Single(after.GetProperty("hits").EnumerateArray()).GetProperty("text").GetString(), StringComparison.Ordinal);
        }
    }

    /// <summary>The status and body the server answers <paramref name="path"/> with, whatever the status.</summary>
    private static async Task<(HttpStatusCode Status, string Body)> AnswerTo(Server server, string path)
    {
        using var response = await server.Client.GetAsync(path, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Every string in <paramref name="json"/> that isn't text the writer wrote, with where it is.</summary>
    private static IEnumerable<(string At, string Text)> Strings(JsonElement json, string at)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.String:
                yield return (at, json.GetString()!);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in json.EnumerateArray())
                {
                    foreach (var s in Strings(item, $"{at}[{i++}]"))
                    {
                        yield return s;
                    }
                }

                break;
            case JsonValueKind.Object:
                foreach (var member in json.EnumerateObject())
                {
                    // A section's heading is the writer's text too.
                    var inSection = at.EndsWith(']') && at.Contains("sections[", StringComparison.Ordinal);
                    if (Authored.Contains(member.Name) || (inSection && member.Name is "name" or "headingPath"))
                    {
                        continue;
                    }

                    foreach (var s in Strings(member.Value, $"{at}.{member.Name}"))
                    {
                        yield return s;
                    }
                }

                break;
        }
    }
}
