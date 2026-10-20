using System.Text.Json;
using Wolfden.Index;

namespace Wolfden.Cli.Tests;

/// <summary><c>wolfden index</c>: rebuilding the index from the files.</summary>
public class IndexCommandTests
{
    [Fact]
    [Trait("Scenario", "SC-CLI-2")]
    public void ItRebuildsTheIndexAndSaysWhatItHolds()
    {
        using var den = TempDen.DraculaCopy();
        var before = Cli.Snapshot(den.Root);

        var (exitCode, output, error) = Cli.Run("index", den.Root);

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.Equal("Rebuilt .wolfden/index.db: 1 universe, 50 entities, and 372 links from 54 files.\n", output);
        Assert.True(den.Exists(".wolfden/index.db"));
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
        // The den's own files are untouched (F-ID-6).
        Assert.Equal(before, Cli.Snapshot(den.Root));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-2")]
    public void ADeletedIndexIsRebuiltWithTheSameContent()
    {
        using var den = TempDen.DraculaCopy();
        Cli.Run("index", den.Root);
        var first = Cli.Run("index", "--den", den.Root, "--json").Output;
        File.Delete(IndexBuilder.IndexPath(den.Root));

        var (exitCode, second, _) = Cli.Run("index", "--den", den.Root, "--json");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Equal(first, second);
    }

    [Fact]
    public void JsonHasAStableShape()
    {
        using var den = TempDen.DraculaCopy();

        var (_, output, _) = Cli.Run("index", den.Root, "--json");

        Assert.DoesNotContain("\r", output, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal(["version", "index", "files", "universes", "entities", "links", "errors", "warnings"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(".wolfden/index.db", root.GetProperty("index").GetString());
        Assert.Equal(50, root.GetProperty("entities").GetInt32());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());
    }

    [Fact]
    public void FilesWithErrorsAreLeftOutAndTheCommandStillSucceeds()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string>
        {
            ["den.yaml"] = "format: 1\n",
            ["universes/vael/universe.md"] = "---\nid: 01JC5CK5000000000000000001\nname: Vael\n---\n",
            ["universes/vael/entities/characters/torn.md"] = "---\nid: 01JC5CK5000000000000000002\ntype: character\nname: [Torn\n---\n",
            ["universes/vael/entities/characters/orrin.md"] = "---\nid: 01JC5CK5000000000000000003\ntype: character\nname: Orrin\n---\n",
        });

        var (exitCode, output, _) = Cli.Run("index", den.Root);

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Equal(
            "Rebuilt .wolfden/index.db: 1 universe, 1 entity, and 0 links from 4 files.\n" +
            "Loading found 1 error and 0 warnings; files with errors are left out. Run 'wolfden check' for details.\n",
            output);
    }

    [Fact]
    public void ADenYamlThatDoesntLoadExitsTwo()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string> { ["den.yaml"] = "format: [1]\n" });

        var (exitCode, output, error) = Cli.Run("index", den.Root);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Equal($"wolfden index: den.yaml in '{den.Root}' doesn't load, so nothing could be indexed. Run 'wolfden check' to see why.\n", error);
    }

    [Fact]
    public void AMissingFolderExitsTwo()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wolfden-cli-tests", "no-such-den-" + Guid.NewGuid().ToString("N"));

        var (exitCode, output, error) = Cli.Run("index", missing);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Equal($"wolfden index: there's no folder at '{missing}'.\n", error);
    }

    [Fact]
    public void AFolderThatIsntADenExitsTwo()
    {
        using var folder = TempDen.Empty();

        var (exitCode, _, error) = Cli.Run("index", folder.Root);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith($"wolfden index: '{folder.Root}' has no den.yaml, so it isn't a den.", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(folder.Root, ".wolfden")));
    }

    [Fact]
    public void AFileWhereTheIndexFolderBelongsExitsTwo()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string> { ["den.yaml"] = "format: 1\n", [".wolfden"] = "not a folder\n" });

        var (exitCode, _, error) = Cli.Run("index", den.Root);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith($"wolfden index: couldn't rebuild the index of the den at '{den.Root}': ", error, StringComparison.Ordinal);
    }
}
