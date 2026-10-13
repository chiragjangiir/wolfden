using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Types;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Structure;

/// <summary>
/// Reads a universe's chapter fields: each container's
/// <c>chapter_defaults</c> and each chapter item's <c>fields</c>, checked
/// against the den's chapter fields (types.md F-CHAP-3), then each
/// chapter's effective values (F-CHAP-5). Every problem is a warning, and a
/// value that doesn't match its definition counts as not set, so the
/// chapter inherits.
/// </summary>
internal sealed class ChapterFieldReader(DenStructure den, Universe universe)
{
    public void Read()
    {
        foreach (var container in new Container[] { universe }.Concat(universe.Series).Concat(universe.Books))
        {
            container.ChapterDefaults = ReadDefaults(container);
        }

        foreach (var book in universe.Books)
        {
            foreach (var chapter in book.Chapters)
            {
                chapter.OwnFields = ReadChapter(book, chapter);
                var effective = new Dictionary<string, ChapterFieldValue>(chapter.OwnFields, StringComparer.Ordinal);
                foreach (var container in book.ScopeChain)
                {
                    foreach (var (name, value) in container.ChapterDefaults)
                    {
                        effective.TryAdd(name, value);
                    }
                }

                chapter.Fields = effective;
            }
        }
    }

    private Dictionary<string, ChapterFieldValue> ReadDefaults(Container container)
    {
        var result = new Dictionary<string, ChapterFieldValue>(StringComparer.Ordinal);
        switch (container.File.Frontmatter?["chapter_defaults"])
        {
            case null or YamlScalar { IsNull: true }:
                return result;
            case YamlMapping map:
                Read(container.FilePath, map, container, result, "chapter_defaults");
                return result;
            case var node:
                Warn(container.FilePath, "F-CHAP-5", node.Line,
                    "chapter_defaults must be a mapping from chapter field to value, such as '{ tense: past }', so it's ignored.");
                return result;
        }
    }

    private Dictionary<string, ChapterFieldValue> ReadChapter(Book book, Chapter chapter)
    {
        var result = new Dictionary<string, ChapterFieldValue>(StringComparer.Ordinal);
        switch (chapter.FieldsNode)
        {
            case null or YamlScalar { IsNull: true }:
                return result;
            case YamlMapping map:
                Read(book.FilePath, map, null, result, $"chapter {chapter}");
                return result;
            case var node:
                Warn(book.FilePath, "F-CHAP-3", node.Line,
                    $"The fields of chapter {chapter} must be a mapping from chapter field to value, such as '{{ pov: \"[[mara-voss]]\" }}', so they're ignored.");
                return result;
        }
    }

    private void Read(string path, YamlMapping map, Container? defaultFrom, Dictionary<string, ChapterFieldValue> result, string where)
    {
        foreach (var entry in map.Entries)
        {
            if (entry.KeyText is not { } name || result.ContainsKey(name))
            {
                continue;
            }

            var value = entry.Value;
            if (value is YamlScalar { IsNull: true })
            {
                continue;
            }

            if (EntityCatalog.IsEntry(value) || value is YamlSequence list && list.Items.Any(EntityCatalog.IsEntry))
            {
                Warn(path, "F-CHAP-3", value.Line,
                    $"{name} in {where} is an entry, but a chapter field has no entries: write the plain value. It's ignored, as if it were left out.");
                continue;
            }

            var definition = den.Types.FindChapterField(name);
            if (definition is null)
            {
                Warn(path, "F-CHAP-3", entry.Key.Line, $"'{name}' isn't a chapter field. Its value is kept as written.");
                result[name] = new ChapterFieldValue(name, null, value, value is YamlSequence s ? s.Items : [value], defaultFrom);
                continue;
            }

            var check = FieldValues.Check(definition, value);
            foreach (var problem in check.Problems)
            {
                Warn(path, "F-CHAP-3", problem.Line, $"{problem.Message} In {where}, it's ignored, so the chapter inherits instead.");
            }

            // F-CHAP-3, F-CHAP-5: a value that doesn't match counts as not set;
            // in a list, only the items that don't match are ignored, and a list
            // left with no items counts as not set. An explicit empty list is
            // "none here".
            var set = definition.IsList ? check.IsEmptyList || check.Items.Count > 0 : check.Matches;
            if (set)
            {
                result[name] = new ChapterFieldValue(name, definition, value, check.Items, defaultFrom);
            }
        }
    }

    private void Warn(string path, string rule, int? line, string message) =>
        den.Add(path, new Diagnostic(rule, DiagnosticSeverity.Warning, line, message));
}
