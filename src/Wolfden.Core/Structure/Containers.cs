using Wolfden.Core.Documents;
using Wolfden.Core.Positions;

namespace Wolfden.Core.Structure;

/// <summary>What kind of container a folder is (format.md §6).</summary>
public enum ContainerKind
{
    /// <summary>A universe, with a <c>universe.md</c>.</summary>
    Universe,

    /// <summary>A series, with a <c>series.md</c>.</summary>
    Series,

    /// <summary>A book, with a <c>book.md</c>, in a series or standalone.</summary>
    Book,
}

/// <summary>Whether a book is frozen (format.md F-BOOK-1).</summary>
public enum BookStatus
{
    /// <summary>The default: the book can be edited.</summary>
    Draft,

    /// <summary>A frozen book.</summary>
    Final,
}

/// <summary>
/// A universe, series, or book that loaded without errors (format.md §6,
/// F-GEN-2). It's a read-only view of the den's structure: enough to
/// resolve and order story positions.
/// </summary>
public abstract class Container
{
    private readonly Universe? _universe;

    private protected Container(ContainerKind kind, Universe? universe, string key, string path, MarkdownFile file, string name, string? id)
    {
        _universe = universe;
        Kind = kind;
        Key = key;
        Path = path;
        File = file;
        Name = name;
        Id = id;
    }

    /// <summary>Universe, series, or book.</summary>
    public ContainerKind Kind { get; }

    /// <summary>The container's key: its folder name (F-KEY-3).</summary>
    public string Key { get; }

    /// <summary>The container folder, relative to the den root, with <c>/</c>.</summary>
    public string Path { get; }

    /// <summary>The container file's path, relative to the den root.</summary>
    public string FilePath => $"{Path}/{FileName(Kind)}";

    /// <summary>The parsed container file.</summary>
    public MarkdownFile File { get; }

    /// <summary>The display name (F-CTR-1).</summary>
    public string Name { get; }

    /// <summary>The container's id, or null if it has none yet (F-ID-2).</summary>
    public string? Id { get; }

    /// <summary>The universe the container is in, or the universe itself.</summary>
    public Universe Universe => _universe ?? (Universe)this;

    /// <summary>The container's place in story order: the start of the story,
    /// series, or book (F-ORD-3).</summary>
    public abstract StoryPoint Start { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind} '{Key}' ({Path})";

    internal static string FileName(ContainerKind kind) => kind switch
    {
        ContainerKind.Universe => "universe.md",
        ContainerKind.Series => "series.md",
        _ => "book.md",
    };
}

/// <summary>A universe: the boundary for story order and positions (format.md §6.2, F-POS-5).</summary>
public sealed class Universe : Container
{
    private readonly Dictionary<string, Book> _booksByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _clashingBookKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _excludedBookKeys = new(StringComparer.Ordinal);

    internal Universe(DenStructure den, string key, string path, MarkdownFile file, string name, string? id)
        : base(ContainerKind.Universe, null, key, path, file, name, id)
    {
        Den = den;
    }

    /// <summary>The den the universe belongs to.</summary>
    public DenStructure Den { get; }

    /// <summary>The start of the story, before every position (F-ORD-3).</summary>
    public override StoryPoint Start => StartOfStory;

    /// <summary>The start of the story, before every position (F-ORD-3).</summary>
    public StoryPoint StartOfStory => StoryPoint.StartOfStory(Key);

    /// <summary>The end of the story, after every position (F-ORD-3).</summary>
    public StoryPoint EndOfStory => StoryPoint.EndOfStory(Key);

    /// <summary>The universe's series and standalone books in story order (F-UNI-1, F-UNI-3).</summary>
    public IReadOnlyList<Container> Works { get; internal set; } = [];

    /// <summary>The universe's series, in story order.</summary>
    public IEnumerable<Series> Series => Works.OfType<Series>();

    /// <summary>Every book of the universe, series and standalone, in story order (F-ORD-1).</summary>
    public IReadOnlyList<Book> Books { get; internal set; } = [];

    /// <summary>
    /// The book with key <paramref name="key"/>, or null if there's none, or
    /// if more than one book of the universe has that key (F-KEY-5).
    /// </summary>
    public Book? FindBook(string key) => _booksByKey.TryGetValue(key, out var book) ? book : null;

    /// <summary>
    /// Resolves a position to a book and chapter of this universe (F-POS-4,
    /// F-POS-5, F-POS-7). A position that doesn't resolve comes back with the
    /// reason, for an F-POS-4 warning.
    /// </summary>
    public PositionResolution Resolve(StoryPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        var key = position.BookKey;
        if (_clashingBookKeys.Contains(key))
        {
            return PositionResolution.Failure(position,
                $"More than one book of universe '{Key}' has the key '{key}' (F-KEY-5), so '{position}' doesn't resolve until the keys are made unique.");
        }

        if (!_booksByKey.TryGetValue(key, out var book))
        {
            if (_excludedBookKeys.Contains(key))
            {
                return PositionResolution.Failure(position,
                    $"Book '{key}' has errors, so positions in it don't resolve until they're fixed.");
            }

            var elsewhere = Den.Universes.FirstOrDefault(u => u != this && u.FindBook(key) is not null);
            return PositionResolution.Failure(position, elsewhere is null
                ? $"Universe '{Key}' has no book '{key}'."
                : $"Universe '{Key}' has no book '{key}'. Universe '{elsewhere.Key}' has one, but positions don't cross universes (F-POS-5).");
        }

        if (position.ChapterKey is not { } chapterKey)
        {
            return PositionResolution.Success(new ResolvedPosition(position, book, null, book.Start));
        }

        if (book.Chapters.Count == 0)
        {
            return PositionResolution.Failure(position,
                $"Book '{key}' has no chapters yet, so '{position}' doesn't resolve. A bare '{key}' names the start of the book.");
        }

        return book.FindChapter(chapterKey) is { } chapter
            ? PositionResolution.Success(new ResolvedPosition(position, book, chapter, chapter.Point))
            : PositionResolution.Failure(position, $"Book '{key}' has no chapter '{chapterKey}'.");
    }

    internal void IndexBooks(IEnumerable<string> presentBookKeys, IEnumerable<Book> loadedBooks)
    {
        var counts = presentBookKeys.GroupBy(k => k, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var (key, count) in counts)
        {
            if (count > 1)
            {
                _clashingBookKeys.Add(key);
            }
        }

        foreach (var book in loadedBooks)
        {
            if (!_clashingBookKeys.Contains(book.Key))
            {
                _booksByKey[book.Key] = book;
            }
        }

        foreach (var key in counts.Keys)
        {
            if (!_clashingBookKeys.Contains(key) && !_booksByKey.ContainsKey(key))
            {
                _excludedBookKeys.Add(key);
            }
        }
    }
}

/// <summary>A series: books in story order (format.md §6.3).</summary>
public sealed class Series : Container
{
    internal Series(Universe universe, string key, string path, MarkdownFile file, string name, string? id)
        : base(ContainerKind.Series, universe, key, path, file, name, id)
    {
    }

    /// <summary>The series' books in story order (F-SER-1, F-SER-3).</summary>
    public IReadOnlyList<Book> Books { get; internal set; } = [];

    /// <summary>
    /// The start of the series: the start of its first book, or, for a
    /// series with no books yet, its own place in story order (F-ORD-3).
    /// </summary>
    public override StoryPoint Start => Books.Count > 0 ? Books[0].Start : new StoryPoint(Universe.Key, Slot, 0);

    internal int Slot { get; set; }
}

/// <summary>A book: an ordered list of chapters (format.md §6.4).</summary>
public sealed class Book : Container
{
    private readonly Dictionary<string, Chapter> _chaptersByKey = new(StringComparer.Ordinal);
    private IReadOnlyList<Chapter> _chapters = [];

    internal Book(Universe universe, Series? series, string key, string path, MarkdownFile file, string name, string? id, BookStatus status)
        : base(ContainerKind.Book, universe, key, path, file, name, id)
    {
        Series = series;
        Status = status;
    }

    /// <summary>The series the book is in, or null for a standalone book.</summary>
    public Series? Series { get; }

    /// <summary>Draft or final (F-BOOK-1).</summary>
    public BookStatus Status { get; }

    /// <summary>The chapters, in chapter order (F-BOOK-2).</summary>
    public IReadOnlyList<Chapter> Chapters
    {
        get => _chapters;
        internal set
        {
            _chapters = value;
            _chaptersByKey.Clear();
            foreach (var chapter in value)
            {
                _chaptersByKey.TryAdd(chapter.Key, chapter);
            }
        }
    }

    /// <summary>
    /// The start of the book (F-ORD-3, F-POS-7): after every chapter of
    /// earlier books. It's the same place as the first chapter, if the book
    /// has chapters.
    /// </summary>
    public override StoryPoint Start => new(Universe.Key, Slot, 0);

    internal int Slot { get; set; }

    /// <summary>The chapter with key <paramref name="key"/>, or null.</summary>
    public Chapter? FindChapter(string key) => _chaptersByKey.GetValueOrDefault(key);
}

/// <summary>
/// One item of a book's <c>chapters</c> list (format.md F-BOOK-3). Its key
/// is stable. Its number is for display only and never affects order or
/// positions.
/// </summary>
public sealed class Chapter
{
    internal Chapter(Book book, string key, int index, long? number, string? title, int line)
    {
        Book = book;
        Key = key;
        Index = index;
        Number = number;
        Title = title;
        Line = line;
    }

    /// <summary>The book the chapter belongs to.</summary>
    public Book Book { get; }

    /// <summary>The chapter key, which positions name.</summary>
    public string Key { get; }

    /// <summary>The chapter's 0-based place in the book's <c>chapters</c> list.</summary>
    public int Index { get; }

    /// <summary>The display number, or null if it has none (F-BOOK-3).</summary>
    public long? Number { get; }

    /// <summary>The title, or null.</summary>
    public string? Title { get; }

    /// <summary>The 1-based line of the item in <c>book.md</c>.</summary>
    public int Line { get; }

    /// <summary>The chapter's place in story order (F-ORD-2).</summary>
    public StoryPoint Point => new(Book.Universe.Key, Book.Slot, Index);

    /// <inheritdoc />
    public override string ToString() => $"{Book.Key}:{Key}";
}
