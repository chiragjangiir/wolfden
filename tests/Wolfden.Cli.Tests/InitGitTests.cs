using System.Text.Json;
using Wolfden.Core.History;
using Wolfden.Git;

namespace Wolfden.Cli.Tests;

/// <summary><c>wolfden init --git</c>: optionally making the new den a git repository (#122).</summary>
public class InitGitTests
{
    [Fact]
    [Trait("Scenario", "SC-CLI-12")]
    public void WithGitTheNewDenIsARepositoryWithHistory()
    {
        using var parent = TempDen.Empty();
        var folder = Path.Combine(parent.Root, "kestrel-notes");

        var (exitCode, output, error) = Cli.Run("init", folder, "--universe", "the-long-dark", "--git");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.Contains(
            $"Created a den at '{folder}'.\nMade it a git repository, so Wolfden can keep the den's history. Nothing is committed yet.\n" +
            "Next: add series, books, and entities (format.md §5.1), then run 'wolfden check'.\n",
            output,
            StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(folder, ".git")));
        Assert.Equal("", TestGit.Run(folder, "log", "--all", "--oneline"));

        // The den checks clean, and its history works: the first checkpoint records it.
        Assert.Equal(CliApp.ExitOk, Cli.Run("check", folder).ExitCode);
        var history = GitHistory.ForDen(folder, TestGit.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HistoryCapabilities.All, history.Capabilities);
        var first = history.Checkpoint(new CheckpointRequest("Start the den"), TestContext.Current.CancellationToken).Value!;
        Assert.Equal(["den.yaml", "universes/the-long-dark/universe.md"], first.Changes.Select(c => c.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-12")]
    public void InsideARepositoryItSaysSoAndMakesNoNewOne()
    {
        using var writing = TempDen.Empty();
        TestGit.Run(writing.Root, "init", "--quiet");
        var folder = Path.Combine(writing.Root, "vael-notes");

        var (exitCode, output, error) = Cli.Run("init", folder, "--git", "--json");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.False(Directory.Exists(Path.Combine(folder, ".git")));
        Assert.True(File.Exists(Path.Combine(folder, "den.yaml")));
        using var json = JsonDocument.Parse(output);
        var git = json.RootElement.GetProperty("git");
        Assert.Equal("already_in_repository", git.GetProperty("outcome").GetString());
        Assert.StartsWith("It's already inside the git repository at '", git.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.EndsWith(
            "', so no new repository was made. Wolfden keeps the den's history there, for the den's files only.",
            git.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-12")]
    public void WithoutGitInstalledTheDenIsStillMadeAndItExplains()
    {
        using var folder = TempDen.Empty();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = InitCommand.Run([folder.Root, "--git"], output, error, TestGit.Options with { Executable = "wolfden-test-no-such-git" });

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error.ToString());
        Assert.True(folder.Exists("den.yaml"));
        Assert.False(Directory.Exists(Path.Combine(folder.Root, ".git")));
        Assert.Contains(
            "Git isn't installed, or Wolfden can't find it on the PATH, so the den wasn't made a repository. " +
            "The den works without one; to keep its history, install git and run 'git init' in the den's folder.",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-12")]
    public void WithoutTheFlagTheDenIsAPlainFolder()
    {
        using var folder = TempDen.Empty();

        var (exitCode, output, _) = Cli.Run("init", folder.Root);

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.False(Directory.Exists(Path.Combine(folder.Root, ".git")));
        Assert.EndsWith(
            "Next: add a universe (format.md §6.2), then run 'wolfden check'. Wolfden doesn't run git unless asked (--git); 'git init' makes the den a repository.\n",
            output,
            StringComparison.Ordinal);
        Assert.Same(NoHistory.Instance, GitHistory.ForDen(folder.Root, TestGit.Options, TestContext.Current.CancellationToken));
    }
}
