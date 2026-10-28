using System.Net;
using System.Text;
using System.Text.Json;

namespace Wolfden.Api.Tests;

/// <summary>
/// Creating notes from the web UI (#29): <c>GET /api/types</c> and
/// <c>POST /api/entity/create</c>, through <c>create_entity</c>'s code, over
/// a copy of the Dracula sample and the invented Starfall den (a den-local
/// pack, and a second universe for pinning).
/// </summary>
public sealed class CreateEndpointTests : IDisposable
{
    private const string BookFile = "universes/dracula/books/dracula/book.md";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly List<TempDen> _dens = [];

    public void Dispose()
    {
        foreach (var den in _dens)
        {
            den.Dispose();
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-67")]
    public async Task TheTypesAreTheDensEnabledTypesBuiltInAndDenLocal()
    {
        var den = Den(TempDen.Starfall());
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var types = (await server.GetJsonAsync("/api/types", HttpStatusCode.OK)).GetProperty("types").EnumerateArray().ToList();

        var keys = types.Select(t => t.GetProperty("key").GetString()).ToList();
        Assert.Equal(["character", "event", "faction", "idea", "item", "location", "note", "ship", "thread"], keys.Order(StringComparer.Ordinal));
        var ship = types.Single(t => t.GetProperty("key").GetString() == "ship");
        Assert.Equal("Ship", ship.GetProperty("name").GetString());
        Assert.Equal("ships", ship.GetProperty("plural").GetString());
        Assert.Equal("starfall", ship.GetProperty("pack").GetString());
        Assert.False(ship.GetProperty("builtIn").GetBoolean());
        var character = types.Single(t => t.GetProperty("key").GetString() == "character");
        Assert.True(character.GetProperty("builtIn").GetBoolean());

        // By display name, so a menu reads in order.
        var names = types.Select(t => t.GetProperty("name").GetString()!).ToList();
        Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), names);

        // It takes no parameters.
        using var extra = await server.Client.GetAsync("/api/types?universe=starfall", Token);
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-67")]
    public async Task ANewNoteIsMadeFromItsTemplateWhereTheWriterSaysAndTheAnswerNamesIt()
    {
        var den = Den(TempDen.Dracula());
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var (code, made) = await PostAsync(server, "/api/entity/create", new { type = "character", name = "Mina's Aunt", universe = "dracula", book = "dracula" });

        Assert.Equal(HttpStatusCode.Created, code);
        const string Path = "universes/dracula/books/dracula/entities/characters/mina-s-aunt.md";
        Assert.Equal(Path, made.GetProperty("path").GetString());
        var entity = made.GetProperty("entity");
        Assert.Equal("Mina's Aunt", entity.GetProperty("name").GetString());
        Assert.Equal("character", entity.GetProperty("type").GetString());
        Assert.Equal("book", entity.GetProperty("scopeKind").GetString());
        Assert.Equal("dracula", entity.GetProperty("scopeKey").GetString());
        Assert.Equal(JsonValueKind.Null, entity.GetProperty("from").ValueKind);
        Assert.True(made.GetProperty("files").EnumerateArray().Single().GetProperty("created").GetBoolean());
        Assert.Contains(made.GetProperty("effects").EnumerateArray(), e => e.GetString()!.Contains("It has no 'from'", StringComparison.Ordinal));

        var text = den.Read(Path);
        Assert.StartsWith("---\nid: ", text, StringComparison.Ordinal);
        Assert.Contains("\ntype: character\nname: Mina's Aunt\nstatus: draft\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Overview\n", text, StringComparison.Ordinal);

        // The page navigates by the entity's key, which the read API answers to.
        var page = await server.GetJsonAsync($"/api/entity?entity={Uri.EscapeDataString(entity.GetProperty("key").GetString()!)}", HttpStatusCode.OK);
        Assert.Equal("Mina's Aunt", page.GetProperty("entity").GetProperty("name").GetString());

        // The same name again gets -2; the universe is the default, and a position sets 'from'.
        var (again, second) = await PostAsync(server, "/api/entity/create", new { type = "character", name = "Mina's Aunt", from = "dracula:5" });
        Assert.Equal(HttpStatusCode.Created, again);
        Assert.Equal("universes/dracula/entities/characters/mina-s-aunt.md", second.GetProperty("path").GetString());
        Assert.Equal("dracula:5", second.GetProperty("entity").GetProperty("from").GetString());
        Assert.Contains("\nstatus: draft\nfrom: ", den.Read("universes/dracula/entities/characters/mina-s-aunt.md"), StringComparison.Ordinal);

        var (third, thirdJson) = await PostAsync(server, "/api/entity/create", new { type = "character", name = "Mina's Aunt", book = "dracula" });
        Assert.Equal(HttpStatusCode.Created, third);
        Assert.Equal("universes/dracula/books/dracula/entities/characters/mina-s-aunt-2.md", thirdJson.GetProperty("path").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-68")]
    public async Task ACreateInAFinalBookOrOfAnUnknownTypeOrMalformedIsRefusedAndWritesNothing()
    {
        var den = Den(TempDen.Dracula());
        den.Write(BookFile, den.Read(BookFile).Replace("\nstatus: draft\n", "\nstatus: final\n", StringComparison.Ordinal));
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var characters = System.IO.Path.Combine(den.Root, "universes", "dracula", "books", "dracula", "entities", "characters");
        var before = Directory.GetFiles(characters).Length;

        var (frozen, frozenJson) = await PostAsync(server, "/api/entity/create", new { type = "character", name = "Someone", book = "dracula" });
        Assert.Equal(HttpStatusCode.Conflict, frozen);
        Assert.Equal("frozen", frozenJson.GetProperty("code").GetString());
        Assert.Contains("final (frozen)", frozenJson.GetProperty("message").GetString(), StringComparison.Ordinal);

        var (unknown, unknownJson) = await PostAsync(server, "/api/entity/create", new { type = "spaceship", name = "Someone" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown);
        Assert.Equal("unknown_type", unknownJson.GetProperty("code").GetString());
        Assert.Contains("character", unknownJson.GetProperty("message").GetString(), StringComparison.Ordinal);

        var (noName, noNameJson) = await PostAsync(server, "/api/entity/create", new { type = "character", name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, noName);
        Assert.Equal("bad_argument", noNameJson.GetProperty("code").GetString());

        var (both, _) = await PostAsync(server, "/api/entity/create", new { type = "character", name = "Someone", series = "x", book = "dracula" });
        Assert.Equal(HttpStatusCode.BadRequest, both);

        var (extra, extraJson) = await PostAsync(server, "/api/entity/create", new { type = "character", name = "Someone", slug = "someone" });
        Assert.Equal(HttpStatusCode.BadRequest, extra);
        Assert.Contains("doesn't take 'slug'", extraJson.GetProperty("message").GetString(), StringComparison.Ordinal);

        var (noBook, noBookJson) = await PostAsync(server, "/api/entity/create", new { type = "character", name = "Someone", book = "sequel" });
        Assert.Equal(HttpStatusCode.NotFound, noBook);
        Assert.Equal("unknown_scope", noBookJson.GetProperty("code").GetString());

        // A 'from' that doesn't resolve (F-POS-4) would leave the note out of every view: refused.
        foreach (var from in new[] { "dracul:5", "dracula:999", "not a position" })
        {
            var (badFrom, badFromJson) = await PostAsync(server, "/api/entity/create", new { type = "character", name = "Someone", from });
            Assert.Equal(HttpStatusCode.BadRequest, badFrom);
            Assert.Equal("bad_position", badFromJson.GetProperty("code").GetString());
        }

        // A name that would break its heading line, or runs on.
        foreach (var name in new[] { "x\u2028## Injected", "x\u2029y", new string('a', 201) })
        {
            var (badName, badNameJson) = await PostAsync(server, "/api/entity/create", new { type = "character", name });
            Assert.Equal(HttpStatusCode.BadRequest, badName);
            Assert.Equal("bad_argument", badNameJson.GetProperty("code").GetString());
        }

        var (form, _) = await PostAsync(server, "/api/entity/create", "type=character&name=Someone", "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form);

        using var get = await server.Client.GetAsync("/api/entity/create", Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        Assert.Equal("POST", get.Content.Headers.Allow.Single());
        using var post = await server.Client.PostAsync("/api/types", new StringContent("{}", Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);

        Assert.Equal(before, Directory.GetFiles(characters).Length);
        Assert.False(Directory.Exists(System.IO.Path.Combine(den.Root, "universes", "dracula", "entities", "characters", "someone.md")));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-68")]
    public async Task APinnedServerCreatesOnlyInItsUniverse()
    {
        var den = Den(TempDen.Starfall());
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { PinnedUniverse = "starfall" });

        var (other, _) = await PostAsync(server, "/api/entity/create", new { type = "ship", name = "Drifter", universe = "drift" });
        Assert.Equal(HttpStatusCode.BadRequest, other);
        Assert.False(Directory.Exists(System.IO.Path.Combine(den.Root, "universes", "drift", "entities")));

        var (code, made) = await PostAsync(server, "/api/entity/create", new { type = "ship", name = "The Long Quiet" });
        Assert.Equal(HttpStatusCode.Created, code);
        Assert.Equal("universes/starfall/entities/ships/the-long-quiet.md", made.GetProperty("path").GetString());
        Assert.Equal("starfall", made.GetProperty("entity").GetProperty("universe").GetString());
    }

    // ------------------------------------------------------------ helpers

    private TempDen Den(TempDen den)
    {
        _dens.Add(den);
        return den;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> PostAsync(Server server, string path, object body, string contentType = "application/json")
    {
        using var content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, contentType);
        using var response = await server.Client.PostAsync(path, content, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
}
