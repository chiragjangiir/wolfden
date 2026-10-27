using Wolfden.Core.Documents;

namespace Wolfden.Core.Tests;

/// <summary>
/// format.md F-RT-1, F-RT-2: replacing the lead (#28) changes only the
/// lead's text, keeping the frontmatter, the blank lines around the lead,
/// and every section.
/// </summary>
public class LeadEditTests
{
    private static MarkdownFile Md(string text) => MarkdownFile.Parse(TestFiles.Utf8(text));

    [Fact]
    public void ReplacingTheLeadKeepsTheSpacingAndSections()
    {
        var file = Md("---\nname: Mara\n---\n\n\n# Mara\n\nOld text.\n\n\n## Notes\nLast.\n");

        var result = file.WithLead("# Mara\n\nNew text.\nTwo lines.\n\n");

        Assert.Equal("---\nname: Mara\n---\n\n\n# Mara\n\nNew text.\nTwo lines.\n\n\n## Notes\nLast.\n", result.Text);
    }

    [Fact]
    public void LineBreaksBecomeTheFilesOwn()
    {
        var file = Md("---\r\nname: Mara\r\n---\r\n\r\nOld.\r\n\r\n## Notes\r\nLast.\r\n");

        Assert.Equal("---\r\nname: Mara\r\n---\r\n\r\nNew.\r\nMore.\r\n\r\n## Notes\r\nLast.\r\n", file.WithLead("New.\nMore.").Text);
    }

    [Fact]
    public void AnEmptyLeadGetsTheText()
    {
        var file = Md("---\nname: Mara\n---\n## Notes\nLast.\n");

        Assert.Equal("---\nname: Mara\n---\nOpening.\n\n## Notes\nLast.\n", file.WithLead("Opening.").Text);
    }

    [Fact]
    public void ALeadCanBeCleared()
    {
        var file = Md("---\nname: Mara\n---\n\nOld.\n\n## Notes\nLast.\n");

        Assert.Equal("---\nname: Mara\n---\n\n\n## Notes\nLast.\n", file.WithLead("").Text);
    }

    [Fact]
    public void ALeadWithoutSectionsOrAFinalNewlineKeepsNone()
    {
        var file = Md("---\nname: Mara\n---\n\nOld.");

        Assert.Equal("---\nname: Mara\n---\n\nNew.", file.WithLead("New.").Text);
    }

    [Theory]
    [InlineData("Text.\n\n## Sneaky\nIn the lead.")]
    [InlineData("Text.\n\n```\nunclosed fence")]
    [InlineData("Setext\n---")]
    public void TextThatWouldChangeTheSectionsIsRefused(string text)
    {
        var file = Md("---\nname: Mara\n---\n\nOld.\n\n## Notes\nLast.\n");

        Assert.Throws<ArgumentException>(() => file.WithLead(text));
    }
}
