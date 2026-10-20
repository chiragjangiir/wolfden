using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>
/// <see cref="EntityCatalog.Update"/> and <see cref="LinkIndex.Build(EntityCatalog, IDenSource, LinkIndex?, IReadOnlySet{string})"/>,
/// which a file watcher uses to read only the files that changed (#14):
/// their result must be the same as a full load of the den as it is now.
/// </summary>
public class IncrementalLoadTests
{
    private const string Mara = "universes/starfall/series/long-dark/entities/characters/mara-voss.md";
    private const string Pike = "universes/starfall/series/long-dark/books/ash/entities/characters/halden-pike.md";
    private const string Outline = "universes/starfall/series/long-dark/books/ember/outline.md";

    public static TheoryData<string> Changes => ["rename", "duplicate id", "error", "delete", "add", "outline", "move"];

    [Theory]
    [MemberData(nameof(Changes))]
    public void AnUpdateIsTheSameAsAFullLoad(string change)
    {
        var source = MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot);
        var structure = DenStructure.Load(source);
        var catalog = EntityCatalog.Load(structure, source);
        var links = LinkIndex.Build(catalog, source);
        var text = System.Text.Encoding.UTF8.GetString(source.ReadFile(Pike));
        var changed = new List<string>();
        switch (change)
        {
            case "rename":
                source.With(Pike, text.Replace("name: Halden Pike", "name: Halden Marsh", StringComparison.Ordinal));
                changed.Add(Pike);
                break;
            case "duplicate id":
                source.With(Pike, text.Replace("id: 01JA7Q8M4XK3V9R2D5W8HN6TQC", "id: 01JA7Q8M4XK3V9R2D5W8HN6TQB", StringComparison.Ordinal));
                changed.Add(Pike);
                break;
            case "error":
                source.With(Pike, text.Replace("aliases: [Pike]", "aliases: [Pike", StringComparison.Ordinal));
                changed.Add(Pike);
                break;
            case "delete":
                source.Without(Mara);
                changed.Add(Mara);
                break;
            case "add":
                source.With("universes/starfall/entities/characters/pike.md", "---\ntype: character\nname: Pike\n---\n\nAnother Pike, with [[Mara Voss]].\n");
                changed.Add("universes/starfall/entities/characters/pike.md");
                break;
            case "outline":
                source.With(Outline, "| row | chapter | beat |\n|---|---|---|\n| 1 | 1 | [[Pike]] meets [[nobody]]. |\n");
                break;
            case "move":
                source.Without(Pike).With("universes/starfall/entities/characters/halden-pike.md", text);
                changed.AddRange([Pike, "universes/starfall/entities/characters/halden-pike.md"]);
                break;
        }

        var outlines = new HashSet<string>(change == "outline" ? [Outline] : [], StringComparer.Ordinal);
        var updated = catalog.Update(source, changed);
        var updatedLinks = LinkIndex.Build(updated, source, links, outlines);

        var fresh = EntityCatalog.Load(structure, source);
        var freshLinks = LinkIndex.Build(fresh, source);
        Assert.Equal(Describe(fresh, freshLinks), Describe(updated, updatedLinks));
        Assert.NotEqual(Describe(catalog, links), Describe(updated, updatedLinks));
    }

    [Fact]
    public void EntitiesOfUnchangedFilesAreKept()
    {
        var source = MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot);
        var catalog = EntityCatalog.Load(source);

        var updated = catalog.Update(source, [Pike]);

        Assert.Same(catalog.FindByPath(Mara), updated.FindByPath(Mara));
        Assert.NotSame(catalog.FindByPath(Pike), updated.FindByPath(Pike));
    }

    [Fact]
    public void AnUpdateDoesntChangeThePreviousCatalog()
    {
        // Pike takes Mara's id: in the update, Mara (the same object in both
        // catalogs) has a duplicate id, but the old catalog and its links
        // must still say what they said (#107).
        var source = MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot);
        var catalog = EntityCatalog.Load(source);
        var links = LinkIndex.Build(catalog, source);
        var before = Describe(catalog, links);
        var text = System.Text.Encoding.UTF8.GetString(source.ReadFile(Pike));
        source.With(Pike, text.Replace("id: 01JA7Q8M4XK3V9R2D5W8HN6TQC", "id: 01JA7Q8M4XK3V9R2D5W8HN6TQB", StringComparison.Ordinal));

        var updated = catalog.Update(source, [Pike]);
        var mara = updated.FindByPath(Mara)!;

        Assert.Same(catalog.FindByPath(Mara), mara);
        Assert.True(updated.HasDuplicateId(mara));
        Assert.False(catalog.HasDuplicateId(mara));
        Assert.Equal(LinkOutcome.Resolved, new LinkResolver(catalog).Resolve(mara.Scope, "Mara Voss").Outcome);
        Assert.Equal(LinkOutcome.Broken, new LinkResolver(updated).Resolve(mara.Scope, "Mara Voss").Outcome);
        Assert.Equal(before, Describe(catalog, links));
    }

    [Theory]
    [InlineData(Mara, "universes/starfall/series/long-dark")]
    [InlineData("universes/starfall/entities/deep/down/here.md", "universes/starfall")]
    [InlineData("universes/starfall/series/long-dark/books/ash/entities/x.md", "universes/starfall/series/long-dark/books/ash")]
    [InlineData("universes/starfall/entities/.hidden/x.md", null)]
    [InlineData("universes/starfall/entities/characters/.x.md", null)]
    [InlineData("universes/starfall/entities/characters/x.MD", null)]
    [InlineData("universes/starfall/Entities/characters/x.md", null)]
    [InlineData("universes/starfall/notes/x.md", null)]
    [InlineData("universes/nowhere/entities/x.md", null)]
    [InlineData("universes/starfall/series/long-dark/series.md", null)]
    public void EntityFileScopeMatchesHowAFullLoadFindsEntityFiles(string path, string? scope)
    {
        var structure = DenStructure.Load(MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot));

        Assert.Equal(scope, EntityCatalog.EntityFileScope(structure, path)?.Path);
    }

    private static List<string> Describe(EntityCatalog catalog, LinkIndex links) =>
        catalog.Entities.Select(e => $"entity {e.Path} {e.Name} {e.Key} dup={catalog.HasDuplicateId(e)}")
            .Concat(catalog.Diagnostics.Select(d => $"catalog {d}"))
            .Concat(links.Links.Select(l => $"link {l.Occurrence.Path}:{l.Occurrence.Link.Line}:{l.Occurrence.Link.Column} {l} {l.Resolution.Problem}"))
            .Concat(links.Diagnostics.Select(d => $"links {d}"))
            .ToList();
}
