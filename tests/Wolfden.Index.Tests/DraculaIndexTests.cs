using Microsoft.EntityFrameworkCore;
using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Outlines;

namespace Wolfden.Index.Tests;

/// <summary>
/// The index of the Dracula sample holds what Core computes from the same
/// files: the same entities, sections, field values, links, and outline rows.
/// </summary>
public sealed class DraculaIndexTests : IClassFixture<DraculaIndexTests.Built>
{
    private readonly Built _built;

    public DraculaIndexTests(Built built)
    {
        _built = built;
    }

    private DenIndex Index => _built.Index;

    private LinkIndex Core => _built.Core;

    [Fact]
    public void EntitiesMatchTheCatalog()
    {
        var rows = Index.Context.Entities.OrderBy(e => e.Path).ToList();
        var entities = Core.Catalog.Entities;

        Assert.Equal(50, rows.Count);
        Assert.Equal(entities.Select(e => e.Path), rows.Select(r => r.Path));
        Assert.All(entities.Zip(rows), pair =>
        {
            var (entity, row) = pair;
            Assert.Equal(entity.Id, row.Ulid);
            Assert.Equal(entity.Key, row.Key);
            Assert.Equal(entity.Name, row.Name);
            Assert.Equal(entity.Type, row.Type);
            Assert.Equal(entity.Status.ToKey(), row.Status);
            Assert.Equal(entity.EntersCanon?.Ordinal, row.FromOrdinal);
            Assert.Equal(entity.Aliases, Index.Context.Aliases.Where(a => a.EntityId == row.Id).OrderBy(a => a.Order).Select(a => a.Alias).ToList());
        });
    }

    [Fact]
    public void SectionsMatchTheFiles()
    {
        var expected = Core.Catalog.Entities.Sum(e => e.File.Sections.Count);

        Assert.Equal(expected, Index.Context.Sections.Count());
    }

    [Fact]
    public void FieldValuesMatchTheFacts()
    {
        var bookkeeping = new Bookkeeping(Core.Catalog.Structure.BookkeepingSections);
        var expected = Core.Catalog.Entities
            .SelectMany(e => EntityFacts.For(e, bookkeeping).Fields)
            .Sum(f => f.Kind == FieldValueKind.Plain ? 1 : f.Entries.Count);

        Assert.Equal(expected, Index.Context.FieldValues.Count());
    }

    [Fact]
    public void EveryLinkIsIndexedAndResolved()
    {
        var links = Index.Context.Links.OrderBy(l => l.Id).ToList();

        Assert.Equal(372, links.Count);
        Assert.Equal(Core.Links.Count, links.Count);
        Assert.All(links, l => Assert.Equal("resolved", l.Outcome));
        Assert.Equal(
            Core.Links.Select(l => (l.Occurrence.Path, l.Occurrence.Link.Line, l.Occurrence.Link.Column, l.Target!.Key)),
            links.Select(l => (l.Path, l.Line, l.Column, l.TargetKey!)));
    }

    [Fact]
    public void BacklinksMatchCore()
    {
        foreach (var entity in Core.Catalog.Entities)
        {
            var expected = Core.LinksTo(entity).Select(l => (l.Occurrence.Path, l.Occurrence.Link.Line, l.Occurrence.Link.Column));
            var actual = Index.Backlinks(entity.Key).Select(l => (l.Path, l.Line, l.Column));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void OutlineRowsAreIndexed()
    {
        var path = "universes/dracula/books/dracula/outline.md";
        var expected = OutlineReader.ReadRows(MarkdownFile.Parse(File.ReadAllBytes(Path.Combine(_built.Den.Root, path))));
        var rows = Index.Context.OutlineRows.OrderBy(r => r.Order).ToList();

        Assert.Equal(70, rows.Count);
        Assert.Equal(expected.Select(r => r.RowId), rows.Select(r => r.RowId));
        Assert.All(rows, r => Assert.NotNull(r.ChapterOrdinal));

        var ten = rows.Single(r => r.RowId == "10");
        Assert.Equal("[[mina-harker|Mina]], [[lucy-westenra|Lucy]]", ten.Pov);
        Assert.Equal("Letters and Whitby", ten.Heading);
        Assert.Contains("\"date\":\"9–24 May\"", ten.CellsJson);
    }

    [Fact]
    public void TheSampleHasNoDiagnostics()
    {
        Assert.Empty(Index.Context.Diagnostics);
        Assert.All(Index.Context.Files, f => Assert.True(f.IsLoaded, f.Path));
    }

    [Fact]
    public void ChaptersAreInStoryOrder()
    {
        var chapters = Index.Context.Chapters.OrderBy(c => c.Index).ToList();

        Assert.Equal(28, chapters.Count);
        Assert.Equal(chapters.Select(c => c.Ordinal).Order(), chapters.Select(c => c.Ordinal));
    }

    [Fact]
    public void SearchFoldsCaseAndAccents()
    {
        var hits = Index.Search("FIANCE");

        Assert.Contains(hits, h => h.EntityName == "Lucy Westenra" && h.SectionName == "Relationships");
        Assert.All(hits, h => Assert.Contains("[", h.Snippet));
    }

    [Fact]
    public void SearchRanksNamesFirst()
    {
        var hits = Index.Search("renfield");

        Assert.Equal("R. M. Renfield", hits[0].EntityName);
        Assert.Null(hits[0].SectionName);
    }

    [Fact]
    public void FindEntityByIdKeyOrPath()
    {
        var lucy = Index.FindEntity("01M4TF5F08GDVZVWZ4JYQDBHZG");

        Assert.Equal("Lucy Westenra", lucy!.Name);
        Assert.Equal(lucy.Id, Index.FindEntity(lucy.Path)!.Id);
        Assert.Null(Index.FindEntity("no-such-entity"));
    }

    [Fact]
    public void LucysAppearanceSectionsCarryTheirPositions()
    {
        var lucy = Index.FindEntity("01M4TF5F08GDVZVWZ4JYQDBHZG")!;
        var sections = Index.Context.Sections.Where(s => s.EntityId == lucy.Id && s.Name == "Appearance").OrderBy(s => s.Index).ToList();
        var chapter = Index.Context.Chapters.AsEnumerable().ToDictionary(c => c.Key, c => c.Ordinal);

        Assert.Equal(3, sections.Count);
        Assert.Equal([chapter["5"], chapter["15"], chapter["16"]], sections.Select(s => s.FromOrdinal!.Value));

        // Appearance is a standard section of the core character, so each
        // hands over to the next one (F-SEC-8), and the last runs on.
        Assert.Equal([chapter["15"], chapter["16"], long.MaxValue], sections.Select(s => s.UntilOrdinal!.Value));
    }

    /// <summary>One index of a Dracula copy, shared by the tests in this class.</summary>
    public sealed class Built : IDisposable
    {
        public Built()
        {
            Den = TempDen.Dracula();
            IndexBuilder.Rebuild(Den.Root);
            Index = DenIndex.Open(Den.Root);
            Core = LinkIndex.Load(Den.Root);
        }

        public TempDen Den { get; }

        public DenIndex Index { get; }

        public LinkIndex Core { get; }

        public void Dispose()
        {
            Index.Dispose();
            Den.Dispose();
        }
    }
}
