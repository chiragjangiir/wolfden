using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// The server end to end, in process: a real MCP client connects over a
/// pair of pipes, lists the tools, and calls each of them (#20).
/// </summary>
public sealed class McpServerTests : IAsyncLifetime
{
    private static readonly string[] ReadToolNames = ["canon_as_of", "check", "get_chapter", "get_entity", "get_history", "list_entities", "search"];

    private static readonly string[] WriteToolNames =
        ["add_idea", "append_section", "change_alias", "checkpoint", "create_entity", "rename_entity", "rename_section", "update_chapter_fields", "update_fields"];

    private readonly TempDen _den = TempDen.Dracula();
    private readonly StringWriter _log = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private Task? _server;
    private McpClient? _client;

    private McpClient Client => _client!;

    public async ValueTask InitializeAsync()
    {
        var transport = new StreamServerTransport(_toServer.Reader.AsStream(), _toClient.Writer.AsStream(), "wolfden");
        _server = WolfdenMcpServer.RunAsync(transport, new DenTools(_den.Root), "0.1.0-test", _log, watch: true, _stop.Token);
        _client = await McpClient.CreateAsync(
            new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream()),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        await _stop.CancelAsync();
        try
        {
            await _server!;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _den.Dispose();
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-10")]
    public async Task TheClientSeesTheServerAndItsTools()
    {
        Assert.Equal("wolfden", Client.ServerInfo!.Name);
        Assert.Contains("story position", Client.ServerInstructions!, StringComparison.Ordinal);
        Assert.Contains("checkpoint", Client.ServerInstructions!, StringComparison.Ordinal);

        var tools = await Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ReadToolNames.Concat(WriteToolNames).Order(StringComparer.Ordinal), tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, t =>
        {
            Assert.Equal(ReadToolNames.Contains(t.Name), t.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        });

        var getEntity = tools.Single(t => t.Name == "get_entity").JsonSchema;
        var properties = getEntity.GetProperty("properties");
        Assert.True(properties.TryGetProperty("entity", out _));
        Assert.True(properties.TryGetProperty("as_of", out _));
        Assert.Equal(["entity"], getEntity.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-10")]
    public async Task EachToolAnswersWithJson()
    {
        var calls = new (string Tool, Dictionary<string, object?> Args, string Expect)[]
        {
            ("search", new() { ["query"] = "Voivode" }, "\"hits\":"),
            ("get_entity", new() { ["entity"] = "Mina", ["as_of"] = "dracula:9" }, "\"view\":\"canon_as_of\""),
            ("list_entities", new() { ["type"] = "location" }, "\"entities\":"),
            ("canon_as_of", new() { ["position"] = "dracula:18" }, "\"pinned\":"),
            ("check", new() { ["severity"] = "error" }, "\"loaded\":true"),
            // This den has no history backend: that's an answer, not an error.
            ("get_history", new() { ["entity"] = "Lucy" }, "\"available\":false"),
            ("get_chapter", new() { ["book"] = "dracula", ["chapter"] = "5" }, "\"hash\":"),
        };
        foreach (var (tool, args, expect) in calls)
        {
            var result = await Call(tool, args);
            Assert.NotEqual(true, result.IsError);
            var text = Text(result);
            Assert.Contains(expect, text, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(text);
            Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        }
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-16")]
    public async Task OverTheWireAWriteTakesItsChangesAsJsonAndAStaleHashIsRefused()
    {
        using var read = JsonDocument.Parse(Text(await Call("get_entity", new() { ["entity"] = "Mr. Swales" })));
        var hash = read.RootElement.GetProperty("entity").GetProperty("hash").GetString();
        var changes = JsonDocument.Parse("""[{"op": "add", "path": "aliases", "value": "old Swales"}]""").RootElement;

        var result = await Call("update_fields", new() { ["entity"] = "Mr. Swales", ["expected_hash"] = hash, ["changes"] = changes });

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(Text(result));
        Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
        Assert.NotEqual(hash, json.RootElement.GetProperty("hash").GetString());
        Assert.Contains("aliases: [old Swales]", _den.Read("universes/dracula/books/dracula/entities/characters/mr-swales.md"), StringComparison.Ordinal);

        var stale = await Call("update_fields", new() { ["entity"] = "Mr. Swales", ["expected_hash"] = hash, ["changes"] = changes });
        Assert.True(stale.IsError);
        Assert.Contains("\"code\":\"stale_file\"", Text(stale), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-2")]
    public async Task OverTheWireAnEarlyViewHoldsNoSpoilers()
    {
        var text = Text(await Call("canon_as_of", new() { ["position"] = "dracula:4" }));

        foreach (var term in new[] { "Lucy", "Renfield", "Voivode", "Un-Dead" })
        {
            Assert.DoesNotContain(term, text, StringComparison.Ordinal);
        }

        var lucy = Text(await Call("get_entity", new() { ["entity"] = "Lucy", ["as_of"] = "dracula:4" }));
        Assert.Contains("\"in_view\":false", lucy, StringComparison.Ordinal);
        Assert.DoesNotContain("Westenra", lucy, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-6")]
    public async Task ABadCallIsAnErrorResultWithACode()
    {
        var result = await Call("get_entity", new() { ["entity"] = "Mina", ["as_of"] = "dracula:99" });

        Assert.True(result.IsError);
        using var json = JsonDocument.Parse(Text(result));
        Assert.Equal("bad_position", json.RootElement.GetProperty("error").GetProperty("code").GetString());

        var unknown = await Call("get_entity", new() { ["entity"] = "Sherlock Holmes" });
        Assert.True(unknown.IsError);
        Assert.Contains("\"code\":\"unknown_entity\"", Text(unknown), StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Scenario", "SC-MCP-6")]
    [InlineData("search", "{\"query\":\"Mina\",\"limit\":\"lots\"}")]
    [InlineData("search", "{}")]
    [InlineData("get_entity", "{\"entity\":42}")]
    [InlineData("canon_as_of", "{\"brief\":\"yes\"}")]
    public async Task AnArgumentOfTheWrongTypeIsABadArgument(string tool, string args)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(args)!;
        var result = await Call(tool, parsed);

        Assert.True(result.IsError);
        using var json = JsonDocument.Parse(Text(result));
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("bad_argument", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-7")]
    public async Task AnEditWhileConnectedShowsInTheNextCall()
    {
        var path = "universes/dracula/books/dracula/entities/characters/mr-swales.md";
        var before = Text(await Call("get_entity", new() { ["entity"] = "mr-swales" }));
        Assert.DoesNotContain("Pinned by the sea", before, StringComparison.Ordinal);

        _den.Write(path, _den.Read(path).ReplaceLineEndings("\n") + "\n## Pinned by the sea\nAn added section.\n");

        // The watcher hosted by the server, or the call's own check of the files, brings it in.
        var after = Text(await Call("get_entity", new() { ["entity"] = "mr-swales" }));
        Assert.Contains("Pinned by the sea", after, StringComparison.Ordinal);
        Assert.Contains("keeping its index up to date", _log.ToString(), StringComparison.Ordinal);
    }

    private async Task<CallToolResult> Call(string tool, Dictionary<string, object?> args) =>
        await Client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
}
