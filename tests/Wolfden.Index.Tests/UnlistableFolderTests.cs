using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Wolfden.Core.Checks;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;
using static Wolfden.Index.Tests.IncrementalUpdateTests;

namespace Wolfden.Index.Tests;

/// <summary>
/// A folder the user may not list is absent, never a crash (#116): the
/// check, the files fingerprint, a full rebuild, and the watcher all leave
/// it out, as if it were deleted, so an updated index stays the same as a
/// full rebuild, and an index built without it is stale once it can be
/// listed. The race of a folder deleted after its parent was listed is in
/// the Core tests, through the den source.
/// </summary>
public sealed class UnlistableFolderTests
{
    private const string Mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
    private const string Characters = "universes/starfall/series/long-dark/entities/characters";
    private const string Ember = "universes/starfall/series/long-dark/books/ember";

    [Theory]
    [InlineData(Characters)]
    [InlineData(Ember)]
    [InlineData("universes/starfall/series/long-dark")]
    [Trait("Scenario", "SC-CLI-9")]
    public void AFolderThatCantBeListedIsCheckedAndIndexedAsIfItWereDeleted(string folder)
    {
        using var den = TempDen.Starfall();
        using var deleted = TempDen.Starfall();
        Directory.Delete(Full(deleted.Root, folder), recursive: true);
        var all = Entities(den);

        using (Unlistable(Full(den.Root, folder)))
        {
            var source = new FileSystemDenSource(den.Root);
            var parent = folder[..folder.LastIndexOf('/')];
            Assert.DoesNotContain(folder[(parent.Length + 1)..], source.GetDirectories(parent));
            Assert.Empty(source.GetDirectories(folder));
            Assert.Empty(source.GetFiles(folder));
            Assert.Empty(source.GetFolderLinks(folder));

            Assert.Equal(Describe(DenCheck.Run(deleted.Root)), Describe(DenCheck.Run(den.Root)));
            Assert.Equal(Entities(deleted), Entities(den));
            Assert.Equal(IndexBuilder.FilesFingerprint(deleted.Root), IndexBuilder.FilesFingerprint(den.Root));

            var state = IndexBuilder.Rebuild(den.Root, beforeSwap: null);
            Assert.Equal(Entities(deleted), state.Catalog.Entities.Select(e => e.Path).ToList());
            Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
        }

        // Once it can be listed, the files no longer match what the index was built from.
        Assert.Equal(IndexStatus.FilesChanged, IndexBuilder.CheckFiles(den.Root));
        var after = IndexBuilder.Rebuild(den.Root, beforeSwap: null);
        Assert.Equal(all, after.Catalog.Entities.Select(e => e.Path).ToList());
        if (folder != Characters)
        {
            Assert.Contains(after.Structure.Universes.SelectMany(u => u.Series.Concat<Container>(u.Books)), c => c.Path == folder);
        }
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
    }

    [Theory]
    [InlineData(Characters)]
    [InlineData(Ember)]
    [Trait("Scenario", "SC-CLI-9")]
    public void AFolderTheLoadCantListAfterTheTreeWasTakenMakesTheIndexStale(string folder)
    {
        using var den = TempDen.Starfall();
        var tree = DenFileTree.Scan(den.Root);
        var fingerprint = tree.Fingerprint();
        Assert.True(tree.HasFolder(folder));

        using (Unlistable(Full(den.Root, folder)))
        {
            var state = IndexWriter.Load(den.Root, tree, fingerprint);

            Assert.False(state.Tree.HasFolder(folder));
            Assert.Empty(state.Tree.FilesUnder(folder));
            Assert.DoesNotContain(state.FileHashes.Keys, p => p.StartsWith(folder + "/", StringComparison.Ordinal));
            var recorded = state.Rows.Meta[IndexSchema.FilesFingerprintKey];
            Assert.NotEqual(fingerprint, recorded);
            Assert.Equal(DenFileTree.Scan(den.Root).Fingerprint(), recorded);
        }

        // Once it can be listed, the files no longer match what the index was built from.
        Assert.Equal(fingerprint, DenFileTree.Scan(den.Root).Fingerprint());
    }

    [Fact]
    public void AFileTheLoadDidntListAfterTheTreeWasTakenMakesTheIndexStaleOnceItsBack()
    {
        // The load finds the folder but not the file (deleted, or the folder
        // listed as empty by then), so it never tries to read it: the tree
        // mustn't keep it either.
        using var den = TempDen.Starfall();
        var tree = DenFileTree.Scan(den.Root);
        var fingerprint = tree.Fingerprint();
        var bytes = File.ReadAllBytes(Full(den.Root, Mara));
        File.Delete(Full(den.Root, Mara));

        var state = IndexWriter.Load(den.Root, tree, fingerprint);

        Assert.Null(state.Tree.Hash(Mara));
        Assert.True(state.Tree.HasFolder(Characters));
        Assert.Equal(DenFileTree.Scan(den.Root).Fingerprint(), state.Rows.Meta[IndexSchema.FilesFingerprintKey]);

        File.WriteAllBytes(Full(den.Root, Mara), bytes);
        Assert.Equal(fingerprint, DenFileTree.Scan(den.Root).Fingerprint());
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-8")]
    public void TheWatcherLeavesOutAnEntitiesFolderItCantList()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");

        using (Unlistable(Full(den.Root, Characters)))
        {
            var update = updater.Apply(Batch(den, (WatcherChangeTypes.Changed, Characters, null), (WatcherChangeTypes.Changed, Mara, null)), out var retry);

            Assert.Empty(retry);
            Assert.Equal(IndexUpdateKind.Updated, update!.Kind);
            Assert.DoesNotContain(updater.State!.Catalog.Entities, e => e.Path.StartsWith(Characters + "/", StringComparison.Ordinal));
            Assert.False(updater.State.Tree.HasFolder(Characters));
            AssertSameAsFullRebuild(den, "with the folder unlistable");

            // A folder made inside it can't be seen either.
            Assert.Null(updater.Apply(Batch(den, (WatcherChangeTypes.Created, $"{Characters}/crew", null)), out retry));
            Assert.Empty(retry);
        }

        // The folder's next event brings it back.
        var back = updater.Apply(Batch(den, (WatcherChangeTypes.Changed, Characters, null), (WatcherChangeTypes.Changed, Mara, null)), out _);
        Assert.Equal(IndexUpdateKind.Updated, back!.Kind);
        Assert.Contains(updater.State!.Catalog.Entities, e => e.Path == Mara);
        AssertSameAsFullRebuild(den, "once the folder can be listed");
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-8")]
    public void TheWatcherRebuildsWithoutAContainerFolderItCantList()
    {
        using var den = TempDen.Starfall();
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");

        using (Unlistable(Full(den.Root, Ember)))
        {
            var update = updater.Apply(Batch(den, (WatcherChangeTypes.Changed, Ember, null)), out var retry);

            Assert.Empty(retry);
            Assert.Equal(IndexUpdateKind.Rebuilt, update!.Kind);
            Assert.DoesNotContain(updater.State!.Structure.Universes.SelectMany(u => u.Books), b => b.Path == Ember);
            AssertSameAsFullRebuild(den, "with the book folder unlistable");
        }

        Assert.Equal(IndexUpdateKind.Rebuilt, updater.Apply(Batch(den, (WatcherChangeTypes.Changed, Ember, null)), out _)!.Kind);
        Assert.Contains(updater.State!.Structure.Universes.SelectMany(u => u.Books), b => b.Path == Ember);
        AssertSameAsFullRebuild(den, "once the book folder can be listed");
    }

    private static List<string> Describe(DenCheck check) =>
        check.Diagnostics.Select(d => $"{d.Path}|{d.Diagnostic.RuleId}|{d.Diagnostic.Line}|{d.Diagnostic.Message}").ToList();

    private static List<string> Entities(TempDen den) => EntityCatalog.Load(den.Root).Entities.Select(e => e.Path).ToList();

    /// <summary>
    /// Makes the folder at <paramref name="full"/> one the user may not list
    /// until disposed: a deny rule for listing on Windows, or no permissions
    /// elsewhere. Skips the test where that doesn't stop listing, as for
    /// root. Disposing it restores the permissions, so the temporary den can
    /// be deleted.
    /// </summary>
    internal static IDisposable Unlistable(string full)
    {
        var cleanup = OperatingSystem.IsWindows() ? DenyListing(full) : WithoutPermissions(full);
        if (!FileSystemDenSource.CanList(full))
        {
            return cleanup;
        }

        cleanup.Dispose();
        Assert.Skip("This user can list a folder without permission to list it.");
        return null!;
    }

    [SupportedOSPlatform("windows")]
    private static Cleanup DenyListing(string full)
    {
        var folder = new DirectoryInfo(full);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = folder.GetAccessControl();
        security.AddAccessRule(rule);
        folder.SetAccessControl(security);
        return new Cleanup(() =>
        {
            var restored = folder.GetAccessControl();
            restored.RemoveAccessRuleSpecific(rule);
            folder.SetAccessControl(restored);
        });
    }

    [UnsupportedOSPlatform("windows")]
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
