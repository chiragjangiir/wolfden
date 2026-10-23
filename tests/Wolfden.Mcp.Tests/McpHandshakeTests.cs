using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Wolfden.Index;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// The server answers a client's first message at once, without waiting for
/// the watcher's first build of the index (#135). A client that waits too
/// long for <c>server/discover</c> (5 seconds by default in the C# SDK)
/// gives up on the newest protocol and tries <c>initialize</c>, which the
/// server then refuses if it has already answered the discover probe.
/// </summary>
public sealed class McpHandshakeTests
{
    [Fact]
    [Trait("Scenario", "SC-MCP-10")]
    public async Task TheServerAnswersBeforeTheWatcherHasBuiltTheIndex()
    {
        using var den = TempDen.Dracula();
        using var log = new StringWriter();
        using var stop = new CancellationTokenSource();
        using var watcherMayStart = new ManualResetEventSlim();
        var toServer = new Pipe();
        var toClient = new Pipe();
        var cancel = TestContext.Current.CancellationToken;

        // The watcher's start (its first build) is held up until the client has connected.
        // On a thread of its own, as in a process of its own: a server that waited for it
        // before serving mustn't hold up the client too.
        var server = Task.Run(
            () => WolfdenMcpServer.RunAsync(
                new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "wolfden"),
                new DenTools(den.Root),
                "0.1.0-test",
                log,
                () =>
                {
                    watcherMayStart.Wait(TimeSpan.FromSeconds(60), stop.Token);
                    var watcher = new DenWatcher(den.Root);
                    watcher.Start();
                    return watcher;
                },
                stop.Token),
            cancel);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            connect.CancelAfter(TimeSpan.FromSeconds(30));
            await using (var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
                new McpClientOptions { DiscoverProbeTimeout = TimeSpan.FromSeconds(20) },
                cancellationToken: connect.Token))
            {
                // It answered server/discover, so the client didn't fall back to initialize.
                Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
                Assert.False(watcherMayStart.IsSet);

                // A call made while the first build is still held up builds the index itself.
                var tools = await client.ListToolsAsync(cancellationToken: cancel);
                Assert.Equal(5, tools.Count);
                var result = await client.CallToolAsync("canon_as_of", new Dictionary<string, object?> { ["position"] = "dracula:5" }, cancellationToken: cancel);
                Assert.NotEqual(true, result.IsError);
                Assert.Contains("Lucy Westenra", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);

                watcherMayStart.Set();
            }
        }
        finally
        {
            watcherMayStart.Set();
            await stop.CancelAsync();
            try
            {
                await server;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task AWatcherThatFailsToStartStopsTheServer()
    {
        using var den = TempDen.Dracula();
        using var log = new StringWriter();
        var toServer = new Pipe();
        var toClient = new Pipe();

        var server = WolfdenMcpServer.RunAsync(
            new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "wolfden"),
            new DenTools(den.Root),
            "0.1.0-test",
            log,
            () => throw new FormatException("the watcher broke"),
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<FormatException>(() => server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal("the watcher broke", error.Message);
    }
}
