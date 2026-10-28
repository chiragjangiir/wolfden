using System.Text;
using Wolfden.Core.Documents;
using Wolfden.Core.History;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>
/// Automatic checkpoints (#30): the <c>auto_checkpoint</c> setting
/// (format.md F-DEN-9), and the poll that makes a checkpoint once the den
/// has been still long enough, driven by a clock of the test's own, never
/// the wall clock.
/// </summary>
public sealed class AutoCheckpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wolfden-core-tests", "auto-" + Guid.NewGuid().ToString("N"));

    public AutoCheckpointTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    // ------------------------------------------------------------ the setting

    [Theory]
    [Trait("Scenario", "SC-WEB-53")]
    [InlineData("auto_checkpoint: 30m", 30, null)]
    [InlineData("auto_checkpoint: 2h", 120, null)]
    [InlineData("auto_checkpoint: 1h30m", 90, null)]
    [InlineData("auto_checkpoint: 5m", 5, null)]
    [InlineData("auto_checkpoint: 24h", 1440, null)]
    [InlineData("auto_checkpoint: \"45m\"", 45, null)]
    [InlineData("auto_checkpoint:", null, null)]
    [InlineData("auto_checkpoint: null", null, null)]
    [InlineData("auto_checkpoint: false", null, null)]
    [InlineData("", null, null)]
    [InlineData("auto_checkpoint: 4m", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 25h", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 0m", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 30", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 30s", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 1d", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 30M", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 30m2h", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 30 m", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: -30m", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: true", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: [30m]", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: { every: 30m }", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 999999999h", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 999999999h999999999m", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 999999999m", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 9999999999m", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 1h60m", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 1h59m", 119, null)]
    [InlineData("auto_checkpoint: 90m", 90, null)]
    [InlineData("auto_checkpoint: |\n  30m", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: \"false\"", null, "F-DEN-9")]
    [InlineData("auto_checkpoint: 'null'", null, "F-DEN-9")]
    public void AutoCheckpointIsATimeFromFiveMinutesToADay(string line, int? minutes, string? warning)
    {
        var text = $"format: 1\n{line}\n";
        var den = DenStructure.Load(MemoryDenSource.Den().With("den.yaml", text));

        Assert.True(den.IsLoaded);
        Assert.Equal(minutes is { } m ? TimeSpan.FromMinutes(m) : null, den.AutoCheckpoint);
        var diagnostics = den.Diagnostics.Select(d => (d.Path, d.Diagnostic.RuleId, d.Diagnostic.Severity)).ToArray();
        Assert.Equal(warning is null ? [] : [("den.yaml", warning, DiagnosticSeverity.Warning)], diagnostics);
        if (warning is not null)
        {
            Assert.Contains("automatic checkpoints stay off", den.Diagnostics.Single().Diagnostic.Message, StringComparison.Ordinal);
        }

        // wolfden serve reads the setting straight from the file, the same way.
        var read = AutoCheckpoint.Read(Encoding.UTF8.GetBytes(text));
        Assert.Equal(den.AutoCheckpoint, read.Every);
        Assert.Equal(warning is not null, read.Problem is not null);
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-53")]
    [InlineData("format: 2\nauto_checkpoint: 30m\n")]
    [InlineData("format: 1\nauto_checkpoint: [30m\n")]
    [InlineData("auto_checkpoint: 30m\n")]
    public void AFileThatDoesntLoadLeavesItOff(string text) =>
        Assert.Equal(AutoCheckpointSetting.Off, AutoCheckpoint.Read(Encoding.UTF8.GetBytes(text)));

    [Theory]
    [InlineData(5, "5m")]
    [InlineData(60, "1h")]
    [InlineData(90, "1h30m")]
    [InlineData(1440, "24h")]
    public void ATimeIsSaidAsTheSettingWritesIt(int minutes, string text)
    {
        Assert.Equal(text, AutoCheckpoint.Format(TimeSpan.FromMinutes(minutes)));
        Assert.Equal(TimeSpan.FromMinutes(minutes), AutoCheckpoint.Parse(text));
    }

    // ------------------------------------------------------------ the message

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void TheMessageSaysHowManyNotesChangedAndLists()
    {
        Assert.Equal(
            "Auto-checkpoint: edited 1 note\n\nedited universes/u/a.md",
            AutoCheckpointer.Message([new HistoryFileChange(HistoryChangeKind.Modified, "universes/u/a.md")]));

        Assert.Equal(
            "Auto-checkpoint: changed 4 notes (2 edited, 1 added, 1 renamed)\n\n" +
            "edited universes/u/a.md\nedited universes/u/b.md\nadded universes/u/c.md\nrenamed universes/u/old.md -> universes/u/d.md",
            AutoCheckpointer.Message(
            [
                new HistoryFileChange(HistoryChangeKind.Modified, "universes/u/b.md"),
                new HistoryFileChange(HistoryChangeKind.Modified, "universes/u/a.md"),
                new HistoryFileChange(HistoryChangeKind.Added, "universes/u/c.md"),
                new HistoryFileChange(HistoryChangeKind.Renamed, "universes/u/d.md", "universes/u/old.md"),
            ]));

        var many = Enumerable.Range(0, 25).Select(i => new HistoryFileChange(HistoryChangeKind.Deleted, $"n{i:00}.md")).ToList();
        var message = AutoCheckpointer.Message(many);
        Assert.StartsWith("Auto-checkpoint: changed 25 notes (25 deleted)\n\n", message, StringComparison.Ordinal);
        Assert.Equal(AutoCheckpointer.MostListed + 3, message.Split('\n').Length);
        Assert.EndsWith("\nand 5 more", message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the poll

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void AQuietSpellMakesOneCheckpoint()
    {
        var (auto, clock, history, _) = Make(TimeSpan.FromMinutes(30));
        history.Pending = [new HistoryFileChange(HistoryChangeKind.Modified, "universes/u/a.md")];

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(AutoCheckpointOutcome.Waiting, auto.Tick());
        Assert.Empty(history.Made);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AutoCheckpointOutcome.Recorded, auto.Tick());
        Assert.Equal("Auto-checkpoint: edited 1 note\n\nedited universes/u/a.md", Assert.Single(history.Made).Message);
        Assert.Null(history.Made[0].CoAuthors);

        // Once per quiet spell, not once per poll.
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(AutoCheckpointOutcome.Settled, auto.Tick());
        Assert.Single(history.Made);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void ANewChangeStartsTheWaitAgain()
    {
        var (auto, clock, history, files) = Make(TimeSpan.FromMinutes(30));
        history.Pending = [new HistoryFileChange(HistoryChangeKind.Modified, "a.md")];

        clock.Advance(TimeSpan.FromMinutes(20));
        files.Print = "edited";
        Assert.Equal(AutoCheckpointOutcome.Waiting, auto.Tick());

        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(AutoCheckpointOutcome.Waiting, auto.Tick());
        Assert.Empty(history.Made);

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(AutoCheckpointOutcome.Recorded, auto.Tick());

        // After a checkpoint, the next change starts a new spell, with its own checkpoint.
        files.Print = "edited again";
        Assert.Equal(AutoCheckpointOutcome.Waiting, auto.Tick());
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoCheckpointOutcome.Recorded, auto.Tick());
        Assert.Equal(2, history.Made.Count);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void NothingToRecordDoesNothing()
    {
        var (auto, clock, history, _) = Make(TimeSpan.FromMinutes(30));

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoCheckpointOutcome.NothingToRecord, auto.Tick());
        Assert.Empty(history.Made);
        Assert.Equal(0, history.Checkpoints);

        // Another process checkpointed between the look and the commit: still nothing made.
        var (other, otherClock, raced, _) = Make(TimeSpan.FromMinutes(30));
        raced.Pending = [new HistoryFileChange(HistoryChangeKind.Modified, "a.md")];
        raced.RecordNothing = true;
        otherClock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoCheckpointOutcome.NothingToRecord, other.Tick());
        otherClock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoCheckpointOutcome.Settled, other.Tick());
        Assert.Equal(1, raced.Checkpoints);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void AGitWriteUnderWayIsWaitedFor()
    {
        var (auto, clock, history, _) = Make(TimeSpan.FromMinutes(30));
        history.Pending = [new HistoryFileChange(HistoryChangeKind.Modified, "a.md")];
        history.Writing = true;

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoCheckpointOutcome.Busy, auto.Tick());
        Assert.Equal(0, history.Checkpoints);

        // Tried again on the next poll, once git is done.
        clock.Advance(AutoCheckpointer.PollInterval);
        history.Writing = false;
        Assert.Equal(AutoCheckpointOutcome.Recorded, auto.Tick());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void ARefusedCheckpointIsTriedAgainAfterAnotherQuietSpell()
    {
        var (auto, clock, history, _) = Make(TimeSpan.FromMinutes(30), out var log);
        history.Pending = [new HistoryFileChange(HistoryChangeKind.Modified, "a.md")];
        history.Refuse = "Git refused the checkpoint, so nothing was recorded (fatal: Unable to create '.git/index.lock': File exists).";

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoCheckpointOutcome.Failed, auto.Tick());
        Assert.Contains(log, l => l.Contains("index.lock", StringComparison.Ordinal) && l.Contains("try again after 30m", StringComparison.Ordinal));

        // Not on every poll: after another quiet spell.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(AutoCheckpointOutcome.Waiting, auto.Tick());
        history.Refuse = null;
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(AutoCheckpointOutcome.Recorded, auto.Tick());
        Assert.Equal(2, history.Checkpoints);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void WithoutTheSettingOrWithABadOneNothingIsMade()
    {
        var (auto, clock, history, files) = Make(null, out var log);
        history.Pending = [new HistoryFileChange(HistoryChangeKind.Modified, "a.md")];
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(AutoCheckpointOutcome.Off, auto.Tick());

        files.Setting = new AutoCheckpointSetting(null, "auto_checkpoint must be a time such as 30m, 2h, or 1h30m, not 'soon', so automatic checkpoints stay off.", 2);
        Assert.Equal(AutoCheckpointOutcome.Off, auto.Tick());
        Assert.Equal(AutoCheckpointOutcome.Off, auto.Tick());
        Assert.Equal(0, history.Checkpoints);

        // Said once, not on every poll.
        Assert.Single(log, l => l.Contains("'soon'", StringComparison.Ordinal) && l.Contains("F-DEN-9", StringComparison.Ordinal));

        // Turned on while running: no restart needed.
        files.Setting = new AutoCheckpointSetting(TimeSpan.FromMinutes(5), null, null);
        Assert.Equal(AutoCheckpointOutcome.Recorded, auto.Tick());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void NoAutomaticCheckpointWhileGitIsInTheMiddleOfSomething()
    {
        var (auto, clock, history, _) = Make(TimeSpan.FromMinutes(30), out var log);
        history.Pending = [new HistoryFileChange(HistoryChangeKind.Modified, "a.md")];
        history.Interrupted = "a rebase";

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoCheckpointOutcome.Interrupted, auto.Tick());
        clock.Advance(AutoCheckpointer.PollInterval);
        Assert.Equal(AutoCheckpointOutcome.Interrupted, auto.Tick());
        Assert.Equal(0, history.Checkpoints);
        Assert.Single(log, l => l.Contains("in the middle of a rebase", StringComparison.Ordinal));

        // Once git has finished, the next poll makes it.
        history.Interrupted = null;
        clock.Advance(AutoCheckpointer.PollInterval);
        Assert.Equal(AutoCheckpointOutcome.Recorded, auto.Tick());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void GitBusyForLongIsSaidOnce()
    {
        var (auto, clock, history, _) = Make(TimeSpan.FromMinutes(30), out var log);
        history.Pending = [new HistoryFileChange(HistoryChangeKind.Modified, "a.md")];
        history.Writing = true;
        clock.Advance(TimeSpan.FromMinutes(30));
        for (var i = 0; i < AutoCheckpointer.BusyPollsBeforeSaying * 3; i++)
        {
            Assert.Equal(AutoCheckpointOutcome.Busy, auto.Tick());
            clock.Advance(AutoCheckpointer.PollInterval);
        }

        Assert.Single(log, l => l.Contains("index.lock", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void ARefusalIsSaidOnceUntilTheFilesChange()
    {
        var (auto, clock, history, files) = Make(TimeSpan.FromMinutes(30), out var log);
        history.Pending = [new HistoryFileChange(HistoryChangeKind.Modified, "a.md")];
        history.Refuse = "Git refused the checkpoint (a hook).";
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(30));
            Assert.Equal(AutoCheckpointOutcome.Failed, auto.Tick());
        }

        Assert.Single(log, l => l.Contains("a hook", StringComparison.Ordinal));

        files.Print = "edited";
        auto.Tick();
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoCheckpointOutcome.Failed, auto.Tick());
        Assert.Equal(2, log.Count(l => l.Contains("a hook", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public async Task ASaveDuringACheckpointDoesntWaitAndStaysTheHosts()
    {
        File.WriteAllText(Path.Combine(_root, "a.md"), "one\n");
        File.WriteAllText(Path.Combine(_root, "b.md"), "one\n");
        var ledger = new CheckpointLedger(new FileSystemDenStore(_root));
        ledger.Wrote("a.md", Encoding.UTF8.GetBytes("one\n"));
        using var hold = new ManualResetEventSlim();
        var history = new FakeHistory
        {
            Pending = [new HistoryFileChange(HistoryChangeKind.Modified, "a.md"), new HistoryFileChange(HistoryChangeKind.Modified, "b.md")],
            Hold = hold,
        };

        // The checkpoint waits, as on a passphrase prompt.
        var checkpoint = Task.Run(() => ledger.Checkpoint(history, new CheckpointRequest("Notes pass after drafting ch 12")), TestContext.Current.CancellationToken);
        Assert.True(history.Committing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // A save, and a status, meanwhile: neither waits for git.
        var save = Task.Run(
            () =>
            {
                File.WriteAllText(Path.Combine(_root, "b.md"), "two\n");
                ledger.Wrote("b.md", Encoding.UTF8.GetBytes("two\n"));
                return ledger.IsOwn(new HistoryFileChange(HistoryChangeKind.Modified, "b.md"));
            },
            TestContext.Current.CancellationToken);
        Assert.True(await save.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "A save waited on a running checkpoint.");


        hold.Set();
        var done = await checkpoint;
        Assert.Equal(["a.md"], done.Written);

        // a.md was the host's before the checkpoint and is recorded: forgotten. b.md was
        // saved during it, so it may not be in it: still the host's.
        Assert.False(ledger.IsOwn(new HistoryFileChange(HistoryChangeKind.Modified, "a.md")));
        Assert.True(ledger.IsOwn(new HistoryFileChange(HistoryChangeKind.Modified, "b.md")));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public void WithoutHistoryTheSettingOnlyWarns()
    {
        var clock = new ManualClock();
        var log = new List<string>();
        var ledger = new CheckpointLedger(new FileSystemDenStore(_root));
        var auto = new AutoCheckpointer(_root, () => NoHistory.Instance, ledger, clock, log.Add)
        {
            Fingerprint = () => "still",
            Setting = () => new AutoCheckpointSetting(TimeSpan.FromMinutes(30), null, null),
        };
        auto.Start();
        Assert.Single(log, l => l.Contains("no history", StringComparison.Ordinal) && l.Contains("wolfden init --git", StringComparison.Ordinal));

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoCheckpointOutcome.NoHistory, auto.Tick());
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoCheckpointOutcome.NoHistory, auto.Tick());
        Assert.Single(log);
        auto.Dispose();
    }

    [Fact]
    public void TheFingerprintChangesWithTheFilesButNotWithHiddenOnes()
    {
        File.WriteAllText(Path.Combine(_root, "den.yaml"), "format: 1\n");
        Directory.CreateDirectory(Path.Combine(_root, "universes", "u"));
        File.WriteAllText(Path.Combine(_root, "universes", "u", "universe.md"), "---\nname: U\n---\n");
        var first = AutoCheckpointer.FilesFingerprint(_root);
        Assert.Equal(first, AutoCheckpointer.FilesFingerprint(_root));

        // The index, git, and an editor's swap files are hidden: not changes to the den.
        Directory.CreateDirectory(Path.Combine(_root, ".wolfden"));
        File.WriteAllText(Path.Combine(_root, ".wolfden", "index.db"), "x");
        File.WriteAllText(Path.Combine(_root, "universes", "u", ".universe.md.swp"), "x");
        Assert.Equal(first, AutoCheckpointer.FilesFingerprint(_root));

        File.WriteAllText(Path.Combine(_root, "universes", "u", "universe.md"), "---\nname: U2\n---\n");
        var edited = AutoCheckpointer.FilesFingerprint(_root);
        Assert.NotEqual(first, edited);

        File.Delete(Path.Combine(_root, "den.yaml"));
        Assert.NotEqual(edited, AutoCheckpointer.FilesFingerprint(_root));
    }

    // ------------------------------------------------------------ helpers

    private (AutoCheckpointer Auto, ManualClock Clock, FakeHistory History, Files Files) Make(TimeSpan? every) => Make(every, out _);

    private (AutoCheckpointer Auto, ManualClock Clock, FakeHistory History, Files Files) Make(TimeSpan? every, out List<string> log)
    {
        var clock = new ManualClock();
        var history = new FakeHistory();
        var files = new Files { Setting = new AutoCheckpointSetting(every, null, null) };
        var lines = new List<string>();
        log = lines;
        var auto = new AutoCheckpointer(_root, () => history, new CheckpointLedger(new FileSystemDenStore(_root)), clock, lines.Add)
        {
            Fingerprint = () => files.Print,
            Setting = () => files.Setting,
        };
        auto.Start();
        auto.Dispose(); // The test polls by hand.
        return (auto, clock, history, files);
    }

    internal sealed class Files
    {
        public string Print { get; set; } = "start";

        public AutoCheckpointSetting Setting { get; set; } = AutoCheckpointSetting.Off;
    }

    /// <summary>A clock the test moves by hand.</summary>
    internal sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A history that records what it was asked to checkpoint.</summary>
    internal sealed class FakeHistory : IHistoryProvider
    {
        public IReadOnlyList<HistoryFileChange> Pending { get; set; } = [];

        public bool Writing { get; set; }

        public bool RecordNothing { get; set; }

        public string? Refuse { get; set; }

        public int Checkpoints { get; private set; }

        public List<CheckpointRequest> Made { get; } = [];

        public string Name => "fake";

        public HistoryCapabilities Capabilities => HistoryCapabilities.All;

        public bool IsWriting => Writing;

        public string? Interrupted { get; set; }

        public string? InterruptedOperation => Interrupted;

        /// <summary>When set, a checkpoint waits for it, as git waits on a passphrase prompt.</summary>
        public ManualResetEventSlim? Hold { get; set; }

        public ManualResetEventSlim Committing { get; } = new();

        HistoryResult<IReadOnlyList<HistoryFileChange>> IHistoryProvider.Pending(CancellationToken cancellationToken) =>
            HistoryResult<IReadOnlyList<HistoryFileChange>>.Done(Pending);

        public HistoryResult<HistoryEntry?> Checkpoint(CheckpointRequest request, CancellationToken cancellationToken = default)
        {
            Checkpoints++;
            Committing.Set();
            Hold?.Wait(TimeSpan.FromSeconds(30));
            if (Refuse is { } why)
            {
                throw new HistoryException(why);
            }

            if (RecordNothing)
            {
                return HistoryResult<HistoryEntry?>.Done(null);
            }

            Made.Add(request);
            return HistoryResult<HistoryEntry?>.Done(new HistoryEntry($"c{Made.Count:D39}", DateTimeOffset.UnixEpoch, "Test Writer", request.Message, Pending));
        }

        public HistoryResult<IReadOnlyList<HistoryEntry>> Log(HistoryLogQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public HistoryResult<HistoryDiff> Diff(HistoryDiffQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public HistoryResult<HistoryTag> Tag(TagRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public HistoryResult<IReadOnlyList<HistoryTag>> Tags(string? prefix = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public HistoryResult<bool> HoldsAtLatest(string path, byte[] content, CancellationToken cancellationToken = default) => HistoryResult<bool>.Done(false);
    }
}
