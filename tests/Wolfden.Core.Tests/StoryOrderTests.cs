using Wolfden.Core.Documents;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>Story order (format.md §8.2): comparing positions across books, series, and standalone books.</summary>
public class StoryOrderTests
{
    private const string EmberBook = "universes/starfall/series/long-dark/books/ember/book.md";

    private static Universe Starfall(DenStructure? den = null) =>
        (den ?? DenStructure.Load(MemoryDenSource.StarfallRoot)).FindUniverse("starfall")!;

    private static StoryPoint Point(Universe universe, string position)
    {
        var resolution = universe.Resolve(StoryPosition.Parse(position));
        Assert.True(resolution.IsResolved, resolution.Problem);
        return resolution.Resolved.Point;
    }

    [Fact]
    public void StarfallPositionsFollowStoryOrder()
    {
        var u = Starfall();
        // F-ORD-1, F-ORD-2: order [long-dark, a-quiet-moon], books [ember, ash],
        // and each book's chapters in list order, whatever their numbers.
        string[] expected =
        [
            "ember:prologue", "ember:1", "ember:2", "ember:2b", "ember:3",
            "ash:1", "ash:2", "ash:3", "ash:4", "ash:epilogue",
            "a-quiet-moon:1", "a-quiet-moon:2", "a-quiet-moon:2b",
        ];

        var points = expected.Select(p => Point(u, p)).ToList();
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = 0; j < points.Count; j++)
            {
                Assert.Equal(i.CompareTo(j), Math.Sign(points[i].CompareTo(points[j])));
            }
        }

        var shuffled = expected.OrderBy(p => p.GetHashCode(StringComparison.Ordinal)).ToList();
        Assert.Equal(expected, shuffled.OrderBy(p => Point(u, p)));
    }

    [Fact]
    public void TheStartOfABookIsItsFirstChapterForOrdering()
    {
        var u = Starfall();

        // F-POS-7: a bare book position orders the same as the book's first chapter.
        Assert.Equal(Point(u, "ember:prologue"), Point(u, "ember"));
        Assert.Equal(0, Point(u, "ash").CompareTo(Point(u, "ash:1")));
        Assert.Equal(u.FindBook("ash")!.Start, Point(u, "ash"));
        // F-ORD-3: it comes after every chapter of earlier books.
        Assert.True(Point(u, "ember:3") < Point(u, "ash"));
        Assert.True(Point(u, "ash:epilogue") < Point(u, "a-quiet-moon"));
    }

    [Fact]
    public void StartAndEndOfStoryBracketEveryPosition()
    {
        var u = Starfall();
        var all = u.Books.SelectMany(b => b.Chapters.Select(c => c.Point).Append(b.Start)).ToList();

        Assert.All(all, p => Assert.True(u.StartOfStory < p && p < u.EndOfStory));
        Assert.True(u.StartOfStory.IsStartOfStory);
        Assert.True(u.EndOfStory.IsEndOfStory);
        Assert.Equal(u.StartOfStory, u.Start);
    }

    [Fact]
    public void SeriesStartAtTheirFirstBookOrTheirPlaceInOrder()
    {
        var u = Starfall();
        var longDark = u.Series.Single(s => s.Key == "long-dark");
        var driftWar = u.Series.Single(s => s.Key == "drift-war");

        Assert.Equal(u.FindBook("ember")!.Start, longDark.Start);
        // A series with no books yet starts after every chapter of the works before it (F-ORD-3).
        Assert.Empty(driftWar.Books);
        Assert.True(Point(u, "a-quiet-moon:2b") < driftWar.Start);
        Assert.True(driftWar.Start < u.EndOfStory);
    }

    [Fact]
    public void AnEmptySeriesBetweenWorksSitsBetweenThem()
    {
        var den = DenStructure.Load(MemoryDenSource.Den()
            .Universe("u", "order: [first, planned, last]")
            .Book("u", null, "first", "chapters: [1, 2]")
            .Series("u", "planned")
            .Book("u", null, "last", "chapters: [1]"));
        var u = den.FindUniverse("u")!;
        var planned = u.Series.Single();

        Assert.Empty(den.Diagnostics);
        Assert.True(Point(u, "first:2") < planned.Start);
        Assert.True(planned.Start < Point(u, "last"));
    }

    [Fact]
    public void PointsOfDifferentUniversesDontCompare()
    {
        var den = DenStructure.Load(MemoryDenSource.StarfallRoot);
        var ember = Point(den.FindUniverse("starfall")!, "ember:1");
        var tide = Point(den.FindUniverse("drift")!, "tide:1");

        Assert.False(ember.IsComparableWith(tide));
        Assert.Throws<ArgumentException>(() => ember.CompareTo(tide));
        Assert.Throws<ArgumentException>(() => ember < tide);
        Assert.NotEqual(ember, tide);
    }

    [Fact]
    public void ResolvedPositionsCompareByPoint()
    {
        var u = Starfall();
        var a = u.Resolve(StoryPosition.Parse("ember:2b")).Resolved!;
        var b = u.Resolve(StoryPosition.Parse("ash")).Resolved!;

        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
        Assert.Equal(1, a.CompareTo(null));
        Assert.Equal(StoryPoint.Max(a.Point, b.Point), b.Point);
        Assert.Equal(StoryPoint.Min(a.Point, b.Point), a.Point);
    }

    [Fact]
    public void RenumberingAndInsertingChaptersKeepsWhatPositionsReferTo()
    {
        var source = MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot);
        var before = Starfall(DenStructure.Load(source));
        var positions = FixturePositions(source);

        // Chapter 2b's revision, undone: the chapters are renumbered, retitled,
        // and two new chapters are inserted, but no key changes (F-BOOK-6).
        source.With(EmberBook, """
            ---
            id: 01JAQKFBF5KZNWJ47TAN9ZT24M
            name: Ember
            chapters:
              - { key: cold-open, number: 1 }
              - { key: prologue, number: 2, title: Before the Dark, Revised }
              - { key: "1", number: 3 }
              - { key: 1b, number: 4 }
              - { key: "2", number: 5 }
              - { key: 2b, number: 6 }
              - { key: "3", number: 7, title: Ambush }
            ---
            """.Replace("\r\n", "\n"));
        var den = DenStructure.Load(source);
        Assert.Empty(den.Diagnostics);
        var after = Starfall(den);

        AssertSameReferentsAndOrder(before, after, positions);
        Assert.Equal(6L, after.Resolve(StoryPosition.Parse("ember:2b")).Resolved!.Chapter!.Number);
        // The new chapters take their places too.
        Assert.True(Point(after, "ember:1") < Point(after, "ember:1b") && Point(after, "ember:1b") < Point(after, "ember:2"));
        Assert.Equal(Point(after, "ember:cold-open"), Point(after, "ember"));
    }

    [Fact]
    public void ReorderingChaptersMovesPositionsWithTheirChapters()
    {
        var source = MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot);
        source.With(EmberBook, "---\nname: Ember\nchapters: [prologue, 2b, 1, 2, 3]\n---\n");
        var u = Starfall(DenStructure.Load(source));

        Assert.True(Point(u, "ember:2b") < Point(u, "ember:1"));
        Assert.Equal("2b", u.Resolve(StoryPosition.Parse("ember:2b")).Resolved!.Chapter!.Key);
    }

    [Fact]
    public void EveryPositionInTheStarfallFixtureResolves()
    {
        var source = MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot);
        var den = DenStructure.Load(source);
        var positions = FixturePositions(source);

        Assert.Equal(25, positions.Count);
        foreach (var (path, reference) in positions)
        {
            var resolution = den.Resolve(path, reference.Position!);
            Assert.True(resolution.IsResolved, $"{path}:{reference.Line}: {resolution.Problem}");
        }
    }

    public static TheoryData<int> Seeds() => new(Enumerable.Range(0, 40));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneratedDensOrderPositionsTotallyAndConsistently(int seed)
    {
        var random = new Random(seed);
        var (source, expected) = GenerateDen(random);
        var den = DenStructure.Load(source);
        Assert.Empty(den.Diagnostics);
        var u = den.FindUniverse("u")!;

        // Every position resolves, in the order the den's lists give.
        var points = expected.Select(e => (e.Position, e.Rank, Point: Point(u, e.Position))).ToList();
        foreach (var a in points)
        {
            foreach (var b in points)
            {
                var ab = a.Point.CompareTo(b.Point);
                var ba = b.Point.CompareTo(a.Point);
                // Antisymmetry, and agreement with the expected story order.
                Assert.Equal(Math.Sign(ab), -Math.Sign(ba));
                Assert.Equal(a.Rank.CompareTo(b.Rank), Math.Sign(ab));
                Assert.Equal(ab == 0, a.Point == b.Point);
            }
        }

        // Transitivity, on a sample of triples.
        for (var i = 0; i < 300 && points.Count > 0; i++)
        {
            var (a, b, c) = (points[random.Next(points.Count)].Point, points[random.Next(points.Count)].Point, points[random.Next(points.Count)].Point);
            if (a <= b && b <= c)
            {
                Assert.True(a <= c);
            }

            if (a < b && b < c)
            {
                Assert.True(a < c);
            }
        }

        // Sorting is stable under shuffling.
        var sorted = points.OrderBy(_ => random.Next()).OrderBy(p => p.Point).Select(p => p.Rank).ToList();
        Assert.Equal(sorted.Order(), sorted);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneratedRenumberingKeepsReferentsAndOrder(int seed)
    {
        var random = new Random(seed);
        var (source, expected) = GenerateDen(random);
        var before = DenStructure.Load(source).FindUniverse("u")!;
        var positions = expected.Select(e => StoryPosition.Parse(e.Position)).ToList();

        // Renumber every book at random and insert new chapters, keeping the
        // existing keys in their order.
        foreach (var book in before.Books)
        {
            var lines = new List<string>();
            var n = random.Next(0, 3);
            foreach (var chapter in book.Chapters)
            {
                if (random.Next(3) == 0)
                {
                    lines.Add($"  - {{ key: new-{n}, number: {n++} }}");
                }

                lines.Add(random.Next(4) == 0 ? $"  - \"{chapter.Key}\"" : $"  - {{ key: \"{chapter.Key}\", number: {n++ * 10} }}");
            }

            var chapters = lines.Count == 0 ? "chapters: []" : "chapters:\n" + string.Join("\n", lines);
            source.With(book.FilePath, $"---\nname: {book.Name}\n{chapters}\n---\n");
        }

        var den = DenStructure.Load(source);
        Assert.DoesNotContain(den.Diagnostics, d => d.Diagnostic.Severity == DiagnosticSeverity.Error);
        var after = den.FindUniverse("u")!;
        AssertSameReferentsAndOrder(before, after, positions.Select(p => ("", new PositionReference(p.ToString(), p, PositionSite.EntityFrom, 0))).ToList());
    }

    private static void AssertSameReferentsAndOrder(Universe before, Universe after, IReadOnlyList<(string Path, PositionReference Reference)> positions)
    {
        var old = positions.Select(p => before.Resolve(p.Reference.Position!).Resolved!).ToList();
        var now = positions.Select(p => after.Resolve(p.Reference.Position!).Resolved!).ToList();
        for (var i = 0; i < old.Count; i++)
        {
            Assert.NotNull(now[i]);
            Assert.Equal(old[i].Book.Key, now[i].Book.Key);
            Assert.Equal(old[i].Chapter?.Key, now[i].Chapter?.Key);
            for (var j = 0; j < old.Count; j++)
            {
                // A bare position follows its book's first chapter, which an
                // insertion at the front can change; every other pair keeps its order.
                if (old[i].Position.IsBookStart || old[j].Position.IsBookStart)
                {
                    continue;
                }

                Assert.Equal(Math.Sign(old[i].CompareTo(old[j])), Math.Sign(now[i].CompareTo(now[j])));
            }
        }
    }

    /// <summary>Every position written in the fixture's entity files, with the file it's in.</summary>
    private static List<(string Path, PositionReference Reference)> FixturePositions(MemoryDenSource source)
    {
        var result = new List<(string, PositionReference)>();
        foreach (var path in source.Files.Where(f => f.Contains("/entities/", StringComparison.Ordinal)))
        {
            var file = MarkdownFile.Parse(source.ReadFile(path), requireFrontmatter: true);
            Assert.Empty(file.Diagnostics);
            var scan = PositionScanner.Scan(file, new HashSet<string> { "planted", "paid_off" });
            Assert.Empty(scan.Diagnostics);
            result.AddRange(scan.References.Select(r => (path, r)));
        }

        return result;
    }

    /// <summary>
    /// A random universe of series and standalone books, with every position
    /// it has and its expected rank in story order. A bare book position
    /// ranks with the book's first chapter (F-POS-7).
    /// </summary>
    private static (MemoryDenSource Source, List<(string Position, int Rank)> Expected) GenerateDen(Random random)
    {
        string[] chapterPool = ["prologue", "1", "2", "2b", "3", "014", "r-1", "rescue", "ch-07", "interlude", "epilogue", "9"];
        var source = MemoryDenSource.Den();
        var works = new List<(string Key, string? Series, List<(string Key, string[] Chapters)> Books)>();
        var bookCount = 0;
        for (var w = random.Next(1, 6); w > 0; w--)
        {
            var isSeries = random.Next(2) == 0;
            var books = new List<(string, string[])>();
            for (var b = isSeries ? random.Next(0, 4) : 1; b > 0; b--)
            {
                var chapters = chapterPool.OrderBy(_ => random.Next()).Take(random.Next(0, 6)).ToArray();
                books.Add(($"book-{bookCount++}", chapters));
            }

            works.Add(isSeries ? ($"series-{works.Count}", "series", books) : (books[0].Item1, null, books));
        }

        works = works.OrderBy(_ => random.Next()).ToList();
        source.Universe("u", $"order: [{string.Join(", ", works.Select(w => w.Key))}]");
        foreach (var work in works)
        {
            if (work.Series is not null)
            {
                source.Series("u", work.Key, $"books: [{string.Join(", ", work.Books.Select(b => b.Key))}]");
            }

            foreach (var (key, chapters) in work.Books)
            {
                source.Book("u", work.Series is null ? null : work.Key, key, $"chapters: [{string.Join(", ", chapters.Select(c => $"\"{c}\""))}]");
            }
        }

        var expected = new List<(string, int)>();
        var rank = 0;
        foreach (var (key, chapters) in works.SelectMany(w => w.Books))
        {
            expected.Add((key, rank));
            foreach (var chapter in chapters)
            {
                expected.Add(($"{key}:{chapter}", rank++));
            }

            if (chapters.Length == 0)
            {
                rank++;
            }
        }

        return (source, expected);
    }
}
