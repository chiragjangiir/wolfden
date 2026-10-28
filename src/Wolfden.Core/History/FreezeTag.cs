using System.Globalization;
using Wolfden.Core.Structure;

namespace Wolfden.Core.History;

/// <summary>What happened to a freeze tag.</summary>
public enum FreezeTagOutcome
{
    /// <summary>The tag was made.</summary>
    Tagged,

    /// <summary>The den has no history that can make tags, so there's no tag; the book is still frozen by <c>status: final</c>.</summary>
    NoHistory,

    /// <summary>The den has history, but nothing in it has been checkpointed yet, so there was nothing to tag.</summary>
    NothingCheckpointed,
}

/// <summary>The outcome of adding a freeze tag.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Name">The tag's name: <c>book/&lt;universe&gt;/&lt;book&gt;/final</c>, or <c>final-2</c>, <c>final-3</c>, and so on after the first.</param>
/// <param name="Tag">The tag made; null if none was.</param>
/// <param name="Message">What happened, in words for the writer.</param>
public sealed record FreezeTagResult(FreezeTagOutcome Outcome, string Name, HistoryTag? Tag, string Message);

/// <summary>
/// The tag that freezing a book adds when the den has history (design.md
/// §5, F-BOOK-1): an extra snapshot, not the freeze itself, which is
/// <c>status: final</c> in <c>book.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Freeze tags are named <c>book/&lt;universe&gt;/&lt;book&gt;/final</c>
/// (maintainer decision, 2026-10-23, #31): book keys are unique only within
/// a universe (F-KEY-5), so the universe's key is part of the name.
/// </para>
/// <para>
/// <b>Each freeze keeps its own tag</b> (maintainer decision, 2026-10-28):
/// a book frozen again after it was unfrozen gets the next name,
/// <c>final-2</c>, <c>final-3</c>, and so on: one more than the highest
/// number already there, so the highest number is always the latest
/// freeze. Tags belong to the whole repository, and another den in it could
/// use the same keys; Wolfden can't tell such a tag from its own, so it
/// counts up past it too. A tag is never moved or deleted.
/// </para>
/// </remarks>
public static class FreezeTag
{
    /// <summary>The most a freeze tag's number can be; the name would be too long to read before then.</summary>
    private const int Highest = 999_999_999;

    /// <summary>
    /// The freeze tag's name for book <paramref name="bookKey"/> of universe
    /// <paramref name="universeKey"/>: <c>…/final</c> for the first freeze,
    /// <c>…/final-<paramref name="number"/></c> for a later one.
    /// </summary>
    public static string NameFor(string universeKey, string bookKey, int number = 1)
    {
        ArgumentNullException.ThrowIfNull(universeKey);
        ArgumentNullException.ThrowIfNull(bookKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        if (!Keys.IsContainerKey(universeKey))
        {
            throw new ArgumentException($"'{universeKey}' isn't a universe key.", nameof(universeKey));
        }

        if (!Keys.IsContainerKey(bookKey))
        {
            throw new ArgumentException($"'{bookKey}' isn't a book key.", nameof(bookKey));
        }

        var name = $"book/{universeKey}/{bookKey}/final";
        return number == 1 ? name : $"{name}-{number.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The number of tag <paramref name="name"/> among the book's freeze
    /// tags: 1 for <c>final</c>, and n for <c>final-n</c> (from 2, written
    /// without leading zeros), or null if it isn't one of them.
    /// </summary>
    public static int? NumberOf(string name, string universeKey, string bookKey)
    {
        ArgumentNullException.ThrowIfNull(name);
        var first = NameFor(universeKey, bookKey);
        if (name == first)
        {
            return 1;
        }

        if (!name.StartsWith(first + "-", StringComparison.Ordinal))
        {
            return null;
        }

        var digits = name[(first.Length + 1)..];
        return digits.Length is > 0 and <= 9 && digits[0] != '0' && digits.All(char.IsAsciiDigit)
            && int.Parse(digits, CultureInfo.InvariantCulture) is var n and >= 2 ? n : null;
    }

    /// <summary>
    /// The book's freeze tags in the repository, by number, or none if the
    /// den's history can't make tags.
    /// </summary>
    /// <exception cref="HistoryException">The backend can list tags but failed to.</exception>
    public static IReadOnlyList<HistoryTag> Existing(IHistoryProvider history, string universeKey, string bookKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        var first = NameFor(universeKey, bookKey);
        if (!history.Capabilities.HasFlag(HistoryCapabilities.Tag))
        {
            return [];
        }

        var tags = history.Tags(first, cancellationToken);
        return tags.IsAvailable
            ? tags.Value.Where(t => NumberOf(t.Name, universeKey, bookKey) is not null).OrderBy(t => NumberOf(t.Name, universeKey, bookKey)).ToList()
            : [];
    }

    /// <summary>
    /// The name the book's next freeze tag takes: <c>final</c> if the
    /// repository has none of its freeze tags yet, otherwise the one
    /// numbered one more than the highest there.
    /// </summary>
    /// <exception cref="HistoryException">The backend can list tags but failed to, or the numbers have run out.</exception>
    public static string NextName(IHistoryProvider history, string universeKey, string bookKey, CancellationToken cancellationToken = default)
    {
        var existing = Existing(history, universeKey, bookKey, cancellationToken);
        if (existing.Count == 0)
        {
            return NameFor(universeKey, bookKey);
        }

        var highest = existing.Max(t => NumberOf(t.Name, universeKey, bookKey)!.Value);
        return highest < Highest
            ? NameFor(universeKey, bookKey, highest + 1)
            : throw new HistoryException($"The repository already has the freeze tag '{NameFor(universeKey, bookKey, highest)}', the highest number Wolfden uses, so it can't add another.");
    }

    /// <summary>
    /// Adds a freeze tag for a book that has just been frozen, at
    /// <paramref name="at"/> (or the latest checkpoint), if the den's history
    /// can make tags. It takes the next name (<see cref="NextName"/>), so a
    /// tag already there is never moved or replaced.
    /// </summary>
    /// <param name="history">The den's history provider.</param>
    /// <param name="universeKey">The book's universe.</param>
    /// <param name="bookKey">The book.</param>
    /// <param name="at">The revision to tag, or null for the latest checkpoint.</param>
    /// <param name="cancellationToken">Stops the backend.</param>
    /// <exception cref="HistoryException">The backend can make tags but failed to.</exception>
    public static FreezeTagResult Add(IHistoryProvider history, string universeKey, string bookKey, string? at = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        var frozen = $"Book '{bookKey}' of universe '{universeKey}' is frozen by its status: final.";
        if (!history.Capabilities.HasFlag(HistoryCapabilities.Tag))
        {
            var first = NameFor(universeKey, bookKey);
            return new FreezeTagResult(FreezeTagOutcome.NoHistory, first, null, $"{frozen} No tag was added: {history.Tag(new TagRequest(first), cancellationToken).Reason}");
        }

        var name = NextName(history, universeKey, bookKey, cancellationToken);
        var tagged = history.Tag(new TagRequest(name, at, $"Freeze book '{bookKey}' of universe '{universeKey}'."), cancellationToken);
        return tagged.IsAvailable
            ? new FreezeTagResult(FreezeTagOutcome.Tagged, name, tagged.Value, $"{frozen} Tagged its snapshot '{name}'.")
            // The backend can tag, so an unavailable tag means there's no checkpoint to name yet.
            : new FreezeTagResult(FreezeTagOutcome.NothingCheckpointed, name, null, $"{frozen} No tag was added: {tagged.Reason}");
    }
}
