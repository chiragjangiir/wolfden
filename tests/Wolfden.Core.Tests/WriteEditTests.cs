using System.Text;
using Wolfden.Core.Documents;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Tests;

/// <summary>The edits the MCP write tools (#21) add to the document API: whole values, placed sections, and lead text.</summary>
public sealed class WriteEditTests
{
    private const string Note = "---\nid: 01JA7Q8M4XK3V9R2D5W8HN6TQB\ntype: character\nname: Mara Voss\nstatus: canon\nfields:\n  born: \"2291\"\n---\n\n# Mara Voss\n\nLead.\n\n## Overview\nXO.\n\n## Voice\nCurt.\n";

    private static MarkdownFile Parse(string text) => MarkdownFile.Parse(Encoding.UTF8.GetBytes(text), requireFrontmatter: true);

    [Fact]
    [Trait("Scenario", "SC-MCP-16")]
    public void AWholeTemporalListIsWrittenOneEntryPerLine()
    {
        var rank = new YamlSequenceValue
        {
            new YamlMappingValue { { "value", YamlScalarValue.String("Lieutenant") }, { "from", YamlScalarValue.String("ember:1") } },
            new YamlMappingValue { { "value", YamlScalarValue.String("Commander") }, { "from", YamlScalarValue.String("ember:22") } },
        };

        var edited = Parse(Note).WithFrontmatterValue(["fields", "rank"], rank);

        Assert.Contains("  born: \"2291\"\n  rank:\n    - { value: Lieutenant, from: \"ember:1\" }\n    - { value: Commander, from: \"ember:22\" }\n---", edited.Text, StringComparison.Ordinal);

        // A list replaces a list, and a mapping goes on the key's line.
        var replaced = edited.WithFrontmatterValue(["fields", "rank"], new YamlSequenceValue { YamlScalarValue.String("Captain") });
        Assert.Contains("  rank: [Captain]\n---", replaced.Text, StringComparison.Ordinal);
        var died = replaced.WithFrontmatterValue(["fields", "died"], new YamlMappingValue { { "value", YamlScalarValue.String("2330") }, { "from", YamlScalarValue.String("ash:9") } });
        Assert.Contains("  died: { value: \"2330\", from: \"ash:9\" }\n---", died.Text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-16")]
    public void InTemplateOrderANewTopLevelKeyGoesInItsPlace()
    {
        var file = Parse(Note);

        var aliases = file.WithNewListItem(["aliases"], YamlScalarValue.String("Voss"), inTemplateOrder: true);
        var from = aliases.WithFrontmatterValue(["from"], YamlScalarValue.String("ember:3"), inTemplateOrder: true);
        var extra = from.WithFrontmatterValue(["x-color"], YamlScalarValue.String("teal"), inTemplateOrder: true);

        Assert.StartsWith(
            "---\nid: 01JA7Q8M4XK3V9R2D5W8HN6TQB\ntype: character\nname: Mara Voss\naliases: [Voss]\nstatus: canon\nfrom: \"ember:3\"\nfields:\n  born: \"2291\"\nx-color: teal\n---\n",
            extra.Text,
            StringComparison.Ordinal);

        // Without it, a new key goes after the existing ones (F-RT-4).
        Assert.Contains("  born: \"2291\"\naliases: [Voss]\n---", file.WithNewListItem(["aliases"], YamlScalarValue.String("Voss")).Text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-17")]
    public void ASectionGoesBeforeAnotherWithItsComment()
    {
        var file = Parse(Note);
        var voice = file.Sections.Single(s => s.Name == "Voice");

        var edited = file.WithNewSection("Appearance", "Tall.", 2, voice, "from=ember:19");

        Assert.Contains("## Overview\nXO.\n\n## Appearance\n<!-- wolfden: from=ember:19 -->\nTall.\n\n## Voice\nCurt.\n", edited.Text, StringComparison.Ordinal);
        Assert.Equal("ember:19", edited.Sections.Single(s => s.Name == "Appearance").Comment!["from"]);

        // A subsection lands under the section before it.
        var sub = edited.WithNewSection("Scar", "Left arm.", 3, edited.Sections.Single(s => s.Name == "Voice"));
        Assert.Equal("Appearance", sub.Sections.Single(s => s.Name == "Scar").Parent!.Name);
        Assert.Throws<ArgumentException>(() => file.WithNewSection("Bad", "x", 2, null, "from=a -->"));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-18")]
    public void TextGoesAtTheEndOfTheLead()
    {
        var edited = Parse(Note).WithTextAddedToLead("More lead.");

        Assert.Contains("# Mara Voss\n\nLead.\n\nMore lead.\n\n## Overview\nXO.", edited.Text, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Parse(Note).WithTextAddedToLead("## Sneaky"));

        var noSections = Parse("---\nname: x\n---\n\n# X\n<!-- prompt -->\n").WithTextAddedToLead("Idea.");
        Assert.EndsWith("# X\n<!-- prompt -->\n\nIdea.\n", noSections.Text, StringComparison.Ordinal);
    }
}
