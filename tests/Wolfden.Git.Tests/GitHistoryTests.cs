using Wolfden.Core.History;

namespace Wolfden.Git.Tests;

/// <summary>
/// The git history provider (#19) against real repositories: log, diffs,
/// checkpoints, and tags, scoped to the den, with hidden paths never
/// recorded or reported (design.md §6).
/// </summary>
public sealed class GitHistoryTests
{
    private const string Mara = "universes/vael/entities/characters/mara-voss.md";
    private const string Fen = "universes/vael/entities/locations/fen-harbour.md";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "SC-HIST-2")]
    public void ANotesHistoryListsTheCheckpointsThatChangedItNewestFirst()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");
        var first = repo.CommitAll("Add Mara");
        repo.WriteDen(Fen, "# Fen Harbour\n");
        repo.CommitAll("Add the harbour");
        repo.WriteDen(Mara, "# Mara Voss\n\nA pilot.\n");
        var third = repo.CommitAll("Mara is a pilot\n\nShe flies the night run.");

        var history = repo.History;
        Assert.Equal("git", history.Name);
        Assert.Equal(HistoryCapabilities.All, history.Capabilities);

        var log = history.Log(new HistoryLogQuery(Mara), Token).Value;

        Assert.Equal([third, first], log.Select(e => e.Id));
        Assert.Equal("Mara is a pilot\n\nShe flies the night run.", log[0].Message);
        Assert.Equal("Test Writer", log[0].Author);
        Assert.Equal([new HistoryFileChange(HistoryChangeKind.Modified, Mara)], log[0].Changes);
        Assert.Equal([new HistoryFileChange(HistoryChangeKind.Added, Mara)], log[1].Changes);
        Assert.True(log[0].When >= log[1].When);

        // The whole den's history has every checkpoint.
        Assert.Equal(3, history.Log(new HistoryLogQuery(), Token).Value.Count);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-2")]
    public void TheLogPagesWithALimitAndABeforeRevisionThroughAMerge()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "one\n");
        repo.CommitAll("one");
        repo.Git("checkout", "--quiet", "-b", "side");
        repo.WriteDen(Fen, "side\n");
        var side = repo.CommitAll("side");
        repo.Git("checkout", "--quiet", "main");
        repo.WriteDen(Mara, "two\n");
        repo.CommitAll("two");
        repo.Git("merge", "--quiet", "--no-ff", "-m", "merge side", "side");
        repo.WriteDen(Mara, "three\n");
        repo.CommitAll("three");

        var history = repo.History;
        var all = history.Log(new HistoryLogQuery(), Token).Value.Select(e => e.Id).ToList();
        Assert.Contains(side, all);

        var paged = new List<string>();
        string? before = null;
        for (var pages = 0; pages < 10; pages++)
        {
            var page = history.Log(new HistoryLogQuery(Limit: 2, Before: before), Token).Value;
            Assert.True(page.Count <= 2);
            if (page.Count == 0)
            {
                break;
            }

            paged.AddRange(page.Select(e => e.Id));
            before = page[^1].Id;
        }

        // Every commit once, the merged branch's included, in the same order as the whole log.
        Assert.Equal(all, paged);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public void ACheckpointRecordsNewChangedAndDeletedNotes()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");
        repo.WriteDen(Fen, "# Fen Harbour\n");
        repo.CommitAll("start");

        repo.WriteDen(Mara, "# Mara Voss\n\nA pilot.\n");
        repo.DeleteDen(Fen);
        repo.WriteDen("universes/vael/entities/items/compass.md", "# The Compass\n");

        var entry = repo.History.Checkpoint(new CheckpointRequest("End of chapter 14 session"), Token).Value;

        Assert.NotNull(entry);
        Assert.Equal("End of chapter 14 session", entry.Message);
        Assert.Equal(
            [
                new HistoryFileChange(HistoryChangeKind.Modified, Mara),
                new HistoryFileChange(HistoryChangeKind.Added, "universes/vael/entities/items/compass.md"),
                new HistoryFileChange(HistoryChangeKind.Deleted, Fen),
            ],
            entry.Changes.OrderBy(c => c.Path, StringComparer.Ordinal));
        Assert.Equal(entry.Id, repo.Git("rev-parse", "HEAD").Trim());
        Assert.Equal("", repo.Git("status", "--porcelain"));

        // Nothing changed since, so there's nothing to record.
        Assert.Null(repo.History.Checkpoint(new CheckpointRequest("Again"), Token).Value);
        Assert.Equal(entry.Id, repo.Git("rev-parse", "HEAD").Trim());
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public void ACheckpointCanRecordOnlySomeNotes()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");
        repo.WriteDen(Mara, "# Mara Voss\n");
        repo.WriteDen(Fen, "# Fen Harbour\n");

        var entry = repo.History.Checkpoint(new CheckpointRequest("Just Mara", [Mara]), Token).Value!;

        Assert.Equal([new HistoryFileChange(HistoryChangeKind.Added, Mara)], entry.Changes);
        Assert.Equal($"?? {Fen}\n", repo.Git("status", "--porcelain", "--untracked-files=all"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public void TheFirstCheckpointOfANewRepositoryWorks()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");

        Assert.Empty(repo.History.Log(new HistoryLogQuery(), Token).Value);
        Assert.False(repo.History.Diff(new HistoryDiffQuery(), Token).IsAvailable);

        var entry = repo.History.Checkpoint(new CheckpointRequest("First"), Token).Value!;

        Assert.Equal(["den.yaml", Mara], entry.Changes.Select(c => c.Path).Order(StringComparer.Ordinal));
        Assert.Single(repo.History.Log(new HistoryLogQuery(), Token).Value);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-4")]
    public void AnAgentsCheckpointRecordsItAsACoAuthor()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");
        repo.WriteDen(Mara, "# Mara Voss\n");

        var entry = repo.History.Checkpoint(
            new CheckpointRequest("Add Mara from the chapter 3 draft", CoAuthors: ["Claude <noreply@anthropic.com>"]), Token).Value!;

        Assert.Equal("Add Mara from the chapter 3 draft\n\nCo-Authored-By: Claude <noreply@anthropic.com>", entry.Message);
        Assert.Equal("Claude <noreply@anthropic.com>\n", repo.Git("log", "-1", "--format=%(trailers:key=Co-Authored-By,valueonly)").TrimEnd('\n') + "\n");
        Assert.Equal("Test Writer\n", repo.Git("log", "-1", "--format=%an"));
    }

    [Theory]
    [Trait("Scenario", "SC-HIST-3")]
    [InlineData("")]
    [InlineData("  ")]
    public void ACheckpointNeedsAMessage(string message)
    {
        using var repo = TestRepo.Create();
        Assert.ThrowsAny<ArgumentException>(() => repo.History.Checkpoint(new CheckpointRequest(message), Token));
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("<noreply@anthropic.com>")]
    [InlineData("Claude <noreply@anthropic.com>\nSigned-off-by: Someone <x@example.com>")]
    [Trait("Scenario", "SC-HIST-4")]
    public void ACoAuthorIsANameAndAnEmailOnOneLine(string coAuthor)
    {
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");
        Assert.Throws<ArgumentException>(() => repo.History.Checkpoint(new CheckpointRequest("Add Mara", CoAuthors: [coAuthor]), Token));
        Assert.Throws<InvalidOperationException>(() => repo.Git("rev-parse", "--verify", "HEAD"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-5")]
    public void ADenInALargerRepositoryHasOnlyItsOwnHistory()
    {
        using var repo = TestRepo.Create("writing/vael-notes");
        repo.WriteRepo("README.md", "# My writing\n");
        repo.WriteDen(Mara, "# Mara Voss\n");
        var start = repo.CommitAll("start");
        repo.WriteRepo("README.md", "# My writing\n\nUpdated.\n");
        repo.CommitAll("Only the readme");

        var history = repo.History;
        var log = history.Log(new HistoryLogQuery(), Token).Value;

        // Only the commit that touched the den, with den-relative paths.
        Assert.Equal([start], log.Select(e => e.Id));
        Assert.Equal(["den.yaml", Mara], log[0].Changes.Select(c => c.Path).Order(StringComparer.Ordinal));
        Assert.Equal(Path.GetFullPath(repo.Root), ((GitHistory)history).RepositoryRoot, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-5")]
    public void ACheckpointLeavesWhatTheUserStagedOutsideTheDenAsItWas()
    {
        using var repo = TestRepo.Create("writing/vael-notes");
        repo.WriteRepo("README.md", "# My writing\n");
        repo.WriteRepo("blog/post.md", "draft\n");
        repo.CommitAll("start");

        repo.WriteRepo("README.md", "# My writing\n\nStaged by hand.\n");
        repo.Git("add", "README.md");
        repo.WriteRepo("blog/post.md", "not staged\n");
        repo.WriteDen(Mara, "# Mara Voss\n");

        var entry = repo.History.Checkpoint(new CheckpointRequest("Add Mara"), Token).Value!;

        Assert.Equal([new HistoryFileChange(HistoryChangeKind.Added, Mara)], entry.Changes);
        Assert.Equal($"writing/vael-notes/{Mara}\n", repo.Git("show", "--name-only", "--format=", "HEAD"));
        Assert.Equal("M  README.md\n M blog/post.md\n", repo.Git("status", "--porcelain"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-5")]
    public void HiddenPathsAreNeverRecordedOrReported()
    {
        using var repo = TestRepo.Create("vael-notes");
        repo.WriteDen(Mara, "# Mara Voss\n");
        repo.CommitAll("start");

        // A commit made by hand that only touches hidden files in the den isn't in its history.
        repo.WriteDen(".obsidian/workspace.json", "{}\n");
        repo.WriteDen("universes/.drafts/old.md", "old\n");
        var hiddenOnly = repo.CommitAll("Hidden only");
        Assert.DoesNotContain(hiddenOnly, repo.History.Log(new HistoryLogQuery(), Token).Value.Select(e => e.Id));

        repo.WriteDen(".obsidian/workspace.json", "{\"open\": true}\n");
        repo.WriteDen(".wolfden/index.db", "not really\n");
        repo.WriteDen("universes/vael/.mara-voss.md.swp", "swap\n");
        repo.WriteDen(Mara, "# Mara Voss\n\nA pilot.\n");

        var diff = repo.History.Diff(new HistoryDiffQuery(), Token).Value;
        Assert.Equal([Mara], diff.Files.Select(f => f.Change.Path));

        var entry = repo.History.Checkpoint(new CheckpointRequest("Mara is a pilot"), Token).Value!;
        Assert.Equal([new HistoryFileChange(HistoryChangeKind.Modified, Mara)], entry.Changes);
        Assert.Equal($"vael-notes/{Mara}\n", repo.Git("show", "--name-only", "--format=", "HEAD"));
        Assert.Contains(" M vael-notes/.obsidian/workspace.json", repo.Git("status", "--porcelain"), StringComparison.Ordinal);

        Assert.ThrowsAny<ArgumentException>(() => repo.History.Log(new HistoryLogQuery(".obsidian/workspace.json"), Token));
        Assert.ThrowsAny<ArgumentException>(() => repo.History.Checkpoint(new CheckpointRequest("Sneaky", [".wolfden"]), Token));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-6")]
    public void ADiffSinceTheLatestCheckpointShowsNewChangedAndDeletedNotesAndLeavesTheIndexAlone()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");
        repo.WriteDen(Fen, "# Fen Harbour\n");
        var start = repo.CommitAll("start");

        repo.WriteDen(Mara, "# Mara Voss\n\nA pilot.\n");
        repo.DeleteDen(Fen);
        repo.WriteDen("universes/vael/entities/items/compass.md", "# The Compass\n");
        var status = repo.Git("status", "--porcelain", "--untracked-files=all");

        var diff = repo.History.Diff(new HistoryDiffQuery(), Token).Value;

        Assert.Equal(start, diff.From);
        Assert.Null(diff.To);
        Assert.Equal(
            [
                (HistoryChangeKind.Modified, Mara),
                (HistoryChangeKind.Added, "universes/vael/entities/items/compass.md"),
                (HistoryChangeKind.Deleted, Fen),
            ],
            diff.Files.Select(f => (f.Change.Kind, f.Change.Path)).OrderBy(c => c.Path, StringComparer.Ordinal));
        var mara = diff.Files.Single(f => f.Change.Path == Mara).Patch;
        Assert.StartsWith($"diff --git a/{Mara} b/{Mara}\n", mara, StringComparison.Ordinal);
        Assert.Contains("\n+A pilot.\n", mara, StringComparison.Ordinal);
        Assert.Contains("\n+# The Compass\n", diff.Files.Single(f => f.Change.Kind == HistoryChangeKind.Added).Patch, StringComparison.Ordinal);

        // A diff only reads: the user's index and files are as they were.
        Assert.Equal(status, repo.Git("status", "--porcelain", "--untracked-files=all"));

        // One file only.
        Assert.Equal([Mara], repo.History.Diff(new HistoryDiffQuery(Path: Mara), Token).Value.Files.Select(f => f.Change.Path));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-6")]
    public void ADiffBetweenTwoCheckpointsShowsARename()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen("universes/vael/entities/characters/mara.md", "# Mara Voss\n\nA pilot who flies the night run to the outer moons.\n");
        var before = repo.CommitAll("start");
        repo.Git("mv", "universes/vael/entities/characters/mara.md", Mara);
        var after = repo.CommitAll("rename");

        var diff = repo.History.Diff(new HistoryDiffQuery(before, after), Token).Value;

        Assert.Equal((before, after), (diff.From, diff.To));
        var file = Assert.Single(diff.Files);
        Assert.Equal(new HistoryFileChange(HistoryChangeKind.Renamed, Mara, "universes/vael/entities/characters/mara.md"), file.Change);
        Assert.Contains("rename to ", file.Patch, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-2")]
    public void NamesWithSpacesAndAccentsComeBackAsTheyAre()
    {
        using var repo = TestRepo.Create();
        const string name = "universes/vael/entities/characters/Ilona Székely [old].md";
        repo.WriteDen(name, "# Ilona\n");
        var id = repo.CommitAll("Add Ilona");

        var log = repo.History.Log(new HistoryLogQuery(name), Token).Value;

        Assert.Equal(id, Assert.Single(log).Id);
        Assert.Equal([new HistoryFileChange(HistoryChangeKind.Added, name)], log[0].Changes);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-7")]
    public void ATagNamesTheLatestCheckpointAndIsNeverMoved()
    {
        using var repo = TestRepo.Create("vael-notes");
        repo.WriteDen(Mara, "# Mara Voss\n");
        var start = repo.CommitAll("start");
        repo.WriteRepo("README.md", "# outside the den\n");
        repo.CommitAll("outside");

        var history = repo.History;
        var tag = history.Tag(new TagRequest("book/vael/ember/final", Message: "Freeze Ember"), Token).Value;

        // The latest checkpoint of the den, not the repository's newest commit.
        Assert.Equal(new HistoryTag("book/vael/ember/final", start), tag);
        Assert.Equal([tag], history.Tags("book/vael/", Token).Value);
        Assert.Equal("Freeze Ember", repo.Git("tag", "-l", "--format=%(contents)", "book/vael/ember/final").TrimEnd('\n'));

        repo.WriteDen(Mara, "# Mara Voss\n\nChanged.\n");
        var later = history.Checkpoint(new CheckpointRequest("later"), Token).Value!;
        var refused = Assert.Throws<HistoryException>(() => history.Tag(new TagRequest("book/vael/ember/final", later.Id), Token));
        Assert.Contains("already has a tag 'book/vael/ember/final'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(start, repo.Git("rev-parse", "book/vael/ember/final^{commit}").Trim());

        // A lightweight tag, and the list in ASCII order.
        history.Tag(new TagRequest("a-snapshot", later.Id), Token);
        Assert.Equal(["a-snapshot", "book/vael/ember/final"], history.Tags(cancellationToken: Token).Value.Select(t => t.Name));
        Assert.Empty(history.Tags("book/orn/", Token).Value);
    }

    [Theory]
    [InlineData("-f")]
    [InlineData("two words")]
    [InlineData("book..final")]
    [Trait("Scenario", "SC-HIST-7")]
    public void ATagNameMustBeOneGitAccepts(string name)
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");
        Assert.ThrowsAny<ArgumentException>(() => repo.History.Tag(new TagRequest(name), Token));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-6")]
    public void ARevisionThatIsntOneIsAHistoryException()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");
        var history = repo.History;

        Assert.Contains("no checkpoint 'nope'", Assert.Throws<HistoryException>(() => history.Log(new HistoryLogQuery(Before: "nope"), Token)).Message, StringComparison.Ordinal);
        Assert.Throws<HistoryException>(() => history.Diff(new HistoryDiffQuery("--output=x"), Token));
        Assert.False(File.Exists(Path.Combine(repo.DenRoot, "x")));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-1")]
    public void ADenOutsideAnyRepositoryHasNoHistory()
    {
        var folder = TestRepo.NewFolder();
        try
        {
            Assert.Same(NoHistory.Instance, GitHistory.ForDen(folder, TestRepo.Options, Token));
            Assert.Equal(GitLocationKind.NotARepository, GitRepository.Locate(folder, TestRepo.Options, Token).Kind);
        }
        finally
        {
            TestRepo.DeleteFolder(folder);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-8")]
    public void WithoutGitEveryOperationSaysSoWithoutThrowing()
    {
        using var repo = TestRepo.Create();
        var options = TestRepo.Options with { Executable = "wolfden-test-no-such-git" };

        var history = GitHistory.ForDen(repo.DenRoot, options, Token);

        Assert.Equal(HistoryCapabilities.None, history.Capabilities);
        var reasons = new[]
        {
            history.Log(new HistoryLogQuery(), Token).Reason,
            history.Diff(new HistoryDiffQuery(), Token).Reason,
            history.Checkpoint(new CheckpointRequest("End of session"), Token).Reason,
            history.Tag(new TagRequest("book/vael/ember/final"), Token).Reason,
            history.Tags(null, Token).Reason,
        };
        Assert.All(reasons, r => Assert.StartsWith("Git isn't installed, or Wolfden can't find it on the PATH, so ", r, StringComparison.Ordinal));
        Assert.Contains("Your edits are still saved in the files.", reasons[2], StringComparison.Ordinal);
        Assert.Equal(GitLocationKind.GitNotInstalled, GitRepository.Locate(repo.DenRoot, options, Token).Kind);
    }

    [Fact]
    public void ACancelledCallStopsGit()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => repo.History.Log(new HistoryLogQuery(), cancelled.Token));
    }
}
