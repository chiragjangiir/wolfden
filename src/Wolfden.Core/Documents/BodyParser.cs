using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Wolfden.Core.Positions;
using Wolfden.Core.Text;
using MarkdigDocument = Markdig.Syntax.MarkdownDocument;

namespace Wolfden.Core.Documents;

/// <summary>
/// Splits a file's body into the lead and sections (format.md §9), using
/// Markdig for CommonMark block structure so that headings inside code
/// blocks and HTML blocks never split sections (F-SEC-1).
/// </summary>
internal static partial class BodyParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();

    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal)
    {
        "idea", "undecided", "candidate", "draft", "canon", "retconned",
    };

    private static readonly HashSet<string> KnownAttributes = new(StringComparer.Ordinal)
    {
        "from", "until", "status", "pin", "canon",
    };

    internal sealed record Result(IReadOnlyList<Section> Sections, string? Title, int? TitleLine, int LeadEnd);

    /// <param name="owner">The file the sections belong to.</param>
    /// <param name="source">The file's text.</param>
    /// <param name="bodyStart">Where the body starts.</param>
    /// <param name="diagnostics">Where problems go.</param>
    /// <param name="isOutline">
    /// True for <c>outline.md</c>, where wolfden comments do nothing
    /// (F-SEC-10): none is read as a section comment, so none can have an
    /// error, each is a warning, and F-SEC-2's stray level-1 heading doesn't apply.
    /// </param>
    public static Result Parse(MarkdownFile owner, SourceText source, int bodyStart, List<Diagnostic> diagnostics, bool isOutline = false)
    {
        var text = source.Text;
        var body = text[bodyStart..];
        MarkdigDocument document;
        try
        {
            document = Markdown.Parse(body, Pipeline);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Markdig refuses very deep nesting (block quotes, lists) with an
            // exception. Report it; the file still round-trips, with no sections.
            diagnostics.Add(new Diagnostic("F-FM-2", DiagnosticSeverity.Error, source.LineIndexOf(bodyStart) + 1,
                $"The body can't be read as CommonMark: {ex.Message}"));
            return new Result([], null, null, text.Length);
        }

        // Top-level HTML blocks by their first line, to find section comments.
        var htmlByLine = new Dictionary<int, HtmlBlock>();
        foreach (var block in document)
        {
            if (block is HtmlBlock html)
            {
                htmlByLine.TryAdd(source.LineIndexOf(bodyStart + html.Span.Start), html);
            }
        }

        // Lines that hold a section comment, valid or not, so the scan for
        // misplaced wolfden comments skips them.
        var commentLines = new HashSet<int>();
        var sections = new List<Section>();
        var stack = new Stack<Section>();
        string? title = null;
        int? titleLine = null;
        var seenLevel2 = false;

        foreach (var heading in document.OfType<HeadingBlock>())
        {
            var startOffset = bodyStart + heading.Span.Start;
            var firstLine = source.LineIndexOf(startOffset);
            var lastLine = source.LineIndexOf(bodyStart + Math.Max(heading.Span.Start, heading.Span.End));
            var (name, nameStart, nameEnd) = HeadingName(source, heading, bodyStart, firstLine, lastLine);

            if (!seenLevel2)
            {
                if (heading.Level != 2)
                {
                    // Before the first level-2 heading, it's all lead (F-SEC-2).
                    if (heading.Level == 1 && title is null)
                    {
                        title = name;
                        titleLine = firstLine + 1;
                    }

                    continue;
                }

                seenLevel2 = true;
            }

            var level = heading.Level;
            if (level == 1)
            {
                if (!isOutline)
                {
                    diagnostics.Add(new Diagnostic("F-SEC-2", DiagnosticSeverity.Warning, firstLine + 1,
                        $"A level-1 heading after the first level-2 heading is read as level 2: '{name}'."));
                }

                level = 2;
            }

            var section = new Section(owner, sections.Count)
            {
                Name = name,
                NameStart = nameStart,
                NameEnd = nameEnd,
                Level = level,
                HeadingLevel = heading.Level,
                IsSetext = heading.IsSetext,
                Line = firstLine + 1,
                HeadingStart = source.Lines[firstLine].Start,
                HeadingEnd = source.Lines[lastLine].EndIncludingTerminator,
            };
            section.BodyStart = section.HeadingEnd;

            if (!isOutline)
            {
                ReadSectionComment(source, bodyStart, section, lastLine, htmlByLine, commentLines, diagnostics);
            }

            while (stack.Count > 0 && stack.Peek().Level >= level)
            {
                stack.Pop();
            }

            if (stack.Count > 0)
            {
                section.Parent = stack.Peek();
                section.Parent.AddSubsection(section);
            }

            stack.Push(section);
            sections.Add(section);
        }

        // Each section's own text ends at the next heading; the whole section
        // ends at the next heading of the same or a higher level.
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            s.OwnEnd = i + 1 < sections.Count ? sections[i + 1].HeadingStart : text.Length;
            s.End = text.Length;
            for (var j = i + 1; j < sections.Count; j++)
            {
                if (sections[j].Level <= s.Level)
                {
                    s.End = sections[j].HeadingStart;
                    break;
                }
            }
        }

        foreach (var s in sections)
        {
            s.BodyLine = s.BodyStart >= text.Length && !source.EndsWithNewline && text.Length > 0
                ? source.Lines.Count + 1
                : source.LineIndexOf(s.BodyStart) + 1;
        }

        FindMisplacedComments(source, document, bodyStart, commentLines, diagnostics, isOutline);
        var leadEnd = sections.Count > 0 ? sections[0].HeadingStart : text.Length;
        return new Result(sections, title, titleLine, leadEnd);
    }

    /// <summary>
    /// Looks for a section comment on the line after the heading, or after
    /// one blank line (F-SEC-6), and parses it (F-SEC-7, F-SEC-10).
    /// </summary>
    private static void ReadSectionComment(SourceText source, int bodyStart, Section section, int headingLastLine,
        Dictionary<int, HtmlBlock> htmlByLine, HashSet<int> commentLines, List<Diagnostic> diagnostics)
    {
        var candidate = headingLastLine + 1;
        if (candidate < source.Lines.Count && IsBlank(source.LineText(candidate)) && !htmlByLine.ContainsKey(candidate))
        {
            candidate++;
        }

        // The comment must start an HTML block: a line indented as code, or
        // inside some other block, isn't a comment at all.
        if (candidate >= source.Lines.Count || !htmlByLine.TryGetValue(candidate, out var html))
        {
            return;
        }

        var lineText = source.LineText(candidate);
        if (!WolfdenCommentStart().IsMatch(lineText))
        {
            return;
        }

        commentLines.Add(candidate);
        var match = PosLine().Match(lineText);
        if (!match.Success)
        {
            var blockEnd = source.LineIndexOf(bodyStart + html.Span.End) + 1;
            var where = blockEnd > candidate + 1
                ? $" It doesn't close on its own line, so everything up to line {blockEnd} is hidden in an HTML comment."
                : "";
            diagnostics.Add(new Diagnostic("F-SEC-10", DiagnosticSeverity.Warning, candidate + 1,
                $"The wolfden comment under '{section.Name}' isn't well-formed, so it's ignored.{where}"));
            return;
        }

        var attributes = new List<SectionAttribute>();
        foreach (Match attr in Attribute().Matches(match.Groups["attrs"].Value))
        {
            var quoted = attr.Groups["q"].Success;
            attributes.Add(new SectionAttribute(attr.Groups["name"].Value,
                quoted ? attr.Groups["q"].Value : attr.Groups["v"].Value, quoted));
        }

        var line = candidate + 1;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attr in attributes)
        {
            if (!seen.Add(attr.Name))
            {
                diagnostics.Add(new Diagnostic("F-SEC-7", DiagnosticSeverity.Error, line,
                    $"The attribute '{attr.Name}' is repeated."));
                continue;
            }

            switch (attr.Name)
            {
                case "from" or "until" when !PositionScanner.IsEmptyBound(attr.Value)
                                            && !StoryPosition.TryParse(attr.Value, out _, out var positionError):
                    // A bad value in a well-formed comment is an error (F-SEC-10).
                    diagnostics.Add(positionError.ToDiagnostic(DiagnosticSeverity.Error, line));
                    break;
                case "status" when !Statuses.Contains(attr.Value):
                    diagnostics.Add(new Diagnostic("F-STAT-1", DiagnosticSeverity.Error, line,
                        $"'{attr.Value}' isn't a status. Use idea, undecided, candidate, draft, canon, or retconned."));
                    break;
                case "pin" when attr.Value is not ("true" or "false"):
                    diagnostics.Add(new Diagnostic("F-PIN-1", DiagnosticSeverity.Error, line,
                        $"pin must be true or false, not '{attr.Value}'."));
                    break;
                case "canon" when attr.Value is not ("true" or "false"):
                    diagnostics.Add(new Diagnostic("F-SEC-7", DiagnosticSeverity.Error, line,
                        $"canon must be true or false, not '{attr.Value}'."));
                    break;
                case var name when !KnownAttributes.Contains(name):
                    diagnostics.Add(new Diagnostic("F-SEC-7", DiagnosticSeverity.Warning, line,
                        $"Unknown attribute '{name}' in a section comment. It's kept, but does nothing."));
                    break;
            }
        }

        section.Comment = new SectionComment(line, lineText, attributes);
        section.BodyStart = source.Lines[candidate].EndIncludingTerminator;
    }

    /// <summary>Reports wolfden comments outside a section comment's place, and every one in an outline (F-SEC-10).</summary>
    private static void FindMisplacedComments(SourceText source, MarkdigDocument document, int bodyStart,
        HashSet<int> commentLines, List<Diagnostic> diagnostics, bool isOutline)
    {
        const string InOutline = "A wolfden comment does nothing in outline.md, so it's ignored. Rows have no positions: map a row to a chapter with its chapter cell.";
        const string Elsewhere = "A wolfden comment does nothing here. Its only place is the line under a section heading.";
        var found = new SortedDictionary<int, string>();
        foreach (var html in document.Descendants<HtmlBlock>())
        {
            var start = bodyStart + html.Span.Start;
            var blockText = source.Text.Substring(start, html.Span.Length);
            foreach (Match m in WolfdenCommentAnywhere().Matches(blockText))
            {
                var line = source.LineIndexOf(start + m.Index);
                if (!commentLines.Contains(line))
                {
                    found.TryAdd(line, isOutline ? InOutline : Elsewhere);
                }
            }
        }

        foreach (var inline in document.Descendants<HtmlInline>())
        {
            if (inline.Tag is null || !WolfdenCommentAnywhere().IsMatch(inline.Tag) || !inline.Tag.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            var line = source.LineIndexOf(bodyStart + inline.Span.Start);
            var message = isOutline ? InOutline
                : OwningBlock(inline) is HeadingBlock ? "A wolfden comment on a heading line does nothing. Move it to the line under the heading."
                : Elsewhere;
            found[line] = message;
        }

        foreach (var (line, message) in found)
        {
            diagnostics.Add(new Diagnostic("F-SEC-10", DiagnosticSeverity.Warning, line + 1, message));
        }
    }

    private static Block? OwningBlock(Inline inline)
    {
        Inline current = inline;
        while (current.Parent is not null)
        {
            current = current.Parent;
        }

        return (current as ContainerInline)?.ParentBlock;
    }

    /// <summary>The section name (F-SEC-3) from the heading's source text, and the offsets of its text.</summary>
    private static (string Name, int Start, int End) HeadingName(SourceText source, HeadingBlock heading, int bodyStart, int firstLine, int lastLine)
    {
        var text = source.Text;
        int contentStart, contentEnd;
        if (heading.IsSetext)
        {
            var first = source.Lines[firstLine];
            contentStart = first.Start;
            contentEnd = source.Lines[Math.Max(firstLine, lastLine - 1)].End;
        }
        else
        {
            var line = source.Lines[firstLine];
            var i = line.Start;
            while (i < line.End && text[i] == ' ')
            {
                i++;
            }

            while (i < line.End && text[i] == '#')
            {
                i++;
            }

            contentStart = i;
            var end = line.End;
            while (end > contentStart && text[end - 1] is ' ' or '\t')
            {
                end--;
            }

            // An optional closing sequence of #s, after a space or tab.
            var hashes = end;
            while (hashes > contentStart && text[hashes - 1] == '#')
            {
                hashes--;
            }

            if (hashes < end && (hashes == contentStart || text[hashes - 1] is ' ' or '\t'))
            {
                end = hashes;
            }

            contentEnd = end;
        }

        // Trailing HTML comments aren't part of the name.
        if (heading.Inline is { } inlines)
        {
            for (var inline = inlines.LastChild; inline is not null; inline = inline.PreviousSibling)
            {
                if (inline is HtmlInline { Tag: { } tag } && tag.StartsWith("<!--", StringComparison.Ordinal))
                {
                    contentEnd = Math.Min(contentEnd, bodyStart + inline.Span.Start);
                }
                else if (inline is not LiteralInline literal || !IsBlank(literal.Content.ToString()))
                {
                    break;
                }
            }
        }

        if (contentEnd <= contentStart)
        {
            return ("", contentStart, contentStart);
        }

        // Where the name's text sits, without the spaces around it, so a
        // rename replaces just those characters.
        int nameStart = contentStart, nameEnd = contentEnd;
        while (nameStart < nameEnd && text[nameStart] is ' ' or '\t' or '\r' or '\n')
        {
            nameStart++;
        }

        while (nameEnd > nameStart && text[nameEnd - 1] is ' ' or '\t' or '\r' or '\n')
        {
            nameEnd--;
        }

        var lines = text[contentStart..contentEnd].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return (string.Join(' ', lines.Select(l => l.Trim(' ', '\t')).Where(l => l.Length > 0)), nameStart, nameEnd);
    }

    private static bool IsBlank(string line) => line.All(c => c is ' ' or '\t');

    /// <summary>True if <paramref name="markdown"/> holds a CommonMark link
    /// reference definition (<c>[ref]: https://…</c>) outside code.</summary>
    internal static bool HasLinkReferenceDefinition(string markdown)
    {
        if (!markdown.Contains("]:", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return Markdown.Parse(markdown, Pipeline).Descendants<LinkReferenceDefinition>().Any();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Too deep to parse: assume there may be one.
            return true;
        }
    }

    [GeneratedRegex(@"^[ \t]*<!--\s*wolfden:", RegexOptions.CultureInvariant)]
    private static partial Regex WolfdenCommentStart();

    [GeneratedRegex(@"<!--\s*wolfden:", RegexOptions.CultureInvariant)]
    private static partial Regex WolfdenCommentAnywhere();

    /// <summary>The <c>pos-line</c> grammar of format.md §9.2.</summary>
    [GeneratedRegex(
        """^ {0,3}<!--[ \t]*wolfden:[ \t]*(?<attrs>[a-z][a-z0-9_-]*=(?:"(?:(?!-->)[^"\r\n])*"|(?:(?!-->)[^"\p{Zs}\t\n\f\r])+)(?:[ \t]+[a-z][a-z0-9_-]*=(?:"(?:(?!-->)[^"\r\n])*"|(?:(?!-->)[^"\p{Zs}\t\n\f\r])+))*)[ \t]*-->[ \t]*$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex PosLine();

    [GeneratedRegex("""(?<name>[a-z][a-z0-9_-]*)=(?:"(?<q>[^"]*)"|(?<v>[^"\p{Zs}\t\n\f\r]+))""", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();
}
