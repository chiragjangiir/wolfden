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
        Assert.False(File.Exists(Path.Combine(den.Root, IndexBuilder.StateFolder, DenWatcher.LockFileName)));
        using var again = new DenWatcher(den.Root);
        again.Start();
    }

    private static IndexUpdate WaitFor(BlockingCollection<IndexUpdate> updates, Func<IndexUpdate, bool> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (updates.TryTake(out var update, TimeSpan.FromMilliseconds(200)) && done(update))
            {
                return update;
            }
        }

        throw new TimeoutException($"No matching index update within {Timeout.TotalSeconds} s.");
    }
}
