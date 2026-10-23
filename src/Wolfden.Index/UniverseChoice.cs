using Wolfden.Core.Positions;

namespace Wolfden.Index;

/// <summary>Why a universe couldn't be chosen.</summary>
public enum UniverseChoiceProblem
{
    /// <summary>The den has no universes yet.</summary>
    NoUniverse,

    /// <summary>The named universe doesn't exist.</summary>
    UnknownUniverse,

    /// <summary>The den has several universes, and neither a name nor a position settles which.</summary>
    UniverseRequired,

    /// <summary>The position doesn't parse, or its book is in no universe.</summary>
    BadPosition,
}

/// <summary>A universe couldn't be chosen; <see cref="Problem"/> says why, and the message says what to do.</summary>
public sealed class UniverseChoiceException : Exception
{
    /// <summary>An exception for <paramref name="problem"/>.</summary>
    public UniverseChoiceException(UniverseChoiceProblem problem, string message)
        : base(message)
    {
        Problem = problem;
    }

    /// <summary>An exception with no message.</summary>
    public UniverseChoiceException()
    {
    }

    /// <summary>An exception with a message.</summary>
    public UniverseChoiceException(string message)
        : base(message)
    {
    }

    /// <summary>An exception with a message and its cause.</summary>
    public UniverseChoiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Why.</summary>
    public UniverseChoiceProblem Problem { get; }
}

/// <summary>
/// Which universe a query is about (maintainer decision, 2026-10-23). A den
/// with one universe uses it. In a den with several, the query names one, or
/// gives a story position whose book key is in exactly one universe: books
/// belong to one universe (format.md F-POS-5), so the position settles it.
/// Nothing is ever chosen by looking at entity names, so a choice never
/// depends on, or gives away, what a universe holds.
/// </summary>
public static class UniverseChoice
{
    /// <summary>
    /// The key of the universe for <paramref name="universeKey"/> (null if
    /// none was named) and <paramref name="position"/> (null if none was given).
    /// </summary>
    /// <exception cref="UniverseChoiceException">No universe can be chosen.</exception>
    public static string Choose(IndexDbContext db, string? universeKey, string? position)
    {
        ArgumentNullException.ThrowIfNull(db);
        var universes = db.Containers.Where(c => c.Kind == "universe").ToList().OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        var keys = string.Join(", ", universes.Select(u => u.Key));
        if (universeKey is not null)
        {
            return universes.Any(u => u.Key == universeKey)
                ? universeKey
                : throw new UniverseChoiceException(
                    UniverseChoiceProblem.UnknownUniverse,
                    universes.Count == 0
                        ? $"The den has no universe '{universeKey}'; it has no universes yet."
                        : $"The den has no universe '{universeKey}'. Its universes: {keys}.");
        }

        switch (universes.Count)
        {
            case 0:
                throw new UniverseChoiceException(UniverseChoiceProblem.NoUniverse, "The den has no universes yet, so there's nothing to read.");
            case 1:
                return universes[0].Key;
        }

        if (position is null)
        {
            throw new UniverseChoiceException(
                UniverseChoiceProblem.UniverseRequired,
                $"The den has {universes.Count} universes ({keys}); name one.");
        }

        if (!StoryPosition.TryParse(position, out var parsed, out var error))
        {
            throw new UniverseChoiceException(UniverseChoiceProblem.BadPosition, error.Message);
        }

        var ids = db.Containers.Where(c => c.Kind == "book" && c.Key == parsed.BookKey).Select(c => c.UniverseId).Distinct().ToList();
        var holding = universes.Where(u => ids.Contains(u.Id)).ToList();
        return holding.Count switch
        {
            1 => holding[0].Key,
            0 => throw new UniverseChoiceException(
                UniverseChoiceProblem.BadPosition,
                $"No universe of the den has a book '{parsed.BookKey}', so '{position}' doesn't resolve. Its universes: {keys}; name one to see its books."),
            _ => throw new UniverseChoiceException(
                UniverseChoiceProblem.UniverseRequired,
                $"More than one universe has a book '{parsed.BookKey}', so '{position}' doesn't say which universe; name one ({keys})."),
        };
    }
}
