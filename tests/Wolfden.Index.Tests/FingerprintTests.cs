namespace Wolfden.Index.Tests;

/// <summary>
/// The files fingerprint holds exactly what matters (#103): every file a
/// rebuild reads is in it, so an edit to one makes the index stale, and the
/// files it leaves out don't change what a rebuild writes, so editing them
/// never needs a rebuild.
/// </summary>
public sealed class FingerprintTests
{
    /// <summary>Files the index doesn't read, in the places a writer might keep them.</summary>
    private static readonly string[] Unread =
    [
        "README.md",
        "LICENSE.md",
        "notes/ideas.md",
        "authors/me.md",
        "drafts/chapter-1.md",
        "archive/old-den/den.yaml",
        "universes/starfall/drafts/opening.md",
        "universes/starfall/series/long-dark/notes.md",
        "universes/starfall/series/long-dark/books/ash/notes.md",
        "universes/starfall/series/long-dark/books/ash/drafts/chapter-1.md",
        "universes/starfall/series/long-dark/books/ash/drafts/outline.md",
        "universes/starfall/entities/locations/map.png",
    ];

    public static TheoryData<string> Dens => ["dracula", "starfall", "generated-1", "generated-2", "generated-3"];

    [Theory]
    [MemberData(nameof(Dens))]
    public void EveryFileARebuildReadsIsInTheFingerprint(string name)
    {
        using var den = Den(name);

        var state = IndexBuilder.Rebuild(den.Root, beforeSwap: null);

        Assert.NotEmpty(state.FileHashes);
        foreach (var (path, hash) in state.FileHashes)
        {
            Assert.True(DenFileTree.IsRead(path), $"{path} is read but IsRead says it isn't.");
            Assert.Equal(hash, state.Tree.Hash(path));
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-4")]
    public void FilesTheIndexDoesntReadDontChangeARebuild()
    {
        using var den = TempDen.Starfall();
        var content = Content(den);
        var fingerprint = IndexBuilder.FilesFingerprint(den.Root);

        foreach (var path in Unread)
        {
            den.Write(path, "---\nformat: 1\nname: Not read\ntype: character\n---\n\nAnything.\n");
        }

        Assert.All(Unread, path => Assert.False(DenFileTree.IsRead(path), path));
        Assert.Equal(content, Content(den));
        Assert.Equal(fingerprint, IndexBuilder.FilesFingerprint(den.Root));
    }

    [Theory]
    [Trait("Scenario", "SC-CLI-6")]
    [InlineData("README.md")]
    [InlineData("drafts/chapter-1.md")]
    [InlineData("universes/starfall/series/long-dark/books/ash/drafts/chapter-1.md")]
    public void AnEditToAFileTheIndexDoesntReadDoesntMakeItStale(string path)
    {
        using var den = TempDen.Starfall();
        den.Write(path, "First draft.\n");
        IndexBuilder.Rebuild(den.Root);

        den.Write(path, "Second draft.\n");

        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(den.Root));
    }

    [Theory]
    [InlineData("den.yaml", true)]
    [InlineData("packs/mine/pack.yaml", true)]
    [InlineData("packs/mine/templates/ship.md", true)]
    [InlineData("universes/u/universe.md", true)]
    [InlineData("universes/u/series/s/series.md", true)]
    [InlineData("universes/u/books/b/book.md", true)]
    [InlineData("universes/u/series/s/books/b/book.md", true)]
    [InlineData("universes/u/books/b/outline.md", true)]
    [InlineData("universes/u/series/s/books/b/outline.md", true)]
    [InlineData("universes/u/entities/characters/mara.md", true)]
    [InlineData("universes/u/series/s/entities/mara.md", true)]
    [InlineData("universes/u/books/b/entities/a/b/c/mara.md", true)]
    [InlineData("universes/u/series/s/books/b/entities/characters/mara.md", true)]
    [InlineData("universes/u/entities/characters/.mara.md", false)]
    [InlineData("universes/u/entities/.hidden/mara.md", false)]
    [InlineData("universes/u/entities/characters/mara.yaml", false)]
    [InlineData("universes/u/series/s/outline.md", false)]
    [InlineData("universes/u/series/s/books/b/notes.md", false)]
    [InlineData("universes/u/drafts/entities/mara.md", false)]
    [InlineData("universes/u/notes.md", false)]
    [InlineData("universes/universe.md", false)]
    [InlineData("README.md", false)]
    [InlineData("archive/den.yaml", false)]
    [InlineData(".obsidian/workspace.md", false)]
    public void IsReadKnowsTheFilesTheLoaderReads(string path, bool read)
    {
        Assert.Equal(read, DenFileTree.IsRead(path));
    }

    private static TempDen Den(string name) => name switch
    {
        "dracula" => TempDen.Dracula(),
        "starfall" => TempDen.Starfall(),
        _ => TempDen.FromFiles(DenGenerator.Generate(int.Parse(name["generated-".Length..], System.Globalization.CultureInfo.InvariantCulture)).Files),
    };

    private static string Content(TempDen den)
    {
        var path = Path.Combine(Path.GetTempPath(), "wolfden-index-tests", $"fingerprint-{Guid.NewGuid():N}.db");
        try
        {
            IndexBuilder.BuildInto(den.Root, path);
            return IndexContent.Of(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
