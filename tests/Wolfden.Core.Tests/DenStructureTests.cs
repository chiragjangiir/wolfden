using Wolfden.Core.Checks;
using Wolfden.Core.Documents;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>Loading a den's structure (format.md §5, §6) and resolving positions against it (§8).</summary>
public class DenStructureTests
{
    private static DenStructure Load(MemoryDenSource source) => DenStructure.Load(source);

    private static string[] Keys(IEnumerable<Container> containers) => containers.Select(c => c.Key).ToArray();

    private static (string Path, string Rule, DiagnosticSeverity Severity)[] Rules(DenStructure den) =>
        den.Diagnostics.Select(d => (d.Path, d.Diagnostic.RuleId, d.Diagnostic.Severity)).ToArray();

    private static PositionResolution Resolve(Universe universe, string position) => universe.Resolve(StoryPosition.Parse(position));

    [Fact]
    public void StarfallLoadsWithNoDiagnostics()
    {
        var den = DenStructure.Load(MemoryDenSource.StarfallRoot);

        Assert.Empty(den.Diagnostics);
        Assert.True(den.IsLoaded);
        Assert.Equal(1, den.Format);
        Assert.Equal(["drift", "starfall"], Keys(den.Universes));
        var starfall = den.FindUniverse("starfall")!;
        Assert.Equal(["long-dark", "a-quiet-moon", "drift-war"], Keys(starfall.Works));
        Assert.Equal(["ember", "ash", "a-quiet-moon"], Keys(starfall.Books));
        var ember = starfall.FindBook("ember")!;
        Assert.Equal("long-dark", ember.Series!.Key);
        Assert.Equal("Ember", ember.Name);
        Assert.Equal("01JAQKFBF5KZNWJ47TAN9ZT24M", ember.Id);
        Assert.Equal(BookStatus.Draft, ember.Status);
        Assert.Equal(["prologue", "1", "2", "2b", "3"], ember.Chapters.Select(c => c.Key));
        Assert.Equal([null, 1L, 2L, 3L, 4L], ember.Chapters.Select(c => c.Number));
        Assert.Equal("The Long Watch", ember.FindChapter("2b")!.Title);
        Assert.Equal(9, ember.FindChapter("2b")!.Line);
        Assert.Null(starfall.FindBook("a-quiet-moon")!.Series);
    }

    [Fact]
    public void FileSystemAndMemorySourcesAgree()
    {
        var disk = DenStructure.Load(MemoryDenSource.StarfallRoot);
        var memory = Load(MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot));

        Assert.Equal(disk.Universes.SelectMany(u => u.Books).Select(b => b.Path), memory.Universes.SelectMany(u => u.Books).Select(b => b.Path));
    }

    [Theory]
    [InlineData("universes/starfall/entities/locations/kessan-reach.md", "starfall")]
    [InlineData("universes/starfall/universe.md", "starfall")]
    [InlineData("universes/starfall/series/long-dark/entities/characters/mara-voss.md", "long-dark")]
    [InlineData("universes/starfall/series/long-dark/books/ash/entities/characters/halden-pike.md", "ash")]
    [InlineData(@"universes\starfall\series\long-dark\books\ember\book.md", "ember")]
    [InlineData("universes/starfall/books/a-quiet-moon/outline.md", "a-quiet-moon")]
    [InlineData("universes/starfall/series/drift-war/series.md", "drift-war")]
    [InlineData("universes/starfall/entities/series/x.md", "starfall")]
    [InlineData("universes/drift/books/tide/entities/x.md", "tide")]
    [InlineData("universes/nowhere/universe.md", null)]
    [InlineData("universes/starfall/series/nowhere/series.md", null)]
    [InlineData("universes/starfall/books/ember/book.md", null)]
    [InlineData("notes/x.md", null)]
    [InlineData("universes/starfall/../starfall/universe.md", null)]
    [InlineData("/universes/starfall/universe.md", null)]
    [InlineData("universes//starfall/universe.md", null)]
    [InlineData("./universes/starfall/universe.md", null)]
    [InlineData("den.yaml", null)]
    public void FindScopeReturnsTheNearestContainer(string path, string? expected)
    {
        var den = DenStructure.Load(MemoryDenSource.StarfallRoot);

        Assert.Equal(expected, den.FindScope(path)?.Key);
    }

    [Fact]
    public void PositionsResolveToBooksAndChapters()
    {
        var starfall = DenStructure.Load(MemoryDenSource.StarfallRoot).FindUniverse("starfall")!;

        var chapter = Resolve(starfall, "ember:2b");
        Assert.True(chapter.IsResolved);
        Assert.Equal("ember", chapter.Resolved.Book.Key);
        Assert.Equal("2b", chapter.Resolved.Chapter!.Key);
        Assert.Equal(3L, chapter.Resolved.Chapter.Number);
        Assert.Null(chapter.ToDiagnostic(4));

        var bare = Resolve(starfall, "a-quiet-moon");
        Assert.True(bare.IsResolved);
        Assert.Null(bare.Resolved.Chapter);
        Assert.Equal(starfall.FindBook("a-quiet-moon")!.Start, bare.Resolved.Point);
    }

    [Theory]
    [InlineData("ember:99", "has no chapter '99'")]
    [InlineData("ember:4", "has no chapter '4'")]
    [InlineData("ember-two:1", "has no book 'ember-two'")]
    [InlineData("ember-two", "has no book 'ember-two'")]
    [InlineData("long-dark:1", "has no book 'long-dark'")]
    [InlineData("tide:1", "positions don't cross universes (F-POS-5)")]
    [InlineData("tide", "positions don't cross universes (F-POS-5)")]
    public void UnresolvedPositionsAreWarnings(string position, string reason)
    {
        var starfall = DenStructure.Load(MemoryDenSource.StarfallRoot).FindUniverse("starfall")!;

        var resolution = Resolve(starfall, position);

        Assert.False(resolution.IsResolved);
        Assert.Contains(reason, resolution.Problem);
        var d = resolution.ToDiagnostic(7)!;
        Assert.Equal(("F-POS-4", DiagnosticSeverity.Warning, 7), (d.RuleId, d.Severity, d.Line));
    }

    [Fact]
    public void ABookWithNoChaptersResolvesOnlyItsStart()
    {
        var den = Load(MemoryDenSource.Den()
            .Book("u", null, "draft-one")
            .Book("u", null, "draft-two", "chapters: []")
            .Universe("u", "order: [draft-one, draft-two]"));
        var u = den.FindUniverse("u")!;

        Assert.Empty(den.Diagnostics);
        foreach (var book in new[] { "draft-one", "draft-two" })
        {
            Assert.True(Resolve(u, book).IsResolved);
            var chapter = Resolve(u, $"{book}:1");
            Assert.False(chapter.IsResolved);
            Assert.Contains("has no chapters yet", chapter.Problem);
        }

        Assert.True(u.FindBook("draft-one")!.Start < u.FindBook("draft-two")!.Start);
    }

    [Fact]
    public void ChapterItemsCanBeScalarsOrMappings()
    {
        var den = Load(MemoryDenSource.Den().Universe("u").Book("u", null, "b",
            "chapters:",
            "  - prologue",
            "  - 1",
            "  - '014'",
            "  - { key: 2b, number: 0x3, title: Watch }",
            "  - key: \"3\"",
            "    number: +4",
            "    fields: { pov: \"[[mara-voss]]\" }",
            "    x-note: kept"));

        Assert.Empty(den.Diagnostics);
        var book = den.FindUniverse("u")!.FindBook("b")!;
        Assert.Equal(["prologue", "1", "014", "2b", "3"], book.Chapters.Select(c => c.Key));
        Assert.Equal([null, null, null, 3L, 4L], book.Chapters.Select(c => c.Number));
        Assert.Equal([0, 1, 2, 3, 4], book.Chapters.Select(c => c.Index));
    }

    [Theory]
    [InlineData("chapters: [1, 1]", "F-BOOK-4")]
    [InlineData("chapters: [{ title: no key }]", "F-BOOK-3")]
    [InlineData("chapters: [~]", "F-BOOK-3")]
    [InlineData("chapters: [[1, 2]]", "F-BOOK-3")]
    [InlineData("chapters: [{ key: [1] }]", "F-BOOK-3")]
    [InlineData("chapters: [{ key: ~ }]", "F-BOOK-3")]
    [InlineData("chapters: [Prologue]", "F-KEY-1")]
    [InlineData("chapters: [\"14.2\"]", "F-KEY-1")]
    [InlineData("chapters: [\"ch:1\"]", "F-KEY-1")]
    [InlineData("chapters: [r75]", "F-KEY-2")]
    [InlineData("chapters: prologue", "F-BOOK-2")]
    [InlineData("chapters: { key: prologue }", "F-BOOK-2")]
    [InlineData("status: frozen", "F-BOOK-1")]
    [InlineData("status: [final]", "F-BOOK-1")]
    [InlineData("id: 01ja7q8m4xk3v9r2d5w8hn6tqb", "F-ID-1")]
    [InlineData("id: 81JA7Q8M4XK3V9R2D5W8HN6TQB", "F-ID-1")]
    [InlineData("name: \"\"", "F-CTR-1")]
    public void ABookWithAnErrorIsLeftOut(string line, string rule)
    {
        var source = MemoryDenSource.Den().Universe("u");
        source.With("universes/u/books/ember/book.md", line.StartsWith("name:", StringComparison.Ordinal)
            ? $"---\n{line}\nchapters: [1]\n---\n"
            : $"---\nname: Ember\n{line}\n---\n");

        var den = Load(source);

        Assert.Contains(("universes/u/books/ember/book.md", rule, DiagnosticSeverity.Error), Rules(den));
        var u = den.FindUniverse("u")!;
        Assert.Empty(u.Books);
        Assert.Null(den.FindScope("universes/u/books/ember/entities/x.md"));
        var resolution = Resolve(u, "ember:1");
        Assert.False(resolution.IsResolved);
        Assert.Contains("has errors", resolution.Problem);
    }

    [Theory]
    [InlineData("---\nname: Ember\nchapters: [1\n---\n", "F-YAML-1")]
    [InlineData("name: Ember\n", "F-FM-1")]
    [InlineData("---\n---\n", "F-CTR-1")]
    [InlineData("---\nid: 01JA7Q8M4XK3V9R2D5W8HN6TQB\n---\n", "F-CTR-1")]
    public void ContainerFileParseErrorsLeaveItOut(string text, string rule)
    {
        var den = Load(MemoryDenSource.Den().Universe("u").With("universes/u/books/ember/book.md", text));

        Assert.Contains(("universes/u/books/ember/book.md", rule, DiagnosticSeverity.Error), Rules(den));
        Assert.Empty(den.FindUniverse("u")!.Books);
    }

    [Fact]
    public void ChapterNumbersAreDisplayOnlyAndBadOnesAreWarnings()
    {
        var den = Load(MemoryDenSource.Den().Universe("u").Book("u", null, "b",
            "chapters:",
            "  - { key: a, number: \"3\" }",
            "  - { key: b, number: 3.5 }",
            "  - { key: c, number: -1 }",
            "  - { key: d, number: 5 }",
            "  - { key: e, number: 4 }",
            "  - { key: f, number: 4 }",
            "  - { key: g, number: ~ }"));

        Assert.Equal(
        [
            ("F-BOOK-3", 4), ("F-BOOK-3", 5), ("F-BOOK-3", 6), ("F-BOOK-5", 8), ("F-BOOK-5", 9),
        ], den.Diagnostics.Select(d => (d.Diagnostic.RuleId, d.Diagnostic.Line ?? 0)));
        Assert.All(den.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Diagnostic.Severity));
        var book = den.FindUniverse("u")!.FindBook("b")!;
        Assert.Equal([null, null, null, 5L, 4L, 4L, null], book.Chapters.Select(c => c.Number));
        // Order follows the list, never the numbers.
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"], book.Chapters.OrderBy(c => c.Point).Select(c => c.Key));
    }

    [Fact]
    public void UniverseOrderPutsSeriesBooksInPlace()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u", "order: [b-series, a-book, c-series]")
            .Series("u", "b-series", "books: [two, one]")
            .Book("u", "b-series", "one", "chapters: [1]")
            .Book("u", "b-series", "two", "chapters: [1]")
            .Book("u", null, "a-book", "chapters: [1]")
            .Series("u", "c-series", "books: [three]")
            .Book("u", "c-series", "three", "chapters: [1]"));

        Assert.Empty(den.Diagnostics);
        Assert.Equal(["two", "one", "a-book", "three"], Keys(den.FindUniverse("u")!.Books));
    }

    [Fact]
    public void WorksMissingFromOrderComeAfterByKeyWithAWarning()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u", "order: [middle, nowhere, 7, ~, [x]]")
            .Book("u", null, "zulu", "chapters: [1]")
            .Book("u", null, "middle", "chapters: [1]")
            .Series("u", "alpha")
            .Book("u", "alpha", "only", "chapters: [1]"));

        var u = den.FindUniverse("u")!;
        Assert.Equal(["middle", "alpha", "zulu"], Keys(u.Works));
        Assert.Equal(["middle", "only", "zulu"], Keys(u.Books));
        Assert.Equal(
        [
            ("F-UNI-2", 3), ("F-UNI-2", 3), ("F-UNI-2", 3), ("F-UNI-2", 3), ("F-UNI-3", 3), ("F-UNI-3", 3),
        ], den.Diagnostics.Select(d => (d.Diagnostic.RuleId, d.Diagnostic.Line ?? 0)));
        Assert.All(den.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Diagnostic.Severity));
    }

    [Fact]
    public void OrderMayBeOmittedWithOneWork()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u")
            .Series("u", "s")
            .Book("u", "s", "only", "chapters: [1]"));

        Assert.Empty(den.Diagnostics);
    }

    [Fact]
    public void WithoutOrderWorksGoByKey()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u")
            .Book("u", null, "b", "chapters: [1]")
            .Book("u", null, "a", "chapters: [1]")
            .Book("u", null, "a-2", "chapters: [1]")
            .Book("u", null, "a1", "chapters: [1]"));

        Assert.Equal(["a", "a-2", "a1", "b"], Keys(den.FindUniverse("u")!.Books));
        Assert.Equal(4, den.Diagnostics.Count(d => d.Diagnostic.RuleId == "F-UNI-3"));
    }

    [Fact]
    public void SeriesBooksMissingFromBooksComeAfterByKey()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u")
            .Series("u", "s", "books: [ash, nowhere]")
            .Book("u", "s", "ember", "chapters: [1]")
            .Book("u", "s", "ash", "chapters: [1]")
            .Book("u", "s", "coal", "chapters: [1]"));

        Assert.Equal(["ash", "coal", "ember"], Keys(den.FindUniverse("u")!.Books));
        Assert.Equal(
        [
            ("universes/u/series/s/series.md", "F-SER-2", DiagnosticSeverity.Warning),
            ("universes/u/series/s/series.md", "F-SER-3", DiagnosticSeverity.Warning),
            ("universes/u/series/s/series.md", "F-SER-3", DiagnosticSeverity.Warning),
        ], Rules(den));
    }

    [Theory]
    [InlineData("order: [a, b, a]", "universes/u/universe.md", "F-UNI-2")]
    [InlineData("order: a", "universes/u/universe.md", "F-UNI-1")]
    public void ARepeatedOrBadOrderLeavesTheUniverseOut(string order, string path, string rule)
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u", order)
            .Book("u", null, "a", "chapters: [1]")
            .Book("u", null, "b", "chapters: [1]"));

        Assert.Contains((path, rule, DiagnosticSeverity.Error), Rules(den));
        Assert.Empty(den.Universes);
        Assert.Null(den.FindScope("universes/u/books/a/book.md"));
    }

    [Theory]
    [InlineData("books: [a, a]", "F-SER-2")]
    [InlineData("books: { a: 1 }", "F-SER-1")]
    public void ARepeatedOrBadBooksListLeavesTheSeriesOut(string books, string rule)
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u", "order: [s, t]")
            .Series("u", "s", books)
            .Book("u", "s", "a", "chapters: [1]")
            .Series("u", "t")
            .Book("u", "t", "z", "chapters: [1]"));

        Assert.Contains(("universes/u/series/s/series.md", rule, DiagnosticSeverity.Error), Rules(den));
        var u = den.FindUniverse("u")!;
        // The series is left out, but the order entry naming it isn't a warning:
        // the folder is there, it just has errors.
        Assert.DoesNotContain(den.Diagnostics, d => d.Diagnostic.RuleId.StartsWith("F-UNI", StringComparison.Ordinal));
        Assert.Equal(["t"], Keys(u.Works));
        Assert.Equal(["z"], Keys(u.Books));
        Assert.Null(den.FindScope("universes/u/series/s/entities/x.md"));
        Assert.Contains("in a series that has errors", Resolve(u, "a:1").Problem);
    }

    [Fact]
    public void DuplicateBookKeysDontResolve()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u", "order: [s, t]")
            .Series("u", "s", "books: [ember, ash]")
            .Book("u", "s", "ember", "chapters: [1]")
            .Book("u", "s", "ash", "chapters: [1]")
            .Series("u", "t", "books: [ember]")
            .Book("u", "t", "ember", "chapters: [1, 2]"));

        Assert.Equal(
        [
            ("universes/u/series/s/books/ember/book.md", "F-KEY-5", DiagnosticSeverity.Warning),
            ("universes/u/series/t/books/ember/book.md", "F-KEY-5", DiagnosticSeverity.Warning),
        ], Rules(den));
        var u = den.FindUniverse("u")!;
        Assert.Null(u.FindBook("ember"));
        Assert.Contains("F-KEY-5", Resolve(u, "ember:1").Problem);
        Assert.Contains("F-KEY-5", Resolve(u, "ember").Problem);
        Assert.True(Resolve(u, "ash:1").IsResolved);
        // Both books keep their places in story order, and their scopes still work.
        Assert.Equal(3, u.Books.Count);
        Assert.Equal("ember", den.FindScope("universes/u/series/t/books/ember/entities/x.md")!.Key);
    }

    [Fact]
    public void ASeriesKeyThatIsAlsoAStandaloneBookKeyIsIgnoredInOrder()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u", "order: [ember, other]")
            .Series("u", "ember")
            .Book("u", "ember", "one", "chapters: [1]")
            .Book("u", null, "ember", "chapters: [1]")
            .Book("u", null, "other", "chapters: [1]"));

        var rules = Rules(den);
        Assert.Contains(("universes/u/series/ember/series.md", "F-KEY-5", DiagnosticSeverity.Warning), rules);
        Assert.Contains(("universes/u/books/ember/book.md", "F-KEY-5", DiagnosticSeverity.Warning), rules);
        Assert.Contains(("universes/u/universe.md", "F-UNI-2", DiagnosticSeverity.Warning), rules);
        var u = den.FindUniverse("u")!;
        // The clashing works come after the listed one, by key, the series first.
        Assert.Equal([("other", ContainerKind.Book), ("ember", ContainerKind.Series), ("ember", ContainerKind.Book)],
            u.Works.Select(w => (w.Key, w.Kind)));
        // The book key itself is unique, so it still resolves.
        Assert.True(Resolve(u, "ember:1").IsResolved);
    }

    [Theory]
    [InlineData("Ember", "F-KEY-1")]
    [InlineData("ember_two", "F-KEY-1")]
    [InlineData("7th-fleet", "F-KEY-2")]
    public void ContainerFoldersMustHaveValidKeys(string folder, string rule)
    {
        var den = Load(MemoryDenSource.Den().Universe("u").Book("u", null, folder, "chapters: [1]"));

        Assert.Equal([($"universes/u/books/{folder}", rule, DiagnosticSeverity.Error)], Rules(den));
        Assert.Empty(den.FindUniverse("u")!.Books);
    }

    [Fact]
    public void AContainerFolderWithoutItsFileIsSkipped()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u")
            .With("universes/u/books/ember/outline.md", "| row | beat |\n")
            .With("universes/v/entities/x.md", "---\nname: x\n---\n"));

        Assert.Equal(
        [
            ("universes/u/books/ember", "F-LAY-4", DiagnosticSeverity.Error),
            ("universes/v", "F-LAY-4", DiagnosticSeverity.Error),
        ], Rules(den));
        Assert.Equal(["u"], Keys(den.Universes));
    }

    [Fact]
    public void HiddenFoldersAreIgnored()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u")
            .With("universes/.trash/universe.md", "oops")
            .With("universes/u/books/.obsidian/x.json", "{}"));

        Assert.Empty(den.Diagnostics);
    }

    [Theory]
    [InlineData(null, "F-DEN-1")]
    [InlineData("format: 0\n", "F-DEN-1")]
    [InlineData("format: -1\n", "F-DEN-1")]
    [InlineData("format: 0x1\n", "F-DEN-1")]
    [InlineData("format: 99999999999999999\n", "F-DEN-1")]
    [InlineData("name: x\n", "F-DEN-1")]
    [InlineData("format: \"1\"\n", "F-DEN-1")]
    [InlineData("format: one\n", "F-DEN-1")]
    [InlineData("format: 2\n", "F-DEN-1")]
    [InlineData("format: [1]\n", "F-DEN-1")]
    [InlineData("format: 1\nformat: 1\n", "F-YAML-3")]
    [InlineData("- format\n", "F-YAML-2")]
    public void ADenThatCantLoadSaysWhy(string? denYaml, string rule)
    {
        var source = new MemoryDenSource();
        if (denYaml is not null)
        {
            source.With("den.yaml", denYaml);
        }

        source.Universe("u");
        var den = Load(source);

        Assert.False(den.IsLoaded);
        Assert.Empty(den.Universes);
        Assert.Contains(den.Diagnostics, d => d.Diagnostic.RuleId == rule && d.Diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ANewerFormatSaysSo()
    {
        var den = Load(new MemoryDenSource().With("den.yaml", "format: 2\n"));

        Assert.Contains("den format 2 is newer than this version of Wolfden", den.Diagnostics.Single().Diagnostic.Message);
    }

    [Fact]
    public void FinalBooksLoad()
    {
        var den = Load(MemoryDenSource.Den().Universe("u").Book("u", null, "b", "status: final"));

        Assert.Equal(BookStatus.Final, den.FindUniverse("u")!.FindBook("b")!.Status);
    }

    [Fact]
    public void ResolvingInAFileOutsideAUniverseThrows()
    {
        var den = DenStructure.Load(MemoryDenSource.StarfallRoot);

        Assert.Throws<ArgumentException>(() => den.Resolve("notes/x.md", StoryPosition.Parse("ember:1")));
        Assert.True(den.Resolve("universes/starfall/entities/x.md", StoryPosition.Parse("ember:1")).IsResolved);
        Assert.False(den.Resolve("universes/drift/entities/x.md", StoryPosition.Parse("ember:1")).IsResolved);
    }

    [Theory]
    [InlineData("format: 0\n", "formats start at 1")]
    [InlineData("format: -1\n", "formats start at 1")]
    [InlineData("format: -99999999999999999\n", "formats start at 1")]
    [InlineData("format: 99999999999999999\n", "is newer than this version of Wolfden")]
    [InlineData("format: +1\n", null)]
    public void FormatsOutsideTheSupportedRangeSaySo(string denYaml, string? message)
    {
        var den = Load(new MemoryDenSource().With("den.yaml", denYaml));

        Assert.Equal(message is null, den.IsLoaded);
        if (message is not null)
        {
            Assert.Contains(message, den.Diagnostics.Single().Diagnostic.Message);
        }
    }

    [Fact]
    public void BooksUnderASeriesWithErrorsStillCountForKeyClashes()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u", "order: [s, t]")
            .Series("u", "s", "books: [b1]")
            .Book("u", "s", "b1", "chapters: [1]")
            .Series("u", "t", "books: b1")
            .Book("u", "t", "b1", "chapters: [1]")
            .Book("u", "t", "b2", "chapters: [1]"));

        var rules = Rules(den);
        Assert.Contains(("universes/u/series/t/series.md", "F-SER-1", DiagnosticSeverity.Error), rules);
        Assert.Contains(("universes/u/series/s/books/b1/book.md", "F-KEY-5", DiagnosticSeverity.Warning), rules);
        Assert.Contains(("universes/u/series/t/books/b1/book.md", "F-KEY-5", DiagnosticSeverity.Warning), rules);
        var u = den.FindUniverse("u")!;
        Assert.Contains("F-KEY-5", Resolve(u, "b1:1").Problem);
        Assert.Contains("in a series that has errors", Resolve(u, "b2:1").Problem);
        // The unread books get no diagnostics of their own (F-GEN-2).
        Assert.DoesNotContain(rules, r => r.Path.StartsWith("universes/u/series/t/books/b2", StringComparison.Ordinal));
    }

    [Fact]
    public void AListedFolderWithoutItsContainerFileIsReportedOnce()
    {
        var den = Load(MemoryDenSource.Den()
            .Universe("u", "order: [s, gone]")
            .Series("u", "s", "books: [b1, b2]")
            .Book("u", "s", "b1", "chapters: [1]")
            .With("universes/u/series/s/books/b2/outline.md", "notes\n")
            .With("universes/u/books/gone/outline.md", "notes\n"));

        Assert.Equal(
        [
            ("universes/u/books/gone", "F-LAY-4", DiagnosticSeverity.Error),
            ("universes/u/series/s/books/b2", "F-LAY-4", DiagnosticSeverity.Error),
        ], Rules(den));
    }

    [Fact]
    public void NamesMatchInExactCaseOnEveryFileSystem()
    {
        var root = Path.Combine(Path.GetTempPath(), "wolfden-case-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "universes", "u", "books", "ember"));
            File.WriteAllText(Path.Combine(root, "den.yaml"), "format: 1\n");
            File.WriteAllText(Path.Combine(root, "universes", "u", "Universe.md"), "---\nname: u\n---\n");
            File.WriteAllText(Path.Combine(root, "universes", "u", "books", "ember", "book.md"), "---\nname: Ember\n---\n");

            var source = new FileSystemDenSource(root);
            var den = DenStructure.Load(source);

            // Universe.md isn't universe.md, and Books/ isn't books/, on Windows too.
            Assert.Equal([("universes/u", "F-LAY-4", DiagnosticSeverity.Error)], Rules(den));
            Assert.Empty(den.Universes);
            Assert.False(source.FileExists("DEN.yaml"));
            Assert.False(source.FileExists("universes/U/Universe.md"));
            Assert.True(source.FileExists("universes/u/books/ember/book.md"));
            Assert.False(source.FileExists("universes/u/Books/ember/book.md"));
            Assert.Empty(source.GetDirectories("Universes"));
            Assert.Empty(source.GetDirectories("universes/u/Books"));
            Assert.Equal(["ember"], source.GetDirectories("universes/u/books"));
            Assert.False(source.FileExists("universes/u/books"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-40")]
    public void APathLookupListsEachFolderOnTheWayAboutOnce()
    {
        // Each path lookup checks every folder on the way in exact case. It
        // used to list each one again, so every lookup in a book listed all
        // of books/, and a check of thousands of standalone books took
        // quadratic time (#119).
        const int books = 300;
        var root = Path.Combine(Path.GetTempPath(), "wolfden-books-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(root).FullName, "den.yaml"), "format: 1\n");
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "universes", "u")).FullName, "universe.md"), "---\nname: U\n---\n");
            for (var i = 0; i < books; i++)
            {
                var book = Path.Combine(root, "universes", "u", "books", $"b{i}");
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(book).FullName, "book.md"), "---\nname: B\nchapters: [1]\n---\n");
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(book, "entities", "characters")).FullName, $"c{i}.md"), "---\ntype: character\nname: C\n---\n");
            }

            var folders = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length + 1;
            var source = new FileSystemDenSource(root);
            var check = DenCheck.Run(source);

            Assert.Equal(books, check.Structure.Universes.Single().Books.Count);
            Assert.Equal(books, check.Catalog.Entities.Count);
            Assert.InRange(source.LookupListings, 1, folders * 2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void APathLookupSeesFoldersMadeOrDeletedAfterItsParentWasListed()
    {
        var root = Path.Combine(Path.GetTempPath(), "wolfden-later-" + Guid.NewGuid().ToString("N"));
        try
        {
            var books = Directory.CreateDirectory(Path.Combine(root, "universes", "u", "books")).FullName;
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(books, "ember")).FullName, "book.md"), "---\nname: Ember\n---\n");
            var source = new FileSystemDenSource(root);
            Assert.True(source.FileExists("universes/u/books/ember/book.md"));
            Assert.False(source.FileExists("universes/u/books/ash/book.md"));

            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(books, "ash")).FullName, "book.md"), "---\nname: Ash\n---\n");
            Directory.Delete(Path.Combine(books, "ember"), recursive: true);

            Assert.True(source.FileExists("universes/u/books/ash/book.md"));
            Assert.Equal(["book.md"], source.GetFiles("universes/u/books/ash"));
            Assert.False(source.FileExists("universes/u/books/ember/book.md"));
            Assert.False(source.FolderExists("universes/u/books/ember"));
            Assert.Empty(source.GetFiles("universes/u/books/ember"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
