using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>
/// How the den store's watch turns a <see cref="FileSystemWatcher"/> event
/// into a <see cref="DenChange"/> (<see cref="FileSystemDenStore.ToDenChange"/>,
/// #121), without a real event. The index's watcher batches what comes out.
/// </summary>
public sealed class DenChangeMappingTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wolfden-mapping", "den");
    private static readonly string Outside = Path.Combine(Path.GetTempPath(), "wolfden-mapping", "elsewhere");

    private static string Full(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

    private static DenChange? Map(WatcherChangeTypes kind, string full, string? old = null) =>
        FileSystemDenStore.ToDenChange(Root, kind, full, old);

    [Theory]
    [InlineData(WatcherChangeTypes.Created, DenChangeKind.Created)]
    [InlineData(WatcherChangeTypes.Changed, DenChangeKind.Changed)]
    [InlineData(WatcherChangeTypes.Deleted, DenChangeKind.Deleted)]
    public void EachKindMapsWithADenPath(WatcherChangeTypes kind, DenChangeKind expected)
    {
        Assert.Equal(new DenChange(expected, "universes/vael/universe.md"), Map(kind, Full("universes/vael/universe.md")));
    }

    [Fact]
    public void ARenameKeepsBothPaths()
    {
        Assert.Equal(
            new DenChange(DenChangeKind.Renamed, "notes/plan.md", "notes/.plan.md.swp"),
            Map(WatcherChangeTypes.Renamed, Full("notes/plan.md"), Full("notes/.plan.md.swp")));
    }

    [Fact]
    public void ARenameFromOutsideTheDenIsACreation()
    {
        Assert.Equal(
            new DenChange(DenChangeKind.Created, "notes/plan.md"),
            Map(WatcherChangeTypes.Renamed, Full("notes/plan.md"), Path.Combine(Outside, "plan.md")));
    }

    [Fact]
    public void ARenameToOutsideTheDenIsADeletion()
    {
        Assert.Equal(
            new DenChange(DenChangeKind.Deleted, "notes/plan.md"),
            Map(WatcherChangeTypes.Renamed, Path.Combine(Outside, "plan.md"), Full("notes/plan.md")));
    }

    [Fact]
    public void ARenameWithoutAnOldPathIsACreation()
    {
        Assert.Equal(new DenChange(DenChangeKind.Created, "notes/plan.md"), Map(WatcherChangeTypes.Renamed, Full("notes/plan.md")));
    }

    [Fact]
    public void AnOldPathOnAnythingButARenameIsIgnored()
    {
        Assert.Equal(new DenChange(DenChangeKind.Changed, "notes/plan.md"), Map(WatcherChangeTypes.Changed, Full("notes/plan.md"), Full("notes/old.md")));
    }

    [Fact]
    public void TheRootAndPathsOutsideTheDenAreNothing()
    {
        Assert.Null(Map(WatcherChangeTypes.Changed, Root));
        Assert.Null(Map(WatcherChangeTypes.Created, Path.Combine(Outside, "x.md")));
        Assert.Null(Map(WatcherChangeTypes.Renamed, Path.Combine(Outside, "x.md"), Path.Combine(Outside, "y.md")));
    }

    [Theory]
    [InlineData("a/b.md", "a/b.md")]
    [InlineData("a/.b/c.md", "a/.b/c.md")]
    [InlineData(".wolfden", ".wolfden")]
    public void PathsUseSlashesAndHiddenOnesAreKeptForTheListener(string path, string expected)
    {
        Assert.Equal(expected, FileSystemDenStore.DenPath(Root, Full(path)));
    }

    [Fact]
    public void ABackslashIsPartOfANameOnLinux()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("On Windows a backslash separates names.");
        }

        Assert.Equal("a\\b.md", FileSystemDenStore.DenPath(Root, Path.Combine(Root, "a\\b.md")));
    }
}
