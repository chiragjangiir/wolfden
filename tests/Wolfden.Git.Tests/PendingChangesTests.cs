using Wolfden.Core.History;

namespace Wolfden.Git.Tests;

/// <summary>
/// What a checkpoint now would record (#30), for the web UI's checkpoint
/// indicator, and whether a git write may be under way, which automatic
/// checkpoints wait for.
/// </summary>
public sealed class PendingChangesTests
{
    [Fact]
    [Trait("Scenario", "SC-WEB-50")]
    public void BeforeTheFirstCheckpointEveryDenFileIsPending()
    {
        using var repo = TestRepo.Create("den");
        repo.WriteDen("den.yaml", "format: 1\n");
        repo.WriteDen("universes/u/universe.md", "---\nname: U\n---\n");
        repo.WriteDen(".wolfden/index.db", "hidden");
        repo.WriteRepo("README.md", "not the den's");

        var pending = repo.History.Pending(TestContext.Current.CancellationToken);

        Assert.True(pending.IsAvailable);
        Assert.Equal(
            [(HistoryChangeKind.Added, "den.yaml"), (HistoryChangeKind.Added, "universes/u/universe.md")],
            pending.Value.Select(c => (c.Kind, c.Path)).OrderBy(c => c.Path, StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-50")]
    public void AfterACheckpointOnlyWhatChangedSinceIsPending()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen("den.yaml", "format: 1\n");
        repo.WriteDen("a.md", "one\n");
        repo.WriteDen("old.md", "a note that will move, long enough for git to pair it as a rename\n");
        repo.CommitAll("first");
        Assert.Empty(repo.History.Pending(TestContext.Current.CancellationToken).Value);

        repo.WriteDen("a.md", "two\n");
        repo.WriteDen("b.md", "new\n");
        File.Move(Path.Combine(repo.DenRoot, "old.md"), Path.Combine(repo.DenRoot, "moved.md"));

        var pending = repo.History.Pending(TestContext.Current.CancellationToken).Value.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        Assert.Equal(
            [
                new HistoryFileChange(HistoryChangeKind.Modified, "a.md"),
                new HistoryFileChange(HistoryChangeKind.Added, "b.md"),
                new HistoryFileChange(HistoryChangeKind.Renamed, "moved.md", "old.md"),
            ],
            pending);

        // Nothing staged by looking.
        Assert.Equal("", repo.Git("diff", "--cached", "--name-only").Trim());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void AnIndexLockMeansAWriteMayBeUnderWay()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen("den.yaml", "format: 1\n");
        repo.CommitAll("first");
        var history = repo.History;
        Assert.False(history.IsWriting);

        var gitDir = repo.Git("rev-parse", "--absolute-git-dir").Trim();
        var lockFile = Path.Combine(gitDir, "index.lock");
        File.WriteAllText(lockFile, "");
        try
        {
            Assert.True(history.IsWriting);
        }
        finally
        {
            File.Delete(lockFile);
        }

        Assert.False(history.IsWriting);
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-53")]
    [InlineData("merge", "a merge")]
    [InlineData("rebase", "a rebase")]
    [InlineData("cherry-pick", "a cherry-pick")]
    [InlineData("revert", "a revert")]
    [InlineData("bisect", "a bisect")]
    [InlineData("detach", "a detached HEAD")]
    public void GitInTheMiddleOfAnOperationIsSaidAndAutoCheckpointsWait(string operation, string said)
    {
        using var repo = TestRepo.Create();
        repo.WriteDen("a.md", "base\n");
        repo.CommitAll("base");
        var main = repo.Git("symbolic-ref", "--short", "HEAD").Trim();
        Assert.Null(repo.History.InterruptedOperation);

        // Two branches that change the same line, so the operation stops part way.
        repo.Git("checkout", "--quiet", "-b", "other");
        repo.WriteDen("a.md", "theirs\n");
        var theirs = repo.CommitAll("theirs");
        repo.Git("checkout", "--quiet", main);
        repo.WriteDen("a.md", "ours\n");
        var ours = repo.CommitAll("ours");
        switch (operation)
        {
            case "merge":
                Fails(repo, "merge", "--quiet", "other");
                break;
            case "rebase":
                Fails(repo, "rebase", "other");
                break;
            case "cherry-pick":
                Fails(repo, "cherry-pick", theirs);
                break;
            case "revert":
                repo.WriteDen("a.md", "later\n");
                repo.CommitAll("later");
                Fails(repo, "revert", "--no-edit", ours);
                break;
            case "bisect":
                repo.Git("bisect", "start");
                break;
            default:
                repo.Git("checkout", "--quiet", "--detach");
                break;
        }

        Assert.StartsWith(said, repo.History.InterruptedOperation, StringComparison.Ordinal);

        // An automatic checkpoint waits, and records nothing.
        repo.WriteDen("b.md", "a new note\n");
        var head = repo.Git("rev-parse", "HEAD").Trim();
        var clock = new Clock();
        var log = new List<string>();
        var auto = new AutoCheckpointer(repo.DenRoot, () => repo.History, new CheckpointLedger(new Wolfden.Core.Structure.FileSystemDenStore(repo.DenRoot)), clock, log.Add)
        {
            Fingerprint = () => "still",
            Setting = () => new Wolfden.Core.Structure.AutoCheckpointSetting(TimeSpan.FromMinutes(5), null, null),
        };
        auto.Start();
        auto.Dispose();
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(AutoCheckpointOutcome.Interrupted, auto.Tick());
        Assert.Equal(head, repo.Git("rev-parse", "HEAD").Trim());
        Assert.Single(log, l => l.Contains(said, StringComparison.Ordinal));
    }

    private static void Fails(TestRepo repo, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repo.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in TestRepo.Options.Environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        start.Environment["GIT_EDITOR"] = "true";
        using var process = System.Diagnostics.Process.Start(start)!;
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.NotEqual(0, process.ExitCode);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ADenWithoutHistoryHasNothingPending()
    {
        IHistoryProvider none = NoHistory.Instance;
        var pending = none.Pending(TestContext.Current.CancellationToken);
        Assert.False(pending.IsAvailable);
        Assert.False(none.IsWriting);
    }
}
