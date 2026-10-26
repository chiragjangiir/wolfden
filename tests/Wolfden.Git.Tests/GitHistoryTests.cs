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
    [Trait("Scenario", "SC-HIST-2")]
    public void APageThatEndsOnAMergedBranchsCommitLosesNoOlderMainLineCommit()
    {
        // A (main), then B (main) and C (side, from A, newer than B), then the
        // merge: the log is merge, C, B, A, and B isn't an ancestor of C.
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "a\n");
        var a = repo.CommitAt("A", 1);
        repo.Git("checkout", "--quiet", "-b", "side");
        repo.WriteDen(Fen, "c\n");
        var c = repo.CommitAt("C", 3);
        repo.Git("checkout", "--quiet", "main");
        repo.WriteDen(Mara, "b\n");
        var b = repo.CommitAt("B", 2);
        repo.Git("merge", "--quiet", "--no-ff", "-m", "merge side", "side");
        var merge = repo.Git("rev-parse", "HEAD").Trim();

        var history = repo.History;
        Assert.Equal([merge, c, b, a], history.Log(new HistoryLogQuery(), Token).Value.Select(e => e.Id));

        var first = history.Log(new HistoryLogQuery(Limit: 2), Token).Value;
        Assert.Equal([merge, c], first.Select(e => e.Id));
        var second = history.Log(new HistoryLogQuery(Limit: 2, Before: c), Token).Value;
        Assert.Equal([b, a], second.Select(e => e.Id));

        for (var limit = 1; limit <= 4; limit++)
        {
            Assert.Equal([merge, c, b, a], Pages(history, limit));
        }

        // A revision that isn't an entry of this log isn't a cursor for it.
        Assert.Throws<HistoryRevisionException>(() => history.Log(new HistoryLogQuery(Mara, Limit: 1, Before: c), Token));
    }

    private static List<string> Pages(IHistoryProvider history, int limit)
    {
        var ids = new List<string>();
        string? before = null;
        for (var pages = 0; pages < 10; pages++)
        {
            var page = history.Log(new HistoryLogQuery(Limit: limit, Before: before), Token).Value;
            if (page.Count == 0)
            {
                break;
            }

            ids.AddRange(page.Select(e => e.Id));
            before = page[^1].Id;
        }

        return ids;
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public void ACheckpointAHookRefusesLeavesTheNewFilesUntracked()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");
        var hook = Path.Combine(repo.Root, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\necho 'not today' >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        repo.WriteDen(Mara, "# Mara Voss\n");
        var status = repo.Git("status", "--porcelain", "--untracked-files=all");

        var refused = Assert.Throws<HistoryException>(() => repo.History.Checkpoint(new CheckpointRequest("Add Mara"), Token));

        Assert.StartsWith("Git refused the checkpoint, so nothing was recorded (", refused.Message, StringComparison.Ordinal);
        Assert.Contains("not today", refused.Message, StringComparison.Ordinal);
        Assert.Equal($"?? {Mara}\n", status);
        Assert.Equal(status, repo.Git("status", "--porcelain", "--untracked-files=all"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public void ACheckpointMessageCantHoldControlCharacters()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");
        Assert.Throws<ArgumentException>(() => repo.History.Checkpoint(new CheckpointRequest("Add\u0001 Mara"), Token));
        Assert.NotNull(repo.History.Checkpoint(new CheckpointRequest("Add Mara\r\n\n\tand more"), Token).Value);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-7")]
    public void ALightweightTagWorksWhenGitSignsTags()
    {
        using var repo = TestRepo.Create();
        var start = repo.CommitAll("start");
        repo.Git("config", "tag.gpgSign", "true");

        Assert.Equal(new HistoryTag("a-snapshot", start), repo.History.Tag(new TagRequest("a-snapshot"), Token).Value);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-1")]
    public void AGitAboveACeilingDirectoryDoesntCount()
    {
        // A broken .git that git doesn't look at, because of GIT_CEILING_DIRECTORIES.
        var outer = TestRepo.NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(outer, ".git"), "gitdir: nowhere\n");
            var den = Path.Combine(outer, "den");
            Directory.CreateDirectory(den);
            var env = new Dictionary<string, string?>(TestRepo.Options.Environment) { ["GIT_CEILING_DIRECTORIES"] = outer };
            var options = TestRepo.Options with { Environment = env };

            Assert.Equal(GitLocationKind.NotARepository, GitRepository.Locate(den, options, Token).Kind);
            Assert.Equal(GitLocationKind.Unusable, GitRepository.Locate(outer, options, Token).Kind);
        }
        finally
        {
            TestRepo.DeleteFolder(outer);
        }
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
    [Trait("Scenario", "SC-HIST-6")]
    public void ADiffWithTheFilesOnDiskWritesNothingIntoTheRepository()
    {
        // A den in a subfolder, and every kind of change a writer can leave
        // before a checkpoint. (The user's diff.autoRefreshIndex is off, which
        // mustn't matter; here it can't, since Wolfden refreshes its
        // temporary index before comparing.)
        using var repo = TestRepo.Create("writing/vael-notes");
        const string old = "universes/vael/entities/characters/mara.md";
        const string accented = "universes/vael/entities/characters/Ilona Székely.md";
        const string staged = "universes/vael/entities/items/compass.md";
        repo.WriteDen(old, "# Mara Voss\n\nA pilot who flies the night run to the outer moons.\n");
        repo.WriteDen(Fen, "# Fen Harbour\n");
        repo.WriteDen("universes/vael/unchanged.md", "# Still the same\n");
        repo.WriteRepo("README.md", "# outside the den\n");
        var start = repo.CommitAll("start");
        repo.Git("config", "diff.autoRefreshIndex", "false");

        File.Move(Path.Combine(repo.DenRoot, old), Path.Combine(repo.DenRoot, Mara));
        repo.DeleteDen(Fen);
        repo.WriteDen(accented, "# Ilona\n");
        repo.WriteDen("universes/vael/empty.md", string.Empty);
        File.WriteAllBytes(Path.Combine(repo.DenRoot, "universes", "vael", "map.png"), [0x89, 0x50, 0x4e, 0x47, 0x00, 0x01, 0x02, 0x00]);
        repo.WriteDen(staged, "# The Compass\n");
        repo.Git("add", $"writing/vael-notes/{staged}");
        repo.WriteDen(".wolfden/index.db", "not really\n");
        repo.WriteRepo("README.md", "# outside the den, changed\n");
        repo.WriteRepo("blog/post.md", "outside the den, new\n");
        var objects = Objects(repo);
        var status = repo.Git("status", "--porcelain", "--untracked-files=all");

        var diff = repo.History.Diff(new HistoryDiffQuery(), Token).Value;

        Assert.Equal(start, diff.From);
        Assert.Equal(
            [
                new HistoryFileChange(HistoryChangeKind.Added, "universes/vael/empty.md"),
                new HistoryFileChange(HistoryChangeKind.Added, accented),
                new HistoryFileChange(HistoryChangeKind.Renamed, Mara, old),
                new HistoryFileChange(HistoryChangeKind.Added, staged),
                new HistoryFileChange(HistoryChangeKind.Deleted, Fen),
                new HistoryFileChange(HistoryChangeKind.Added, "universes/vael/map.png"),
            ],
            diff.Files.Select(f => f.Change).OrderBy(c => c.Path, StringComparer.Ordinal));
        Assert.Contains("Binary files", diff.Files.Single(f => f.Change.Path.EndsWith(".png", StringComparison.Ordinal)).Patch, StringComparison.Ordinal);
        Assert.Contains("\n+# Ilona\n", diff.Files.Single(f => f.Change.Path == accented).Patch, StringComparison.Ordinal);
        Assert.Contains("\n+# The Compass\n", diff.Files.Single(f => f.Change.Path == staged).Patch, StringComparison.Ordinal);

        // One file only, a new one and a deleted one.
        Assert.Equal([accented], repo.History.Diff(new HistoryDiffQuery(Path: accented), Token).Value.Files.Select(f => f.Change.Path));
        Assert.Equal([Fen], repo.History.Diff(new HistoryDiffQuery(Path: Fen), Token).Value.Files.Select(f => f.Change.Path));
        Assert.Empty(repo.History.Diff(new HistoryDiffQuery(Path: "universes/vael/never.md"), Token).Value.Files);

        // Nothing was stored: no loose object and no pack, not even the empty file's.
        Assert.Equal(objects, Objects(repo));
        Assert.Equal(status, repo.Git("status", "--porcelain", "--untracked-files=all"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-6")]
    public void ADiffOnABranchWithNoCommitsYetComparesTheFilesOnDisk()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");
        repo.WriteDen(Fen, "# Fen Harbour\n");
        var start = repo.CommitAll("start");
        repo.Git("checkout", "--quiet", "--orphan", "fresh");
        repo.Git("rm", "--quiet", "-r", "--cached", ".");
        repo.WriteDen(Mara, "# Mara Voss\n\nA pilot.\n");
        var objects = Objects(repo);

        var diff = repo.History.Diff(new HistoryDiffQuery(start), Token).Value;

        Assert.Equal([new HistoryFileChange(HistoryChangeKind.Modified, Mara)], diff.Files.Select(f => f.Change));
        Assert.Equal(objects, Objects(repo));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-6")]
    public void ADiffWithTheFilesOnDiskReadsNewFilesWhenGitIgnoresStatData()
    {
        // With core.ignoreStat, git would mark the new files' entries as
        // unchanged and take them as the empty blob, which isn't stored.
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");
        repo.CommitAll("start");
        repo.Git("config", "core.ignoreStat", "true");
        repo.Git("config", "core.splitIndex", "true");
        repo.WriteDen(Mara, "# Mara Voss\n\nA pilot.\n");
        repo.WriteDen(Fen, "# Fen Harbour\n");
        var files = Directory.EnumerateFiles(Path.Combine(repo.Root, ".git")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

        var diff = repo.History.Diff(new HistoryDiffQuery(), Token).Value;

        Assert.Equal([(HistoryChangeKind.Modified, Mara), (HistoryChangeKind.Added, Fen)], diff.Files.Select(f => (f.Change.Kind, f.Change.Path)));
        Assert.Contains("\n+# Fen Harbour\n", diff.Files.Single(f => f.Change.Path == Fen).Patch, StringComparison.Ordinal);

        // And no shared index (core.splitIndex) was left in the user's .git folder.
        Assert.Equal(files, Directory.EnumerateFiles(Path.Combine(repo.Root, ".git")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-6")]
    public void ADiffWithTheFilesOnDiskTakesTheListAndThePatchesFromOneLook()
    {
        // A clean filter that gives a file's committed text on its second
        // run and a changed text on every other: Wolfden's refresh runs it
        // first (changed), then a diff decides with the second (unchanged)
        // and refreshes with the third (changed). A raw diff and a patch run
        // apart would disagree, the way they would if the writer saved in
        // between: no file listed, then one patch. One run can't.
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");
        repo.CommitAll("start");
        repo.Git(
            "config",
            "filter.wolfden-flip.clean",
            "n=$(cat .git/wolfden-flip 2>/dev/null || echo 0); n=$((n+1)); echo $n > .git/wolfden-flip; cat; [ $n = 2 ] || echo changed");
        repo.WriteRepo(".gitattributes", "*.md filter=wolfden-flip\n");

        var diff = repo.History.Diff(new HistoryDiffQuery(), Token).Value;

        Assert.All(diff.Files, f => Assert.StartsWith($"diff --git a/{f.Change.Path} b/{f.Change.Path}\n", f.Patch, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-6")]
    public void PatchesPairWithNamesGitQuotes()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows doesn't allow tabs, quotes, or backslashes in names.");
        }

        using var repo = TestRepo.Create();
        repo.WriteDen("universes/a.md", "a\n");
        var start = repo.CommitAll("start");
        string[] names = ["universes/tab\there.md", "universes/say \"hi\".md", "universes/back\\slash.md", "universes/a b.md"];
        foreach (var name in names)
        {
            repo.WriteDen(name, $"{name}\n");
        }

        var diff = repo.History.Diff(new HistoryDiffQuery(start), Token).Value;

        Assert.Equal(names.Order(StringComparer.Ordinal), diff.Files.Select(f => f.Change.Path).Order(StringComparer.Ordinal));
        Assert.All(diff.Files, f => Assert.Contains($"+{f.Change.Path}\n", f.Patch, StringComparison.Ordinal));
        Assert.Equal("\"a/universes/tab\\there.md\"", GitHistory.QuotePath("a/universes/tab\there.md"));
        Assert.Equal("a/universes/a b é.md", GitHistory.QuotePath("a/universes/a b é.md"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-6")]
    public void ADiffInASha256RepositoryWritesNothing()
    {
        using var repo = TestRepo.Create(objectFormat: "sha256");
        repo.WriteDen(Mara, "# Mara Voss\n");
        var start = repo.CommitAll("start");
        Assert.Equal(64, start.Length);
        repo.WriteDen(Mara, "# Mara Voss\n\nA pilot.\n");
        repo.WriteDen(Fen, "# Fen Harbour\n");
        repo.WriteDen("universes/empty.md", string.Empty);
        var objects = Objects(repo);

        var diff = repo.History.Diff(new HistoryDiffQuery(), Token).Value;

        Assert.Equal(
            [(HistoryChangeKind.Added, "universes/empty.md"), (HistoryChangeKind.Modified, Mara), (HistoryChangeKind.Added, Fen)],
            diff.Files.Select(f => (f.Change.Kind, f.Change.Path)).OrderBy(c => c.Path, StringComparer.Ordinal));
        Assert.Equal(objects, Objects(repo));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-6")]
    public void ADiffLeavesOutWhatASparseCheckoutKeepsOffTheDisk()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");
        repo.WriteDen(Fen, "# Fen Harbour\n");
        var start = repo.CommitAll("start");
        repo.Git("sparse-checkout", "set", "universes/vael/entities/characters");
        Assert.False(File.Exists(Path.Combine(repo.DenRoot, Fen.Replace('/', Path.DirectorySeparatorChar))));
        repo.WriteDen(Mara, "# Mara Voss\n\nA pilot.\n");

        var diff = repo.History.Diff(new HistoryDiffQuery(start), Token).Value;

        Assert.Equal([new HistoryFileChange(HistoryChangeKind.Modified, Mara)], diff.Files.Select(f => f.Change));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-6")]
    public void EditsInsideASubmoduleArentTheDens()
    {
        using var other = TestRepo.Create();
        other.WriteRepo("map.md", "# Map\n");
        other.CommitAll("map");
        using var repo = TestRepo.Create();
        repo.WriteDen(Mara, "# Mara Voss\n");
        repo.Git("-c", "protocol.file.allow=always", "submodule", "--quiet", "add", other.Root, "universes/maps");
        var start = repo.CommitAll("start");
        File.AppendAllText(Path.Combine(repo.DenRoot, "universes", "maps", "map.md"), "Edited.\n");
        repo.WriteDen(Mara, "# Mara Voss\n\nA pilot.\n");

        var diff = repo.History.Diff(new HistoryDiffQuery(start), Token).Value;

        Assert.Equal([new HistoryFileChange(HistoryChangeKind.Modified, Mara)], diff.Files.Select(f => f.Change));
    }

    /// <summary>Every file in the repository's object store, loose objects and packs alike.</summary>
    private static List<string> Objects(TestRepo repo)
    {
        var store = Path.Combine(repo.Root, ".git", "objects");
        return Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(store, file))
            .Order(StringComparer.Ordinal)
            .ToList();
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
    [InlineData("HEAD")]
    [InlineData("@")]
    [InlineData("refs/heads/main")]
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

        var nope = Assert.Throws<HistoryRevisionException>(() => history.Log(new HistoryLogQuery(Before: "nope"), Token));
        Assert.Contains("no checkpoint 'nope'", nope.Message, StringComparison.Ordinal);
        Assert.Equal("nope", nope.Revision);
        Assert.Throws<HistoryRevisionException>(() => history.Diff(new HistoryDiffQuery("--output=x"), Token));
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
