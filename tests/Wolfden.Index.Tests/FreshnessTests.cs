namespace Wolfden.Index.Tests;

/// <summary>
/// <see cref="IndexBuilder.CheckFiles"/> and <see cref="IndexBuilder.EnsureUpToDate"/>:
/// telling whether the den's files changed since the index was built, for
/// one-off commands such as <c>wolfden query</c> that have no file watcher.
/// </summary>
public sealed class FreshnessTests
{
    private const string Mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";

    [Fact]
    public void AFreshIndexIsCurrent()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);

        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
    }

    [Fact]
    public void AMissingIndexIsMissing()
    {
        using var den = TempDen.Starfall();

        Assert.Equal(IndexStatus.Missing, IndexBuilder.CheckFiles(den.Root));
    }

    [Fact]
    public void AnEditedFileMakesItStale()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);

        File.AppendAllText(Path.Combine(den.Root, Mara), "\nOne more line.\n");

        Assert.Equal(IndexStatus.FilesChanged, IndexBuilder.CheckFiles(den.Root));
        // Check alone doesn't look at the files, as before.
        Assert.Equal(IndexStatus.Current, IndexBuilder.Check(den.Root));
    }

    [Theory]
    [InlineData("universes/starfall/entities/locations/new-place.md")]
    [InlineData("universes/starfall/series/long-dark/books/new-book/book.md")]
    [InlineData("packs/mine/pack.yaml")]
    public void ANewFileMakesItStale(string path)
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);

        den.Write(path, "---\nname: New\n---\n");

        Assert.Equal(IndexStatus.FilesChanged, IndexBuilder.CheckFiles(den.Root));
    }

    [Fact]
    public void ADeletedFileMakesItStale()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);

        File.Delete(Path.Combine(den.Root, Mara));

        Assert.Equal(IndexStatus.FilesChanged, IndexBuilder.CheckFiles(den.Root));
    }

    [Fact]
    public void ANewEmptyFolderMakesItStale()
    {
        // A container folder without its container file is an error (F-LAY-4), so it matters.
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);

        Directory.CreateDirectory(Path.Combine(den.Root, "universes", "empty"));

        Assert.Equal(IndexStatus.FilesChanged, IndexBuilder.CheckFiles(den.Root));
    }

    [Theory]
    [InlineData(".git/HEAD")]
    [InlineData(".obsidian/workspace.md")]
    [InlineData("universes/starfall/entities/locations/.draft.md")]
    public void HiddenFilesDontMakeItStale(string path)
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);

        den.Write(path, "anything\n");

        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
    }

    [Fact]
    public void AnImageDoesntMakeItStale()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);
        var image = Path.Combine(den.Root, "universes", "starfall", "entities", "locations", "map.png");

        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47]);

        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-6")]
    public void EnsureUpToDateRebuildsAStaleIndexAndThenItIsCurrent()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);
        den.Write(Mara, File.ReadAllText(Path.Combine(den.Root, Mara)).Replace("name: Mara Voss", "name: Mara Ellery", StringComparison.Ordinal));

        Assert.Equal(IndexStatus.FilesChanged, IndexBuilder.EnsureUpToDate(den.Root));

        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
        using var index = DenIndex.Open(den.Root);
        Assert.Equal("Mara Ellery", index.FindEntity("01JA7Q8M4XK3V9R2D5W8HN6TQB")!.Name);
    }

    [Fact]
    public void EnsureUpToDateLeavesACurrentIndexAlone()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);
        var written = File.GetLastWriteTimeUtc(den.IndexPath);

        Assert.Equal(IndexStatus.Current, IndexBuilder.EnsureUpToDate(den.Root));

        Assert.Equal(written, File.GetLastWriteTimeUtc(den.IndexPath));
    }
}
