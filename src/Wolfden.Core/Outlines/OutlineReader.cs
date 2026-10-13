using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;

namespace Wolfden.Core.Outlines;

/// <summary>One row of an outline table (format.md §11, F-OUT-9).</summary>
/// <param name="RowId">The row id, valid by the <c>row-id</c> grammar, such as <c>69a</c>.</param>
/// <param name="Number">The row id's number, or null if it's too long to hold.</param>
/// <param name="Suffix">The row id's letter suffix, or an empty string (F-OUT-4).</param>
/// <param name="Line">The row's 1-based line in <c>outline.md</c>.</param>
/// <param name="Order">The row's 0-based place among the outline's rows, in file order.</param>
/// <param name="Heading">The heading of the outline section the row is in (F-OUT-3), or null before the first heading.</param>
/// <param name="Beat">The <c>beat</c> cell, which may be empty.</param>
/// <param name="Chapter">The <c>chapter</c> cell, or null if it's empty.</param>
/// <param name="Pov">The <c>pov</c> cell, or null if it's empty.</param>
/// <param name="Status">The row's status: its <c>status</c> cell matched case-insensitively, or <c>draft</c> (F-OUT-9, F-STAT-2).</param>
/// <param name="Notes">The <c>notes</c> cell, or null if it's empty.</param>
/// <param name="Cells">
/// Every non-empty cell, named and custom, by its normalized header
/// (F-OUT-11), trimmed, with GFM's <c>\|</c> escape removed (F-OUT-12).
/// </param>
public sealed record OutlineRow(
    string RowId, long? Number, string Suffix, int Line, int Order, string? Heading, string Beat, string? Chapter,
    string? Pov, FactStatus Status, string? Notes, IReadOnlyDictionary<string, string> Cells);

/// <summary>
/// Reads the rows of a book's <c>outline.md</c> (format.md §11): every body
/// line of each top-level GFM table whose header has both a <c>row</c> and
/// a <c>beat</c> column. It doesn't report the F-OUT warnings; that's
/// <c>wolfden check</c>'s job (#16).
/// </summary>
public static class OutlineReader
{
    /// <summary>
    /// True if an error excludes the outline (F-GEN-2): one that stops the
    /// file being read, such as bad encoding or bad frontmatter. Errors in
    /// section comments don't, since wolfden comments do nothing in an
    /// outline (F-SEC-10).
    /// </summary>
    public static bool IsExcluded(MarkdownFile outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        return outline.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error
            && (d.RuleId is "F-ENC-1" or "F-ENC-3" or "F-FM-1" or "F-FM-2" || d.RuleId.StartsWith("F-YAML-", StringComparison.Ordinal)));
    }

    /// <summary>The outline's rows in file order. Rows whose <c>row</c> cell isn't a valid row id are left out (F-OUT-9).</summary>
    public static IReadOnlyList<OutlineRow> ReadRows(MarkdownFile outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        var rows = new List<OutlineRow>();
        if (outline.SourceForReading is not { } source
            || BodyLinkScanner.Parse(source.Text, outline.BodyStart, BodyLinkScanner.WithTables) is not { } document)
        {
            return rows;
        }

        var text = source.Text;
        var bodyStart = outline.BodyStart;
        string? heading = null;
        foreach (var block in document)
        {
            if (block is HeadingBlock h)
            {
                heading = HeadingText(source.LineText(source.LineIndexOf(bodyStart + h.Span.Start)), h.IsSetext);
                continue;
            }

            if (block is not Table table)
            {
                continue;
            }

            var first = source.LineIndexOf(bodyStart + table.Span.Start);
            var last = source.LineIndexOf(bodyStart + Math.Max(table.Span.Start, table.Span.End));
            if (last <= first || !BodyLinkScanner.IsDelimiterRow(source.LineText(first + 1)))
            {
                continue;
            }

            var header = BodyLinkScanner.SplitCells(text, source.Lines[first].Start, source.Lines[first].End);
            var columns = new List<(string Key, int Index)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < header.Count; i++)
            {
                // F-OUT-11: an empty key is ignored, and so is a repeat after its first column.
                var key = BodyLinkScanner.NormalizeHeader(text[header[i].Start..header[i].End]);
                if (key.Length > 0 && seen.Add(key))
                {
                    columns.Add((key, i));
                }
            }

            if (!seen.Contains("row") || !seen.Contains("beat"))
            {
                continue;
            }

            for (var line = first + 2; line <= last; line++)
            {
                var cells = BodyLinkScanner.SplitCells(text, source.Lines[line].Start, source.Lines[line].End);
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, index) in columns)
                {
                    if (index < cells.Count && Unescape(text[cells[index].Start..cells[index].End].Trim()) is { Length: > 0 } value)
                    {
                        values[key] = value;
                    }
                }

                if (values.GetValueOrDefault("row") is not { } rowId || !BodyLinkScanner.IsRowId(rowId))
                {
                    continue;
                }

                var digits = rowId.TakeWhile(char.IsAsciiDigit).Count();
                var status = values.GetValueOrDefault("status") is { } s ? FactStatuses.Parse(s.ToLowerInvariant()) ?? FactStatus.Draft : FactStatus.Draft;
                rows.Add(new OutlineRow(
                    rowId,
                    long.TryParse(rowId.AsSpan(0, digits), out var number) ? number : null,
                    rowId[digits..],
                    line + 1,
                    rows.Count,
                    heading,
                    values.GetValueOrDefault("beat") ?? "",
                    values.GetValueOrDefault("chapter"),
                    values.GetValueOrDefault("pov"),
                    status,
                    values.GetValueOrDefault("notes"),
                    values));
            }
        }

        return rows;
    }

    /// <summary>GFM removes the backslash from <c>\|</c> in a cell before reading it (F-OUT-12).</summary>
    private static string Unescape(string cell) => cell.Replace("\\|", "|", StringComparison.Ordinal);

    /// <summary>A heading's text without its <c>#</c> markers or closing sequence, trimmed.</summary>
    private static string HeadingText(string line, bool setext)
    {
        var text = line.Trim();
        if (setext)
        {
            return text;
        }

        text = text.TrimStart('#').Trim();
        var closing = text.TrimEnd('#');
        return closing.Length == 0 || closing.EndsWith(' ') || closing.EndsWith('\t') ? closing.Trim() : text;
    }
}
