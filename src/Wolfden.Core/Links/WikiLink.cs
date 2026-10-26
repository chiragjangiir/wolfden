using System.Text.RegularExpressions;

namespace Wolfden.Core.Links;

/// <summary>
/// A wiki-link as written: <c>[[target]]</c>, <c>[[target#Heading]]</c>,
/// <c>[[target|label]]</c>, or <c>[[target#Heading|label]]</c> (format.md §10.1).
/// </summary>
/// <param name="Text">The link exactly as written, from <c>[[</c> to <c>]]</c>.</param>
/// <param name="Target">The target, trimmed (F-LINK-3). Never empty.</param>
/// <param name="Heading">The heading after <c>#</c>, trimmed, or null.</param>
/// <param name="Label">The label after <c>|</c>, trimmed, or null.</param>
/// <param name="Line">The 1-based line of the <c>[[</c> in the file.</param>
/// <param name="Column">The 1-based column of the <c>[[</c> on that line, in UTF-16 code units.</param>
public sealed record WikiLink(string Text, string Target, string? Heading, string? Label, int Line, int Column)
{
    /// <summary>The text a reader sees: the label, or else the link's text as written (§10.1).</summary>
    public string DisplayText => Label ?? Text[2..^2];

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>The link grammar of format.md §10.1.</summary>
public static partial class LinkSyntax
{
    /// <summary>
    /// Finds the links in a piece of plain text, such as a frontmatter
    /// string. Lines and columns count from the start of
    /// <paramref name="text"/>. A <c>[[</c> after a backslash isn't a link
    /// (F-LINK-1), nor is an embed, <c>![[...]]</c> (F-LINK-2), nor a link
    /// with an empty target (F-LINK-3).
    /// </summary>
    public static IReadOnlyList<WikiLink> FindLinks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<WikiLink>();
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        foreach (var raw in Scan(text, 0, text.Length, null))
        {
            if (raw.IsEmpty)
            {
                continue;
            }

            var index = lineStarts.BinarySearch(raw.Offset);
            var line = index >= 0 ? index : ~index - 1;
            result.Add(raw.ToLink(text, line + 1, raw.Offset - lineStarts[line] + 1));
        }

        return result;
    }

    /// <summary>A link found by <see cref="Scan"/>, before it has a line and column.</summary>
    internal readonly record struct RawLink(int Offset, int Length, string Target, string? Heading, string? Label)
    {
        /// <summary>True for a link with an empty target, which isn't a link (F-LINK-3).</summary>
        public bool IsEmpty => Target.Length == 0;

        public WikiLink ToLink(string text, int line, int column) =>
            new(text.Substring(Offset, Length), Target, Heading, Label, line, column);
    }

    /// <summary>
    /// Finds links in <c>text[start..end)</c>, skipping any that overlap an
    /// excluded range (code and HTML comments in a body). Links with an empty
    /// target are returned too, so the caller can report them (F-LINK-3).
    /// </summary>
    internal static IEnumerable<RawLink> Scan(string text, int start, int end, RangeSet? excluded)
    {
        var regex = LinkPattern();
        var pos = start;
        while (pos < end)
        {
            var m = regex.Match(text, pos, end - pos);
            if (!m.Success)
            {
                yield break;
            }

            var index = m.Index;
            if (IsEscaped(text, index, start) || IsEmbed(text, index, start) || excluded?.Overlaps(index, index + m.Length) == true)
            {
                // Something else may start inside this match, such as the
                // second '[' of '\[[[a]]'.
                pos = index + 1;
                continue;
            }

            pos = index + m.Length;
            var heading = Trimmed(m.Groups["h"]);
            var label = Trimmed(m.Groups["l"])?.Replace("\\|", "|", StringComparison.Ordinal);
            yield return new RawLink(index, m.Length, m.Groups["t"].Value.Trim(), heading, label);
        }
    }

    private static string? Trimmed(Group group)
    {
        if (!group.Success)
        {
            return null;
        }

        var value = group.Value.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>True if the character at <paramref name="index"/> follows an odd number of backslashes.</summary>
    internal static bool IsEscaped(string text, int index, int floor)
    {
        var count = 0;
        for (var i = index - 1; i >= floor && text[i] == '\\'; i--)
        {
            count++;
        }

        return count % 2 == 1;
    }

    /// <summary>
    /// <paramref name="linkText"/>, a whole link such as <c>[[mara-voss#Old|Voss]]</c>,
    /// with its heading replaced by <paramref name="heading"/>. The spacing
    /// around the old heading, the target, and the label stay as written.
    /// Null if it isn't one link with a heading.
    /// </summary>
    internal static string? WithHeading(string linkText, string heading)
    {
        var m = LinkPattern().Match(linkText);
        if (!m.Success || m.Index != 0 || m.Length != linkText.Length || !m.Groups["h"].Success)
        {
            return null;
        }

        var group = m.Groups["h"];
        var value = group.Value;
        var start = group.Index + (value.Length - value.TrimStart().Length);
        var end = group.Index + value.TrimEnd().Length;
        return end <= start ? null : string.Concat(linkText.AsSpan(0, start), heading, linkText.AsSpan(end));
    }

    /// <summary>
    /// <paramref name="linkText"/>, a whole link such as <c>[[Mr. Swales # Voice |him]]</c>,
    /// with its target replaced by <paramref name="target"/>. The spacing
    /// around the old target, the heading, and the label stay as written.
    /// Null if it isn't one link with a target.
    /// </summary>
    internal static string? WithTarget(string linkText, string target)
    {
        var m = LinkPattern().Match(linkText);
        if (!m.Success || m.Index != 0 || m.Length != linkText.Length)
        {
            return null;
        }

        var group = m.Groups["t"];
        var value = group.Value;
        var start = group.Index + (value.Length - value.TrimStart().Length);
        var end = group.Index + value.TrimEnd().Length;
        return end <= start ? null : string.Concat(linkText.AsSpan(0, start), target, linkText.AsSpan(end));
    }

    /// <summary>
    /// <paramref name="linkText"/>, a whole link such as <c>[[Voss#Arc]]</c>,
    /// with its target replaced by <paramref name="id"/>, so it reaches its
    /// entity by id (F-LINK-6), and showing what it showed before: a link
    /// without a label gets its target and heading as written as its label
    /// (<c>[[01JA...#Arc|Voss#Arc]]</c>). In a table row the label goes
    /// after <c>\|</c>, so the cell isn't split (F-OUT-12). Null if it isn't
    /// one link with a target.
    /// </summary>
    internal static string? WithIdTarget(string linkText, string id, bool inTableRow)
    {
        var m = LinkPattern().Match(linkText);
        if (!m.Success || m.Index != 0 || m.Length != linkText.Length || WithTarget(linkText, id) is not { } retargeted)
        {
            return null;
        }

        if (m.Groups["l"].Success)
        {
            return retargeted;
        }

        var shown = linkText[2..^2].Trim();
        return string.Concat(retargeted.AsSpan(0, retargeted.Length - 2), inTableRow ? "\\|" : "|", shown, "]]");
    }

    private static bool IsEmbed(string text, int index, int floor) =>
        index - 1 >= floor && text[index - 1] == '!' && !IsEscaped(text, index - 1, floor);

    /// <summary>
    /// <c>link = "[[" target [ "#" heading ] [ "|" label ] "]]"</c>. The
    /// target may be empty here, for the F-LINK-3 warning. A <c>\|</c> also
    /// separates the label, so a link escaped for a table cell
    /// (<c>[[mara-voss\|Voss]]</c>) reads the same everywhere (F-OUT-12).
    /// </summary>
    [GeneratedRegex(
        """\[\[(?<t>(?:[^\[\]|#\r\n\\]|\\(?!\|))*)(?:\#(?<h>(?:[^\[\]|#\r\n\\]|\\(?!\|))+))?(?:\\?\|(?<l>[^\[\]\r\n]+))?\]\]""",
        RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();
}

/// <summary>
/// A set of half-open text ranges, such as the code and comments of a body,
/// that answers "does this span overlap any of them?" in logarithmic time.
/// </summary>
internal sealed class RangeSet
{
    private readonly int[] _starts;
    private readonly int[] _maxEnds;

    public RangeSet(IEnumerable<(int Start, int End)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ToArray();
        _starts = new int[sorted.Length];
        _maxEnds = new int[sorted.Length];
        var max = int.MinValue;
        for (var i = 0; i < sorted.Length; i++)
        {
            _starts[i] = sorted[i].Start;
            max = Math.Max(max, sorted[i].End);
            _maxEnds[i] = max;
        }
    }

    /// <summary>True if <c>[start, end)</c> overlaps any range.</summary>
    public bool Overlaps(int start, int end)
    {
        // The ranges that start before end are a prefix of the sorted list;
        // one of them overlaps if the furthest any of them reaches is past start.
        var count = UpperBound(end - 1);
        return count > 0 && _maxEnds[count - 1] > start;
    }

    /// <summary>The number of ranges whose start is at most <paramref name="value"/>.</summary>
    private int UpperBound(int value)
    {
        int lo = 0, hi = _starts.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_starts[mid] <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
