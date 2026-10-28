using System.Text.Json;
using Wolfden.Core.Structure;

namespace Wolfden.Cli.Tests;

/// <summary>
/// <c>wolfden new</c> (#29): the command line over the MCP tool
/// <c>create_entity</c>, whose behaviour is tested in Wolfden.Mcp
/// (<c>WriteToolTests</c>). Its <c>--in</c> reads as <c>wolfden copy</c>'s
/// <c>--to</c> does (the same code, tested in <c>CopyCommandTests</c>).
/// Each test works on its own copy of <c>samples/dracula</c>.
/// </summary>
public sealed class NewCommandTests : IDisposable
{
    private const string Book = "universes/dracula/books/dracula";

    private readonly TempDen _den = TempDen.DraculaCopy();

    public void Dispose() => _den.Dispose();

    private (int ExitCode, string Output, string Error) New(params string[] args) =>
        Cli.Run(["new", "--den", _den.Root, .. args]);

    [Fact]
    [Trait("Scenario", "SC-CLI-15")]
    public void ANewNoteComesFromItsTemplateInTheUniverseWithANewId()
    {
        var (exitCode, output, error) = New("character", "Sister Agatha");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        const string Path = "universes/dracula/entities/characters/sister-agatha.md";
        Assert.StartsWith($"Created {Path}\n  Created {Path} from the 'character' template, with id ", output, StringComparison.Ordinal);
        Assert.Contains("It has no 'from'", output, StringComparison.Ordinal);
        var text = _den.Read(Path);
        var id = text.Split('\n')[1]["id: ".Length..];
        Assert.True(Keys.IsId(id), id);
        Assert.Contains("\ntype: character\nname: Sister Agatha\nstatus: draft\n", text, StringComparison.Ordinal);
        Assert.Contains("\n# Sister Agatha\n\n## Overview\n", text, StringComparison.Ordinal);

        // The same name again gets -2; the check finds nothing wrong with either.
        Assert.StartsWith(
            "Created universes/dracula/entities/characters/sister-agatha-2.md", New("character", "Sister Agatha").Output, StringComparison.Ordinal);
        var (_, check, _) = Cli.Run("check", _den.Root);
        Assert.DoesNotContain("sister-agatha", check, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-15")]
    public void InNamesWhereItGoesAndFromWhereTheReaderMeetsIt()
    {
        Assert.Equal(CliApp.ExitOk, New("location", "Buda-Pesth hospital", "--in", "dracula/book:dracula", "--from", "dracula:8").ExitCode);
        var text = _den.Read($"{Book}/entities/locations/buda-pesth-hospital.md");
        Assert.Contains("\nstatus: draft\nfrom: ", text, StringComparison.Ordinal);
        Assert.Contains("dracula:8", text, StringComparison.Ordinal);

        Assert.Equal(CliApp.ExitOk, New("note", "Whitby dialect", "--in", "dracula/", "--slug", "dialect").ExitCode);
        Assert.True(_den.Exists("universes/dracula/entities/notes/dialect.md"));

        // As for copy: 'dracula' is the universe and its book, so it says how to name each.
        var (exitCode, output, error) = New("note", "Someone", "--in", "dracula");
        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Equal(
            "wolfden new: 'dracula' is the universe and a book of it: give --in 'dracula/' for the universe, or 'dracula/book:dracula' for the book.\n", error);

        // With two universes, one has to be named, and the error says how.
        _den.Write("universes/legend/universe.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A1\nname: Legend\n---\n");
        var two = New("note", "Someone");
        Assert.Equal(CliApp.ExitUsage, two.ExitCode);
        Assert.Contains("with --in", two.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("'universe'", two.Error, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitOk, New("note", "Someone", "--in", "legend").ExitCode);
        Assert.True(_den.Exists("universes/legend/entities/notes/someone.md"));

        // With two, a bare key is a universe: 'dracula' is no longer ambiguous.
        Assert.Equal(CliApp.ExitOk, New("note", "Someone else", "--in", "dracula").ExitCode);
        Assert.True(_den.Exists("universes/dracula/entities/notes/someone-else.md"));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-15")]
    public void ARefusedCreateExitsTwoAndWritesNothing()
    {
        _den.Write("universes/dracula/books/aftermath/book.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY9A0\nname: Aftermath\nstatus: final\n---\n");
        var before = Cli.Snapshot(_den.Root);

        var frozen = New("character", "Young Swales", "--in", "aftermath");
        Assert.Equal(CliApp.ExitUsage, frozen.ExitCode);
        Assert.StartsWith("wolfden new: Book 'aftermath' is final (frozen)", frozen.Error, StringComparison.Ordinal);

        var unknown = New("starship", "Demeter");
        Assert.Equal(CliApp.ExitUsage, unknown.ExitCode);
        Assert.Contains("No enabled pack defines the type 'starship'", unknown.Error, StringComparison.Ordinal);
        Assert.Contains("character", unknown.Error, StringComparison.Ordinal);

        var taken = New("character", "Count", "--in", "dracula/book:dracula", "--slug", "count-dracula");
        Assert.Equal(CliApp.ExitUsage, taken.ExitCode);
        Assert.Contains("taken", taken.Error, StringComparison.Ordinal);

        var noBook = New("character", "Someone", "--in", "dracula/sequel");
        Assert.Equal(CliApp.ExitUsage, noBook.ExitCode);
        Assert.Contains("has no book 'sequel'", noBook.Error, StringComparison.Ordinal);

        Assert.Equal(CliApp.ExitUsage, New("character").ExitCode);
        Assert.Equal(CliApp.ExitUsage, New("character", "  ").ExitCode);
        Assert.Equal(CliApp.ExitUsage, New("character", "A", "B").ExitCode);
        Assert.Equal(before, Cli.Snapshot(_den.Root));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-15")]
    public void ADenWhoseDenYamlDoesntLoadSaysSo()
    {
        _den.Write("den.yaml", "format: [\n");

        var withIn = New("note", "Someone", "--in", "dracula/");
        Assert.Equal(CliApp.ExitUsage, withIn.ExitCode);
        Assert.StartsWith("wolfden new: den.yaml doesn't load, so --in can't be read and nothing can be created.", withIn.Error, StringComparison.Ordinal);

        var without = New("note", "Someone");
        Assert.Equal(CliApp.ExitUsage, without.ExitCode);
        Assert.Contains("den.yaml doesn't load", without.Error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-15")]
    public void JsonPrintsTheWriteResult()
    {
        var (exitCode, output, _) = New("thread", "The missing manifest", "--json");

        Assert.Equal(CliApp.ExitOk, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal("universes/dracula/entities/threads/the-missing-manifest.md", json.RootElement.GetProperty("path").GetString());
        Assert.Equal("The missing manifest", json.RootElement.GetProperty("entity").GetProperty("name").GetString());
        Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
    }
}
