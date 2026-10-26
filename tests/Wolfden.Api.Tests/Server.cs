using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace Wolfden.Api.Tests;

/// <summary>A server started on a free port, with a client for it.</summary>
internal sealed class Server : IAsyncDisposable
{
    private Server(WebApplication app)
    {
        App = app;
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    /// <summary>The port it listens on, as text.</summary>
    public string Port => new Uri(App.Urls.Single()).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<Server> StartAsync(WolfdenApiOptions options, Action<WebApplication>? configure = null)
    {
        var app = WolfdenApi.Build(new WolfdenApiOptions(options.DenRoot)
        {
            Port = 0,
            PinnedUniverse = options.PinnedUniverse,
            WebRoot = options.WebRoot,
            Version = options.Version,
            Log = options.Log,
        });
        configure?.Invoke(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new Server(app);
    }

    public async Task<JsonElement> GetJsonAsync(string path, HttpStatusCode expected)
    {
        using var response = await Client.GetAsync(path, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(expected == response.StatusCode, $"{path}: expected {expected}, got {response.StatusCode}: {text}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
    }
}

/// <summary>A stand-in for a built UI (web/dist) in a temporary folder.</summary>
internal static class WebRoot
{
    public const string IndexHtml = "<!doctype html><title>Wolfden test UI</title><script src=\"/assets/app.js\"></script>";
    public const string Script = "console.log('wolfden');";

    public static string Built()
    {
        var root = Path.Combine(Path.GetTempPath(), "wolfden-api-tests", "ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(root, "assets", "app.js"), Script);
        return root;
    }
}
