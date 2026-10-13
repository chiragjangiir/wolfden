using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Links;

/// <summary>
/// Finds the links in a den's files (format.md F-LINK-1) and works out what
/// each one needs for the link checks: its scope, the status of the fact
/// that holds it, whether it's bookkeeping, and its F-LINK-13 positions.
/// </summary>
internal sealed class LinkScanner(Bookkeeping bookkeeping, List<LinkOccurrence> links, List<DenDiagnostic> diagnostics)
{
    public void ScanEntity(CatalogEntity entity)
    {
        var file = entity.File;
        var facts = EntityFacts.For(entity, bookkeeping);
        var entityPosition = Single(entity.EntersCanon is { } p ? new LinkPosition(p, entity.EntersCanonDescription) : null);
        foreach (var field in facts.Fields)
        {
            if (field.Kind == FieldValueKind.Plain)
            {
                foreach (var link in ScalarLinks(entity.Path, file, field.Value))
                {
                    Add(new LinkOccurrence(entity.Path, link, LinkSite.EntityField, entity.Scope)
                    {
                        Entity = entity, Field = field.Name, Status = entity.Status, Positions = entityPosition,
                    });
                }

                continue;
            }

            foreach (var entry in field.Entries)
            {
                var position = EntryPosition(entity, entry.Node);
                foreach (var link in ScalarLinks(entity.Path, file, entry.Node))
                {
                    Add(new LinkOccurrence(entity.Path, link, LinkSite.EntityFieldEntry, entity.Scope)
                    {
                        Entity = entity, Field = field.Name, Status = entry.Status, Positions = position,
                    });
                }
            }
        }

        foreach (var found in BodyLinkScanner.ScanBody(file))
        {
            if (Empty(entity.Path, found))
            {
                continue;
            }

            var link = found.ToWikiLink(file.Text);
            var section = file.Sections.FirstOrDefault(s => s.HeadingStart <= found.Offset && found.Offset < s.OwnEnd);
            if (section is null)
            {
                Add(new LinkOccurrence(entity.Path, link, LinkSite.EntityLead, entity.Scope)
                {
                    Entity = entity, Status = entity.Status, Positions = entityPosition,
                });
                continue;
            }

            var fact = facts[section];
            Add(new LinkOccurrence(entity.Path, link, LinkSite.EntitySection, entity.Scope)
            {
                Entity = entity,
                Section = section,
                Status = fact.Status,
                IsBookkeeping = fact.IsBookkeeping,
                Positions = Single(fact.EffectiveFrom is { } from ? new LinkPosition(from.Point, from.Description) : null),
            });
        }
    }

    public void ScanContainer(Container container)
    {
        var file = container.File;
        var path = container.FilePath;
        foreach (var found in BodyLinkScanner.ScanBody(file))
        {
            if (!Empty(path, found))
            {
                Add(new LinkOccurrence(path, found.ToWikiLink(file.Text), LinkSite.ContainerBody, container));
            }
        }

        if (file.Frontmatter?["chapter_defaults"] is YamlMapping defaults)
        {
            foreach (var field in defaults.Entries)
            {
                if (field.KeyText is not { } name)
                {
                    continue;
                }

                // F-CHAP-3: a value, or a list item, that is ignored is as if it were left out.
                var value = container.ChapterDefaults.GetValueOrDefault(name);
                var positions = value is null ? [] : InheritingChapters(container, name);
                foreach (var (item, link) in ItemLinks(path, file, field.Value))
                {
                    var held = value?.Holds(item) == true;
                    Add(new LinkOccurrence(path, link, LinkSite.ChapterDefault, container)
                    {
                        Field = name, Positions = held ? positions : [], IsIgnoredValue = !held,
                    });
                }
            }
        }

        if (container is Book book && file.Frontmatter?["chapters"] is YamlSequence items)
        {
            foreach (var item in items.Items.OfType<YamlMapping>())
            {
                if (item["key"] is not YamlScalar { IsNull: false } key || book.FindChapter(key.Text) is not { } chapter
                    || item["fields"] is not YamlMapping chapterFields)
                {
                    continue;
                }

                var position = Single(new LinkPosition(chapter.Point, chapter.ToString()));
                foreach (var field in chapterFields.Entries)
                {
                    var value = field.KeyText is { } name ? chapter.OwnFields.GetValueOrDefault(name) : null;
                    foreach (var (part, link) in ItemLinks(path, file, field.Value))
                    {
                        var held = value?.Holds(part) == true;
                        Add(new LinkOccurrence(path, link, LinkSite.ChapterField, book)
                        {
                            Field = field.KeyText, Chapter = chapter, Positions = held ? position : [], IsIgnoredValue = !held,
                        });
                    }
                }
            }
        }
    }

    public void ScanOutline(Book book, string path, MarkdownFile file)
    {
        foreach (var found in BodyLinkScanner.ScanOutline(file))
        {
            if (Empty(path, found))
            {
                continue;
            }

            var link = found.ToWikiLink(file.Text);
            if (found.Row is not { } row)
            {
                Add(new LinkOccurrence(path, link, LinkSite.OutlineNotes, book));
                continue;
            }

            // F-OUT-9: status is matched case-insensitively, and anything else is draft.
            var status = row.Status is { Length: > 0 } s ? FactStatuses.Parse(s.ToLowerInvariant()) ?? FactStatus.Draft : FactStatus.Draft;
            var chapter = row.Chapter is { Length: > 0 } c ? book.FindChapter(c) : null;
            var position = chapter is null
                ? new LinkPosition(book.Start, book.StartDescription)
                : new LinkPosition(chapter.Point, chapter.ToString());
            Add(new LinkOccurrence(path, link, LinkSite.OutlineRow, book)
            {
                OutlineRow = row.RowId, Status = status, Positions = [position],
            });
        }
    }

    /// <summary>
    /// The F-LINK-13 positions of a link in a container's
    /// <c>chapter_defaults</c>: each chapter whose effective value for the
    /// field comes from this container (types.md F-CHAP-4, F-CHAP-5).
    /// </summary>
    private static List<LinkPosition> InheritingChapters(Container container, string field)
    {
        IEnumerable<Book> books = container switch
        {
            Universe u => u.Books,
            Series s => s.Books,
            Book b => [b],
            _ => [],
        };

        var result = new List<LinkPosition>();
        foreach (var chapter in books.SelectMany(b => b.Chapters))
        {
            if (chapter.Fields.TryGetValue(field, out var value) && value.DefaultFrom == container)
            {
                result.Add(new LinkPosition(chapter.Point, chapter.ToString(), chapter));
            }
        }

        return result;
    }

    /// <summary>
    /// The links in a chapter field's value, each with the top-level item it's
    /// in: a list item, or the value itself if it isn't a list. Items are
    /// what F-CHAP-3 keeps or ignores.
    /// </summary>
    private IEnumerable<(YamlNode Item, WikiLink Link)> ItemLinks(string path, MarkdownFile file, YamlNode value)
    {
        foreach (var item in value is YamlSequence list ? list.Items : [value])
        {
            foreach (var link in ScalarLinks(path, file, item))
            {
                yield return (item, link);
            }
        }
    }

    /// <summary>
    /// An entry's position for F-LINK-13: its <c>from</c>. Without one the
    /// entry starts at the start of the story, which isn't checked, and one
    /// that doesn't resolve gives no position.
    /// </summary>
    private static IReadOnlyList<LinkPosition> EntryPosition(CatalogEntity entity, YamlMapping entry)
    {
        if (entry["from"] is not YamlScalar { IsNull: false } from || !StoryPosition.TryParse(from.Text, out var position, out _))
        {
            return [];
        }

        return Single(entity.Universe.Resolve(position).Resolved is { } resolved ? new LinkPosition(resolved.Point, from.Text) : null);
    }

    private static IReadOnlyList<LinkPosition> Single(LinkPosition? position) => position is null ? [] : [position];

    /// <summary>F-LINK-3: a link with an empty target is a warning, and isn't a link.</summary>
    private bool Empty(string path, FoundLink found)
    {
        if (!found.Link.IsEmpty)
        {
            return false;
        }

        diagnostics.Add(new DenDiagnostic(path, new Diagnostic("F-LINK-3", DiagnosticSeverity.Warning, found.Line,
            "This link has no target, so it isn't a link. Add the slug, name, or alias of the entity it's for.")));
        return true;
    }

    /// <summary>
    /// The links in every string under <paramref name="node"/> (F-LINK-1).
    /// A link's line and column are where its text appears in the file, or
    /// the string's start when the YAML escapes or folds it.
    /// </summary>
    private IEnumerable<WikiLink> ScalarLinks(string path, MarkdownFile file, YamlNode node)
    {
        var source = file.SourceForReading!;
        foreach (var scalar in Strings(node))
        {
            var text = scalar.Text;
            var searchFrom = scalar.Start;
            foreach (var raw in LinkSyntax.Scan(text, 0, text.Length, null))
            {
                var linkText = text.Substring(raw.Offset, raw.Length);
                var end = Math.Min(scalar.ContentEnd, source.Text.Length);
                var at = searchFrom < end ? source.Text.IndexOf(linkText, searchFrom, end - searchFrom, StringComparison.Ordinal) : -1;
                var offset = at >= 0 ? at : scalar.Start;
                if (at >= 0)
                {
                    searchFrom = at + linkText.Length;
                }

                var line = source.LineIndexOf(offset);
                var found = new FoundLink(raw, offset, line + 1, offset - source.Lines[line].Start + 1);
                if (!Empty(path, found))
                {
                    yield return found.ToWikiLink(text);
                }
            }
        }
    }

    /// <summary>Every non-null scalar value under a node, in file order. Mapping keys aren't strings under it.</summary>
    private static IEnumerable<YamlScalar> Strings(YamlNode node)
    {
        switch (node)
        {
            case YamlScalar { IsNull: false } scalar:
                yield return scalar;
                break;
            case YamlSequence list:
                foreach (var s in list.Items.SelectMany(Strings))
                {
                    yield return s;
                }

                break;
            case YamlMapping map:
                foreach (var s in map.Entries.SelectMany(e => Strings(e.Value)))
                {
                    yield return s;
                }

                break;
        }
    }

    private void Add(LinkOccurrence occurrence) => links.Add(occurrence);
}
