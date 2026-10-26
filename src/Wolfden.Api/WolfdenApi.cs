using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wolfden.Api;

/// <summary>
/// Wolfden's web server (design.md §9, #23): the minimal API under
/// <c>/api</c> (<see cref="DenEndpoints"/>) and the built web UI, for one
/// den. <c>wolfden serve</c> hosts it in-process.
/// </summary>
/// <remarks>
/// <para>
/// v0 is local-first and single-user, with no accounts and no
/// authentication, so the server listens on 127.0.0.1 only, never on
/// another network interface, and answers only requests addressed to
/// <c>localhost</c> or <c>127.0.0.1</c>. The host check keeps a web page
/// on another site from reaching the den through a DNS name that resolves
/// to this machine (DNS rebinding). Hosting for several users is a later
/// phase (design.md §10), with its own authentication.
/// </para>
/// </remarks>
public static class WolfdenApi
{
    /// <summary>The port <c>wolfden serve</c> uses unless told otherwise: 9653 spells WOLF on a phone keypad.</summary>
    public const int DefaultPort = 9653;

    /// <summary>The host names the server answers to; any other <c>Host</c> header gets 400.</summary>
    public static readonly IReadOnlyList<string> AllowedHosts = ["localhost", "127.0.0.1"];

    /// <summary>
    /// Builds the server for <paramref name="options"/>, listening on
    /// 127.0.0.1 at <see cref="WolfdenApiOptions.Port"/>. Start it with
    /// <c>StartAsync</c>; a port that's in use fails there, with an
    /// <see cref="IOException"/>.
    /// </summary>
    public static WebApplication Build(WolfdenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(WolfdenApi).Assembly.GetName().Name,
            // Not the current folder, so an appsettings.json there changes nothing.
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));
        builder.Services.AddHostFiltering(hosts =>
        {
            hosts.AllowedHosts = [.. AllowedHosts];
            hosts.AllowEmptyHosts = false;
        });
        builder.Services.Configure<ConsoleLifetimeOptions>(lifetime => lifetime.SuppressStatusMessages = true);
        builder.Logging.ClearProviders();
        if (options.Log is { } log)
        {
            builder.Logging.AddProvider(new TextWriterLoggerProvider(log)).SetMinimumLevel(LogLevel.Warning)
                // A failure to start, such as a port in use, is thrown from StartAsync for the caller to report.
                .AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);
        }

        var app = builder.Build();
        app.UseHostFiltering();
        MapWebUi(app, options.WebRoot);
        app.MapWolfdenApi(options);
        return app;
    }

    /// <summary>
    /// Serves the built UI from <paramref name="webRoot"/>, with
    /// <c>index.html</c> for any other path that isn't a file, so the UI's
    /// own routes work on reload. Without a built UI, <c>/</c> says how to
    /// build one.
    /// </summary>
    private static void MapWebUi(WebApplication app, string? webRoot)
    {
        if (webRoot is null || !File.Exists(Path.Combine(webRoot, "index.html")))
        {
            app.MapGet("/", () => Results.Content(NotBuiltPage, "text/html; charset=utf-8"));
            return;
        }

        var files = new PhysicalFileProvider(Path.GetFullPath(webRoot));
        app.Lifetime.ApplicationStopped.Register(files.Dispose);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
    }

    /// <summary>What <c>/</c> shows when the web UI isn't built.</summary>
    internal const string NotBuiltPage =
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Wolfden</title>
        </head>
        <body style="font-family: system-ui, sans-serif; max-width: 40rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.5">
          <h1>Wolfden</h1>
          <p>The server is running, but this copy of Wolfden was built without its web UI.</p>
          <p>To build it, run <code>npm ci</code> and <code>npm run build</code> in the repository's <code>web/</code> folder
          (Node.js 22.12 or later), then <code>dotnet build</code> again and restart <code>wolfden serve</code>.</p>
          <p>The API works without it: <a href="/api/den">/api/den</a>.</p>
        </body>
        </html>
        """;
}
