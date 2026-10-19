using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Checks;

/// <summary>
/// The continuity checks that only <c>wolfden check</c> runs (design.md §7):
/// the warnings that loading a den doesn't already report. Loading reports
/// parse errors, ids, links, and spoilers (<see cref="DenStructure"/>,
/// <see cref="EntityCatalog"/>, <see cref="LinkIndex"/>); these add
/// unknown types (F-ENT-2), missing required fields and sections (F-FLD-3,
/// F-SEC-5), positions that don't resolve (F-POS-4), empty ranges (F-TMP-3,
/// F-SEC-8), and overlapping values and sections (F-TMP-5, F-SEC-4).
/// </summary>
internal static class ContinuityChecks
{
    public static List<DenDiagnostic> Run(EntityCatalog catalog)
    {
        var diagnostics = new List<DenDiagnostic>();
        var bookkeeping = new Bookkeeping(catalog.Structure.BookkeepingSections);
        foreach (var entity in catalog.Entities)
        {
            var found = new List<Diagnostic>();
            CheckType(entity, found);
            CheckPositions(entity.Universe, PositionScanner.Scan(entity.File, entity.Definition?.PositionFields), found);
            var facts = EntityFacts.For(entity, bookkeeping);
            CheckEntries(facts, found);
            CheckSections(facts, found);
            diagnostics.AddRange(found.Select(d => new DenDiagnostic(entity.Path, d)));
        }

        foreach (var universe in catalog.Structure.Universes)
        {
            foreach (var container in new Container[] { universe }.Concat(universe.Series).Concat(universe.Books))
            {
                var found = new List<Diagnostic>();
                CheckPositions(universe, PositionScanner.ScanContainer(container.File), found);
                diagnostics.AddRange(found.Select(d => new DenDiagnostic(container.FilePath, d)));
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// F-ENT-2: an unknown type is a warning, and the entity is untyped.
    /// For a known type, F-FLD-3 with F-FT-14 (a missing required field) and
    /// F-SEC-5 with F-TYPE-11 (a missing required section).
    /// </summary>
    private static void CheckType(CatalogEntity entity, List<Diagnostic> found)
    {
        var fm = entity.File.Frontmatter!;
        var typeLine = fm["type"]?.Line ?? 1;
        if (entity.Definition is not { } type)
        {
            found.Add(new Diagnostic("F-ENT-2", DiagnosticSeverity.Warning, typeLine,
                $"unknown type '{entity.Type}': no enabled pack defines it, so the entity is untyped and isn't checked against a type. Check the spelling, or enable the pack that defines it in den.yaml."));
            return;
        }

        var fields = fm["fields"] as YamlMapping;
        foreach (var field in type.Fields.Where(f => f.IsRequired))
        {
            var entry = fields?.Entries.FirstOrDefault(e => e.KeyText == field.Name);
            if (entry is null || !HasValue(entry.Value))
            {
                found.Add(new Diagnostic("F-FLD-3", DiagnosticSeverity.Warning, entry?.Key.Line ?? typeLine,
                    $"{field.Name} is required for type '{type.Key}', but it has no value."));
            }
        }

        foreach (var section in type.Sections.Where(s => s.IsRequired))
        {
            if (!entity.File.TopLevelSections.Any(s => type.FindSection(s.Name) == section))
            {
                found.Add(new Diagnostic("F-SEC-5", DiagnosticSeverity.Warning, typeLine,
                    $"'## {section.Name}' is required for type '{type.Key}', but this entity has no such section."));
            }
        }
    }

    /// <summary>
    /// F-FT-14: a field has a value if it has a plain value that isn't null,
    /// or an entry whose value isn't null. An empty list is unset (F-FT-12).
    /// </summary>
    private static bool HasValue(YamlNode value) => value switch
    {
        YamlMapping map when EntityCatalog.IsEntry(map) => map["value"] is { } inner && !IsUnset(inner),
        YamlSequence { Items.Count: > 0 } list when list.Items.All(EntityCatalog.IsEntry) =>
            list.Items.Cast<YamlMapping>().Any(m => m["value"] is { } inner && !IsUnset(inner)),
        _ => !IsUnset(value),
    };

    private static bool IsUnset(YamlNode value) => value is YamlScalar { IsNull: true } or YamlSequence { Items.Count: 0 };

    /// <summary>F-POS-4: a well-formed position that names a book or chapter the universe doesn't have.</summary>
    private static void CheckPositions(Universe universe, PositionScan scan, List<Diagnostic> found)
    {
        foreach (var reference in scan.References)
        {
            if (reference.Position is { } position && universe.Resolve(position).ToDiagnostic(reference.Line) is { } diagnostic)
            {
                var what = reference.Site == PositionSite.FieldValue ? "is kept, but not placed in story order" : "is left out of canon views";
                found.Add(diagnostic with { Message = $"{reference.Text}: {diagnostic.Message} Until it resolves, the fact that uses it {what}." });
            }
        }
    }

    /// <summary>F-TMP-3 (an entry whose <c>until</c> isn't after its <c>from</c>) and F-TMP-5 (live entries that overlap).</summary>
    private static void CheckEntries(EntityFacts facts, List<Diagnostic> found)
    {
        foreach (var field in facts.Fields)
        {
            foreach (var entry in field.Entries.Where(e => e.IsEmptyRange))
            {
                found.Add(new Diagnostic("F-TMP-3", DiagnosticSeverity.Warning, entry.Line,
                    $"{field.Name}: until {entry.UntilText} isn't after from {entry.FromText ?? "the start of the story"}, so this entry is never in effect."));
            }

            var live = field.Entries.Where(e => e.IsLive)
                .Select(e => new Span(e.EffectiveFrom!.Value, e.FromText, e.UntilText is null ? null : e.EffectiveUntil, e.UntilText, e.Line))
                .ToList();
            foreach (var (line, message) in Overlaps(live))
            {
                found.Add(new Diagnostic("F-TMP-5", DiagnosticSeverity.Warning, line,
                    $"{field.Name}: {message} Where they overlap, views use the entry with the later from, or the one later in the file."));
            }
        }
    }

    /// <summary>
    /// F-SEC-8 (a section whose <c>until</c> isn't after its <c>from</c>) and
    /// F-SEC-4 (live level-2 standard sections with the same name that overlap).
    /// </summary>
    private static void CheckSections(EntityFacts facts, List<Diagnostic> found)
    {
        var universe = facts.Entity.Universe;
        foreach (var fact in facts.Sections.Where(f => f.UntilText is not null))
        {
            // The section's own from, or for a level-2 section the one F-SEC-8 gives it.
            // A subsection with no from of its own is F-SEC-9's business.
            var from = fact.FromText is { } fromText ? Resolve(universe, fromText)
                : fact.Section.Parent is null ? fact.EffectiveFrom?.Point
                : null;
            if (from is { } start && Resolve(universe, fact.UntilText!) is { } until && until <= start)
            {
                found.Add(new Diagnostic("F-SEC-8", DiagnosticSeverity.Warning, fact.Section.Comment?.Line ?? fact.Section.Line,
                    $"'{fact.Section.Name}': until {fact.UntilText} isn't after from {fact.FromText ?? fact.EffectiveFrom!.Description}, so this section is never in effect."));
            }
        }

        var standard = facts.Sections.Where(f => f.IsStandard && f.IsLive)
            .GroupBy(f => LinkText.Normalize(f.Section.Name), StringComparer.Ordinal);
        foreach (var group in standard)
        {
            var spans = group
                .Select(f => new Span(f.EffectiveFrom!.Point, f.EffectiveFrom.Description, f.UntilText is null ? null : f.EffectiveUntil, f.UntilText,
                    f.Section.Comment?.Line ?? f.Section.Line))
                .ToList();
            foreach (var (line, message) in Overlaps(spans))
            {
                found.Add(new Diagnostic("F-SEC-4", DiagnosticSeverity.Warning, line,
                    $"'{group.First().Section.Name}': {message} Views show both where they overlap."));
            }
        }
    }

    /// <summary>A live fact's range, for the overlap rules.</summary>
    /// <param name="From">Where it starts.</param>
    /// <param name="FromText">Its from as written or described, or null for the start of the story.</param>
    /// <param name="Until">Its explicit until, or null if it has none.</param>
    /// <param name="UntilText">Its until as written.</param>
    /// <param name="Line">The line to report on.</param>
    private sealed record Span(StoryPoint From, string? FromText, StoryPoint? Until, string? UntilText, int Line);

    /// <summary>
    /// F-TMP-5, F-SEC-4: two live facts overlap if they share a from, or if
    /// an explicit until reaches past the next one's from. Each overlap is
    /// reported once, on the later fact for a shared from and on the one
    /// whose until runs on otherwise.
    /// </summary>
    private static IEnumerable<(int Line, string Message)> Overlaps(List<Span> spans)
    {
        var ordered = spans.OrderBy(s => s.From).ThenBy(s => s.Line).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].From.CompareTo(ordered[i - 1].From) == 0)
            {
                yield return (ordered[i].Line,
                    $"this and the one on line {ordered[i - 1].Line} both start at {ordered[i].FromText ?? "the start of the story"}, so they overlap.");
            }
        }

        foreach (var span in ordered.Where(s => s.Until is not null))
        {
            var next = ordered.FirstOrDefault(s => s.From > span.From);
            if (next is not null && span.Until!.Value > next.From)
            {
                yield return (span.Line,
                    $"this runs until {span.UntilText}, past the from of the one on line {next.Line} ({next.FromText}), so they overlap.");
            }
        }
    }

    private static StoryPoint? Resolve(Universe universe, string text) =>
        StoryPosition.TryParse(text, out var position, out _) && universe.Resolve(position).Resolved is { } resolved ? resolved.Point : null;
}
