namespace Wolfden.Index.Tests;

/// <summary>
/// The file watcher's incremental update (#14), driven by synthetic batches
/// of changed paths so the tests don't depend on file event timing. After
/// each batch the index must have the content of a full rebuild of the same
/// files. <see cref="IncrementalUpdatePropertyTests"/> does the same for
/// random change sequences.
/// </summary>
public sealed class IncrementalUpdateTests
{
    [Fact]
    [Trait("Scenario", "SC-WATCH-1")]
    public void AnEditedNoteIsUpdatedWithoutARebuild()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        var written = File.GetLastWriteTimeUtc(den.IndexPath);
        const string mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
        den.Write(mara, File.ReadAllText(Path.Combine(den.Root, mara)).Replace("name: Mara Voss", "name: Mara Ellery", StringComparison.Ordinal));

        var update = updater.Apply(Batch(den, (WatcherChangeTypes.Changed, mara, null)), out _);

        Assert.NotNull(update);
        Assert.Equal(IndexUpdateKind.Updated, update.Kind);
        Assert.Equal([mara], update.Files);
        Assert.True(update.Rows > 0);
        using var index = DenIndex.Open(den.Root);
        Assert.Equal("Mara Ellery", index.FindEntity("01JA7Q8M4XK3V9R2D5W8HN6TQB")!.Name);
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
        AssertSameAsFullRebuild(den, "after the edit");
        Assert.True(File.GetLastWriteTimeUtc(den.IndexPath) >= written);
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-2")]
    public void RenamingACharacterBreaksTheOldLinksAndAnAliasMendsThem()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        const string pike = "universes/starfall/series/long-dark/books/ash/entities/characters/halden-pike.md";
        var text = File.ReadAllText(Path.Combine(den.Root, pike));
        int Broken()
        {
            using var index = DenIndex.Open(den.Root);
            return index.Context.Links.Count(l => l.Outcome == "broken");
        }

        var before = Broken();
        var renamedText = text.Replace("name: Halden Pike", "name: Halden Marsh", StringComparison.Ordinal);
        den.Write(pike, renamedText);
        updater.Apply(Batch(den, (WatcherChangeTypes.Changed, pike, null)), out _);
        AssertSameAsFullRebuild(den, "after the rename");
        var renamed = Broken();

        den.Write(pike, renamedText.Replace("aliases: [Pike]", "aliases: [Pike, Halden Pike]", StringComparison.Ordinal));
        updater.Apply(Batch(den, (WatcherChangeTypes.Changed, pike, null)), out _);
        AssertSameAsFullRebuild(den, "after the alias");

        Assert.True(renamed > before, $"{renamed} broken links after the rename, {before} before.");
        Assert.Equal(before, Broken());
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-3")]
    public void AStructureChangeRebuilds()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        File.AppendAllText(Path.Combine(den.Root, "den.yaml"), "spoiler_checks: true\n");

        var update = updater.Apply(Batch(den, (WatcherChangeTypes.Changed, "den.yaml", null)), out _);

        Assert.NotNull(update);
        Assert.Equal(IndexUpdateKind.Rebuilt, update.Kind);
        Assert.Equal("den.yaml changed", update.Reason);
        AssertSameAsFullRebuild(den, "after den.yaml changed");
    }

    [Theory]
    [Trait("Scenario", "SC-WATCH-3")]
    [InlineData("universes/starfall/series/long-dark/books/ember/book.md")]
    [InlineData("universes/starfall/universe.md")]
    public void AContainerFileChangeRebuilds(string path)
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        File.AppendAllText(Path.Combine(den.Root, path), "\nMore about it.\n");

        var update = updater.Apply(Batch(den, (WatcherChangeTypes.Changed, path, null)), out _);

        Assert.Equal(IndexUpdateKind.Rebuilt, update!.Kind);
        AssertSameAsFullRebuild(den, $"after {path} changed");
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-4")]
    public void FilesTheIndexDoesntReadChangeOnlyTheFingerprint()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        den.Write("notes/ideas.md", "Some loose ideas.\n");
        den.Write("universes/starfall/entities/locations/map.png", "not really a picture");
        den.Write(".git/HEAD", "ref: refs/heads/main\n");

        var update = updater.Apply(
            Batch(den, (WatcherChangeTypes.Created, "notes", null), (WatcherChangeTypes.Created, "notes/ideas.md", null),
                (WatcherChangeTypes.Created, "universes/starfall/entities/locations/map.png", null), (WatcherChangeTypes.Created, ".git/HEAD", null)),
            out _);

        Assert.Null(update);
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
        AssertSameAsFullRebuild(den, "after the notes");
    }

    [Fact]
    public void AnEmptyBatchChangesNothing()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        var before = File.ReadAllBytes(den.IndexPath);

        Assert.Null(updater.Apply(Batch(den), out _));
        // A save that changes no bytes, as some editors do.
        const string mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
        File.SetLastWriteTimeUtc(Path.Combine(den.Root, mara), DateTime.UtcNow);
        Assert.Null(updater.Apply(Batch(den, (WatcherChangeTypes.Changed, mara, null)), out _));

        Assert.Equal(before, File.ReadAllBytes(den.IndexPath));
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-5")]
    public void AnIndexRebuiltByAnotherCommandIsRebuiltNotPatched()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        // A query elsewhere saw an edit first and rebuilt the index itself.
        const string mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
        File.AppendAllText(Path.Combine(den.Root, mara), "\nA new line.\n");
        IndexBuilder.EnsureUpToDate(den.Root);
        const string pike = "universes/starfall/series/long-dark/books/ash/entities/characters/halden-pike.md";
        File.AppendAllText(Path.Combine(den.Root, pike), "\nAnother line.\n");

        var update = updater.Apply(Batch(den, (WatcherChangeTypes.Changed, mara, null), (WatcherChangeTypes.Changed, pike, null)), out _);

        Assert.Equal(IndexUpdateKind.Rebuilt, update!.Kind);
        Assert.Equal("another command rebuilt the index meanwhile", update.Reason);
        AssertSameAsFullRebuild(den, "after the other rebuild");
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-5")]
    public void ADeletedIndexIsRebuilt()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        File.Delete(den.IndexPath);
        const string mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
        File.AppendAllText(Path.Combine(den.Root, mara), "\nA new line.\n");

        var update = updater.Apply(Batch(den, (WatcherChangeTypes.Changed, mara, null)), out _);

        Assert.Equal(IndexUpdateKind.Rebuilt, update!.Kind);
        AssertSameAsFullRebuild(den, "after the delete");
    }

    [Fact]
    public void LostEventsRebuild()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");

        var update = updater.Apply(new ChangeBatch(new HashSet<string>(), new HashSet<string>(), Overflowed: true), out _);

        Assert.Equal(IndexUpdateKind.Rebuilt, update!.Kind);
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-1")]
    public void AMovedFolderOfNotesIsFollowed()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        const string from = "universes/starfall/series/long-dark/entities/characters";
        const string to = "universes/starfall/series/long-dark/entities/crew";
        Directory.Move(Path.Combine(den.Root, from), Path.Combine(den.Root, to));

        // Windows reports only the folder's rename, not each file in it.
        var update = updater.Apply(Batch(den, (WatcherChangeTypes.Renamed, to, from)), out _);

        Assert.Equal(IndexUpdateKind.Updated, update!.Kind);
        Assert.Contains($"{to}/mara-voss.md", update.Files);
        Assert.Contains($"{from}/mara-voss.md", update.Files);
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
        AssertSameAsFullRebuild(den, "after the move");
    }

    [Fact]
    public void ANoteBeingWrittenIsTriedAgain()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        const string mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
        var full = Path.Combine(den.Root, mara);
        File.AppendAllText(full, "\nA new line.\n");

        IReadOnlyList<string> retry;
        using (new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
            {
                // Only Windows refuses to read a file another process holds open.
                return;
            }

            Assert.Null(updater.Apply(Batch(den, (WatcherChangeTypes.Changed, mara, null)), out retry));
        }

        Assert.Equal([mara], retry);
        Assert.Equal(IndexUpdateKind.Updated, updater.Apply(Batch(den, (WatcherChangeTypes.Changed, mara, null)), out _)!.Kind);
        AssertSameAsFullRebuild(den, "after the retry");
    }

    /// <summary>A batch of events, by den-relative path, as the watcher would collect them.</summary>
    private static ChangeBatch Batch(TempDen den, params (WatcherChangeTypes Kind, string Path, string? OldPath)[] events)
    {
        var batcher = new ChangeBatcher(den.Root, TimeSpan.Zero, TimeSpan.Zero);
        var now = DateTime.UtcNow;
        foreach (var (kind, path, old) in events)
        {
            batcher.Add(kind, Full(den.Root, path), old is null ? null : Full(den.Root, old), now);
        }

        return batcher.Take(now) ?? new ChangeBatch(new HashSet<string>(), new HashSet<string>(), false);
    }

    internal static string Full(string root, string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The den's index has the same content as a full rebuild of a copy of its files, and its fingerprint matches the files.</summary>
    internal static void AssertSameAsFullRebuild(TempDen den, string when)
    {
        var reference = Path.Combine(Path.GetTempPath(), "wolfden-index-tests", $"reference-{Guid.NewGuid():N}.db");
        string expected;
        try
        {
            IndexBuilder.BuildInto(den.Root, reference);
            expected = IndexContent.Of(reference);
        }
        finally
        {
            File.Delete(reference);
        }

        var actual = IndexContent.Of(den.IndexPath);
        if (expected != actual)
        {
            var e = expected.Split('\n').CountBy(l => l).ToDictionary(StringComparer.Ordinal);
            var a = actual.Split('\n').CountBy(l => l).ToDictionary(StringComparer.Ordinal);
            var missing = e.Where(l => a.GetValueOrDefault(l.Key) < l.Value).Select(l => $"{l.Value - a.GetValueOrDefault(l.Key)}x {l.Key}").Take(8);
            var extra = a.Where(l => e.GetValueOrDefault(l.Key) < l.Value).Select(l => $"{l.Value - e.GetValueOrDefault(l.Key)}x {l.Key}").Take(8);
            Assert.Fail($"{when}: the index differs from a full rebuild.\nMissing:\n{string.Join('\n', missing)}\nExtra:\n{string.Join('\n', extra)}");
        }

        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
    }
}
