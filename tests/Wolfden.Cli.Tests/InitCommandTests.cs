using System.Text.Json;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Cli.Tests;

/// <summary><c>wolfden init</c>: making a new den that <c>wolfden check</c> passes.</summary>
public class InitCommandTests
{
    [Fact]
    [Trait("Scenario", "SC-CLI-1")]
    public void ANewDenWithAUniversePassesCheckAndCanBeQueried()
    {
        using var parent = TempDen.Empty();
        var folder = Path.Combine(parent.Root, "kestrel-notes");

        var (exitCode, output, error) = Cli.Run("init", folder, "--universe", "the-long-dark");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.StartsWith(
            "created den.yaml\ncreated universes/the-long-dark/universe.md\n\n" +
            $"Created a den at '{folder}'.\n",
            output,
            StringComparison.Ordinal);

        var universe = MarkdownFile.Parse(File.ReadAllBytes(Path.Combine(folder, "universes", "the-long-dark", "universe.md")), requireFrontmatter: true);
        Assert.True(Keys.IsId(((YamlScalar)universe.GetFrontmatterValue("id")!).Text));
        Assert.Equal("The Long Dark", ((YamlScalar)universe.GetFrontmatterValue("name")!).Text);

        Assert.Equal((CliApp.ExitOk, "No errors or warnings. Checked 0 entities; spoiler checks are off.\n", ""), Cli.Run("check", folder));
        Assert.Equal((CliApp.ExitOk, "Canon of universe 'the-long-dark' at the end of the story: 0 entities.\n", ""), Cli.Run("query", "--den", folder));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-1")]
    public void ANewDenWithoutAUniverseIsJustDenYaml()
    {
        using var folder = TempDen.Empty();

        var (exitCode, _, _) = Cli.Run("init", folder.Root);

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Equal(["den.yaml"], Directory.EnumerateFileSystemEntries(folder.Root).Select(Path.GetFileName));
        Assert.Equal(
            """
            # Wolfden den settings. The format is described in Wolfden's docs/format.md, section 5.2.
            format: 1
            # name: My Notes           # display name; defaults to the folder's name
            # spoiler_checks: true     # flag links to things the reader hasn't met yet (F-DEN-7)

            """.ReplaceLineEndings("\n"),
            folder.Read("den.yaml"));
        Assert.Equal(CliApp.ExitOk, Cli.Run("check", folder.Root).ExitCode);
    }

    [Fact]
    public void ANameIsWrittenSoItStaysAString()
    {
        using var folder = TempDen.Empty();

        Cli.Run("init", folder.Root, "--name", "True");

        var yaml = YamlFile.Parse(File.ReadAllBytes(Path.Combine(folder.Root, "den.yaml")));
        var name = (YamlScalar)yaml.GetValue("name")!;
        Assert.Equal("True", name.Text);
        Assert.NotEqual(YamlScalarStyle.Plain, name.Style);
        Assert.Equal("No errors or warnings. Checked 0 entities; spoiler checks are off.\n", Cli.Run("check", folder.Root).Output);
    }

    [Fact]
    public void OtherFilesInTheFolderAreLeftAlone()
    {
        using var folder = TempDen.FromFiles(new Dictionary<string, string> { ["README.md"] = "# My notes\n" });

        var (exitCode, _, _) = Cli.Run("init", folder.Root, "--universe", "vael");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Equal("# My notes\n", folder.Read("README.md"));
    }

    [Fact]
    public void JsonListsTheFilesCreated()
    {
        using var folder = TempDen.Empty();

        var (_, output, _) = Cli.Run("init", folder.Root, "--universe", "vael", "--json");

        using var json = JsonDocument.Parse(output);
        Assert.Equal(["version", "den", "created"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Path.GetFullPath(folder.Root), json.RootElement.GetProperty("den").GetString());
        Assert.Equal(["den.yaml", "universes/vael/universe.md"], json.RootElement.GetProperty("created").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void AnExistingDenIsNeverOverwritten()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string> { ["den.yaml"] = "format: 1\nname: Mine\n" });

        var (exitCode, output, error) = Cli.Run("init", den.Root, "--universe", "vael");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Equal($"wolfden init: '{den.Root}' is already a den: it has a den.yaml. Nothing was changed.\n", error);
        Assert.Equal("format: 1\nname: Mine\n", den.Read("den.yaml"));
        Assert.False(den.Exists("universes/vael/universe.md"));
    }

    [Fact]
    public void AFailurePartWayRemovesWhatThisRunCreated()
    {
        // universe.md is already there, so writing it fails after den.yaml was written.
        using var folder = TempDen.FromFiles(new Dictionary<string, string> { ["universes/vael/universe.md"] = "---\nname: Mine\n---\n" });

        var (exitCode, output, error) = Cli.Run("init", folder.Root, "--universe", "vael");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.StartsWith($"wolfden init: couldn't create the den at '{folder.Root}': ", error, StringComparison.Ordinal);
        Assert.EndsWith(" Nothing was left behind.\n", error, StringComparison.Ordinal);
        Assert.False(folder.Exists("den.yaml"));
        Assert.Equal("---\nname: Mine\n---\n", folder.Read("universes/vael/universe.md"));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-1")]
    public void ADenInsideADenIsRefused()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string> { ["den.yaml"] = "format: 1\n" });
        var inner = Path.Combine(den.Root, "archive", "old");

        var (exitCode, _, error) = Cli.Run("init", inner);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal($"wolfden init: '{inner}' is inside the den at '{den.Root}', and dens don't nest (F-LAY-1). Choose a folder outside it.\n", error);
        Assert.False(Directory.Exists(inner));
    }

    [Theory]
    [Trait("Scenario", "SC-CLI-1")]
    [InlineData("the-long-dark")]
    [InlineData("books/drafts/2026/the-long-dark")]
    public void ADenAroundADenIsRefused(string below)
    {
        using var folder = TempDen.FromFiles(new Dictionary<string, string> { [$"{below}/den.yaml"] = "format: 1\n" });
        var inner = Path.Combine(folder.Root, below.Replace('/', Path.DirectorySeparatorChar));

        var (exitCode, output, error) = Cli.Run("init", folder.Root, "--universe", "vael");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Equal($"wolfden init: '{folder.Root}' already holds the den at '{inner}', and dens don't nest (F-LAY-1). Choose a folder that doesn't hold one, or first move that den's den.yaml out of the way, or into a hidden folder such as .old/, which Wolfden ignores.\n", error);
        Assert.False(folder.Exists("den.yaml"));
        Assert.False(Directory.Exists(Path.Combine(folder.Root, "universes")));
    }

    [Fact]
    public void TheSearchBelowStopsAfterItsFolderLimit()
    {
        // Six folders on the first level, then the den one level further
        // down: a limit of five stops before it, a larger one finds it.
        var files = new Dictionary<string, string> { ["z/the-long-dark/den.yaml"] = "format: 1\n" };
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            files[$"{name}/readme.txt"] = "x\n";
        }

        using var folder = TempDen.FromFiles(files);

        Assert.Null(DenLocation.FindBelow(folder.Root, maxFolders: 5));
        Assert.Equal(Path.Combine(folder.Root, "z", "the-long-dark"), DenLocation.FindBelow(folder.Root, maxFolders: 7));
    }

    [Fact]
    public void TheSearchBelowSkipsFolderLinks()
    {
        using var folder = TempDen.Empty();
        using var elsewhere = TempDen.FromFiles(new Dictionary<string, string> { ["the-long-dark/den.yaml"] = "format: 1\n" });
        var link = Path.Combine(folder.Root, "linked");
        if (!TryLinkFolder(link, Path.Combine(elsewhere.Root, "the-long-dark")))
        {
            Assert.Skip("This system doesn't allow folder links to be created.");
        }

        try
        {
            Assert.Null(DenLocation.FindBelow(folder.Root));
            Assert.Equal(CliApp.ExitOk, Cli.Run("init", folder.Root).ExitCode);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>A symbolic link where the OS allows it, else a junction on Windows.</summary>
    private static bool TryLinkFolder(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows without developer mode; try a junction, which needs no rights.
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        mklink.WaitForExit();
        return mklink.ExitCode == 0 && Directory.Exists(link);
    }

    [Theory]
    [InlineData(".backup/old/den.yaml")]
    [InlineData("a/b/c/d/e/den.yaml")]
    public void ADenHiddenOrDeeperThanTheSearchDoesNotStopInit(string path)
    {
        using var folder = TempDen.FromFiles(new Dictionary<string, string> { [path] = "format: 1\n" });

        var (exitCode, _, error) = Cli.Run("init", folder.Root);

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.True(folder.Exists("den.yaml"));
    }

    [Theory]
    [InlineData("The Long Dark")]
    [InlineData("7th-fleet")]
    [InlineData("long--dark")]
    public void AnInvalidUniverseKeyIsRefused(string key)
    {
        using var folder = TempDen.Empty();

        var (exitCode, _, error) = Cli.Run("init", folder.Root, "--universe", key);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith($"wolfden init: '{key}' isn't a valid universe key", error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Root));
    }

    [Fact]
    public void AnEmptyNameIsRefused()
    {
        using var folder = TempDen.Empty();

        var (exitCode, _, error) = Cli.Run("init", folder.Root, "--name", " ");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal("wolfden init: the name can't be empty.\n", error);
    }
}

[Collection(nameof(CurrentFolder))]
public class InitCommandCurrentFolderTests
{
    [Fact]
    public void WithoutAFolderItMakesTheCurrentFolderADen()
    {
        using var folder = TempDen.Empty();

        var (exitCode, _, _) = Cli.RunIn(folder.Root, "init");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.True(folder.Exists("den.yaml"));
    }
}
