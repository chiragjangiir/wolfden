using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Wolfden.Api;
using Wolfden.Index;
using Wolfden.Mcp;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden serve [&lt;den&gt;] [--port &lt;n&gt;] [--no-open]</c>: hosts the
/// API and the built web UI on 127.0.0.1 in this process (design.md §9,
/// #23), opens the default browser, and runs until Ctrl+C.
/// </summary>
/// <remarks>
/// <para>
/// It hosts the same watcher as <c>wolfden mcp</c>, so the index stays up
/// to date while it runs; each request also checks the files first. The
/// built UI is <c>web/dist</c>, copied next to <c>wolfden</c> as
/// <c>wwwroot</c> when it was built before <c>dotnet build</c>. Without it,
/// the API still works and <c>/</c> says how to build the UI.
/// </para>
/// <para>
/// Exit codes: 0 on Ctrl+C, and 2 when the command line is wrong, there's
/// no den, the universe doesn't exist, or the port can't be listened on.
/// </para>
/// </remarks>
internal static class ServeCommand
{
    internal static readonly CommandSpec Spec = new(
        "serve",
        "usage: wolfden serve [<den>] [--den <den>] [--universe <key>] [--port <n>] [--no-open] [--no-watch]",
        $"""
        Serves the den's web UI and API on this computer only (127.0.0.1), opens
        it in your default browser, and runs until Ctrl+C. It keeps the den's
        index up to date as files change while it runs. If another Wolfden is
        already watching the den (such as 'wolfden index --watch'), it relies on
        that one, and on a check of the files before each request.

          <den>, --den <den>  the den's folder; by default, the current folder or
                              the nearest folder above it with a den.yaml
          --universe <key>    serve only this universe
          --port <n>          the port to listen on (default {WolfdenApi.DefaultPort}); 0 picks a
                              free one
          --no-open           don't open a browser; the address is printed either way
          --no-watch          don't watch the files; each request still checks
                              them first and rebuilds the index if they changed

        There are no accounts in this version: anyone who can use this computer
        can open the page while it runs.

        Exits 0 on Ctrl+C, and 2 if the server couldn't start: no den, a
        --universe the den doesn't have, or a port that's in use or reserved.
        """,
        ["--no-open", "--no-watch"],
        ["--den", "--universe", "--port"],
        [],
        1,
        "give at most one den folder.");

    /// <summary>For tests: opens the address in place of the default browser.</summary>
    internal static Action<string>? BrowserOpener { get; set; }

    /// <summary>For tests: the built UI's folder in place of <see cref="DefaultWebRoot"/>.</summary>
    internal static string? WebRoot { get; set; }

    /// <summary>For tests: starts the server in place of <c>StartAsync</c>, to make it fail as the system would.</summary>
    internal static Func<WebApplication, CancellationToken, Task>? StartOverride { get; set; }

    /// <summary>For tests: called with the address once the server is listening.</summary>
    internal static Action<string>? Started { get; set; }

    /// <summary>Where the built UI is: <c>wwwroot</c> next to <c>wolfden</c>.</summary>
    internal static string DefaultWebRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot");

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken stop = default)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        var port = WolfdenApi.DefaultPort;
        if (line.Value("--port") is { } portText
            && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port > 65535))
        {
            error.WriteLine($"wolfden serve: the port must be a whole number from 1 to 65535, or 0 for a free one, not '{portText}'.");
            error.WriteLine(Spec.Usage);
            return CliApp.ExitUsage;
        }

        if (!DenLocation.TryGetGiven(Spec, line, error, out var given) || DenLocation.Resolve(Spec.Name, given, error) is not { } root)
        {
            return CliApp.ExitUsage;
        }

        root = Path.GetFullPath(root);
        var pinned = line.Value("--universe");
        if (!PinnedUniverse.Check(Spec.Name, root, pinned, error))
        {
            return CliApp.ExitUsage;
        }

        var log = TextWriter.Synchronized(error);
        var app = WolfdenApi.Build(new WolfdenApiOptions(root)
        {
            Port = port,
            PinnedUniverse = pinned,
            WebRoot = WebRoot ?? DefaultWebRoot,
            Version = CliApp.Version,
            Log = log,
        });

        CliApp.RunsUntilStopped = true;
        using var stopWatching = new CancellationTokenSource();
        Task<DenWatcher?> watching = Task.FromResult<DenWatcher?>(null);
        try
        {
            try
            {
                (StartOverride ?? ((a, token) => a.StartAsync(token)))(app, stop).GetAwaiter().GetResult();
            }
            catch (Exception e) when (StartFailure(e, port) is { } message)
            {
                error.WriteLine(message);
                return CliApp.ExitUsage;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return CliApp.ExitOk;
            }

            // Build listens on 127.0.0.1 only, and reads no configuration that could add a listener; this makes sure.
            if (WolfdenApi.NonLoopbackAddresses(app) is { Count: > 0 } exposed)
            {
                error.WriteLine($"wolfden serve: stopped, because the server also listened on {string.Join(", ", exposed)}, beyond this computer. It should listen on 127.0.0.1 only.");
                return CliApp.ExitUsage;
            }

            var url = app.Urls.First().TrimEnd('/') + "/";
            output.WriteLine($"wolfden serve: serving the den at '{root}' on {url}");
            output.WriteLine("Press Ctrl+C to stop.");
            output.Flush();

            if (!line.Has("--no-watch"))
            {
                // Serve first: the page can load while the watcher builds the index.
                watching = Task.Run(
                    () => WolfdenMcpServer.StartWatcher(
                        root, log, WolfdenMcpServer.WatcherRetryDelays, r => new DenWatcher(r), stopWatching.Token, "wolfden serve"),
                    CancellationToken.None);
            }

            if (!line.Has("--no-open"))
            {
                Open(url, error);
            }

            Started?.Invoke(url);
            WaitHandle.WaitAny([stop.WaitHandle, app.Lifetime.ApplicationStopping.WaitHandle]);
            return CliApp.ExitOk;
        }
        finally
        {
            stopWatching.Cancel();
            try
            {
                watching.GetAwaiter().GetResult()?.Dispose();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log.WriteLine($"wolfden serve: couldn't watch the den: {e.Message}");
            }

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    app.StopAsync(timeout.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // Gave open connections their five seconds.
                }
            }

            ((IAsyncDisposable)app).DisposeAsync().AsTask().GetAwaiter().GetResult();
            CliApp.RunsUntilStopped = false;
        }
    }

    /// <summary>
    /// What to say when the server couldn't start listening, or null for a
    /// failure that isn't about the port. A port in use is an
    /// <see cref="IOException"/> around an <see cref="AddressInUseException"/>;
    /// one the system won't let a program use, such as a port in a range
    /// Windows reserves, or one below 1024 on Linux, is a
    /// <see cref="SocketException"/> (access denied).
    /// </summary>
    internal static string? StartFailure(Exception e, int port) => e switch
    {
        IOException { InnerException: AddressInUseException } or AddressInUseException =>
            $"wolfden serve: port {port} on 127.0.0.1 is in use, perhaps by another 'wolfden serve'. Stop that, or pick another port with '--port <n>'.",
        SocketException or IOException =>
            $"wolfden serve: couldn't listen on 127.0.0.1 port {port}: {e.Message} (the port may be reserved; try another with '--port <n>', or '--port 0' for a free one)",
        _ => null,
    };

    /// <summary>Opens <paramref name="url"/> in the default browser, or says to open it by hand.</summary>
    private static void Open(string url, TextWriter error)
    {
        try
        {
            if (BrowserOpener is { } opener)
            {
                opener(url);
            }
            else
            {
                // The shell opens a URL in the default browser: start on Windows, open on macOS, xdg-open on Linux.
                using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            error.WriteLine($"wolfden serve: couldn't open a browser ({e.Message}). Open {url} in yours.");
        }
    }
}
