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
            Assert.False(source.FolderExists(folder));
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
    [InlineData("packs/spare", "packs/spare/readme.md")]
    [InlineData("packs/Spare", "packs/Spare/pack.yaml")]
    [InlineData("notes", "notes/idea.md")]
    [InlineData("universes/starfall/books/Odd", "universes/starfall/books/Odd/book.md")]
    [Trait("Scenario", "SC-CLI-9")]
    public void AFolderThatCantBeListedIsntWarnedAboutByName(string folder, string file)
    {
        // Folders the check reports by name alone: a pack folder without a
        // pack.yaml or with an invalid key, the reserved notes/, and a
        // container folder with an invalid key.
        using var den = TempDen.Starfall();
        using var deleted = TempDen.Starfall();
        den.Write(file, "---\nname: Spare\n---\n");
        var listable = Describe(DenCheck.Run(den.Root));
        Assert.Contains(listable, d => d.StartsWith(folder + "|", StringComparison.Ordinal));
        File.Delete(Full(den.Root, file));

        using (Unlistable(Full(den.Root, folder)))
        {
            Assert.Equal(Describe(DenCheck.Run(deleted.Root)), Describe(DenCheck.Run(den.Root)));
            Assert.Equal(IndexBuilder.FilesFingerprint(deleted.Root), IndexBuilder.FilesFingerprint(den.Root));
        }
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

            // An entities folder stays in the tree, but entities folders aren't in the fingerprint.
            Assert.Equal(folder == Characters, state.Tree.HasFolder(folder));
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

    [Theory]
    [InlineData("universes/starfall/series", "long-dark")]
    [InlineData("universes/starfall/books", "a-quiet-moon")]
    [Trait("Scenario", "SC-CLI-9")]
    public void AFolderDeletedAfterTheLoadReadItLeavesTheIndexStale(string parent, string name)
    {
        // The loader lists a universe's series/ and books/ twice: once to
        // load them and once for the order. A folder deleted between the
        // two was read, so the files fingerprint must still count it, and
        // the index looks stale against the den without it.
        using var den = TempDen.Starfall();
        var tree = DenFileTree.Scan(den.Root);
        var racing = new DeletingSource(new FileSystemDenSource(den.Root), parent, name, Full(den.Root, $"{parent}/{name}"));

        var state = IndexWriter.Load(den.Root, racing, tree, tree.Fingerprint());

        Assert.True(racing.Deleted);
        Assert.Contains(state.Structure.Universes.SelectMany(u => u.Series.Concat<Container>(u.Books)), c => c.Path == $"{parent}/{name}");
        Assert.True(state.Tree.HasFolder($"{parent}/{name}"));
        Assert.NotEqual(DenFileTree.Scan(den.Root).Fingerprint(), state.Rows.Meta[IndexSchema.FilesFingerprintKey]);
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-8")]
    public void TheWatcherLeavesOutAFolderItCantListWhenAFileInItChanges()
    {
        // Saving a note in place, or adding one, gives the note an event but
        // not its folder: the watcher must still drop the whole folder.
        using var den = TempDen.Starfall();
        const string ilya = $"{Characters}/ilya-brand.md";
        const string added = $"{Characters}/zed-amar.md";
        den.Write(ilya, "---\ntype: character\nname: Ilya Brand\n---\n\nSails with [[Mara Voss]].\n");
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        Assert.Contains(updater.State!.Catalog.Entities, e => e.Path == ilya);

        File.AppendAllText(Full(den.Root, Mara), "\nA line added in place.\n");
        den.Write(added, "---\ntype: character\nname: Zed Amar\n---\n");
        using (Unlistable(Full(den.Root, Characters)))
        {
            var update = updater.Apply(Batch(den, (WatcherChangeTypes.Changed, Mara, null)), out var retry);

            Assert.Empty(retry);
            Assert.Equal(IndexUpdateKind.Updated, update!.Kind);
            Assert.DoesNotContain(updater.State!.Catalog.Entities, e => e.Path.StartsWith(Characters + "/", StringComparison.Ordinal));
            AssertSameAsFullRebuild(den, "after a note in the unlistable folder changed");

            Assert.Null(updater.Apply(Batch(den, (WatcherChangeTypes.Created, added, null)), out retry));
            Assert.Empty(retry);
            AssertSameAsFullRebuild(den, "after a note was added to the unlistable folder");
        }

        // Once it can be listed, the next event for a note in it brings back the whole folder.
        var back = updater.Apply(Batch(den, (WatcherChangeTypes.Changed, added, null)), out _);
        Assert.Equal(IndexUpdateKind.Updated, back!.Kind);
        Assert.Contains(updater.State!.Catalog.Entities, e => e.Path == ilya);
        Assert.Contains(updater.State.Catalog.Entities, e => e.Path == Mara);
        AssertSameAsFullRebuild(den, "once the folder can be listed");
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

    /// <summary>
    /// A den source that deletes the folder <paramref name="name"/> in
    /// <paramref name="parent"/> on disk just before the second listing of
    /// <paramref name="parent"/>, after the first listing let the load read it.
    /// </summary>
    private sealed class DeletingSource(IDenSource inner, string parent, string name, string full) : IDenSource
    {
        private int _listings;

        public bool Deleted { get; private set; }

        public bool FileExists(string path) => inner.FileExists(path);

        public byte[] ReadFile(string path) => inner.ReadFile(path);

        public IEnumerable<string> GetDirectories(string path)
        {
            if (path == parent && ++_listings == 2)
            {
                Directory.Delete(full, recursive: true);
                Deleted = true;
            }

            var names = inner.GetDirectories(path).ToList();
            Assert.True(path != parent || _listings != 1 || names.Contains(name));
            return names;
        }

        public IEnumerable<string> GetFiles(string path) => inner.GetFiles(path);

        public IEnumerable<string> GetFolderLinks(string path) => inner.GetFolderLinks(path);

        public bool FolderExists(string path) => inner.FolderExists(path);
    }

    private sealed class Cleanup(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
