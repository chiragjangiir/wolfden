using Wolfden.Core.Entities;
using Wolfden.Core.Links;

namespace Wolfden.Core.Tests;

/// <summary>Link text normalization (format.md F-LINK-4) and bookkeeping patterns (F-SEC-12).</summary>
public class LinkTextTests
{
    [Theory]
    [InlineData("Mara Voss", "mara voss")]
    [InlineData("  MARA \t  voss\n", "mara voss")]
    [InlineData("mara-voss", "mara-voss")]
    [InlineData("Straße", "strasse")]
    [InlineData("STRASSE", "strasse")]
    [InlineData("ΣΊΣΥΦΟΣ", "σίσυφοσ")]
    [InlineData("σίσυφος", "σίσυφοσ")]
    [InlineData("Zoë", "zoë")]
    [InlineData("Ｍａｒａ", "ｍａｒａ")]
    [InlineData("a b", "a b")]
    public void TextIsFoldedTrimmedAndCollapsed(string text, string normalized)
    {
        Assert.Equal(normalized, LinkText.Normalize(text));
    }

    [Theory]
    [InlineData("Mara Voss", "mara voss", true)]
    [InlineData("Mara Voss", "mara-voss", false)]
    [InlineData("Zoë", "ZOË", true)]
    public void MatchingIsCaseInsensitiveButKeepsHyphens(string a, string b, bool match)
    {
        Assert.Equal(match, LinkText.Matches(a, b));
    }

    [Theory]
    [InlineData("Change log", true)]
    [InlineData("Changelog 2026", true)]
    [InlineData("**Change log**", true)]
    [InlineData("v12 session notes (merged)", true)]
    [InlineData("Session Notes", true)]
    [InlineData("Revision history 2026", true)]
    [InlineData("Revision history: draft 3", false)]
    [InlineData("`Changelog`", true)]
    [InlineData("Session", false)]
    [InlineData("Change logistics", false)]
    [InlineData("Possession notes", false)]
    [InlineData("Overview", false)]
    public void BuiltInBookkeepingPatternsMatchAsTheSpecSays(string name, bool bookkeeping)
    {
        Assert.Equal(bookkeeping, new Bookkeeping().MatchesName(name));
    }

    [Theory]
    [InlineData("Scratch", true)]
    [InlineData("scratch", true)]
    [InlineData("Scratchpad", false)]
    [InlineData("Ash working notes", true)]
    [InlineData("working notes", false)]
    public void DenPatternsAddToTheBuiltInOnes(string name, bool bookkeeping)
    {
        Assert.Equal(bookkeeping, new Bookkeeping(["Scratch", "* working notes"]).MatchesName(name));
    }
}
