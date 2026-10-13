using Wolfden.Core.Documents;
using Wolfden.Core.Links;

namespace Wolfden.Core.Tests;

/// <summary>The link grammar (format.md §10.1) and where links are recognized (F-LINK-1 to F-LINK-3).</summary>
public class LinkSyntaxTests
{
    private static WikiLink One(string text) => Assert.Single(LinkSyntax.FindLinks(text));

    /// <summary>The links in an entity body, as (text, line, column).</summary>
    private static (string Text, int Line, int Column)[] BodyLinks(string body)
    {
        var file = MarkdownFile.Parse(TestFiles.Utf8("---\ntype: note\nname: N\n---\n" + body), requireFrontmatter: true);
        return BodyLinkScanner.ScanBody(file).Where(f => !f.Link.IsEmpty)
            .Select(f => (file.Text.Substring(f.Offset, f.Link.Length), f.Line, f.Column)).ToArray();
    }

    private static string[] BodyTargets(string body) =>
        BodyLinks(body).Select(l => LinkSyntax.FindLinks(l.Text).Single().Target).ToArray();

    [Theory]
    [InlineData("[[mara-voss]]", "mara-voss", null, null)]
    [InlineData("[[mara-voss|Voss]]", "mara-voss", null, "Voss")]
    [InlineData("[[mara-voss#Shoulder injury]]", "mara-voss", "Shoulder injury", null)]
    [InlineData("[[mara-voss#Shoulder injury|her wound]]", "mara-voss", "Shoulder injury", "her wound")]
    [InlineData("[[ Mara  Voss ]]", "Mara  Voss", null, null)]
    [InlineData("[[mara-voss # Appearance | Voss ]]", "mara-voss", "Appearance", "Voss")]
    [InlineData("[[mara-voss|a|b]]", "mara-voss", null, "a|b")]
    [InlineData("[[mara-voss|a # b]]", "mara-voss", null, "a # b")]
    [InlineData(@"[[mara-voss\|Voss]]", "mara-voss", null, "Voss")]
    [InlineData(@"[[mara-voss\|a\|b]]", "mara-voss", null, "a|b")]
    [InlineData("[[01JA7Q8M4XK3V9R2D5W8HN6TQB]]", "01JA7Q8M4XK3V9R2D5W8HN6TQB", null, null)]
    [InlineData("[[Zoë]]", "Zoë", null, null)]
    [InlineData("[[[mara-voss]]", "mara-voss", null, null)]
    [InlineData("[[a#b]]]", "a", "b", null)]
    public void TheGrammarSplitsTargetHeadingAndLabel(string text, string target, string? heading, string? label)
    {
        var link = One(text);

        Assert.Equal(target, link.Target);
        Assert.Equal(heading, link.Heading);
        Assert.Equal(label, link.Label);
    }

    [Theory]
    [InlineData("[[]]")]
    [InlineData("[[ |Voss]]")]
    [InlineData("[[   ]]")]
    [InlineData("[[#Appearance]]")]
    [InlineData(@"\[[mara-voss]]")]
    [InlineData("![[mara-voss]]")]
    [InlineData("![[map.png]]")]
    [InlineData("[[mara-voss#]]")]
    [InlineData("[[mara-voss|]]")]
    [InlineData("[[a#b#c]]")]
    [InlineData("[[mara\nvoss]]")]
    [InlineData("[[mara-voss|Vo\nss]]")]
    [InlineData("[[mara [x] voss]]")]
    [InlineData("[mara-voss]")]
    [InlineData("[[mara-voss]")]
    public void TheseAreNotLinks(string text)
    {
        Assert.Empty(LinkSyntax.FindLinks(text));
    }

    [Theory]
    [InlineData(@"\\[[mara-voss]]", 1)]
    [InlineData(@"\\\[[mara-voss]]", 0)]
    [InlineData(@"\![[mara-voss]]", 1)]
    [InlineData("[[a]] and [[b|c]] and [[d#e]]", 3)]
    [InlineData("[[a]][[b]]", 2)]
    public void BackslashesAndEmbedsAreCountedExactly(string text, int count)
    {
        Assert.Equal(count, LinkSyntax.FindLinks(text).Count);
    }

    [Fact]
    public void EmptyTargetsAreFoundForTheWarning()
    {
        // F-LINK-3: they're reported, so the scanner returns them marked empty.
        var raw = LinkSyntax.Scan("[[]] [[ |Voss]] [[ok]]", 0, 22, null).ToList();

        Assert.Equal([true, true, false], raw.Select(r => r.IsEmpty));
    }

    [Fact]
    public void LinesAndColumnsCountFromTheStartOfTheText()
    {
        var links = LinkSyntax.FindLinks("one\ntwo [[a]]\n  [[b]]");

        Assert.Equal([(2, 5), (3, 3)], links.Select(l => (l.Line, l.Column)));
        Assert.Equal("[[a]]", links[0].Text);
        Assert.Equal("a", links[0].DisplayText);
        Assert.Equal("Voss", One("[[mara-voss|Voss]]").DisplayText);
        Assert.Equal("mara-voss#Appearance", One("[[mara-voss#Appearance]]").DisplayText);
    }

    [Fact]
    public void BodyLinksHaveFileLinesAndColumns()
    {
        var links = BodyLinks("\nSee [[kessan-reach]].\n\n## Overview\n- [[mara-voss]] and [[Voss]]\n");

        Assert.Equal([("[[kessan-reach]]", 6, 5), ("[[mara-voss]]", 9, 3), ("[[Voss]]", 9, 21)], links);
    }

    [Theory]
    [InlineData("`[[code]]` and [[real]]")]
    [InlineData("``a [[code]] b`` [[real]]")]
    [InlineData("<!-- [[comment]] --> [[real]]")]
    [InlineData("<!--\n[[comment]]\n-->\n\n[[real]]")]
    [InlineData("<!-- wolfden-ish [[comment]]\nstill comment -->\n[[real]]")]
    [InlineData("```\n[[code]]\n```\n[[real]]")]
    [InlineData("~~~ md\n[[code]]\n~~~\n\n[[real]]")]
    [InlineData("    [[indented code]]\n\n[[real]]")]
    [InlineData("- item\n\n      [[indented code in a list]]\n\n[[real]]")]
    [InlineData("> ```\n> [[code in a quote]]\n> ```\n\n[[real]]")]
    [InlineData("<div>\n<!-- [[comment]] -->\n[[real]]\n</div>")]
    [InlineData("[[real]] `[[code`]]")]
    public void CodeAndCommentsHideLinks(string body)
    {
        Assert.Equal(["real"], BodyTargets(body));
    }

    [Theory]
    [InlineData("> [[quoted]]", "quoted")]
    [InlineData("- [[listed]]", "listed")]
    [InlineData("## Heading [[in heading]]", "in heading")]
    [InlineData("*[[emphasized]]*", "emphasized")]
    [InlineData("[link text](http://example.com) [[after]]", "after")]
    [InlineData("<div>[[in html]]</div>", "in html")]
    [InlineData("| a | [[cell\\|label]] |\n|---|---|", "cell")]
    public void LinksAreFoundAnywhereElse(string body, string target)
    {
        Assert.Equal([target], BodyTargets(body));
    }

    [Fact]
    public void AnUnclosedFenceHidesTheRestOfTheBody()
    {
        Assert.Empty(BodyTargets("```\n[[code]]\n\n## Not a heading\n[[still code]]\n"));
    }

    [Fact]
    public void OutlineCellsSplitAtUnescapedPipes()
    {
        var outline = MarkdownFile.Parse(TestFiles.Utf8(
            "# Outline\n\n[[notes]]\n\n| row | chapter | beat | status |\n|---|---|---|---|\n" +
            "| 1 | 2 | [[mara-voss\\|Voss]] wakes. | Candidate |\n" +
            "| 2 | | [[a|b]] splits the cell. | |\n" +
            "| x | 1 | [[bad-row]] | |\n" +
            "| 3 | 1 | `[[code]]` | |\n\nAfter [[after]].\n"));
        var links = BodyLinkScanner.ScanOutline(outline).Where(l => !l.Link.IsEmpty).ToList();

        Assert.Equal(["notes", "mara-voss", "bad-row", "after"], links.Select(l => l.Link.Target));
        Assert.Null(links[0].Row);
        Assert.Equal(new OutlineRowCells("1", "Candidate", "2", 7), links[1].Row);
        Assert.Equal("Voss", links[1].Link.Label);
        Assert.Null(links[2].Row);
        Assert.Null(links[3].Row);
    }

    [Theory]
    [InlineData("row", "row")]
    [InlineData(" POV ", "pov")]
    [InlineData("Row #", "row")]
    [InlineData("POV / Narrator", "pov_narrator")]
    [InlineData(" Draft-Status ", "draft_status")]
    [InlineData("---", "")]
    public void OutlineHeadersAreNormalized(string header, string key)
    {
        Assert.Equal(key, BodyLinkScanner.NormalizeHeader(header));
    }

    [Theory]
    [InlineData("37", true)]
    [InlineData("69a", true)]
    [InlineData("69ab", true)]
    [InlineData("0", false)]
    [InlineData("07", false)]
    [InlineData("a1", false)]
    [InlineData("69A", false)]
    [InlineData("", false)]
    public void RowIdsFollowTheGrammar(string text, bool valid)
    {
        Assert.Equal(valid, BodyLinkScanner.IsRowId(text));
    }
}
