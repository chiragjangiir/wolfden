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

    [Fact]
    public void ADenWithoutHistoryHasNothingPending()
    {
        IHistoryProvider none = NoHistory.Instance;
        var pending = none.Pending(TestContext.Current.CancellationToken);
        Assert.False(pending.IsAvailable);
        Assert.False(none.IsWriting);
    }
}
