using Wolfden.Core.Documents;
using Wolfden.Core.Positions;

namespace Wolfden.Core.Tests;

/// <summary>The position grammar (format.md §8.1) and where positions are read.</summary>
public class PositionSyntaxTests
{
    private static MarkdownFile Md(string text) => MarkdownFile.Parse(TestFiles.Utf8(text), requireFrontmatter: true);

    [Theory]
    // format.md §8.1's table.
    [InlineData("ember:14", "ember", "14")]
    [InlineData("ember:prologue", "ember", "prologue")]
    [InlineData("a-quiet-moon:2b", "a-quiet-moon", "2b")]
    [InlineData("ember", "ember", null)]
    // F-KEY-1, F-KEY-2: chapter keys may start with a digit or be r-something that isn't r+digit.
    [InlineData("ember:ch-07", "ember", "ch-07")]
    [InlineData("ember:014", "ember", "014")]
    [InlineData("ember:rescue", "ember", "rescue")]
    [InlineData("ember:r-2", "ember", "r-2")]
    [InlineData("ember:r", "ember", "r")]
    [InlineData("e7:7", "e7", "7")]
    [InlineData("dracula:note", "dracula", "note")]
    public void ValidPositionsParse(string text, string book, string? chapter)
    {
        Assert.True(StoryPosition.TryParse(text, out var position, out var error), error?.Message);
        Assert.Equal(book, position.BookKey);
        Assert.Equal(chapter, position.ChapterKey);
        Assert.Equal(chapter is null, position.IsBookStart);
        Assert.Equal(text, position.ToString());
        Assert.Equal(position, StoryPosition.Parse(text));
    }

    [Theory]
    // format.md §8.1's table.
    [InlineData("ember:14.2", "F-POS-2")]
    [InlineData("ember:r75", "F-POS-6")]
    [InlineData("ember:r69a", "F-POS-6")]
    [InlineData("ember:", "F-POS-3")]
    [InlineData(":14", "F-POS-3")]
    [InlineData("Ember:14", "F-POS-3")]
    [InlineData("ember: 14", "F-POS-3")]
    [InlineData("ember:14:2", "F-POS-3")]
    // F-POS-6 comes before F-POS-2, and both look only at the chapter part.
    [InlineData("ember:r75.1", "F-POS-6")]
    [InlineData("ember:r7:2", "F-POS-6")]
    [InlineData("ember:.", "F-POS-2")]
    [InlineData("ember:14.", "F-POS-2")]
    [InlineData("ember:1:2.3", "F-POS-2")]
    // A '.' with no ':' isn't the scene form: it's just not a position.
    [InlineData("ember.1", "F-POS-3")]
    [InlineData("ember:R75", "F-POS-3")]
    [InlineData("", "F-POS-3")]
    [InlineData(" ember:14", "F-POS-3")]
    [InlineData("ember:14 ", "F-POS-3")]
    [InlineData("ember:14\n", "F-POS-3")]
    [InlineData("ember\t", "F-POS-3")]
    [InlineData("ember::14", "F-POS-3")]
    [InlineData("7th-fleet:1", "F-POS-3")]
    [InlineData("7th-fleet", "F-POS-3")]
    [InlineData("-ember:1", "F-POS-3")]
    [InlineData("ember-:1", "F-POS-3")]
    [InlineData("ember--ash:1", "F-POS-3")]
    [InlineData("ember:1-", "F-POS-3")]
    [InlineData("kessan_reach:1", "F-POS-3")]
    [InlineData("émber:1", "F-POS-3")]
    [InlineData("ember:１", "F-POS-3")]
    [InlineData("ember: 1", "F-POS-3")]
    [InlineData("\"ember:14\"", "F-POS-3")]
    [InlineData("[[ember]]", "F-POS-3")]
    public void InvalidPositionsFailWithTheirRule(string text, string rule)
    {
        Assert.False(StoryPosition.TryParse(text, out var position, out var error));
        Assert.Null(position);
        Assert.Equal(rule, error.RuleId);
        Assert.Throws<FormatException>(() => StoryPosition.Parse(text));
    }

    [Fact]
    public void ErrorMessagesUseTheSpecWording()
    {
        StoryPosition.TryParse("ember:14.2", out _, out var scene);
        StoryPosition.TryParse("ember:r75", out _, out var row);
        StoryPosition.TryParse("Ember:14", out _, out var invalid);

        Assert.Contains("scenes are not supported yet; v0 positions are chapter-level", scene!.Message);
        Assert.Contains("outline-row positions are not supported yet; v0 positions are chapter-level", row!.Message);
        Assert.Contains("invalid story position", invalid!.Message);
        Assert.Contains("'Ember:14'", invalid.Message);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void KeysAreAtMost64Characters(int length, bool valid)
    {
        var key = "e" + new string('a', length - 1);

        Assert.Equal(valid, StoryPosition.TryParse(key, out _, out _));
        Assert.Equal(valid, StoryPosition.TryParse($"ember:{key}", out _, out _));
    }

    [Fact]
    public void CreateValidatesKeys()
    {
        Assert.Equal("ember:2b", StoryPosition.Create("ember", "2b").ToString());
        Assert.Equal("ember", StoryPosition.Create("ember").ToString());
        Assert.Throws<ArgumentException>(() => StoryPosition.Create("7th"));
        Assert.Throws<ArgumentException>(() => StoryPosition.Create("ember", "r75"));
        Assert.Throws<ArgumentException>(() => StoryPosition.Create("ember", "14.2"));
    }

    [Theory]
    [InlineData("from=ember:19", null)]
    [InlineData("from=\"ember:19\" until=ember:30", null)]
    [InlineData("from=a-quiet-moon:2b", null)]
    [InlineData("from=ember", null)]
    [InlineData("from=ember:14.2", "F-POS-2")]
    [InlineData("until=ember:r75", "F-POS-6")]
    [InlineData("from=Ember:14", "F-POS-3")]
    [InlineData("from=ember:14:2", "F-POS-3")]
    [InlineData("from=\"ember: 14\"", "F-POS-3")]
    [InlineData("from=\"\"", "F-POS-3")]
    public void SectionCommentPositionsAreCheckedWhenTheFileIsParsed(string attributes, string? rule)
    {
        var file = Md($"---\nname: x\n---\n## Shoulder injury\n<!-- wolfden: {attributes} -->\nText.\n");

        var positionDiagnostics = file.Diagnostics.Where(d => d.RuleId.StartsWith("F-POS", StringComparison.Ordinal)).ToList();
        if (rule is null)
        {
            Assert.Empty(file.Diagnostics);
        }
        else
        {
            // A bad value in a well-formed comment is an error (F-SEC-10), on the comment's line.
            var d = Assert.Single(positionDiagnostics);
            Assert.Equal(rule, d.RuleId);
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.Equal(5, d.Line);
        }
    }

    [Fact]
    public void ARepeatedFromIsReportedOnceAndTheFirstCounts()
    {
        var file = Md("---\nname: x\n---\n## A\n<!-- wolfden: from=ember:19 from=ember:14.2 -->\n");

        var d = Assert.Single(file.Diagnostics);
        Assert.Equal("F-SEC-7", d.RuleId);
        var reference = Assert.Single(PositionScanner.Scan(file).References);
        Assert.Equal("ember:19", reference.Text);
    }

    [Fact]
    public void APositionWithASpaceAfterTheColonIsAYamlError()
    {
        var file = Md("---\nname: x\nfrom: ember: 14\n---\n");

        Assert.Contains(file.Diagnostics, d => d.RuleId == "F-YAML-1" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ScannerFindsEveryFrontmatterAndSectionPosition()
    {
        var file = Md("""
            ---
            type: character
            name: Mara Voss
            from: "ember:1"
            until: "ash:1"
            fields:
              rank:
                - { value: Lieutenant, from: "ember:1" }
                - { value: Commander, from: ember:22, until: 'ash:30' }
              species: { value: velari, from: "ash:4" }
              born: "2291"
              x: { not: an entry, from: "ember:9" }
              planted: ["ember:2", "ash:1"]
              paid_off: "ash:20"
            ---

            ## Shoulder injury
            <!-- wolfden: from=ember:19 status=canon -->

            ### Recovery
            <!-- wolfden: from=ash:3 until=ash:20 -->
            """.Replace("\r\n", "\n"));

        var scan = PositionScanner.Scan(file, new HashSet<string> { "planted", "paid_off" });

        Assert.Empty(file.Diagnostics);
        Assert.Empty(scan.Diagnostics);
        var found = scan.References.Select(r => (r.Site, r.Text, r.Line, r.Field, r.Section?.Name)).ToList();
        Assert.Equal(
        [
            (PositionSite.EntityFrom, "ember:1", 4, null, null),
            (PositionSite.EntryFrom, "ember:1", 8, "rank", null),
            (PositionSite.EntryFrom, "ember:22", 9, "rank", null),
            (PositionSite.EntryUntil, "ash:30", 9, "rank", null),
            (PositionSite.EntryFrom, "ash:4", 10, "species", null),
            (PositionSite.FieldValue, "ember:2", 13, "planted", null),
            (PositionSite.FieldValue, "ash:1", 13, "planted", null),
            (PositionSite.FieldValue, "ash:20", 14, "paid_off", null),
            (PositionSite.SectionFrom, "ember:19", 18, null, "Shoulder injury"),
            (PositionSite.SectionFrom, "ash:3", 21, null, "Recovery"),
            (PositionSite.SectionUntil, "ash:20", 21, null, "Recovery"),
        ], found);
        Assert.All(scan.References, r => Assert.NotNull(r.Position));
    }

    [Fact]
    public void BadFromAndUntilInFrontmatterAreErrors()
    {
        var file = Md("""
            ---
            name: x
            from: "ember:14.2"
            fields:
              rank:
                - { value: a, from: "ember:r75" }
                - { value: b, from: [ember, 1], until: Ember:1 }
              born: { value: "2291", from: ~ }
            ---
            """.Replace("\r\n", "\n"));

        var scan = PositionScanner.Scan(file);

        Assert.Equal(
        [
            ("F-POS-2", 3),
            ("F-POS-6", 6),
            ("F-POS-3", 7),
            ("F-POS-3", 7),
        ], scan.Diagnostics.Select(d => (d.RuleId, d.Line ?? 0)).ToList());
        Assert.All(scan.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        // Unparsable positions are still listed, so callers can show them as written. The
        // list-valued from isn't a string at all, and the null from is the same as no from.
        Assert.Equal(["ember:14.2", "ember:r75", "Ember:1"], scan.References.Select(r => r.Text));
        Assert.All(scan.References, r => Assert.Null(r.Position));
    }

    [Fact]
    public void BadPositionFieldValuesAreWarnings()
    {
        var file = Md("""
            ---
            name: x
            fields:
              planted: ["ember:2", "ember:14.2", { a: b }, ~]
              paid_off:
            ---
            """.Replace("\r\n", "\n"));

        var scan = PositionScanner.Scan(file, new HashSet<string> { "planted", "paid_off" });

        Assert.Equal(["F-FT-10", "F-FT-10"], scan.Diagnostics.Select(d => d.RuleId));
        Assert.All(scan.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Contains("scenes are not supported yet", scan.Diagnostics[0].Message);
        Assert.Equal(["ember:2", "ember:14.2"], scan.References.Select(r => r.Text));
    }

    [Fact]
    public void PositionFieldsAreReadFromEntriesToo()
    {
        var file = Md("""
            ---
            name: x
            fields:
              paid_off: { value: "ash:20", from: "ash:1", status: candidate }
            ---
            """.Replace("\r\n", "\n"));

        var scan = PositionScanner.Scan(file, new HashSet<string> { "paid_off" });

        Assert.Equal([(PositionSite.EntryFrom, "ash:1"), (PositionSite.FieldValue, "ash:20")],
            scan.References.Select(r => (r.Site, r.Text)));
    }
}
