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
        var entityPosition = Single(entity.EntersCanon is { } p ? new LinkPosition(p, entity.EntersCanonDescription) : null);
        if (file.Frontmatter?["fields"] is YamlMapping fields)
        {
            foreach (var field in fields.Entries)
            {
                if (field.KeyText is not { } name)
                {
                    continue;
                }

                var entries = Entries(field.Value);
                if (entries is null)
                {
                    foreach (var link in ScalarLinks(entity.Path, file, field.Value))
                    {
                        Add(new LinkOccurrence(entity.Path, link, LinkSite.EntityField, entity.Scope)
                        {
                            Entity = entity, Field = name, Status = entity.Status, Positions = entityPosition,
                        });
                    }

                    continue;
                }

                foreach (var entry in entries)
                {
                    var status = EntryStatus(entity, entry);
                    var position = EntryPosition(entity, entry);
                    foreach (var link in ScalarLinks(entity.Path, file, entry))
                    {
                        Add(new LinkOccurrence(entity.Path, link, LinkSite.EntityFieldEntry, entity.Scope)
                        {
                            Entity = entity, Field = name, Status = status, Positions = position,
                        });
                    }
                }
            }
        }

        var sections = new SectionFacts(entity, bookkeeping);
        foreach (var found in BodyLinkScanner.ScanBody(file))
        {
            if (Empty(entity.Path, found))
            {
                continue;
            }

            var link = found.ToWikiLink(file.Text);
            var section = file.Sections.FirstOrDefault(s => s.HeadingStart <= found.Offset && found.Offset < s.OwnEnd);
            Add(section is null
                ? new LinkOccurrence(entity.Path, link, LinkSite.EntityLead, entity.Scope)
                {
                    Entity = entity, Status = entity.Status, Positions = entityPosition,
                }
                : new LinkOccurrence(entity.Path, link, LinkSite.EntitySection, entity.Scope)
                {
                    Entity = entity,
                    Section = section,
                    Status = sections.Status(section),
                    IsBookkeeping = sections.IsBookkeeping(section),
                    Positions = Single(sections.From(section)),
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

                var positions = HasValue(field.Value) ? InheritingChapters(container, name) : [];
                foreach (var link in ScalarLinks(path, file, field.Value))
                {
                    Add(new LinkOccurrence(path, link, LinkSite.ChapterDefault, container) { Field = name, Positions = positions });
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
                    foreach (var link in ScalarLinks(path, file, field.Value))
                    {
                        Add(new LinkOccurrence(path, link, LinkSite.ChapterField, book)
                        {
                            Field = field.KeyText, Chapter = chapter, Positions = position,
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
        foreach (var book in books)
        {
            var own = OwnChapterFields(book);
            foreach (var chapter in book.Chapters)
            {
                if (own.TryGetValue(chapter.Key, out var fields) && HasValue(fields[field]))
                {
                    continue;
                }

                var from = book.ScopeChain.FirstOrDefault(c => HasValue((c.File.Frontmatter?["chapter_defaults"] as YamlMapping)?[field]));
                if (from == container)
                {
                    result.Add(new LinkPosition(chapter.Point, chapter.ToString(), chapter));
                }
            }
        }

        return result;
    }

    /// <summary>Each chapter's <c>fields</c> mapping in <c>book.md</c>, by chapter key.</summary>
    private static Dictionary<string, YamlMapping> OwnChapterFields(Book book)
    {
        var result = new Dictionary<string, YamlMapping>(StringComparer.Ordinal);
        if (book.File.Frontmatter?["chapters"] is YamlSequence items)
        {
            foreach (var item in items.Items.OfType<YamlMapping>())
            {
                if (item["key"] is YamlScalar { IsNull: false } key && item["fields"] is YamlMapping fields)
                {
                    result.TryAdd(key.Text, fields);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// True if a chapter field's value is set (F-CHAP-5): not null, and not
    /// an entry or a list holding one, which is ignored (F-CHAP-3). An empty
    /// list is a value.
    /// </summary>
    private static bool HasValue(YamlNode? value) =>
        value is not (null or YamlScalar { IsNull: true })
        && !EntityCatalog.IsEntry(value)
        && !(value is YamlSequence list && list.Items.Any(EntityCatalog.IsEntry));

    /// <summary>The entries of a field's value (F-TMP-1), or null for a plain value.</summary>
    private static IReadOnlyList<YamlMapping>? Entries(YamlNode value) => value switch
    {
        YamlMapping map when EntityCatalog.IsEntry(map) => [map],
        YamlSequence { Items.Count: > 0 } list when list.Items.All(EntityCatalog.IsEntry) => list.Items.Cast<YamlMapping>().ToList(),
        _ => null,
    };

    /// <summary>An entry's effective status (F-STAT-2, F-STAT-3).</summary>
    internal static FactStatus EntryStatus(CatalogEntity entity, YamlMapping entry)
    {
        var own = entry["status"] is YamlScalar { IsNull: false } s && FactStatuses.Parse(s.Text) is { } parsed ? parsed : entity.Status;
        return FactStatuses.Effective(own, entity.Status);
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

    /// <summary>Statuses, bookkeeping, and effective <c>from</c>s of one entity's sections, computed once each.</summary>
    private sealed class SectionFacts(CatalogEntity entity, Bookkeeping bookkeeping)
    {
        private readonly Dictionary<Section, LinkPosition?> _from = [];

        public FactStatus Status(Section section)
        {
            var holder = section.Parent is { } parent ? Status(parent) : entity.Status;
            return FactStatuses.Effective(OwnStatus(section), holder);
        }

        public bool IsBookkeeping(Section section) => bookkeeping.IsBookkeeping(section);

        /// <summary>
        /// The section's effective <c>from</c> (F-SEC-8, F-SEC-9): the later
        /// of its own <c>from</c> and its parent's, a level-2 section without
        /// one taking the entity's. Null if a position on the way doesn't
        /// resolve.
        /// </summary>
        public LinkPosition? From(Section section)
        {
            if (!_from.TryGetValue(section, out var result))
            {
                result = Compute(section);
                _from[section] = result;
            }

            return result;
        }

        private LinkPosition? Compute(Section section)
        {
            var own = OwnFrom(section, out var unresolved);
            if (unresolved)
            {
                return null;
            }

            if (section.Parent is not { } parent)
            {
                return own ?? (entity.EntersCanon is { } p ? new LinkPosition(p, entity.EntersCanonDescription) : null);
            }

            var inherited = From(parent);
            if (inherited is null || own is null)
            {
                return inherited;
            }

            return own.Point >= inherited.Point ? own : inherited;
        }

        /// <summary>
        /// The section's own <c>from</c>: its comment's, or for a thread's
        /// <c>## Payoff</c> without one, the thread's <c>paid_off</c>
        /// (types.md F-CORE-4).
        /// </summary>
        private LinkPosition? OwnFrom(Section section, out bool unresolved)
        {
            unresolved = false;
            var text = section.Comment?["from"];
            if (text is null || PositionScanner.IsEmptyBound(text))
            {
                text = section.Parent is null && IsPayoff(section) ? PaidOff() : null;
            }

            if (text is null || !StoryPosition.TryParse(text, out var position, out _))
            {
                return null;
            }

            if (entity.Universe.Resolve(position).Resolved is not { } resolved)
            {
                unresolved = true;
                return null;
            }

            return new LinkPosition(resolved.Point, text);
        }

        private bool IsPayoff(Section section) =>
            entity.Type == "thread" && LinkText.Normalize(section.Name) == "payoff";

        /// <summary>
        /// A thread's <c>paid_off</c>, read from a plain value or a single
        /// in-canon entry (types.md §9.1). Anything else counts as unset.
        /// </summary>
        private string? PaidOff() =>
            (entity.File.Frontmatter?["fields"] as YamlMapping)?["paid_off"] switch
            {
                YamlScalar { IsNull: false } plain => plain.Text,
                YamlMapping entry when EntityCatalog.IsEntry(entry) && EntryStatus(entity, entry).IsInCanon()
                    && entry["value"] is YamlScalar { IsNull: false } value => value.Text,
                _ => null,
            };

        private FactStatus OwnStatus(Section section) =>
            section.Comment?["status"] is { } s && FactStatuses.Parse(s) is { } parsed ? parsed
            : section.Parent is { } parent ? OwnStatus(parent)
            : entity.Status;
    }
}
