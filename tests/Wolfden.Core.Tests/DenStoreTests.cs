using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;

namespace Wolfden.Core.Tests;

/// <summary>
/// The den store's write half on disk (<see cref="FileSystemDenStore"/>,
/// #121): atomic writes that keep bytes exactly, paths that can't leave
/// the den, names that are one file on every OS, and read-only stores that
/// refuse writes. Each test works in a fresh copy of the Starfall den.
/// </summary>
public sealed class DenStoreTests : IDisposable
{
    private const string Mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
    private const string Characters = "universes/starfall/series/long-dark/entities/characters";

    private readonly string _root;
    private readonly string _outside;

    public DenStoreTests()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wolfden-core-tests", "store-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(folder, "den");
        _outside = Path.Combine(folder, "outside");
        Directory.CreateDirectory(_outside);
        foreach (var file in Directory.EnumerateFiles(MemoryDenSource.StarfallRoot, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(_root, Path.GetRelativePath(MemoryDenSource.StarfallRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temporary folder left behind is harmless.
        }
    }

    private FileSystemDenStore Store() => new(_root);

    private string Full(string path) => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // ----- writing -----

    public static TheoryData<string> ExactBytes() => new()
    {
        "bom-crlf-no-final-newline.md",
        "crlf-entity.md",
        "invalid-utf8.md",
        "lone-cr.md",
        "mixed-endings.md",
        "no-final-newline.md",
    };

    [Theory]
    [MemberData(nameof(ExactBytes))]
    [Trait("Scenario", "SC-STORE-1")]
    public void AWriteKeepsTheBytesExactlyAsGiven(string fixture)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestFiles.RepoRoot, "tests", "Wolfden.Core.Tests", "Fixtures", fixture));
        var store = Store();

        store.WriteFile(Mara, bytes);

        Assert.Equal(bytes, File.ReadAllBytes(Full(Mara)));
        Assert.Equal(bytes, store.ReadFile(Mara));
    }

    [Fact]
    public void AnEmptyFileCanBeWritten()
    {
        var store = Store();

        store.WriteFile(Mara, []);

        Assert.Empty(File.ReadAllBytes(Full(Mara)));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-1")]
    public void AWriteLeavesNoTemporaryFileBehind()
    {
        var store = Store();
        var before = Directory.GetFileSystemEntries(Full(Characters)).Order(StringComparer.Ordinal).ToList();

        store.WriteFile(Mara, Bytes("---\nname: Mara Voss\n---\n"));
        store.WriteFile($"{Characters}/wren-ashby.md", Bytes("---\nname: Wren Ashby\n---\n"), DenWriteMode.Create);

        var after = Directory.GetFileSystemEntries(Full(Characters)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([.. before, Full($"{Characters}/wren-ashby.md")], after.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-1")]
    public void AWriteThatFailsPartWayLeavesTheOldFileAndNoTemporaryFile()
    {
        var old = File.ReadAllBytes(Full(Mara));
        string? temp = null;
        var store = new FileSystemDenStore(_root)
        {
            BeforeReplace = t =>
            {
                temp = t;
                throw new IOException("The power went out.");
            },
        };

        var e = Assert.Throws<IOException>(() => store.WriteFile(Mara, Bytes("half a note")));

        Assert.Equal("The power went out.", e.Message);
        Assert.Equal(old, File.ReadAllBytes(Full(Mara)));
        Assert.NotNull(temp);
        Assert.StartsWith(FileSystemDenStore.TempPrefix, Path.GetFileName(temp), StringComparison.Ordinal);
        Assert.False(File.Exists(temp));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-1")]
    public void AReaderSeesTheOldFileOrTheNewOneNeverPartOfEither()
    {
        // Two versions of different lengths, each all one letter, so a torn
        // read (part of one, part of the other, or cut short) shows.
        var a = Bytes(new string('a', 300_000));
        var b = Bytes(new string('b', 200_000));
        var store = Store();
        store.WriteFile(Mara, a);
        var torn = new ConcurrentBag<string>();
        var reads = 0;
        var stop = 0;

        var reader = new Thread(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                byte[] seen;
                try
                {
                    seen = File.ReadAllBytes(Full(Mara));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Windows: caught mid-replace. Not a torn read.
                    Thread.Sleep(1);
                    continue;
                }

                Interlocked.Increment(ref reads);
                if (!seen.AsSpan().SequenceEqual(a) && !seen.AsSpan().SequenceEqual(b))
                {
                    torn.Add($"{seen.Length} bytes");
                }

                Thread.Sleep(1);
            }
        });
        reader.Start();

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 60; i++)
        {
            store.WriteFile(Mara, i % 2 == 0 ? b : a, DenWriteMode.Replace);
        }

        Volatile.Write(ref stop, 1);
        reader.Join();
        Assert.Empty(torn);
        Assert.True(reads > 0, $"The reader never read the file in {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void MissingFoldersOnTheWayAreMade()
    {
        var store = Store();
        const string ship = "universes/starfall/entities/ships/kestrel.md";

        store.WriteFile(ship, Bytes("---\ntype: ship\nname: Kestrel\n---\n"), DenWriteMode.Create);

        Assert.True(store.FileExists(ship));
        Assert.Equal(["kestrel.md"], store.GetFiles("universes/starfall/entities/ships"));
    }

    [Fact]
    public void CreateRefusesAFileThatsThereAndLeavesItAlone()
    {
        var old = File.ReadAllBytes(Full(Mara));

        var e = Assert.Throws<DenStoreException>(() => Store().WriteFile(Mara, Bytes("new"), DenWriteMode.Create));

        Assert.Equal(DenStoreError.AlreadyExists, e.Error);
        Assert.Equal(old, File.ReadAllBytes(Full(Mara)));
    }

    [Fact]
    public void ReplaceRefusesAFileThatIsntThereAndMakesNothing()
    {
        const string wren = $"{Characters}/wren-ashby.md";

        var e = Assert.Throws<DenStoreException>(() => Store().WriteFile(wren, Bytes("new"), DenWriteMode.Replace));

        Assert.Equal(DenStoreError.NotFound, e.Error);
        Assert.False(File.Exists(Full(wren)));
    }

    [Fact]
    public void AFolderIsntWrittenAsAFileAndAFileIsntAFolder()
    {
        var store = Store();

        Assert.Equal(DenStoreError.NotAFile, Assert.Throws<DenStoreException>(() => store.WriteFile(Characters, Bytes("x"))).Error);
        Assert.Equal(DenStoreError.NotAFile, Assert.Throws<DenStoreException>(() => store.WriteFile("den.yaml/x.md", Bytes("x"))).Error);
        Assert.Equal(DenStoreError.NotAFile, Assert.Throws<DenStoreException>(() => store.DeleteFile(Characters)).Error);
        Assert.True(Directory.Exists(Full(Characters)));
    }

    // ----- paths that would leave the den -----

    [Theory]
    [Trait("Scenario", "SC-STORE-2")]
    [InlineData("../outside/escaped.md")]
    [InlineData("universes/../../outside/escaped.md")]
    [InlineData("..\\outside\\escaped.md")]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".wolfden/index.db")]
    public void AWriteOutsideTheDenOrIntoAHiddenFolderIsRefusedAndTouchesNothing(string path)
    {
        var store = Store();

        var e = Assert.Throws<DenStoreException>(() => store.WriteFile(path, Bytes("escaped")));

        Assert.Equal(DenStoreError.InvalidPath, e.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".wolfden")));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-2")]
    public void AnAbsolutePathIsRefused()
    {
        var absolute = Path.Combine(_outside, "escaped.md").Replace('\\', '/');

        var e = Assert.Throws<DenStoreException>(() => Store().WriteFile(absolute, Bytes("escaped")));

        Assert.Equal(DenStoreError.InvalidPath, e.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-2")]
    public void EveryWriteChecksItsPaths()
    {
        var store = Store();
        const string escape = "../outside/escaped.md";
        File.WriteAllText(Path.Combine(_outside, "keep.md"), "keep");

        Assert.Equal(DenStoreError.InvalidPath, Assert.Throws<DenStoreException>(() => store.DeleteFile("../outside/keep.md")).Error);
        Assert.Equal(DenStoreError.InvalidPath, Assert.Throws<DenStoreException>(() => store.MoveFile(Mara, escape)).Error);
        Assert.Equal(DenStoreError.InvalidPath, Assert.Throws<DenStoreException>(() => store.MoveFile("../outside/keep.md", Mara)).Error);
        Assert.Equal(DenStoreError.InvalidPath, Assert.Throws<DenStoreException>(() => store.MoveFolder(Characters, "../outside/characters")).Error);
        Assert.Equal(DenStoreError.InvalidPath, Assert.Throws<DenStoreException>(() => store.DeleteFolder("../outside")).Error);
        Assert.Equal(DenStoreError.InvalidPath, Assert.Throws<DenStoreException>(() => store.DeleteFolder("")).Error);

        Assert.Equal(["keep.md"], Directory.EnumerateFileSystemEntries(_outside).Select(Path.GetFileName));
        Assert.True(File.Exists(Full(Mara)));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-2")]
    public void AWriteNeverGoesThroughAFolderLink()
    {
        File.WriteAllText(Path.Combine(_outside, "keep.md"), "keep");
        using var link = LinkFolder(Full("universes/starfall/shared"), _outside);
        var store = Store();

        var write = Assert.Throws<DenStoreException>(() => store.WriteFile("universes/starfall/shared/escaped.md", Bytes("escaped")));
        var delete = Assert.Throws<DenStoreException>(() => store.DeleteFile("universes/starfall/shared/keep.md"));
        var moveOut = Assert.Throws<DenStoreException>(() => store.MoveFile("universes/starfall/shared/keep.md", $"{Characters}/keep.md"));
        var deleteLink = Assert.Throws<DenStoreException>(() => store.DeleteFolder("universes/starfall/shared"));

        Assert.All([write, delete, moveOut, deleteLink], e => Assert.Equal(DenStoreError.InvalidPath, e.Error));
        Assert.Contains("folder link", write.Message, StringComparison.Ordinal);
        Assert.Equal(["keep.md"], Directory.EnumerateFileSystemEntries(_outside).Select(Path.GetFileName));
    }

    // ----- names that differ only in case -----

    [Fact]
    [Trait("Scenario", "SC-STORE-3")]
    public void ANameThatDiffersOnlyInCaseIsRefusedOnEveryOs()
    {
        var store = Store();
        var old = File.ReadAllBytes(Full(Mara));

        var file = Assert.Throws<DenStoreException>(() => store.WriteFile($"{Characters}/Mara-Voss.md", Bytes("another Mara")));
        var folder = Assert.Throws<DenStoreException>(() => store.WriteFile("universes/Starfall/entities/notes/x.md", Bytes("x")));
        var moved = Assert.Throws<DenStoreException>(() => store.MoveFile("universes/starfall/entities/locations/kessan-reach.md", $"{Characters}/MARA-VOSS.md"));

        Assert.All([file, folder, moved], e => Assert.Equal(DenStoreError.CaseConflict, e.Error));
        Assert.Contains($"'{Mara}' is already there", file.Message, StringComparison.Ordinal);
        Assert.Contains("'universes/starfall' is already there", folder.Message, StringComparison.Ordinal);
        Assert.Equal(old, File.ReadAllBytes(Full(Mara)));
        Assert.Equal(["mara-voss.md"], store.GetFiles(Characters));
        Assert.Equal(["starfall"], store.GetDirectories("universes").Where(d => d.Equals("starfall", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-3")]
    public void AWindowsShortNameIsAnotherNameAndIsRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Short names are a Windows file system feature.");
        }

        var full = Full(Characters);
        var shortFull = ShortPath(full);
        var shortName = Path.GetFileName(shortFull);
        if (string.Equals(shortName, Path.GetFileName(full), StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("This volume doesn't make short names.");
        }

        var e = Assert.Throws<DenStoreException>(() =>
            Store().WriteFile($"universes/starfall/series/long-dark/entities/{shortName}/mara-voss.md", Bytes("through a short name")));

        Assert.Equal(DenStoreError.CaseConflict, e.Error);
        Assert.Contains("another name", e.Message, StringComparison.Ordinal);
    }

    // ----- moving, renaming, deleting -----

    [Fact]
    [Trait("Scenario", "SC-STORE-5")]
    public void AFileIsMovedOrRenamedWithItsBytes()
    {
        var store = Store();
        var bytes = File.ReadAllBytes(Full(Mara));
        const string to = "universes/starfall/entities/characters/mara-voss.md";

        store.MoveFile(Mara, to);

        Assert.False(store.FileExists(Mara));
        Assert.Equal(bytes, store.ReadFile(to));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-5")]
    public void ARenameThatChangesOnlyTheCaseIsAllowed()
    {
        var store = Store();
        var bytes = File.ReadAllBytes(Full(Mara));

        store.MoveFile(Mara, $"{Characters}/Mara-Voss.md");
        store.MoveFolder("universes/starfall/series/long-dark/books/ember", "universes/starfall/series/long-dark/books/Ember");

        Assert.Equal(["Mara-Voss.md"], store.GetFiles(Characters));
        Assert.Equal(bytes, store.ReadFile($"{Characters}/Mara-Voss.md"));
        Assert.Contains("Ember", store.GetDirectories("universes/starfall/series/long-dark/books"));
        Assert.DoesNotContain("ember", store.GetDirectories("universes/starfall/series/long-dark/books"));
        Assert.True(store.FileExists("universes/starfall/series/long-dark/books/Ember/book.md"));
        Assert.False(store.FileExists("universes/starfall/series/long-dark/books/ember/book.md"));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-5")]
    public void AFolderIsMovedWithEverythingInIt()
    {
        var store = Store();
        const string from = "universes/starfall/series/long-dark/books/ember";
        const string to = "universes/starfall/books/ember";
        var outline = store.ReadFile($"{from}/outline.md");

        store.MoveFolder(from, to);

        Assert.False(store.FolderExists(from));
        Assert.Equal(outline, store.ReadFile($"{to}/outline.md"));
        Assert.True(store.FileExists($"{to}/book.md"));
    }

    [Fact]
    public void AMoveDoesntOverwriteOrMoveAFolderIntoItself()
    {
        var store = Store();
        const string kessan = "universes/starfall/entities/locations/kessan-reach.md";

        Assert.Equal(DenStoreError.AlreadyExists, Assert.Throws<DenStoreException>(() => store.MoveFile(kessan, Mara)).Error);
        Assert.Equal(DenStoreError.AlreadyExists, Assert.Throws<DenStoreException>(() => store.MoveFolder("universes/drift", "universes/starfall")).Error);
        Assert.Equal(DenStoreError.InvalidPath, Assert.Throws<DenStoreException>(() => store.MoveFolder("universes/starfall", "universes/starfall/inner")).Error);
        Assert.Equal(DenStoreError.NotFound, Assert.Throws<DenStoreException>(() => store.MoveFile($"{Characters}/nobody.md", $"{Characters}/somebody.md")).Error);
        Assert.Equal(DenStoreError.NotAFile, Assert.Throws<DenStoreException>(() => store.MoveFolder(Mara, $"{Characters}/mara")).Error);
        Assert.True(store.FileExists(kessan));
        Assert.True(store.FileExists(Mara));
    }

    [Fact]
    public void DeletingAFileOrAnEmptyFolder()
    {
        var store = Store();

        store.DeleteFile(Mara);
        Assert.False(File.Exists(Full(Mara)));
        Assert.Equal(DenStoreError.NotFound, Assert.Throws<DenStoreException>(() => store.DeleteFile(Mara)).Error);

        const string ships = "universes/starfall/series/long-dark/entities/ships";
        var notEmpty = Assert.Throws<DenStoreException>(() => store.DeleteFolder(ships));
        Assert.Equal(DenStoreError.NotEmpty, notEmpty.Error);
        Assert.True(Directory.Exists(Full(ships)));

        store.DeleteFolder(Characters);
        Assert.False(store.FolderExists(Characters));
        Assert.Equal(DenStoreError.NotFound, Assert.Throws<DenStoreException>(() => store.DeleteFolder(Characters)).Error);
    }

    [Fact]
    public void AFolderWhoseOnlyEntriesAreHiddenIsntDeletedAndTheMessageNamesOne()
    {
        var store = Store();
        store.DeleteFile(Mara);
        File.WriteAllText(Path.Combine(Full(Characters), ".mara-voss.md.swp"), "swap");

        var e = Assert.Throws<DenStoreException>(() => store.DeleteFolder(Characters));

        Assert.Equal(DenStoreError.NotEmpty, e.Error);
        Assert.Contains("'.mara-voss.md.swp', which is hidden", e.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Full(Characters)));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-5")]
    public void ACaseOnlyFolderRenameThatFailsHalfWayIsPutBack()
    {
        // As on Windows, where the plain move is refused or does nothing, so
        // the rename goes through a hidden name, and its second step fails.
        const string books = "universes/starfall/series/long-dark/books";
        var moves = 0;
        var store = new FileSystemDenStore(_root)
        {
            OneNameForAllCases = true,
            DirectoryMove = (from, to) =>
            {
                switch (++moves)
                {
                    case 1:
                        throw new IOException("Source and destination path must be different.");
                    case 3:
                        throw new IOException("The disk is full.");
                    default:
                        Directory.Move(from, to);
                        break;
                }
            },
        };

        var e = Assert.Throws<IOException>(() => store.MoveFolder($"{books}/ember", $"{books}/Ember"));

        Assert.Equal("The disk is full.", e.Message);
        Assert.Equal(4, moves);
        Assert.Equal(["ash", "ember"], Directory.EnumerateDirectories(Full(books)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(store.FileExists($"{books}/ember/book.md"));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-5")]
    public void ACaseOnlyFolderRenameGoesThroughAHiddenNameWhenThePlainMoveIsRefused()
    {
        const string books = "universes/starfall/series/long-dark/books";
        var moves = 0;
        var store = new FileSystemDenStore(_root)
        {
            OneNameForAllCases = true,
            DirectoryMove = (from, to) =>
            {
                if (++moves == 1)
                {
                    throw new IOException("Source and destination path must be different.");
                }

                Directory.Move(from, to);
            },
        };

        store.MoveFolder($"{books}/ember", $"{books}/Ember");

        Assert.Equal(3, moves);
        Assert.Equal(["Ember", "ash"], Directory.EnumerateDirectories(Full(books)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-3")]
    public void ACaseOnlyRenameOntoAnotherFileWithThatNameIsRefused()
    {
        // Only Linux can hold mara-voss.md and Mara-Voss.md side by side.
        File.WriteAllText(Full($"{Characters}/Mara-Voss.md"), "another Mara");
        if (Directory.GetFiles(Full(Characters)).Length < 2)
        {
            Assert.Skip("This file system takes names that differ only in case as one name.");
        }

        var e = Assert.Throws<DenStoreException>(() => new FileSystemDenStore(_root).MoveFile(Mara, $"{Characters}/MARA-VOSS.md"));

        Assert.Equal(DenStoreError.CaseConflict, e.Error);
        Assert.Equal("another Mara", File.ReadAllText(Full($"{Characters}/Mara-Voss.md")));
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-3")]
    public void ANameInAnotherUnicodeFormIsRefused()
    {
        // "Café" with é as one character (NFC), and with e and a combining accent (NFD).
        var nfc = "café.md";
        var nfd = "café.md";
        var store = Store();
        store.WriteFile($"{Characters}/{nfc}", Bytes("---\nname: Café\n---\n"), DenWriteMode.Create);

        var e = Assert.Throws<DenStoreException>(() => store.WriteFile($"{Characters}/{nfd}", Bytes("another café")));

        Assert.Equal(DenStoreError.CaseConflict, e.Error);
        Assert.Contains("Unicode form", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadOnlyFileIsRefusedAtOnceWithAClearReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("On Linux and macOS a read-only file can be replaced: its folder's permissions decide.");
        }

        var store = Store();
        File.SetAttributes(Full(Mara), FileAttributes.ReadOnly);
        try
        {
            var watch = Stopwatch.StartNew();
            var e = Assert.Throws<DenStoreException>(() => store.WriteFile(Mara, Bytes("x"), DenWriteMode.Replace));

            Assert.Equal(DenStoreError.AccessDenied, e.Error);
            Assert.Equal($"'{Mara}' can't be written: the file is read-only.", e.Message);
            Assert.IsType<UnauthorizedAccessException>(e.InnerException);
            Assert.True(watch.ElapsedMilliseconds < 200, $"It took {watch.ElapsedMilliseconds} ms: a read-only file isn't worth trying again.");
        }
        finally
        {
            File.SetAttributes(Full(Mara), FileAttributes.Normal);
        }
    }

    [Fact]
    public void AFileAnotherProgramHoldsOpenIsReportedAsInUse()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Only Windows keeps an open file from being replaced.");
        }

        var store = Store();
        using var held = new FileStream(Full(Mara), FileMode.Open, FileAccess.Read, FileShare.None);

        var e = Assert.Throws<DenStoreException>(() => store.WriteFile(Mara, Bytes("x"), DenWriteMode.Replace));

        Assert.True(e.Error is DenStoreError.InUse or DenStoreError.AccessDenied, e.Error.ToString());
        Assert.NotNull(e.InnerException);
    }

    [Theory]
    [InlineData("/", "/universes/x.md", true)]
    [InlineData("/den", "/den/universes/x.md", true)]
    [InlineData("/den", "/den", false)]
    [InlineData("/den", "/denmark/x.md", false)]
    [InlineData("/", "/", false)]
    public void ADenAtTheRootOfAFileSystemCanBeWritten(string root, string full, bool inside)
    {
        var separator = Path.DirectorySeparatorChar.ToString();
        var drive = OperatingSystem.IsWindows() ? "C:" : "";
        Assert.Equal(inside, FileSystemDenStore.IsInside(drive + root.Replace("/", separator), drive + full.Replace("/", separator)));
    }

    // ----- a store that lives as long as its host -----

    [Fact]
    public void ReadsSeeAFolderRenamedOutsideTheStore()
    {
        // A long-lived store, as in the MCP server: something else renames
        // characters/ to Characters/ between two reads.
        var store = Store();
        Assert.True(store.FileExists(Mara));
        var renamed = Mara.Replace("/characters/", "/Characters/", StringComparison.Ordinal);

        Directory.Move(Full(Characters), Full(Characters) + "-moving");
        Directory.Move(Full(Characters) + "-moving", Full(Characters.Replace("characters", "Characters", StringComparison.Ordinal)));

        Assert.False(store.FileExists(Mara));
        Assert.True(store.FileExists(renamed));
        store.WriteFile(renamed, Bytes("---\nname: Mara Voss\n---\n"), DenWriteMode.Replace);
        Assert.Equal(DenStoreError.CaseConflict, Assert.Throws<DenStoreException>(() => store.WriteFile(Mara, Bytes("x"), DenWriteMode.Replace)).Error);
    }

    [Fact]
    public void ASnapshotReadsOneLoad()
    {
        var store = Store();

        var snapshot = store.Snapshot();

        Assert.Equal(store.Root, snapshot.Root);
        Assert.True(snapshot.FileExists(Mara));
    }

    // ----- read-only stores -----

    [Fact]
    [Trait("Scenario", "SC-STORE-4")]
    public void TheBuiltInPacksRefuseEveryWrite()
    {
        IDenStore packs = BuiltInPacks.Instance;
        const string thread = "core/types/thread.yaml";
        var before = packs.ReadFile(thread);

        var refusals = new[]
        {
            Assert.Throws<DenStoreException>(() => packs.WriteFile(thread, Bytes("name: thread\n"))),
            Assert.Throws<DenStoreException>(() => packs.WriteFile("core/types/ship.yaml", Bytes("name: ship\n"), DenWriteMode.Create)),
            Assert.Throws<DenStoreException>(() => packs.DeleteFile(thread)),
            Assert.Throws<DenStoreException>(() => packs.MoveFile(thread, "core/types/plot.yaml")),
            Assert.Throws<DenStoreException>(() => packs.MoveFolder("core", "mine")),
            Assert.Throws<DenStoreException>(() => packs.DeleteFolder("core/templates")),
        };

        Assert.True(packs.IsReadOnly);
        Assert.All(refusals, e => Assert.Equal(DenStoreError.ReadOnly, e.Error));
        Assert.Equal($"'{thread}' can't be written: the built-in packs ship with Wolfden and can't be changed. To change a built-in type, extend it from a pack in the den's packs/ folder (types.md).", refusals[0].Message);
        Assert.Equal(before, packs.ReadFile(thread));
        Assert.False(packs.FileExists("core/types/ship.yaml"));
    }

    [Fact]
    public void AStoreOnDiskCanBeWritten()
    {
        Assert.False(Store().IsReadOnly);
    }

    // ----- reads through the store -----

    [Fact]
    public void TheStoreReadsTheDenAsTheSourceOnDiskDoes()
    {
        IDenSource store = Store();
        IDenSource source = new FileSystemDenSource(_root);

        Assert.Equal(source.GetDirectories("universes"), store.GetDirectories("universes"));
        Assert.Equal(source.GetFiles(Characters), store.GetFiles(Characters));
        Assert.Equal(source.ReadFile(Mara), store.ReadFile(Mara));
        Assert.Equal(source.TryReadFile("nothing.md"), store.TryReadFile("nothing.md"));
        Assert.True(store.FolderExists(Characters));
        Assert.False(store.FileExists($"{Characters}/Mara-Voss.md"));
        Assert.Equal(
            Describe(Wolfden.Core.Checks.DenCheck.Run(source)),
            Describe(Wolfden.Core.Checks.DenCheck.Run(store)));
    }

    // ----- watching -----

    [Fact]
    public void TheStoreReportsChangesWithDenPaths()
    {
        var store = Store();
        var changes = new BlockingCollection<DenChange>();
        var observer = new Recorder(changes);
        const string wren = $"{Characters}/wren-ashby.md";

        using (store.Watch(observer))
        {
            store.WriteFile(wren, Bytes("---\nname: Wren Ashby\n---\n"), DenWriteMode.Create);
            WaitFor(changes, c => c.Path == wren && c.Kind is DenChangeKind.Renamed or DenChangeKind.Created or DenChangeKind.Changed);

            store.DeleteFile(wren);
            WaitFor(changes, c => c.Path == wren && c.Kind == DenChangeKind.Deleted);
        }

        Assert.Empty(observer.Errors);
        while (changes.TryTake(out _))
        {
        }

        File.AppendAllText(Full(Mara), "\nAfter the watch.\n");
        Assert.False(changes.TryTake(out _, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void ARenameOutOfAndIntoTheDenRootIsReportedAsDeletedAndCreated()
    {
        // A FileSystemWatcher reports only paths under the den, so the
        // others are reported by DenPath's rules, checked here directly.
        Assert.Null(FileSystemDenStore.DenPath(_root, _root));
        Assert.Null(FileSystemDenStore.DenPath(_root, Path.Combine(_outside, "x.md")));
        Assert.Equal("universes/x.md", FileSystemDenStore.DenPath(_root, Path.Combine(_root, "universes", "x.md")));
        Assert.Equal(".wolfden/index.db", FileSystemDenStore.DenPath(_root, Path.Combine(_root, ".wolfden", "index.db")));
    }

    private static string Describe(Wolfden.Core.Checks.DenCheck check) =>
        string.Join("\n", check.Diagnostics.Select(d => d.ToString()));

    private static T WaitFor<T>(BlockingCollection<T> items, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (items.TryTake(out var item, TimeSpan.FromMilliseconds(200)) && done(item))
            {
                return item;
            }
        }

        throw new TimeoutException("Nothing matching within 30 s.");
    }

    private sealed class Recorder(BlockingCollection<DenChange> changes) : IDenChangeObserver
    {
        public ConcurrentBag<Exception> Errors { get; } = [];

        public void Changed(DenChange change) => changes.Add(change);

        public void Lost() => Errors.Add(new InternalBufferOverflowException());

        public void Broken(Exception error) => Errors.Add(error);
    }

    /// <summary>
    /// Makes <paramref name="link"/> a link to the folder <paramref name="target"/>:
    /// a symbolic link where the OS allows it, else a junction on Windows,
    /// else the test is skipped. Disposing it removes the link, not what it
    /// links to.
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

    private static string ShortPath(string full)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(full, buffer, buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);
}
