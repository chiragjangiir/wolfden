using Wolfden.Core.Structure;

namespace Wolfden.Index.Tests;

/// <summary>
/// How a den store's changes (<see cref="DenChange"/>) become batches
/// (#14, #121), with made-up times, so no test waits on a clock or a real
/// file event. How file events become changes is tested in the Core tests
/// (<c>DenChangeMappingTests</c>).
/// </summary>
public sealed class ChangeBatcherTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(3);

    private static ChangeBatcher New() => new(Quiet, MaxDelay);

    private static DenChange Change(DenChangeKind kind, string path, string? old = null) => new(kind, path, old);

    [Fact]
    public void NothingIsDueWithoutEvents()
    {
        var batcher = New();

        Assert.Null(batcher.DueIn(T0));
        Assert.Null(batcher.Take(T0.AddHours(1)));
        Assert.False(batcher.HasPending);
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-1")]
    public void ABurstOfSavesIsOneBatchOnceEventsStop()
    {
        var batcher = New();
        const string note = "universes/vael/entities/characters/ione.md";

        // An editor's save: a temporary file, a rename over the note, and a few writes.
        batcher.Add(Change(DenChangeKind.Created, note + ".tmp"), T0);
        batcher.Add(Change(DenChangeKind.Renamed, note, note + ".tmp"), T0.AddMilliseconds(5));
        batcher.Add(Change(DenChangeKind.Changed, note), T0.AddMilliseconds(10));
        batcher.Add(Change(DenChangeKind.Changed, note), T0.AddMilliseconds(200));

        Assert.Null(batcher.Take(T0.AddMilliseconds(400)));
        Assert.Equal(TimeSpan.FromMilliseconds(100), batcher.DueIn(T0.AddMilliseconds(400)));
        var batch = batcher.Take(T0.AddMilliseconds(500));

        Assert.NotNull(batch);
        Assert.Equal([note, note + ".tmp"], batch.Paths.Order(StringComparer.Ordinal));
        Assert.Equal([note, note + ".tmp"], batch.Moved.Order(StringComparer.Ordinal));
        Assert.False(batch.Overflowed);
        Assert.False(batcher.HasPending);
    }

    [Fact]
    public void AChangedEventAloneIsntAMove()
    {
        var batcher = New();
        batcher.Add(Change(DenChangeKind.Changed, "universes/vael/entities"), T0);

        var batch = batcher.Take(T0 + Quiet)!;

        Assert.Equal(["universes/vael/entities"], batch.Paths);
        Assert.Empty(batch.Moved);
    }

    [Theory]
    [InlineData(DenChangeKind.Created)]
    [InlineData(DenChangeKind.Deleted)]
    [InlineData(DenChangeKind.Renamed)]
    public void ACreatedDeletedOrRenamedPathIsAMove(DenChangeKind kind)
    {
        var batcher = New();
        batcher.Add(Change(kind, "universes/vael/entities"), T0);

        Assert.Equal(["universes/vael/entities"], batcher.Take(T0 + Quiet)!.Moved);
    }

    [Fact]
    public void EventsThatKeepComingAreBatchedAfterTheMaxDelay()
    {
        var batcher = New();
        var t = T0;
        for (var i = 0; t < T0 + MaxDelay; i++, t = t.AddMilliseconds(100))
        {
            batcher.Add(Change(DenChangeKind.Changed, $"notes/n{i}.md"), t);
            if (t < T0 + MaxDelay)
            {
                Assert.Null(batcher.Take(t));
            }
        }

        var batch = batcher.Take(T0 + MaxDelay);

        Assert.NotNull(batch);
        Assert.Equal(30, batch.Paths.Count);
    }

    [Theory]
    [InlineData(".wolfden/index.db")]
    [InlineData(".wolfden/index.db-journal")]
    [InlineData(".git/index")]
    [InlineData("universes/vael/entities/characters/.ione.md.swp")]
    [InlineData("universes/vael/entities/characters/.wolfden-write-0123.tmp")]
    [InlineData("universes/.obsidian/workspace.json")]
    [Trait("Scenario", "SC-WATCH-4")]
    public void HiddenPathsAreIgnored(string path)
    {
        var batcher = New();

        batcher.Add(Change(DenChangeKind.Changed, path), T0);

        Assert.False(batcher.HasPending);
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-4")]
    public void AStoresAtomicWriteIsBatchedAsTheNoteAlone()
    {
        // The store writes a hidden temporary file, then moves it over the note (#121).
        var batcher = New();
        const string note = "universes/vael/entities/characters/ione.md";
        const string temp = "universes/vael/entities/characters/.wolfden-write-0123.tmp";

        batcher.Add(Change(DenChangeKind.Created, temp), T0);
        batcher.Add(Change(DenChangeKind.Changed, temp), T0);
        batcher.Add(Change(DenChangeKind.Renamed, note, temp), T0);

        var batch = batcher.Take(T0 + Quiet);
        Assert.NotNull(batch);
        Assert.Equal([note], batch.Paths);
        Assert.Equal([note], batch.Moved);
        Assert.False(batch.Overflowed);
    }

    [Fact]
    public void ARenameOutOfAHiddenFolderKeepsTheVisibleName()
    {
        var batcher = New();

        batcher.Add(Change(DenChangeKind.Renamed, "notes/plan.md", ".trash/plan.md"), T0);

        Assert.Equal(["notes/plan.md"], batcher.Take(T0 + Quiet)!.Paths);
    }

    [Fact]
    public void LostEventsMarkTheBatch()
    {
        var batcher = New();

        batcher.AddOverflow(T0);
        var batch = batcher.Take(T0 + Quiet);

        Assert.NotNull(batch);
        Assert.True(batch.Overflowed);
        Assert.Empty(batch.Paths);
        Assert.False(batcher.Take(T0.AddHours(1))?.Overflowed ?? false);
    }

    [Fact]
    public void PathsToTryAgainAreBatchedLater()
    {
        var batcher = New();

        batcher.AddAgain(["notes/busy.md"], T0);

        Assert.Equal(["notes/busy.md"], batcher.Take(T0 + Quiet)!.Paths);
    }
}
