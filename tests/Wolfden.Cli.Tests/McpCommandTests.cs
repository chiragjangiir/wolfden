using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Wolfden.Cli.Tests;

/// <summary><c>wolfden mcp</c>: the MCP server over stdio (#20).</summary>
public class McpCommandTests
{
    [Fact]
    public void HelpDescribesTheServer()
    {
        var (exitCode, output, _) = Cli.Run("mcp", "--help");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.StartsWith("usage: wolfden mcp [<den>] [--den <den>] [--universe <key>] [--no-watch]", output, StringComparison.Ordinal);
        Assert.Contains("(search, get_entity, list_entities, canon_as_of, get_chapter,\nget_chapter_defaults, check, and get_history", output, StringComparison.Ordinal);
        Assert.Contains("and checkpoint, which commits them with git", output, StringComparison.Ordinal);
        Assert.Contains("mcp [<den>]", Cli.Run("--help").Output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-10")]
    public void WithoutADenItDoesntStart()
    {
        using var empty = TempDen.Empty();

        var (exitCode, output, error) = Cli.Run("mcp", empty.Root);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal("", output);
        Assert.Contains("has no den.yaml", error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-12")]
    public void AUniverseTheDenDoesntHaveCantBePinned()
    {
        var (exitCode, output, error) = Cli.Run("mcp", TempDen.DraculaRoot, "--universe", "nowhere");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal("", output);
        Assert.Equal("wolfden mcp: the den has no universe 'nowhere'. Its universes: dracula.\n", error);

        var (star, _, starError) = Cli.Run("mcp", TempDen.DraculaRoot, "--universe", "*");
        Assert.Equal(CliApp.ExitUsage, star);
        Assert.StartsWith("wolfden mcp: '*' (every universe) can't be pinned", starError, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrongCommandLineIsExitTwo()
    {
        Assert.Equal(CliApp.ExitUsage, Cli.Run("mcp", "--json").ExitCode);
        Assert.Equal(CliApp.ExitUsage, Cli.Run("mcp", "a", "b").ExitCode);
    }

    [Fact]
    public void AServerThatFailsAfterStartingIsExitThree()
    {
        using var den = TempDen.DraculaCopy();
        McpCommand.TransportFactory = () => new FailingTransport();
        try
        {
            var (exitCode, output, error) = Cli.Run("mcp", den.Root, "--no-watch");

            Assert.Equal(CliApp.ExitStoppedByFailure, exitCode);
            Assert.Equal("", output);
            Assert.Contains("wolfden mcp: the server stopped:", error, StringComparison.Ordinal);
        }
        finally
        {
            McpCommand.TransportFactory = null;
        }
    }

    [Fact]
    public void AWatcherThatFailsToStartIsExitThree()
    {
        using var den = TempDen.DraculaCopy();
        McpCommand.TransportFactory = () => new SilentTransport();
        McpCommand.WatcherFactory = (_, _) => throw new FormatException("the watcher broke");
        try
        {
            var (exitCode, output, error) = Cli.Run("mcp", den.Root);

            Assert.Equal(CliApp.ExitStoppedByFailure, exitCode);
            Assert.Equal("", output);
            Assert.Contains("wolfden mcp: the server stopped: the watcher broke", error, StringComparison.Ordinal);
        }
        finally
        {
            McpCommand.TransportFactory = null;
            McpCommand.WatcherFactory = null;
        }
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-10")]
    public async Task OverRawPipesStdoutHoldsOnlyJsonRpcAndStdinEofEndsTheServer()
    {
        using var den = TempDen.DraculaCopy();
        var start = new System.Diagnostics.ProcessStartInfo(Dotnet)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var arg in new[] { Wolfden, "mcp", "--den", den.Root })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stdin = process.StandardInput;
        stdin.AutoFlush = true;
        var cancel = TestContext.Current.CancellationToken;

        await stdin.WriteAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}""" + "\n");
        var lines = new List<string> { await ReadLine(process, cancel) };
        await stdin.WriteAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n");
        await stdin.WriteAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""" + "\n");
        lines.Add(await ReadLine(process, cancel));
        await stdin.WriteAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"canon_as_of","arguments":{"position":"dracula:5"}}}""" + "\n");
        lines.Add(await ReadLine(process, cancel));

        // End of input: the client has gone, so the server stops.
        stdin.Close();
        var rest = await process.StandardOutput.ReadToEndAsync(cancel);
        lines.AddRange(rest.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using (var exit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            exit.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(exit.Token);
        }

        // Every line on standard output is a JSON-RPC message; logs went to standard error.
        Assert.All(lines, line =>
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal("2.0", json.RootElement.GetProperty("jsonrpc").GetString());
        });
        Assert.Equal([1, 2, 3], lines.Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.TryGetProperty("id", out _)).Select(e => e.GetProperty("id").GetInt32()));
        Assert.Contains("Lucy Westenra", lines[2], StringComparison.Ordinal);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("wolfden mcp: serving the den", await stderr, StringComparison.Ordinal);

        static async Task<string> ReadLine(System.Diagnostics.Process process, CancellationToken cancel) =>
            await process.StandardOutput.ReadLineAsync(cancel) ?? throw new InvalidOperationException("The server closed standard output.");
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-10")]
    public async Task AnAIClientRunsItOverStdio()
    {
        using var den = TempDen.DraculaCopy();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "wolfden",
            Command = Dotnet,
            Arguments = [Wolfden, "mcp", "--den", den.Root],
        });
        var cancel = TestContext.Current.CancellationToken;

        // The SDK's client gives the server 5 seconds by default to answer
        // server/discover, from before the process has even started. On a
        // busy CI machine, starting .NET alone can take longer. The client
        // then falls back to initialize, which the server can refuse once it
        // has answered the probe after all (#135). The server answers at once
        // (McpHandshakeTests), so this test waits longer, and checks that the
        // client didn't fall back.
        var options = new McpClientOptions { DiscoverProbeTimeout = TimeSpan.FromSeconds(30) };
        await using (var client = await McpClient.CreateAsync(transport, options, cancellationToken: cancel))
        {
            Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);

            var tools = await client.ListToolsAsync(cancellationToken: cancel);
            Assert.Equal(["add_idea", "append_section", "canon_as_of", "change_alias", "check", "checkpoint", "copy_entity", "create_entity", "get_chapter", "get_chapter_defaults", "get_entity", "get_history", "list_entities", "move_entity", "rename_entity", "rename_section", "search", "update_chapter_defaults", "update_chapter_fields", "update_fields"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));

            var result = await client.CallToolAsync("canon_as_of", new Dictionary<string, object?> { ["position"] = "dracula:5" }, cancellationToken: cancel);
            Assert.NotEqual(true, result.IsError);
            using var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.Equal("dracula", json.RootElement.GetProperty("universe").GetString());
            Assert.Contains(
                json.RootElement.GetProperty("entities").EnumerateArray(),
                e => e.GetProperty("name").GetString() == "Lucy Westenra");
        }

        // The server ran in the den, so it built the index there.
        Assert.True(File.Exists(Path.Combine(den.Root, ".wolfden", "index.db")));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-14")]
    public async Task OverStdioHistoryComesFromGit()
    {
        using var den = TempDen.DraculaCopy();
        TestGit.Run(den.Root, "init", "--quiet");
        TestGit.Run(den.Root, "add", "--all");
        TestGit.Run(den.Root, "commit", "--quiet", "-m", "Start the Dracula notes");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "wolfden",
            Command = Dotnet,
            Arguments = [Wolfden, "mcp", "--den", den.Root, "--no-watch"],
            EnvironmentVariables = TestGit.Options.Environment.ToDictionary(),
        });
        var cancel = TestContext.Current.CancellationToken;

        var options = new McpClientOptions { DiscoverProbeTimeout = TimeSpan.FromSeconds(30) };
        await using var client = await McpClient.CreateAsync(transport, options, cancellationToken: cancel);
        var result = await client.CallToolAsync("get_history", new Dictionary<string, object?> { ["entity"] = "Lucy Westenra" }, cancellationToken: cancel);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal("git", json.RootElement.GetProperty("backend").GetString());
        Assert.True(json.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("Start the Dracula notes", Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray()).GetProperty("message").GetString());
    }

    /// <summary>The built <c>wolfden</c>, next to the test binaries.</summary>
    private static string Wolfden => Path.Combine(AppContext.BaseDirectory, "wolfden.dll");

    /// <summary>The <c>dotnet</c> host running the tests, or the one on the PATH.</summary>
    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    /// <summary>A transport whose connection breaks as soon as the server reads from it.</summary>
    private sealed class FailingTransport : ITransport
    {
        private readonly System.Threading.Channels.Channel<JsonRpcMessage> _channel = System.Threading.Channels.Channel.CreateUnbounded<JsonRpcMessage>();

        public FailingTransport()
        {
            _channel.Writer.Complete(new IOException("the connection broke"));
        }

        public string? SessionId => null;

        public System.Threading.Channels.ChannelReader<JsonRpcMessage> MessageReader => _channel.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A transport whose client never says anything and never leaves.</summary>
    private sealed class SilentTransport : ITransport
    {
        private readonly System.Threading.Channels.Channel<JsonRpcMessage> _channel = System.Threading.Channels.Channel.CreateUnbounded<JsonRpcMessage>();

        public string? SessionId => null;

        public System.Threading.Channels.ChannelReader<JsonRpcMessage> MessageReader => _channel.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
