using System.Net;
using System.Text;
using System.Text.Json;

namespace Wolfden.Api.Tests;

/// <summary>
/// The editing endpoints of the web UI (#28): an entity's source, and edits
/// of its lead, its sections, and its whole file, through the MCP write
/// tools' code. Over a copy of the Dracula sample with an invented note
/// added, and the invented Starfall den for pinning.
/// </summary>
public class WriteEndpointTests
{
    private const string NotePath = "universes/dracula/entities/notes/edit-test.md";
    private const string LinkerPath = "universes/dracula/entities/notes/edit-linker.md";
    private const string SwalesPath = "universes/dracula/books/dracula/entities/characters/mr-swales.md";

    private const string Note =
        "---\n" +
        "type: note\n" +
        "name: Edit Test\n" +
        "status: canon\n" +
        "---\n" +
        "\n" +
        "# Edit Test\n" +
        "\n" +
        "The opening line.\n" +
        "\n" +
        "## First\n" +
        "<!-- wolfden: pin=true -->\n" +
        "First text.\n" +
        "\n" +
        "## Second\n" +
        "\n" +
        "Second text.\n" +
        "<!-- a writer's comment -->\n" +
        "\n" +
        "### Child\n" +
        "Child text.\n";

    private const string Linker =
        "---\n" +
        "id: 01M4TF5F0EKGGWD5FNVBAEY8Z1\n" +
        "type: note\n" +
        "name: Edit Linker\n" +
        "status: canon\n" +
        "---\n" +
        "\n" +
        "See [[Edit Test#Second]] and [[Edit Test#Child]].\n";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TempDen NewDen()
    {
        var den = TempDen.Dracula();
        den.Write(NotePath, Note);
        den.Write(LinkerPath, Linker);
        return den;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> PostAsync(
        Server server, string path, object body, string contentType = "application/json", Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, contentType),
        };
        configure?.Invoke(request);
        using var response = await server.Client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<JsonElement> SourceAsync(Server server, string entity) =>
        await server.GetJsonAsync($"/api/entity/source?entity={Uri.EscapeDataString(entity)}", HttpStatusCode.OK);

    private static string Hash(JsonElement source) => source.GetProperty("hash").GetString()!;

    private static JsonElement Section(JsonElement source, string name) =>
        source.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    private static int IndexOf(JsonElement source, string name) => Section(source, name).GetProperty("index").GetInt32();

    [Fact]
    [Trait("Scenario", "SC-WEB-40")]
    public async Task TheSourceGivesTheFilesHashAndEachSectionsTextAsWritten()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var source = await SourceAsync(server, NotePath);

        var bytes = File.ReadAllBytes(Path.Combine(den.Root, NotePath));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), Hash(source));
        Assert.Equal(Note, source.GetProperty("text").GetString());
        Assert.Equal("lf", source.GetProperty("lineEndings").GetString());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("frozen").ValueKind);
        Assert.Equal("# Edit Test\n\nThe opening line.", source.GetProperty("lead").GetProperty("text").GetString());
        var first = Section(source, "First");
        Assert.Equal("<!-- wolfden: pin=true -->", first.GetProperty("comment").GetString());
        Assert.Equal("First text.", first.GetProperty("body").GetString());
        Assert.True(first.GetProperty("pinned").GetBoolean());

        // Other HTML comments are part of the body; subsections aren't.
        var second = Section(source, "Second");
        Assert.Equal("Second text.\n<!-- a writer's comment -->", second.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("comment").ValueKind);
        Assert.Equal(second.GetProperty("index").GetInt32(), Section(source, "Child").GetProperty("parent").GetInt32());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-40")]
    public async Task ReplacingASectionChangesOnlyItsTextAndGivesTheFileAnId()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, NotePath);

        var (status, json) = await PostAsync(server, "/api/edit/section", new
        {
            entity = NotePath, expectedHash = Hash(source), index = IndexOf(source, "Second"), name = "Second", text = "New second text.\n\nMore.",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(json.GetProperty("changed").GetBoolean());
        var id = json.GetProperty("idAdded").GetString()!;
        Assert.Equal(Note.Replace("type: note\n", $"id: {id}\ntype: note\n", StringComparison.Ordinal)
            .Replace("Second text.\n<!-- a writer's comment -->\n", "New second text.\n\nMore.\n", StringComparison.Ordinal), den.Read(NotePath));
        Assert.Equal(Hash(await SourceAsync(server, NotePath)), json.GetProperty("hash").GetString());
        Assert.Empty(json.GetProperty("pinnedCanon").EnumerateArray());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-40")]
    public async Task EditingPinnedCanonSaysSo()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, NotePath);

        var (status, json) = await PostAsync(server, "/api/edit/section", new
        {
            entity = NotePath, expectedHash = Hash(source), index = IndexOf(source, "First"), name = "First", text = "Changed.",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        var pinned = Assert.Single(json.GetProperty("pinnedCanon").EnumerateArray()).GetString()!;
        Assert.Contains("section 'First'", pinned, StringComparison.Ordinal);
        Assert.Contains("<!-- wolfden: pin=true -->\nChanged.\n", den.Read(NotePath), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-40")]
    public async Task ReplacingTheLeadKeepsTheFrontmatterAndSections()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, NotePath);

        var (status, json) = await PostAsync(server, "/api/edit/lead", new { entity = NotePath, expectedHash = Hash(source), text = "# Edit Test\n\nA new opening." });

        Assert.Equal(HttpStatusCode.OK, status);
        var text = den.Read(NotePath);
        Assert.EndsWith("\n# Edit Test\n\nA new opening.\n\n## First\n" + Note[(Note.IndexOf("<!-- wolfden", StringComparison.Ordinal))..], text, StringComparison.Ordinal);
        Assert.Contains("Replaced the text before the sections.", json.GetProperty("effects").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-41")]
    public async Task AddingASectionPutsItAtTheEndOrBeforeAnother()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, NotePath);

        var (status, json) = await PostAsync(server, "/api/edit/section/add", new { entity = NotePath, expectedHash = Hash(source), name = "Last", text = "At the end." });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.EndsWith("Child text.\n\n## Last\nAt the end.\n", den.Read(NotePath), StringComparison.Ordinal);

        source = await SourceAsync(server, NotePath);
        (status, _) = await PostAsync(server, "/api/edit/section/add", new
        {
            entity = NotePath, expectedHash = json.GetProperty("hash").GetString(), name = "Between", text = "", beforeIndex = IndexOf(source, "Second"), beforeName = "Second",
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("First text.\n\n## Between\n\n## Second\n", den.Read(NotePath), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-41")]
    public async Task RenamingASectionRewritesTheLinksToIt()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, NotePath);

        var (status, json) = await PostAsync(server, "/api/edit/section/rename", new
        {
            entity = NotePath, expectedHash = Hash(source), index = IndexOf(source, "Second"), name = "Second", newName = "Later",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\n## Later\n", den.Read(NotePath), StringComparison.Ordinal);
        Assert.Contains("See [[Edit Test#Later]] and [[Edit Test#Child]].", den.Read(LinkerPath), StringComparison.Ordinal);
        Assert.Equal(2, json.GetProperty("files").GetArrayLength());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-41")]
    public async Task RemovingASectionTakesItsSubsectionsAndSaysWhichLinksLoseIt()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, NotePath);

        var (status, json) = await PostAsync(server, "/api/edit/section/remove", new
        {
            entity = NotePath, expectedHash = Hash(source), index = IndexOf(source, "Second"), name = "Second",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        var text = den.Read(NotePath);
        Assert.DoesNotContain("Second", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Child", text, StringComparison.Ordinal);
        Assert.EndsWith("First text.\n", text, StringComparison.Ordinal);
        var effects = json.GetProperty("effects").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(effects, e => e.Contains("with its 1 subsection", StringComparison.Ordinal));
        Assert.Contains(effects, e => e.Contains("to 'Second' by heading no longer reach a section", StringComparison.Ordinal) && e.Contains(LinkerPath, StringComparison.Ordinal));
        Assert.Contains(effects, e => e.Contains("to 'Child' by heading no longer reach a section", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-42")]
    public async Task EditFileReplacesTheWholeFileButNeverItsId()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, LinkerPath);

        var changed = Linker.Replace("status: canon\n", "status: canon\nfields:\n  topic: links\n", StringComparison.Ordinal);
        var (status, json) = await PostAsync(server, "/api/edit/file", new { entity = LinkerPath, expectedHash = Hash(source), text = changed });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(changed, den.Read(LinkerPath));

        var noId = changed.Replace("id: 01M4TF5F0EKGGWD5FNVBAEY8Z1\n", "", StringComparison.Ordinal);
        (status, var refused) = await PostAsync(server, "/api/edit/file", new { entity = LinkerPath, expectedHash = json.GetProperty("hash").GetString(), text = noId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("unsupported_edit", refused.GetProperty("code").GetString());
        Assert.Contains("F-ID-4", refused.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(changed, den.Read(LinkerPath));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-42")]
    public async Task AnEditThatWouldLeaveAnErrorIsRefusedWithTheErrors()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, LinkerPath);

        var broken = Linker.Replace("status: canon\n", "status: canon\nfields: [unclosed\n", StringComparison.Ordinal);
        var (status, json) = await PostAsync(server, "/api/edit/file", new { entity = LinkerPath, expectedHash = Hash(source), text = broken });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("invalid_edit", json.GetProperty("code").GetString());
        Assert.Contains(LinkerPath, json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(Linker, den.Read(LinkerPath));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-42")]
    public async Task TextThatWouldAddAHeadingToASectionIsRefused()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, NotePath);

        var (status, json) = await PostAsync(server, "/api/edit/section", new
        {
            entity = NotePath, expectedHash = Hash(source), index = IndexOf(source, "First"), name = "First", text = "Text.\n\n## Sneaky",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_argument", json.GetProperty("code").GetString());
        Assert.Equal(Note, den.Read(NotePath));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-43")]
    public async Task AStaleEditIsRefusedWithTheFileAsItIsNow()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, LinkerPath);

        // Another program (an agent, an editor) saves the file meanwhile.
        var theirs = Linker.Replace("See ", "Also see ", StringComparison.Ordinal);
        den.Write(LinkerPath, theirs);
        var (status, json) = await PostAsync(server, "/api/edit/lead", new { entity = LinkerPath, expectedHash = Hash(source), text = "Mine." });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("stale_file", json.GetProperty("code").GetString());
        var current = json.GetProperty("current");
        Assert.Equal(theirs, current.GetProperty("text").GetString());
        Assert.NotEqual(Hash(source), Hash(current));
        Assert.Equal(theirs, den.Read(LinkerPath));

        // Keeping mine: the same edit on the new hash.
        (status, _) = await PostAsync(server, "/api/edit/lead", new { entity = LinkerPath, expectedHash = Hash(current), text = "Mine." });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.EndsWith("---\n\nMine.\n", den.Read(LinkerPath), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-43")]
    public async Task AStaleSectionIndexOrNameIsRefused()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, NotePath);

        var (status, json) = await PostAsync(server, "/api/edit/section", new
        {
            entity = NotePath, expectedHash = Hash(source), index = IndexOf(source, "First"), name = "Second", text = "Wrong one.",
        });

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("unknown_section", json.GetProperty("code").GetString());
        Assert.Equal(Note, den.Read(NotePath));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-44")]
    public async Task ANoteInAFinalBookIsFrozen()
    {
        using var den = NewDen();
        var book = "universes/dracula/books/dracula/book.md";
        den.Write(book, den.Read(book).Replace("status: draft", "status: final", StringComparison.Ordinal));
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var before = den.Read(SwalesPath);

        var source = await SourceAsync(server, SwalesPath);
        Assert.Contains("final (frozen)", source.GetProperty("frozen").GetString(), StringComparison.Ordinal);
        var page = await server.GetJsonAsync($"/api/entity?entity={Uri.EscapeDataString(SwalesPath)}", HttpStatusCode.OK);
        Assert.Contains("final (frozen)", page.GetProperty("entity").GetProperty("frozen").GetString(), StringComparison.Ordinal);

        var (status, json) = await PostAsync(server, "/api/edit/section", new
        {
            entity = SwalesPath, expectedHash = Hash(source), index = IndexOf(source, "Voice"), name = "Voice", text = "Changed.",
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("frozen", json.GetProperty("code").GetString());
        Assert.Equal(before, den.Read(SwalesPath));
        var draft = await server.GetJsonAsync($"/api/entity?entity={Uri.EscapeDataString(NotePath)}", HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("entity").GetProperty("frozen").ValueKind);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-45")]
    public async Task EditingAndSavingTheSameTextKeepsEveryByte()
    {
        using var den = NewDen();

        // CRLF line breaks, a byte order mark, odd spacing, and no final newline.
        var crlf = "﻿" + Note.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("## Second\r\n\r\n", "## Second  \r\n\r\n\r\n", StringComparison.Ordinal).TrimEnd('\r', '\n');
        File.WriteAllText(Path.Combine(den.Root, NotePath), crlf, new UTF8Encoding(false));
        var original = File.ReadAllBytes(Path.Combine(den.Root, NotePath));
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, NotePath);
        Assert.Equal("crlf", source.GetProperty("lineEndings").GetString());
        Assert.True(source.GetProperty("byteOrderMark").GetBoolean());

        foreach (var (path, body) in new (string, object)[]
        {
            ("/api/edit/file", new { entity = NotePath, expectedHash = Hash(source), text = source.GetProperty("text").GetString() }),
            ("/api/edit/lead", new { entity = NotePath, expectedHash = Hash(source), text = source.GetProperty("lead").GetProperty("text").GetString() }),
            ("/api/edit/section", new
            {
                entity = NotePath, expectedHash = Hash(source), index = IndexOf(source, "Second"), name = "Second", text = Section(source, "Second").GetProperty("body").GetString(),
            }),
        })
        {
            var (status, json) = await PostAsync(server, path, body);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(json.GetProperty("changed").GetBoolean(), path);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(den.Root, NotePath)));
        }

        // An edit of one section leaves every other byte as it was.
        var (_, edited) = await PostAsync(server, "/api/edit/section", new
        {
            entity = NotePath, expectedHash = Hash(source), index = IndexOf(source, "Child"), name = "Child", text = "New child.",
        });
        var id = edited.GetProperty("idAdded").GetString();
        var expected = crlf.Replace("type: note\r\n", $"id: {id}\r\ntype: note\r\n", StringComparison.Ordinal)
            .Replace("Child text.", "New child.", StringComparison.Ordinal);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(expected[1..])], File.ReadAllBytes(Path.Combine(den.Root, NotePath)));
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-46")]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data")]
    public async Task AnEditThatIsntJsonIsRefusedSoAFormCantPostOne(string contentType)
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, LinkerPath);

        var (status, json) = await PostAsync(
            server, "/api/edit/lead", JsonSerializer.Serialize(new { entity = LinkerPath, expectedHash = Hash(source), text = "Forged." }), contentType);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, status);
        Assert.Equal("unsupported_media_type", json.GetProperty("code").GetString());
        Assert.Equal(Linker, den.Read(LinkerPath));
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-46")]
    [InlineData("Origin", "https://attacker.example")]
    [InlineData("Origin", "http://localhost:1")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    public async Task ACrossSiteEditIsRefused(string header, string value)
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, LinkerPath);

        var (status, json) = await PostAsync(
            server, "/api/edit/lead", new { entity = LinkerPath, expectedHash = Hash(source), text = "Forged." }, configure: r => r.Headers.Add(header, value));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("cross_site", json.GetProperty("code").GetString());
        Assert.Equal(Linker, den.Read(LinkerPath));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-46")]
    public async Task AnEditFromItsOwnPageIsAnswered()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, LinkerPath);

        var (status, _) = await PostAsync(
            server, "/api/edit/lead", new { entity = LinkerPath, expectedHash = Hash(source), text = "Mine." },
            configure: r => r.Headers.Add("Origin", $"http://localhost:{server.Port}"));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-46")]
    public async Task AnEditEndpointAnswersOnlyPostAndItsOwnMembers()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var source = await SourceAsync(server, LinkerPath);

        using var get = await server.Client.GetAsync("/api/edit/lead", Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        Assert.Equal("POST", get.Content.Headers.Allow.Single());

        var (status, json) = await PostAsync(server, "/api/edit/lead", new { entity = LinkerPath, expected_hash = Hash(source), text = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("expected_hash", json.GetProperty("message").GetString(), StringComparison.Ordinal);

        (status, json) = await PostAsync(server, "/api/edit/lead", new { entity = LinkerPath, text = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("expectedHash", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(Linker, den.Read(LinkerPath));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-46")]
    public async Task APinnedServerEditsOnlyItsUniverse()
    {
        using var den = TempDen.Starfall();
        const string Reach = "universes/starfall/entities/locations/kessan-reach.md";
        var before = den.Read(Reach);
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { PinnedUniverse = "drift" });

        await server.GetJsonAsync($"/api/entity/source?entity={Reach}", HttpStatusCode.NotFound);
        var (status, json) = await PostAsync(server, "/api/edit/lead", new { entity = Reach, universe = "starfall", expectedHash = new string('0', 64), text = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_argument", json.GetProperty("code").GetString());
        Assert.Contains("pinned", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, den.Read(Reach));
    }
}
