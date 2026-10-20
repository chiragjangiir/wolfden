using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Checks;

/// <summary>
/// The checks of an entity's <c>fields</c> against its type, and of their
/// shapes (format.md §7.2, §7.3; types.md §5): fields the type doesn't
/// define (F-FLD-2), values that don't match their field types (F-FLD-3,
/// types.md F-FT-4 to F-FT-12), the universal <c>row</c> and
/// <c>placeholder</c> (F-FLD-7, F-FT-17), unquoted links (F-FLD-6), entries
/// on fields that aren't temporal (F-TMP-8), and unknown entry keys
/// (F-TMP-2). Every one is a warning: the value is kept and indexed as
/// written. Missing required fields are <see cref="ContinuityChecks"/>'.
/// </summary>
internal static class FieldChecks
{
    private static readonly HashSet<string> EntryKeys = new(StringComparer.Ordinal) { "value", "from", "until", "status", "pin" };

    public static void Run(CatalogEntity entity, RowIndex rows, List<Diagnostic> found)
    {
        if (entity.File.Frontmatter?["fields"] is not YamlMapping fields)
        {
            return;
        }

        var type = entity.Definition;
        foreach (var field in fields.Entries)
        {
            if (field.KeyText is not { } name)
            {
                continue;
            }

            var entries = Entries(field.Value);
            foreach (var entry in entries)
            {
                CheckEntryKeys(name, entry, found);
            }

            var definition = type?.FindField(name);
            var isUniversal = TypeDefinition.IsUniversalField(name);
            if (type is not null && definition is null && !isUniversal)
            {
                found.Add(new Diagnostic("F-FLD-2", DiagnosticSeverity.Warning, field.Key.Line,
                    $"Type '{type.Key}' doesn't define the field '{name}', so its value is kept and indexed as written. Check the spelling, or define the field in a pack."));
            }

            // F-TMP-8: entries on a field that isn't temporal. The universal fields aren't temporal (types.md §5.5).
            if (definition is { IsTemporal: false } || isUniversal)
            {
                CheckTemporal(name, type, field.Value, entries, found);
            }

            var values = entries.Count > 0 ? entries.Select(e => e["value"]!).ToList() : [field.Value];
            foreach (var value in values)
            {
                if (UnquotedLink(value) is { } suggestion)
                {
                    // F-FLD-6 says what went wrong, so the value isn't checked against its type as well.
                    found.Add(new Diagnostic("F-FLD-6", DiagnosticSeverity.Warning, value.Line,
                        $"{name}: without quotes, YAML reads {suggestion} as a list inside a list, not a link. Write \"{suggestion}\"."));
                    continue;
                }

                switch (name)
                {
                    case "row":
                        CheckRow(entity, value, rows, found);
                        break;
                    case "placeholder":
                        CheckPlaceholder(value, found);
                        break;
                    default:
                        if (definition is not null)
                        {
                            CheckValue(name, definition, value, found);
                        }

                        break;
                }
            }

            if (name == "row")
            {
                foreach (var entry in entries.Where(e => HasBound(e, "from") || HasBound(e, "until")))
                {
                    found.Add(new Diagnostic("F-FLD-7", DiagnosticSeverity.Warning, entry.Line,
                        "row: rows have no positions, so a row entry shouldn't have a from or until. Remove it."));
                }
            }
        }
    }

    /// <summary>True if an entry has a <c>from</c> or <c>until</c> that counts: one that isn't null or empty (F-TMP-2).</summary>
    private static bool HasBound(YamlMapping entry, string key) =>
        entry[key] is { } node && node is not YamlScalar { IsNull: true } && node is not YamlScalar { Text: "" };

    /// <summary>The entries of a field's value (F-TMP-1): a single entry, a temporal list, or none for a plain value.</summary>
    private static List<YamlMapping> Entries(YamlNode value) => value switch
    {
        YamlMapping map when EntityCatalog.IsEntry(map) => [map],
        YamlSequence { Items.Count: > 0 } list when list.Items.All(EntityCatalog.IsEntry) => list.Items.Cast<YamlMapping>().ToList(),
        _ => [],
    };

    /// <summary>F-TMP-2: keys other than value, from, until, status, and pin in an entry.</summary>
    private static void CheckEntryKeys(string name, YamlMapping entry, List<Diagnostic> found)
    {
        foreach (var key in entry.Entries.Where(e => e.KeyText is not { } k || !EntryKeys.Contains(k)))
        {
            found.Add(new Diagnostic("F-TMP-2", DiagnosticSeverity.Warning, key.Key.Line,
                $"{name}: '{key.KeyText}' isn't an entry key (an entry has value, from, until, status, and pin). It's kept, but does nothing."));
        }
    }

    /// <summary>
    /// F-TMP-8: a temporal list, or an entry with <c>until</c>, on a field
    /// that isn't temporal. A <c>row</c> entry's <c>until</c> is F-FLD-7's.
    /// </summary>
    private static void CheckTemporal(string name, TypeDefinition? type, YamlNode value, List<YamlMapping> entries, List<Diagnostic> found)
    {
        var what = type is not null && !TypeDefinition.IsUniversalField(name) ? $"in type '{type.Key}'" : "on any type";
        var fix = TypeDefinition.IsUniversalField(name) ? "" : " To let its value change over the story, define it with temporal: true.";
        if (value is YamlSequence && entries.Count > 0)
        {
            found.Add(new Diagnostic("F-TMP-8", DiagnosticSeverity.Warning, value.Line,
                $"{name} isn't temporal {what}, but it has a list of entries. It's still read as temporal.{fix}"));
        }
        else if (name != "row" && entries is [{ } single] && HasBound(single, "until"))
        {
            found.Add(new Diagnostic("F-TMP-8", DiagnosticSeverity.Warning, single["until"]!.Line,
                $"{name} isn't temporal {what}, but this entry has an until. It's still read as temporal.{fix} A from alone is fine: it says where the reader learns the fact."));
        }
    }

    /// <summary>F-FLD-3 with types.md §5.2: a value that doesn't match its field type.</summary>
    private static void CheckValue(string name, FieldDefinition definition, YamlNode value, List<Diagnostic> found)
    {
        foreach (var problem in FieldValues.Check(definition, value).Problems)
        {
            var message = problem.Message.StartsWith(name, StringComparison.Ordinal) ? problem.Message : $"{name}: {problem.Message}";
            found.Add(new Diagnostic(problem.RuleId, DiagnosticSeverity.Warning, problem.Line, message));
        }
    }

    /// <summary>F-FT-17: <c>placeholder</c> is a <c>bool</c> (F-FT-7).</summary>
    private static void CheckPlaceholder(YamlNode value, List<Diagnostic> found)
    {
        if (value is not YamlScalar { IsNull: true } && (value is not YamlScalar scalar || scalar.AsBoolean() is null))
        {
            found.Add(new Diagnostic("F-FT-7", DiagnosticSeverity.Warning, value.Line,
                $"placeholder is true or false, not '{Describe(value)}'."));
        }
    }

    /// <summary>
    /// F-FLD-7: a <c>row</c> value is a row id or a list of them, and each
    /// resolves among the outlines of the entity's series or standalone
    /// book, or for a universe-scope entity among all of the universe's.
    /// </summary>
    private static void CheckRow(CatalogEntity entity, YamlNode value, RowIndex rows, List<Diagnostic> found)
    {
        if (value is YamlScalar { IsNull: true })
        {
            return;
        }

        var where = entity.Scope switch
        {
            Book { Series: { } series } => $"the outlines of series '{series.Key}'",
            Book book => $"the outline of book '{book.Key}'",
            Series series => $"the outlines of series '{series.Key}'",
            _ => $"the outlines of universe '{entity.Universe.Key}'",
        };

        foreach (var item in value is YamlSequence list ? list.Items : [value])
        {
            if (item is not YamlScalar { IsNull: false } scalar)
            {
                found.Add(new Diagnostic("F-FLD-7", DiagnosticSeverity.Warning, item.Line,
                    $"row holds a row id, such as 37 or 37a, or a list of them, not {(item is YamlScalar ? "an empty item" : item is YamlSequence ? "a list" : "a mapping")}."));
                continue;
            }

            var rowId = scalar.Text;
            if (!BodyLinkScanner.IsRowId(rowId))
            {
                found.Add(new Diagnostic("F-FLD-7", DiagnosticSeverity.Warning, item.Line,
                    $"row: '{rowId}' isn't a row id (a number with no leading zero, then any lowercase letters, such as 37 or 37a), so it doesn't tie this entity to a row."));
                continue;
            }

            var matches = RowIndex.GroupsFor(entity.Scope)
                .Select(g => (Group: g, Places: rows.Group(g).GetValueOrDefault(rowId) ?? []))
                .Where(m => m.Places.Count > 0)
                .ToList();
            if (matches.Count == 0)
            {
                found.Add(new Diagnostic("F-FLD-7", DiagnosticSeverity.Warning, item.Line,
                    $"row: there's no row {rowId} in {where}, so this entity isn't tied to a row."));
            }
            else if (matches.Count > 1 || matches[0].Places.Count > 1)
            {
                var places = matches.SelectMany(m => m.Places).Select(p => $"{p.Path}:{p.Line}");
                found.Add(new Diagnostic("F-FLD-7", DiagnosticSeverity.Warning, item.Line,
                    $"row: row {rowId} is ambiguous: it's in more than one place in {where} ({string.Join(", ", places)}), so this entity isn't tied to it."));
            }
        }
    }

    /// <summary>
    /// F-FLD-6: a one-item list whose only item is a list of strings, which
    /// is what YAML makes of an unquoted <c>[[velari]]</c>. Returns the link
    /// as it was probably meant, or null.
    /// </summary>
    private static string? UnquotedLink(YamlNode value) =>
        value is YamlSequence { Items: [YamlSequence { Items.Count: > 0 } inner] }
        && inner.Items.All(i => i is YamlScalar { IsNull: false })
            ? "[[" + string.Join(", ", inner.Items.Cast<YamlScalar>().Select(s => s.Text)) + "]]"
            : null;

    private static string Describe(YamlNode node) => node switch
    {
        YamlScalar s => s.Text,
        YamlSequence => "a list",
        _ => "a mapping",
    };
}
