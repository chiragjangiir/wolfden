using Wolfden.Core.Documents;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Positions;

/// <summary>Where in a file a position is written.</summary>
public enum PositionSite
{
    /// <summary>An entity's top-level <c>from</c> (format.md F-ENT-6).</summary>
    EntityFrom,

    /// <summary>A field entry's <c>from</c> (F-TMP-2).</summary>
    EntryFrom,

    /// <summary>A field entry's <c>until</c> (F-TMP-2).</summary>
    EntryUntil,

    /// <summary>A section comment's <c>from</c> (F-SEC-7).</summary>
    SectionFrom,

    /// <summary>A section comment's <c>until</c> (F-SEC-7).</summary>
    SectionUntil,

    /// <summary>The value of a field of type <c>position</c> (types.md F-FT-10).</summary>
    FieldValue,
}

/// <summary>A position written in a file, parsed but not yet resolved.</summary>
/// <param name="Text">The position as written.</param>
/// <param name="Position">The parsed position, or null if the text isn't one.</param>
/// <param name="Site">Where it's written.</param>
/// <param name="Line">The 1-based line in the file.</param>
/// <param name="Field">For an entry or a position field, the field's name.</param>
/// <param name="Section">For a section comment, its section.</param>
public sealed record PositionReference(string Text, StoryPosition? Position, PositionSite Site, int Line, string? Field = null, Section? Section = null);

/// <summary>The positions found in a file, and the problems with them.</summary>
/// <param name="References">Every position, in the order found: frontmatter first, then sections in file order.</param>
/// <param name="Diagnostics">
/// Syntax problems with positions in the frontmatter. Problems in section
/// comments are already in the file's own <see cref="DenFile.Diagnostics"/>,
/// so they aren't repeated here.
/// </param>
public sealed record PositionScan(IReadOnlyList<PositionReference> References, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Finds the story positions in an entity or container file: the entity's
/// <c>from</c>, the <c>from</c> and <c>until</c> of field entries, the
/// <c>from</c> and <c>until</c> of section comments, and, given the names of
/// the fields the entity's type defines as <c>position</c>, their values.
/// </summary>
public static class PositionScanner
{
    /// <summary>Scans a parsed file for positions.</summary>
    /// <param name="file">An entity or container file.</param>
    /// <param name="positionFields">
    /// The fields whose type is <c>position</c> (types.md F-FT-10), such as a
    /// thread's <c>planted</c> and <c>paid_off</c>. Their values are scanned
    /// too, and a bad one is a warning rather than an error.
    /// </param>
    public static PositionScan Scan(MarkdownFile file, IReadOnlySet<string>? positionFields = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        var references = new List<PositionReference>();
        var diagnostics = new List<Diagnostic>();
        var frontmatter = file.Frontmatter;
        if (frontmatter is not null)
        {
            if (frontmatter["from"] is { } from)
            {
                ReadBound(from, PositionSite.EntityFrom, null, references, diagnostics);
            }

            if (frontmatter["fields"] is YamlMapping fields)
            {
                foreach (var entry in fields.Entries)
                {
                    if (entry.KeyText is not { } name)
                    {
                        continue;
                    }

                    ScanField(name, entry.Value, positionFields?.Contains(name) == true, references, diagnostics);
                }
            }
        }

        foreach (var section in file.Sections)
        {
            if (section.Comment is not { } comment)
            {
                continue;
            }

            // Only the first of a repeated attribute counts; a repeat is an F-SEC-7 error.
            foreach (var attr in comment.Attributes.DistinctBy(a => a.Name))
            {
                var site = attr.Name switch
                {
                    "from" => PositionSite.SectionFrom,
                    "until" => PositionSite.SectionUntil,
                    _ => (PositionSite?)null,
                };

                if (site is { } s)
                {
                    StoryPosition.TryParse(attr.Value, out var position, out _);
                    references.Add(new PositionReference(attr.Value, position, s, comment.Line, Section: section));
                }
            }
        }

        return new PositionScan(references, diagnostics);
    }

    private static void ScanField(string name, YamlNode value, bool isPositionField, List<PositionReference> references, List<Diagnostic> diagnostics)
    {
        // F-TMP-1: a single entry, a list of entries, or a plain value.
        var entries = value switch
        {
            YamlMapping map when map.ContainsKey("value") => [map],
            YamlSequence list => list.Items.OfType<YamlMapping>().Where(m => m.ContainsKey("value")).ToList(),
            _ => new List<YamlMapping>(),
        };

        if (entries.Count == 0)
        {
            if (isPositionField)
            {
                ReadFieldValue(name, value, references, diagnostics);
            }

            return;
        }

        foreach (var entry in entries)
        {
            if (entry["from"] is { } from)
            {
                ReadBound(from, PositionSite.EntryFrom, name, references, diagnostics);
            }

            if (entry["until"] is { } until)
            {
                ReadBound(until, PositionSite.EntryUntil, name, references, diagnostics);
            }

            if (isPositionField && entry["value"] is { } inner)
            {
                ReadFieldValue(name, inner, references, diagnostics);
            }
        }
    }

    /// <summary>
    /// A <c>from</c> or <c>until</c> in frontmatter. A bad one is an error
    /// (F-POS-2, F-POS-3, F-POS-6). A null one is the same as leaving it out.
    /// </summary>
    private static void ReadBound(YamlNode node, PositionSite site, string? field, List<PositionReference> references, List<Diagnostic> diagnostics)
    {
        if (node is YamlScalar { IsNull: true })
        {
            return;
        }

        if (node is not YamlScalar scalar)
        {
            var key = site == PositionSite.EntryUntil ? "until" : "from";
            diagnostics.Add(new Diagnostic("F-POS-3", DiagnosticSeverity.Error, node.Line,
                $"{key} must be a story position, such as \"ember:14\", not a {(node is YamlSequence ? "list" : "mapping")}: {StoryPosition.InvalidMessage}."));
            return;
        }

        if (!StoryPosition.TryParse(scalar.Text, out var position, out var error))
        {
            diagnostics.Add(error.ToDiagnostic(DiagnosticSeverity.Error, scalar.Line));
        }

        references.Add(new PositionReference(scalar.Text, position, site, scalar.Line, field));
    }

    /// <summary>
    /// The value of a <c>position</c> field: a position, or a list of them for
    /// a list field. A bad one is a warning (types.md F-FT-10). A null value
    /// is unset (F-FLD-4).
    /// </summary>
    private static void ReadFieldValue(string field, YamlNode value, List<PositionReference> references, List<Diagnostic> diagnostics)
    {
        var items = value is YamlSequence list ? list.Items : [value];
        foreach (var item in items)
        {
            switch (item)
            {
                case YamlScalar { IsNull: true }:
                    break;
                case YamlScalar scalar:
                    if (!StoryPosition.TryParse(scalar.Text, out var position, out var error))
                    {
                        diagnostics.Add(error.ToDiagnostic(DiagnosticSeverity.Warning, scalar.Line, "F-FT-10"));
                    }

                    references.Add(new PositionReference(scalar.Text, position, PositionSite.FieldValue, scalar.Line, field));
                    break;
                default:
                    diagnostics.Add(new Diagnostic("F-FT-10", DiagnosticSeverity.Warning, item.Line,
                        $"{field} holds story positions, such as \"ember:14\", not a {(item is YamlSequence ? "list" : "mapping")}: {StoryPosition.InvalidMessage}."));
                    break;
            }
        }
    }
}
