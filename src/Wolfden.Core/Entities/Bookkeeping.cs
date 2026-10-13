using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Wolfden.Core.Documents;
using Wolfden.Core.Links;

namespace Wolfden.Core.Entities;

/// <summary>
/// Decides which sections are bookkeeping: change logs and session notes
/// that record work on the notes rather than the story (format.md F-SEC-12).
/// </summary>
public sealed class Bookkeeping
{
    /// <summary>The built-in bookkeeping patterns (F-SEC-12).</summary>
    public static readonly IReadOnlyList<string> BuiltInPatterns =
    [
        "Change log", "Change log *", "Changelog", "Changelog *", "Revision history", "Revision history *",
        "Session note", "Session note *", "Session notes", "Session notes *",
        "* session note", "* session note *", "* session notes", "* session notes *",
    ];

    private readonly List<Regex> _patterns;

    /// <summary>The built-in patterns plus <paramref name="extra"/>, a den's <c>bookkeeping_sections</c> (F-DEN-6).</summary>
    public Bookkeeping(IEnumerable<string>? extra = null)
    {
        _patterns = BuiltInPatterns.Concat(extra ?? []).Select(Compile).ToList();
    }

    /// <summary>
    /// True if a section with this name matches a bookkeeping pattern. The
    /// name is matched as plain text, with emphasis, code, and link markup
    /// removed, after F-LINK-4 normalization.
    /// </summary>
    public bool MatchesName(string sectionName)
    {
        ArgumentNullException.ThrowIfNull(sectionName);
        var name = LinkText.Normalize(PlainText(sectionName));
        return _patterns.Any(p => p.IsMatch(name));
    }

    /// <summary>
    /// True if the section is bookkeeping: its comment has
    /// <c>canon=false</c>, or its name matches a pattern and its comment
    /// doesn't have <c>canon=true</c>, or it's a subsection of a bookkeeping
    /// section.
    /// </summary>
    public bool IsBookkeeping(Section section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Parent is { } parent && IsBookkeeping(parent))
        {
            return true;
        }

        return section.Comment?["canon"] switch
        {
            "false" => true,
            "true" => false,
            _ => MatchesName(section.Name),
        };
    }

    /// <summary>A heading's text content, as CommonMark reads it.</summary>
    internal static string PlainText(string headingText)
    {
        var document = Markdown.Parse("## " + headingText);
        if (document.FirstOrDefault() is not HeadingBlock { Inline: { } inline })
        {
            return headingText;
        }

        var sb = new StringBuilder();
        Append(inline, sb);
        return sb.ToString();
    }

    private static void Append(ContainerInline container, StringBuilder sb)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case AutolinkInline autolink:
                    sb.Append(autolink.Url);
                    break;
                case HtmlEntityInline entity:
                    sb.Append(entity.Transcoded.ToString());
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case ContainerInline nested:
                    Append(nested, sb);
                    break;
            }
        }
    }

    private static Regex Compile(string pattern)
    {
        var parts = LinkText.Normalize(pattern).Split('*').Select(Regex.Escape);
        return new Regex("^" + string.Join(".*", parts) + "\\z", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
}
