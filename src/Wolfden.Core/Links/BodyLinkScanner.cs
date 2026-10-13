using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Wolfden.Core.Documents;
using Wolfden.Core.Text;
using MarkdigDocument = Markdig.Syntax.MarkdownDocument;

namespace Wolfden.Core.Links;

/// <summary>A link found in a file, with its place in the file.</summary>
/// <param name="Link">The link, with an empty target if it has none (F-LINK-3).</param>
/// <param name="Offset">Offset of its <c>[[</c> in the file's text.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
/// <param name="Row">For a link in an outline row, the row (F-OUT-9).</param>
internal sealed record FoundLink(LinkSyntax.RawLink Link, int Offset, int Line, int Column, OutlineRowCells? Row = null)
{
    public WikiLink ToWikiLink(string text) => Link.ToLink(text, Line, Column);
}

/// <summary>The cells of one outline row that links depend on (F-OUT-9).</summary>
/// <param name="RowId">The row id, valid by the <c>row-id</c> grammar.</param>
/// <param name="Status">The <c>status</c> cell, trimmed, or null.</param>
/// <param name="Chapter">The <c>chapter</c> cell, trimmed, or null.</param>
/// <param name="Line">The row's 1-based line.</param>
internal sealed record OutlineRowCells(string RowId, string? Status, string? Chapter, int Line);

/// <summary>
/// Finds the links in a file's body (format.md F-LINK-1): anywhere except
/// code spans, code blocks, and HTML comments. In <c>outline.md</c>, GFM
/// table rows are split into cells first, so an unescaped <c>|</c> ends a
/// cell, and links in outline rows carry the row (F-OUT-12, F-OUT-9).
/// </summary>
internal static class BodyLinkScanner
{
    private static readonly MarkdownPipeline Plain = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();

    private static readonly MarkdownPipeline WithTables =
        new MarkdownPipelineBuilder().UsePreciseSourceLocation().UsePipeTables().Build();

    /// <summary>The links in an entity or container file's body, in file order.</summary>
    public static List<FoundLink> ScanBody(MarkdownFile file)
    {
        var result = new List<FoundLink>();
        if (file.SourceForReading is not { } source || Parse(source.Text, file.BodyStart, Plain) is not { } document)
        {
            return result;
        }

        var excluded = Exclusions(document, file.BodyStart, source.Text);
        Collect(source, file.BodyStart, source.Text.Length, excluded, null, result);
        return result;
    }

    /// <summary>The links in an outline's body, in file order.</summary>
    public static List<FoundLink> ScanOutline(MarkdownFile file)
    {
        var result = new List<FoundLink>();
        if (file.SourceForReading is not { } source || Parse(source.Text, file.BodyStart, WithTables) is not { } document)
        {
            return result;
        }

        var text = source.Text;
        var bodyStart = file.BodyStart;
        var excluded = Exclusions(document, bodyStart, text);
        var pos = bodyStart;
        foreach (var table in document.OfType<Table>())
        {
            var first = source.LineIndexOf(bodyStart + table.Span.Start);
            var last = source.LineIndexOf(bodyStart + Math.Max(table.Span.Start, table.Span.End));
            if (last <= first || !IsDelimiterRow(source.LineText(first + 1)))
            {
                continue;
            }

            Collect(source, pos, source.Lines[first].Start, excluded, null, result);
            ScanTable(source, first, last, excluded, result);
            pos = source.Lines[last].EndIncludingTerminator;
        }

        Collect(source, pos, text.Length, excluded, null, result);
        return result;
    }

    /// <summary>
    /// Normalizes an outline header cell (F-OUT-11): lowercase, each run of
    /// characters outside <c>a-z</c> and <c>0-9</c> replaced with <c>_</c>,
    /// and <c>_</c> trimmed from both ends.
    /// </summary>
    internal static string NormalizeHeader(string cell)
    {
        var sb = new StringBuilder();
        var pending = false;
        foreach (var c in cell.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pending && sb.Length > 0)
                {
                    sb.Append('_');
                }

                pending = false;
                sb.Append(c);
            }
            else
            {
                pending = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>True for a <c>row-id</c> (format.md §11): a number with no leading zero, then lowercase letters.</summary>
    internal static bool IsRowId(string text)
    {
        var i = 0;
        if (text.Length == 0 || text[0] is < '1' or > '9')
        {
            return false;
        }

        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        while (i < text.Length && char.IsAsciiLetterLower(text[i]))
        {
            i++;
        }

        return i == text.Length;
    }

    private static MarkdigDocument? Parse(string text, int bodyStart, MarkdownPipeline pipeline)
    {
        try
        {
            return Markdown.Parse(text[bodyStart..], pipeline);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An F-FM-2 error, already reported by the parser. The file is excluded.
            return null;
        }
    }

    private static void ScanTable(SourceText source, int first, int last, List<(int Start, int End)> excluded, List<FoundLink> result)
    {
        var text = source.Text;
        var headerCells = SplitCells(text, source.Lines[first].Start, source.Lines[first].End);
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < headerCells.Count; i++)
        {
            // A repeated header is ignored after its first column (F-OUT-11).
            columns.TryAdd(NormalizeHeader(text[headerCells[i].Start..headerCells[i].End]), i);
        }

        var isOutline = columns.ContainsKey("row") && columns.ContainsKey("beat");
        foreach (var cell in headerCells)
        {
            Collect(source, cell.Start, cell.End, excluded, null, result);
        }

        for (var line = first + 2; line <= last; line++)
        {
            var cells = SplitCells(text, source.Lines[line].Start, source.Lines[line].End);
            OutlineRowCells? row = null;
            if (isOutline && Cell(cells, columns["row"]) is { } rowId && IsRowId(rowId))
            {
                row = new OutlineRowCells(rowId, Cell(cells, columns.GetValueOrDefault("status", -1)),
                    Cell(cells, columns.GetValueOrDefault("chapter", -1)), line + 1);
            }

            foreach (var cell in cells)
            {
                Collect(source, cell.Start, cell.End, excluded, row, result);
            }
        }

        string? Cell(List<(int Start, int End)> cells, int index) =>
            index >= 0 && index < cells.Count ? text[cells[index].Start..cells[index].End].Trim() : null;
    }

    /// <summary>
    /// Splits a table line into cells at each unescaped <c>|</c>, as GFM
    /// does, dropping a leading and a trailing pipe.
    /// </summary>
    private static List<(int Start, int End)> SplitCells(string text, int start, int end)
    {
        while (start < end && text[start] is ' ' or '\t')
        {
            start++;
        }

        while (end > start && text[end - 1] is ' ' or '\t')
        {
            end--;
        }

        if (start < end && text[start] == '|')
        {
            start++;
        }

        if (end > start && text[end - 1] == '|' && !LinkSyntax.IsEscaped(text, end - 1, start))
        {
            end--;
        }

        var cells = new List<(int, int)>();
        var cellStart = start;
        for (var i = start; i < end; i++)
        {
            if (text[i] == '|' && !LinkSyntax.IsEscaped(text, i, start))
            {
                cells.Add((cellStart, i));
                cellStart = i + 1;
            }
        }

        cells.Add((cellStart, end));
        return cells;
    }

    private static bool IsDelimiterRow(string line)
    {
        var cells = line.Trim().Trim('|').Split('|');
        return cells.Length > 0 && cells.All(c =>
        {
            var t = c.Trim().TrimStart(':').TrimEnd(':');
            return t.Length > 0 && t.All(ch => ch == '-');
        });
    }

    private static void Collect(SourceText source, int start, int end, List<(int Start, int End)> excluded, OutlineRowCells? row, List<FoundLink> result)
    {
        foreach (var raw in LinkSyntax.Scan(source.Text, start, end, excluded))
        {
            var line = source.LineIndexOf(raw.Offset);
            result.Add(new FoundLink(raw, raw.Offset, line + 1, raw.Offset - source.Lines[line].Start + 1, row));
        }
    }

    /// <summary>The ranges, in file offsets, where links aren't recognized: code and HTML comments.</summary>
    private static List<(int Start, int End)> Exclusions(MarkdigDocument document, int bodyStart, string text)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case CodeBlock block:
                    // An unclosed fence's span can stop short of its last line, so take the lines too.
                    var (blockStart, blockEnd) = Range(block);
                    if (block.Lines.Count > 0)
                    {
                        var lastLine = block.Lines.Lines[block.Lines.Count - 1].Slice;
                        blockEnd = Math.Max(blockEnd, Math.Min(text.Length, bodyStart + lastLine.End + 1));
                    }

                    ranges.Add((blockStart, blockEnd));
                    break;
                case HtmlBlock html:
                    var (start, end) = Range(html);
                    AddComments(text, start, end, ranges);
                    break;
                case CodeInline code:
                    ranges.Add(Range(code));
                    break;
                case HtmlInline { Tag: { } tag } inline when tag.StartsWith("<!--", StringComparison.Ordinal):
                    ranges.Add(Range(inline));
                    break;
            }
        }

        return ranges;

        (int, int) Range(MarkdownObject o) =>
            (bodyStart + o.Span.Start, Math.Min(text.Length, bodyStart + Math.Max(o.Span.Start, o.Span.End) + 1));
    }

    /// <summary>The HTML comments in an HTML block: from each <c>&lt;!--</c> to the next <c>--&gt;</c>, or the end of the block.</summary>
    private static void AddComments(string text, int start, int end, List<(int Start, int End)> ranges)
    {
        var i = start;
        while (i < end)
        {
            var open = text.IndexOf("<!--", i, end - i, StringComparison.Ordinal);
            if (open < 0)
            {
                return;
            }

            // "<!-->" and "<!--->" are complete comments in CommonMark 0.31.2.
            var close = text.IndexOf("-->", open + 2, end - (open + 2), StringComparison.Ordinal);
            var stop = close < 0 ? end : close + 3;
            ranges.Add((open, stop));
            i = stop;
        }
    }
}
