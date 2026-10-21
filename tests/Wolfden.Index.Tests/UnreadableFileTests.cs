using Wolfden.Core.Checks;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;
using static Wolfden.Index.Tests.IncrementalUpdateTests;

namespace Wolfden.Index.Tests;

/// <summary>
/// A file the den lists but can't read is absent, never a crash (#112):
/// a link to a file that's gone or that loops, a file another program
/// holds open (Windows) or the user may not read. The check, the files
/// fingerprint, a full rebuild, and the watcher all agree on that, so an
/// updated index stays the same as a full rebuild. The race of a file
/// deleted between the listing and the read is in the Core tests, through
/// the den source.
/// </summary>
public sealed class UnreadableFileTests
{
    private const string Mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
    private const string Locations = "universes/starfall/entities/locations";

    [Fact]
    [Trait("Scenario", "SC-CLI-8")]
    public void BrokenAndLoopingFileLinksAreAbsentToCheckAndTheIndex()
    {
        using var den = TempDen.Starfall();
        var check = Describe(DenCheck.Run(den.Root));
        var entities = Entities(den);
        var fingerprint = IndexBuilder.FilesFingerprint(den.Root);

        using var gone = LinkFile(Full(den.Root, $"{Locations}/gone.md"), Full(den.Root, "nowhere/gone.md"));
        using var loopA = LinkFile(Full(den.Root, $"{Locations}/loop-a.md"), Full(den.Root, $"{Locations}/loop-b.md"));
        using var loopB = LinkFile(Full(den.Root, $"{Locations}/loop-b.md"), Full(den.Root, $"{Locations}/loop-a.md"));

        var source = new FileSystemDenSource(den.Root);
        foreach (var name in new[] { "gone.md", "loop-a.md", "loop-b.md" })
        {
            Assert.True(FileSystemDenSource.IsBrokenLink(new FileInfo(Full(den.Root, $"{Locations}/{name}"))), name);
            Assert.False(source.FileExists($"{Locations}/{name}"), name);
            Assert.Null(((IDenSource)source).TryReadFile($"{Locations}/{name}"));
        }

        Assert.Equal(["kessan-reach.md"], source.GetFiles(Locations));
        Assert.Equal(check, Describe(DenCheck.Run(den.Root)));
        Assert.Equal(entities, Entities(den));
        Assert.Equal(fingerprint, IndexBuilder.FilesFingerprint(den.Root));
        IndexBuilder.Rebuild(den.Root);
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
    }

    [Fact]
    public void ALinkToAFileThatIsThereIsReadAsThatFile()
    {
        using var den = TempDen.Starfall();
        var moved = Full(den.Root, "elsewhere/mara-voss.md");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(Full(den.Root, Mara), moved);
        using var link = LinkFile(Full(den.Root, Mara), moved);

        Assert.False(FileSystemDenSource.IsBrokenLink(new FileInfo(Full(den.Root, Mara))));
        Assert.Contains(Mara, Entities(den));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-8")]
    public void ANoteReplacedByABrokenLinkIsCheckedAndIndexedAsIfItWereDeleted()
    {
        using var den = TempDen.Starfall();
        using var deleted = TempDen.Starfall();
        File.Delete(Full(deleted.Root, Mara));
        File.Delete(Full(den.Root, Mara));
        using var link = LinkFile(Full(den.Root, Mara), Full(den.Root, "nowhere/mara-voss.md"));

        Assert.Equal(Describe(DenCheck.Run(deleted.Root)), Describe(DenCheck.Run(den.Root)));
        Assert.Equal(Entities(deleted), Entities(den));
        Assert.Equal(IndexBuilder.FilesFingerprint(deleted.Root), IndexBuilder.FilesFingerprint(den.Root));
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-7")]
    public void TheWatcherTreatsABrokenLinkAsAbsentAtOnce()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");

        File.Delete(Full(den.Root, Mara));
        using var link = LinkFile(Full(den.Root, Mara), Full(den.Root, "nowhere/mara-voss.md"));
        var update = updater.Apply(Batch(den, (WatcherChangeTypes.Deleted, Mara, null), (WatcherChangeTypes.Created, Mara, null)), out var retry);

        Assert.Empty(retry);
        Assert.Equal(IndexUpdateKind.Updated, update!.Kind);
        Assert.DoesNotContain(updater.State!.Catalog.Entities, e => e.Path == Mara);
        AssertSameAsFullRebuild(den, "after the note became a broken link");

        using var gone = LinkFile(Full(den.Root, $"{Locations}/gone.md"), Full(den.Root, "nowhere/gone.md"));
        Assert.Null(updater.Apply(Batch(den, (WatcherChangeTypes.Created, $"{Locations}/gone.md", null)), out retry));
        Assert.Empty(retry);
        AssertSameAsFullRebuild(den, "after a new broken link");
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-8")]
    public void ANoteThatCantBeReadIsLeftOutAndTheIndexIsStaleOnceItCanBe()
    {
        using var den = TempDen.Starfall();
        IndexBuilder.Rebuild(den.Root);

        using (Unreadable(Full(den.Root, Mara)))
        {
            Assert.DoesNotContain(Mara, Entities(den));
            Assert.DoesNotContain(DenCheck.Run(den.Root).Catalog.Entities, e => e.Path == Mara);
            Assert.Equal(IndexStatus.FilesChanged, IndexBuilder.CheckFiles(den.Root));

            var state = IndexBuilder.Rebuild(den.Root, beforeSwap: null);

            Assert.DoesNotContain(state.Catalog.Entities, e => e.Path == Mara);
            Assert.Null(state.Tree.Hash(Mara));
            Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
        }

        Assert.Equal(IndexStatus.FilesChanged, IndexBuilder.CheckFiles(den.Root));
        Assert.Contains(IndexBuilder.Rebuild(den.Root, beforeSwap: null).Catalog.Entities, e => e.Path == Mara);
    }

    [Fact]
    public void AFileTheLoadCantReadAfterTheTreeWasTakenMakesTheIndexStale()
    {
        using var den = TempDen.Starfall();
        var tree = DenFileTree.Scan(den.Root);
        var fingerprint = tree.Fingerprint();
        Assert.NotNull(tree.Hash(Mara));

        using (Unreadable(Full(den.Root, Mara)))
        {
            var state = IndexWriter.Load(den.Root, tree, fingerprint);

            Assert.DoesNotContain(state.Catalog.Entities, e => e.Path == Mara);
            Assert.Null(state.Tree.Hash(Mara));
            Assert.DoesNotContain(Mara, state.FileHashes.Keys);
            var recorded = state.Rows.Meta[IndexSchema.FilesFingerprintKey];
            Assert.NotEqual(fingerprint, recorded);
            Assert.Equal(DenFileTree.Scan(den.Root).Fingerprint(), recorded);
        }

        // Once it can be read, the files no longer match what the index was built from.
        Assert.Equal(fingerprint, DenFileTree.Scan(den.Root).Fingerprint());
    }

    private static List<string> Describe(DenCheck check) =>
        check.Diagnostics.Select(d => $"{d.Path}|{d.Diagnostic.RuleId}|{d.Diagnostic.Line}|{d.Diagnostic.Message}").ToList();

    private static List<string> Entities(TempDen den) => EntityCatalog.Load(den.Root).Entities.Select(e => e.Path).ToList();

    /// <summary>
    /// Makes <paramref name="link"/> a symbolic link to the file
    /// <paramref name="target"/>, which needn't exist, or skips the test
    /// where the OS or user can't make one (Windows without developer
    /// mode). Disposing it removes the link, not what it links to.
    /// </summary>
    internal static IDisposable LinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This system doesn't allow file links to be created.");
        }

        return new Cleanup(() => File.Delete(link));
    }

    /// <summary>
    /// Makes the file at <paramref name="full"/> unreadable until disposed:
    /// held open with no sharing on Windows, or with no permissions
    /// elsewhere. Skips the test where that doesn't stop reads, as for root.
    /// </summary>
    internal static IDisposable Unreadable(string full)
    {
        if (OperatingSystem.IsWindows())
        {
            var stream = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return new Cleanup(stream.Dispose);
        }

        var cleanup = WithoutPermissions(full);
        try
        {
            File.ReadAllBytes(full);
        }
        catch (UnauthorizedAccessException)
        {
            return cleanup;
        }

        cleanup.Dispose();
        Assert.Skip("This user can read a file without read permission.");
        return null!;
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static Cleanup WithoutPermissions(string full)
    {
        var mode = File.GetUnixFileMode(full);
        File.SetUnixFileMode(full, UnixFileMode.None);
        return new Cleanup(() => File.SetUnixFileMode(full, mode));
    }

    private sealed class Cleanup(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
