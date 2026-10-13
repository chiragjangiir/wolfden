using System.Text;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Wolfden.Core.Links;

namespace Wolfden.Core.Documents;

/// <summary>
/// Separates the HTML comments in markdown (format.md F-SEC-11) from the
/// text around them, as CommonMark reads them: a <c>&lt;!--</c> in a code
/// span or code block is just text.
/// </summary>
public static class MarkdownComments
{
    /// <summary>
    /// Splits <paramref name="markdown"/> into its text with every HTML
    /// comment replaced by a space, and the comments' contents (without
    /// <c>&lt;!--</c> and <c>--&gt;</c>), one per line.
    /// </summary>
    public static (string Text, string Comments) Split(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (!markdown.Contains("<!--", StringComparison.Ordinal)
            || BodyLinkScanner.Parse(markdown, 0, BodyLinkScanner.Plain) is not { } document)
        {
            return (markdown, "");
        }

        var ranges = new List<(int Start, int End)>();
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case HtmlBlock html:
                    var (start, end) = Range(html);
                    BodyLinkScanner.AddComments(markdown, start, end, ranges);
                    break;
                case HtmlInline { Tag: { } tag } inline when tag.StartsWith("<!--", StringComparison.Ordinal):
                    ranges.Add(Range(inline));
                    break;
            }
        }

        if (ranges.Count == 0)
        {
            return (markdown, "");
        }

        var text = new StringBuilder();
        var comments = new StringBuilder();
        var at = 0;
        foreach (var (start, end) in ranges.OrderBy(r => r.Start))
        {
            if (start < at)
            {
                continue;
            }

            text.Append(markdown, at, start - at).Append(' ');
            var comment = markdown[start..end];
            comment = comment.StartsWith("<!--", StringComparison.Ordinal) ? comment[4..] : comment;
            comment = comment.EndsWith("-->", StringComparison.Ordinal) ? comment[..^3] : comment;
            comments.Append(comments.Length > 0 ? "\n" : "").Append(comment.Trim());
            at = end;
        }

        text.Append(markdown, at, markdown.Length - at);
        return (text.ToString(), comments.ToString());

        (int, int) Range(MarkdownObject o) =>
            (o.Span.Start, Math.Min(markdown.Length, Math.Max(o.Span.Start, o.Span.End) + 1));
    }
}
