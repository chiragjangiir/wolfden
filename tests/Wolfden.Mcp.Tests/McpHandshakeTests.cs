using System.IO.Pipelines;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolfden.Index;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// The server doesn't make the client wait for the watcher's first build of
/// the index (#135). A client that waits too long for <c>server/discover</c>
/// (5 seconds by default in the C# SDK) gives up on the newest protocol and
/// tries <c>initialize</c>, which the server then refuses if it has already
/// answered the discover probe. The watcher starts in the background, and
/// still ends up running when a call holds the index open meanwhile.
/// </summary>
public sealed class McpHandshakeTests
{
    private const string LucyPath = "universes/dracula/books/dracula/entities/characters/lucy-westenra.md";

    [Fact]
    [Trait("Scenario", "SC-MCP-10")]
    public async Task TheServerAnswersWhileTheWatchersFirstBuildIsHeldUp()
    {
        using var den = TempDen.Dracula();
        using var log = new LockedLog();
        using var stop = new CancellationTokenSource();
        using var holding = new ManualResetEventSlim();
        using var mayBuild = new ManualResetEventSlim();
        var toServer = new Pipe();
        var toClient = new Pipe();
        var cancel = TestContext.Current.CancellationToken;

        // The watcher's start is held up inside, holding the watch lock and listening, just before its first build.
        var server = Serve(den, log, toServer, toClient, stop.Token, (watchLog, watchStop) => WolfdenMcpServer.StartWatcher(
            den.Root,
            watchLog,
            [],
            root => new DenWatcher(root)
            {
                BeforeFirstBuild = () =>
                {
                    holding.Set();
                    mayBuild.Wait(TimeSpan.FromSeconds(60), watchStop);
                },
            },
            watchStop));
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            connect.CancelAfter(TimeSpan.FromSeconds(30));
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
                new McpClientOptions { DiscoverProbeTimeout = TimeSpan.FromSeconds(20) },
                cancellationToken: connect.Token);

            // It answered server/discover, so the client didn't fall back to initialize.
            Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
            Assert.True(holding.Wait(TimeSpan.FromSeconds(30), cancel));
            Assert.False(mayBuild.IsSet);
            Assert.True(DenWatcher.IsWatching(den.Root));

            // A call made while the first build is held up gets the den's notes.
            var tools = await client.ListToolsAsync(cancellationToken: cancel);
            Assert.Equal(5, tools.Count);
            Assert.Contains("Lucy Westenra", await Text(client.CallToolAsync("canon_as_of", new Dictionary<string, object?> { ["position"] = "dracula:5" }, cancellationToken: cancel)), StringComparison.Ordinal);

            // An edit made meanwhile is in what calls see once the watcher has started.
            File.AppendAllText(Path.Combine(den.Root, LucyPath), "\nHer diary mentions a quillwhistle.\n");
            mayBuild.Set();
            await WaitFor(() => log.ToString().Contains("keeping its index up to date", StringComparison.Ordinal), cancel);
            Assert.True(DenWatcher.IsWatching(den.Root));
            Assert.Contains("lucy-westenra", await Text(client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "quillwhistle" }, cancellationToken: cancel)), StringComparison.Ordinal);
        }
        finally
        {
            mayBuild.Set();
            await Stop(stop, server);
        }
    }

    [Fact]
    public async Task AWatcherStartsEvenIfACallHoldsTheIndexOpenDuringItsFirstBuild()
    {
        using var den = TempDen.Dracula();
        IndexBuilder.Rebuild(den.Root);
        using var log = new LockedLog();
        using var stop = new CancellationTokenSource();
        var cancel = TestContext.Current.CancellationToken;

        // A reader holds index.db open, as a call does while it reads, so on
        // Windows the first build can't replace the file.
        var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = IndexBuilder.IndexPath(den.Root),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        reader.Open();
        using (var command = reader.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM sqlite_master";
            command.ExecuteScalar();
        }

        var server = Serve(den, log, new Pipe(), new Pipe(), stop.Token, (watchLog, watchStop) => WolfdenMcpServer.StartWatcher(
            den.Root, watchLog, Enumerable.Repeat(TimeSpan.FromMilliseconds(200), 50).ToList(), root => new DenWatcher(root), watchStop));
        try
        {
            // The first start fails (on Windows) or succeeds (where an open file can be replaced); then the reader lets go.
            await WaitFor(
                () => log.ToString() is var text &&
                      (text.Contains("couldn't start watching the den", StringComparison.Ordinal) ||
                       text.Contains("keeping its index up to date", StringComparison.Ordinal)),
                cancel);
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains("couldn't start watching the den", log.ToString(), StringComparison.Ordinal);
            }

            reader.Dispose();

            // It tried again, and the watcher is running.
            await WaitFor(() => log.ToString().Contains("keeping its index up to date", StringComparison.Ordinal), cancel);
            Assert.True(DenWatcher.IsWatching(den.Root));
            Assert.DoesNotContain("couldn't watch the den", log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            reader.Dispose();
            await Stop(stop, server);
        }
    }

    [Fact]
    public async Task AServerStoppedBeforeItStartsDoesntStartAWatcher()
    {
        using var den = TempDen.Dracula();
        using var log = new LockedLog();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var started = false;

        var server = Serve(den, log, new Pipe(), new Pipe(), stop.Token, (_, _) =>
        {
            started = true;
            return null;
        });

        await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(started);
        Assert.False(File.Exists(IndexBuilder.IndexPath(den.Root)));
    }

    [Fact]
    public async Task WhenTheClientLeavesBeforeTheWatcherStartsItDoesntBuildTheIndex()
    {
        using var den = TempDen.Dracula();
        using var log = new LockedLog();
        var toServer = new Pipe();

        // The client closes standard input at once, before the watcher gets going.
        await toServer.Writer.CompleteAsync();
        var server = Serve(den, log, toServer, new Pipe(), TestContext.Current.CancellationToken, (watchLog, watchStop) =>
        {
            Assert.True(watchStop.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)), "the server didn't say it had stopped");
            return WolfdenMcpServer.StartWatcher(den.Root, watchLog, [], root => new DenWatcher(root), watchStop);
        });

        await server.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(IndexBuilder.IndexPath(den.Root)));
        Assert.False(DenWatcher.IsWatching(den.Root));
    }

    [Fact]
    public async Task AWatcherThatFailsToStartStopsTheServer()
    {
        using var den = TempDen.Dracula();
        using var log = new LockedLog();

        var server = Serve(den, log, new Pipe(), new Pipe(), TestContext.Current.CancellationToken, (_, _) => throw new FormatException("the watcher broke"));

        var error = await Assert.ThrowsAsync<FormatException>(() => server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal("the watcher broke", error.Message);
    }

    /// <summary>
    /// Runs the server on a thread of its own, as in a process of its own:
    /// a server that waited for the watcher before serving mustn't hold up
    /// the client too.
    /// </summary>
    private static Task Serve(
        TempDen den, TextWriter log, Pipe toServer, Pipe toClient, CancellationToken stop, Func<TextWriter, CancellationToken, DenWatcher?> startWatcher) =>
        Task.Run(
            () => WolfdenMcpServer.RunAsync(
                new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "wolfden"),
                new DenTools(den.Root),
                "0.1.0-test",
                log,
                startWatcher,
                stop),
            CancellationToken.None);

    private static async Task Stop(CancellationTokenSource stop, Task server)
    {
        await stop.CancelAsync();
        try
        {
            await server;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<string> Text(ValueTask<CallToolResult> call)
    {
        var result = await call;
        Assert.NotEqual(true, result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private static async Task WaitFor(Func<bool> condition, CancellationToken cancel)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(60), "timed out waiting");
            await Task.Delay(50, cancel);
        }
    }

    /// <summary>A log the test can read while the server's threads write to it.</summary>
    private sealed class LockedLog : StringWriter
    {
        private readonly Lock _gate = new();

        public override void Write(char value)
        {
            lock (_gate)
            {
                base.Write(value);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate)
            {
                base.Write(buffer, index, count);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                base.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                base.WriteLine(value);
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return base.ToString();
            }
        }
    }
}
