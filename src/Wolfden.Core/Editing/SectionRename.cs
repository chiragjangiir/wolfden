using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Editing;

/// <summary>
/// What renaming a section changes (<see cref="SectionRename.Plan"/>): the
/// entity's file with the new heading, and every file whose links to the
/// section were rewritten, none of them written yet.
/// </summary>
/// <param name="Files">The new contents of each file that changes, by den-relative path: the entity's file first.</param>
/// <param name="Originals">What each of those files held when the plan was made, to check that nothing changed them before they're written.</param>
/// <param name="LinksRewritten">How many links were rewritten, in all files.</param>
/// <param name="FrozenPaths">Files that link to the section but are in a final book (F-BOOK-1), so they can't be rewritten; when there are any, the rename mustn't be made.</param>
public sealed record SectionRenamePlan(
    IReadOnlyDictionary<string, byte[]> Files, IReadOnlyDictionary<string, byte[]> Originals, int LinksRewritten, IReadOnlyList<string> FrozenPaths);

/// <summary>
/// Renames a section and rewrites, in the same change, every link to it by
/// heading (<c>[[entity#Old name]]</c>) in any file of the den (format.md
/// F-RT-5, maintainer decision 2026-10-23). A link is "to it" when it
/// resolves to the entity and its heading matches this section first
/// (F-LINK-9), so links to another section with the same name are left alone.
/// </summary>
public static class SectionRename
{
    /// <summary>The characters a heading in a link can't hold (format.md §10.1).</summary>
    private static readonly char[] NotInLinks = ['[', ']', '|', '#', '\r', '\n'];

    /// <summary>
    /// Plans the rename of <paramref name="section"/>, a section of
    /// <paramref name="entity"/>'s file as <paramref name="links"/> loaded it,
    /// to <paramref name="newName"/>. Nothing is written.
    /// </summary>
    /// <param name="links">The den's links, from the same load as <paramref name="entity"/>.</param>
    /// <param name="den">The den, to read outlines that hold links.</param>
    /// <param name="entity">The entity whose section is renamed.</param>
    /// <param name="section">The section, one of <c>entity.File.Sections</c>.</param>
    /// <param name="newName">The new heading text.</param>
    /// <exception cref="ArgumentException">The section isn't the entity's, the name isn't one line, or links to the section can't name it (it holds <c>[</c>, <c>]</c>, <c>|</c>, or <c>#</c>).</exception>
    /// <exception cref="NotSupportedException">A file's links can't be rewritten in place.</exception>
    /// <exception cref="EditVerificationException">An edit didn't read back as intended (a bug).</exception>
    public static SectionRenamePlan Plan(LinkIndex links, IDenSource den, CatalogEntity entity, Section section, string newName)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(den);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(newName);
        if (!ReferenceEquals(section.Owner, entity.File))
        {
            throw new ArgumentException("The section isn't one of the entity's.", nameof(section));
        }

        var targeting = links.LinksTo(entity).Where(l => ReferenceEquals(l.TargetSection, section)).ToList();
        if (targeting.Count > 0 && newName.IndexOfAny(NotInLinks) >= 0)
        {
            throw new ArgumentException(
                $"{targeting.Count} link(s) name '{section.Name}', and a link can't name a heading with [, ], |, or # in it, so they couldn't follow the rename. Choose a name without them.",
                nameof(newName));
        }

        var structure = links.Catalog.Structure;
        var containers = structure.Universes.SelectMany(u => new Container[] { u }.Concat(u.Series).Concat(u.Books)).ToDictionary(c => c.FilePath, StringComparer.Ordinal);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var frozen = new List<string>();
        var rewritten = 0;

        var byPath = targeting.GroupBy(l => l.Occurrence.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (!byPath.ContainsKey(entity.Path))
        {
            byPath[entity.Path] = [];
        }

        foreach (var (path, found) in byPath.OrderBy(p => p.Key == entity.Path ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            MarkdownFile file;
            Book? book;
            if (links.Catalog.FindByPath(path) is { } holder)
            {
                file = holder.File;
                book = holder.Scope as Book;
            }
            else if (containers.TryGetValue(path, out var container))
            {
                file = container.File;
                book = container as Book;
            }
            else
            {
                var bytes = den.ReadFile(path);
                file = MarkdownFile.ParseOutline(bytes);
                book = structure.FindScope(path) as Book;
            }

            if (book is { Status: BookStatus.Final })
            {
                frozen.Add(path);
                continue;
            }

            originals[path] = file.ToBytes();
            var places = found.Select(l => (l.Occurrence.Link.Line, l.Occurrence.Link.Column, l.Occurrence.Link.Target)).ToHashSet();
            var (edited, count) = Rewrite(file, places, section.Name, newName);
            rewritten += count;
            if (path == entity.Path)
            {
                var renamed = edited.Sections[section.Index];
                if (renamed.Name != section.Name)
                {
                    throw new EditVerificationException("Rewriting the links in the entity's own file moved its sections, so the rename was refused.");
                }

                edited = edited.WithSectionName(renamed, newName);
            }

            files[path] = edited.ToBytes();
        }

        return new SectionRenamePlan(files, originals, rewritten, frozen);
    }

    /// <summary>Rewrites the heading of each link at <paramref name="places"/> (line, column, target) whose heading matches <paramref name="oldName"/>.</summary>
    private static (MarkdownFile File, int Count) Rewrite(MarkdownFile file, HashSet<(int Line, int Column, string Target)> places, string oldName, string newName)
    {
        if (places.Count == 0)
        {
            return (file, 0);
        }

        var count = 0;
        var text = file.Text;

        // The body first, by offset, then the frontmatter, whose offsets the body's edits don't move.
        var replacements = new List<(int Start, int Length, string Text)>();
        var found = file.IsOutline ? BodyLinkScanner.ScanOutline(file) : BodyLinkScanner.ScanBody(file);
        foreach (var link in found)
        {
            if (Matches(link, places, oldName) && LinkSyntax.WithHeading(text.Substring(link.Link.Offset, link.Link.Length), newName) is { } replaced)
            {
                replacements.Add((link.Link.Offset, link.Link.Length, replaced));
            }
        }

        var result = file.WithBodyTextReplaced(replacements);
        count += replacements.Count;

        if (file.Frontmatter is { } frontmatter && file.SourceForReading is { } source)
        {
            var edits = new List<(IReadOnlyList<string> Path, string Value)>();
            foreach (var key in new[] { "fields", "chapter_defaults", "chapters" })
            {
                if (frontmatter[key] is { } node)
                {
                    foreach (var (path, scalar) in Scalars([key], node))
                    {
                        var value = scalar.Text;
                        var changes = LinkScanner.ScalarLinkPlaces(source, scalar).Where(l => Matches(l, places, oldName)).ToList();
                        foreach (var link in changes.OrderByDescending(l => l.Link.Offset))
                        {
                            if (LinkSyntax.WithHeading(value.Substring(link.Link.Offset, link.Link.Length), newName) is { } replaced)
                            {
                                value = string.Concat(value.AsSpan(0, link.Link.Offset), replaced, value.AsSpan(link.Link.Offset + link.Link.Length));
                                count++;
                            }
                        }

                        if (value != scalar.Text)
                        {
                            edits.Add((path, value));
                        }
                    }
                }
            }

            foreach (var (path, value) in edits)
            {
                result = result.WithFrontmatterValue(path, YamlScalarValue.String(value));
            }
        }

        return (result, count);
    }

    private static bool Matches(FoundLink link, HashSet<(int Line, int Column, string Target)> places, string oldName) =>
        link.Link.Heading is { } heading && places.Contains((link.Line, link.Column, link.Link.Target)) && LinkText.Matches(heading, oldName);

    /// <summary>Every non-null scalar under <paramref name="node"/>, with its path.</summary>
    private static IEnumerable<(IReadOnlyList<string> Path, YamlScalar Scalar)> Scalars(IReadOnlyList<string> path, YamlNode node)
    {
        switch (node)
        {
            case YamlScalar { IsNull: false } scalar:
                yield return (path, scalar);
                break;
            case YamlSequence list:
                for (var i = 0; i < list.Items.Count; i++)
                {
                    foreach (var s in Scalars([.. path, i.ToString(System.Globalization.CultureInfo.InvariantCulture)], list.Items[i]))
                    {
                        yield return s;
                    }
                }

                break;
            case YamlMapping map:
                foreach (var entry in map.Entries.Where(e => e.KeyText is not null))
                {
                    foreach (var s in Scalars([.. path, entry.KeyText!], entry.Value))
                    {
                        yield return s;
                    }
                }

                break;
        }
    }
}
