using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Wolfden.Core.Structure;

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
/// another network interface. No configuration can add a listener: not
/// environment variables (<c>ASPNETCORE_URLS</c> is overridden, with a
/// warning), a command line, or an <c>appsettings.json</c>. It answers only requests addressed to
/// <c>localhost</c> or <c>127.0.0.1</c>, which keeps a web page on another
/// site from reaching the den through a DNS name that resolves to this
/// machine (DNS rebinding), and the API refuses requests a browser marks as
/// coming from another site (<see cref="CrossSiteGuard"/>). It sends no
/// CORS headers, so a browser never lets another site read an answer.
/// Hosting for several users is a later phase (design.md §10), with its own
/// authentication.
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
    /// <c>StartAsync</c>; a port that's in use fails there with an
    /// <see cref="IOException"/>, and one the system won't let it use
    /// (reserved, or below 1024 on Linux) with a
    /// <see cref="System.Net.Sockets.SocketException"/>.
    /// </summary>
    public static WebApplication Build(WolfdenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(WolfdenApi).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        // No configuration may add a listener: drop the environment (Kestrel__Endpoints__…)
        // and an appsettings.json next to wolfden. The host has already read
        // ASPNETCORE_URLS and its kin by now; the Listen below overrides them,
        // and Kestrel says so in a warning.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
            kestrel.Listen(IPAddress.Loopback, options.Port);
        });
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
        app.UseExceptionHandler(errors => errors.Run(InternalError));
        app.Use((context, next) =>
        {
            context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            context.Response.OnStarting(() =>
            {
                AddContentSecurityPolicy(context.Response, options.DenRoot);
                return Task.CompletedTask;
            });
            return next(context);
        });
        app.UseHostFiltering();
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), api => api.Use(CrossSiteGuard));
        MapWebUi(app, options.WebRoot);
        app.MapWolfdenApi(options);
        return app;
    }

    /// <summary>The addresses <paramref name="app"/> listens on that aren't on the loopback interface. After <c>StartAsync</c>, there should be none.</summary>
    public static IReadOnlyList<string> NonLoopbackAddresses(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Urls
            .Where(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip))
            .ToList();
    }

    /// <summary>
    /// Refuses an API request a browser says comes from another site:
    /// <c>Sec-Fetch-Site: cross-site</c>, or an <c>Origin</c> that isn't
    /// this server (<c>localhost</c> or <c>127.0.0.1</c> on the port the
    /// request came in on). Requests without either header, such as
    /// <c>curl</c>'s, and the browser's same-origin requests pass. The Vite
    /// dev server's proxy drops the <c>Origin</c> of its own page, which has
    /// another port, and passes any other on (web/vite.config.ts).
    /// </summary>
    internal static Task CrossSiteGuard(HttpContext context, RequestDelegate next)
    {
        var headers = context.Request.Headers;
        var crossSite = string.Equals(headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase);
        var origin = headers.Origin.ToString();
        if (!crossSite && (origin.Length == 0 || IsThisServer(origin, context.Connection.LocalPort)))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new ApiError(
            "cross_site", "Wolfden's API answers only its own pages, not requests from another site."));

        static bool IsThisServer(string origin, int port) =>
            Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            && uri.Port == port;
    }

    /// <summary>
    /// The Content-Security-Policy of the UI's pages (#180). The built UI
    /// loads its script and stylesheet from this server and has no inline
    /// script or style. Images may come from this server or a <c>data:</c>
    /// URL (the page's empty icon). The UI asks only this server's API.
    /// Plugins, a <c>&lt;base&gt;</c>, and framing by any page are refused.
    /// </summary>
    internal const string PagePolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    /// <summary><see cref="PagePolicy"/> for a den with <c>remote_images: true</c> (format.md F-DEN-8): images may come from websites too.</summary>
    internal const string PagePolicyWithRemoteImages =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https: http:; connect-src 'self'; " +
        "font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    /// <summary>
    /// The policy of everything that isn't a page, such as the API's JSON
    /// and the UI's scripts: none of it should ever load anything.
    /// </summary>
    internal const string NonPagePolicy = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

    /// <summary>
    /// The policy of the page that says how to build the UI: its only
    /// content is its own inline style, and nothing in it comes from the den.
    /// </summary>
    internal const string NotBuiltPagePolicy = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

    /// <summary>
    /// Adds a Content-Security-Policy to a response that hasn't one: <see
    /// cref="PagePolicy"/> for a page, or <see cref="NonPagePolicy"/>. It's
    /// defence in depth; the UI itself shows no image from a website unless
    /// the den allows it (#180). A page reads <c>den.yaml</c> each time, so
    /// a change to <c>remote_images</c> applies on the next load.
    /// </summary>
    private static void AddContentSecurityPolicy(HttpResponse response, string denRoot)
    {
        if (response.Headers.ContainsKey(HeaderNames.ContentSecurityPolicy))
        {
            return;
        }

        var page = MediaTypeHeaderValue.TryParse(response.ContentType, out var type)
            && type.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase);
        response.Headers.ContentSecurityPolicy = !page ? NonPagePolicy
            : RemoteImages(denRoot) ? PagePolicyWithRemoteImages
            : PagePolicy;
    }

    /// <summary>
    /// True if the den's <c>den.yaml</c> has <c>remote_images: true</c>
    /// (format.md F-DEN-8). It's read from the file, not the index, so the
    /// page's policy and <c>/api/den</c> agree without a rebuild; a missing
    /// or unreadable file, or one that doesn't parse, leaves it off.
    /// </summary>
    internal static bool RemoteImages(string denRoot)
    {
        try
        {
            return DenStructure.ReadRemoteImages(File.ReadAllBytes(Path.Combine(denRoot, "den.yaml")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>An unexpected failure, as JSON in the API's error shape.</summary>
    private static Task InternalError(HttpContext context)
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return context.Response.WriteAsJsonAsync(new ApiError(
            "internal_error", $"Something went wrong in Wolfden: {error?.Message ?? "an unknown error"}"));
    }

    /// <summary>
    /// Serves the built UI from <paramref name="webRoot"/>, with
    /// <c>index.html</c> for any other path that isn't a file, and for any
    /// path under <c>/u</c> (the UI's pages, which can end in a note's file
    /// name), so the UI's own routes work on reload. Without a built UI, <c>/</c> says how to
    /// build one. Pages are sent with <c>Cache-Control: no-cache</c>, so a
    /// new build shows on the next load.
    /// </summary>
    private static void MapWebUi(WebApplication app, string? webRoot)
    {
        if (webRoot is null || !File.Exists(Path.Combine(webRoot, "index.html")))
        {
            app.MapGet("/", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.ContentSecurityPolicy = NotBuiltPagePolicy;
                return Results.Content(NotBuiltPage, "text/html; charset=utf-8");
            });
            return;
        }

        var files = new PhysicalFileProvider(Path.GetFullPath(webRoot));
        app.Lifetime.ApplicationStopped.Register(files.Dispose);
        var staticFiles = new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = NoCacheForPages,
        };
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(staticFiles);
        app.MapFallbackToFile("index.html", staticFiles);

        // The UI's own pages are under /u, and an entity without an id is
        // named by its path, such as .../e/universes/dracula/entities/x.md.
        // The fallback above skips paths that look like files, so these get
        // index.html whatever they end in.
        app.MapFallbackToFile("/u/{**path}", "index.html", staticFiles);
    }

    private static void NoCacheForPages(StaticFileResponseContext file)
    {
        if (file.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            file.Context.Response.Headers.CacheControl = "no-cache";
        }
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
