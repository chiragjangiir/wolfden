using Wolfden.Core.Documents;

namespace Wolfden.Core.Tests;

/// <summary>format.md §9.1 and §9.2: sections and section comments.</summary>
public class SectionTests
{
    private static MarkdownFile Md(string text) => MarkdownFile.Parse(TestFiles.Utf8(text));

    [Fact]
    public void SplitsTheOddFixtureIntoSections()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("odd-yaml-and-sections.md"));

        Assert.Equal("Kessan Reach", file.Title);
        Assert.StartsWith("Text right after the frontmatter", file.Lead, StringComparison.Ordinal);
        Assert.EndsWith("============\n\n", file.Lead, StringComparison.Ordinal);
        Assert.Equal(
            [
                ("Overview", 2, 37),
                ("Docking rings", 2, 56),
                ("Custom: Things the miners never say aloud", 2, 62),
                ("Deep\tshafts", 3, 65),
                ("Fourth level", 4, 68),
                ("A stray level-1 heading", 2, 71),
                ("Trailing spaces", 2, 75),
            ],
            file.Sections.Select(s => (s.Name, s.Level, s.Line)));

        var overview = file.Sections[0];
        Assert.Equal("canon", overview.Comment?["status"]);
        Assert.Equal("true", overview.Comment?["pin"]);
        Assert.Contains("## Not a heading: inside a fenced code block", overview.Body, StringComparison.Ordinal);
        Assert.Contains("## Inside an HTML block, not a heading", overview.Body, StringComparison.Ordinal);
        Assert.Contains("    ## Indented code, not a heading", overview.Body, StringComparison.Ordinal);

        var docking = file.Sections[1];
        Assert.True(docking.IsSetext);
        Assert.Equal("ember:4", docking.Comment?["from"]);
        Assert.True(docking.Comment!.Attributes[0].IsQuoted);
        Assert.Equal("false", docking.Comment["canon"]);
        Assert.StartsWith("A setext section", docking.Body, StringComparison.Ordinal);

        var custom = file.Sections[2];
        Assert.Equal(["Deep\tshafts"], custom.Subsections.Select(s => s.Name));
        Assert.Same(custom, file.Sections[3].Parent);
        Assert.Same(file.Sections[3], file.Sections[4].Parent);
        Assert.Contains("#### Fourth level", custom.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("### Deep", custom.Body, StringComparison.Ordinal);

        var stray = file.Sections[5];
        Assert.Equal(1, stray.HeadingLevel);
        Assert.Null(stray.Parent);

        var warning = Assert.Single(file.Diagnostics);
        Assert.Equal(("F-SEC-2", DiagnosticSeverity.Warning, 71), (warning.RuleId, warning.Severity, warning.Line));
    }

    [Fact]
    public void LeadIsEverythingBeforeTheFirstLevel2Heading()
    {
        var file = Md("---\nname: x\n---\nIntro.\n\n# Title\n\n### Early h3\nStill lead.\n\n## First\nBody.\n");

        Assert.Equal("Title", file.Title);
        Assert.Equal("Intro.\n\n# Title\n\n### Early h3\nStill lead.\n\n", file.Lead);
        Assert.Equal(["First"], file.Sections.Select(s => s.Name));
    }

    [Fact]
    public void FileWithNoFrontmatterStillHasSections()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("no-frontmatter.md"));

        Assert.False(file.HasFrontmatter);
        Assert.Equal("Notes from Halden Pike", file.Title);
        Assert.Equal(["Ember", "Kessan Reach"], file.Sections.Select(s => s.Name));
        Assert.Equal("The first book.\n\n", file.Sections[0].Body);
    }

    [Theory]
    [InlineData("## Overview", "Overview")]
    [InlineData("##   Overview   ", "Overview")]
    [InlineData("## Overview ##", "Overview")]
    [InlineData("## Overview #####   ", "Overview")]
    [InlineData("## C#", "C#")]
    [InlineData("## Issue \\#", "Issue \\#")]
    [InlineData("## *Emphasis* and `code`", "*Emphasis* and `code`")]
    [InlineData("## Overview <!-- note -->", "Overview")]
    [InlineData("## Overview <!-- a --> <!-- b -->", "Overview")]
    [InlineData("## Overview <!-- a --> ##", "Overview")]
    [InlineData("## `<!-- in code -->`", "`<!-- in code -->`")]
    [InlineData("   ## Indented three", "Indented three")]
    [InlineData("##", "")]
    public void SectionNamesFollowFSec3(string heading, string name)
    {
        var file = Md($"{heading}\nText.\n");

        Assert.Equal(name, Assert.Single(file.Sections).Name);
    }

    [Fact]
    public void MultiLineSetextHeadingNameIsJoinedWithSpaces()
    {
        var file = Md("## First\n\nThe long\n  dark\n---\nText.\n");

        Assert.Equal(["First", "The long dark"], file.Sections.Select(s => s.Name));
        Assert.Equal(3, file.Sections[1].Line);
        Assert.Equal("The long\n  dark\n---\n", file.Sections[1].HeadingText);
    }

    [Fact]
    public void HeadingsInsideContainersAndCodeDoNotSplitSections()
    {
        var file = Md("""
            ## Only
            > ## In a block quote
            - ## In a list item
            ````
            ```
            ## In a nested fence
            ```
            ````
            <!--
            ## In a multi-line HTML comment
            -->
            """);

        Assert.Equal(["Only"], file.Sections.Select(s => s.Name));
    }

    [Fact]
    public void UnclosedFenceRunsToTheEndOfTheFile()
    {
        var file = Md("## A\n```\n## B\n");

        Assert.Equal(["A"], file.Sections.Select(s => s.Name));
    }

    [Fact]
    public void SubsectionTreeAndSpans()
    {
        var text = "## A\na\n### A1\na1\n#### A1x\nx\n### A2\na2\n## B\nb\n";
        var file = Md(text);

        var (a, a1, a1x, a2, b) = (file.Sections[0], file.Sections[1], file.Sections[2], file.Sections[3], file.Sections[4]);
        Assert.Equal([a1, a2], a.Subsections);
        Assert.Equal([a1x], a1.Subsections);
        Assert.Equal("a\n", a.Body);
        Assert.Equal("## A\na\n### A1\na1\n#### A1x\nx\n### A2\na2\n", a.Text);
        Assert.Equal("### A1\na1\n#### A1x\nx\n", a1.Text);
        Assert.Equal("## B\nb\n", b.Text);
        Assert.Equal([a, b], file.TopLevelSections);
    }

    [Theory]
    [InlineData("<!-- wolfden: from=ember:19 -->", "from", "ember:19")]
    [InlineData("<!-- wolfden: from=\"ember:19\" until=ember:30 -->", "until", "ember:30")]
    [InlineData("<!--wolfden: until=ash:1 -->", "until", "ash:1")]
    [InlineData("<!-- wolfden: from=ember:19-->", "from", "ember:19")]
    [InlineData("  <!-- wolfden: from=a-quiet-moon:2b -->  ", "from", "a-quiet-moon:2b")]
    [InlineData("<!-- wolfden:\tpov=\"Mara Voss\"\tfrom=ember:1 -->", "pov", "Mara Voss")]
    [InlineData("<!-- wolfden: pov=Zoë -->", "pov", "Zoë")]
    [InlineData("<!-- wolfden: canon=false -->", "canon", "false")]
    [InlineData("<!-- wolfden: status=undecided pin=true -->", "status", "undecided")]
    public void WellFormedSectionCommentsAreParsed(string comment, string attribute, string value)
    {
        var file = Md($"## Shoulder injury\n{comment}\nText.\n");

        var section = Assert.Single(file.Sections);
        Assert.NotNull(section.Comment);
        Assert.Equal(value, section.Comment[attribute]);
        Assert.Equal(2, section.Comment.Line);
        Assert.Equal(comment, section.Comment.Text);
        Assert.Equal("Text.\n", section.Body);
        Assert.DoesNotContain(file.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("<!-- wolfden: from=ember:19 from=ember:20 -->", "F-SEC-7", DiagnosticSeverity.Error)]
    [InlineData("<!-- wolfden: status=maybe -->", "F-STAT-1", DiagnosticSeverity.Error)]
    [InlineData("<!-- wolfden: pin=yes -->", "F-PIN-1", DiagnosticSeverity.Error)]
    [InlineData("<!-- wolfden: canon=maybe -->", "F-SEC-7", DiagnosticSeverity.Error)]
    [InlineData("<!-- wolfden: pov=mara -->", "F-SEC-7", DiagnosticSeverity.Warning)]
    public void BadValuesInAWellFormedCommentAreReported(string comment, string rule, DiagnosticSeverity severity)
    {
        var file = Md($"## Shoulder injury\n{comment}\nText.\n");

        Assert.NotNull(file.Sections[0].Comment);
        var d = Assert.Single(file.Diagnostics);
        Assert.Equal((rule, severity, 2), (d.RuleId, d.Severity, d.Line));
    }

    [Theory]
    [InlineData("<!-- wolfden: -->")]
    [InlineData("<!-- wolfden: from=ember:19--> -->")]
    [InlineData("<!-- wolfden: from=ember:19 --> trailing")]
    [InlineData("<!-- wolfden: from=ember 19 -->")]
    [InlineData("<!-- wolfden: From=ember:19 -->")]
    [InlineData("<!-- wolfden: from=\"ember:19 -->")]
    public void MalformedCommentInTheRightPlaceIsAWarningAndIgnored(string comment)
    {
        var file = Md($"## Shoulder injury\n{comment}\nText.\n");

        Assert.Null(file.Sections[0].Comment);
        var d = Assert.Single(file.Diagnostics);
        Assert.Equal(("F-SEC-10", DiagnosticSeverity.Warning, 2), (d.RuleId, d.Severity, d.Line));
        Assert.Contains(comment.Trim(), file.Sections[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void UnclosedCommentNamesTheLineWhereItsHtmlBlockEnds()
    {
        var file = Md("## Shoulder injury\n<!-- wolfden: from=ember:19\n## Hidden\nText.\n-->\n## Next\n");

        Assert.Equal(["Shoulder injury", "Next"], file.Sections.Select(s => s.Name));
        var d = Assert.Single(file.Diagnostics);
        Assert.Equal(("F-SEC-10", 2), (d.RuleId, d.Line));
        Assert.Contains("line 5", d.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n", true)]
    [InlineData(" \t\n", true)]
    [InlineData("\n\n", false)]
    public void OneBlankLineIsAllowedBeforeTheComment(string blank, bool valid)
    {
        var file = Md($"## Shoulder injury\n{blank}<!-- wolfden: from=ember:19 -->\nText.\n");

        Assert.Equal(valid, file.Sections[0].Comment is not null);
        Assert.Equal(valid, file.Diagnostics.Count == 0);
        if (!valid)
        {
            Assert.Equal("F-SEC-10", Assert.Single(file.Diagnostics).RuleId);
        }
    }

    [Fact]
    public void CommentAfterSetextUnderlineAndStrayLevel1Counts()
    {
        var file = Md("## A\n\nSetext\n------\n<!-- wolfden: from=ember:2 -->\n\n# Stray\n<!-- wolfden: from=ember:3 -->\n");

        Assert.Equal(["ember:2", "ember:3"], file.Sections.Skip(1).Select(s => s.Comment?["from"]));
        Assert.Equal(["F-SEC-2"], file.Diagnostics.Select(d => d.RuleId));
    }

    [Fact]
    public void CommentIndentedAsCodeIsNotAComment()
    {
        var file = Md("## A\n\n    <!-- wolfden: from=ember:19 -->\n");

        Assert.Null(file.Sections[0].Comment);
        Assert.Empty(file.Diagnostics);
    }

    [Fact]
    public void MisplacedWolfdenCommentsAreWarnings()
    {
        var file = Md("""
            <!-- wolfden: from=ember:1 -->
            Lead.

            ## Shoulder injury <!-- wolfden: from=ember:19 -->
            Text with <!-- wolfden: from=ember:2 --> inline.

            <!-- wolfden: from=ember:3 -->

            > <!-- wolfden: from=ember:4 -->

            `<!-- wolfden: in a code span -->`

            ```
            <!-- wolfden: in a code block -->
            ```

            <!-- todo: an ordinary comment is fine -->
            """);

        Assert.Null(file.Sections[0].Comment);
        Assert.Equal(
            [1, 4, 5, 7, 9],
            file.Diagnostics.Select(d => d.Line ?? 0));
        Assert.All(file.Diagnostics, d => Assert.Equal(("F-SEC-10", DiagnosticSeverity.Warning), (d.RuleId, d.Severity)));
        Assert.Contains("Move it to the line under the heading", file.Diagnostics[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryHtmlCommentUnderAHeadingIsBodyText()
    {
        var file = Md("## Open questions\n<!-- wolfden: status=undecided -->\n<!-- What isn't decided yet? -->\n");

        var section = Assert.Single(file.Sections);
        Assert.Equal("undecided", section.Comment?["status"]);
        Assert.Equal("<!-- What isn't decided yet? -->\n", section.Body);
        Assert.Empty(file.Diagnostics);
    }

    [Fact]
    public void AttributesKeepOrderAndUnknownOnes()
    {
        var file = Md("## A\n<!-- wolfden: x-mood=grim from=ember:1 pin=false -->\n");

        Assert.Equal(
            [new SectionAttribute("x-mood", "grim", false), new SectionAttribute("from", "ember:1", false), new SectionAttribute("pin", "false", false)],
            file.Sections[0].Comment!.Attributes);
    }
}
