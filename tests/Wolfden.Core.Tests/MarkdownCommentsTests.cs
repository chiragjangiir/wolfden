using Wolfden.Core.Documents;

namespace Wolfden.Core.Tests;

/// <summary>Separating HTML comments (F-SEC-11) from the text around them.</summary>
public class MarkdownCommentsTests
{
    [Fact]
    public void TextWithoutCommentsIsUnchanged()
    {
        Assert.Equal(("Plain *text*.\n", ""), MarkdownComments.Split("Plain *text*.\n"));
    }

    [Fact]
    public void InlineAndBlockCommentsAreSplitOut()
    {
        var (text, comments) = MarkdownComments.Split("Keeper. <!-- todo: harbour -->\n\n<!--\nsecond\n-->\nAfter.\n");

        Assert.DoesNotContain("harbour", text, StringComparison.Ordinal);
        Assert.DoesNotContain("second", text, StringComparison.Ordinal);
        Assert.Contains("Keeper.", text, StringComparison.Ordinal);
        Assert.Contains("After.", text, StringComparison.Ordinal);
        Assert.Equal("todo: harbour\nsecond", comments);
    }

    [Fact]
    public void CommentsInCodeAreText()
    {
        var markdown = "Use `<!-- x -->` here.\n\n```\n<!-- y -->\n```\n";

        Assert.Equal((markdown, ""), MarkdownComments.Split(markdown));
    }

    [Fact]
    public void AnUnclosedCommentRunsToTheEndOfItsBlock()
    {
        var (text, comments) = MarkdownComments.Split("Before.\n\n<!-- open\nstill open\n");

        Assert.Contains("Before.", text, StringComparison.Ordinal);
        Assert.Equal("open\nstill open", comments);
    }
}
