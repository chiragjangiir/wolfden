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
        foreach (var raw in Scan(text, 0, text.Length, null))
        {
            if (raw.IsEmpty)
            {
                continue;
            }

            var lineStart = raw.Offset == 0 ? 0 : text.LastIndexOf('\n', raw.Offset - 1) + 1;
            var line = 1 + text.AsSpan(0, lineStart).Count('\n');
            result.Add(raw.ToLink(text, line, raw.Offset - lineStart + 1));
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
    internal static IEnumerable<RawLink> Scan(string text, int start, int end, IReadOnlyList<(int Start, int End)>? excluded)
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
            if (IsEscaped(text, index, start) || IsEmbed(text, index, start) || Overlaps(excluded, index, index + m.Length))
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

    private static bool IsEmbed(string text, int index, int floor) =>
        index - 1 >= floor && text[index - 1] == '!' && !IsEscaped(text, index - 1, floor);

    private static bool Overlaps(IReadOnlyList<(int Start, int End)>? ranges, int start, int end)
    {
        if (ranges is null)
        {
            return false;
        }

        foreach (var (s, e) in ranges)
        {
            if (s < end && start < e)
            {
                return true;
            }
        }

        return false;
    }

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
