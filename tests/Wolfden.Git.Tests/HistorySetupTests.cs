using System.Text;
using Wolfden.Core.History;

namespace Wolfden.Git.Tests;

/// <summary>
/// Turning history on for a den that has none (#195), with real
/// repositories in temporary folders. Git runs with a configuration of the
/// tests' own: the tests' usual one, or, for a missing name or email, a
/// global config file of their own with none, so the developer's own
/// settings are never read or written.
/// </summary>
public sealed class HistorySetupTests : IDisposable
{
    private const string Mara = "universes/kestrel/entities/characters/mara-voss.md";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            TestRepo.DeleteFolder(folder);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-14")]
    public void ADenWithoutHistoryGetsARepositoryAGitignoreAndAFirstCheckpoint()
    {
        var den = Den();
        var setup = new GitHistorySetup(den, TestRepo.Options);
        Assert.Same(NoHistory.Instance, GitHistory.ForDen(den, TestRepo.Options, Token));

        var check = setup.Check(Token);
        Assert.True(check.Possible);
        Assert.False(check.NeedsName);
        Assert.False(check.NeedsEmail);
        Assert.False(check.Finishing);

        var result = setup.TurnOn(new HistorySetupRequest(), Token);

        Assert.Equal(HistorySetupStatus.Done, result.Status);
        Assert.Null(result.Code);
        Assert.True(result.RepositoryMade);
        Assert.Equal("created", result.GitIgnore);
        Assert.Empty(result.IdentitySaved);
        Assert.Equal(
            [
                "Made the den's folder a git repository.",
                "Created .gitignore, with .wolfden/, so Wolfden's index stays out of the history.",
                "Made the first checkpoint, \"Start of history\", of 2 files.",
            ],
            result.Steps);
        Assert.Equal("# Wolfden's index, rebuilt from the den's files\n.wolfden/\n", File.ReadAllText(Path.Combine(den, ".gitignore")));

        var history = GitHistory.ForDen(den, TestRepo.Options, Token);
        Assert.Equal(HistoryCapabilities.All, history.Capabilities);
        var log = history.Log(new HistoryLogQuery(), Token).Value;
        var first = Assert.Single(log);
        Assert.Equal(result.Checkpoint!.Id, first.Id);
        Assert.Equal("Start of history", first.Message.TrimEnd('\n'));
        Assert.Equal("Test Writer", first.Author);
        Assert.Equal(["den.yaml", Mara], first.Changes.Select(c => c.Path).Order(StringComparer.Ordinal));

        // Nothing was saved in the repository's own config: git had a name and email.
        Assert.DoesNotContain("[user]", File.ReadAllText(Path.Combine(den, ".git", "config")), StringComparison.Ordinal);

        // Now it has history, turning it on again is refused, and nothing changes.
        var again = setup.TurnOn(new HistorySetupRequest(), Token);
        Assert.Equal(HistorySetupStatus.Refused, again.Status);
        Assert.Equal(HistorySetupCodes.HasRepository, again.Code);
        Assert.Single(history.Log(new HistoryLogQuery(), Token).Value);
    }

    [Theory]
    [Trait("Scenario", "SC-HIST-14")]
    [InlineData("*.tmp\n", "*.tmp\n.wolfden/\n", "added")]
    [InlineData("*.tmp", "*.tmp\n.wolfden/\n", "added")]
    [InlineData("*.tmp\r\n", "*.tmp\r\n.wolfden/\r\n", "added")]
    [InlineData("", ".wolfden/\n", "added")]
    [InlineData("*.tmp\n.wolfden\n", "*.tmp\n.wolfden\n", "present")]
    [InlineData("/.wolfden/  \r\n", "/.wolfden/  \r\n", "present")]

    // Git's own answer: a leading space is part of the pattern, and a later negation wins.
    [InlineData(" .wolfden/\n", " .wolfden/\n.wolfden/\n", "added")]
    [InlineData(".wolfden/\n!.wolfden/\n", ".wolfden/\n!.wolfden/\n.wolfden/\n", "added")]
    public void AnExistingGitignoreKeepsWhatItHasAndGetsTheLineOnlyIfItsMissing(string before, string after, string outcome)
    {
        var den = Den();
        File.WriteAllText(Path.Combine(den, ".gitignore"), before, new UTF8Encoding(false));

        var result = new GitHistorySetup(den, TestRepo.Options).TurnOn(new HistorySetupRequest(), Token);

        Assert.Equal(HistorySetupStatus.Done, result.Status);
        Assert.Equal(outcome, result.GitIgnore);
        Assert.Equal(after, File.ReadAllText(Path.Combine(den, ".gitignore")));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-14")]
    public void WithoutTheFirstCheckpointTheRepositoryHasNone()
    {
        var den = Den();

        var result = new GitHistorySetup(den, TestRepo.Options).TurnOn(new HistorySetupRequest(FirstCheckpoint: false), Token);

        Assert.Equal(HistorySetupStatus.Done, result.Status);
        Assert.Null(result.Checkpoint);
        Assert.Equal("Made no first checkpoint, as asked.", result.Steps[^1]);
        var history = GitHistory.ForDen(den, TestRepo.Options, Token);
        Assert.Empty(history.Log(new HistoryLogQuery(), Token).Value);
        Assert.Equal(2, history.Pending(Token).Value.Count);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-15")]
    public void AMissingNameAndEmailAreAskedForAndSavedForTheDensRepositoryOnly()
    {
        var den = Den();
        var global = Config("");
        var options = Without(global);
        var setup = new GitHistorySetup(den, options);

        var check = setup.Check(Token);
        Assert.True(check.Possible);
        Assert.True(check.NeedsName);
        Assert.True(check.NeedsEmail);
        Assert.False(check.IdentityCertain);

        // Not given: refused, and the repository made to ask git is taken away again.
        var refused = setup.TurnOn(new HistorySetupRequest(), Token);
        Assert.Equal(HistorySetupStatus.Refused, refused.Status);
        Assert.Equal(HistorySetupCodes.IdentityNeeded, refused.Code);
        Assert.True(refused.NeedsName);
        Assert.True(refused.NeedsEmail);
        Assert.Contains("no name or email", refused.Problem, StringComparison.Ordinal);
        Assert.Contains("placeholder", refused.Problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(den, ".git")));
        Assert.False(File.Exists(Path.Combine(den, ".gitignore")));

        // Given: a pen name and a placeholder email, saved in the repository's own config.
        var result = setup.TurnOn(new HistorySetupRequest("  Ash Calloway ", "ash@example.invalid"), Token);

        Assert.Equal(HistorySetupStatus.Done, result.Status);
        Assert.Equal(["user.name", "user.email"], result.IdentitySaved);
        Assert.Contains("Saved your name and email for this den's repository only.", result.Steps);
        Assert.Equal("Ash Calloway\n", TestRepo.RunGit(den, "config", "--local", "--get", "user.name"));
        Assert.Equal("ash@example.invalid\n", TestRepo.RunGit(den, "config", "--local", "--get", "user.email"));
        Assert.Equal("", File.ReadAllText(global));
        Assert.Equal("Ash Calloway", GitHistory.ForDen(den, options, Token).Log(new HistoryLogQuery(), Token).Value[0].Author);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-15")]
    public void OnlyTheMissingHalfIsSavedAndAnExistingSettingIsLeftAlone()
    {
        var den = Den();
        var global = Config("[user]\n\tname = Ash Calloway\n");
        var options = Without(global);
        var setup = new GitHistorySetup(den, options);

        var check = setup.Check(Token);
        Assert.False(check.NeedsName);
        Assert.True(check.NeedsEmail);
        var refused = setup.TurnOn(new HistorySetupRequest("Someone Else"), Token);
        Assert.Equal(HistorySetupCodes.IdentityNeeded, refused.Code);
        Assert.False(refused.NeedsName);
        Assert.True(refused.NeedsEmail);

        var result = setup.TurnOn(new HistorySetupRequest("Someone Else", "ash@example.invalid"), Token);

        Assert.Equal(HistorySetupStatus.Done, result.Status);
        Assert.Equal(["user.email"], result.IdentitySaved);
        Assert.Contains("Saved your email for this den's repository only.", result.Steps);
        Assert.Contains("Git already has a name for this den (Ash Calloway), so the one given (Someone Else) wasn't used.", result.Steps);
        Assert.DoesNotContain("name", File.ReadAllText(Path.Combine(den, ".git", "config")), StringComparison.Ordinal);
        Assert.Equal("Ash Calloway", GitHistory.ForDen(den, options, Token).Log(new HistoryLogQuery(), Token).Value[0].Author);
        Assert.Equal("[user]\n\tname = Ash Calloway\n", File.ReadAllText(global));
    }

    [Theory]
    [InlineData("Ash\nCalloway", null, "The name must be one line")]
    [InlineData("Ash <Calloway>", null, "can't hold '<' or '>'")]
    [InlineData(null, "ash@example.invalid\t", null)]
    [InlineData(null, "a\u0007b", "The email must be one line")]
    public void ANameOrEmailThatGitCantRecordIsRefused(string? name, string? email, string? problem)
    {
        var den = Den();

        var result = new GitHistorySetup(den, TestRepo.Options).TurnOn(new HistorySetupRequest(name, email), Token);

        if (problem is null)
        {
            // Trimmed first, so a trailing tab is fine.
            Assert.Equal(HistorySetupStatus.Done, result.Status);
            return;
        }

        Assert.Equal(HistorySetupStatus.Refused, result.Status);
        Assert.Equal(HistorySetupCodes.BadArgument, result.Code);
        Assert.Contains(problem, result.Problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(den, ".git")));
    }

    [Fact]
    public void ATooLongNameIsRefused()
    {
        Assert.Equal("The name is at most 200 characters, not 201.", HistorySetup.IdentityProblem("name", new string('a', 201)));
        Assert.Null(HistorySetup.IdentityProblem("name", new string('a', 200)));
        Assert.Equal("The email can't be empty.", HistorySetup.IdentityProblem("email", "  "));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-17")]
    public void ADenInsideALargerRepositoryIsRefusedWithNothingChanged()
    {
        using var repo = TestRepo.Create("notes/kestrel");

        var result = new GitHistorySetup(repo.DenRoot, TestRepo.Options).TurnOn(new HistorySetupRequest(), Token);

        Assert.Equal(HistorySetupStatus.Refused, result.Status);
        Assert.Equal(HistorySetupCodes.InRepository, result.Code);
        Assert.Contains("which keeps its history already", result.Problem, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repo.DenRoot, ".gitignore")));
        Assert.False(Directory.Exists(Path.Combine(repo.DenRoot, ".git")));
        Assert.False(new GitHistorySetup(repo.DenRoot, TestRepo.Options).Check(Token).Possible);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-17")]
    public void ADenWhoseOwnRepositoryHasCheckpointsIsRefused()
    {
        using var repo = TestRepo.Create();
        var head = repo.CommitAll("Start");

        var result = new GitHistorySetup(repo.DenRoot, TestRepo.Options).TurnOn(new HistorySetupRequest(), Token);

        Assert.Equal(HistorySetupStatus.Refused, result.Status);
        Assert.Equal(HistorySetupCodes.HasRepository, result.Code);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD").Trim());
        Assert.False(File.Exists(Path.Combine(repo.DenRoot, ".gitignore")));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-16")]
    public void ADenWhoseOwnRepositoryHasNoCheckpointsIsFinished()
    {
        using var repo = TestRepo.Create();
        var setup = new GitHistorySetup(repo.DenRoot, TestRepo.Options);
        Assert.True(setup.Check(Token).Finishing);

        var result = setup.TurnOn(new HistorySetupRequest(), Token);

        Assert.Equal(HistorySetupStatus.Done, result.Status);
        Assert.False(result.RepositoryMade);
        Assert.Equal("Used the git repository already in the den's folder, which had no checkpoints yet.", result.Steps[0]);
        Assert.Equal("Start of history", repo.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-16")]
    public void AFailedFirstCheckpointSaysWhatWasDoneAndTryingAgainFinishes()
    {
        var den = Den();
        var hooks = Folder();
        var hook = Path.Combine(hooks, "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\necho 'notes must be proofread first' >&2\nexit 1\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var global = Config(
            $"[user]\n\tname = Test Writer\n\temail = writer@example.com\n[commit]\n\tgpgsign = false\n[core]\n\tautocrlf = false\n\thooksPath = {hooks.Replace('\\', '/')}\n");
        var setup = new GitHistorySetup(den, Without(global));

        var failed = setup.TurnOn(new HistorySetupRequest(), Token);

        Assert.Equal(HistorySetupStatus.Incomplete, failed.Status);
        Assert.Equal(HistorySetupCodes.HistoryFailed, failed.Code);
        Assert.True(failed.RepositoryMade);
        Assert.Equal("created", failed.GitIgnore);
        Assert.Null(failed.Checkpoint);
        Assert.Equal(2, failed.Steps.Count);
        Assert.Contains("Couldn't make the first checkpoint", failed.Problem, StringComparison.Ordinal);
        Assert.Contains("notes must be proofread first", failed.Problem, StringComparison.Ordinal);
        Assert.Contains("history is on", failed.Problem, StringComparison.Ordinal);
        Assert.Contains("run 'wolfden init --git' in the den again", failed.Problem, StringComparison.Ordinal);

        // The hook is fixed: trying again finishes, with the same repository and .gitignore.
        File.Delete(hook);
        var finished = setup.TurnOn(new HistorySetupRequest(), Token);

        Assert.Equal(HistorySetupStatus.Done, finished.Status);
        Assert.False(finished.RepositoryMade);
        Assert.Equal("present", finished.GitIgnore);
        Assert.NotNull(finished.Checkpoint);
        Assert.Equal("# Wolfden's index, rebuilt from the den's files\n.wolfden/\n", File.ReadAllText(Path.Combine(den, ".gitignore")));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-17")]
    public void WithoutGitItSaysSoAndChangesNothing()
    {
        var den = Den();
        var setup = new GitHistorySetup(den, TestRepo.Options with { Executable = "wolfden-test-no-such-git" });

        var check = setup.Check(Token);
        var result = setup.TurnOn(new HistorySetupRequest(), Token);

        Assert.False(check.Possible);
        Assert.Equal(HistorySetupCodes.GitNotInstalled, check.Code);
        Assert.Equal(HistorySetupStatus.Refused, result.Status);
        Assert.Equal(HistorySetupCodes.GitNotInstalled, result.Code);
        Assert.StartsWith("Git isn't installed", result.Problem, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(den, ".gitignore")));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-15")]
    public void ANameAndEmailGitGivesOnlyInsideTheRepositoryAreUsedAndNothingIsAsked()
    {
        // includeIf "gitdir:…" applies only once the den's folder is a repository.
        var den = Den();
        var included = Config("[user]\n\tname = Ash Calloway\n\temail = ash@example.invalid\n");
        var global = Config($"[includeIf \"gitdir/i:**/{Path.GetFileName(den)}/\"]\n\tpath = {included.Replace('\\', '/')}\n");
        var options = Without(global);
        var setup = new GitHistorySetup(den, options);
        var check = setup.Check(Token);
        Assert.True(check.NeedsName);
        Assert.False(check.IdentityCertain);

        var result = setup.TurnOn(new HistorySetupRequest(), Token);

        Assert.Equal(HistorySetupStatus.Done, result.Status);
        Assert.Empty(result.IdentitySaved);
        Assert.Equal("Ash Calloway", GitHistory.ForDen(den, options, Token).Log(new HistoryLogQuery(), Token).Value[0].Author);
        Assert.DoesNotContain("[user]", File.ReadAllText(Path.Combine(den, ".git", "config")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-15")]
    public void ANameAndEmailGivenWhenGitHasThemAfterAllAreNotUsedAndItSaysSo()
    {
        var den = Den();
        var included = Config("[user]\n\tname = Ash Calloway\n\temail = ash@example.invalid\n");
        var global = Config($"[includeIf \"gitdir/i:**/{Path.GetFileName(den)}/\"]\n\tpath = {included.Replace('\\', '/')}\n");

        var result = new GitHistorySetup(den, Without(global)).TurnOn(new HistorySetupRequest("Mara Voss", "mara@example.invalid"), Token);

        Assert.Equal(HistorySetupStatus.Done, result.Status);
        Assert.Empty(result.IdentitySaved);
        Assert.Contains("Git already has a name for this den (Ash Calloway), so the one given (Mara Voss) wasn't used.", result.Steps);
        Assert.Contains("Git already has an email for this den (ash@example.invalid), so the one given (mara@example.invalid) wasn't used.", result.Steps);
    }

    [Theory]
    [Trait("Scenario", "SC-HIST-15")]
    [InlineData(";")]
    [InlineData(" ,;: ")]
    public void ANameGitWontRecordIsRefusedBeforeAnythingIsSaved(string name)
    {
        var den = Den();
        var setup = new GitHistorySetup(den, Without(Config("")));

        var result = setup.TurnOn(new HistorySetupRequest(name, "ash@example.invalid"), Token);

        Assert.Equal(HistorySetupStatus.Refused, result.Status);
        Assert.Equal(HistorySetupCodes.BadArgument, result.Code);
        Assert.StartsWith("Git won't record that name and email on a checkpoint", result.Problem, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", result.Problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(den, ".git")));
        Assert.False(File.Exists(Path.Combine(den, ".gitignore")));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-17")]
    public void AnEmptyDotGitInsideALargerRepositoryDoesntMakeTheDenItsOwnRepository()
    {
        // A half-synced or half-deleted .git: git doesn't take it for a repository, so the den is in the larger one.
        using var repo = TestRepo.Create("notes/kestrel");
        Directory.CreateDirectory(Path.Combine(repo.DenRoot, ".git"));
        var configBefore = File.ReadAllText(Path.Combine(repo.Root, ".git", "config"));

        var result = new GitHistorySetup(repo.DenRoot, TestRepo.Options).TurnOn(new HistorySetupRequest("Ash Calloway", "ash@example.invalid"), Token);

        Assert.Equal(HistorySetupStatus.Refused, result.Status);
        Assert.Equal(HistorySetupCodes.InRepository, result.Code);
        Assert.Equal(configBefore, File.ReadAllText(Path.Combine(repo.Root, ".git", "config")));
        Assert.Throws<InvalidOperationException>(() => repo.Git("rev-parse", "--verify", "HEAD"));
        Assert.False(File.Exists(Path.Combine(repo.DenRoot, ".gitignore")));
    }

    [Fact]
    public void AGitignoreThatCantBeWrittenSaysSoWithOneFullStop()
    {
        var den = Den();
        Directory.CreateDirectory(Path.Combine(den, ".gitignore"));

        var result = new GitHistorySetup(den, TestRepo.Options).TurnOn(new HistorySetupRequest(), Token);

        Assert.Equal(HistorySetupStatus.Incomplete, result.Status);
        Assert.Equal(HistorySetupCodes.WriteFailed, result.Code);
        Assert.StartsWith("Couldn't update .gitignore: ", result.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("..", result.Problem, StringComparison.Ordinal);
    }

    /// <summary>A den without history: a den.yaml and one note, in a folder outside any repository.</summary>
    private string Den()
    {
        var den = Folder();
        File.WriteAllText(Path.Combine(den, "den.yaml"), "format: 1\n", new UTF8Encoding(false));
        var note = Path.Combine(den, Mara.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        File.WriteAllText(note, "# Mara Voss\n", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(den, ".wolfden"));
        File.WriteAllText(Path.Combine(den, ".wolfden", "index.db"), "not recorded", new UTF8Encoding(false));
        return den;
    }

    private string Folder()
    {
        var folder = TestRepo.NewFolder();
        _folders.Add(folder);
        return folder;
    }

    /// <summary>A global git config file of the test's own, holding <paramref name="text"/>.</summary>
    private string Config(string text)
    {
        var folder = Folder();
        var file = Path.Combine(folder, "gitconfig");
        File.WriteAllText(file, text, new UTF8Encoding(false));
        return file;
    }

    /// <summary>The tests' git, with <paramref name="global"/> as its global config in place of the usual one.</summary>
    private static GitOptions Without(string global)
    {
        var environment = new Dictionary<string, string?>(TestRepo.Options.Environment)
        {
            ["GIT_CONFIG_GLOBAL"] = global,
            ["GIT_AUTHOR_NAME"] = null,
            ["GIT_AUTHOR_EMAIL"] = null,
            ["GIT_COMMITTER_NAME"] = null,
            ["GIT_COMMITTER_EMAIL"] = null,
            ["EMAIL"] = null,
        };
        return TestRepo.Options with { Environment = environment };
    }
}
