using Wolfden.Core.Documents;
using Wolfden.Core.Text;

namespace Wolfden.Core.Tests;

/// <summary>format.md §2: encoding, byte order marks, line endings, final newlines.</summary>
public class EncodingTests
{
    [Fact]
    public void ByteOrderMarkIsKeptButNotPartOfTheText()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("bom-entity.md"));

        Assert.True(file.HasByteOrderMark);
        Assert.StartsWith("---", file.Text, StringComparison.Ordinal);
        Assert.True(file.HasFrontmatter);
        Assert.Equal("Mara Voss", file.GetFrontmatterValue("name")?.ToString());
        Assert.Empty(file.Diagnostics);
    }

    [Fact]
    public void CrlfFileIsReadLikeAnLfFile()
    {
        var crlf = MarkdownFile.Parse(TestFiles.Fixture("crlf-entity.md"));
        var lf = MarkdownFile.Parse(TestFiles.Utf8(crlf.Text.ReplaceLineEndings("\n")));

        Assert.Equal(LineEndingKind.CrLf, crlf.LineEndings);
        Assert.Equal(LineEndingKind.Lf, lf.LineEndings);
        Assert.Equal(lf.Sections.Select(s => (s.Name, s.Level, s.Line)), crlf.Sections.Select(s => (s.Name, s.Level, s.Line)));
        Assert.Equal(lf.Frontmatter!.Keys, crlf.Frontmatter!.Keys);
        Assert.Equal("ember:19", crlf.Sections[1].Comment?["from"]);
        Assert.Empty(crlf.Diagnostics);
    }

    [Fact]
    public void MixedLineEndingsAreAWarning()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("mixed-endings.md"));

        Assert.Equal(LineEndingKind.Mixed, file.LineEndings);
        var diagnostic = Assert.Single(file.Diagnostics);
        Assert.Equal(("F-ENC-3", DiagnosticSeverity.Warning), (diagnostic.RuleId, diagnostic.Severity));
        Assert.True(file.CanEdit);
    }

    [Fact]
    public void LoneCarriageReturnIsAnErrorWithItsLine()
    {
        var bytes = TestFiles.Fixture("lone-cr.md");
        var file = MarkdownFile.Parse(bytes);

        var error = Assert.Single(file.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("F-ENC-3", 7), (error.RuleId, error.Line));
        Assert.False(file.CanEdit);
        Assert.Throws<InvalidOperationException>(() => file.WithField("born", Yaml.YamlScalarValue.String("2291")));
        Assert.Equal(bytes, file.ToBytes());
    }

    [Fact]
    public void InvalidUtf8IsAnErrorAndTheBytesStillRoundTrip()
    {
        var bytes = TestFiles.Fixture("invalid-utf8.md");
        var file = MarkdownFile.Parse(bytes);

        var error = Assert.Single(file.Diagnostics);
        Assert.Equal(("F-ENC-1", DiagnosticSeverity.Error, 7), (error.RuleId, error.Severity, error.Line));
        Assert.False(file.IsReadable);
        Assert.False(file.CanEdit);
        Assert.Empty(file.Sections);
        Assert.Throws<InvalidOperationException>(() => file.Text);
        Assert.Equal(bytes, file.ToBytes());
    }

    [Theory]
    [InlineData("no-final-newline.md", false)]
    [InlineData("bom-crlf-no-final-newline.md", false)]
    [InlineData("crlf-entity.md", true)]
    [InlineData("frontmatter-only-no-final-newline.md", false)]
    public void FinalNewlineIsReported(string fixture, bool expected)
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture(fixture));

        Assert.Equal(expected, file.EndsWithNewline);
    }

    [Fact]
    public void YamlFileWithBomAndNoFinalNewline()
    {
        var bytes = TestFiles.Fixture("den-bom-no-final-newline.yaml");
        var file = YamlFile.Parse(bytes);

        Assert.True(file.HasByteOrderMark);
        Assert.False(file.EndsWithNewline);
        Assert.Equal("1", file.GetValue("format")?.ToString());
        Assert.Equal("Wren Calder", file.GetValue("authors", "wren-calder", "name")?.ToString());
        Assert.Empty(file.Diagnostics);
        Assert.Equal(bytes, file.ToBytes());
    }
}

/// <summary>
/// Guards the .gitattributes rule that keeps fixtures byte-exact. If git
/// normalized them on commit, the CRLF and BOM round-trip tests would pass
/// on LF files and prove nothing.
/// </summary>
public class GitAttributesGuardTests
{
    [Theory]
    [InlineData("crlf-entity.md")]
    [InlineData("den-crlf.yaml")]
    [InlineData("bom-crlf-no-final-newline.md")]
    public void CrlfFixtureReallyHasCrlfBytes(string fixture)
    {
        var bytes = TestFiles.Fixture(fixture);

        Assert.Contains("\r\n", TestFiles.Text(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("\n", TestFiles.Text(bytes).Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bom-entity.md")]
    [InlineData("bom-crlf-no-final-newline.md")]
    [InlineData("den-bom-no-final-newline.yaml")]
    public void BomFixtureReallyStartsWithABom(string fixture)
    {
        Assert.Equal([0xEF, 0xBB, 0xBF], TestFiles.Fixture(fixture)[..3]);
    }

    [Theory]
    [InlineData("no-final-newline.md")]
    [InlineData("bom-crlf-no-final-newline.md")]
    [InlineData("den-bom-no-final-newline.yaml")]
    public void NoFinalNewlineFixtureReallyLacksOne(string fixture)
    {
        Assert.NotEqual((byte)'\n', TestFiles.Fixture(fixture)[^1]);
    }

    [Fact]
    public void MixedAndLoneCrFixturesKeepTheirBytes()
    {
        var mixed = TestFiles.Text(TestFiles.Fixture("mixed-endings.md"));
        Assert.Contains("\r\n", mixed, StringComparison.Ordinal);
        Assert.Contains("\n\n", mixed, StringComparison.Ordinal);
        Assert.Contains("line\rends", TestFiles.Text(TestFiles.Fixture("lone-cr.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void GitAttributesMarksTheFixtureFolderAsNotText()
    {
        var attributes = File.ReadAllLines(Path.Combine(TestFiles.RepoRoot, ".gitattributes"));

        Assert.Contains(attributes, line => line.Trim() == "tests/Wolfden.Core.Tests/Fixtures/** -text");
    }
}
