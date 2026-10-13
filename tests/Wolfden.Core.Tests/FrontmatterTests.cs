using Wolfden.Core.Documents;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Tests;

/// <summary>format.md §3: frontmatter boundaries and YAML values for reading.</summary>
public class FrontmatterTests
{
    private static MarkdownFile Md(string text, bool requireFrontmatter = false) =>
        MarkdownFile.Parse(TestFiles.Utf8(text), requireFrontmatter);

    [Fact]
    public void ReadsNestedFieldsListsAndMaps()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("odd-yaml-and-sections.md"));
        var fm = file.Frontmatter!;

        Assert.Equal(
            ["id", "type", "name", "aliases", "status", "x-notes", "x-summary", "fields", "x-unknown-map"],
            fm.Keys);
        Assert.Equal("location", fm["type"]!.ToString());
        Assert.Equal(YamlScalarStyle.SingleQuoted, ((YamlScalar)fm["name"]!).Style);
        Assert.Equal(["the Reach", "Kessan's Belt"], ((YamlSequence)fm["aliases"]!).Items.Select(i => i.ToString()));
        Assert.Equal("Keep this block as written.\n  Indented more.\n", fm["x-notes"]!.ToString());
        Assert.Equal("A contested belt of mining stations at the edge of settled space.", fm["x-summary"]!.ToString());

        var fields = (YamlMapping)fm["fields"]!;
        Assert.Equal("cold", fields["climate"]!.ToString());
        Assert.Equal("014", fields["founded"]!.ToString());
        Assert.True(((YamlScalar)fields["population"]!).IsNull);
        Assert.True(((YamlScalar)fields["ruler"]!).IsNull);
        Assert.Equal("[[tessaly]]", file.GetFrontmatterValue("fields", "neighbors", "north")!.ToString());

        var eras = (YamlSequence)fields["eras"]!;
        Assert.True(((YamlMapping)eras.Items[0]).IsFlow);
        var occupied = (YamlMapping)eras.Items[1];
        Assert.False(occupied.IsFlow);
        Assert.Equal(["value", "from", "extra-key"], occupied.Keys);
        Assert.Equal(25, occupied.Line);
    }

    [Theory]
    [InlineData("14", YamlScalarStyle.Plain)]
    [InlineData("\"14\"", YamlScalarStyle.DoubleQuoted)]
    [InlineData("'14'", YamlScalarStyle.SingleQuoted)]
    public void ScalarsAreReadAsWritten(string written, YamlScalarStyle style)
    {
        // F-YAML-5: 14, "14", and '14' are the same text.
        var file = Md($"---\nkey: {written}\n---\n");

        var scalar = Assert.IsType<YamlScalar>(file.GetFrontmatterValue("key"));
        Assert.Equal(("14", style), (scalar.Text, scalar.Style));
        Assert.False(scalar.IsNull);
    }

    [Theory]
    [InlineData("name:", true)]
    [InlineData("name: ~", true)]
    [InlineData("name: null", true)]
    [InlineData("name: NULL", true)]
    [InlineData("name: \"null\"", false)]
    [InlineData("name: ''", false)]
    [InlineData("name: 1984", false)]
    public void NullsFollowTheCoreSchema(string line, bool isNull)
    {
        var file = Md($"---\n{line}\n---\n");

        Assert.Equal(isNull, ((YamlScalar)file.GetFrontmatterValue("name")!).IsNull);
    }

    [Theory]
    [InlineData("---  \t\nname: Ember\n---\t\nBody\n")]
    [InlineData("---\nname: Ember\n--- \n")]
    [InlineData("---\r\nname: Ember\r\n---\r\nBody\r\n")]
    public void FenceLinesMayHaveTrailingSpacesAndTabs(string text)
    {
        var file = Md(text);

        Assert.True(file.HasFrontmatter);
        Assert.Equal("Ember", file.GetFrontmatterValue("name")?.ToString());
    }

    [Fact]
    public void DotsDoNotCloseFrontmatter()
    {
        var file = Md("---\nname: Ember\n...\n");

        Assert.False(file.HasFrontmatter);
        Assert.Equal(("F-FM-1", DiagnosticSeverity.Error, 1), Single(file));
        Assert.False(file.CanEdit);
    }

    [Fact]
    public void UnclosedFrontmatterIsAnError()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("unclosed-frontmatter.md"));

        Assert.False(file.HasFrontmatter);
        Assert.Equal(("F-FM-1", DiagnosticSeverity.Error, 1), Single(file));
    }

    [Fact]
    public void MissingFrontmatterIsAnErrorOnlyWhenRequired()
    {
        var bytes = TestFiles.Fixture("no-frontmatter.md");

        Assert.Empty(MarkdownFile.Parse(bytes).Diagnostics);
        Assert.Equal(("F-FM-1", DiagnosticSeverity.Error, 1), Single(MarkdownFile.Parse(bytes, requireFrontmatter: true)));
    }

    [Fact]
    public void FrontmatterMustStartOnTheFirstLine()
    {
        var file = Md("\n---\nname: Ember\n---\n");

        Assert.False(file.HasFrontmatter);
        Assert.Null(file.Frontmatter);
    }

    [Theory]
    [InlineData("empty-frontmatter.md")]
    [InlineData("comment-only-frontmatter.md")]
    public void EmptyFrontmatterHasNoKeysAndNoDiagnostics(string fixture)
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture(fixture));

        Assert.True(file.HasFrontmatter);
        Assert.Null(file.Frontmatter);
        Assert.Empty(file.Diagnostics);
        Assert.True(file.CanEdit);
    }

    [Fact]
    public void YamlSyntaxErrorReportsItsFileLine()
    {
        var file = Md("---\nname: Ember\nx: \"a\" b\ny: 1\n---\n\n## Overview\n");

        Assert.Equal(("F-YAML-1", DiagnosticSeverity.Error, 3), Single(file));
        Assert.False(file.CanEdit);
        Assert.Single(file.Sections);
    }

    [Theory]
    [InlineData("x: [1, 2\ny: 1\n")]
    [InlineData("a:\n\tb: tab\n")]
    [InlineData("a: 'unclosed\n")]
    [InlineData("a: b: c\n")]
    public void MalformedYamlIsAnErrorNotACrash(string yaml)
    {
        var bytes = TestFiles.Utf8("---\n" + yaml + "---\n## A\n");
        var file = MarkdownFile.Parse(bytes);

        Assert.Contains(file.Diagnostics, d => d.RuleId == "F-YAML-1" && d.Severity == DiagnosticSeverity.Error);
        Assert.False(file.CanEdit);
        Assert.Equal(bytes, file.ToBytes());
        Assert.Equal(["A"], file.Sections.Select(s => s.Name));
    }

    [Theory]
    [InlineData("- a\n- b\n")]
    [InlineData("just text\n")]
    [InlineData("~\n")]
    public void TopLevelMustBeAMapping(string yaml)
    {
        var file = Md("---\n" + yaml + "---\n");

        Assert.Equal(("F-YAML-2", DiagnosticSeverity.Error, 2), Single(file));
        Assert.Null(file.Frontmatter);
    }

    [Fact]
    public void RepeatedKeysAreErrorsAtEveryDepth()
    {
        var file = Md("---\nname: A\nfields:\n  x: 1\n  x: 2\nname: B\n---\n");

        Assert.Equal(
            [("F-YAML-3", 5), ("F-YAML-3", 6)],
            file.Diagnostics.Select(d => (d.RuleId, d.Line ?? 0)));
        Assert.Equal("A", file.GetFrontmatterValue("name")!.ToString());
    }

    [Theory]
    [InlineData("a: &x 1\nb: *x\n", 2)]
    [InlineData("a: !!str 1\n", 1)]
    [InlineData("a: !local x\n", 1)]
    [InlineData("base: { k: 1 }\nother:\n  <<: { k: 2 }\n", 1)]
    public void AnchorsAliasesTagsAndMergeKeysAreErrors(string yaml, int count)
    {
        var file = Md("---\n" + yaml + "---\n");

        Assert.Equal(count, file.Diagnostics.Count(d => d.RuleId == "F-YAML-4" && d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void SecondYamlDocumentIsAnError()
    {
        var file = YamlFile.Parse(TestFiles.Utf8("a: 1\n---\nb: 2\n"));

        Assert.Equal(("F-YAML-1", DiagnosticSeverity.Error, 2), Single(file));
    }

    [Fact]
    public void UnknownKeysAreKeptInOrder()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("crlf-entity.md"));

        Assert.Equal(["id", "type", "name", "aliases", "status", "fields", "x-color"], file.Frontmatter!.Keys);
        Assert.Equal("teal", file.GetFrontmatterValue("x-color")!.ToString());
    }

    private static (string, DiagnosticSeverity, int?) Single(DenFile file)
    {
        var d = Assert.Single(file.Diagnostics);
        return (d.RuleId, d.Severity, d.Line);
    }
}
