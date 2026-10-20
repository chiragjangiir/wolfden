using System.Text.RegularExpressions;
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
    string? Pov, FactStatus Status, string? Notes, IReadOnlyDictionary<string, string> Cells)
{
    /// <summary>
    /// Compares two row ids in row order (F-OUT-4): by number, then by
    /// letter suffix as an ASCII string, with no suffix first, so
    /// <c>69 &lt; 69a &lt; 69aa &lt; 69b &lt; 70</c>. Numbers of any length compare
    /// correctly, since a row id has no leading zeros.
    /// </summary>
    public static int CompareIds(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var da = a.TakeWhile(char.IsAsciiDigit).Count();
        var db = b.TakeWhile(char.IsAsciiDigit).Count();
        var byNumber = da != db ? da.CompareTo(db) : string.CompareOrdinal(a[..da], b[..db]);
        return byNumber != 0 ? byNumber : string.CompareOrdinal(a[da..], b[db..]);
    }
}

/// <summary>What reading an outline gave: its rows and the problems found in the file itself.</summary>
/// <param name="Rows">The rows, in file order.</param>
/// <param name="Diagnostics">
/// The F-OUT warnings that depend only on the file (F-OUT-2, F-OUT-3,
/// F-OUT-4, F-OUT-9's row ids and statuses, F-OUT-11, F-OUT-12), by line.
/// The file's parse diagnostics are in its own <see cref="DenFile.Diagnostics"/>.
/// </param>
public sealed record OutlineRead(IReadOnlyList<OutlineRow> Rows, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Reads the rows of a book's <c>outline.md</c> (format.md §11): every body
/// line of each top-level GFM table whose header has both a <c>row</c> and
/// a <c>beat</c> column. The warnings that span files (F-OUT-5, F-OUT-6,
/// F-OUT-9's chapters, F-OUT-10) are <c>wolfden check</c>'s.
/// </summary>
public static partial class OutlineReader
{
    /// <summary>
    /// True if an error excludes the outline (F-GEN-2): one that stops the
    /// file being read, such as bad encoding or bad frontmatter. Parse the
    /// file with <see cref="MarkdownFile.ParseOutline"/>, where wolfden
    /// comments are only ever warnings (F-SEC-10). For a file parsed as an
    /// entity would be, errors in its section comments are ignored here.
    /// </summary>
    public static bool IsExcluded(MarkdownFile outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        return outline.IsOutline
            ? outline.HasErrors
            : outline.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error
                && (d.RuleId is "F-ENC-1" or "F-ENC-3" or "F-FM-1" or "F-FM-2" || d.RuleId.StartsWith("F-YAML-", StringComparison.Ordinal)));
    }

    /// <summary>The outline's rows in file order. Rows whose <c>row</c> cell isn't a valid row id are left out (F-OUT-9).</summary>
    public static IReadOnlyList<OutlineRow> ReadRows(MarkdownFile outline) => Read(outline).Rows;

    /// <summary>The outline's rows in file order, and the warnings that depend only on the file.</summary>
    public static OutlineRead Read(MarkdownFile outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        var rows = new List<OutlineRow>();
        var diagnostics = new List<Diagnostic>();
        if (outline.SourceForReading is not { } source
            || BodyLinkScanner.Parse(source.Text, outline.BodyStart, BodyLinkScanner.WithTables) is not { } document)
        {
            return new OutlineRead(rows, diagnostics);
        }

        void Warn(string rule, int line, string message) => diagnostics.Add(new Diagnostic(rule, DiagnosticSeverity.Warning, line, message));

        var text = source.Text;
        var bodyStart = outline.BodyStart;
        string? heading = null;
        var section = 0;
        int? tableInSection = null;
        OutlineRow? previous = null;
        foreach (var block in document)
        {
            if (block is HeadingBlock h)
            {
                heading = HeadingText(source.LineText(source.LineIndexOf(bodyStart + h.Span.Start)), h.IsSetext);
                section++;
                tableInSection = null;
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
            var keys = header.Select(c => BodyLinkScanner.NormalizeHeader(text[c.Start..c.End])).ToList();
            var hasRow = keys.Contains("row");
            var hasBeat = keys.Contains("beat");
            if (!hasRow || !hasBeat)
            {
                if (hasRow || hasBeat)
                {
                    Warn("F-OUT-2", first + 1, hasRow
                        ? "This table has a row column but no beat column, so it's a free-form note, not an outline table. Did you mean an outline table? Add a beat column."
                        : "This table has a beat column but no row column, so it's a free-form note, not an outline table. Did you mean an outline table? Add a row column.");
                }

                continue;
            }

            // F-OUT-3: at most one outline table per section.
            if (tableInSection is { } earlier)
            {
                Warn("F-OUT-3", first + 1, $"This is the second outline table in this section (the first is on line {earlier}). Its rows are still read, but each section should hold one outline table.");
            }
            else
            {
                tableInSection = first + 1;
            }

            // F-OUT-11: a header that isn't a field name is ignored, and so is a repeat after its first column.
            var columns = new List<(string Key, int Index)>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < keys.Count; i++)
            {
                var written = text[header[i].Start..header[i].End].Trim();
                if (!FieldName().IsMatch(keys[i]))
                {
                    Warn("F-OUT-11", first + 1, $"The header '{written}' doesn't make a column name (lowercase letters, digits, and '_', starting with a letter), so the column is ignored.");
                }
                else if (!seen.TryAdd(keys[i], i))
                {
                    Warn("F-OUT-11", first + 1, $"The header '{written}' is the column '{keys[i]}' again, so this later column is ignored.");
                }
                else
                {
                    columns.Add((keys[i], i));
                }
            }

            for (var line = first + 2; line <= last; line++)
            {
                var cells = BodyLinkScanner.SplitCells(text, source.Lines[line].Start, source.Lines[line].End);
                if (cells.Count > header.Count)
                {
                    Warn("F-OUT-12", line + 1, $"This row has {cells.Count} cells, but the header has {header.Count}. An unescaped | splits a cell and shifts the columns after it: write \\| for a | inside a cell, as in [[mara-voss\\|Voss]].");
                }

                foreach (var cell in cells)
                {
                    if (HasUnclosedLink(text, cell.Start, cell.End))
                    {
                        Warn("F-OUT-12", line + 1, $"The cell '{text[cell.Start..cell.End].Trim()}' has a [[ with no ]] after it. If a link's | split the cell, write it as \\|, as in [[mara-voss\\|Voss]].");
                    }
                }

                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, index) in columns)
                {
                    if (index < cells.Count && Unescape(text[cells[index].Start..cells[index].End].Trim()) is { Length: > 0 } value)
                    {
                        values[key] = value;
                    }
                }

                if (values.GetValueOrDefault("row") is not { } rowId)
                {
                    Warn("F-OUT-9", line + 1, "This row has no row id, so it's ignored. Give it one, such as 37 or 37a.");
                    continue;
                }

                if (!BodyLinkScanner.IsRowId(rowId))
                {
                    Warn("F-OUT-9", line + 1, $"'{rowId}' isn't a row id (a number with no leading zero, then any lowercase letters, such as 37 or 37a), so the row is ignored.");
                    continue;
                }

                var status = FactStatus.Draft;
                if (values.GetValueOrDefault("status") is { } written)
                {
                    if (FactStatuses.Parse(written.ToLowerInvariant()) is { } parsed)
                    {
                        status = parsed;
                    }
                    else
                    {
                        Warn("F-OUT-9", line + 1, $"Row {rowId}: '{written}' isn't a status, so the row is read as draft. Use idea, undecided, candidate, draft, canon, or retconned.");
                    }
                }

                var digits = rowId.TakeWhile(char.IsAsciiDigit).Count();
                var row = new OutlineRow(
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
                    values);

                // F-OUT-4: each row comes after the one before it. A repeat is F-OUT-5's.
                if (previous is not null && OutlineRow.CompareIds(rowId, previous.RowId) < 0)
                {
                    Warn("F-OUT-4", line + 1, $"Row {rowId} comes after row {previous.RowId} (line {previous.Line}) in the file, but before it in row order. Keep rows in ascending order.");
                }

                previous = row;
                rows.Add(row);
            }
        }

        return new OutlineRead(rows, diagnostics);
    }

    /// <summary>GFM removes the backslash from <c>\|</c> in a cell before reading it (F-OUT-12).</summary>
    private static string Unescape(string cell) => cell.Replace("\\|", "|", StringComparison.Ordinal);

    /// <summary>F-OUT-12: a <c>[[</c>, not escaped, with no <c>]]</c> after it in the same cell.</summary>
    private static bool HasUnclosedLink(string text, int start, int end)
    {
        var cell = text[start..end];
        var i = cell.IndexOf("[[", StringComparison.Ordinal);
        while (i >= 0)
        {
            if (LinkSyntax.IsEscaped(cell, i, 0))
            {
                i = cell.IndexOf("[[", i + 1, StringComparison.Ordinal);
                continue;
            }

            var close = cell.IndexOf("]]", i + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                return true;
            }

            i = cell.IndexOf("[[", close + 2, StringComparison.Ordinal);
        }

        return false;
    }

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

    /// <summary>A field name (format.md F-FLD-1), which an outline header must normalize to (F-OUT-11).</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FieldName();
}
