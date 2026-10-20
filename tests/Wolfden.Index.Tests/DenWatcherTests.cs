using System.Collections.Concurrent;

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
    [Trait("Scenario", "SC-WATCH-5")]
    public void OnlyOneWatcherRunsPerDen()
    {
        using var den = TempDen.Starfall();
        using var first = new DenWatcher(den.Root);
        first.Start();
        using var second = new DenWatcher(den.Root);

        var e = Assert.Throws<InvalidOperationException>(() => second.Start());

        Assert.Contains("already watching", e.Message, StringComparison.Ordinal);
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
        using var watcher = Quick(den);
        var updates = new BlockingCollection<IndexUpdate>();
        var failures = new BlockingCollection<Exception>();
        watcher.Updated += (_, u) => updates.Add(u);
        watcher.Failed += (_, e) => failures.Add(e);
        watcher.Start();

        watcher.ReportWatcherError(new InternalBufferOverflowException());

        WaitFor(updates, u => u.Kind == IndexUpdateKind.Rebuilt && u.Reason!.Contains("too many changes", StringComparison.Ordinal));
        Assert.Empty(failures);
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
            CreateWatcher = root => ++created is 2 or 3 ? throw new IOException("No space left on device.") : new FileSystemWatcher(root),
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
        Assert.Contains("listening again failed: No space left on device. Trying again in 20 ms", first.Message, StringComparison.Ordinal);
        Assert.Contains("Trying again in 40 ms", second.Message, StringComparison.Ordinal);
        Assert.Contains("Listening again", third.Message, StringComparison.Ordinal);
        WaitFor(updates, u => u.Kind == IndexUpdateKind.Rebuilt);
        Assert.Equal(4, created);
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
            BeforeApply = _ =>
            {
                if (Interlocked.Decrement(ref fails) >= 0)
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
        var update = WaitFor(updates, _ => true);
        Assert.Equal(IndexUpdateKind.Rebuilt, update.Kind);
        Assert.Equal("the index wasn't known to be up to date", update.Reason);
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
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
