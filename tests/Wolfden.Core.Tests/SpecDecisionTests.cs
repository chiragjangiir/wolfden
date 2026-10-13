using Wolfden.Core.Documents;

namespace Wolfden.Core.Tests;

/// <summary>
/// The four format.md clarifications decided on PR #78: F-SEC-1 (top-level
/// headings only), F-SEC-2 (deeper headings in the lead), F-YAML-2 (empty
/// frontmatter), and §9.2 (a section comment is indented at most three spaces).
/// </summary>
public class SpecDecisionTests
{
    private static MarkdownFile Md(string text) => MarkdownFile.Parse(TestFiles.Utf8(text));

    [Theory]
    [InlineData("> ## Aside\n")]
    [InlineData("- ## Item\n")]
    [InlineData("1. ## Item\n")]
    [InlineData("> - > ## Deep\n")]
    public void FSec1HeadingsInBlockQuotesAndListItemsAreSectionText(string nested)
    {
        var file = Md("## Overview\n" + nested + "After.\n");

        var section = Assert.Single(file.Sections);
        Assert.Equal("Overview", section.Name);
        Assert.Contains(nested.TrimEnd('\n'), section.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void FSec2DeeperHeadingsBeforeTheFirstLevel2AreLead()
    {
        var file = Md("# Title\n### Early\n<!-- wolfden: from=ember:1 -->\n#### Earlier\nText.\n## First\n");

        Assert.Equal(["First"], file.Sections.Select(s => s.Name));
        Assert.Contains("### Early", file.Lead, StringComparison.Ordinal);
        var d = Assert.Single(file.Diagnostics);
        Assert.Equal(("F-SEC-10", DiagnosticSeverity.Warning, 3), (d.RuleId, d.Severity, d.Line));
    }

    [Theory]
    [InlineData("---\n---\n")]
    [InlineData("---\n# just a comment\n\n---\n")]
    public void FYaml2EmptyFrontmatterIsAnEmptyMapping(string text)
    {
        var file = Md(text);

        Assert.True(file.HasFrontmatter);
        Assert.Null(file.Frontmatter);
        Assert.Empty(file.Diagnostics);
    }

    [Theory]
    [InlineData("<!-- wolfden: from=ember:19 -->")]
    [InlineData(" <!-- wolfden: from=ember:19 -->")]
    [InlineData("   <!-- wolfden: from=ember:19 -->")]
    public void SectionCommentMayBeIndentedUpToThreeSpaces(string line)
    {
        var file = Md($"## A\n{line}\nText.\n");

        Assert.Equal("ember:19", file.Sections[0].Comment?["from"]);
        Assert.Empty(file.Diagnostics);
    }

    [Theory]
    [InlineData("## A\n    <!-- wolfden: from=ember:19 -->\n")]
    [InlineData("## A\n\n    <!-- wolfden: from=ember:19 -->\n")]
    [InlineData("## A\n\t<!-- wolfden: from=ember:19 -->\n")]
    public void SectionCommentIndentedFourOrMoreIsCode(string text)
    {
        var file = Md(text);

        Assert.Null(file.Sections[0].Comment);
        Assert.Empty(file.Diagnostics);
        Assert.Contains("<!-- wolfden:", file.Sections[0].Body, StringComparison.Ordinal);
    }
}
