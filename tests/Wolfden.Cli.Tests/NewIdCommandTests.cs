using System.Text.Json;
using Wolfden.Core.Structure;

namespace Wolfden.Cli.Tests;

/// <summary><c>wolfden new-id</c>: ids for files made by hand (format.md F-ID-5).</summary>
public class NewIdCommandTests
{
    [Fact]
    [Trait("Scenario", "SC-CLI-7")]
    public void ItPrintsOneValidId()
    {
        var (exitCode, output, error) = Cli.Run("new-id");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        var id = output.TrimEnd('\n');
        Assert.True(Keys.IsId(id), id);
        Assert.Equal(id + "\n", output);
    }

    [Fact]
    public void ItPrintsAsManyDifferentIdsAsAskedFor()
    {
        var (_, output, _) = Cli.Run("new-id", "5");

        var ids = output.TrimEnd('\n').Split('\n');
        Assert.Equal(5, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(Keys.IsId(id), id));
    }

    [Fact]
    public void JsonListsTheIds()
    {
        var (_, output, _) = Cli.Run("new-id", "2", "--json");

        using var json = JsonDocument.Parse(output);
        Assert.Equal(["version", "ids"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, json.RootElement.GetProperty("ids").GetArrayLength());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("two")]
    [InlineData("+3")]
    public void ABadCountExitsTwo(string count)
    {
        var (exitCode, output, error) = Cli.Run("new-id", count);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.StartsWith($"wolfden new-id: the count must be a whole number from 1 to 1000, not '{count}'.", error, StringComparison.Ordinal);
    }
}
