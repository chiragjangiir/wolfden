namespace Wolfden.Index;

/// <summary>
/// The one place that turns a story point into a range filter over the
/// index, so canon views (#15) and anything else ask "in effect at p" the
/// same way.
/// </summary>
/// <remarks>
/// Ranges are half-open, <c>from &lt;= p &lt; until</c>, as in F-TMP-3 and
/// F-SEC-8. A fact with no end stores <c>until = </c><see cref="EndOfStory"/>,
/// the same value as the end-of-story point itself, so a plain comparison
/// at <see cref="EndOfStory"/> would match nothing. F-VIEW-1 says an omitted
/// position means the end of the story, "all canon so far", so at
/// <see cref="EndOfStory"/> these helpers match the facts that are still in
/// effect when the story ends: those with no end. A fact whose position
/// doesn't resolve has null ordinals and never matches (F-POS-4).
/// Ordinals compare only within one universe, so callers also filter on
/// <c>UniverseId</c>.
/// </remarks>
public static class IndexQueries
{
    /// <summary>The ordinal of the start of the story, before every position (F-ORD-3).</summary>
    public const long StartOfStory = long.MinValue;

    /// <summary>The ordinal of the end of the story, after every position, and the point an omitted position means (F-VIEW-1).</summary>
    public const long EndOfStory = long.MaxValue;

    /// <summary>The field values in effect at <paramref name="point"/>. It doesn't filter on status or liveness.</summary>
    public static IQueryable<FieldValueRecord> InEffectAt(this IQueryable<FieldValueRecord> values, long point)
    {
        ArgumentNullException.ThrowIfNull(values);
        return point == EndOfStory
            ? values.Where(v => v.FromOrdinal != null && v.UntilOrdinal == EndOfStory)
            : values.Where(v => v.FromOrdinal <= point && point < v.UntilOrdinal);
    }

    /// <summary>The sections in effect at <paramref name="point"/>. It doesn't filter on status or bookkeeping.</summary>
    public static IQueryable<SectionRecord> InEffectAt(this IQueryable<SectionRecord> sections, long point)
    {
        ArgumentNullException.ThrowIfNull(sections);
        return point == EndOfStory
            ? sections.Where(s => s.FromOrdinal != null && s.UntilOrdinal == EndOfStory)
            : sections.Where(s => s.FromOrdinal <= point && point < s.UntilOrdinal);
    }

    /// <summary>The entities that have entered canon by <paramref name="point"/> (F-ENT-6). It doesn't filter on status.</summary>
    public static IQueryable<EntityRecord> EnteredBy(this IQueryable<EntityRecord> entities, long point)
    {
        ArgumentNullException.ThrowIfNull(entities);
        return entities.Where(e => e.FromOrdinal <= point);
    }
}
