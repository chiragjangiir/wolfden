using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace Wolfden.Api.Tests;

/// <summary>The API and the web UI's files, served on a free port of 127.0.0.1 (#23).</summary>
public class ApiTests
{
    [Fact]
    [Trait("Scenario", "SC-WEB-1")]
    public async Task HealthSaysOkWithTheVersion()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { Version = "9.9.9-test" });

        using var response = await server.Client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"status":"ok","wolfden":"9.9.9-test"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-1")]
    public async Task DenGivesItsNameAndUniversesInCamelCase()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var json = await server.GetJsonAsync("/api/den", HttpStatusCode.OK);

        Assert.Equal(1, json.GetProperty("version").GetInt32());
        Assert.Equal("Dracula", json.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("pinnedUniverse").ValueKind);
        var universe = Assert.Single(json.GetProperty("universes").EnumerateArray());
        Assert.Equal("dracula", universe.GetProperty("key").GetString());
        Assert.Equal("Dracula", universe.GetProperty("name").GetString());
        Assert.Equal(0, universe.GetProperty("series").GetInt32());
        Assert.Equal(1, universe.GetProperty("books").GetInt32());
        Assert.True(universe.GetProperty("entities").GetInt32() > 0);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-1")]
    public async Task DenSeesAnEditWithoutARestart()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        await server.GetJsonAsync("/api/den", HttpStatusCode.OK);

        den.Write("den.yaml", den.Read("den.yaml").Replace("name: Dracula", "name: Dracula Notes", StringComparison.Ordinal));

        var json = await server.GetJsonAsync("/api/den", HttpStatusCode.OK);
        Assert.Equal("Dracula Notes", json.GetProperty("name").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-1")]
    public async Task APinnedServerListsOnlyItsUniverse()
    {
        using var den = TempDen.Starfall();
        await using var all = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var allJson = await all.GetJsonAsync("/api/den", HttpStatusCode.OK);
        var keys = allJson.GetProperty("universes").EnumerateArray().Select(u => u.GetProperty("key").GetString()!).ToList();
        Assert.True(keys.Count > 1);
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);

        await using var pinned = await Server.StartAsync(new WolfdenApiOptions(den.Root) { PinnedUniverse = keys[1] });
        var json = await pinned.GetJsonAsync("/api/den", HttpStatusCode.OK);

        Assert.Equal(keys[1], json.GetProperty("pinnedUniverse").GetString());
        Assert.Equal(keys[1], Assert.Single(json.GetProperty("universes").EnumerateArray()).GetProperty("key").GetString());
    }

    [Fact]
    public async Task ADenYamlThatDoesntLoadIsDenNotLoaded()
    {
        using var den = TempDen.Dracula();
        den.Write("den.yaml", "format: [not a number\n");
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var json = await server.GetJsonAsync("/api/den", HttpStatusCode.ServiceUnavailable);

        Assert.Equal("den_not_loaded", json.GetProperty("code").GetString());
        Assert.Contains("wolfden check", json.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownApiPathIsAJsonNotFound()
    {
        // With a built UI, whose fallback to index.html mustn't answer for the API.
        using var den = TempDen.Dracula();
        var webRoot = WebRoot.Built();
        try
        {
            await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = webRoot });

            var json = await server.GetJsonAsync("/api/nothing/here", HttpStatusCode.NotFound);

            Assert.Equal("not_found", json.GetProperty("code").GetString());
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-3")]
    public async Task ItListensOnLoopbackOnlyAndRefusesOtherHostNames()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        Assert.StartsWith("http://127.0.0.1:", Assert.Single(server.App.Urls), StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/den");
        request.Headers.Host = "attacker.example";
        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var localhost = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        localhost.Headers.Host = "localhost:5173";
        using var proxied = await server.Client.SendAsync(localhost, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, proxied.StatusCode);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-1")]
    public async Task WithoutABuiltUiTheRootSaysHowToBuildIt()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = Path.Combine(den.Root, "no-ui-here") });

        using var response = await server.Client.GetAsync("/", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("npm run build", html, StringComparison.Ordinal);
        await server.GetJsonAsync("/api/den", HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-1")]
    public async Task ABuiltUiIsServedWithIndexHtmlForItsOwnRoutes()
    {
        using var den = TempDen.Dracula();
        var webRoot = WebRoot.Built();
        try
        {
            await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = webRoot });
            var token = TestContext.Current.CancellationToken;

            Assert.Equal(WebRoot.IndexHtml, await server.Client.GetStringAsync("/", token));
            Assert.Equal(WebRoot.Script, await server.Client.GetStringAsync("/assets/app.js", token));
            Assert.Equal(WebRoot.IndexHtml, await server.Client.GetStringAsync("/universes/dracula", token));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-17")]
    public async Task AnEntityPageNamedByItsPathIsServedWithIndexHtml()
    {
        using var den = TempDen.Dracula();
        var webRoot = WebRoot.Built();
        try
        {
            await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = webRoot });
            var token = TestContext.Current.CancellationToken;

            // A note without an id is named by its path, which ends in .md.
            Assert.Equal(WebRoot.IndexHtml, await server.Client.GetStringAsync("/u/dracula/e/universes/dracula/entities/locations/bistritz.md?at=dracula:3", token));
            Assert.Equal(WebRoot.IndexHtml, await server.Client.GetStringAsync("/u/dracula/e/universes%2Fdracula%2Fnotes%2Fa%20b.md", token));

            // Real files, the API, and a missing file outside /u are as before.
            Assert.Equal(WebRoot.Script, await server.Client.GetStringAsync("/assets/app.js", token));
            using var missing = await server.Client.GetAsync("/assets/missing.js", token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var den1 = await server.GetJsonAsync("/api/den", HttpStatusCode.OK);
            Assert.Equal("Dracula", den1.GetProperty("name").GetString());
            await server.GetJsonAsync("/api/nothing.md", HttpStatusCode.NotFound);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }
}
