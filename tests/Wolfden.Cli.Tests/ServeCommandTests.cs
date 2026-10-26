using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Wolfden.Cli.Tests;

/// <summary><c>wolfden serve</c>: the API and web UI on 127.0.0.1 (#23).</summary>
public class ServeCommandTests
{
    [Fact]
    public void HelpDescribesTheServer()
    {
        var (exitCode, output, _) = Cli.Run("serve", "--help");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.StartsWith("usage: wolfden serve [<den>] [--den <den>] [--universe <key>] [--port <n>] [--no-open] [--no-watch]", output, StringComparison.Ordinal);
        Assert.Contains("(default 9653)", output, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", output, StringComparison.Ordinal);
        Assert.Contains("serve [<den>]", Cli.Run("--help").Output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-1")]
    public void WithoutADenItDoesntStart()
    {
        using var empty = TempDen.Empty();

        var (exitCode, output, error) = Cli.Run("serve", empty.Root, "--no-open");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal("", output);
        Assert.Contains("has no den.yaml", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("65536")]
    [InlineData("+80")]
    [InlineData("")]
    public void APortThatIsntOneExitsTwo(string port)
    {
        var (exitCode, output, error) = Cli.Run("serve", TempDen.DraculaRoot, "--no-open", "--port", port);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal("", output);
        Assert.StartsWith($"wolfden serve: the port must be a whole number from 1 to 65535, or 0 for a free one, not '{port}'.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AUniverseTheDenDoesntHaveExitsTwo()
    {
        var (exitCode, _, error) = Cli.Run("serve", TempDen.DraculaRoot, "--no-open", "--universe", "nowhere");
        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal("wolfden serve: the den has no universe 'nowhere'. Its universes: dracula.\n", error);

        var (star, _, starError) = Cli.Run("serve", TempDen.DraculaRoot, "--no-open", "--universe", "*");
        Assert.Equal(CliApp.ExitUsage, star);
        Assert.StartsWith("wolfden serve: '*' (every universe) can't be pinned", starError, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-2")]
    public void APortInUseExitsTwoAndSaysSo()
    {
        using var den = TempDen.DraculaCopy();
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            var port = ((IPEndPoint)taken.LocalEndpoint).Port;

            var (exitCode, output, error) = Cli.Run("serve", den.Root, "--no-open", "--no-watch", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Assert.Equal(CliApp.ExitUsage, exitCode);
            Assert.Equal("", output);
            Assert.Equal($"wolfden serve: port {port} on 127.0.0.1 is in use, perhaps by another 'wolfden serve'. Stop that, or pick another port with '--port <n>'.\n", error);
        }
        finally
        {
            taken.Stop();
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-1")]
    public async Task ItServesTheApiAndOpensABrowserUntilStopped()
    {
        using var den = TempDen.DraculaCopy();
        var token = TestContext.Current.CancellationToken;
        var opened = new List<string>();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeCommand.BrowserOpener = opened.Add;
        ServeCommand.WebRoot = Path.Combine(den.Root, "no-ui-here");
        ServeCommand.Started = url => started.TrySetResult(url);
        using var stop = new CancellationTokenSource();
        var output = new StringWriter();
        var error = new StringWriter();
        try
        {
            var serving = Task.Run(() => CliApp.RunUntil(["serve", den.Root, "--port", "0"], output, error, stop.Token), token);
            var url = await started.Task.WaitAsync(TimeSpan.FromSeconds(30), token);

            Assert.StartsWith("http://127.0.0.1:", url, StringComparison.Ordinal);
            Assert.Equal([url], opened);
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            Assert.Contains("\"status\":\"ok\"", await client.GetStringAsync("/api/health", token), StringComparison.Ordinal);
            using var den1 = JsonDocument.Parse(await client.GetStringAsync("/api/den", token));
            Assert.Equal("Dracula", den1.RootElement.GetProperty("name").GetString());
            Assert.Contains("npm run build", await client.GetStringAsync("/", token), StringComparison.Ordinal);

            await stop.CancelAsync();
            Assert.Equal(CliApp.ExitOk, await serving.WaitAsync(TimeSpan.FromSeconds(30), token));
            Assert.StartsWith($"wolfden serve: serving the den at '{Path.GetFullPath(den.Root)}' on {url}", output.ToString(), StringComparison.Ordinal);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("/api/health", token));
        }
        finally
        {
            await stop.CancelAsync();
            ServeCommand.BrowserOpener = null;
            ServeCommand.WebRoot = null;
            ServeCommand.Started = null;
        }
    }

    [Fact]
    public async Task NoOpenDoesntOpenABrowser()
    {
        using var den = TempDen.DraculaCopy();
        var token = TestContext.Current.CancellationToken;
        var opened = new List<string>();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeCommand.BrowserOpener = opened.Add;
        ServeCommand.Started = url => started.TrySetResult(url);
        using var stop = new CancellationTokenSource();
        try
        {
            var serving = Task.Run(() => CliApp.RunUntil(["serve", den.Root, "--port", "0", "--no-open", "--no-watch"], new StringWriter(), new StringWriter(), stop.Token), token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), token);

            await stop.CancelAsync();
            Assert.Equal(CliApp.ExitOk, await serving.WaitAsync(TimeSpan.FromSeconds(30), token));
            Assert.Empty(opened);
        }
        finally
        {
            await stop.CancelAsync();
            ServeCommand.BrowserOpener = null;
            ServeCommand.Started = null;
        }
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-2")]
    [InlineData(SocketError.AccessDenied)]
    [InlineData(SocketError.AddressNotAvailable)]
    public void APortTheSystemWontLetItUseExitsTwoAndSaysSo(SocketError socketError)
    {
        using var den = TempDen.DraculaCopy();
        ServeCommand.StartOverride = (_, _) => throw new SocketException((int)socketError);
        try
        {
            var (exitCode, output, error) = Cli.Run("serve", den.Root, "--no-open", "--no-watch", "--port", "50000");

            Assert.Equal(CliApp.ExitUsage, exitCode);
            Assert.Equal("", output);
            Assert.StartsWith("wolfden serve: couldn't listen on 127.0.0.1 port 50000: ", error, StringComparison.Ordinal);
            Assert.EndsWith("(the port may be reserved; try another with '--port <n>', or '--port 0' for a free one)\n", error, StringComparison.Ordinal);
            Assert.Single(error.TrimEnd('\n').Split('\n'));
        }
        finally
        {
            ServeCommand.StartOverride = null;
        }
    }

    [Fact]
    public void AFailureToStartThatIsntAboutThePortIsntReportedAsOne()
    {
        Assert.Null(ServeCommand.StartFailure(new InvalidOperationException("something else"), 9653));
        Assert.Contains("is in use", ServeCommand.StartFailure(new IOException("bind", new Microsoft.AspNetCore.Connections.AddressInUseException("in use")), 9653), StringComparison.Ordinal);
    }

    /// <summary>
    /// The built UI that dotnet build copied next to wolfden (web/dist as
    /// wwwroot), served for real. Skipped when the UI wasn't built first,
    /// unless WOLFDEN_REQUIRE_BUILT_UI is 1, as in CI's web job.
    /// </summary>
    [Fact]
    [Trait("Scenario", "SC-WEB-1")]
    public async Task TheBuiltUiIsServedFromWwwroot()
    {
        var index = Path.Combine(ServeCommand.DefaultWebRoot, "index.html");
        if (!File.Exists(index))
        {
            Assert.False(Environment.GetEnvironmentVariable("WOLFDEN_REQUIRE_BUILT_UI") == "1", $"There's no built UI at {index}: build web/ before the solution.");
            Assert.Skip("The web UI isn't built (npm run build in web/), so dotnet build didn't copy it.");
        }

        using var den = TempDen.DraculaCopy();
        var token = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeCommand.Started = url => started.TrySetResult(url);
        using var stop = new CancellationTokenSource();
        try
        {
            var serving = Task.Run(() => CliApp.RunUntil(["serve", den.Root, "--port", "0", "--no-open", "--no-watch"], new StringWriter(), new StringWriter(), stop.Token), token);
            using var client = new HttpClient { BaseAddress = new Uri(await started.Task.WaitAsync(TimeSpan.FromSeconds(30), token)) };

            using var page = await client.GetAsync("/", token);
            var html = await page.Content.ReadAsStringAsync(token);
            Assert.Equal(await File.ReadAllTextAsync(index, token), html);
            Assert.True(page.Headers.CacheControl?.NoCache);

            // The page's Content-Security-Policy allows no inline script or
            // style, so the built page must have none (#180).
            Assert.Equal(
                "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
                Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
            Assert.All(System.Text.RegularExpressions.Regex.Matches(html, @"<script\b[^>]*>").Select(m => m.Value), tag => Assert.Contains(" src=", tag, StringComparison.Ordinal));
            Assert.DoesNotMatch(@"<style\b|\sstyle=|\son[a-z]+=", html);
            var script = System.Text.RegularExpressions.Regex.Match(html, @"src=""(/assets/[^""]+\.js)""").Groups[1].Value;
            using var asset = await client.GetAsync(script, token);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal("text/javascript", asset.Content.Headers.ContentType?.MediaType);
            Assert.Equal(html, await client.GetStringAsync("/universes/dracula", token));

            await stop.CancelAsync();
            Assert.Equal(CliApp.ExitOk, await serving.WaitAsync(TimeSpan.FromSeconds(30), token));
        }
        finally
        {
            await stop.CancelAsync();
            ServeCommand.Started = null;
        }
    }
}
