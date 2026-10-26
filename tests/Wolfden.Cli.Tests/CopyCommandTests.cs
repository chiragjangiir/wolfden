using System.Text.Json;
using Wolfden.Core.Structure;

namespace Wolfden.Cli.Tests;

/// <summary>
/// <c>wolfden copy</c> (#129): the command line over the MCP tool
/// <c>copy_entity</c>, whose behaviour is tested in Wolfden.Mcp
/// (<c>CopyEntityToolTests</c>). Each test works on its own copy of
/// <c>samples/dracula</c>.
/// </summary>
public sealed class CopyCommandTests : IDisposable
{
    private const string Characters = "universes/dracula/books/dracula/entities/characters";

    private readonly TempDen _den = TempDen.DraculaCopy();

    public void Dispose() => _den.Dispose();

    private (int ExitCode, string Output, string Error) Copy(params string[] args) =>
        Cli.Run(["copy", "--den", _den.Root, .. args]);

    [Fact]
    [Trait("Scenario", "SC-CLI-13")]
    public void ACopyGoesBesideTheOriginalWithANewIdAndSaysWhatItMeans()
    {
        var (exitCode, output, error) = Copy("Count Dracula", "Count Varga");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.StartsWith($"Copied to {Characters}/count-varga.md\n  Copied 'Count Dracula' ", output, StringComparison.Ordinal);
        Assert.Contains("weren't copied", output, StringComparison.Ordinal);
        var copy = _den.Read($"{Characters}/count-varga.md");
        var id = copy.Split('\n')[1]["id: ".Length..];
        Assert.True(Keys.IsId(id), id);
        Assert.NotEqual("01M4TF5EZZJ0FZ8PAAT1WY4GR4", id);
        Assert.Contains("\nname: Count Varga\nstatus: canon\n", copy, StringComparison.Ordinal);

        // A whole-den check finds no duplicate id.
        var (_, check, _) = Cli.Run("check", _den.Root);
        Assert.DoesNotContain("F-ID-3", check, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-13")]
    public void ToNamesTheUniverseOrOneOfItsSeriesOrBooks()
    {
        _den.Write("universes/dracula/books/aftermath/book.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A0\nname: Aftermath\nstatus: draft\nchapters: [1]\n---\n");

        Assert.Equal(CliApp.ExitOk, Copy("Mr. Swales", "Young Swales", "--to", "dracula/aftermath").ExitCode);
        Assert.True(_den.Exists("universes/dracula/books/aftermath/entities/characters/young-swales.md"));
        Assert.Equal(CliApp.ExitOk, Copy("Mr. Swales", "Elder Swales", "--to", "dracula/book:aftermath", "--slug", "elder").ExitCode);
        Assert.True(_den.Exists("universes/dracula/books/aftermath/entities/characters/elder.md"));
        Assert.Equal(CliApp.ExitOk, Copy("Mr. Swales", "Swales of Whitby", "--to", "dracula/").ExitCode);
        Assert.True(_den.Exists("universes/dracula/entities/characters/swales-of-whitby.md"));
        Assert.Contains("has no book 'sequel'", Copy("Mr. Swales", "Someone", "--to", "dracula/sequel").Error, StringComparison.Ordinal);

        // With one universe, a book key alone will do (#127).
        Assert.Equal(CliApp.ExitOk, Copy("Mr. Swales", "Swales the younger", "--to", "aftermath").ExitCode);
        Assert.True(_den.Exists("universes/dracula/books/aftermath/entities/characters/swales-the-younger.md"));

        // 'dracula' is both the universe and its book: the error says how to name each.
        var (exitCode, output, error) = Copy("Mr. Swales", "Someone", "--to", "dracula");
        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Equal(
            "wolfden copy: 'dracula' is the universe and a book of it: give --to 'dracula/' for the universe, or 'dracula/book:dracula' for the book.\n", error);

        // With two universes, a bare book key isn't one: the error says how to name it.
        _den.Write("universes/legend/universe.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A1\nname: Legend\n---\n");
        var two = Copy("Mr. Swales", "Someone", "--universe", "dracula", "--to", "aftermath");
        Assert.Equal(CliApp.ExitUsage, two.ExitCode);
        Assert.Contains("no universe 'aftermath'", two.Error, StringComparison.Ordinal);
        Assert.Contains("give --to 'dracula/aftermath'", two.Error, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitOk, Copy("Mr. Swales", "Someone", "--universe", "dracula", "--to", "dracula/ book: aftermath").ExitCode);
        Assert.True(_den.Exists("universes/dracula/books/aftermath/entities/characters/someone.md"));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-13")]
    public void ADenWhoseDenYamlDoesntLoadSaysSo()
    {
        _den.Write("den.yaml", "format: [\n");

        var (exitCode, _, error) = Copy("Mr. Swales", "Someone", "--to", "dracula/");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith("wolfden copy: den.yaml doesn't load", error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-13")]
    public void ARefusedCopyExitsTwoAndWritesNothing()
    {
        _den.Write("universes/dracula/books/aftermath/book.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A0\nname: Aftermath\nstatus: final\n---\n");
        _den.Write("universes/dracula/entities/notes/whitby-dialect.md",
            "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A2\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\nSee [[the Count]].\n");
        var before = Cli.Snapshot(_den.Root);

        var frozen = Copy("Mr. Swales", "Young Swales", "--to", "dracula/aftermath");
        var relink = Copy("Mr. Swales", "the Count");

        Assert.Equal(CliApp.ExitUsage, frozen.ExitCode);
        Assert.StartsWith("wolfden copy: Book 'aftermath' is final (frozen)", frozen.Error, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitUsage, relink.ExitCode);
        Assert.Contains("would change which entity they reach", relink.Error, StringComparison.Ordinal);
        Assert.Contains("or pass --allow-relinks to make the copy anyway", relink.Error, StringComparison.Ordinal);
        Assert.Contains("another name (or --slug)", relink.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_relinks", relink.Error, StringComparison.Ordinal);
        Assert.Equal(before, Cli.Snapshot(_den.Root));

        Assert.Equal(CliApp.ExitOk, Copy("Mr. Swales", "the Count", "--allow-relinks").ExitCode);
        Assert.Equal(CliApp.ExitUsage, Copy("Mr. Swales").ExitCode);
        var unknown = Copy("Mr. Nobody", "Someone");
        Assert.Equal(CliApp.ExitUsage, unknown.ExitCode);
        Assert.Contains("Use 'wolfden query --brief' to find it.", unknown.Error, StringComparison.Ordinal);
        Assert.Contains("as <new name>", Copy("Mr. Swales", " ").Error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-13")]
    public void JsonPrintsTheWriteResult()
    {
        var (exitCode, output, _) = Copy("Mr. Swales", "Old Gale", "--json");

        Assert.Equal(CliApp.ExitOk, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal($"{Characters}/old-gale.md", json.RootElement.GetProperty("path").GetString());
        Assert.Equal("Old Gale", json.RootElement.GetProperty("entity").GetProperty("name").GetString());
        Assert.True(json.RootElement.GetProperty("files")[0].GetProperty("created").GetBoolean());
    }
}
