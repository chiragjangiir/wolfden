using Wolfden.Core.Checks;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>
/// A file that's listed but can't be read, because it was deleted between
/// the listing and the read, another program holds it, or the user may not
/// read it, is absent to the den's loads and checks, never a crash (#112).
/// The race is simulated through the den source, so it's the same on every
/// OS; <c>UnreadableFileTests</c> in the index tests covers real files.
/// </summary>
public sealed class UnreadableFileTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[]
        {
            "universes/starfall/series/long-dark/entities/characters/mara-voss.md",
            "universes/starfall/series/long-dark/books/ember/outline.md",
            "universes/starfall/series/long-dark/books/ember/book.md",
            "universes/starfall/universe.md",
            "den.yaml",
        })
        {
            foreach (var failure in new[] { "deleted", "locked", "denied" })
            {
                data.Add(path, failure);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("Scenario", "SC-CLI-8")]
    public void AFileThatCantBeReadIsCheckedAsIfItWerentThere(string path, string failure)
    {
        var unreadable = new UnreadableSource(MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot), path, failure);
        var without = MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot).Without(path);

        Assert.Equal(Describe(DenCheck.Run(without)), Describe(DenCheck.Run(unreadable)));
        Assert.Equal(Entities(without), Entities(unreadable));
        Assert.True(unreadable.Attempts > 0);
    }

    [Theory]
    [InlineData("packs/starfall/pack.yaml")]
    [InlineData("packs/starfall/types/ship.yaml")]
    [InlineData("packs/starfall/extensions/character.yaml")]
    [InlineData("packs/starfall/templates/ship.md")]
    [Trait("Scenario", "SC-CLI-8")]
    public void APackFileThatCantBeReadIsCheckedAsIfItWerentThere(string path)
    {
        var unreadable = new UnreadableSource(MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot), path, "deleted");
        var without = MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot).Without(path);

        Assert.Equal(Describe(DenCheck.Run(without)), Describe(DenCheck.Run(unreadable)));
        Assert.Equal(Entities(without), Entities(unreadable));
        Assert.True(unreadable.Attempts > 0);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-8")]
    public void AnUnlistedPackFolderWhosePackYamlCantBeReadIsWarnedAsNotAPack()
    {
        const string path = "packs/spare/pack.yaml";
        MemoryDenSource Den() => MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot)
            .With(path, "name: Spare\n")
            .With("packs/spare/types/crate.yaml", "name: Crate\nplural: crates\n");
        var unreadable = new UnreadableSource(Den(), path, "locked");
        var without = Den().Without(path);

        var expected = Describe(DenCheck.Run(without));
        Assert.Contains(expected, d => d.StartsWith("packs/spare|F-PACK-5|", StringComparison.Ordinal));
        Assert.Equal(expected, Describe(DenCheck.Run(unreadable)));
    }

    [Fact]
    public void AnUnreadablePackYamlGivesTheSameSingleErrorAsAMissingOne()
    {
        const string path = "packs/starfall/pack.yaml";
        var unreadable = new UnreadableSource(MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot), path, "locked");

        var errors = DenCheck.Run(unreadable).Diagnostics.Where(d => d.Diagnostic.Severity == Documents.DiagnosticSeverity.Error).ToList();

        Assert.Equal("F-DEN-4", Assert.Single(errors).Diagnostic.RuleId);
    }

    [Fact]
    public void AnUpdateLeavesOutAChangedFileThatCantBeRead()
    {
        const string mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
        var source = MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot);
        var structure = DenStructure.Load(source);
        var catalog = EntityCatalog.Load(structure, source);
        Assert.Contains(catalog.Entities, e => e.Path == mara);

        var updated = catalog.Update(new UnreadableSource(source, mara, "deleted"), [mara]);

        Assert.DoesNotContain(updated.Entities, e => e.Path == mara);
        Assert.Equal(Entities(MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot).Without(mara)), updated.Entities.Select(e => e.Path).ToList());
    }

    [Fact]
    public void TryReadFileIsNullForAFileThatIsntThere()
    {
        IDenSource source = MemoryDenSource.Den();

        Assert.Null(source.TryReadFile("universes/nowhere/universe.md"));
        Assert.NotNull(source.TryReadFile("den.yaml"));
    }

    private static List<string> Describe(DenCheck check) =>
        check.Diagnostics.Select(d => $"{d.Path}|{d.Diagnostic.RuleId}|{d.Diagnostic.Line}|{d.Diagnostic.Message}").ToList();

    private static List<string> Entities(IDenSource source) =>
        EntityCatalog.Load(DenStructure.Load(source), source).Entities.Select(e => e.Path).ToList();

    /// <summary>
    /// A den source that lists one file but fails to read it, as the file
    /// system does when the file is deleted after the listing, is held open
    /// by another program (Windows), or may not be read.
    /// </summary>
    private sealed class UnreadableSource(IDenSource inner, string path, string failure) : IDenSource
    {
        public int Attempts { get; private set; }

        public bool FileExists(string p) => inner.FileExists(p);

        public byte[] ReadFile(string p)
        {
            if (p != path)
            {
                return inner.ReadFile(p);
            }

            Attempts++;
            throw failure switch
            {
                "deleted" => new FileNotFoundException("Could not find file.", p),
                "locked" => new IOException("The process cannot access the file because it is being used by another process."),
                _ => new UnauthorizedAccessException("Access to the path is denied."),
            };
        }

        public IEnumerable<string> GetDirectories(string p) => inner.GetDirectories(p);

        public IEnumerable<string> GetFiles(string p) => inner.GetFiles(p);
    }
}
