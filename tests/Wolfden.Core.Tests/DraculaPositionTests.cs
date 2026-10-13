using Wolfden.Core.Documents;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Tests;

/// <summary>
/// Every story position in <c>samples/dracula</c> resolves, and where the
/// sample makes the order clear, the positions follow it.
/// </summary>
public class DraculaPositionTests
{
    private static readonly Lazy<DenStructure> Den = new(() => DenStructure.Load(MemoryDenSource.DraculaRoot));

    private static Universe Dracula => Den.Value.FindUniverse("dracula")!;

    public static TheoryData<string> EntityFiles() =>
        new(TestFiles.RepoFiles("samples/dracula")
            .Where(f => f.Contains("/entities/", StringComparison.Ordinal) && f.EndsWith(".md", StringComparison.Ordinal))
            .Select(f => f["samples/dracula/".Length..]));

    private static (MarkdownFile File, PositionScan Scan) ScanEntity(string path)
    {
        var file = MarkdownFile.Parse(TestFiles.ReadRepoFile($"samples/dracula/{path}"), requireFrontmatter: true);
        // The position fields come from the loaded type, such as the core thread's planted and paid_off.
        var type = file.GetFrontmatterValue("type") is YamlScalar { IsNull: false } t ? Den.Value.Types.Find(t.Text) : null;
        return (file, PositionScanner.Scan(file, type?.PositionFields));
    }

    private static StoryPoint Resolve(string path, PositionReference reference)
    {
        Assert.NotNull(reference.Position);
        var resolution = Den.Value.Resolve(path, reference.Position);
        Assert.True(resolution.IsResolved, $"{path}:{reference.Line}: {resolution.Problem}");
        return resolution.Resolved.Point;
    }

    [Fact]
    public void TheStructureLoadsCleanly()
    {
        Assert.Empty(Den.Value.Diagnostics);
        var book = Assert.Single(Dracula.Books);
        Assert.Equal("dracula", book.Key);
        Assert.Null(book.Series);
        // 27 chapters and a closing Note, in order, with numbers for display only.
        Assert.Equal(Enumerable.Range(1, 27).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("note"),
            book.Chapters.Select(c => c.Key));
        Assert.Null(book.FindChapter("note")!.Number);
        Assert.Equal(27L, book.FindChapter("27")!.Number);
    }

    [Fact]
    public void ThereArePositionsToCheck()
    {
        var count = EntityFiles().Sum(row => ScanEntity(row.Data).Scan.References.Count);

        // Every from, until, planted, and paid_off in the sample: 176 froms and
        // untils, and 13 thread positions. Update this when the sample changes.
        Assert.Equal(189, count);
    }

    [Theory]
    [MemberData(nameof(EntityFiles))]
    public void EveryPositionParsesAndResolves(string path)
    {
        var (file, scan) = ScanEntity(path);

        Assert.DoesNotContain(file.Diagnostics, d => d.RuleId.StartsWith("F-POS", StringComparison.Ordinal));
        Assert.Empty(scan.Diagnostics);
        foreach (var reference in scan.References)
        {
            Resolve(path, reference);
        }
    }

    [Theory]
    [MemberData(nameof(EntityFiles))]
    public void TemporalEntriesAreListedInStoryOrder(string path)
    {
        // F-TMP-7: writers list entries in story order, and the sample does.
        var (_, scan) = ScanEntity(path);
        foreach (var field in scan.References.Where(r => r.Site == PositionSite.EntryFrom).GroupBy(r => r.Field))
        {
            var points = field.Select(r => Resolve(path, r)).ToList();
            for (var i = 1; i < points.Count; i++)
            {
                Assert.True(points[i - 1] < points[i], $"{path}: {field.Key} entries aren't in story order at line {field.ElementAt(i).Line}.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(EntityFiles))]
    public void EveryUntilIsAfterItsFrom(string path)
    {
        // F-TMP-3 and F-SEC-8: the sample has no empty ranges.
        var (_, scan) = ScanEntity(path);
        var refs = scan.References;
        for (var i = 0; i < refs.Count; i++)
        {
            var until = refs[i];
            if (until.Site is not (PositionSite.EntryUntil or PositionSite.SectionUntil))
            {
                continue;
            }

            var from = refs[i - 1];
            Assert.Equal(until.Line, from.Line);
            Assert.True(Resolve(path, from) < Resolve(path, until), $"{path}:{until.Line}: until isn't after from.");
        }
    }

    [Theory]
    [MemberData(nameof(EntityFiles))]
    public void SubsectionsStartNoEarlierThanTheirParents(string path)
    {
        // F-SEC-9: a subsection whose from is before its parent's is a warning,
        // and the sample has none.
        var (file, scan) = ScanEntity(path);
        var sectionFrom = scan.References.Where(r => r.Site == PositionSite.SectionFrom)
            .ToDictionary(r => r.Section!, r => Resolve(path, r));
        foreach (var section in file.Sections)
        {
            if (!sectionFrom.TryGetValue(section, out var own))
            {
                continue;
            }

            for (var parent = section.Parent; parent is not null; parent = parent.Parent)
            {
                if (sectionFrom.TryGetValue(parent, out var parentFrom))
                {
                    Assert.True(parentFrom <= own, $"{path}:{section.Line}: '{section.Name}' starts before '{parent.Name}'.");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(EntityFiles))]
    public void ThreadsArePaidOffAfterTheyArePlanted(string path)
    {
        // types.md F-CORE-2: paid_off isn't before the earliest planted position.
        var (_, scan) = ScanEntity(path);
        var planted = scan.References.Where(r => r.Field == "planted").Select(r => Resolve(path, r)).ToList();
        var paidOff = scan.References.Where(r => r.Field == "paid_off").Select(r => Resolve(path, r)).ToList();
        if (planted.Count == 0)
        {
            return;
        }

        var earliest = planted.Min();
        Assert.All(paidOff, p => Assert.True(earliest <= p, $"{path}: paid off before it's planted."));
        Assert.Equal(planted.Order(), planted);
    }

    [Theory]
    // Facts the novel makes plain: who the reader meets first, and what happens when.
    [InlineData("books/dracula/entities/characters/jonathan-harker.md", "books/dracula/entities/characters/lucy-westenra.md")]
    [InlineData("books/dracula/entities/characters/lucy-westenra.md", "books/dracula/entities/characters/abraham-van-helsing.md")]
    [InlineData("books/dracula/entities/events/harker-arrives-at-the-castle.md", "books/dracula/entities/events/voyage-of-the-demeter.md")]
    [InlineData("books/dracula/entities/events/voyage-of-the-demeter.md", "books/dracula/entities/events/death-of-lucy.md")]
    [InlineData("books/dracula/entities/events/death-of-lucy.md", "books/dracula/entities/events/lucy-is-laid-to-rest.md")]
    [InlineData("books/dracula/entities/events/lucy-is-laid-to-rest.md", "books/dracula/entities/events/renfield-lets-the-count-in.md")]
    [InlineData("books/dracula/entities/events/renfield-lets-the-count-in.md", "books/dracula/entities/events/death-of-the-count.md")]
    [InlineData("books/dracula/entities/events/death-of-the-count.md", "books/dracula/entities/characters/quincey-harker.md")]
    public void EntitiesEnterCanonInTheNovelsOrder(string earlier, string later)
    {
        Assert.True(EntityFrom(earlier) < EntityFrom(later));
    }

    [Fact]
    public void TheNoteComesAfterTheLastChapter()
    {
        var u = Dracula;
        var note = u.Resolve(StoryPosition.Parse("dracula:note")).Resolved!;
        var last = u.Resolve(StoryPosition.Parse("dracula:27")).Resolved!;

        Assert.True(last.Point < note.Point);
        Assert.Equal(u.Resolve(StoryPosition.Parse("dracula:1")).Resolved!.Point, u.Resolve(StoryPosition.Parse("dracula")).Resolved!.Point);
    }

    private static StoryPoint EntityFrom(string path)
    {
        var full = $"universes/dracula/{path}";
        var reference = ScanEntity(full).Scan.References.Single(r => r.Site == PositionSite.EntityFrom);
        return Resolve(full, reference);
    }
}
