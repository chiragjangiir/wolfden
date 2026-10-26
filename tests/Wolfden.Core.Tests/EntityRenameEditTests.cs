using Wolfden.Core.Documents;
using Wolfden.Core.Links;

namespace Wolfden.Core.Tests;

/// <summary>
/// The one-file edits renaming an entity needs (#150, format.md F-RT-5):
/// a link's target replaced with everything else kept, and the display
/// title changed with nothing else.
/// </summary>
public class EntityRenameEditTests
{
    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void ALinksTargetIsReplacedKeepingItsSpacingHeadingAndLabel()
    {
        Assert.Equal("[[Old Swales]]", LinkSyntax.WithTarget("[[Mr. Swales]]", "Old Swales"));
        Assert.Equal("[[ Old Swales # voice |how he talks]]", LinkSyntax.WithTarget("[[ mr.  swales # voice |how he talks]]", "Old Swales"));
        Assert.Equal("[[Old Swales\\|Swales]]", LinkSyntax.WithTarget("[[Mr. Swales\\|Swales]]", "Old Swales"));
        Assert.Null(LinkSyntax.WithTarget("[[ |label]]", "x"));
        Assert.Null(LinkSyntax.WithTarget("not a link", "x"));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void TheTitleChangesAndNothingElse()
    {
        var file = MarkdownFile.Parse(TestFiles.Utf8("---\nname: Mara Voss\n---\n\n# Mara Voss ##\n\nLead.\n\n## Arc\nText.\n"));

        var renamed = file.WithTitle("Mara Ellery");

        Assert.Equal("---\nname: Mara Voss\n---\n\n# Mara Ellery ##\n\nLead.\n\n## Arc\nText.\n", renamed.Text);
        Assert.Equal("Mara Ellery", renamed.Title);
        Assert.Same(renamed, renamed.WithTitle("Mara Ellery"));
        Assert.Throws<ArgumentException>(() => file.WithTitle("Two\nlines"));
        Assert.Throws<InvalidOperationException>(() => MarkdownFile.Parse(TestFiles.Utf8("---\nname: X\n---\n\nNo title.\n")).WithTitle("X"));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-26")]
    public void ASetextTitleOrOneWithACommentKeepsItsForm()
    {
        var setext = MarkdownFile.Parse(TestFiles.Utf8("---\nname: Mara Voss\n---\n\nMara Voss\n=========\n\nLead.\n"));
        var renamed = setext.WithTitle("Mara Ellery");
        Assert.Equal("---\nname: Mara Voss\n---\n\nMara Ellery\n=========\n\nLead.\n", renamed.Text);
        Assert.Equal("Mara Ellery", renamed.Title);

        var commented = MarkdownFile.Parse(TestFiles.Utf8("---\nname: Mara Voss\n---\n\n# Mara Voss <!-- working title -->\n\n## Arc\nText.\n"));
        Assert.Equal("Mara Voss", commented.Title);
        Assert.Equal(
            "---\nname: Mara Voss\n---\n\n# Mara Ellery <!-- working title -->\n\n## Arc\nText.\n",
            commented.WithTitle("Mara Ellery").Text);
    }
}
