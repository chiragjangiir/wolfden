using System.Net;
using System.Text.Json;
using Wolfden.Mcp;

namespace Wolfden.Api.Tests;

/// <summary>
/// The read endpoints the web UI builds on (#24 to #27), over the Dracula
/// sample and the invented Starfall den (two universes; a series of two
/// books, a standalone book, and a series with no books yet). Where an
/// endpoint answers what an MCP read tool answers, the answers are compared
/// member by member.
/// </summary>
public class ReadEndpointTests
{
    private const string MaraFile = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";

    [Fact]
    [Trait("Scenario", "SC-WEB-4")]
    public async Task TheHierarchyListsSeriesBooksAndChaptersInStoryOrderWithCountsPerScope()
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var json = await server.GetJsonAsync("/api/hierarchy?universe=starfall", HttpStatusCode.OK);

        Assert.Equal(1, json.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("position").ValueKind);
        var universe = json.GetProperty("universe");
        Assert.Equal("starfall", universe.GetProperty("key").GetString());
        Assert.Equal(["long-dark", "drift-war"], Keys(universe.GetProperty("series")));
        var longDark = universe.GetProperty("series")[0];
        Assert.Equal(["ember", "ash"], Keys(longDark.GetProperty("books")));
        Assert.Empty(Keys(universe.GetProperty("series")[1].GetProperty("books")));
        Assert.Equal(["a-quiet-moon"], Keys(universe.GetProperty("books")));

        var ember = longDark.GetProperty("books")[0];
        Assert.Equal("draft", ember.GetProperty("status").GetString());
        Assert.Equal("ember", ember.GetProperty("position").GetString());
        Assert.Equal(["prologue", "1", "2", "2b", "3"], Keys(ember.GetProperty("chapters")));
        var chapter = ember.GetProperty("chapters")[3];
        Assert.Equal("The Long Watch", chapter.GetProperty("title").GetString());
        Assert.Equal(3, chapter.GetProperty("number").GetInt64());
        Assert.Equal("ember:2b", chapter.GetProperty("position").GetString());

        // Counts are of the entities filed at each scope itself, by type.
        var counts = longDark.GetProperty("entities");
        Assert.Equal(2, counts.GetProperty("total").GetInt32());
        Assert.Equal(1, counts.GetProperty("byType").GetProperty("character").GetInt32());
        Assert.Equal(1, counts.GetProperty("byType").GetProperty("ship").GetInt32());
        Assert.Equal(2, longDark.GetProperty("books")[1].GetProperty("entities").GetProperty("total").GetInt32());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-4")]
    public async Task TheHierarchyAsOfAPositionCountsOnlyWhatTheReaderHasMet()
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        // A position settles the universe: only Starfall has a book 'ember'.
        var json = await server.GetJsonAsync("/api/hierarchy?asOf=ember:1", HttpStatusCode.OK);

        Assert.Equal("ember:1", json.GetProperty("position").GetString());
        var universe = json.GetProperty("universe");
        var ash = universe.GetProperty("series")[0].GetProperty("books")[1];
        Assert.Equal(0, ash.GetProperty("entities").GetProperty("total").GetInt32());
        Assert.Empty(ash.GetProperty("entities").GetProperty("byType").EnumerateObject());
        // The thread enters at ember:2; the location at ember:1.
        Assert.Equal(1, universe.GetProperty("entities").GetProperty("total").GetInt32());

        var later = await server.GetJsonAsync("/api/hierarchy?asOf=ash:1", HttpStatusCode.OK);
        Assert.Equal(2, later.GetProperty("universe").GetProperty("series")[0].GetProperty("books")[1].GetProperty("entities").GetProperty("total").GetInt32());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-4")]
    public async Task APinnedServerReadsOnlyItsUniverse()
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { PinnedUniverse = "drift" });

        var json = await server.GetJsonAsync("/api/hierarchy", HttpStatusCode.OK);
        Assert.Equal("drift", json.GetProperty("universe").GetProperty("key").GetString());
        Assert.Equal("drift", (await server.GetJsonAsync("/api/positions", HttpStatusCode.OK)).GetProperty("universe").GetString());

        var refused = await server.GetJsonAsync("/api/entities?universe=starfall", HttpStatusCode.BadRequest);
        Assert.Equal("bad_argument", refused.GetProperty("code").GetString());
        Assert.Contains("wolfden serve --universe", refused.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("bad_argument", (await server.GetJsonAsync("/api/search?query=tessaly&universe=*", HttpStatusCode.BadRequest)).GetProperty("code").GetString());
        // A position in another universe doesn't resolve in the pinned one.
        Assert.Equal("bad_position", (await server.GetJsonAsync("/api/canon?asOf=ember:1", HttpStatusCode.BadRequest)).GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-5")]
    public async Task PositionsListEveryChapterInStoryOrderWithTitles()
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var json = await server.GetJsonAsync("/api/positions?universe=starfall", HttpStatusCode.OK);

        var positions = json.GetProperty("positions").EnumerateArray().Select(p => p.GetProperty("position").GetString()).ToList();
        Assert.Equal(
            ["ember:prologue", "ember:1", "ember:2", "ember:2b", "ember:3", "ash:1", "ash:2", "ash:3", "ash:4", "ash:epilogue",
             "a-quiet-moon:1", "a-quiet-moon:2", "a-quiet-moon:2b"],
            positions);
        Assert.Equal(positions.Count, json.GetProperty("count").GetInt32());
        var prologue = json.GetProperty("positions")[0];
        Assert.Equal("Before the Dark", prologue.GetProperty("title").GetString());
        Assert.Equal("Ember", prologue.GetProperty("bookName").GetString());
        Assert.Equal("long-dark", prologue.GetProperty("series").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("positions")[10].GetProperty("series").ValueKind);

        // Every position listed is one asOf takes.
        foreach (var position in positions)
        {
            await server.GetJsonAsync($"/api/canon?universe=starfall&asOf={position}&brief=true", HttpStatusCode.OK);
        }

        // In a den with several universes, which one must be said.
        Assert.Equal("universe_required", (await server.GetJsonAsync("/api/positions", HttpStatusCode.BadRequest)).GetProperty("code").GetString());
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-6")]
    [InlineData("Mara Voss", null)]
    [InlineData("Mara Voss", "ember:1")]
    [InlineData("Mara Voss", "ember:2b")]
    [InlineData("Mara Voss", "ash:3")]
    [InlineData("Voss", "ash:epilogue")]
    [InlineData("Halden Pike", "ember:3")]
    [InlineData("Halden Pike", "ash:2")]
    [InlineData("Lantern", "ash:1")]
    [InlineData("Lantern", "ash:epilogue")]
    [InlineData("01JA7Q8M4XK3V9R2D5W8HN6TQF", "a-quiet-moon:1")]
    [InlineData("01JA7Q8M4XK3V9R2D5W8HN6TQF", "ash:2")]
    [InlineData("the-missing-manifest", "a-quiet-moon:2b")]
    public async Task AnEntityPageAsOfAPositionGivesWhatGetEntityGives(string entity, string? asOf)
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var mcp = new DenTools(den.Root).GetEntity(entity, "starfall", asOf);

        var json = await server.GetJsonAsync(
            $"/api/entity?universe=starfall&entity={Uri.EscapeDataString(entity)}" + (asOf is null ? "" : $"&asOf={asOf}"), HttpStatusCode.OK);

        SameAsMcp.Assert(mcp, json);
        Assert.Equal(mcp.InView, json.GetProperty("entity").ValueKind == JsonValueKind.Object);
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-6")]
    [InlineData("Mina Harker", null)]
    [InlineData("Mina Harker", "dracula:3")]
    [InlineData("Mina", "dracula:22")]
    [InlineData("Lucy Westenra", "dracula:12")]
    [InlineData("Count Dracula", "dracula")]
    [InlineData("Abraham Van Helsing", "dracula:8")]
    public async Task ADraculaEntityPageGivesWhatGetEntityGives(string entity, string? asOf)
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var mcp = new DenTools(den.Root).GetEntity(entity, null, asOf);

        var json = await server.GetJsonAsync($"/api/entity?entity={Uri.EscapeDataString(entity)}" + (asOf is null ? "" : $"&asOf={asOf}"), HttpStatusCode.OK);

        SameAsMcp.Assert(mcp, json);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-6")]
    public async Task AnEntityPageGivesItsScopeChainEntryPointAndResolvedLinks()
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var page = (await server.GetJsonAsync("/api/entity?universe=starfall&entity=Mara%20Voss", HttpStatusCode.OK)).GetProperty("entity");

        Assert.Equal(["starfall", "long-dark"], page.GetProperty("scope").EnumerateArray().Select(c => c.GetProperty("key").GetString()));
        Assert.Equal(["universe", "series"], page.GetProperty("scope").EnumerateArray().Select(c => c.GetProperty("kind").GetString()));
        Assert.Equal(["Voss", "the Commander"], page.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()));
        var entry = page.GetProperty("entry");
        Assert.Equal("ember:1", entry.GetProperty("position").GetString());
        Assert.False(entry.GetProperty("inherited").GetBoolean());

        // Each entry of a field that changes over time has its own links.
        var postings = page.GetProperty("fields").EnumerateArray().Where(f => f.GetProperty("name").GetString() == "posting").ToList();
        Assert.Equal(2, postings.Count);
        var tessaly = Assert.Single(postings[0].GetProperty("links").EnumerateArray());
        Assert.Equal("[[tessaly]]", tessaly.GetProperty("text").GetString());
        Assert.Equal("resolved", tessaly.GetProperty("outcome").GetString());
        Assert.Equal("Tessaly", tessaly.GetProperty("entity").GetProperty("name").GetString());
        Assert.Equal("01JA7Q8M4XK3V9R2D5W8HN6TQE", tessaly.GetProperty("entity").GetProperty("id").GetString());
        Assert.Equal("Kessan Reach", Assert.Single(postings[1].GetProperty("links").EnumerateArray()).GetProperty("entity").GetProperty("name").GetString());

        var injury = page.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Shoulder injury");
        Assert.Equal("Takes a flechette round at [[kessan-reach]].", injury.GetProperty("body").GetString());
        Assert.Equal("Kessan Reach", Assert.Single(injury.GetProperty("links").EnumerateArray()).GetProperty("entity").GetProperty("name").GetString());

        // A standalone book's entity: the universe, then the book.
        var lantern = (await server.GetJsonAsync("/api/entity?universe=starfall&entity=01JA7Q8M4XK3V9R2D5W8HN6TQF", HttpStatusCode.OK)).GetProperty("entity");
        Assert.Equal(["universe", "book"], lantern.GetProperty("scope").EnumerateArray().Select(c => c.GetProperty("kind").GetString()));
        Assert.Equal("a-quiet-moon", lantern.GetProperty("scope")[1].GetProperty("key").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-6")]
    public async Task AnEntityWithoutFromInheritsItsScopesStart()
    {
        using var den = TempDen.Starfall();
        den.Write(
            "universes/starfall/series/long-dark/books/ash/entities/characters/oren-tal.md",
            "---\ntype: character\nname: Oren Tal\nstatus: canon\n---\n\n## Overview\nA deckhand.\n");
        den.Write("universes/starfall/entities/characters/the-old-admiral.md", "---\ntype: character\nname: The old admiral\nstatus: canon\n---\n");
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var oren = (await server.GetJsonAsync("/api/entity?universe=starfall&entity=Oren%20Tal", HttpStatusCode.OK)).GetProperty("entity").GetProperty("entry");
        Assert.Equal("ash", oren.GetProperty("position").GetString());
        Assert.True(oren.GetProperty("inherited").GetBoolean());
        Assert.Contains("book 'ash'", oren.GetProperty("description").GetString(), StringComparison.Ordinal);

        var admiral = (await server.GetJsonAsync("/api/entity?universe=starfall&entity=The%20old%20admiral", HttpStatusCode.OK)).GetProperty("entity").GetProperty("entry");
        Assert.Equal(JsonValueKind.Null, admiral.GetProperty("position").ValueKind);
        Assert.True(admiral.GetProperty("inherited").GetBoolean());
        Assert.Contains("start of the story", admiral.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-6")]
    public async Task BacklinksSayWhereTheLinkIsAndAsOfAPositionOnlyCountWhatTheReaderHasMet()
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var full = (await server.GetJsonAsync("/api/entity?universe=starfall&entity=Kessan%20Reach", HttpStatusCode.OK)).GetProperty("entity");
        var fromMara = full.GetProperty("backlinks").EnumerateArray().Where(b => b.GetProperty("path").GetString() == MaraFile).ToList();
        Assert.Contains(fromMara, b => b.GetProperty("section").GetString() == "Shoulder injury" && b.GetProperty("site").GetString() == "entity_section");
        Assert.Contains(fromMara, b => b.GetProperty("section").GetString() == "In command");
        Assert.Contains(fromMara, b => b.GetProperty("field").GetString() == "posting" && b.GetProperty("site").GetString() == "entity_field_entry");
        Assert.All(fromMara, b => Assert.Equal("Mara Voss", b.GetProperty("source").GetProperty("name").GetString()));

        // At ember:1 the reader hasn't reached the injury (ember:2b), the command (ember:3), or the ash:4 posting.
        var early = (await server.GetJsonAsync("/api/entity?universe=starfall&entity=Kessan%20Reach&asOf=ember:1", HttpStatusCode.OK)).GetProperty("entity");
        Assert.DoesNotContain(early.GetProperty("backlinks").EnumerateArray(), b => b.GetProperty("path").GetString() == MaraFile);
        var later = (await server.GetJsonAsync("/api/entity?universe=starfall&entity=Kessan%20Reach&asOf=ember:3", HttpStatusCode.OK)).GetProperty("entity");
        Assert.Equal(
            ["In command", "Shoulder injury"],
            later.GetProperty("backlinks").EnumerateArray().Where(b => b.GetProperty("path").GetString() == MaraFile)
                .Select(b => b.GetProperty("section").GetString()).Order(StringComparer.Ordinal));

        // The manifest's payoff names Halden Pike, but only from a-quiet-moon:2b.
        var pike = (await server.GetJsonAsync("/api/entity?universe=starfall&entity=Halden%20Pike&asOf=ash:1", HttpStatusCode.OK)).GetProperty("entity");
        Assert.DoesNotContain(pike.GetProperty("backlinks").EnumerateArray(), b => b.GetProperty("source").ValueKind == JsonValueKind.Object
            && b.GetProperty("source").GetProperty("name").GetString() == "The missing manifest");
        var paid = (await server.GetJsonAsync("/api/entity?universe=starfall&entity=Halden%20Pike&asOf=a-quiet-moon:2b", HttpStatusCode.OK)).GetProperty("entity");
        Assert.Contains(paid.GetProperty("backlinks").EnumerateArray(), b => b.GetProperty("section").GetString() == "Payoff");
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-6")]
    public async Task AsOfAPositionALinkToAnEntityTheReaderHasntMetDoesntSayWhatItReaches()
    {
        using var den = TempDen.Starfall();
        den.Write(MaraFile, den.Read(MaraFile).Replace(
            "Executive officer of the *Tessaly*.", "Executive officer of the *Tessaly*. Distrusts [[01JA7Q8M4XK3V9R2D5W8HN6TQC|the new hand]].", StringComparison.Ordinal));
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        static JsonElement Link(JsonElement page) =>
            Assert.Single(page.GetProperty("entity").GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Overview")
                .GetProperty("links").EnumerateArray());

        var early = Link(await server.GetJsonAsync("/api/entity?universe=starfall&entity=Mara%20Voss&asOf=ember:1", HttpStatusCode.OK));
        Assert.False(early.GetProperty("inView").GetBoolean());
        Assert.Equal(JsonValueKind.Null, early.GetProperty("entity").ValueKind);
        Assert.Equal("resolved", early.GetProperty("outcome").GetString());

        var met = Link(await server.GetJsonAsync("/api/entity?universe=starfall&entity=Mara%20Voss&asOf=ash:1", HttpStatusCode.OK));
        Assert.True(met.GetProperty("inView").GetBoolean());
        Assert.Equal("Halden Pike", met.GetProperty("entity").GetProperty("name").GetString());

        var full = Link(await server.GetJsonAsync("/api/entity?universe=starfall&entity=Mara%20Voss", HttpStatusCode.OK));
        Assert.Equal(JsonValueKind.Null, full.GetProperty("inView").ValueKind);
        Assert.Equal("Halden Pike", full.GetProperty("entity").GetProperty("name").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-6")]
    public async Task BacklinksAsOfAPositionFromChaptersAndOutlineRowsCountUpToThatChapter()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        async Task<List<string?>> From(string entity, string asOf, string site, string member) =>
            (await server.GetJsonAsync($"/api/entity?entity={entity}&asOf={asOf}", HttpStatusCode.OK)).GetProperty("entity").GetProperty("backlinks")
                .EnumerateArray().Where(b => b.GetProperty("site").GetString() == site).Select(b => b.GetProperty(member).GetString()).ToList();

        // Mina is the POV of chapter 5 on: book.md's chapter fields link her.
        var pov = await From("Mina%20Harker", "dracula:5", "chapter_field", "chapter");
        Assert.Contains("dracula:5", pov);
        Assert.DoesNotContain("dracula:6", pov);
        Assert.Contains("dracula:6", await From("Mina%20Harker", "dracula:6", "chapter_field", "chapter"));

        // The outline's rows for chapter 1 name Jonathan; those for chapter 2 come later.
        var rows = await From("Jonathan%20Harker", "dracula:1", "outline_row", "outlineRow");
        Assert.Equal(["1", "2"], rows);
        Assert.Contains("3", await From("Jonathan%20Harker", "dracula:2", "outline_row", "outlineRow"));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-4")]
    public async Task APinnedServerDoesntNameAnotherUniversesEntityInABrokenLink()
    {
        using var den = TempDen.Starfall();
        den.Write("universes/drift/entities/characters/harbour-ghost.md", "---\ntype: character\nname: Harbour Ghost\nstatus: canon\n---\n");
        den.Write(MaraFile, den.Read(MaraFile).Replace(
            "Executive officer of the *Tessaly*.", "Executive officer of the *Tessaly*. Fears the [[Harbour Ghost]].", StringComparison.Ordinal));

        static JsonElement Link(JsonElement page) => Assert.Single(page.GetProperty("entity").GetProperty("sections").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "Overview").GetProperty("links").EnumerateArray());

        await using var all = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var open = Link(await all.GetJsonAsync("/api/entity?universe=starfall&entity=Mara%20Voss", HttpStatusCode.OK));
        Assert.Equal("broken", open.GetProperty("outcome").GetString());
        Assert.Contains("universe 'drift'", open.GetProperty("problem").GetString(), StringComparison.Ordinal);

        await using var pinned = await Server.StartAsync(new WolfdenApiOptions(den.Root) { PinnedUniverse = "starfall" });
        var problem = Link(await pinned.GetJsonAsync("/api/entity?entity=Mara%20Voss", HttpStatusCode.OK)).GetProperty("problem").GetString();
        Assert.Equal("'Harbour Ghost' matches no entity in universe 'starfall'.", problem);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-10")]
    public async Task AnEmptyAsOfIsRefusedRatherThanShowingEverything()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        foreach (var path in new[] { "/api/entity?entity=Mina&asOf=", "/api/canon?asOf=%20", "/api/hierarchy?asOf=", "/api/entities?asOf=", "/api/search?query=Mina&asOf=" })
        {
            Assert.Equal("bad_argument", (await server.GetJsonAsync(path, HttpStatusCode.BadRequest)).GetProperty("code").GetString());
        }

        // Other empty filters are left out.
        await server.GetJsonAsync("/api/entities?type=&status=", HttpStatusCode.OK);
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-7")]
    [InlineData(null, null, null)]
    [InlineData("ember:1", null, null)]
    [InlineData("ember:2b", "ember", null)]
    [InlineData("ash:3", null, "character")]
    [InlineData("a-quiet-moon:2b", null, null)]
    [InlineData("ash", "long-dark", null)]
    public async Task CanonAsOfGivesWhatCanonAsOfGives(string? asOf, string? filter, string? type)
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var series = filter == "long-dark" ? filter : null;
        var book = filter is not null && series is null ? filter : null;
        var mcp = new DenTools(den.Root).CanonAsOf("starfall", asOf, series, book, type);

        var query = "/api/canon?universe=starfall" + (asOf is null ? "" : $"&asOf={asOf}") + (series is null ? "" : $"&series={series}")
            + (book is null ? "" : $"&book={book}") + (type is null ? "" : $"&type={type}");
        SameAsMcp.Assert(mcp, await server.GetJsonAsync(query, HttpStatusCode.OK));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-7")]
    public async Task ListsAndSearchesWithoutAPositionGiveWhatTheToolsGive()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var tools = new DenTools(den.Root);

        SameAsMcp.Assert(tools.ListEntities(type: "character"), await server.GetJsonAsync("/api/entities?type=character", HttpStatusCode.OK));
        SameAsMcp.Assert(tools.ListEntities(limit: 5), await server.GetJsonAsync("/api/entities?limit=5", HttpStatusCode.OK));
        SameAsMcp.Assert(tools.ListEntities(status: "canon", book: "dracula"), await server.GetJsonAsync("/api/entities?status=canon&book=dracula", HttpStatusCode.OK));
        SameAsMcp.Assert(tools.Search("blood"), await server.GetJsonAsync("/api/search?query=blood", HttpStatusCode.OK));
        SameAsMcp.Assert(tools.Search("Mina", type: "character", canonOnly: true, limit: 3), await server.GetJsonAsync("/api/search?query=Mina&type=character&canonOnly=true&limit=3", HttpStatusCode.OK));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-7")]
    public async Task ListsAsOfAPositionHoldOnlyWhatTheReaderHasMet()
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        static List<string?> Names(JsonElement json) => json.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        var all = Names(await server.GetJsonAsync("/api/entities?universe=starfall&type=character", HttpStatusCode.OK));
        Assert.Equal(["Mara Voss", "Halden Pike"], all);
        Assert.Equal(["Mara Voss"], Names(await server.GetJsonAsync("/api/entities?universe=starfall&type=character&asOf=ember:3", HttpStatusCode.OK)));
        Assert.Equal(all, Names(await server.GetJsonAsync("/api/entities?universe=starfall&type=character&asOf=ash:1", HttpStatusCode.OK)));

        // The order and the members are the canon view's.
        var view = new DenTools(den.Root).CanonAsOf("starfall", "ash:1", brief: true);
        var listed = await server.GetJsonAsync("/api/entities?universe=starfall&asOf=ash:1", HttpStatusCode.OK);
        Assert.Equal(view.Entities.Select(e => e.Path), listed.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("path").GetString()));
        Assert.Equal(view.Count, listed.GetProperty("total").GetInt32());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-8")]
    public async Task SearchFiltersByStatusScopeAndPosition()
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        static List<string?> Sections(JsonElement json) => json.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("section").GetString()).ToList();

        // The injury section enters at ember:2b.
        Assert.Empty(Sections(await server.GetJsonAsync("/api/search?query=flechette&universe=starfall&asOf=ember:2", HttpStatusCode.OK)));
        var hit = Assert.Single((await server.GetJsonAsync("/api/search?query=flechette&asOf=ember:2b", HttpStatusCode.OK)).GetProperty("hits").EnumerateArray());
        Assert.Equal("Shoulder injury", hit.GetProperty("section").GetString());
        Assert.Contains("[flechette]", hit.GetProperty("snippet").GetString(), StringComparison.Ordinal);

        // A book filter keeps the book's, its series', and its universe's entities, as in a canon view.
        static List<string?> Entities(JsonElement json) => json.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("entity").GetProperty("name").GetString()).Distinct().ToList();
        Assert.Contains("Lantern", Entities(await server.GetJsonAsync("/api/search?query=lantern&universe=starfall", HttpStatusCode.OK)));
        var inAsh = await server.GetJsonAsync("/api/search?query=Tessaly&universe=starfall&book=ash", HttpStatusCode.OK);
        Assert.Contains("Halden Pike", Entities(inAsh));
        Assert.Contains("Mara Voss", Entities(inAsh));
        var inMoon = await server.GetJsonAsync("/api/search?query=Tessaly&universe=starfall&book=a-quiet-moon", HttpStatusCode.OK);
        Assert.DoesNotContain("Halden Pike", Entities(inMoon));
        Assert.DoesNotContain("Mara Voss", Entities(inMoon));

        Assert.Empty(Entities(await server.GetJsonAsync("/api/search?query=Tessaly&universe=starfall&status=idea", HttpStatusCode.OK)));
        Assert.Equal("bad_argument", (await server.GetJsonAsync("/api/search?query=Tessaly&status=lost", HttpStatusCode.BadRequest)).GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-10")]
    public async Task BadRequestsGetACodeAndAMessage()
    {
        using var den = TempDen.Starfall();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        async Task Expect(string path, HttpStatusCode status, string code)
        {
            var json = await server.GetJsonAsync(path, status);
            Assert.Equal(code, json.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("message").GetString()));
        }

        await Expect("/api/entity?universe=starfall&entity=Mara%20Voss&asOf=ember:99", HttpStatusCode.BadRequest, "bad_position");
        await Expect("/api/canon?universe=starfall&asOf=nowhere:1", HttpStatusCode.BadRequest, "bad_position");
        await Expect("/api/hierarchy?asOf=nowhere", HttpStatusCode.BadRequest, "bad_position");
        await Expect("/api/entity?universe=starfall&entity=Nobody", HttpStatusCode.NotFound, "unknown_entity");
        await Expect("/api/entity?universe=nowhere&entity=Mara%20Voss", HttpStatusCode.NotFound, "unknown_universe");
        await Expect("/api/hierarchy?universe=nowhere", HttpStatusCode.NotFound, "unknown_universe");
        await Expect("/api/entities?universe=starfall&series=nope", HttpStatusCode.NotFound, "unknown_scope");
        await Expect("/api/entities?universe=starfall&type=dragon", HttpStatusCode.BadRequest, "unknown_type");
        await Expect("/api/entity?entity=Mara%20Voss", HttpStatusCode.BadRequest, "universe_required");
        await Expect("/api/entity?universe=starfall", HttpStatusCode.BadRequest, "bad_argument");
        await Expect("/api/entity?universe=starfall&entity=Mara%20Voss&as_of=ember:1", HttpStatusCode.BadRequest, "bad_argument");
        await Expect("/api/entities?universe=starfall&type=ship&type=character", HttpStatusCode.BadRequest, "bad_argument");
        await Expect("/api/entities?universe=starfall&limit=lots", HttpStatusCode.BadRequest, "bad_argument");
        await Expect("/api/entities?universe=starfall&limit=0", HttpStatusCode.BadRequest, "bad_argument");
        await Expect("/api/canon?universe=starfall&brief=maybe", HttpStatusCode.BadRequest, "bad_argument");
        await Expect("/api/canon?universe=starfall&series=long-dark&book=ash", HttpStatusCode.BadRequest, "bad_argument");
        await Expect("/api/search?universe=starfall", HttpStatusCode.BadRequest, "bad_argument");

        // Two entities share the name; the error lists them.
        var ambiguous = await server.GetJsonAsync("/api/entity?universe=starfall&entity=Lantern", HttpStatusCode.Conflict);
        Assert.Equal("ambiguous_entity", ambiguous.GetProperty("code").GetString());
        Assert.Equal(2, ambiguous.GetProperty("candidates").GetArrayLength());
        Assert.DoesNotContain("name its universe", ambiguous.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Ask again with one of the candidates' keys.", ambiguous.GetProperty("message").GetString(), StringComparison.Ordinal);

        // Advice names the API's endpoints, not the MCP tools (#175).
        var every = await server.GetJsonAsync("/api/entity?universe=*&entity=Mara%20Voss", HttpStatusCode.BadRequest);
        Assert.Contains("is only for /api/search and /api/entities;", every.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("list_entities", every.GetProperty("message").GetString(), StringComparison.Ordinal);

        // The reader hasn't met Halden Pike at ember:1: not an error, but not in the view.
        var unmet = await server.GetJsonAsync("/api/entity?universe=starfall&entity=Halden%20Pike&asOf=ember:1", HttpStatusCode.OK);
        Assert.False(unmet.GetProperty("inView").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unmet.GetProperty("entity").ValueKind);

        using var post = await server.Client.PostAsync("/api/entity", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    private static List<string?> Keys(JsonElement array) => array.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
}
