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
        Assert.StartsWith("usage: wolfden mcp [<den>] [--den <den>] [--no-watch]", output, StringComparison.Ordinal);
        Assert.Contains("search, get_entity, list_entities, canon_as_of, and check", output, StringComparison.Ordinal);
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
    public void AWrongCommandLineIsExitTwo()
    {
        Assert.Equal(CliApp.ExitUsage, Cli.Run("mcp", "--json").ExitCode);
        Assert.Equal(CliApp.ExitUsage, Cli.Run("mcp", "a", "b").ExitCode);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-10")]
    public async Task AnAIClientRunsItOverStdio()
    {
        using var den = TempDen.DraculaCopy();
        var wolfden = Path.Combine(AppContext.BaseDirectory, "wolfden.dll");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "wolfden",
            Command = dotnet,
            Arguments = [wolfden, "mcp", "--den", den.Root],
        });
        var cancel = TestContext.Current.CancellationToken;

        await using (var client = await McpClient.CreateAsync(transport, cancellationToken: cancel))
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancel);
            Assert.Equal(["canon_as_of", "check", "get_entity", "list_entities", "search"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));

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
}
