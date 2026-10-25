using Wolfden.Core.History;

namespace Wolfden.Git.Tests;

/// <summary>Tests that tie up the whole thread pool for a moment, so they don't run alongside others.</summary>
[CollectionDefinition(nameof(StarvedPool), DisableParallelization = true)]
public sealed class StarvedPool;

/// <summary>
/// Git's input and output never wait on the thread pool (#146 review): git
/// can exit before its output is read, and a reader waiting for a pool
/// thread would find it late, and take it to be empty or cut short.
/// </summary>
[Collection(nameof(StarvedPool))]
public sealed class StarvedPoolTests
{
    private const string Mara = "universes/vael/entities/characters/mara-voss.md";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "SC-HIST-3")]
    public void GitsOutputAndInputArriveWholeWhileThePoolIsBusy()
    {
        using var repo = TestRepo.Create();
        var start = repo.CommitAll("start");

        // Bigger than any pipe's buffer, both ways: the message goes in on
        // git's input, and comes back out in the log and the diff.
        var message = string.Join("\n", Enumerable.Range(1, 6000).Select(i => $"Line {i} of the session notes, about Mara Voss and the Fen."));
        var text = "# Mara Voss\n\n" + string.Join("\n", Enumerable.Range(1, 6000).Select(i => $"She crossed the fen on night {i}.")) + "\n";
        repo.WriteDen(Mara, text);

        using var release = new ManualResetEventSlim();
        var blockers = Environment.ProcessorCount * 4 + 64;
        for (var i = 0; i < blockers; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ => release.Wait(), null);
        }

        try
        {
            // Evidence the pool is saturated: work is waiting for a thread.
            Assert.True(ThreadPool.PendingWorkItemCount > 0, "The thread pool isn't busy, so this proves nothing.");

            var history = GitHistory.ForDen(repo.DenRoot, TestRepo.Options, Token);
            Assert.Equal(HistoryCapabilities.All, history.Capabilities);
            var entry = history.Checkpoint(new CheckpointRequest(message), Token).Value;
            Assert.NotNull(entry);
            Assert.Equal(message, entry.Message);

            var log = history.Log(new HistoryLogQuery(), Token).Value!;
            Assert.Equal([entry.Id, start], log.Select(e => e.Id));
            Assert.Equal(message, log[0].Message);

            var diff = history.Diff(new HistoryDiffQuery(start, entry.Id), Token).Value!;
            var file = Assert.Single(diff.Files);
            Assert.Contains("+She crossed the fen on night 6000.\n", file.Patch, StringComparison.Ordinal);
            Assert.True(ThreadPool.PendingWorkItemCount > 0, "The thread pool stopped being busy part way.");
        }
        finally
        {
            release.Set();
        }
    }
}
