using System.Net;

namespace Wolfden.Api.Tests;

/// <summary>
/// The Content-Security-Policy <c>wolfden serve</c> sends, and the den's
/// <c>remote_images</c> setting (format.md F-DEN-8), which lets notes'
/// images from websites load (#180).
/// </summary>
public class ContentSecurityPolicyTests
{
    private const string Page =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    private const string PageWithRemoteImages =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https: http:; connect-src 'self'; " +
        "font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    private const string NotAPage = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

    [Fact]
    [Trait("Scenario", "SC-WEB-36")]
    public async Task PagesGetAPolicyThatLoadsOnlyFromThisServer()
    {
        using var den = TempDen.Dracula();
        var webRoot = WebRoot.Built();
        try
        {
            await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = webRoot });

            foreach (var path in new[] { "/", "/index.html", "/u/dracula/e/E1?at=dracula:3", "/u/dracula/e/universes/dracula/notes/a.md" })
            {
                Assert.Equal(Page, await PolicyAsync(server, path, HttpStatusCode.OK));
            }

            Assert.DoesNotContain("unsafe", Page, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-36")]
    public async Task EverythingElseGetsAPolicyThatLoadsNothing()
    {
        using var den = TempDen.Dracula();
        den.Write("den.yaml", den.Read("den.yaml") + "remote_images: true\n");
        var webRoot = WebRoot.Built();
        try
        {
            await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = webRoot });

            Assert.Equal(NotAPage, await PolicyAsync(server, "/api/den", HttpStatusCode.OK));
            Assert.Equal(NotAPage, await PolicyAsync(server, "/api/health", HttpStatusCode.OK));
            Assert.Equal(NotAPage, await PolicyAsync(server, "/api/nothing", HttpStatusCode.NotFound));
            Assert.Equal(NotAPage, await PolicyAsync(server, "/assets/app.js", HttpStatusCode.OK));
            Assert.Equal(NotAPage, await PolicyAsync(server, "/assets/missing.js", HttpStatusCode.NotFound));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-36")]
    public async Task ThePageThatSaysHowToBuildTheUiHasItsOwnPolicy()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = Path.Combine(den.Root, "no-ui-here") });

        Assert.Equal(
            "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'",
            await PolicyAsync(server, "/", HttpStatusCode.OK));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-35")]
    public async Task ByDefaultTheDenDoesntAllowRemoteImages()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var json = await server.GetJsonAsync("/api/den", HttpStatusCode.OK);

        Assert.False(json.GetProperty("remoteImages").GetBoolean());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-37")]
    public async Task RemoteImagesTrueAllowsThemInThePolicyAndTellsTheUi()
    {
        using var den = TempDen.Dracula();
        var webRoot = WebRoot.Built();
        try
        {
            await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = webRoot });
            var original = den.Read("den.yaml");

            den.Write("den.yaml", original + "remote_images: true\n");
            Assert.Equal(PageWithRemoteImages, await PolicyAsync(server, "/", HttpStatusCode.OK));
            Assert.True((await server.GetJsonAsync("/api/den", HttpStatusCode.OK)).GetProperty("remoteImages").GetBoolean());

            // Turned off again, it applies on the next load, without a restart.
            den.Write("den.yaml", original + "remote_images: false\n");
            Assert.Equal(Page, await PolicyAsync(server, "/u/dracula", HttpStatusCode.OK));
            Assert.False((await server.GetJsonAsync("/api/den", HttpStatusCode.OK)).GetProperty("remoteImages").GetBoolean());

            // A value that isn't a boolean leaves them blocked (a warning in check).
            den.Write("den.yaml", original + "remote_images: yes\n");
            Assert.Equal(Page, await PolicyAsync(server, "/", HttpStatusCode.OK));
            Assert.False((await server.GetJsonAsync("/api/den", HttpStatusCode.OK)).GetProperty("remoteImages").GetBoolean());
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-37")]
    public async Task ADenYamlThatDoesntLoadKeepsRemoteImagesBlocked()
    {
        using var den = TempDen.Dracula();
        var webRoot = WebRoot.Built();
        try
        {
            den.Write("den.yaml", "format: [not a number\nremote_images: true\n");
            await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = webRoot });

            Assert.Equal(Page, await PolicyAsync(server, "/", HttpStatusCode.OK));

            File.Delete(Path.Combine(den.Root, "den.yaml"));
            Assert.Equal(Page, await PolicyAsync(server, "/", HttpStatusCode.OK));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    private static async Task<string> PolicyAsync(Server server, string path, HttpStatusCode expected)
    {
        using var response = await server.Client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.True(expected == response.StatusCode, $"{path}: expected {expected}, got {response.StatusCode}");
        return Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
    }
}
