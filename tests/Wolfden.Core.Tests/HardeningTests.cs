using System.Text;
using Wolfden.Core.Documents;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Tests;

/// <summary>
/// Inputs that once broke the parser or an edit: keep-chomped block
/// scalars, deep nesting, unusual line-break characters, anchors and
/// repeated keys, and files that need frontmatter added.
/// </summary>
public class HardeningTests
{
    private static MarkdownFile Md(string text, bool requireFrontmatter = false) =>
        MarkdownFile.Parse(TestFiles.Utf8(text), requireFrontmatter);

    // --- Keep-chomped block scalars (|+, >+) ---

    [Fact]
    public void NewKeyGoesAfterTheKeptBlankLinesOfALastKeepScalar()
    {
        var file = Md("---\na: |+\n  l1\n\n---\n");

        var result = file.WithFrontmatterValue("b", YamlScalarValue.String("x"));

        Assert.Equal("---\na: |+\n  l1\n\nb: x\n---\n", result.Text);
        Assert.Equal("l1\n\n", result.GetFrontmatterValue("a")!.ToString());
    }

    [Theory]
    [InlineData("|+")]
    [InlineData(">+")]
    [InlineData("|2+")]
    [InlineData("|+2")]
    public void KeepScalarUnderFieldsKeepsItsValue(string header)
    {
        var text = $"---\nfields:\n  motto: {header}\n    one\n\n\nname: Ember\n---\n";
        var file = Md(text);
        var before = file.GetFrontmatterValue("fields", "motto")!.ToString();

        var result = file.WithField("born", YamlScalarValue.String("2291"));

        Assert.Equal(text.Replace("\n\n\nname:", "\n\n\n  born: \"2291\"\nname:", StringComparison.Ordinal), result.Text);
        Assert.Equal(before, result.GetFrontmatterValue("fields", "motto")!.ToString());
    }

    [Fact]
    public void KeepScalarAsLastListItemKeepsItsValue()
    {
        var text = "---\nfields:\n  rank:\n    - Ensign\n    - |+\n      Lieutenant\n\n# note\n---\n";
        var file = Md(text);

        var result = file.WithField("born", YamlScalarValue.String("x"));

        Assert.Equal("---\nfields:\n  rank:\n    - Ensign\n    - |+\n      Lieutenant\n\n  born: x\n# note\n---\n", result.Text);
        Assert.Equal("Lieutenant\n\n", ((YamlSequence)result.GetFrontmatterValue("fields", "rank")!).Items[1].ToString());
    }

    [Fact]
    public void ReplacingAKeepScalarRemovesItsKeptLines()
    {
        var result = Md("---\na: |+\n  l1\n\nb: 1\n---\n").WithFrontmatterValue("a", YamlScalarValue.String("x"));

        Assert.Equal("---\na: x\nb: 1\n---\n", result.Text);
    }

    [Fact]
    public void KeepScalarInYamlFile()
    {
        var file = YamlFile.Parse(TestFiles.Utf8("format: 1\nnotes: >+\n  Kept.\n\n\n"));

        var result = file.WithValue("name", YamlScalarValue.String("Starfall"));

        Assert.Equal("format: 1\nnotes: >+\n  Kept.\n\n\nname: Starfall\n", result.Text);
        Assert.Equal(file.GetValue("notes")!.ToString(), result.GetValue("notes")!.ToString());
    }

    [Fact]
    public void KeepScalarFixtureSurvivesEveryKindOfKeyEdit()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("keep-chomped-scalars.md"));
        Assert.True(file.CanEdit);

        var edits = new Func<MarkdownFile, MarkdownFile>[]
        {
            f => f.WithFrontmatterValue("x-new", YamlScalarValue.String("v")),
            f => f.WithField("born", YamlScalarValue.String("2291")),
            f => f.WithField("motto", YamlScalarValue.String("short")),
            f => f.WithFrontmatterValue("x-keep-top", YamlScalarValue.Null),
        };
        foreach (var edit in edits)
        {
            var result = edit(file);

            // The whole-tree check inside the edit would have thrown if any
            // other value had changed; check the kept scalars explicitly too.
            foreach (var path in new[] { new[] { "x-last" }, ["fields", "last-kept"] })
            {
                Assert.Equal(file.GetFrontmatterValue(path)!.ToString(), result.GetFrontmatterValue(path)!.ToString());
            }

            Assert.Equal(file.Body, result.Body);
        }
    }

    // --- Whole-tree verification ---

    [Fact]
    public void TreeCheckRejectsAChangeToAnotherValue()
    {
        var before = Md("---\na: 1\nb: |+\n  x\n\nc: 3\n---\n").Frontmatter;
        var sibling = Md("---\na: 1\nb: |+\n  x\nc: 3\nd: 4\n---\n").Frontmatter;
        var good = Md("---\na: 1\nb: |+\n  x\n\nc: 3\nd: 4\n---\n").Frontmatter;
        var reordered = Md("---\nb: |+\n  x\n\na: 1\nc: 3\nd: 4\n---\n").Frontmatter;

        Assert.True(YamlTreeCheck.SameExcept(before, good, ["d"], YamlScalarValue.Integer(4)));
        Assert.False(YamlTreeCheck.SameExcept(before, sibling, ["d"], YamlScalarValue.Integer(4)));
        Assert.False(YamlTreeCheck.SameExcept(before, reordered, ["d"], YamlScalarValue.Integer(4)));
        Assert.False(YamlTreeCheck.SameExcept(before, good, ["d"], YamlScalarValue.Integer(5)));
    }

    [Fact]
    public void TreeCheckAcceptsNewNestedMappings()
    {
        var before = Md("---\na: 1\nfields:\n---\n").Frontmatter;
        var after = Md("---\na: 1\nfields:\n  pov: Mara\n---\n").Frontmatter;

        Assert.True(YamlTreeCheck.SameExcept(before, after, ["fields", "pov"], YamlScalarValue.String("Mara")));
        Assert.True(YamlTreeCheck.SameExcept(null, Md("---\nfields:\n  pov: Mara\n---\n").Frontmatter, ["fields", "pov"], YamlScalarValue.String("Mara")));
    }

    // --- Deep nesting never crashes ---

    [Theory]
    [InlineData(300)]
    [InlineData(5000)]
    public void DeeplyNestedFlowYamlIsAnError(int depth)
    {
        var bytes = TestFiles.Utf8("---\na: " + new string('[', depth) + new string(']', depth) + "\n---\n## A\n");

        var file = MarkdownFile.Parse(bytes);

        Assert.Contains(file.Diagnostics, d => d.RuleId == "F-YAML-1" && d.Line == 2);
        Assert.False(file.CanEdit);
        Assert.Equal(bytes, file.ToBytes());
        Assert.Equal(["A"], file.Sections.Select(s => s.Name));
    }

    [Fact]
    public void DeeplyNestedBlockYamlIsAnError()
    {
        var sb = new StringBuilder("format: 1\n");
        for (var i = 0; i < 400; i++)
        {
            sb.Append(' ', i).Append("k:\n");
        }

        var bytes = TestFiles.Utf8(sb.ToString());
        var file = YamlFile.Parse(bytes);

        Assert.Contains(file.Diagnostics, d => d.RuleId == "F-YAML-1");
        Assert.Equal(bytes, file.ToBytes());
    }

    [Fact]
    public void NestingJustUnderTheLimitParses()
    {
        var depth = YamlBlock.MaxDepth - 1;
        var file = Md("---\na: " + new string('[', depth) + new string(']', depth) + "\n---\n");

        Assert.Empty(file.Diagnostics);
    }

    [Theory]
    [InlineData(">", 200)]
    [InlineData("> ", 2000)]
    public void DeeplyNestedBlockQuotesAreAnError(string marker, int depth)
    {
        var bytes = TestFiles.Utf8("---\nname: x\n---\n## A\n" + string.Concat(Enumerable.Repeat(marker, depth)) + " x\n");

        var file = MarkdownFile.Parse(bytes);

        AssertBodyFailure(file, bytes);
    }

    [Fact]
    public void DeeplyNestedListsAreAnError()
    {
        var sb = new StringBuilder("## A\n");
        for (var i = 0; i < 400; i++)
        {
            sb.Append(' ', i * 2).Append("- x\n");
        }

        var bytes = TestFiles.Utf8(sb.ToString());
        var file = MarkdownFile.Parse(bytes);

        AssertBodyFailure(file, bytes);
    }

    private static void AssertBodyFailure(MarkdownFile file, byte[] bytes)
    {
        var d = Assert.Single(file.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("F-FM-2", d.RuleId);
        Assert.Empty(file.Sections);
        Assert.False(file.CanEdit);
        Assert.Equal(bytes, file.ToBytes());
    }

    // --- Line breaks in edit input ---

    [Theory]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u0085b")]
    [InlineData("a\fb")]
    [InlineData("a\vb")]
    public void OnlyCrAndLfBreakLinesInSectionText(string body)
    {
        var file = Md("## A\nold\n\n## B\n");

        var edited = file.WithSectionBody(file.Sections[0], body);
        var appended = file.WithNewSection("C", body);

        Assert.Equal($"## A\n{body}\n\n## B\n", edited.Text);
        Assert.Equal($"## A\nold\n\n## B\n\n## C\n{body}\n", appended.Text);
    }

    [Fact]
    public void BlankLinesAroundNewTextAreDropped()
    {
        var file = Md("## A\nold\n\n## B\n");

        Assert.Equal("## A\nnew\n\n## B\n", file.WithSectionBody(file.Sections[0], "\n\nnew\n\n\n").Text);
        Assert.Equal("## A\nold\n\n## B\n\n## C\nnew\n", file.WithNewSection("C", " \n\r\nnew\r\n\n").Text);
    }

    [Fact]
    public void AppendedSectionMayStartWithASectionComment()
    {
        var result = Md("## A\n").WithNewSection("B", "<!-- wolfden: from=ember:2 -->\nText.");

        Assert.Equal("ember:2", result.Sections[^1].Comment?["from"]);
        Assert.Equal("Text.\n", result.Sections[^1].Body);
    }

    // --- Anchors, aliases, tags, repeated keys block edits ---

    [Theory]
    [InlineData("a: &x foo\nb: *x\n", "F-YAML-4")]
    [InlineData("a: 1\nb: 2\na: 3\n", "F-YAML-3")]
    [InlineData("a: !!str 1\n", "F-YAML-4")]
    [InlineData("a:\n  <<: { k: 1 }\n", "F-YAML-4")]
    public void YamlThatOtherToolsReadDifferentlyCannotBeEdited(string yaml, string rule)
    {
        var file = Md("---\n" + yaml + "---\n## S\n");

        Assert.False(file.CanEdit);
        var ex = Assert.Throws<InvalidOperationException>(() => file.WithFrontmatterValue("a", YamlScalarValue.String("bar")));
        Assert.Contains(rule, ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => file.WithSectionBody(file.Sections[0], "x"));
        Assert.Throws<InvalidOperationException>(() => YamlFile.Parse(TestFiles.Utf8(yaml)).WithValue("a", YamlScalarValue.Null));
    }

    [Fact]
    public void AliasToAnUnknownAnchorIsAParseError()
    {
        var file = Md("---\nb: *missing\n---\n");

        Assert.Contains(file.Diagnostics, d => d.RuleId == "F-YAML-1" && d.Line == 2);
        Assert.Contains(file.Diagnostics, d => d.RuleId == "F-YAML-4" && d.Line == 2);
    }

    [Fact]
    public void AnchorsFixtureRoundTripsButIsNotEditable()
    {
        var bytes = TestFiles.Fixture("anchors-and-repeats.md");
        var file = MarkdownFile.Parse(bytes);

        Assert.Equal(bytes, file.ToBytes());
        Assert.False(file.CanEdit);
        Assert.Equal(["F-YAML-4", "F-YAML-4", "F-YAML-3"], file.Diagnostics.Select(d => d.RuleId));
    }

    // --- Adding frontmatter to a file that needs it ---

    [Fact]
    public void FrontmatterCanBeAddedToAnEntityFileWithoutAny()
    {
        var file = Md("## A\nx\n", requireFrontmatter: true);
        Assert.False(file.CanEdit);

        var result = file.WithFrontmatterValue("id", YamlScalarValue.String("01JA7Q8M4XK3V9R2D5W8HN6TQB"));

        Assert.Equal("---\nid: 01JA7Q8M4XK3V9R2D5W8HN6TQB\n---\n## A\nx\n", result.Text);
        Assert.Empty(result.Diagnostics);
        Assert.True(result.CanEdit);
    }

    [Fact]
    public void MissingFrontmatterStillBlocksOtherEdits()
    {
        var file = Md("## A\nx\n", requireFrontmatter: true);

        Assert.Throws<InvalidOperationException>(() => file.WithSectionBody(file.Sections[0], "y"));
        Assert.Throws<InvalidOperationException>(() => file.WithNewSection("B"));
    }

    [Fact]
    public void UnclosedFrontmatterStillBlocksFrontmatterEdits()
    {
        var file = Md("---\nname: x\n\n## A\n", requireFrontmatter: true);

        Assert.Throws<InvalidOperationException>(() => file.WithFrontmatterValue("id", YamlScalarValue.String("x")));
    }

    // --- Non-printable characters are escaped ---

    [Theory]
    [InlineData("a\uFFFEb", "\"a\\uFFFEb\"")]
    [InlineData("a\uFFFFb", "\"a\\uFFFFb\"")]
    [InlineData("a\u007Fb", "\"a\\u007Fb\"")]
    [InlineData("a\u0085b", "\"a\\u0085b\"")]
    [InlineData("a\u2028b", "\"a\\u2028b\"")]
    public void NonPrintableCharactersAreEscaped(string value, string written)
    {
        var result = Md("---\nname: x\n---\n").WithFrontmatterValue("name", YamlScalarValue.String(value));

        Assert.Equal($"---\nname: {written}\n---\n", result.Text);
        Assert.Equal(value, result.GetFrontmatterValue("name")!.ToString());
    }

    // --- Section.BodyLine ---

    [Fact]
    public void BodyLinePointsAtTheFirstLineOfTheBody()
    {
        var file = Md("---\nname: x\n---\n## A\n<!-- wolfden: from=ember:1 -->\nText.\n\nSetext\n---\n\nMore.\n## Empty");

        Assert.Equal([(4, 6), (8, 10), (12, 13)], file.Sections.Select(s => (s.Line, s.BodyLine)));
    }

    // --- Known YamlDotNet limits, pinned ---

    [Fact]
    public void TabIndentedCommentIsReportedByYamlDotNet()
    {
        // Valid YAML 1.2, but YamlDotNet rejects a tab before a comment. This
        // pins the current behavior: an F-YAML-1 error, not a crash.
        var file = Md("---\na: 1\n\t# note\n---\n");

        Assert.Contains(file.Diagnostics, d => d.RuleId == "F-YAML-1");
    }

    [Theory]
    [InlineData("---\n")]
    [InlineData("--- # empty\n")]
    [InlineData("# only a comment\n")]
    [InlineData("")]
    public void EmptyYamlFileIsAnEmptyMapping(string text)
    {
        var file = YamlFile.Parse(TestFiles.Utf8(text));

        Assert.Null(file.Root);
        Assert.Empty(file.Diagnostics);
        Assert.True(file.CanEdit);
    }

    [Fact]
    public void EncodedTextMatchesTheInputForEveryFixtureAndSample()
    {
        // ToBytes re-encodes the decoded text, which is what every edit
        // builds on, so this is the real round-trip guarantee.
        var paths = TestFiles.RepoFiles("samples/dracula", "packs/core")
            .Concat(Directory.EnumerateFiles(TestFiles.FixturesDir).Select(f => Path.GetRelativePath(TestFiles.RepoRoot, f)));
        foreach (var path in paths)
        {
            var bytes = TestFiles.ReadRepoFile(path);
            DenFile file = path.EndsWith(".yaml", StringComparison.Ordinal) ? YamlFile.Parse(bytes) : MarkdownFile.Parse(bytes);
            if (file.IsReadable)
            {
                var reencoded = (file.HasByteOrderMark ? new byte[] { 0xEF, 0xBB, 0xBF } : []).Concat(Encoding.UTF8.GetBytes(file.Text));
                Assert.Equal(bytes, reencoded);
            }

            Assert.Equal(bytes, file.ToBytes());
        }
    }
}
