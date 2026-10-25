using System.Diagnostics;
using Wolfden.Core.History;

namespace Wolfden.Git.Tests;

/// <summary>
/// Time limits and cancelling (#143, #145): a git that only reads is
/// stopped, but one that writes the user's index or refs is never stopped
/// part way, which would leave <c>.git/index.lock</c> behind. Hooks and
/// filters here wait for a file the test writes, never a fixed time, and
/// give up on their own after a minute.
/// </summary>
public sealed class GitTimeoutTests
{
    private const string Mara = "universes/vael/entities/characters/mara-voss.md";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A shell script that says it has started, waits until it's released, then exits with <paramref name="exitCode"/>.</summary>
    private static string Waiting(string name, int exitCode) =>
        $"""
        #!/bin/sh
        : > .git/wolfden-{name}-started
        i=0
        while [ ! -f .git/wolfden-{name}-release ] && [ $i -lt 600 ]; do sleep 0.1; i=$((i+1)); done
        : > .git/wolfden-{name}-done
        exit {exitCode}

        """;

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public async Task ACheckpointWhoseHookHangsLeavesGitRunningAndPutsTheIndexBack()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");
        var head = repo.Git("rev-parse", "HEAD");
        repo.WriteHook("pre-commit", Waiting("hook", exitCode: 1));
        repo.WriteDen(Mara, "# Mara Voss\n");
        var history = GitHistory.ForDen(repo.DenRoot, TestRepo.Options with { WriteTimeout = TimeSpan.FromSeconds(5) }, Token);
        try
        {
            var stuck = Assert.Throws<HistoryException>(() => history.Checkpoint(new CheckpointRequest("Add Mara"), Token));

            Assert.StartsWith("Git hasn't finished the checkpoint after 5 seconds. Wolfden stopped waiting but left git running", stuck.Message, StringComparison.Ordinal);
            Assert.Contains("signing (a passphrase prompt)", stuck.Message, StringComparison.Ordinal);
            Assert.Contains("hook", stuck.Message, StringComparison.Ordinal);
            await WaitFor(() => File.Exists(GitFile(repo, "wolfden-hook-started")), "the hook to start");

            // Git still holds its lock: it wasn't stopped, and the lock wasn't deleted.
            Assert.True(File.Exists(GitFile(repo, "index.lock")), "Git's index.lock is gone while git is still committing.");
        }
        finally
        {
            Release(repo, "hook");
        }

        // The hook refuses; once git has exited, the new file is untracked again.
        await WaitFor(
            () => File.Exists(GitFile(repo, "wolfden-hook-done")) && !File.Exists(GitFile(repo, "index.lock")) && Status(repo) == $"?? {Mara}\n",
            "git to finish and the new file to be untracked again");
        Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public async Task ACheckpointGitFinishesAfterTheTimeLimitIsKept()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");
        repo.WriteHook("pre-commit", Waiting("hook", exitCode: 0));
        repo.WriteDen(Mara, "# Mara Voss\n");
        var history = GitHistory.ForDen(repo.DenRoot, TestRepo.Options with { WriteTimeout = TimeSpan.FromSeconds(5) }, Token);
        try
        {
            Assert.Throws<HistoryException>(() => history.Checkpoint(new CheckpointRequest("Add Mara"), Token));
            await WaitFor(() => File.Exists(GitFile(repo, "wolfden-hook-started")), "the hook to start");
        }
        finally
        {
            Release(repo, "hook");
        }

        await WaitFor(
            () => !File.Exists(GitFile(repo, "index.lock")) && repo.Git("log", "-1", "--format=%s") == "Add Mara\n",
            "git to record the checkpoint");
        Assert.Equal($"{Mara}\n", repo.Git("show", "--name-only", "--format=", "HEAD"));
        Assert.Equal("", Status(repo));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public async Task ACheckpointCancelledWhileGitCommitsIsStillRecorded()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");
        repo.WriteHook("pre-commit", Waiting("hook", exitCode: 0));
        repo.WriteDen(Mara, "# Mara Voss\n");

        // Cancel while the pre-commit hook runs, then let the hook finish.
        // Stopping git there would leave index.lock behind (#145).
        using var cancel = new CancellationTokenSource();
        var watch = Task.Run(
            async () =>
            {
                try
                {
                    await WaitFor(() => File.Exists(GitFile(repo, "wolfden-hook-started")), "the hook to start");
                    await cancel.CancelAsync();
                }
                finally
                {
                    Release(repo, "hook");
                }
            },
            Token);

        var entry = repo.History.Checkpoint(new CheckpointRequest("Add Mara"), cancel.Token).Value;
        await watch;

        Assert.True(cancel.IsCancellationRequested);
        Assert.NotNull(entry);
        Assert.Equal("Add Mara", entry.Message);
        Assert.Equal(repo.Git("rev-parse", "HEAD").Trim(), entry.Id);
        Assert.False(File.Exists(GitFile(repo, "index.lock")));
        Assert.Equal("", Status(repo));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public async Task AHooksBackgroundChildDoesntHoldTheCheckpointUp()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");

        // Like a post-commit 'git push &': the child keeps git's stderr open
        // after git has exited, until the test releases it (or a minute).
        repo.WriteHook("post-commit", $"#!/bin/sh\n{BackgroundChild}\nexit 0\n");
        repo.WriteDen(Mara, "# Mara Voss\n");
        try
        {
            // The default limits: what's measured is the wait after git exits, not a limit.
            var clock = Stopwatch.StartNew();
            var entry = repo.History.Checkpoint(new CheckpointRequest("Add Mara"), Token).Value;
            clock.Stop();

            Assert.NotNull(entry);
            Assert.Equal("Add Mara", entry.Message);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"The checkpoint took {clock.Elapsed}, waiting on the hook's child.");
            Assert.False(File.Exists(GitFile(repo, "wolfden-child-done")), "The hook's child ended before the checkpoint returned, so this proves nothing.");
        }
        finally
        {
            await EndBackgroundChild(repo);
        }
    }

    [Fact]
    public async Task OutputAChildHoldsOpenIsReportedIncompleteAndNotUsed()
    {
        using var repo = TestRepo.Create();
        var options = TestRepo.Options with { ReadTimeout = TimeSpan.FromSeconds(5) };
        try
        {
            // An alias runs in the shell: it prints, then leaves a child
            // holding git's standard output open after git has exited.
            var clock = Stopwatch.StartNew();
            var run = GitProcess.Run(options, repo.Root, ["-c", $"alias.wolfden-partial=!printf partial; {BackgroundChild}", "wolfden-partial"], cancellationToken: Token);
            clock.Stop();

            Assert.True(run.Succeeded, run.Said);
            Assert.False(run.OutputComplete);
            Assert.Equal("partial", run.Output);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Git's output was waited for for {clock.Elapsed}.");
            var refused = Assert.Throws<HistoryException>(() => GitHistory.OutputOf(run, "wolfden-partial"));
            Assert.StartsWith("git wolfden-partial finished, but its output was still open when Wolfden stopped waiting", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            await EndBackgroundChild(repo);
        }
    }

    /// <summary>A shell line that starts a child holding the shell's output open until the test releases it, or a minute passes.</summary>
    private const string BackgroundChild =
        "(i=0; while [ ! -f .git/wolfden-child-release ] && [ $i -lt 600 ]; do sleep 0.1; i=$((i+1)); done; : > .git/wolfden-child-done) &";

    /// <summary>
    /// Releases the <see cref="BackgroundChild"/> and waits until it has
    /// gone, so no process outlives the test or holds its folder. A pid
    /// wouldn't do: under Git for Windows, the shell's pids aren't Windows's.
    /// </summary>
    private static async Task EndBackgroundChild(TestRepo repo)
    {
        Release(repo, "child");
        await WaitFor(() => File.Exists(GitFile(repo, "wolfden-child-done")), "the background child to end");
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public void ACancelledCheckpointLeavesTheIndexAsItWas()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");
        repo.WriteDen(Mara, "# Mara Voss\n");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => repo.History.Checkpoint(new CheckpointRequest("Add Mara"), cancelled.Token));

        Assert.Equal($"?? {Mara}\n", Status(repo));
        Assert.Equal("start\n", repo.Git("log", "-1", "--format=%s"));
    }

    [Fact]
    public void AReadThatHangsIsStopped()
    {
        using var repo = TestRepo.Create();
        repo.CommitAll("start");

        // A clean filter that hangs: comparing with the files as they are
        // now runs it, through Wolfden's temporary index.
        repo.Git(
            "config",
            "filter.wolfden-slow.clean",
            ": > .git/wolfden-filter-started; i=0; while [ ! -f .git/wolfden-filter-release ] && [ $i -lt 600 ]; do sleep 0.1; i=$((i+1)); done; cat");
        repo.WriteRepo(".gitattributes", "*.md filter=wolfden-slow\n");
        repo.WriteDen(Mara, "# Mara Voss\n");
        var history = GitHistory.ForDen(repo.DenRoot, TestRepo.Options with { ReadTimeout = TimeSpan.FromSeconds(10) }, Token);
        var clock = Stopwatch.StartNew();
        try
        {
            var stopped = Assert.Throws<HistoryException>(() => history.Diff(new HistoryDiffQuery(), Token));
            clock.Stop();

            Assert.Equal("git add didn't finish within 10 seconds, so Wolfden stopped it, and nothing was changed. " +
                "Git may be waiting on a credential helper or a filter (such as Git LFS), or the repository may be very large.", stopped.Message);
            Assert.True(File.Exists(GitFile(repo, "wolfden-filter-started")), "The filter never ran.");

            // The filter would have waited a minute; Wolfden didn't wait for it.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(45), $"Wolfden waited {clock.Elapsed} for a stopped git.");
            Assert.False(File.Exists(GitFile(repo, "index.lock")));
        }
        finally
        {
            Release(repo, "filter");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ATimeLimitMustBePositive(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GitOptions.Default with { ReadTimeout = TimeSpan.FromSeconds(seconds) });
        Assert.Throws<ArgumentOutOfRangeException>(() => GitOptions.Default with { WriteTimeout = TimeSpan.FromSeconds(seconds) });
        Assert.Equal(Timeout.InfiniteTimeSpan, (GitOptions.Default with { WriteTimeout = Timeout.InfiniteTimeSpan }).WriteTimeout);
    }

    private static string GitFile(TestRepo repo, string name) => Path.Combine(repo.Root, ".git", name);

    private static void Release(TestRepo repo, string name) => File.WriteAllText(GitFile(repo, $"wolfden-{name}-release"), "");

    /// <summary>The repository's status, read without taking any of git's locks, so it can't get in the way of a git that's still running.</summary>
    private static string Status(TestRepo repo) =>
        TestRepo.RunGit(repo.Root, new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0" }, "status", "--porcelain", "--untracked-files=all");

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Gave up waiting for {what}.");
            await Task.Delay(50, Token);
        }
    }
}
