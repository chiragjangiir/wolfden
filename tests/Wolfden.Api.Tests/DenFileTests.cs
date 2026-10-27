using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Wolfden.Api.Tests;

/// <summary>
/// The den's own images, served under <c>/den-files</c> for the notes that
/// show them (format.md F-LAY-9, #186), and everything that isn't one,
/// which is a 404.
/// </summary>
public class DenFileTests
{
    private const string FilePolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; sandbox";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>";

    private static void WriteBytes(TempDen den, string path, byte[] bytes)
    {
        var full = Path.Combine(den.Root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-38")]
    public async Task AnImageInTheDenIsServedWithItsTypeAndAPolicyThatRunsNothing()
    {
        using var den = TempDen.Dracula();
        WriteBytes(den, "universes/dracula/images/castle map.png", Png);
        den.Write("images/seal.SVG", Svg);
        den.Write("images/photo.JPEG", "jpeg");
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var token = TestContext.Current.CancellationToken;

        using var png = await server.Client.GetAsync("/den-files/universes/dracula/images/castle%20map.png", token);
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await png.Content.ReadAsByteArrayAsync(token));
        AssertFileHeaders(png);

        using var svg = await server.Client.GetAsync("/den-files/images/seal.SVG", token);
        Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
        AssertFileHeaders(svg);
        Assert.Contains("sandbox", Assert.Single(svg.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);

        using var jpeg = await server.Client.GetAsync("/den-files/images/photo.JPEG", token);
        Assert.Equal("image/jpeg", jpeg.Content.Headers.ContentType?.MediaType);

        // HEAD works too, without the body.
        using var head = await server.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/den-files/images/seal.SVG"), token);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(token));
    }

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.avif", "image/avif")]
    [InlineData("a.svg", "image/svg+xml")]
    [InlineData("A.PNG", "image/png")]
    public async Task EachImageTypeHasItsContentType(string name, string type)
    {
        using var den = TempDen.Dracula();
        WriteBytes(den, "images/" + name, Png);
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        using var response = await server.Client.GetAsync("/den-files/images/" + name, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-38")]
    public async Task TheBrowserRevalidatesAndGetsA304WhileTheFileIsTheSame()
    {
        using var den = TempDen.Dracula();
        WriteBytes(den, "images/map.png", Png);
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var token = TestContext.Current.CancellationToken;

        using var first = await server.Client.GetAsync("/den-files/images/map.png", token);
        Assert.Equal("no-cache", first.Headers.CacheControl?.ToString());
        var tag = first.Headers.ETag;
        Assert.NotNull(tag);
        Assert.NotNull(first.Content.Headers.LastModified);

        using var again = new HttpRequestMessage(HttpMethod.Get, "/den-files/images/map.png");
        again.Headers.IfNoneMatch.Add(tag);
        using var unchanged = await server.Client.SendAsync(again, token);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);

        // The writer replaces the image: a new tag, and the new bytes.
        WriteBytes(den, "images/map.png", [.. Png, 4, 5]);
        File.SetLastWriteTimeUtc(Path.Combine(den.Root, "images", "map.png"), DateTime.UtcNow.AddMinutes(1));
        using var afterEdit = new HttpRequestMessage(HttpMethod.Get, "/den-files/images/map.png");
        afterEdit.Headers.IfNoneMatch.Add(tag);
        using var changed = await server.Client.SendAsync(afterEdit, token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(Png.Length + 2, (await changed.Content.ReadAsByteArrayAsync(token)).Length);
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-39")]
    [InlineData("/den-files/den.yaml")] // not an image
    [InlineData("/den-files/universes/dracula/universe.md")]
    [InlineData("/den-files/images/notes.txt")]
    [InlineData("/den-files/images/missing.png")] // not there
    [InlineData("/den-files/images")] // a folder
    [InlineData("/den-files/images/")]
    [InlineData("/den-files/")]
    [InlineData("/den-files")]
    [InlineData("/den-files/.hidden.png")] // hidden, at any depth
    [InlineData("/den-files/images/.hidden.png")]
    [InlineData("/den-files/.obsidian/pic.png")]
    [InlineData("/den-files/.wolfden/pic.png")]
    [InlineData("/den-files/.git/pic.png")]
    [InlineData("/den-files/%2Ewolfden/pic.png")] // encoded
    [InlineData("/den-files/images%2F.hidden.png")]
    [InlineData("/den-files/images/%2E%2E/%2E%2E/outside.png")]
    [InlineData("/den-files/images/..%2F..%2Foutside.png")]
    [InlineData("/den-files/images%5Cmap.png")] // a backslash
    [InlineData("/den-files/images//map.png")] // an empty name
    [InlineData("/den-files/images/map.png%20")] // Windows would drop the space
    [InlineData("/den-files/images/map.png.")]
    [InlineData("/den-files/images/map.png%3A%3A%24DATA")] // an NTFS stream
    [InlineData("/den-files/images/notes.txt%3Ahidden.png")]
    [InlineData("/den-files/C%3A/images/map.png")] // a drive
    [InlineData("/den-files/images/m%2Ap.png")] // a wildcard
    [InlineData("/den-files/images/ma%3F.png")]
    public async Task AnythingButAnImageInTheDenIsA404(string path)
    {
        using var den = TempDen.Dracula();
        WriteBytes(den, "images/map.png", Png);
        den.Write("images/notes.txt", "notes");
        WriteBytes(den, ".hidden.png", Png);
        WriteBytes(den, "images/.hidden.png", Png);
        WriteBytes(den, ".obsidian/pic.png", Png);
        WriteBytes(den, ".wolfden/pic.png", Png);
        WriteBytes(den, ".git/pic.png", Png);
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        await AssertNotFoundAsync(server, path);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("images/../../outside.png")]
    [InlineData("/images/map.png")]
    [InlineData("C:/images/map.png")]
    [InlineData("C:map.png")]
    [InlineData("\\\\server\\share\\map.png")]
    [InlineData("images\\map.png")]
    [InlineData("images/./map.png")]
    [InlineData("images/WOLFDE~1/pic.png")]
    [InlineData("")]
    [InlineData(null)]
    public void PathsThatLeaveTheDenOrArentWrittenAsDenPathsAreRefused(string? path)
    {
        using var den = TempDen.Dracula();
        WriteBytes(den, "images/map.png", Png);
        WriteBytes(den, "images/.wolfden/pic.png", Png);

        Assert.Null(DenFiles.Find(den.Root, path));
        Assert.NotNull(DenFiles.Find(den.Root, "images/map.png"));
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-39")]
    [InlineData("/den-files/images/../../outside.png")]
    [InlineData("/den-files/images/../.wolfden/pic.png")]
    [InlineData("/den-files/images/%2E%2E/%2E%2E/outside.png")]
    [InlineData("/den-files/images/%2e%2e%2f%2e%2e%2foutside.png")]
    [InlineData("/den-files/images/..%5C..%5Coutside.png")]
    public async Task AnEscapeSentAsWrittenIsA404(string path)
    {
        using var den = TempDen.Dracula();
        WriteBytes(den, "images/map.png", Png);
        WriteBytes(den, ".wolfden/pic.png", Png);
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        // A raw request, since HttpClient would take out the dot segments itself.
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, int.Parse(server.Port, System.Globalization.CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);
        await using var stream = tcp.GetStream();
        var request = System.Text.Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        var status = await reader.ReadLineAsync(TestContext.Current.CancellationToken);

        Assert.Equal("HTTP/1.1 404 Not Found", status);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-39")]
    public async Task APinnedServerServesOnlyItsUniverseAndTheDenLevelImages()
    {
        using var den = TempDen.Starfall();
        WriteBytes(den, "universes/starfall/images/ship.png", Png);
        WriteBytes(den, "universes/drift/images/station.png", Png);
        WriteBytes(den, "images/crest.png", Png);
        WriteBytes(den, "universes/banner.png", Png);
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { PinnedUniverse = "starfall" });
        var token = TestContext.Current.CancellationToken;

        foreach (var path in new[] { "universes/starfall/images/ship.png", "images/crest.png", "universes/banner.png" })
        {
            using var served = await server.Client.GetAsync("/den-files/" + path, token);
            Assert.True(served.StatusCode == HttpStatusCode.OK, $"{path}: {served.StatusCode}");
        }

        await AssertNotFoundAsync(server, "/den-files/universes/drift/images/station.png");
        if (OperatingSystem.IsWindows())
        {
            // Another case names the same folder on Windows; it's still another universe's.
            await AssertNotFoundAsync(server, "/den-files/Universes/DRIFT/images/station.png");
        }

        Assert.Null(DenFiles.Find(den.Root, "universes/drift/images/station.png", "starfall"));
        Assert.NotNull(DenFiles.Find(den.Root, "universes/drift/images/station.png", "drift"));
    }

    [Fact]
    public async Task AnUnpinnedServerServesEveryUniversesImages()
    {
        using var den = TempDen.Starfall();
        WriteBytes(den, "universes/starfall/images/ship.png", Png);
        WriteBytes(den, "universes/drift/images/station.png", Png);
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var token = TestContext.Current.CancellationToken;

        foreach (var path in new[] { "universes/starfall/images/ship.png", "universes/drift/images/station.png" })
        {
            using var served = await server.Client.GetAsync("/den-files/" + path, token);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        }
    }

    [Fact]
    public void APinnedServerDoesntFollowAFileLinkIntoAnotherUniverse()
    {
        using var den = TempDen.Starfall();
        WriteBytes(den, "universes/drift/images/station.png", Png);
        LinkFile(Path.Combine(den.Root, "universes", "starfall", "station.png"), Path.Combine(den.Root, "universes", "drift", "images", "station.png"));

        Assert.Null(DenFiles.Find(den.Root, "universes/starfall/station.png", "starfall"));
        Assert.NotNull(DenFiles.Find(den.Root, "universes/starfall/station.png"));
    }

    [Fact]
    public async Task ARequestFromAnotherSiteIsA404()
    {
        using var den = TempDen.Dracula();
        WriteBytes(den, "images/map.png", Png);
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var token = TestContext.Current.CancellationToken;

        using var crossSite = new HttpRequestMessage(HttpMethod.Get, "/den-files/images/map.png");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var refused = await server.Client.SendAsync(crossSite, token);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        using var otherOrigin = new HttpRequestMessage(HttpMethod.Get, "/den-files/images/map.png");
        otherOrigin.Headers.Add("Origin", "https://evil.example");
        using var refusedToo = await server.Client.SendAsync(otherOrigin, token);
        Assert.Equal(HttpStatusCode.NotFound, refusedToo.StatusCode);

        using var sameOrigin = new HttpRequestMessage(HttpMethod.Get, "/den-files/images/map.png");
        sameOrigin.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var served = await server.Client.SendAsync(sameOrigin, token);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-39")]
    public async Task AFolderLinkIsntFollowedEvenToImagesInTheDen()
    {
        using var den = TempDen.Dracula();
        using var outside = TempDen.Dracula();
        WriteBytes(outside, "secret/pic.png", Png);
        WriteBytes(den, "images/map.png", Png);
        using var away = LinkFolder(Path.Combine(den.Root, "linked"), Path.Combine(outside.Root, "secret"));
        using var within = LinkFolder(Path.Combine(den.Root, "also-images"), Path.Combine(den.Root, "images"));
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        await AssertNotFoundAsync(server, "/den-files/linked/pic.png");
        await AssertNotFoundAsync(server, "/den-files/also-images/map.png");
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-39")]
    public async Task AFileLinkIsServedOnlyIfItLeadsToAnImageInTheDen()
    {
        using var den = TempDen.Dracula();
        using var outside = TempDen.Dracula();
        WriteBytes(outside, "secret.png", Png);
        WriteBytes(den, "images/map.png", Png);
        WriteBytes(den, ".wolfden/pic.png", Png);
        LinkFile(Path.Combine(den.Root, "images", "away.png"), Path.Combine(outside.Root, "secret.png"));
        LinkFile(Path.Combine(den.Root, "images", "hidden.png"), Path.Combine(den.Root, ".wolfden", "pic.png"));
        LinkFile(Path.Combine(den.Root, "images", "yaml.png"), Path.Combine(den.Root, "den.yaml"));
        LinkFile(Path.Combine(den.Root, "images", "same.png"), Path.Combine(den.Root, "images", "map.png"));
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        await AssertNotFoundAsync(server, "/den-files/images/away.png");
        await AssertNotFoundAsync(server, "/den-files/images/hidden.png");
        await AssertNotFoundAsync(server, "/den-files/images/yaml.png");
        using var same = await server.Client.GetAsync("/den-files/images/same.png", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
    }

    [Fact]
    public async Task DenFilesDontHideTheUisOwnRoutes()
    {
        using var den = TempDen.Dracula();
        var webRoot = WebRoot.Built();
        try
        {
            WriteBytes(den, "u/dracula/pic.png", Png);
            await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = webRoot });
            var token = TestContext.Current.CancellationToken;

            // A den folder named like a UI route is still the UI there, and the den file only under /den-files.
            using var page = await server.Client.GetAsync("/u/dracula/pic.png", token);
            Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
            using var file = await server.Client.GetAsync("/den-files/u/dracula/pic.png", token);
            Assert.Equal("image/png", file.Content.Headers.ContentType?.MediaType);

            // A missing den file is a 404, not the UI's page.
            await AssertNotFoundAsync(server, "/den-files/u/dracula/missing.png");
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    private static void AssertFileHeaders(HttpResponseMessage response)
    {
        Assert.Equal(FilePolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
    }

    private static async Task AssertNotFoundAsync(Server server, string path)
    {
        using var response = await server.Client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path}: expected 404, got {response.StatusCode}");
        Assert.NotEqual("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    private static IDisposable LinkFolder(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return new Removal(() => Directory.Delete(link));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows without developer mode or admin rights; try a junction, which needs neither.
        }

        if (OperatingSystem.IsWindows())
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            mklink.WaitForExit();
            if (mklink.ExitCode == 0 && Directory.Exists(link))
            {
                return new Removal(() => Directory.Delete(link));
            }
        }

        Assert.Skip("This system doesn't allow folder links to be created.");
        return null!;
    }

    private static void LinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This system doesn't allow file links to be created.");
        }
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
