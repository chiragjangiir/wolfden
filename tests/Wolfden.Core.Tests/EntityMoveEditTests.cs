using Wolfden.Core.Editing;
using Wolfden.Core.Links;

namespace Wolfden.Core.Tests;

/// <summary>
/// The pieces of moving an entity to another scope (#89, format.md F-RT-5):
/// a link rewritten to an id that shows what it showed, and a den overlay
/// with the moved file's old path gone.
/// </summary>
public class EntityMoveEditTests
{
    private const string Id = "01JA7Q8M4XK3V9R2D5W8HN6TQE";

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void ALinkRewrittenToAnIdShowsTheSameText()
    {
        Assert.Equal($"[[{Id}|Tessaly]]", LinkSyntax.WithIdTarget("[[Tessaly]]", Id, inTableRow: false));
        Assert.Equal($"[[{Id}#Crew|Tessaly#Crew]]", LinkSyntax.WithIdTarget("[[Tessaly#Crew]]", Id, inTableRow: false));
        Assert.Equal($"[[ {Id} |her]]", LinkSyntax.WithIdTarget("[[ the  Tessaly |her]]", Id, inTableRow: false));
        Assert.Equal($"[[{Id}\\|Tessaly]]", LinkSyntax.WithIdTarget("[[Tessaly]]", Id, inTableRow: true));
        Assert.Equal($"[[{Id}\\|her]]", LinkSyntax.WithIdTarget("[[Tessaly\\|her]]", Id, inTableRow: true));
        Assert.Null(LinkSyntax.WithIdTarget("[[ |label]]", Id, inTableRow: false));
        Assert.Null(LinkSyntax.WithIdTarget("not a link", Id, inTableRow: false));
    }

    [Theory]
    [Trait("Scenario", "SC-MCP-28")]
    [InlineData("| a | [[X]] |\n", true)]
    [InlineData("  | a | [[X]] |\n", true)]
    [InlineData("a | b\n--- | ---\nc | [[X]]\n", true)]
    [InlineData("[[X]] | b\n:--|--:\n", true)]
    [InlineData("> | a | [[X]] |\n", true)]
    [InlineData("> a | b\n> --- | ---\n> c | [[X]]\n", true)]
    [InlineData("See [[X]].\n", false)]
    [InlineData("See [[Y|y]] and [[X]].\n", false)]
    [InlineData("a | b\n\nc | [[X]]\n--- | ---\n", true)]
    [InlineData("a | b\n--- | ---\n\nc | [[X]]\n", false)]
    [InlineData("See [[X]] | a\nh1 | h2\n--- | ---\nc | d\n", false)]
    [InlineData("See [[X]] | a\n```\n-|-\n```\n", false)]
    [InlineData("See [[X]] | a\n<!-- -|- -->\n", false)]
    [InlineData("```\n-|-\n```\nSee [[X]] | a\n", false)]
    [InlineData("<!--\n-|-\n-->\nSee [[X]] | a\n", false)]
    [InlineData("- a | b\n  --- | ---\n  c | [[X]]\n", true)]
    [InlineData("- [[X]] | b\n  --- | ---\n", true)]
    [InlineData("a | b\r\n--- | ---\r\nc | [[X]]\r\n", true)]
    public void ATableRowIsFoundWithOrWithoutItsOuterPipes(string text, bool inTable)
    {
        Assert.Equal(inTable, LinkRewriter.InTableRow(text, text.IndexOf("[[X]]", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-28")]
    public void AnOverlayCanLeaveAFileOut()
    {
        var den = new MemoryDenSource().With("a/old.md", "old").With("a/kept.md", "kept");

        var overlay = new OverlayDenSource(den, new Dictionary<string, byte[]> { ["b/new.md"] = TestFiles.Utf8("new") }, ["a/old.md"]);

        Assert.False(overlay.FileExists("a/old.md"));
        Assert.Null(overlay.TryReadFile("a/old.md"));
        Assert.Null(overlay.TryReadListedFile("a/old.md"));
        Assert.Throws<FileNotFoundException>(() => overlay.ReadFile("a/old.md"));
        Assert.Equal(["kept.md"], overlay.GetFiles("a"));
        Assert.Equal(["new.md"], overlay.GetFiles("b"));
        Assert.True(overlay.FileExists("a/kept.md"));
    }
}
