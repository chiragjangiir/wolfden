using System.Diagnostics.CodeAnalysis;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Positions;

/// <summary>
/// Why a string isn't a story position: the format.md rule it breaks and a
/// message for the writer.
/// </summary>
/// <param name="RuleId"><c>F-POS-2</c> (scene form), <c>F-POS-6</c> (row form), or <c>F-POS-3</c> (anything else).</param>
/// <param name="Message">A message for the writer, quoting the text.</param>
public sealed record PositionSyntaxError(string RuleId, string Message)
{
    /// <summary>
    /// This error as a diagnostic. <c>from</c> and <c>until</c> make it an
    /// error; a <c>position</c> field's value makes it a warning under
    /// types.md F-FT-10.
    /// </summary>
    public Diagnostic ToDiagnostic(DiagnosticSeverity severity, int? line, string? ruleId = null) =>
        new(ruleId ?? RuleId, severity, line, Message);
}

/// <summary>
/// A story position as written: a book key and, optionally, a chapter key
/// (format.md §8.1). <c>ember:14</c> is chapter <c>14</c> of <c>ember</c>,
/// and a bare <c>ember</c> is the start of the book (F-POS-7). A position
/// names keys, not chapter numbers, so renumbering chapters never changes
/// what it refers to (F-BOOK-6). Where it falls in story order is decided by
/// resolving it against a universe (<see cref="Universe.Resolve(StoryPosition)"/>).
/// </summary>
public sealed record StoryPosition
{
    /// <summary>The F-POS-2 message.</summary>
    public const string SceneMessage = "scenes are not supported yet; v0 positions are chapter-level";

    /// <summary>The F-POS-6 message.</summary>
    public const string RowMessage = "outline-row positions are not supported yet; v0 positions are chapter-level";

    /// <summary>The F-POS-3 message.</summary>
    public const string InvalidMessage = "invalid story position";

    private StoryPosition(string bookKey, string? chapterKey)
    {
        BookKey = bookKey;
        ChapterKey = chapterKey;
    }

    /// <summary>The book key, the part before the <c>:</c>.</summary>
    public string BookKey { get; }

    /// <summary>The chapter key, or null for a bare book position (F-POS-7).</summary>
    public string? ChapterKey { get; }

    /// <summary>True for a bare book position such as <c>ember</c>: the start of the book (F-POS-7).</summary>
    public bool IsBookStart => ChapterKey is null;

    /// <summary>A position in <paramref name="bookKey"/> at <paramref name="chapterKey"/>, or its start if null.</summary>
    /// <exception cref="ArgumentException">A key isn't valid for its part of a position.</exception>
    public static StoryPosition Create(string bookKey, string? chapterKey = null)
    {
        ArgumentNullException.ThrowIfNull(bookKey);
        if (!Keys.IsContainerKey(bookKey))
        {
            throw new ArgumentException($"'{bookKey}' isn't a book key.", nameof(bookKey));
        }

        if (chapterKey is not null && !Keys.IsChapterKey(chapterKey))
        {
            throw new ArgumentException($"'{chapterKey}' isn't a chapter key.", nameof(chapterKey));
        }

        return new StoryPosition(bookKey, chapterKey);
    }

    /// <summary>Parses a position, throwing if it isn't one.</summary>
    /// <exception cref="FormatException">The text isn't a story position.</exception>
    public static StoryPosition Parse(string text) =>
        TryParse(text, out var position, out var error) ? position : throw new FormatException(error.Message);

    /// <summary>
    /// Parses a position (format.md §8.1). The text is taken exactly as
    /// written: no trimming, since a position has no whitespace (F-POS-1).
    /// The row form is checked first (F-POS-6), then the scene form
    /// (F-POS-2), then the grammar (F-POS-3).
    /// </summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out StoryPosition? position,
        [NotNullWhen(false)] out PositionSyntaxError? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        position = null;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            // F-POS-6 and F-POS-2 look only at the chapter part, the text after the first ':'.
            var chapterPart = text[(colon + 1)..];
            if (Keys.IsRowForm(chapterPart))
            {
                error = new PositionSyntaxError("F-POS-6", $"'{text}': {RowMessage}. To tie a fact to an outline row, use a row field.");
                return false;
            }

            if (chapterPart.Contains('.', StringComparison.Ordinal))
            {
                error = new PositionSyntaxError("F-POS-2", $"'{text}': {SceneMessage}.");
                return false;
            }
        }

        var bookKey = colon >= 0 ? text[..colon] : text;
        var chapterKey = colon >= 0 ? text[(colon + 1)..] : null;
        if (!Keys.IsContainerKey(bookKey) || (chapterKey is not null && !Keys.IsChapterKey(chapterKey)))
        {
            error = new PositionSyntaxError("F-POS-3", $"'{text}' is an {InvalidMessage}. {Hint(text, bookKey, chapterKey)}");
            return false;
        }

        position = new StoryPosition(bookKey, chapterKey);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => ChapterKey is null ? BookKey : $"{BookKey}:{ChapterKey}";

    private static string Hint(string text, string bookKey, string? chapterKey)
    {
        if (text.Length == 0)
        {
            return "A position is a book key, optionally followed by ':' and a chapter key, such as ember:14.";
        }

        if (text.Any(char.IsWhiteSpace))
        {
            return "A position has no spaces.";
        }

        if (chapterKey is not null && chapterKey.Contains(':', StringComparison.Ordinal))
        {
            return "A position has at most one ':'.";
        }

        if (!Keys.IsKey(bookKey) || (chapterKey is not null && !Keys.IsKey(chapterKey)))
        {
            return "Book and chapter keys are 1 to 64 lowercase letters and digits, with single hyphens between words.";
        }

        return "A book key starts with a letter.";
    }
}
