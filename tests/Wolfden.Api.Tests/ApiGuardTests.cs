using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;

namespace Wolfden.Api.Tests;

/// <summary>What the server refuses, how it fails, and the headers it sends (#23, review of #167).</summary>
public class ApiGuardTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Scenario", "SC-WEB-3")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Origin", "https://attacker.example")]
    [InlineData("Origin", "http://attacker.example")]
    [InlineData("Origin", "null")]
    [InlineData("Origin", "http://localhost:1")]
    [InlineData("Origin", "https://127.0.0.1:{port}")]
    public async Task ACrossSiteRequestIsRefusedWithoutCorsHeaders(string header, string value)
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/den");
        request.Headers.Add(header, value.Replace("{port}", server.Port, StringComparison.Ordinal));
        using var response = await server.Client.SendAsync(request, Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"code\":\"cross_site\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Dracula", body, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-3")]
    [InlineData(null, null)]
    [InlineData("Sec-Fetch-Site", "same-origin")]
    [InlineData("Sec-Fetch-Site", "none")]
    [InlineData("Origin", "http://127.0.0.1:{port}")]
    [InlineData("Origin", "http://localhost:{port}")]
    public async Task ARequestFromItsOwnPageOrWithoutABrowserIsAnswered(string? header, string? value)
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/den");
        if (header is not null)
        {
            request.Headers.Add(header, value!.Replace("{port}", server.Port, StringComparison.Ordinal));
        }

        using var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ACrossSiteRequestForThePageIsntTheApisToRefuse()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var response = await server.Client.SendAsync(request, Token);

        // A link from another site opens the page; only the API answers no.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnIndexThatCantBeBuiltIsDenUnreadable()
    {
        using var den = TempDen.Dracula();
        den.Write(".wolfden", "a file where the index folder should be\n");
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var json = await server.GetJsonAsync("/api/den", HttpStatusCode.ServiceUnavailable);

        Assert.Equal("den_unreadable", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnUnexpectedFailureIsAJsonInternalError()
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(
            new WolfdenApiOptions(den.Root),
            app => app.MapGet("/api/test-failure", string () => throw new InvalidOperationException("a test failure")));

        var json = await server.GetJsonAsync("/api/test-failure", HttpStatusCode.InternalServerError);

        Assert.Equal("internal_error", json.GetProperty("code").GetString());
        Assert.Contains("a test failure", json.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST", "/api/den")]
    [InlineData("DELETE", "/api/health")]
    [InlineData("PUT", "/api/den/")]
    public async Task AWrongMethodOnAKnownPathIsMethodNotAllowed(string method, string path)
    {
        using var den = TempDen.Dracula();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        using var response = await server.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("\"code\":\"method_not_allowed\"", body, StringComparison.Ordinal);
        Assert.Equal(["GET"], response.Content.Headers.Allow);
    }

    [Fact]
    public async Task PagesAreNotCachedAndNothingIsSniffed()
    {
        using var den = TempDen.Dracula();
        var webRoot = WebRoot.Built();
        try
        {
            await using var built = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = webRoot });
            await using var notBuilt = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = Path.Combine(den.Root, "no-ui-here") });

            foreach (var (server, path) in new[] { (built, "/"), (built, "/index.html"), (built, "/universes/dracula"), (notBuilt, "/") })
            {
                using var response = await server.Client.GetAsync(path, Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoCache, $"{path} should be sent with Cache-Control: no-cache");
                Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
            }

            using var api = await built.Client.GetAsync("/api/health", Token);
            Assert.Equal(["nosniff"], api.Headers.GetValues("X-Content-Type-Options"));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/assets/../../secret.txt")]
    [InlineData("/..%2fsecret.txt")]
    [InlineData("/assets/..%2f..%2fsecret.txt")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/..\\secret.txt")]
    public async Task APathCantReachAFileOutsideTheUi(string path)
    {
        using var den = TempDen.Dracula();
        var webRoot = WebRoot.Built();
        var secret = Path.Combine(Path.GetDirectoryName(webRoot)!, "secret.txt");
        File.WriteAllText(secret, "not for the browser");
        try
        {
            await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { WebRoot = webRoot });

            // Raw, so nothing on the client side tidies the path first.
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, int.Parse(server.Port, System.Globalization.CultureInfo.InvariantCulture), Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"), Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var response = await reader.ReadToEndAsync(Token);

            Assert.DoesNotContain("not for the browser", response, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secret);
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public void NonLoopbackAddressesNamesEveryAddressBeyondThisComputer()
    {
        using var den = TempDen.Dracula();
        var app = WolfdenApi.Build(new WolfdenApiOptions(den.Root) { Port = 0 });
        app.Urls.Add("http://127.0.0.1:5000");
        app.Urls.Add("http://[::1]:5000");
        app.Urls.Add("http://0.0.0.0:5000");
        app.Urls.Add("http://localhost:5000");
        app.Urls.Add("http://192.0.2.1:5000");

        // localhost is a name, which can resolve elsewhere: only loopback addresses count.
        Assert.Equal(["http://0.0.0.0:5000", "http://localhost:5000", "http://192.0.2.1:5000"], WolfdenApi.NonLoopbackAddresses(app));
    }
}

/// <summary>Tests that change the process's environment, so they run alone.</summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>Configuration can't make the server listen beyond 127.0.0.1 (review of #167).</summary>
[Collection(nameof(ProcessEnvironment))]
public class ListenerConfigurationTests
{
    [Fact]
    [Trait("Scenario", "SC-WEB-3")]
    public async Task EnvironmentVariablesAndAppSettingsAddNoListener()
    {
        using var den = TempDen.Dracula();
        string[] variables = ["Kestrel__Endpoints__Extra__Url", "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "URLS"];
        var settings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.Delete(settings); // left over from a run that crashed
        try
        {
            Environment.SetEnvironmentVariable(variables[0], "http://0.0.0.0:0");
            Environment.SetEnvironmentVariable(variables[1], "http://0.0.0.0:0");
            Environment.SetEnvironmentVariable(variables[2], "0");
            Environment.SetEnvironmentVariable(variables[3], "http://0.0.0.0:0");
            File.WriteAllText(settings, """{ "Kestrel": { "Endpoints": { "FromFile": { "Url": "http://0.0.0.0:0" } } } }""");

            await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

            Assert.StartsWith("http://127.0.0.1:", Assert.Single(server.App.Urls), StringComparison.Ordinal);
            Assert.Empty(WolfdenApi.NonLoopbackAddresses(server.App));
        }
        finally
        {
            foreach (var variable in variables)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }

            File.Delete(settings);
        }
    }
}
