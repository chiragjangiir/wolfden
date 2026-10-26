using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Editing;

/// <summary>
/// The den-wide link rewrite that renames share (format.md F-RT-5): each
/// link to rewrite is spliced in place in its file, in a body, an outline,
/// or a frontmatter value, so everything else stays as written.
/// </summary>
internal static partial class LinkRewriter
{
    /// <summary>What a rewrite changes, none of it written yet.</summary>
    /// <param name="Files">The new contents of each file that changes, by den-relative path: the entity's file first.</param>
    /// <param name="Originals">What each of those files held when the rewrite was planned.</param>
    /// <param name="Count">How many links were rewritten, in all files.</param>
    /// <param name="Frozen">Files with links to rewrite that are in a final book (F-BOOK-1), so they weren't rewritten.</param>
    internal sealed record Result(Dictionary<string, byte[]> Files, Dictionary<string, byte[]> Originals, int Count, List<string> Frozen);

    /// <summary>
    /// Rewrites each of <paramref name="targeting"/> (links that resolve to
    /// <paramref name="entity"/>) whose target and heading pass
    /// <paramref name="matches"/>, with <paramref name="replace"/> applied to
    /// its text. The entity's own file is always in the result, with
    /// <paramref name="editOwn"/> applied after its links are rewritten.
    /// </summary>
    /// <param name="newText">The text the rewrite puts in links, to tell whether a frontmatter value can keep its style.</param>
    internal static Result Rewrite(
        LinkIndex links, IDenSource den, CatalogEntity entity, IReadOnlyList<ResolvedLink> targeting, Func<string, string?, bool> matches,
        Func<string, string?> replace, string newText, Func<MarkdownFile, MarkdownFile> editOwn) =>
        Rewrite(links, den, entity, targeting, matches, (text, _) => replace(text), newText, editOwn);

    /// <summary>
    /// As the other overload, with <paramref name="replace"/> told whether
    /// the link is in a table row of a body or an outline (a line that
    /// starts with <c>|</c>), where a <c>|</c> it adds must be written <c>\|</c>
    /// (F-OUT-12).
    /// </summary>
    internal static Result Rewrite(
        LinkIndex links, IDenSource den, CatalogEntity entity, IReadOnlyList<ResolvedLink> targeting, Func<string, string?, bool> matches,
        Func<string, bool, string?> replace, string newText, Func<MarkdownFile, MarkdownFile> editOwn)
    {
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
            var (edited, count) = RewriteFile(file, places, matches, replace, newText);
            rewritten += count;
            if (path == entity.Path)
            {
                edited = editOwn(edited);
            }

            files[path] = edited.ToBytes();
        }

        return new Result(files, originals, rewritten, frozen);
    }

    /// <summary>
    /// The files of the entity's universe that the den leaves out for errors
    /// and whose text holds a link that passes <paramref name="looksLikeLink"/>
    /// (given its target and heading). Their links can't be resolved, so
    /// they're found by their text.
    /// </summary>
    internal static List<string> Skipped(LinkIndex links, IDenSource den, CatalogEntity entity, Func<string, string?, bool> looksLikeLink)
    {
        var universe = $"{entity.Universe.Path}/";
        var broken = links.Catalog.Diagnostics.Concat(links.Catalog.Structure.Diagnostics)
            .Where(d => d.Diagnostic.Severity == DiagnosticSeverity.Error && d.Path.StartsWith(universe, StringComparison.Ordinal) && d.Path.EndsWith(".md", StringComparison.Ordinal))
            .Select(d => d.Path)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var path in broken)
        {
            if (den.TryReadFile(path) is not { } bytes)
            {
                continue;
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (LinkSyntax.Scan(text, 0, text.Length, null).Any(l => !l.IsEmpty && looksLikeLink(l.Target, l.Heading)))
            {
                result.Add(path);
            }
        }

        return result;
    }

    /// <summary>Rewrites each link at <paramref name="places"/> (line, column, target) that passes <paramref name="matches"/>.</summary>
    private static (MarkdownFile File, int Count) RewriteFile(
        MarkdownFile file, HashSet<(int Line, int Column, string Target)> places, Func<string, string?, bool> matches, Func<string, bool, string?> replace, string newText)
    {
        if (places.Count == 0)
        {
            return (file, 0);
        }

        bool Matches(FoundLink link) => places.Contains((link.Line, link.Column, link.Link.Target)) && matches(link.Link.Target, link.Link.Heading);

        var count = 0;
        var text = file.Text;

        // The body first, by offset, then the frontmatter, whose offsets the body's edits don't move.
        var replacements = new List<(int Start, int Length, string Text)>();
        var found = file.IsOutline ? BodyLinkScanner.ScanOutline(file) : BodyLinkScanner.ScanBody(file);
        Lines? lines = null;
        foreach (var link in found)
        {
            if (Matches(link) && replace(text.Substring(link.Link.Offset, link.Link.Length), (lines ??= new Lines(text)).InTableRow(link.Link.Offset)) is { } replaced)
            {
                replacements.Add((link.Link.Offset, link.Link.Length, replaced));
            }
        }

        var result = file.WithBodyTextReplaced(replacements);
        count += replacements.Count;

        if (file.Frontmatter is { } frontmatter && file.SourceForReading is { } source)
        {
            // A link written verbatim in the source is spliced in place, so the
            // value keeps its style (a block scalar, quotes, escapes elsewhere in
            // it). A value whose link the YAML escapes or folds is written anew.
            var splices = new List<(int Start, int Length, string Text)>();
            var spliced = new List<(IReadOnlyList<string> Path, string Value)>();
            var rewritten = new List<(IReadOnlyList<string> Path, string Value)>();
            foreach (var key in new[] { "fields", "chapter_defaults", "chapters" })
            {
                if (frontmatter[key] is { } node)
                {
                    foreach (var (path, scalar) in Scalars([key], node))
                    {
                        var value = scalar.Text;
                        var verbatim = true;
                        var here = new List<(int Start, int Length, string Text)>();
                        var changes = LinkScanner.ScalarLinkPlaces(source, scalar).Where(Matches).ToList();
                        foreach (var link in changes.OrderByDescending(l => l.Link.Offset))
                        {
                            var linkText = value.Substring(link.Link.Offset, link.Link.Length);
                            if (replace(linkText, false) is { } replaced)
                            {
                                value = string.Concat(value.AsSpan(0, link.Link.Offset), replaced, value.AsSpan(link.Link.Offset + link.Link.Length));
                                count++;
                                if (link.Offset + linkText.Length <= source.Text.Length && string.CompareOrdinal(source.Text, link.Offset, linkText, 0, linkText.Length) == 0)
                                {
                                    here.Add((link.Offset, linkText.Length, replaced));
                                }
                                else
                                {
                                    verbatim = false;
                                }
                            }
                        }

                        if (value == scalar.Text)
                        {
                            continue;
                        }

                        if (verbatim && !NeedsQuoting(scalar, newText))
                        {
                            splices.AddRange(here);
                            spliced.Add((path, value));
                        }
                        else
                        {
                            rewritten.Add((path, value));
                        }
                    }
                }
            }

            result = result.WithFrontmatterTextReplaced(splices, spliced);
            foreach (var (path, value) in rewritten)
            {
                result = result.WithFrontmatterValue(path, YamlScalarValue.String(value));
            }
        }

        return (result, count);
    }

    /// <summary>
    /// True if the line that holds <paramref name="offset"/> is a table row
    /// (GFM tables), where a <c>|</c> splits the cell. See <see cref="Lines"/>.
    /// </summary>
    internal static bool InTableRow(string text, int offset) => new Lines(text).InTableRow(offset);

    /// <summary>A text's lines, split once, to ask of several offsets whether each is in a table row.</summary>
    internal sealed class Lines
    {
        private readonly string[] _lines;
        private readonly int[] _starts;

        public Lines(string text)
        {
            _lines = text.Split('\n');
            _starts = new int[_lines.Length];
            for (int i = 1, at = 0; i < _lines.Length; i++)
            {
                at += _lines[i - 1].Length + 1;
                _starts[i] = at;
            }
        }

        /// <summary>
        /// True if the line that holds <paramref name="offset"/> is a table
        /// row: one that starts with <c>|</c>; one with a <c>|</c> whose next
        /// line is a delimiter row (<c>---|---</c>), so it's the header; or
        /// one with a <c>|</c> below a delimiter row in the same run of
        /// non-blank lines, for a table written without the outer pipes.
        /// Only lines above are looked at, as GFM makes only a paragraph's
        /// last line a header, and the walk stops at a code fence or an HTML
        /// comment's marker. A blockquote's <c>&gt;</c> markers are skipped
        /// first, for a table in a quote.
        /// </summary>
        public bool InTableRow(int offset)
        {
            var at = Array.BinarySearch(_starts, offset);
            at = at >= 0 ? at : ~at - 1;
            var line = Unquoted(_lines[at]);
            if (line.StartsWith('|'))
            {
                return true;
            }

            if (!line.Contains('|', StringComparison.Ordinal) || IsMarker(line))
            {
                return false;
            }

            if (at + 1 < _lines.Length && Unquoted(_lines[at + 1]) is var next && !IsMarker(next) && DelimiterRow().IsMatch(next))
            {
                return true;
            }

            for (var i = at - 1; i >= 0; i--)
            {
                var above = Unquoted(_lines[i]);
                if (above.Length == 0 || IsMarker(above))
                {
                    return false;
                }

                if (DelimiterRow().IsMatch(above))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True for a line that opens or closes a code fence or an HTML comment, where a table can't continue.</summary>
        private static bool IsMarker(string line) =>
            line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal)
            || line.Contains("<!--", StringComparison.Ordinal) || line.Contains("-->", StringComparison.Ordinal);
    }

    /// <summary>The line without its indent, any blockquote markers, or a carriage return.</summary>
    private static string Unquoted(string line)
    {
        var s = line.TrimEnd('\r').TrimStart();
        while (s.StartsWith('>'))
        {
            s = s[1..].TrimStart();
        }

        return s;
    }

    /// <summary>A GFM table's delimiter row, with at least one <c>|</c>: <c>---|:--:</c> or <c>| --- | --- |</c>.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?=.*\|)\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DelimiterRow();

    /// <summary>True if splicing <paramref name="text"/> into <paramref name="scalar"/> as written could change how YAML reads it, so the value is written anew instead.</summary>
    private static bool NeedsQuoting(YamlScalar scalar, string text) => scalar.Style switch
    {
        YamlScalarStyle.Literal or YamlScalarStyle.Folded => false,
        YamlScalarStyle.DoubleQuoted => text.IndexOfAny(['"', '\\']) >= 0,
        YamlScalarStyle.SingleQuoted => text.Contains('\''),
        _ => text.IndexOfAny([':', ',', '"', '\'', '{', '}', '\\']) >= 0,
    };

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
