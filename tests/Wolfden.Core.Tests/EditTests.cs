using Wolfden.Core.Documents;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Tests;

/// <summary>
/// format.md F-RT-1, F-RT-2, F-RT-4: edits change only the bytes of what
/// they touch. Each test spells out the whole expected file.
/// </summary>
public class EditTests
{
    private const string Entity = """
        ---
        # Mara's notes
        id: 01JA7Q8M4XK3V9R2D5W8HN6TQB
        type: character
        name: Mara Voss   # display name
        aliases: [Voss, "the Commander"]
        status: 'draft'
        fields:
          rank:
            - { value: Lieutenant, from: "ember:1" }
            - { value: Commander, from: "ember:22" }  # promoted
          born: "2291"
          died:
        x-color: teal
        ---

        # Mara Voss

        ## Appearance
        Silver-streaked hair.

        ## Shoulder injury
        <!-- wolfden: from=ember:19 -->
        Takes a flechette round.

        ### Recovery
        Months of therapy.
        """;

    private static MarkdownFile Md(string text) => MarkdownFile.Parse(TestFiles.Utf8(text));

    private static string Edit(string text, Func<MarkdownFile, MarkdownFile> edit) => edit(Md(text)).Text;

    [Fact]
    public void ReplaceTopLevelScalarKeepsSpacingAndComment()
    {
        var result = Edit(Entity, f => f.WithFrontmatterValue("name", YamlScalarValue.String("Mara Halden")));

        Assert.Equal(Entity.Replace("name: Mara Voss   # display name", "name: Mara Halden   # display name", StringComparison.Ordinal), result);
    }

    [Fact]
    public void ReplaceKeepsTheQuotingStyle()
    {
        var result = Edit(Entity, f => f.WithFrontmatterValue("status", YamlScalarValue.String("canon")));

        Assert.Equal(Entity.Replace("status: 'draft'", "status: 'canon'", StringComparison.Ordinal), result);
    }

    [Theory]
    [InlineData("2291", "\"2291\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("null", "\"null\"")]
    [InlineData("", "\"\"")]
    [InlineData("a: b", "\"a: b\"")]
    [InlineData("say \"hi\"", "say \"hi\"")]
    [InlineData("[[velari]]", "\"[[velari]]\"")]
    [InlineData("#tag", "\"#tag\"")]
    [InlineData("line one\nline two", "\"line one\\nline two\"")]
    [InlineData("Zoë", "Zoë")]
    [InlineData("- dash", "\"- dash\"")]
    public void NewStringsAreQuotedOnlyWhenTheyMustBe(string value, string written)
    {
        var result = Edit("---\nname: x\n---\n", f => f.WithFrontmatterValue("name", YamlScalarValue.String(value)));

        Assert.Equal($"---\nname: {written}\n---\n", result);
        Assert.Equal(value, Md(result).GetFrontmatterValue("name")!.ToString());
    }

    [Fact]
    public void SetFieldUnderFields()
    {
        var result = Edit(Entity, f => f.WithField("born", YamlScalarValue.String("2290")));

        Assert.Equal(Entity.Replace("born: \"2291\"", "born: \"2290\"", StringComparison.Ordinal), result);
    }

    [Fact]
    public void SetNullFieldFillsItIn()
    {
        var result = Edit(Entity, f => f.WithField("died", YamlScalarValue.String("Never")));

        Assert.Equal(Entity.Replace("  died:\n", "  died: Never\n", StringComparison.Ordinal), result);
    }

    [Fact]
    public void ReplaceTemporalListWithScalar()
    {
        var result = Edit(Entity, f => f.WithField("rank", YamlScalarValue.String("Commander")));

        Assert.Equal(
            Entity.Replace("  rank:\n    - { value: Lieutenant, from: \"ember:1\" }\n    - { value: Commander, from: \"ember:22\" }  # promoted\n", "  rank: Commander\n", StringComparison.Ordinal),
            result);
    }

    [Fact]
    public void AddNewFieldAfterTheLastOne()
    {
        var result = Edit(Entity, f => f.WithField("species", YamlScalarValue.String("[[velari]]")));

        Assert.Equal(Entity.Replace("  died:\n", "  died:\n  species: \"[[velari]]\"\n", StringComparison.Ordinal), result);
    }

    [Fact]
    public void AddNewTopLevelKeyAfterTheLastOne()
    {
        var result = Edit(Entity, f => f.WithFrontmatterValue("from", YamlScalarValue.String("ember:1")));

        // A new position is quoted, as format.md §7.3 writes them.
        Assert.Equal(Entity.Replace("x-color: teal\n", "x-color: teal\nfrom: \"ember:1\"\n", StringComparison.Ordinal), result);
    }

    [Fact]
    public void AddFieldsMappingWhenMissing()
    {
        var text = "---\ntype: note\nname: Ember\n---\nBody.\n";

        var result = Edit(text, f => f.WithField("pov", YamlScalarValue.String("Mara Voss")));

        Assert.Equal("---\ntype: note\nname: Ember\nfields:\n  pov: Mara Voss\n---\nBody.\n", result);
    }

    [Theory]
    [InlineData("fields:\n", "fields:\n  pov: Mara\n")]
    [InlineData("fields:   # none yet\n", "fields:   # none yet\n  pov: Mara\n")]
    [InlineData("fields: ~\n", "fields:\n  pov: Mara\n")]
    [InlineData("fields: {}  # none yet\n", "fields:  # none yet\n  pov: Mara\n")]
    public void FillEmptyFieldsMapping(string before, string after)
    {
        var result = Edit($"---\nname: Ember\n{before}x-a: 1\n---\n", f => f.WithField("pov", YamlScalarValue.String("Mara")));

        Assert.Equal($"---\nname: Ember\n{after}x-a: 1\n---\n", result);
    }

    [Fact]
    public void NewFieldUsesTheFilesIndentStep()
    {
        var text = "---\nfields:\n    climate: cold\n---\n";

        var result = Edit(text, f => f.WithField("founded", YamlScalarValue.String("014")));

        Assert.Equal("---\nfields:\n    climate: cold\n    founded: \"014\"\n---\n", result);
    }

    [Fact]
    public void NewNestedMappingUsesTheFilesIndentStep()
    {
        var text = "---\nx-a:\n    b: 1\nname: Ember\n---\n";

        var result = Edit(text, f => f.WithField("pov", YamlScalarValue.String("Mara")));

        Assert.Equal("---\nx-a:\n    b: 1\nname: Ember\nfields:\n    pov: Mara\n---\n", result);
    }

    [Fact]
    public void TypedScalarsAndNull()
    {
        var text = "---\nformat: 1\nspoiler_checks: false\nname: Starfall\n---\n";
        var file = Md(text)
            .WithFrontmatterValue("format", YamlScalarValue.Integer(2))
            .WithFrontmatterValue("spoiler_checks", YamlScalarValue.Boolean(true))
            .WithFrontmatterValue("name", YamlScalarValue.Null);

        Assert.Equal("---\nformat: 2\nspoiler_checks: true\nname:\n---\n", file.Text);
    }

    [Fact]
    public void BlockScalarIsReplacedWhole()
    {
        var text = "---\nx-notes: |\n  one\n  two\n\n# about the next key\nname: Ember\n---\n";

        var result = Edit(text, f => f.WithFrontmatterValue("x-notes", YamlScalarValue.String("short")));

        Assert.Equal("---\nx-notes: short\n\n# about the next key\nname: Ember\n---\n", result);
    }

    [Fact]
    public void FileWithoutFrontmatterGetsSome()
    {
        var result = Edit("# Title\n", f => f.WithFrontmatterValue("name", YamlScalarValue.String("Ember")));

        Assert.Equal("---\nname: Ember\n---\n# Title\n", result);
    }

    [Fact]
    public void EmptyFrontmatterGetsItsFirstKey()
    {
        var result = Edit("---\n# Nothing here yet.\n---\nBody.\n", f => f.WithFrontmatterValue("name", YamlScalarValue.String("Ember")));

        Assert.Equal("---\n# Nothing here yet.\nname: Ember\n---\nBody.\n", result);
    }

    [Fact]
    public void SettingTheSameValueChangesNothing()
    {
        var file = Md(Entity);

        Assert.Same(file, file.WithFrontmatterValue("status", YamlScalarValue.String("draft")));
        Assert.Same(file, file.WithField("born", YamlScalarValue.String("2291")));
        Assert.Same(file, file.WithField("died", YamlScalarValue.Null));
    }

    [Fact]
    public void FlowMappingsTakeNewKeysAndNonMappingsAreRefused()
    {
        var file = Md("---\nfields: { a: 1 }\naliases: [x]\n---\n");

        Assert.Equal("---\nfields: { a: 1, b: \"2\" }\naliases: [x]\n---\n", file.WithField("b", YamlScalarValue.String("2")).Text);
        Assert.Throws<InvalidOperationException>(() => file.WithFrontmatterValue(["aliases", "x"], YamlScalarValue.String("2")));
        Assert.Throws<ArgumentException>(() => file.WithFrontmatterValue("bad key", YamlScalarValue.String("2")));
    }

    [Fact]
    public void FrontmatterEditsKeepCrlfBomAndMissingFinalNewline()
    {
        var bytes = TestFiles.Fixture("bom-crlf-no-final-newline.md");
        var file = MarkdownFile.Parse(bytes);

        var edited = file.WithField("species", YamlScalarValue.String("human")).WithFrontmatterValue("name", YamlScalarValue.String("Mara Halden"));

        var expected = file.Text
            .Replace("  born: \"2291\"\r\n", "  born: \"2291\"\r\n  species: human\r\n", StringComparison.Ordinal)
            .Replace("name: Mara Voss\r\n", "name: Mara Halden\r\n", StringComparison.Ordinal);
        Assert.Equal(expected, edited.Text);
        Assert.Equal([0xEF, 0xBB, 0xBF], edited.ToBytes()[..3]);
        Assert.False(edited.EndsWithNewline);
        Assert.Equal(Text.LineEndingKind.CrLf, edited.LineEndings);
    }

    [Fact]
    public void ReplaceSectionBodyKeepsHeadingCommentSubsectionsAndBlankLines()
    {
        var file = Md(Entity);
        var injury = file.Sections.Single(s => s.Name == "Shoulder injury");

        var result = file.WithSectionBody(injury, "Takes a round at [[kessan-reach]].\nThe arm never recovers.").Text;

        Assert.Equal(
            Entity.Replace("Takes a flechette round.\n", "Takes a round at [[kessan-reach]].\nThe arm never recovers.\n", StringComparison.Ordinal),
            result);
    }

    [Fact]
    public void ReplaceLastSectionBodyKeepsMissingFinalNewline()
    {
        var file = Md(Entity);

        var result = file.WithSectionBody(file.Sections[^1], "Weeks.\n").Text;

        Assert.Equal(Entity.Replace("Months of therapy.", "Weeks.", StringComparison.Ordinal), result);
    }

    [Fact]
    public void ClearingTheLastSectionBodyKeepsMissingFinalNewline()
    {
        var file = Md("## A\na\n\n## B\n\nlast");

        var result = file.WithSectionBody(file.Sections[1], "");

        Assert.Equal("## A\na\n\n## B", result.Text);
        Assert.False(result.EndsWithNewline);
    }

    [Theory]
    [InlineData("## A\n## B\n", "## A\nnew\n## B\n")]
    [InlineData("## A\n\n## B\n", "## A\nnew\n\n## B\n")]
    [InlineData("## A", "## A\nnew")]
    [InlineData("## A\n", "## A\nnew\n")]
    [InlineData("## A\n<!-- wolfden: pin=true -->\n\n## B\n", "## A\n<!-- wolfden: pin=true -->\nnew\n\n## B\n")]
    public void FillingAnEmptySectionBody(string before, string after)
    {
        var file = Md(before);

        Assert.Equal(after, file.WithSectionBody(file.Sections[0], "new").Text);
    }

    [Fact]
    public void SectionBodyEditUsesTheFilesLineEndings()
    {
        var bytes = TestFiles.Fixture("crlf-entity.md");
        var file = MarkdownFile.Parse(bytes);

        var result = file.WithSectionBody(file.Sections[0], "Grey hair.\nA scar.\n");

        Assert.Equal(file.Text.Replace("Silver-streaked hair.\r\n", "Grey hair.\r\nA scar.\r\n", StringComparison.Ordinal), result.Text);
        Assert.Equal(Text.LineEndingKind.CrLf, result.LineEndings);
    }

    [Theory]
    [InlineData("## Sneaky\ntext")]
    [InlineData("```\nunclosed fence")]
    [InlineData("<!-- wolfden: from=ember:1 -->\nbecomes a comment")]
    [InlineData("<!--\nunclosed HTML comment")]
    public void SectionBodyThatWouldChangeTheOutlineIsRefused(string body)
    {
        var file = Md("## A\nold\n\n## B\nb\n");

        Assert.Throws<ArgumentException>(() => file.WithSectionBody(file.Sections[0], body));
    }

    [Fact]
    public void SectionFromAnotherFileIsRefused()
    {
        var a = Md("## A\n");
        var b = Md("## A\n");

        Assert.Throws<ArgumentException>(() => a.WithSectionBody(b.Sections[0], "x"));
    }

    [Theory]
    [InlineData("## A\ntext\n", "## A\ntext\n\n## Arc\nShe changes.\n")]
    [InlineData("## A\ntext\n\n", "## A\ntext\n\n## Arc\nShe changes.\n")]
    [InlineData("## A\ntext", "## A\ntext\n\n## Arc\nShe changes.")]
    [InlineData("---\nname: x\n---\n", "---\nname: x\n---\n\n## Arc\nShe changes.\n")]
    [InlineData("", "## Arc\nShe changes.\n")]
    public void AppendSection(string before, string after)
    {
        var result = Md(before).WithNewSection("Arc", "She changes.");

        Assert.Equal(after, result.Text);
        Assert.Equal("Arc", result.Sections[^1].Name);
    }

    [Fact]
    public void AppendSubsectionAndEmptySection()
    {
        var result = Md(Entity).WithNewSection("Later", level: 3);

        Assert.Equal(Entity + "\n\n### Later", result.Text);
        Assert.Equal("Shoulder injury", result.Sections[^1].Parent?.Name);
    }

    [Fact]
    public void AppendSectionKeepsCrlf()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("crlf-entity.md"));

        var result = file.WithNewSection("Arc", "One.\nTwo.");

        Assert.Equal(file.Text + "\r\n## Arc\r\nOne.\r\nTwo.\r\n", result.Text);
    }

    [Theory]
    [InlineData("Act #")]
    [InlineData(" padded")]
    [InlineData("two\nlines")]
    [InlineData("")]
    [InlineData("Name <!-- c -->")]
    public void AppendSectionRefusesNamesThatDoNotReadBack(string name)
    {
        Assert.Throws<ArgumentException>(() => Md("## A\n").WithNewSection(name));
    }

    [Fact]
    public void AppendSubsectionWithoutASectionIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Md("# Title\n\nLead.\n").WithNewSection("Early", level: 3));
    }

    [Fact]
    public void AppendAfterFrontmatterWithNoFinalNewline()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("frontmatter-only-no-final-newline.md"));

        var result = file.WithNewSection("Overview", "Text.");

        Assert.Equal(file.Text + "\n\n## Overview\nText.", result.Text);
        Assert.Equal(file.Frontmatter!.Keys, result.Frontmatter!.Keys);
    }

    [Fact]
    public void AppendSectionInsideAnOpenFenceIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Md("## A\n```\ncode\n").WithNewSection("B"));
    }

    [Fact]
    public void YamlFileEdits()
    {
        var bytes = TestFiles.Fixture("den-crlf.yaml");
        var file = YamlFile.Parse(bytes);

        var edited = file
            .WithValue("name", YamlScalarValue.String("Starfall"))
            .WithValue(["authors", "wren-calder", "x-pen"], YamlScalarValue.Boolean(true))
            .WithValue("spoiler_checks", YamlScalarValue.Boolean(false));

        var expected = file.Text
            .Replace("name: \"Starfall Notes\"   # display name", "name: \"Starfall\"   # display name", StringComparison.Ordinal)
            .Replace("    name: Wren Calder\r\n", "    name: Wren Calder\r\n    x-pen: true\r\n", StringComparison.Ordinal)
            .Replace("spoiler_checks: true", "spoiler_checks: false", StringComparison.Ordinal);
        Assert.Equal(expected, edited.Text);
    }

    [Fact]
    public void YamlFileWithoutFinalNewlineGetsKeyWithoutOne()
    {
        var file = YamlFile.Parse(TestFiles.Utf8("format: 1\nname: Starfall"));

        Assert.Equal("format: 1\nname: Starfall\nx-new: v", file.WithValue("x-new", YamlScalarValue.String("v")).Text);
    }

    [Fact]
    public void EditsCarryOnFromAnEditedFile()
    {
        var file = Md(Entity)
            .WithField("born", YamlScalarValue.String("2290"))
            .WithField("species", YamlScalarValue.String("human"));
        var appearance = file.Sections.Single(s => s.Name == "Appearance");

        var result = file.WithSectionBody(appearance, "Grey.").WithNewSection("Arc", "Rises.");

        var expected = Entity
            .Replace("born: \"2291\"", "born: \"2290\"", StringComparison.Ordinal)
            .Replace("  died:\n", "  died:\n  species: human\n", StringComparison.Ordinal)
            .Replace("Silver-streaked hair.", "Grey.", StringComparison.Ordinal)
            + "\n\n## Arc\nRises.";
        Assert.Equal(expected, result.Text);
    }
}
