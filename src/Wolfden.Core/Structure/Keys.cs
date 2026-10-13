using System.Text.RegularExpressions;

namespace Wolfden.Core.Structure;

/// <summary>
/// Keys name universes, series, books, chapters, entity slugs, authors,
/// packs, and types (format.md §4.1).
/// </summary>
public static partial class Keys
{
    /// <summary>The longest a key can be (F-KEY-1).</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// True if <paramref name="text"/> is a key (F-KEY-1): 1 to 64 lowercase
    /// ASCII letters and digits, with single hyphens between words.
    /// </summary>
    public static bool IsKey(string text) =>
        text.Length is >= 1 and <= MaxLength && KeyPattern().IsMatch(text);

    /// <summary>
    /// True if <paramref name="text"/> is a valid universe, series, or book
    /// key: a key that starts with a letter (F-KEY-2).
    /// </summary>
    public static bool IsContainerKey(string text) => IsKey(text) && char.IsAsciiLetterLower(text[0]);

    /// <summary>
    /// True if <paramref name="text"/> is a valid chapter key: a key that
    /// isn't <c>r</c> followed by a digit, the form reserved for outline-row
    /// positions (F-KEY-2, F-POS-6).
    /// </summary>
    public static bool IsChapterKey(string text) => IsKey(text) && !IsRowForm(text);

    /// <summary>True if <paramref name="text"/> starts with <c>r</c> and a digit, the reserved row form (F-POS-6).</summary>
    internal static bool IsRowForm(string text) => text.Length >= 2 && text[0] == 'r' && char.IsAsciiDigit(text[1]);

    // \z, not $: $ would also match before a final newline.
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
