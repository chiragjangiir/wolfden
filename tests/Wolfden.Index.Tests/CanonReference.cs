using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;

namespace Wolfden.Index.Tests;

/// <summary>
/// A reference canon-as-of view (format.md F-VIEW-1 to F-VIEW-3), worked
/// out straight from what Core parses, with no SQL. It's used only in
/// tests, to check <see cref="DenIndex.CanonAsOf"/> against an independent
/// reading of the rules. It is written for clarity, not speed.
/// </summary>
internal static class CanonReference
{
    /// <summary>
    /// The view of <paramref name="scope"/> at <paramref name="position"/>,
    /// or at the end of the story for null.
    /// Entities come in F-VIEW-3 order: the universe's, then series and
    /// books in story order (a series just before its first book).
    /// </summary>
    public static CanonView Compute(EntityCatalog catalog, CanonScope scope, string? position)
    {
        var structure = catalog.Structure;
        var universe = structure.FindUniverse(scope.UniverseKey)
            ?? throw new ArgumentException($"No universe '{scope.UniverseKey}'.", nameof(scope));
        Container container = scope.Kind switch
        {
            CanonScopeKind.Universe => universe,
            CanonScopeKind.Series => universe.Series.Single(s => s.Key == scope.Key),
            _ => universe.FindBook(scope.Key!) ?? throw new ArgumentException($"No book '{scope.Key}'.", nameof(scope)),
        };
        var point = position is null
            ? universe.EndOfStory
            : universe.Resolve(StoryPosition.Parse(position)).Resolved?.Point
                ?? throw new ArgumentException($"'{position}' doesn't resolve.", nameof(position));

        var covered = Covered(container);
        var bookkeeping = new Bookkeeping(structure.BookkeepingSections);

        return new CanonView(scope, position, catalog.Entities
            .Where(e => covered.Contains(e.Scope) && e.Status.IsInCanon() && e.EntersCanon is { } enters && enters <= point)
            .OrderBy(e => e.Scope.Kind != ContainerKind.Universe)
            .ThenBy(e => e.Scope.Start)
            .ThenBy(e => e.Scope.Kind != ContainerKind.Series)
            .ThenBy(e => e.Type, StringComparer.Ordinal)
            .ThenBy(e => LinkText.Normalize(e.Name), StringComparer.Ordinal)
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => Entity(EntityFacts.For(e, bookkeeping), point))
            .ToList());
    }

    /// <summary>
    /// The scopes a view of <paramref name="container"/> covers: the literal
    /// F-VIEW-1 reading, the container and the ones that enclose it, not the
    /// ones it encloses. Nearest first.
    /// </summary>
    public static IReadOnlyList<Container> Covered(Container container) => container.ScopeChain;

    /// <summary>
    /// True if a fact with this range is in effect at <paramref name="p"/>:
    /// <c>from &lt;= p &lt; until</c> (F-TMP-3, F-SEC-8). At the end of the
    /// story, "all canon so far" (F-VIEW-1), that's the facts that never end.
    /// A range that doesn't resolve is never in effect (F-POS-4).
    /// </summary>
    public static bool InEffect(StoryPoint? from, StoryPoint? until, StoryPoint p) =>
        from is { } f && until is { } u && (p.IsEndOfStory ? u.IsEndOfStory : f <= p && p < u);

    private static CanonEntity Entity(EntityFacts facts, StoryPoint p)
    {
        var e = facts.Entity;
        return new CanonEntity(
            e.Key, e.Id, e.Name, e.Type, e.Status.ToKey(), e.Scope.Kind.ToString().ToLowerInvariant(), e.Scope.Key, e.Path,
            e.From?.ToString(), e.File.Title, e.File.Lead, e.File.LeadLine,
            Fields(facts, p),
            facts.Sections
                .Where(s => s.Status.IsInCanon() && !s.IsBookkeeping && InEffect(s.EffectiveFrom?.Point, s.EffectiveUntil, p))
                .Select(s => new CanonSection(
                    s.Section.Name, s.Section.Level, HeadingPath(s.Section), s.Section.Body, s.Status.ToKey(), s.Pinned,
                    s.FromText, s.UntilText, e.Path, s.Section.Line))
                .ToList());
    }

    /// <summary>
    /// F-TMP-4, F-TMP-5: each written field's value at p, from the live
    /// entry in effect with the latest <c>from</c>, then the latest in the
    /// file. An unset field shows its type's default in its place, and the
    /// defaults of fields the file doesn't write come last, in the type's
    /// field order (types.md F-FT-15).
    /// </summary>
    private static List<CanonFieldValue> Fields(EntityFacts facts, StoryPoint p)
    {
        var e = facts.Entity;
        var defaults = (e.Definition?.Fields ?? [])
            .Where(f => f.Default is { } d && d is not Core.Yaml.YamlScalar { IsNull: true })
            .ToList();
        var result = new List<CanonFieldValue>();
        foreach (var field in facts.Fields)
        {
            var value = Value(e, field, p);
            if (value is not null)
            {
                result.Add(value);
            }
            else if (defaults.FirstOrDefault(d => d.Name == field.Name) is { } d)
            {
                result.Add(Default(d));
            }
        }

        result.AddRange(defaults.Where(d => facts.Fields.All(f => f.Name != d.Name)).Select(Default));
        return result;
    }

    private static CanonFieldValue? Value(CatalogEntity e, FieldFact field, StoryPoint p)
    {
        if (field.Kind == FieldValueKind.Plain)
        {
            var json = IndexWriter.Json(field.Value);
            return json == "null" ? null
                : new CanonFieldValue(field.Name, json, "plain", null, e.Status.ToKey(), false, null, null, e.Path, field.Line);
        }

        var entry = field.Entries
            .Where(x => x.IsLive && InEffect(x.EffectiveFrom, x.EffectiveUntil, p))
            .OrderBy(x => x.EffectiveFrom!.Value)
            .ThenBy(x => x.Index)
            .LastOrDefault();
        if (entry is null || IndexWriter.Json(entry.Value) is "null")
        {
            return null;
        }

        return new CanonFieldValue(
            field.Name, IndexWriter.Json(entry.Value), field.Kind == FieldValueKind.Entry ? "entry" : "temporal", entry.Index,
            entry.Status.ToKey(), entry.Pinned, entry.FromText, entry.UntilText, e.Path, entry.Line);
    }

    private static CanonFieldValue Default(Core.Types.FieldDefinition d) =>
        new(d.Name, IndexWriter.Json(d.Default), "default", null, null, false, null, null, d.Path, d.Line);

    private static List<string> HeadingPath(Core.Documents.Section section)
    {
        var path = new List<string>();
        for (var s = section; s is not null; s = s.Parent)
        {
            path.Insert(0, s.Name);
        }

        return path;
    }
}
