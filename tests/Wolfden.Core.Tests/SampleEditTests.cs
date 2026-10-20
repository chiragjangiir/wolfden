using Wolfden.Core.Documents;
using Wolfden.Core.Text;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Tests;

/// <summary>
/// Targeted edits on real sample files (#18, format.md F-RT-1, F-RT-2,
/// F-RT-4). Each test spells out the whole expected file as the original
/// with only the intended bytes replaced, so a stray change anywhere else
/// (a comment, key order, quoting, another section) fails it.
/// </summary>
/// <remarks>
/// Expected text is written with LF and converted to the file's own line
/// ending, so the tests don't depend on how git checked the samples out.
/// The crafted-text versions of these edits are in <see cref="EditTests"/>,
/// and random edit sequences over every sample are in
/// <see cref="PropertyTests"/>.
/// </remarks>
public class SampleEditTests
{
    private const string Book = "samples/dracula/universes/dracula/books/dracula/";
    private const string Count = Book + "entities/characters/count-dracula.md";
    private const string Lucy = Book + "entities/characters/lucy-westenra.md";
    private const string BookFile = Book + "book.md";
    private const string Den = "samples/dracula/den.yaml";

    private static MarkdownFile Md(string path) => MarkdownFile.Parse(TestFiles.ReadRepoFile(path), requireFrontmatter: true);

    /// <summary>
    /// <paramref name="file"/>'s text with the one occurrence of
    /// <paramref name="oldText"/> replaced by <paramref name="newText"/>,
    /// both written with LF and converted to the file's line ending.
    /// </summary>
    private static string ReplaceOnce(DenFile file, string oldText, string newText)
    {
        var nl = file.LineEndings == LineEndingKind.CrLf ? "\r\n" : "\n";
        oldText = oldText.Replace("\n", nl, StringComparison.Ordinal);
        newText = newText.Replace("\n", nl, StringComparison.Ordinal);
        var at = file.Text.IndexOf(oldText, StringComparison.Ordinal);

        Assert.True(at >= 0, $"The sample no longer contains: {oldText}");
        Assert.True(file.Text.IndexOf(oldText, at + 1, StringComparison.Ordinal) < 0, $"The sample contains more than one: {oldText}");
        return file.Text[..at] + newText + file.Text[(at + oldText.Length)..];
    }

    [Fact]
    [Trait("Scenario", "SC-RT-2")]
    public void ChangingOneFieldKeepsCommentsFlowValuesAndTheOtherFields()
    {
        var file = Md(Count);

        var edited = file.WithField("role", YamlScalarValue.String("The vampire who comes to England."));

        Assert.Equal(ReplaceOnce(file, "  role: The antagonist.\n", "  role: The vampire who comes to England.\n"), edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-2")]
    public void ChangingAQuotedValueKeepsItsQuotes()
    {
        var file = Md(Count);

        var edited = file.WithFrontmatterValue("from", YamlScalarValue.String("dracula:2"));

        Assert.Equal(ReplaceOnce(file, "from: \"dracula:1\"\n", "from: \"dracula:2\"\n"), edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-2")]
    public void ChangingATopLevelValueKeepsTheYamlCommentsAroundIt()
    {
        // book.md has a block of YAML comments between `status` and
        // `chapter_defaults`, and a long `chapters` list after them.
        var file = Md(BookFile);

        var edited = file.WithFrontmatterValue("status", YamlScalarValue.String("canon"));

        Assert.Equal(ReplaceOnce(file, "status: draft\n# Every document", "status: canon\n# Every document"), edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-2")]
    public void ChangingANestedValueChangesOnlyThatValue()
    {
        var file = Md(BookFile);

        var edited = file.WithFrontmatterValue(["chapter_defaults", "tense"], YamlScalarValue.String("present"));

        Assert.Equal(ReplaceOnce(file, "  tense: past\n", "  tense: present\n"), edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-2")]
    public void ChangingDenYamlValuesKeepsTheRest()
    {
        var file = YamlFile.Parse(TestFiles.ReadRepoFile(Den));

        var edited = file
            .WithValue("name", YamlScalarValue.String("Dracula Notes"))
            .WithValue("spoiler_checks", YamlScalarValue.Boolean(false));

        var expected = ReplaceOnce(file, "name: Dracula\n", "name: Dracula Notes\n");
        expected = expected.Replace("spoiler_checks: true", "spoiler_checks: false", StringComparison.Ordinal);
        Assert.Equal(expected, edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-3")]
    public void ANewFieldGoesAfterTheLastFieldAndItsComment()
    {
        // The last field, species, has a YAML comment above it. The new
        // field goes after species, so the comment stays attached to it.
        var file = Md(Count);

        var edited = file.WithField("height", YamlScalarValue.String("tall"));

        Assert.Equal(
            ReplaceOnce(file,
                "  species: { value: \"vampire (Un-Dead)\", from: \"dracula:18\", pin: true }\n---\n",
                "  species: { value: \"vampire (Un-Dead)\", from: \"dracula:18\", pin: true }\n  height: tall\n---\n"),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-3")]
    public void ANewTopLevelKeyGoesAfterTheLongChaptersList()
    {
        var file = Md(BookFile);

        var edited = file.WithFrontmatterValue("x-working-title", YamlScalarValue.String("The Un-Dead"));

        Assert.Equal(
            ReplaceOnce(file,
                "      when: { text: \"seven years later\", sort: [7] }\n---\n",
                "      when: { text: \"seven years later\", sort: [7] }\nx-working-title: The Un-Dead\n---\n"),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-3")]
    public void ANewNestedDenYamlKeyGoesAfterItsSiblings()
    {
        var file = YamlFile.Parse(TestFiles.ReadRepoFile(Den));

        var edited = file.WithValue(["authors", "bram-stoker", "x-born"], YamlScalarValue.String("1847"));

        Assert.Equal(ReplaceOnce(file, "    name: Bram Stoker\n", "    name: Bram Stoker\n    x-born: \"1847\"\n"), edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-4")]
    public void EditingOneOfTwoSameNamedSectionsLeavesTheOtherAlone()
    {
        var file = Md(Count);
        var later = file.Sections.Where(s => s.Name == "Appearance").ElementAt(1);

        var edited = file.WithSectionBody(later, "In London he looks far younger.");

        Assert.Equal(
            ReplaceOnce(file,
                "<!-- wolfden: from=dracula:13 -->\nIn London he looks far younger, with a black moustache and a pointed\nbeard. Jonathan knows him at once.\n",
                "<!-- wolfden: from=dracula:13 -->\nIn London he looks far younger.\n"),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-4")]
    public void EditingASubsectionKeepsItsCommentAndItsParent()
    {
        var file = Md(Lucy);
        var decline = file.Sections.Single(s => s.Name == "Decline");

        var edited = file.WithSectionBody(decline, "She grows pale and thin.\nThe marks on her throat never heal.");

        Assert.Equal(
            ReplaceOnce(file,
                "<!-- wolfden: from=dracula:8 -->\nFrom August on she grows pale and thin, with two small red marks on her\nthroat that never heal.\n",
                "<!-- wolfden: from=dracula:8 -->\nShe grows pale and thin.\nThe marks on her throat never heal.\n"),
            edited.Text);
        Assert.Equal(file.Sections.Single(s => s.Name == "Decline").Parent!.Body, edited.Sections.Single(s => s.Name == "Decline").Parent!.Body);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-4")]
    public void EditingASectionWithAListKeepsTheSectionsAroundIt()
    {
        var file = Md(Lucy);
        var relationships = file.Sections.Single(s => s.Name == "Relationships");

        var edited = file.WithSectionBody(relationships, "- [[mina-harker]]: her closest friend.\n- [[arthur-holmwood]]: her fiancé.");

        Assert.Equal(
            ReplaceOnce(file,
                "- [[mina-harker]]: her closest friend.\n- [[mrs-westenra]]: her mother, whose heart is failing.\n- [[arthur-holmwood]]: her fiancé.\n- [[john-seward]] and [[quincey-morris]]: suitors she refuses on the same\n  day, who stay her friends.\n",
                "- [[mina-harker]]: her closest friend.\n- [[arthur-holmwood]]: her fiancé.\n"),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-5")]
    public void AddingASectionAtTheEndKeepsEverythingBeforeIt()
    {
        var file = Md(Lucy);

        var edited = file.WithNewSection("Open questions", "Who else did the Count visit at Whitby?");

        var nl = file.LineEndings == LineEndingKind.CrLf ? "\r\n" : "\n";
        Assert.Equal(file.Text + nl + "## Open questions" + nl + "Who else did the Count visit at Whitby?" + nl, edited.Text);
        Assert.Equal("Open questions", edited.Sections[^1].Name);
        Assert.Null(edited.Sections[^1].Parent);
    }

    [Theory]
    [Trait("Scenario", "SC-RT-6")]
    [InlineData("crlf")]
    [InlineData("bom-crlf")]
    [InlineData("bom-crlf-no-final-newline")]
    public void EditsOnAWindowsSavedSampleKeepCrlfBomAndFinalNewline(string variant)
    {
        var file = MarkdownFile.Parse(RoundTripTests.Variant(TestFiles.ReadRepoFile(Count), variant), requireFrontmatter: true);
        var later = file.Sections.Where(s => s.Name == "Appearance").ElementAt(1);

        var edited = file
            .WithField("role", YamlScalarValue.String("The vampire who comes to England."))
            .WithField("height", YamlScalarValue.String("tall"));
        edited = edited.WithSectionBody(edited.Sections[later.Index], "In London he looks far younger.\nJonathan knows him.");

        var expected = ReplaceOnce(file, "  role: The antagonist.\n", "  role: The vampire who comes to England.\n");
        expected = expected
            .Replace("pin: true }\r\n---\r\n", "pin: true }\r\n  height: tall\r\n---\r\n", StringComparison.Ordinal)
            .Replace(
                "In London he looks far younger, with a black moustache and a pointed\r\nbeard. Jonathan knows him at once.\r\n",
                "In London he looks far younger.\r\nJonathan knows him.\r\n",
                StringComparison.Ordinal);
        Assert.Equal(expected, edited.Text);
        Assert.Equal(LineEndingKind.CrLf, edited.LineEndings);
        Assert.Equal(file.HasByteOrderMark, edited.HasByteOrderMark);
        Assert.Equal(file.EndsWithNewline, edited.EndsWithNewline);
        Assert.Equal(file.HasByteOrderMark, edited.ToBytes().AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]));
    }

    [Fact]
    public void ChangingAValueInsideAFlowMappingIsRefusedAndChangesNothing()
    {
        // Editing one key of a temporal entry like `died: { value: ..., from: ... }`
        // isn't supported yet (a follow-up to #18). It must be refused, not
        // written as a rewrite of the whole entry.
        var file = Md(Lucy);
        var before = file.ToBytes();

        Assert.Throws<NotSupportedException>(() => file.WithFrontmatterValue(["fields", "died", "from"], YamlScalarValue.String("dracula:13")));
        Assert.Equal(before, file.ToBytes());
    }

    [Fact]
    public void AddingAnItemToAListIsRefused()
    {
        // There's no API yet to add or remove one item of `aliases` or of a
        // temporal list (a follow-up to #18). Treating the list as a mapping
        // is refused.
        var file = Md(Lucy);

        Assert.Throws<InvalidOperationException>(() => file.WithFrontmatterValue(["aliases", "extra"], YamlScalarValue.String("Miss Lucy")));
    }
}
