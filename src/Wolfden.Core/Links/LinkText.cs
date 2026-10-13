using System.Text;

namespace Wolfden.Core.Links;

/// <summary>
/// The normalization that link targets, slugs, names, aliases, headings, and
/// section names are compared under (format.md F-LINK-4).
/// </summary>
public static class LinkText
{
    /// <summary>
    /// Normalizes <paramref name="text"/> for comparison: Unicode NFC
    /// (F-ENC-5), trimmed, each run of whitespace collapsed to one space, then
    /// case folded. Hyphens and spaces stay distinct, so <c>mara-voss</c> and
    /// <c>mara voss</c> don't match.
    /// </summary>
    /// <remarks>
    /// .NET has no Unicode default case folding, so this uses the
    /// invariant simple case mappings (upper, then lower), which agree with
    /// simple case folding for nearly every character, plus the common
    /// full foldings that expand to more than one character (<c>ß</c> to
    /// <c>ss</c>, the Latin ligatures).
    /// </remarks>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var nfc = text.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(nfc.Length);
        var pendingSpace = false;
        foreach (var rune in nfc.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            AppendFolded(sb, rune);
        }

        return sb.ToString();
    }

    /// <summary>True if the two texts match under <see cref="Normalize"/>.</summary>
    public static bool Matches(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static void AppendFolded(StringBuilder sb, Rune rune)
    {
        var expansion = rune.Value switch
        {
            0x00DF or 0x1E9E => "ss",
            0x0130 => "i̇",
            0x0149 => "ʼn",
            0xFB00 => "ff",
            0xFB01 => "fi",
            0xFB02 => "fl",
            0xFB03 => "ffi",
            0xFB04 => "ffl",
            0xFB05 or 0xFB06 => "st",
            _ => null,
        };

        if (expansion is not null)
        {
            sb.Append(expansion);
            return;
        }

        sb.Append(Rune.ToLowerInvariant(Rune.ToUpperInvariant(rune)).ToString());
    }
}
