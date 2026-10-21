using Wolfden.Core.Checks;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>
/// A folder that's deleted after its parent was listed is absent to the
/// den's loads and checks, never a crash and never a broken container
/// (#116). The race is simulated through the den source, so it's the same
/// on every OS; <c>UnlistableFolderTests</c> in the index tests covers real
/// folders the user may not list.
/// </summary>
public sealed class UnlistableFolderTests
{
    [Theory]
    [InlineData("universes/starfall/series/long-dark/entities/characters")]
    [InlineData("universes/starfall/series/long-dark/books/ash/entities/items")]
    [InlineData("universes/starfall/series/long-dark/books/ember")]
    [InlineData("universes/starfall/series/drift-war")]
    [InlineData("universes/starfall/books/a-quiet-moon")]
    [InlineData("universes/drift")]
    [Trait("Scenario", "SC-CLI-9")]
    public void AFolderDeletedAfterItsParentWasListedIsCheckedAsIfItWerentThere(string folder)
    {
        var vanishing = new VanishingFolderSource(MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot), folder);
        var without = Without(MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot), folder);

        Assert.Equal(Describe(DenCheck.Run(without)), Describe(DenCheck.Run(vanishing)));
        Assert.True(vanishing.Vanished, "The folder was never listed.");
        Assert.Equal(Entities(without), Entities(new VanishingFolderSource(MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot), folder)));
    }

    [Fact]
    public void AnEmptyContainerFolderThatsStillThereIsStillAnError()
    {
        // The second look in DenLoader only clears a folder that's gone: one
        // that's still listed without its book.md is still F-LAY-4.
        const string folder = "universes/starfall/series/long-dark/books/ember";
        var source = MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot).Without($"{folder}/book.md");

        Assert.Contains(DenCheck.Run(source).Diagnostics, d => d.Path == folder && d.Diagnostic.RuleId == "F-LAY-4");
    }

    private static MemoryDenSource Without(MemoryDenSource source, string folder)
    {
        foreach (var path in source.Files.Where(p => p.StartsWith(folder + "/", StringComparison.Ordinal)).ToList())
        {
            source.Without(path);
        }

        return source;
    }

    private static List<string> Describe(DenCheck check) =>
        check.Diagnostics.Select(d => $"{d.Path}|{d.Diagnostic.RuleId}|{d.Diagnostic.Line}|{d.Diagnostic.Message}").ToList();

    private static List<string> Entities(IDenSource source) =>
        EntityCatalog.Load(DenStructure.Load(source), source).Entities.Select(e => e.Path).ToList();

    /// <summary>
    /// A den source whose folder <paramref name="folder"/> is deleted just
    /// after the first listing of its parent: that listing still names it,
    /// and from then on nothing is in it, its parent's listings leave it
    /// out, and reading a file in it fails, as on disk.
    /// </summary>
    private sealed class VanishingFolderSource(IDenSource inner, string folder) : IDenSource
    {
        private readonly string _parent = folder[..folder.LastIndexOf('/')];

        /// <summary>True once the folder is gone: its parent was listed with it.</summary>
        public bool Vanished { get; private set; }

        private bool Gone(string path) => Vanished && (path == folder || path.StartsWith(folder + "/", StringComparison.Ordinal));

        public bool FileExists(string path) => !Gone(path) && inner.FileExists(path);

        public byte[] ReadFile(string path) =>
            Gone(path) ? throw new DirectoryNotFoundException($"Could not find a part of the path '{path}'.") : inner.ReadFile(path);

        public IEnumerable<string> GetDirectories(string path)
        {
            if (Gone(path))
            {
                return [];
            }

            var names = inner.GetDirectories(path).ToList();
            if (path == _parent)
            {
                if (Vanished)
                {
                    names.Remove(folder[(_parent.Length + 1)..]);
                }

                Vanished = true;
            }

            return names;
        }

        public IEnumerable<string> GetFiles(string path) => Gone(path) ? [] : inner.GetFiles(path);
    }
}
