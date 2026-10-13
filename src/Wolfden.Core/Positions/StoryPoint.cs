namespace Wolfden.Core.Positions;

/// <summary>
/// A place in one universe's story order (format.md §8.2): the start of the
/// story, the start of a series or book, a chapter, or the end of the story.
/// Points of the same universe form a total order. Points of different
/// universes can't be compared, since positions don't cross universes
/// (F-POS-5).
/// </summary>
/// <remarks>
/// Two points are equal when they are the same place in story order. The
/// start of a book with chapters is the same place as its first chapter,
/// because a bare book position "orders the same as the book's first
/// chapter" for <c>from</c> and <c>until</c> (F-POS-7). A book with no
/// chapters yet still has a start, after every chapter of earlier books.
/// <c>default(StoryPoint)</c> belongs to no universe: it equals only itself,
/// and comparing it with a real point throws.
/// </remarks>
public readonly record struct StoryPoint : IComparable<StoryPoint>
{
    private const int StartSlot = int.MinValue;
    private const int EndSlot = int.MaxValue;

    internal StoryPoint(string universeKey, int slot, int chapter)
    {
        UniverseKey = universeKey;
        Slot = slot;
        Chapter = chapter;
    }

    /// <summary>The key of the universe whose story order this point is in.</summary>
    public string UniverseKey { get; }

    /// <summary>True for the start of the story, before every position (F-ORD-3).</summary>
    public bool IsStartOfStory => Slot == StartSlot;

    /// <summary>True for the end of the story, after every position (F-ORD-3).</summary>
    public bool IsEndOfStory => Slot == EndSlot;

    /// <summary>
    /// The point as one number that sorts in story order, for storing and
    /// range queries (an index compares <c>from &lt;= p &lt; until</c> as
    /// integers). The start of the story is <see cref="long.MinValue"/> and
    /// the end is <see cref="long.MaxValue"/>. Ordinals of different
    /// universes mean nothing to each other, and they change when the
    /// universe's books or chapters are reordered, so they're only valid for
    /// the structure they were computed from.
    /// </summary>
    public long Ordinal =>
        IsStartOfStory ? long.MinValue
        : IsEndOfStory ? long.MaxValue
        : ((long)Slot << 32) | (uint)Chapter;

    /// <summary>The place of the book (or empty series) in story order.</summary>
    internal int Slot { get; }

    /// <summary>The chapter's index in its book's <c>chapters</c> list; 0 for the start of a book.</summary>
    internal int Chapter { get; }

    public static bool operator <(StoryPoint left, StoryPoint right) => left.CompareTo(right) < 0;

    public static bool operator <=(StoryPoint left, StoryPoint right) => left.CompareTo(right) <= 0;

    public static bool operator >(StoryPoint left, StoryPoint right) => left.CompareTo(right) > 0;

    public static bool operator >=(StoryPoint left, StoryPoint right) => left.CompareTo(right) >= 0;

    /// <summary>True if the two points are in the same universe, so they can be compared.</summary>
    public bool IsComparableWith(StoryPoint other) => string.Equals(UniverseKey, other.UniverseKey, StringComparison.Ordinal);

    /// <summary>Compares two points of the same universe in story order (F-ORD-2, F-ORD-3).</summary>
    /// <exception cref="ArgumentException">The points are in different universes (F-POS-5).</exception>
    public int CompareTo(StoryPoint other)
    {
        if (!IsComparableWith(other))
        {
            throw new ArgumentException(
                $"Points in universe '{UniverseKey}' and universe '{other.UniverseKey}' can't be compared: story order doesn't cross universes.",
                nameof(other));
        }

        var bySlot = Slot.CompareTo(other.Slot);
        return bySlot != 0 ? bySlot : Chapter.CompareTo(other.Chapter);
    }

    /// <summary>The later of two points.</summary>
    public static StoryPoint Max(StoryPoint a, StoryPoint b) => a >= b ? a : b;

    /// <summary>The earlier of two points.</summary>
    public static StoryPoint Min(StoryPoint a, StoryPoint b) => a <= b ? a : b;

    /// <inheritdoc />
    public override string ToString() =>
        IsStartOfStory ? $"{UniverseKey}: start of story"
        : IsEndOfStory ? $"{UniverseKey}: end of story"
        : $"{UniverseKey}: slot {Slot}, chapter {Chapter}";

    internal static StoryPoint StartOfStory(string universeKey) => new(universeKey, StartSlot, 0);

    internal static StoryPoint EndOfStory(string universeKey) => new(universeKey, EndSlot, 0);
}
