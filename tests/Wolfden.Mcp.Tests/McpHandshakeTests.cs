using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolfden.Index;
using Wolfden.Index.Tests;

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

        // An index from before an edit, so a call finds the files changed and
        // waits for the watcher rather than building a missing index (#137).
        IndexBuilder.Rebuild(den.Root);
        File.AppendAllText(Path.Combine(den.Root, LucyPath), "\nHer diary mentions a quillwhistle.\n");
        var root = Path.GetFullPath(den.Root);
        using var builds = new IndexBuildCounter(root);
        var watcherStarts = 0;

        // The watcher's start is held up inside, holding the watch lock and listening, just before its first build.
        var server = Serve(
            den,
            log,
            toServer,
            toClient,
            stop.Token,
            (watchLog, watchStop) => WolfdenMcpServer.StartWatcher(
                den.Root,
                watchLog,

                // One quick retry, in case the call's look at the index keeps the first build from replacing it (Windows).
                [TimeSpan.FromMilliseconds(200)],
                r => new DenWatcher(r)
                {
                    BeforeFirstBuild = () =>
                    {
                        Interlocked.Increment(ref watcherStarts);
                        holding.Set();
                        mayBuild.Wait(TimeSpan.FromSeconds(60), watchStop);
                    },
                },
                watchStop),
            watcherWait: TimeSpan.FromSeconds(60));
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
            var tools = await client.ListToolsAsync(cancellationToken: cancel);
            Assert.Equal(6, tools.Count);

            // A call made while the first build is held up finds the files
            // changed and waits for the watcher, without rebuilding the index.
            var search = Text(client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "quillwhistle" }, cancellationToken: cancel));
            await WaitFor(() => builds.WatcherWaits > 0, cancel);
            await Task.Delay(200, cancel);
            Assert.False(search.IsCompleted, "The call didn't wait for the watcher.");
            Assert.Equal(0, builds.Rebuilds);

            // Once the watcher builds, the call sees the edit, and only the watcher rebuilt the index.
            mayBuild.Set();
            Assert.Contains("lucy-westenra", await search, StringComparison.Ordinal);
            await WaitFor(() => log.ToString().Contains("keeping its index up to date", StringComparison.Ordinal), cancel);
            Assert.True(DenWatcher.IsWatching(den.Root));
            Assert.Equal(watcherStarts, builds.Rebuilds);
            Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
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

    [Fact]
    public async Task WhenAWatcherThatFailsToStartBreaksTheServerTooItsErrorComesFirst()
    {
        using var den = TempDen.Dracula();
        using var log = new LockedLog();
        var transport = new BreakingTransport();

        // The watcher fails while the server reads; stopping the server then breaks its transport too.
        var server = Serve(den, log, transport, TestContext.Current.CancellationToken, (_, _) =>
        {
            Assert.True(transport.Reading.Task.Wait(TimeSpan.FromSeconds(30)), "the server didn't start reading");
            throw new FormatException("the watcher broke");
        });

        var error = await Assert.ThrowsAsync<AggregateException>(() => server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Collection(
            error.InnerExceptions,
            e => Assert.Equal("the watcher broke", Assert.IsType<FormatException>(e).Message),
            e => Assert.Equal("the pipe broke", Assert.IsType<IOException>(e).Message));
    }

    [Fact]
    public async Task WhenTheServerFailsFirstItsErrorComesFirst()
    {
        using var den = TempDen.Dracula();
        using var log = new LockedLog();
        var transport = new BreakingTransport();

        // The transport breaks while the watcher starts; the watcher then fails once the server stops.
        var server = Serve(den, log, transport, TestContext.Current.CancellationToken, (_, watchStop) =>
        {
            Assert.True(transport.Reading.Task.Wait(TimeSpan.FromSeconds(30)), "the server didn't start reading");
            transport.Break.TrySetResult();
            Assert.True(watchStop.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)), "the server didn't say it had stopped");
            throw new FormatException("the watcher broke");
        });

        var error = await Assert.ThrowsAsync<AggregateException>(() => server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Collection(
            error.InnerExceptions,
            e => Assert.Equal("the pipe broke", Assert.IsType<IOException>(e).Message),
            e => Assert.Equal("the watcher broke", Assert.IsType<FormatException>(e).Message));
    }

    [Theory]
    [InlineData("a file where .wolfden belongs")]
    [InlineData("a folder where the index belongs")]
    [InlineData("no den folder")]
    [InlineData("SQLite reporting a malformed database")]
    public void AWatcherThatCantStartForAReasonThatWontClearGivesUpAtOnce(string problem)
    {
        using var den = TempDen.Dracula();
        var root = den.Root;
        Action? beforeBuild = null;
        switch (problem)
        {
            case "a file where .wolfden belongs":
                File.WriteAllText(Path.Combine(root, IndexBuilder.StateFolder), "not a folder");
                break;
            case "a folder where the index belongs":
                Directory.CreateDirectory(IndexBuilder.IndexPath(root));
                break;
            case "no den folder":
                root = Path.Combine(root, "missing");
                break;
            default:
                beforeBuild = () => throw new SqliteException("database disk image is malformed", 11);
                break;
        }

        using var log = new StringWriter();
        var tries = 0;

        var watcher = WolfdenMcpServer.StartWatcher(
            root,
            log,
            Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 5).ToList(),
            r =>
            {
                tries++;
                return new DenWatcher(r) { BeforeFirstBuild = beforeBuild };
            },
            TestContext.Current.CancellationToken);

        Assert.Null(watcher);
        Assert.Equal(1, tries);
        Assert.Contains("couldn't watch the den", log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Trying again", log.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5)] // SQLITE_BUSY
    [InlineData(6)] // SQLITE_LOCKED
    [InlineData(517)] // SQLITE_BUSY_SNAPSHOT, an extended code of SQLITE_BUSY
    [InlineData(0)] // the rebuilt index couldn't replace one held open
    public void AWatcherThatCantStartForAReasonThatCanClearTriesAgain(int sqliteCode)
    {
        using var den = TempDen.Dracula();
        using var log = new StringWriter();
        var tries = 0;

        using var watcher = WolfdenMcpServer.StartWatcher(
            den.Root,
            log,
            Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 5).ToList(),
            r => new DenWatcher(r)
            {
                BeforeFirstBuild = () =>
                {
                    if (++tries == 1)
                    {
                        throw sqliteCode == 0 ? new IndexInUseException("The index is in use.") : new SqliteException("database is locked", sqliteCode);
                    }
                },
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(watcher);
        Assert.Equal(2, tries);
        var text = log.ToString();
        Assert.Contains("Trying again in 0 s.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("meanwhile", text, StringComparison.Ordinal);
        Assert.Contains("keeping its index up to date", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherWatcherOnTheDenIsNotTriedAgain()
    {
        using var den = TempDen.Dracula();
        using var first = new DenWatcher(den.Root);
        first.Start();
        using var log = new StringWriter();
        var tries = 0;

        var watcher = WolfdenMcpServer.StartWatcher(
            den.Root,
            log,
            Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 5).ToList(),
            r =>
            {
                tries++;
                return new DenWatcher(r);
            },
            TestContext.Current.CancellationToken);

        Assert.Null(watcher);
        Assert.Equal(1, tries);
        Assert.Contains("without its own watcher", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the server on a thread of its own, as in a process of its own:
    /// a server that waited for the watcher before serving mustn't hold up
    /// the client too.
    /// </summary>
    private static Task Serve(
        TempDen den,
        TextWriter log,
        Pipe toServer,
        Pipe toClient,
        CancellationToken stop,
        Func<TextWriter, CancellationToken, DenWatcher?> startWatcher,
        TimeSpan? watcherWait = null) =>
        Serve(den, log, new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "wolfden"), stop, startWatcher, watcherWait);

    private static Task Serve(
        TempDen den,
        TextWriter log,
        ITransport transport,
        CancellationToken stop,
        Func<TextWriter, CancellationToken, DenWatcher?> startWatcher,
        TimeSpan? watcherWait = null) =>
        Task.Run(
            () => WolfdenMcpServer.RunAsync(
                transport,
                new DenTools(den.Root) { WatcherWait = watcherWait ?? IndexBuilder.WatcherWait },
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

    /// <summary>
    /// A transport with no client, whose read breaks with an I/O error, not
    /// a cancellation, when the server stops or the test sets <see cref="Break"/>,
    /// as a pipe can when its other end goes.
    /// </summary>
    private sealed class BreakingTransport : ITransport
    {
        private readonly BreakingReader _reader = new();

        /// <summary>Set once the server waits to read.</summary>
        public TaskCompletionSource Reading => _reader.Reading;

        /// <summary>Set to break the read.</summary>
        public TaskCompletionSource Break => _reader.Break;

        public string? SessionId => null;

        public ChannelReader<JsonRpcMessage> MessageReader => _reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class BreakingReader : ChannelReader<JsonRpcMessage>
        {
            public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Break { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override bool TryRead([MaybeNullWhen(false)] out JsonRpcMessage item)
            {
                item = null;
                return false;
            }

            public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            {
                Reading.TrySetResult();
                using (cancellationToken.Register(() => Break.TrySetResult()))
                {
                    await Break.Task.ConfigureAwait(false);
                }

                throw new IOException("the pipe broke");
            }
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
