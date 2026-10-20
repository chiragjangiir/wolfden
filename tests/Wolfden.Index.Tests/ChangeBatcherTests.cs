namespace Wolfden.Index.Tests;

/// <summary>
/// How file events become batches (#14), with made-up times, so no test
/// waits on a clock or a real file event.
/// </summary>
public sealed class ChangeBatcherTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wolfden-batcher", "den");
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(3);

    private static ChangeBatcher New() => new(Root, Quiet, MaxDelay);

    private static string Full(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

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
        batcher.Add(WatcherChangeTypes.Created, Full(note + ".tmp"), null, T0);
        batcher.Add(WatcherChangeTypes.Renamed, Full(note), Full(note + ".tmp"), T0.AddMilliseconds(5));
        batcher.Add(WatcherChangeTypes.Changed, Full(note), null, T0.AddMilliseconds(10));
        batcher.Add(WatcherChangeTypes.Changed, Full(note), null, T0.AddMilliseconds(200));

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
        batcher.Add(WatcherChangeTypes.Changed, Full("universes/vael/entities"), null, T0);

        var batch = batcher.Take(T0 + Quiet)!;

        Assert.Equal(["universes/vael/entities"], batch.Paths);
        Assert.Empty(batch.Moved);
    }

    [Fact]
    public void EventsThatKeepComingAreBatchedAfterTheMaxDelay()
    {
        var batcher = New();
        var t = T0;
        for (var i = 0; t < T0 + MaxDelay; i++, t = t.AddMilliseconds(100))
        {
            batcher.Add(WatcherChangeTypes.Changed, Full($"notes/n{i}.md"), null, t);
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
    [InlineData("universes/.obsidian/workspace.json")]
    [Trait("Scenario", "SC-WATCH-4")]
    public void HiddenPathsAreIgnored(string path)
    {
        var batcher = New();

        batcher.Add(WatcherChangeTypes.Changed, Full(path), null, T0);

        Assert.False(batcher.HasPending);
    }

    [Fact]
    public void PathsOutsideTheDenAreIgnored()
    {
        var batcher = New();

        batcher.Add(WatcherChangeTypes.Created, Path.Combine(Path.GetTempPath(), "elsewhere.md"), null, T0);
        batcher.Add(WatcherChangeTypes.Changed, Root, null, T0);

        Assert.False(batcher.HasPending);
    }

    [Fact]
    public void ARenameOutOfAHiddenFolderKeepsTheVisibleName()
    {
        var batcher = New();

        batcher.Add(WatcherChangeTypes.Renamed, Full("notes/plan.md"), Full(".trash/plan.md"), T0);

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

    [Theory]
    [InlineData("a/b.md", "a/b.md")]
    [InlineData("a/.b/c.md", null)]
    [InlineData(".wolfden", null)]
    public void RelativePathsUseSlashes(string path, string? expected)
    {
        Assert.Equal(expected, ChangeBatcher.Relative(Root, Full(path)));
    }
}
