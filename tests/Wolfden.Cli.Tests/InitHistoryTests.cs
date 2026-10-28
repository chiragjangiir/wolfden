using System.Text;
using System.Text.Json;
using Wolfden.Core.History;
using Wolfden.Git;

namespace Wolfden.Cli.Tests;

/// <summary>
/// <c>wolfden init --git</c> on a folder that's already a den: turning on
/// its history (#195). Git runs with the tests' own configuration, or, for
/// a missing name or email, with a global config file of the test's own
/// that has none, never the developer's.
/// </summary>
public class InitHistoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "SC-HIST-14")]
    public void AnExistingDenGetsHistoryWithAFirstCheckpoint()
    {
        using var den = TempDen.DraculaCopy();
        File.WriteAllText(Path.Combine(den.Root, ".gitignore"), "*.bak\n", new UTF8Encoding(false));

        var (exitCode, output, error) = Cli.Run("init", den.Root, "--git");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        var history = GitHistory.ForDen(den.Root, TestGit.Options, Token);
        var first = Assert.Single(history.Log(new HistoryLogQuery(), Token).Value);
        Assert.Equal(
            $"Turned on history for the den at '{den.Root}'.\n" +
            "Made the den's folder a git repository.\n" +
            "Added .wolfden/ to .gitignore, so Wolfden's index stays out of the history.\n" +
            $"Made the first checkpoint, \"Start of history\", of {first.Changes.Count} files.\n" +
            "Checkpoints work from now on: in the web UI ('wolfden serve', then Checkpoint…), or through an AI assistant.\n",
            output);
        Assert.Equal("*.bak\n.wolfden/\n", den.Read(".gitignore"));
        Assert.Equal("Start of history", first.Message.TrimEnd('\n'));
        Assert.Contains(first.Changes, c => c.Path == "den.yaml");
        Assert.Empty(history.Pending(Token).Value);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-14")]
    public void FromInsideTheDenWithNoCheckpointItSaysWhatItDidAsJson()
    {
        using var den = TempDen.DraculaCopy();

        var (exitCode, output, error) = Cli.RunIn(Path.Combine(den.Root, "universes"), "init", "..", "--git", "--no-checkpoint", "--json");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        using var json = JsonDocument.Parse(output);
        Assert.Equal([".gitignore"], json.RootElement.GetProperty("created").EnumerateArray().Select(e => e.GetString()));
        var git = json.RootElement.GetProperty("git");
        Assert.Equal("created", git.GetProperty("outcome").GetString());
        Assert.Equal("created", git.GetProperty("gitIgnore").GetString());
        Assert.Equal("Made no first checkpoint, as asked.", git.GetProperty("steps").EnumerateArray().Last().GetString());
        Assert.False(git.TryGetProperty("checkpoint", out _));
        Assert.False(git.TryGetProperty("code", out _));
        Assert.Empty(GitHistory.ForDen(den.Root, TestGit.Options, Token).Log(new HistoryLogQuery(), Token).Value);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-15")]
    public void AMissingNameAndEmailAreAskedForThenSavedForTheDenOnly()
    {
        using var den = TempDen.DraculaCopy();
        using var config = TempDen.Empty();
        var global = Path.Combine(config.Root, "gitconfig");
        File.WriteAllText(global, "", new UTF8Encoding(false));
        var git = NoIdentity(global);

        var output = new StringWriter();
        var error = new StringWriter();
        var refused = InitCommand.Run([den.Root, "--git"], output, error, git);

        Assert.Equal(CliApp.ExitUsage, refused);
        Assert.Empty(output.ToString());
        Assert.Equal(
            "wolfden init: git records who made each checkpoint, and has no name or email for this den. " +
            "Run 'wolfden init --git' again with --author-name <name> --author-email <email>: they're saved for this den's repository only, never globally. " +
            "Any name will do, a pen name included, and the email can be a placeholder, such as you@example.com. Nothing was changed in git.\n",
            error.ToString().ReplaceLineEndings("\n"));
        Assert.False(Directory.Exists(Path.Combine(den.Root, ".git")));

        output = new StringWriter();
        error = new StringWriter();
        var done = InitCommand.Run([den.Root, "--git", "--author-name", "Ash Calloway", "--author-email", "ash@example.invalid"], output, error, git);

        Assert.Equal(CliApp.ExitOk, done);
        Assert.Contains("Saved your name and email for this den's repository only.\n", output.ToString().ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal("Ash Calloway\n", TestGit.Run(den.Root, "config", "--local", "--get", "user.name"));
        Assert.Equal("ash@example.invalid\n", TestGit.Run(den.Root, "config", "--local", "--get", "user.email"));
        Assert.Equal("", File.ReadAllText(global));
        Assert.Equal("Ash Calloway", GitHistory.ForDen(den.Root, git, Token).Log(new HistoryLogQuery(), Token).Value[0].Author);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-15")]
    public void ANameAndEmailGitGivesOnlyInsideTheRepositoryAreUsedAndOnesGivenSaySoIfNotUsed()
    {
        using var den = TempDen.DraculaCopy();
        using var config = TempDen.Empty();
        var included = Path.Combine(config.Root, "included");
        File.WriteAllText(included, "[user]\n\tname = Ash Calloway\n\temail = ash@example.invalid\n", new UTF8Encoding(false));
        var global = Path.Combine(config.Root, "gitconfig");
        File.WriteAllText(
            global, $"[includeIf \"gitdir/i:**/{Path.GetFileName(den.Root)}/\"]\n\tpath = {included.Replace('\\', '/')}\n", new UTF8Encoding(false));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = InitCommand.Run([den.Root, "--git", "--author-name", "Mara Voss"], output, error, NoIdentity(global));

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error.ToString());
        Assert.Contains(
            "Git already has a name for this den (Ash Calloway), so the one given (Mara Voss) wasn't used.\n",
            output.ToString().ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Equal("Ash Calloway", GitHistory.ForDen(den.Root, NoIdentity(global), Token).Log(new HistoryLogQuery(), Token).Value[0].Author);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-12")]
    [Trait("Scenario", "SC-HIST-15")]
    public void ANewDenWithoutANameOrEmailIsMadeButItsHistoryIsntTurnedOn()
    {
        using var parent = TempDen.Empty();
        var global = Path.Combine(parent.Root, "gitconfig");
        File.WriteAllText(global, "[user]\n\tname = Ash Calloway\n", new UTF8Encoding(false));
        var folder = Path.Combine(parent.Root, "kestrel-notes");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = InitCommand.Run([folder, "--git"], output, error, NoIdentity(global));

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.True(File.Exists(Path.Combine(folder, "den.yaml")));
        Assert.False(Directory.Exists(Path.Combine(folder, ".git")));
        Assert.StartsWith(
            "wolfden init: the den was created, but its history wasn't turned on: git records who made each checkpoint, and has no email for this den. " +
            "Run 'wolfden init --git' in the den with --author-email <email>:",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Scenario", "SC-HIST-14")]
    [InlineData(new[] { "--name", "Kestrel" }, "--name and --universe don't apply")]
    [InlineData(new[] { "--universe", "kestrel" }, "--name and --universe don't apply")]
    public void NewDenOptionsAreRefusedForAnExistingDen(string[] extra, string says)
    {
        using var den = TempDen.DraculaCopy();

        var (exitCode, _, error) = Cli.Run(["init", den.Root, "--git", .. extra]);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Contains(says, error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(den.Root, ".git")));
    }

    [Theory]
    [InlineData(new[] { "--no-checkpoint" }, "wolfden init: --no-checkpoint goes with --git, which turns on the den's history.\n")]
    [InlineData(new[] { "--author-name", "Ash" }, "wolfden init: --author-name goes with --git, which turns on the den's history.\n")]
    [InlineData(new[] { "--git", "--author-email", "a\tb" }, "wolfden init: --author-email: The email must be one line, with no control characters.\n")]
    [InlineData(new[] { "--git", "--author-name", "Ash <Calloway>" }, "wolfden init: --author-name: The name can't hold '<' or '>', which git can't record in one.\n")]
    public void HistoryOptionsAreChecked(string[] extra, string expected)
    {
        using var den = TempDen.DraculaCopy();

        var (exitCode, _, error) = Cli.Run(["init", den.Root, .. extra]);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal(expected, error);
        Assert.False(Directory.Exists(Path.Combine(den.Root, ".git")));
    }

    [Fact]
    public void WithoutGitAnExistingDenIsStillRefused()
    {
        using var den = TempDen.DraculaCopy();

        var (exitCode, _, error) = Cli.Run("init", den.Root);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal(
            $"wolfden init: '{den.Root}' is already a den: it has a den.yaml. Nothing was changed. To turn on its history, run 'wolfden init --git' in it.\n",
            error);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-17")]
    public void ADenInsideALargerRepositoryIsRefused()
    {
        using var writing = TempDen.Empty();
        TestGit.Run(writing.Root, "init", "--quiet");
        writing.Write("notes/den.yaml", "format: 1\n");

        var (exitCode, output, error) = Cli.Run("init", Path.Combine(writing.Root, "notes"), "--git", "--json");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        using var json = JsonDocument.Parse(output);
        var git = json.RootElement.GetProperty("git");
        Assert.Equal("refused", git.GetProperty("outcome").GetString());
        Assert.Equal("in_repository", git.GetProperty("code").GetString());
        Assert.Contains("which keeps its history already", error, StringComparison.Ordinal);
        Assert.False(writing.Exists("notes/.gitignore"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-17")]
    public void ADenWithHistoryAlreadyIsRefused()
    {
        using var den = TempDen.DraculaCopy();
        Assert.Equal(CliApp.ExitOk, Cli.Run("init", den.Root, "--git").ExitCode);

        var (exitCode, output, error) = Cli.Run("init", den.Root, "--git");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Equal("wolfden init: The den's folder is already a git repository with checkpoints, so its history is on. Nothing was changed.\n", error);
        Assert.Single(GitHistory.ForDen(den.Root, TestGit.Options, Token).Log(new HistoryLogQuery(), Token).Value);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-16")]
    public void ADenWhoseOwnRepositoryHasNoCheckpointsIsFinished()
    {
        using var den = TempDen.DraculaCopy();
        TestGit.Run(den.Root, "init", "--quiet");

        var (exitCode, output, error) = Cli.Run("init", den.Root, "--git", "--json");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        using var json = JsonDocument.Parse(output);
        Assert.Equal("finished", json.RootElement.GetProperty("git").GetProperty("outcome").GetString());
        Assert.Equal("Start of history", json.RootElement.GetProperty("git").GetProperty("checkpoint").GetProperty("message").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-17")]
    public void WithoutGitInstalledAnExistingDenIsRefused()
    {
        using var den = TempDen.DraculaCopy();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = InitCommand.Run([den.Root, "--git"], output, error, TestGit.Options with { Executable = "wolfden-test-no-such-git" });

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith("wolfden init: Git isn't installed, or Wolfden can't find it on the PATH", error.ToString(), StringComparison.Ordinal);
        Assert.False(den.Exists(".gitignore"));
    }

    private static GitOptions NoIdentity(string global)
    {
        var environment = new Dictionary<string, string?>(TestGit.Options.Environment)
        {
            ["GIT_CONFIG_GLOBAL"] = global,
            ["GIT_AUTHOR_NAME"] = null,
            ["GIT_AUTHOR_EMAIL"] = null,
            ["GIT_COMMITTER_NAME"] = null,
            ["GIT_COMMITTER_EMAIL"] = null,
            ["EMAIL"] = null,
        };
        return TestGit.Options with { Environment = environment };
    }
}
