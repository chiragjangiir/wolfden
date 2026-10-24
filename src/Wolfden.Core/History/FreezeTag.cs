using Wolfden.Core.Structure;

namespace Wolfden.Core.History;

/// <summary>What happened to a freeze tag.</summary>
public enum FreezeTagOutcome
{
    /// <summary>The tag was made.</summary>
    Tagged,

    /// <summary>The den has no history that can make tags, so there's no tag; the book is still frozen by <c>status: final</c>.</summary>
    NoHistory,

    /// <summary>The repository already has a tag with the name, so none was made: a tag is never moved or replaced.</summary>
    AlreadyTagged,
}

/// <summary>The outcome of adding a freeze tag.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Name">The tag's name, <c>book/&lt;universe&gt;/&lt;book&gt;/final</c>.</param>
/// <param name="Tag">The tag made, or the one already there; null without history.</param>
/// <param name="Message">What happened, in words for the writer.</param>
public sealed record FreezeTagResult(FreezeTagOutcome Outcome, string Name, HistoryTag? Tag, string Message);

/// <summary>
/// The tag that freezing a book adds when the den has history (design.md
/// §5, F-BOOK-1): an extra snapshot, not the freeze itself, which is
/// <c>status: final</c> in <c>book.md</c>.
/// </summary>
/// <remarks>
/// Freeze tags are named <c>book/&lt;universe&gt;/&lt;book&gt;/final</c>
/// (maintainer decision, 2026-10-23, #31): book keys are unique only within
/// a universe (F-KEY-5), so the universe's key is part of the name. Tags
/// belong to the whole repository, and another den in it could use the same
/// keys, so an existing tag is reported, never moved.
/// </remarks>
public static class FreezeTag
{
    /// <summary>The freeze tag's name for book <paramref name="bookKey"/> of universe <paramref name="universeKey"/>.</summary>
    public static string NameFor(string universeKey, string bookKey)
    {
        ArgumentNullException.ThrowIfNull(universeKey);
        ArgumentNullException.ThrowIfNull(bookKey);
        if (!Keys.IsContainerKey(universeKey))
        {
            throw new ArgumentException($"'{universeKey}' isn't a universe key.", nameof(universeKey));
        }

        if (!Keys.IsContainerKey(bookKey))
        {
            throw new ArgumentException($"'{bookKey}' isn't a book key.", nameof(bookKey));
        }

        return $"book/{universeKey}/{bookKey}/final";
    }

    /// <summary>
    /// Adds the freeze tag of a book that has just been frozen, at
    /// <paramref name="at"/> (or the latest checkpoint), if the den's history
    /// can make tags. If the repository already has the tag, it's left where
    /// it is, and the result says so.
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
        var name = NameFor(universeKey, bookKey);
        var frozen = $"Book '{bookKey}' of universe '{universeKey}' is frozen by its status: final.";
        if (!history.Capabilities.HasFlag(HistoryCapabilities.Tag))
        {
            return new FreezeTagResult(FreezeTagOutcome.NoHistory, name, null, $"{frozen} No tag was added: {history.Tag(new TagRequest(name), cancellationToken).Reason}");
        }

        var tags = history.Tags(name, cancellationToken);
        if (tags.IsAvailable && tags.Value.FirstOrDefault(t => t.Name == name) is { } existing)
        {
            return new FreezeTagResult(
                FreezeTagOutcome.AlreadyTagged,
                name,
                existing,
                $"{frozen} The repository already has a tag '{name}', so no tag was added: Wolfden never moves or replaces a tag. " +
                "Tags belong to the whole repository, so another den in it, or an earlier freeze, may have made it.");
        }

        var tagged = history.Tag(new TagRequest(name, at, $"Freeze book '{bookKey}' of universe '{universeKey}'."), cancellationToken);
        return tagged.IsAvailable
            ? new FreezeTagResult(FreezeTagOutcome.Tagged, name, tagged.Value, $"{frozen} Tagged its snapshot '{name}'.")
            : new FreezeTagResult(FreezeTagOutcome.NoHistory, name, null, $"{frozen} No tag was added: {tagged.Reason}");
    }
}
