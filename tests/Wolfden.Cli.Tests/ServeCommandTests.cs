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
}
