using System.Diagnostics;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;

namespace Wolfden.Index.Tests;

/// <summary>
/// Folder links (symbolic links, and junctions on Windows) are skipped by
/// every walk of a den: the loader's, the files fingerprint's, and the
/// watcher's, so links that loop can't hang a load or a query (#102).
/// </summary>
public sealed class FolderLinkTests
{
    private const string Locations = "universes/starfall/entities/locations";

    [Fact]
    public void LinksThatLoopAreSkippedByTheLoaderAndTheIndex()
    {
        using var den = TempDen.Starfall();
        var entities = EntityCatalog.Load(den.Root).Entities.Select(e => e.Path).ToList();
        var fingerprint = IndexBuilder.FilesFingerprint(den.Root);

        // Two links that loop back up the tree: followed, they'd branch without end.
        using var loopA = LinkFolder(Path.Combine(den.Root, Full(Locations), "loop-a"), Path.Combine(den.Root, Full("universes/starfall/entities")));
        using var loopB = LinkFolder(Path.Combine(den.Root, Full(Locations), "loop-b"), Path.Combine(den.Root, Full("universes/starfall")));

        Assert.Equal(entities, EntityCatalog.Load(den.Root).Entities.Select(e => e.Path));
        Assert.Equal(fingerprint, IndexBuilder.FilesFingerprint(den.Root));
        IndexBuilder.Rebuild(den.Root);
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
    }

    [Fact]
    public void ANoteReachedOnlyThroughALinkIsNotRead()
    {
        using var den = TempDen.Starfall();
        using var elsewhere = TempDen.FromFiles(new Dictionary<string, string>
        {
            ["shared/the-drowned-gate.md"] = "---\nid: 01JA7Q8M4XK3V9R2D5W8HN6TQZ\ntype: location\nname: The Drowned Gate\n---\n",
        });
        var fingerprint = IndexBuilder.FilesFingerprint(den.Root);
        var link = Path.Combine(den.Root, Full(Locations), "shared");
        using var linked = LinkFolder(link, Path.Combine(elsewhere.Root, "shared"));

        Assert.DoesNotContain(EntityCatalog.Load(den.Root).Entities, e => e.Name == "The Drowned Gate");
        Assert.False(new FileSystemDenSource(den.Root).FileExists($"{Locations}/shared/the-drowned-gate.md"));
        Assert.True(FileSystemDenSource.IsFolderLink(new DirectoryInfo(link)));
        Assert.Equal(fingerprint, IndexBuilder.FilesFingerprint(den.Root));
    }

    [Fact]
    public void AnOrdinaryFolderIsNotALink()
    {
        using var den = TempDen.Starfall();

        Assert.False(FileSystemDenSource.IsFolderLink(new DirectoryInfo(Path.Combine(den.Root, Full(Locations)))));
    }

    [Fact]
    public void TheWatcherSkipsALinkedFolderToo()
    {
        using var den = TempDen.Starfall();
        using var elsewhere = TempDen.FromFiles(new Dictionary<string, string>
        {
            ["shared/the-drowned-gate.md"] = "---\nid: 01JA7Q8M4XK3V9R2D5W8HN6TQZ\ntype: location\nname: The Drowned Gate\n---\n",
        });
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        using var linked = LinkFolder(Path.Combine(den.Root, Full(Locations), "shared"), Path.Combine(elsewhere.Root, "shared"));

        var update = updater.Apply(
            IncrementalUpdateTests.Batch(den, (WatcherChangeTypes.Created, $"{Locations}/shared", null), (WatcherChangeTypes.Created, $"{Locations}/shared/the-drowned-gate.md", null)),
            out _);

        Assert.Null(update);
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
        IncrementalUpdateTests.AssertSameAsFullRebuild(den, "after the link");
    }

    private static string Full(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// Makes <paramref name="link"/> a link to the folder <paramref name="target"/>:
    /// a symbolic link where the OS allows it, else a junction on Windows,
    /// else the test is skipped. Disposing it removes the link, not what it
    /// links to, before the temporary dens are deleted.
    /// </summary>
    private static Link LinkFolder(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return new Link(link);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows without developer mode or admin rights; try a junction, which needs neither.
        }

        if (OperatingSystem.IsWindows())
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            mklink.WaitForExit();
            if (mklink.ExitCode == 0 && Directory.Exists(link))
            {
                return new Link(link);
            }
        }

        Assert.Skip("This system doesn't allow folder links to be created.");
        return null!;
    }

    private sealed class Link(string path) : IDisposable
    {
        public void Dispose() => Directory.Delete(path);
    }
}
