using System.Collections.Concurrent;
using Wolfden.Core.Structure;

namespace Wolfden.Index.Tests;

/// <summary>
/// A few end-to-end tests of <see cref="DenWatcher"/> with real file
/// events (#14). Event timing varies by OS and load, so they wait
/// generously and check only what must happen eventually. What the batches
/// do is tested without real events in <see cref="IncrementalUpdateTests"/>.
/// </summary>
public sealed class DenWatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private const string Mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";

    [Fact]
    [Trait("Scenario", "SC-WATCH-1")]
    public void AnEditIsInTheIndexWithinMoments()
    {
        using var den = TempDen.Starfall();
        using var watcher = new DenWatcher(den.Root, TimeSpan.FromMilliseconds(100));
        var updates = new BlockingCollection<IndexUpdate>();
        watcher.Updated += (_, u) => updates.Add(u);

        var first = watcher.Start();
        Assert.Equal(IndexUpdateKind.Rebuilt, first.Kind);
        Assert.True(first.Summary.Entities > 0);

        den.Write(Mara, File.ReadAllText(Path.Combine(den.Root, Mara)).Replace("name: Mara Voss", "name: Mara Ellery", StringComparison.Ordinal));

        var update = WaitFor(updates, u => u.Files.Contains(Mara) || u.Kind == IndexUpdateKind.Rebuilt);
        using var index = DenIndex.Open(den.Root);
        Assert.Equal("Mara Ellery", index.FindEntity("01JA7Q8M4XK3V9R2D5W8HN6TQB")!.Name);
        Assert.Equal(update.Summary.Entities, index.Context.Entities.Count());
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-1")]
    public void ANewNoteAndADeletedNoteAreFollowed()
    {
        using var den = TempDen.Starfall();
        using var watcher = new DenWatcher(den.Root, TimeSpan.FromMilliseconds(100));
        var updates = new BlockingCollection<IndexUpdate>();
        watcher.Updated += (_, u) => updates.Add(u);
        var entities = watcher.Start().Summary.Entities;
        const string wren = "universes/starfall/entities/characters/wren-ashby.md";

        den.Write(wren, "---\ntype: character\nname: Wren Ashby\nstatus: canon\n---\n\nA pilot.\n");
        WaitFor(updates, u => u.Summary.Entities == entities + 1);
        File.Delete(Path.Combine(den.Root, Mara));
        WaitFor(updates, u => u.Summary.Entities == entities);

        using var index = DenIndex.Open(den.Root);
        Assert.NotNull(index.FindEntity(wren));
        Assert.Null(index.FindEntity(Mara));
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-1")]
    public void AWriteThroughTheDenStoreIsFollowedLikeAnyEdit()
    {
        // The atomic write's temporary file is hidden, so only the note shows.
        using var den = TempDen.Starfall();
        var store = new FileSystemDenStore(den.Root);
        using var watcher = new DenWatcher(store, TimeSpan.FromMilliseconds(100));
        var updates = new BlockingCollection<IndexUpdate>();
        watcher.Updated += (_, u) => updates.Add(u);
        watcher.Start();
        Assert.Same(store, watcher.Store);

        var text = File.ReadAllText(Path.Combine(den.Root, Mara)).Replace("name: Mara Voss", "name: Mara Ellery", StringComparison.Ordinal);
        store.WriteFile(Mara, System.Text.Encoding.UTF8.GetBytes(text), DenWriteMode.Replace);

        var update = WaitFor(updates, u => u.Files.Contains(Mara) || u.Kind == IndexUpdateKind.Rebuilt);
        Assert.DoesNotContain(update.Files, f => f.Contains(FileSystemDenStore.TempPrefix, StringComparison.Ordinal));
        using var index = DenIndex.Open(den.Root);
        Assert.Equal("Mara Ellery", index.FindEntity("01JA7Q8M4XK3V9R2D5W8HN6TQB")!.Name);
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-5")]
    public void OnlyOneWatcherRunsPerDen()
    {
        using var den = TempDen.Starfall();
        using var first = new DenWatcher(den.Root);
        first.Start();
        using var second = new DenWatcher(den.Root);

        var e = Assert.Throws<DenAlreadyWatchedException>(() => second.Start());

        Assert.Contains("already watching", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbesInThisProcessNeverLookLikeAnotherWatcher()
    {
        // IsWatching opens the lock file for a moment. In this process it
        // takes turns with a starting watcher, so however often calls look,
        // the start isn't mistaken for a second watcher (#137).
        using var den = TempDen.Starfall();
        using var done = new CancellationTokenSource();
        // Dedicated threads, so the spinning probes don't take the thread pool from tests running alongside.
        var probes = Enumerable.Range(0, 3).Select(_ => Task.Factory.StartNew(
            () =>
            {
                while (!done.IsCancellationRequested)
                {
                    DenWatcher.IsWatching(den.Root);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();
        try
        {
            // Each start stops once it holds the lock, before the build, so many fit in a short test.
            for (var i = 0; i < 300; i++)
            {
                using var watcher = new DenWatcher(den.Root) { BeforeFirstBuild = () => throw new TimeoutException("Held the lock.") };
                Assert.Equal("Held the lock.", Assert.Throws<TimeoutException>(() => watcher.Start()).Message);
            }
        }
        finally
        {
            done.Cancel();
            await Task.WhenAll(probes);
        }
    }

    [Fact]
    public void DisposingStopsTheWatcherAndReleasesTheDen()
    {
        using var den = TempDen.Starfall();
        var updates = new BlockingCollection<IndexUpdate>();
        using (var watcher = new DenWatcher(den.Root, TimeSpan.FromMilliseconds(50)))
        {
            watcher.Updated += (_, u) => updates.Add(u);
            watcher.Start();
        }

        File.AppendAllText(Path.Combine(den.Root, Mara), "\nA new line.\n");

        Assert.False(updates.TryTake(out _, TimeSpan.FromSeconds(1)));
        using var again = new DenWatcher(den.Root);
        again.Start();
    }

    [Fact]
    public void LostEventsRebuildWithoutAnError()
    {
        using var den = TempDen.Starfall();
        var feed = new CapturingFeed(new FileSystemDenStore(den.Root));
        using var watcher = new DenWatcher(den.Root, TimeSpan.FromMilliseconds(50))
        {
            FirstPause = TimeSpan.FromMilliseconds(20),
            MaxPause = TimeSpan.FromMilliseconds(100),
            Changes = feed,
        };
        var updates = new BlockingCollection<IndexUpdate>();
        var failures = new BlockingCollection<Exception>();
        watcher.Updated += (_, u) => updates.Add(u);
        watcher.Failed += (_, e) => failures.Add(e);
        watcher.Start();

        // As the store's watch reports a full event buffer.
        feed.Observers[^1].Lost();

        WaitFor(updates, u => u.Kind == IndexUpdateKind.Rebuilt && u.Reason!.Contains("too many changes", StringComparison.Ordinal));
        Assert.Empty(failures);
    }

    [Fact]
    public void AReplacedWatchsLostEventsAndBreaksAreIgnored()
    {
        using var den = TempDen.Starfall();
        var feed = new CapturingFeed(new FileSystemDenStore(den.Root));
        using var watcher = new DenWatcher(den.Root, TimeSpan.FromMilliseconds(50))
        {
            FirstPause = TimeSpan.FromMilliseconds(20),
            MaxPause = TimeSpan.FromMilliseconds(100),
            Changes = feed,
        };
        var updates = new BlockingCollection<IndexUpdate>();
        var failures = new BlockingCollection<Exception>();
        watcher.Updated += (_, u) => updates.Add(u);
        watcher.Failed += (_, e) => failures.Add(e);
        watcher.Start();
        watcher.ReportWatcherError(new IOException("The drive went away."));
        WaitFor(failures, e => e.Message.Contains("Listening again", StringComparison.Ordinal));
        WaitFor(updates, u => u.Kind == IndexUpdateKind.Rebuilt);
        Assert.Equal(2, feed.Observers.Count);

        // The first watch, replaced, has a last word: neither is acted on.
        feed.Observers[0].Lost();
        feed.Observers[0].Broken(new IOException("The old watch broke."));

        Assert.False(updates.TryTake(out _, TimeSpan.FromMilliseconds(500)));
        Assert.False(failures.TryTake(out _, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void TheChangeFeedCantBeNull()
    {
        using var den = TempDen.Starfall();

        Assert.Throws<ArgumentNullException>(() => new DenWatcher(den.Root) { Changes = null! });
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-5")]
    public void WhenFileEventsStopItListensAgainRebuildsAndSaysSo()
    {
        using var den = TempDen.Starfall();
        using var watcher = Quick(den);
        var updates = new BlockingCollection<IndexUpdate>();
        var failures = new BlockingCollection<Exception>();
        watcher.Updated += (_, u) => updates.Add(u);
        watcher.Failed += (_, e) => failures.Add(e);
        watcher.Start();

        // As when a network drive drops: Windows stops the watcher for good.
        watcher.ReportWatcherError(new IOException("The network path was not found."));

        var failure = WaitFor(failures, _ => true);
        Assert.Contains("file events stopped (The network path was not found)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Listening again", failure.Message, StringComparison.Ordinal);
        WaitFor(updates, u => u.Kind == IndexUpdateKind.Rebuilt);

        // The new watcher sees edits.
        den.Write(Mara, File.ReadAllText(Path.Combine(den.Root, Mara)).Replace("name: Mara Voss", "name: Mara Ellery", StringComparison.Ordinal));
        WaitFor(updates, u => u.Files.Contains(Mara));
    }

    [Fact]
    public void WhenListeningAgainFailsItKeepsTryingAndReporting()
    {
        using var den = TempDen.Starfall();
        var created = 0;
        using var watcher = new DenWatcher(den.Root, TimeSpan.FromMilliseconds(50))
        {
            FirstPause = TimeSpan.FromMilliseconds(20),
            MaxPause = TimeSpan.FromMilliseconds(80),
            // The first watcher works; the next two can't be made; then it works again.
            Changes = new FlakyFeed(new FileSystemDenStore(den.Root), () => ++created is 2 or 3),
        };
        var updates = new BlockingCollection<IndexUpdate>();
        var failures = new BlockingCollection<Exception>();
        watcher.Updated += (_, u) => updates.Add(u);
        watcher.Failed += (_, e) => failures.Add(e);
        watcher.Start();

        watcher.ReportWatcherError(new IOException("Too many folders to watch."));

        var first = WaitFor(failures, _ => true);
        var second = WaitFor(failures, _ => true);
        var third = WaitFor(failures, _ => true);
        Assert.Contains("listening again failed: No space left on device. Trying again in 40 ms", first.Message, StringComparison.Ordinal);
        Assert.Contains("Trying again in 80 ms", second.Message, StringComparison.Ordinal);
        Assert.Contains("Listening again", third.Message, StringComparison.Ordinal);
        WaitFor(updates, u => u.Kind == IndexUpdateKind.Rebuilt);
        Assert.Equal(4, created);
    }

    [Fact]
    public void AWatchThatBreaksAgainRightAwayWaitsLongerEachTime()
    {
        // As on Linux at the limit of folder watches: the new watch starts,
        // then breaks again at once.
        using var den = TempDen.Starfall();
        using var watcher = new DenWatcher(den.Root, TimeSpan.FromMilliseconds(50))
        {
            FirstPause = TimeSpan.FromMilliseconds(20),
            MaxPause = TimeSpan.FromMilliseconds(160),
            HealthyAfter = TimeSpan.FromSeconds(30),
        };
        var restarts = Restarts(watcher, breaksAgain: 5, out var updates, out var failures);
        watcher.Start();
        var start = DateTime.UtcNow;

        watcher.ReportWatcherError(new IOException("The watch limit was reached."));

        var seen = Enumerable.Range(0, 6).Select(_ => WaitFor(restarts, _ => true)).ToList();
        Assert.Equal([20, 40, 80, 160, 160, 160], seen.Select(r => (int)r.Pause.TotalMilliseconds));
        var previous = start;
        foreach (var (at, pause) in seen)
        {
            // Each attempt waited at least its pause (less a little for clock grain).
            Assert.True(at - previous >= pause * 0.8, $"Listened again {(at - previous).TotalMilliseconds} ms after the break; the pause was {pause.TotalMilliseconds} ms.");
            previous = at;
        }

        // One report per attempt, not one per error, and no more rebuilds than attempts.
        Thread.Sleep(500);
        Assert.Equal(6, failures.Count);
        var rebuilds = updates.Count(u => u.Kind == IndexUpdateKind.Rebuilt);
        Assert.InRange(rebuilds, 1, 6);
    }

    [Fact]
    public void AfterRunningWellForAWhileTheNextBreakStartsFromTheFirstPause()
    {
        using var den = TempDen.Starfall();
        using var watcher = new DenWatcher(den.Root, TimeSpan.FromMilliseconds(50))
        {
            FirstPause = TimeSpan.FromMilliseconds(20),
            MaxPause = TimeSpan.FromMilliseconds(160),
            HealthyAfter = TimeSpan.FromMilliseconds(300),
        };
        var restarts = Restarts(watcher, breaksAgain: 1, out _, out _);
        watcher.Start();

        watcher.ReportWatcherError(new IOException("The watch limit was reached."));
        Assert.Equal(TimeSpan.FromMilliseconds(20), WaitFor(restarts, _ => true).Pause);
        Assert.Equal(TimeSpan.FromMilliseconds(40), WaitFor(restarts, _ => true).Pause);

        // The new watch runs without trouble for longer than HealthyAfter.
        Thread.Sleep(600);
        watcher.ReportWatcherError(new IOException("The drive went away."));

        Assert.Equal(TimeSpan.FromMilliseconds(20), WaitFor(restarts, _ => true).Pause);
    }

    /// <summary>
    /// Records each time the watcher listens again, with the pause it waited,
    /// and breaks the new watch again at once, <paramref name="breaksAgain"/> times.
    /// </summary>
    private static BlockingCollection<(DateTime At, TimeSpan Pause)> Restarts(
        DenWatcher watcher, int breaksAgain, out BlockingCollection<IndexUpdate> updates, out BlockingCollection<Exception> failures)
    {
        var restarts = new BlockingCollection<(DateTime, TimeSpan)>();
        var seenUpdates = updates = new BlockingCollection<IndexUpdate>();
        var seenFailures = failures = new BlockingCollection<Exception>();
        watcher.Updated += (_, u) => seenUpdates.Add(u);
        watcher.Failed += (_, e) =>
        {
            seenFailures.Add(e);
            if (e.Message.Contains("Listening again", StringComparison.Ordinal))
            {
                restarts.Add((DateTime.UtcNow, watcher.LastRestartPause));
                if (Interlocked.Decrement(ref breaksAgain) >= 0)
                {
                    watcher.ReportWatcherError(new IOException("The watch limit was reached."));
                }
            }
        };
        return restarts;
    }

    [Fact]
    public void AFailedBatchIsRetriedAsARebuildAfterAGrowingPause()
    {
        using var den = TempDen.Starfall();
        var fails = 2;
        using var watcher = new DenWatcher(den.Root, TimeSpan.FromMilliseconds(50))
        {
            FirstPause = TimeSpan.FromMilliseconds(20),
            MaxPause = TimeSpan.FromSeconds(1),
            // Only the edit's batch and the rebuilds after it fail. A new
            // watch can start with a batch of its own (on Windows, events
            // for the freshly copied den's folders, which change nothing).
            // Had that one failed, the rebuild could read Mara before the
            // edit and report Rebuilt while the edit was still on its way.
            BeforeApply = batch =>
            {
                if ((batch.Paths.Contains(Mara) || batch.Overflowed) && Interlocked.Decrement(ref fails) >= 0)
                {
                    throw new IOException("The index is locked.");
                }
            },
        };
        var updates = new BlockingCollection<IndexUpdate>();
        var failures = new BlockingCollection<Exception>();
        watcher.Updated += (_, u) => updates.Add(u);
        watcher.Failed += (_, e) => failures.Add(e);
        watcher.Start();

        File.AppendAllText(Path.Combine(den.Root, Mara), "\nA new line.\n");

        Assert.Equal("couldn't update the index: The index is locked. Trying again with a rebuild in 20 ms.", WaitFor(failures, _ => true).Message);
        Assert.Equal("couldn't update the index: The index is locked. Trying again with a rebuild in 40 ms.", WaitFor(failures, _ => true).Message);
        var update = WaitFor(updates, u => u.Kind == IndexUpdateKind.Rebuilt);
        Assert.Equal("the index wasn't known to be up to date", update.Reason);

        // The edit is in the index once the watcher has caught up: at once
        // if the rebuild read it, else after the edit's own batch.
        var deadline = DateTime.UtcNow + Timeout;
        while (IndexBuilder.CheckFiles(den.Root) != IndexStatus.Current)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The index wasn't current within {Timeout.TotalSeconds} s.");
            Thread.Sleep(50);
        }
    }

    [Fact]
    public void AnUpdatedHandlerThatThrowsIsntAFailedBatch()
    {
        using var den = TempDen.Starfall();
        using var watcher = Quick(den);
        var updates = new BlockingCollection<IndexUpdate>();
        var failures = new BlockingCollection<Exception>();
        var thrown = false;
        watcher.Updated += (_, u) =>
        {
            updates.Add(u);
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("The host's console went away.");
            }
        };
        watcher.Failed += (_, e) => failures.Add(e);
        watcher.Start();

        File.AppendAllText(Path.Combine(den.Root, Mara), "\nA new line.\n");
        WaitFor(updates, u => u.Files.Contains(Mara));
        Assert.Contains("a handler of the watcher's updates failed: The host's console went away.", WaitFor(failures, _ => true).Message, StringComparison.Ordinal);
        File.AppendAllText(Path.Combine(den.Root, Mara), "\nAnother line.\n");

        // Still incremental: the handler's failure didn't make the watcher rebuild.
        var next = WaitFor(updates, u => u.Files.Contains(Mara) || u.Kind == IndexUpdateKind.Rebuilt);
        Assert.Equal(IndexUpdateKind.Updated, next.Kind);
    }

    [Theory]
    [InlineData(1000, 30_000, 2000)]
    [InlineData(20_000, 30_000, 30_000)]
    [InlineData(30_000, 30_000, 30_000)]
    public void PausesDoubleUpToTheMaximum(int pause, int max, int next)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(next), DenWatcher.NextPause(TimeSpan.FromMilliseconds(pause), TimeSpan.FromMilliseconds(max)));
    }

    /// <summary>A den store's change feed whose watches can't be started while <paramref name="fail"/> says so.</summary>
    private sealed class FlakyFeed(IDenChangeFeed inner, Func<bool> fail) : IDenChangeFeed
    {
        public IDisposable Watch(IDenChangeObserver observer) =>
            fail() ? throw new IOException("No space left on device.") : inner.Watch(observer);
    }

    /// <summary>A den store's change feed that keeps each watch's observer, so a test can report as the store would.</summary>
    private sealed class CapturingFeed(IDenChangeFeed inner) : IDenChangeFeed
    {
        public List<IDenChangeObserver> Observers { get; } = [];

        public IDisposable Watch(IDenChangeObserver observer)
        {
            lock (Observers)
            {
                Observers.Add(observer);
            }

            return inner.Watch(observer);
        }
    }

    private static DenWatcher Quick(TempDen den) => new(den.Root, TimeSpan.FromMilliseconds(50))
    {
        FirstPause = TimeSpan.FromMilliseconds(20),
        MaxPause = TimeSpan.FromMilliseconds(100),
    };

    private static T WaitFor<T>(BlockingCollection<T> items, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (items.TryTake(out var item, TimeSpan.FromMilliseconds(200)) && done(item))
            {
                return item;
            }
        }

        throw new TimeoutException($"Nothing matching within {Timeout.TotalSeconds} s.");
    }
}
