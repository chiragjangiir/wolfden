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
/// <see cref="CollectionEditTests"/>, and <see cref="SectionEditTests"/>,
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
    private static string ReplaceOnce(DenFile file, string oldText, string newText) =>
        ReplaceOnce(file.Text, Newline(file), oldText, newText);

    /// <summary>The same for <paramref name="text"/>, whose line ending is <paramref name="nl"/>.</summary>
    private static string ReplaceOnce(string text, string nl, string oldText, string newText)
    {
        oldText = oldText.Replace("\n", nl, StringComparison.Ordinal);
        newText = newText.Replace("\n", nl, StringComparison.Ordinal);
        var at = text.IndexOf(oldText, StringComparison.Ordinal);

        Assert.True(at >= 0, $"The sample no longer contains: {oldText}");
        Assert.True(text.IndexOf(oldText, at + 1, StringComparison.Ordinal) < 0, $"The sample contains more than one: {oldText}");
        return text[..at] + newText + text[(at + oldText.Length)..];
    }

    private static string Newline(DenFile file) => file.LineEndings == LineEndingKind.CrLf ? "\r\n" : "\n";

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
        expected = ReplaceOnce(expected, Newline(file), "spoiler_checks: true\n", "spoiler_checks: false\n");
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

        var nl = Newline(file);
        Assert.Equal(file.Text + nl + "## Open questions" + nl + "Who else did the Count visit at Whitby?" + nl, edited.Text);
        Assert.Equal("Open questions", edited.Sections[^1].Name);
        Assert.Null(edited.Sections[^1].Parent);
    }

    [Theory]
    [Trait("Scenario", "SC-RT-6")]
    [InlineData("crlf")]
    [InlineData("bom-crlf")]
    [InlineData("crlf-no-final-newline")]
    [InlineData("bom-crlf-no-final-newline")]
    public void EditsOnAWindowsSavedSampleKeepCrlfBomAndFinalNewline(string variant)
    {
        var file = MarkdownFile.Parse(RoundTripTests.Variant(TestFiles.ReadRepoFile(Count), variant), requireFrontmatter: true);
        var later = file.Sections.Where(s => s.Name == "Appearance").ElementAt(1);

        var edited = file
            .WithField("role", YamlScalarValue.String("The vampire who comes to England."))
            .WithField("height", YamlScalarValue.String("tall"));
        edited = edited
            .WithSectionBody(edited.Sections[later.Index], "In London he looks far younger.\nJonathan knows him.")
            .WithNewSection("Open questions", "Where are the last boxes?\nWho moved them?");

        const string nl = "\r\n";
        var expected = ReplaceOnce(file, "  role: The antagonist.\n", "  role: The vampire who comes to England.\n");
        expected = ReplaceOnce(expected, nl, "pin: true }\n---\n", "pin: true }\n  height: tall\n---\n");
        expected = ReplaceOnce(expected, nl,
            "In London he looks far younger, with a black moustache and a pointed\nbeard. Jonathan knows him at once.\n",
            "In London he looks far younger.\nJonathan knows him.\n");
        // An appended section starts after a blank line and ends the way the
        // file did: with CRLF, or with no final newline at all.
        expected += file.EndsWithNewline
            ? nl + "## Open questions" + nl + "Where are the last boxes?" + nl + "Who moved them?" + nl
            : nl + nl + "## Open questions" + nl + "Where are the last boxes?" + nl + "Who moved them?";
        Assert.Equal(expected, edited.Text);
        Assert.Equal(LineEndingKind.CrLf, edited.LineEndings);
        Assert.Equal(file.HasByteOrderMark, edited.HasByteOrderMark);
        Assert.Equal(file.EndsWithNewline, edited.EndsWithNewline);
        Assert.Equal(file.HasByteOrderMark, edited.ToBytes().AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]));
    }

    [Fact]
    [Trait("Scenario", "SC-RT-14")]
    public void TreatingAListAsAMappingIsRefusedAndChangesNothing()
    {
        // In a list, a path segment is an item number. A key is refused, and
        // the file is left as it was.
        var file = Md(Lucy);
        var before = file.ToBytes();

        Assert.Throws<InvalidOperationException>(() => file.WithFrontmatterValue(["aliases", "extra"], YamlScalarValue.String("Miss Lucy")));
        Assert.Equal(before, file.ToBytes());
    }

    [Fact]
    [Trait("Scenario", "SC-RT-7")]
    public void AnAliasGoesAtTheEndOfTheFlowList()
    {
        var file = Md(Lucy);

        var edited = file.WithNewListItem(["aliases"], YamlScalarValue.String("Miss Lucy"));

        Assert.Equal(ReplaceOnce(file, "aliases: [Lucy]\n", "aliases: [Lucy, Miss Lucy]\n"), edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-7")]
    public void ANewTemporalEntryGoesAfterTheLastOneInItsStyle()
    {
        var file = Md(Lucy);
        var entry = new YamlMappingValue
        {
            { "value", YamlScalarValue.String("Remembered by her friends.") },
            { "from", YamlScalarValue.String("dracula:27") },
        };

        var edited = file.WithNewListItem(["fields", "role"], entry);

        Assert.Equal(
            ReplaceOnce(file,
                "    - { value: At peace., from: \"dracula:16\" }\n",
                "    - { value: At peace., from: \"dracula:16\" }\n    - { value: Remembered by her friends., from: \"dracula:27\" }\n"),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-7")]
    public void ANestedBlockListGetsANewItemAtItsIndentAndQuoting()
    {
        var file = Md(BookFile);
        var chapters = (YamlSequence)file.GetFrontmatterValue("chapters")!;
        var last = chapters.Items.Select((c, i) => (c, i)).Single(x => (x.c as YamlMapping)?["key"]?.ToString() == "27").i;

        var edited = file.WithNewListItem(["chapters", last.ToString(System.Globalization.CultureInfo.InvariantCulture), "fields", "pov"], YamlScalarValue.String("[[quincey-morris]]"));

        Assert.Equal(
            ReplaceOnce(file,
                "        - \"[[john-seward]]\"\n      when: { text: \"1–6 November\"",
                "        - \"[[john-seward]]\"\n        - \"[[quincey-morris]]\"\n      when: { text: \"1–6 November\""),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-7")]
    public void ANewChapterIsABlockMappingLikeTheOthers()
    {
        var file = Md(BookFile);
        var chapter = new YamlMappingValue { { "key", YamlScalarValue.String("postscript") }, { "title", YamlScalarValue.String("Postscript") } };

        var edited = file.WithNewListItem(["chapters"], chapter);

        Assert.Equal(
            ReplaceOnce(file,
                "      when: { text: \"seven years later\", sort: [7] }\n---\n",
                "      when: { text: \"seven years later\", sort: [7] }\n  - key: postscript\n    title: Postscript\n---\n"),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-8")]
    public void RemovingAnAliasKeepsTheOthers()
    {
        var file = Md(Count);

        var edited = file.WithoutFrontmatterValue(["aliases", "1"]);

        Assert.Equal(ReplaceOnce(file, "aliases: [Dracula, the Count, Count de Ville]\n", "aliases: [Dracula, Count de Ville]\n"), edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-8")]
    public void RemovingATemporalEntryRemovesOnlyItsLine()
    {
        var file = Md(Lucy);

        var edited = file.WithoutFrontmatterValue(["fields", "role", "1"]);

        Assert.Equal(ReplaceOnce(file, "    - { value: The Count's first victim in England., from: \"dracula:8\" }\n", ""), edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-9")]
    public void ChangingWhenAFactIsKnownChangesOnlyItsFrom()
    {
        var file = Md(Lucy);

        var edited = file.WithFrontmatterValue(["fields", "died", "from"], YamlScalarValue.String("dracula:13"));

        Assert.Equal(
            ReplaceOnce(file, "sort: [0, 9, 20] }, from: \"dracula:12\" }\n", "sort: [0, 9, 20] }, from: \"dracula:13\" }\n"),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-9")]
    public void UnpinningAFactKeepsTheCommentAboveIt()
    {
        var file = Md(Count);

        var edited = file
            .WithFrontmatterValue(["fields", "species", "pin"], YamlScalarValue.Boolean(false))
            .WithFrontmatterValue(["fields", "born", "status"], YamlScalarValue.String("candidate"));

        var expected = ReplaceOnce(file, "from: \"dracula:18\", pin: true }\n", "from: \"dracula:18\", pin: false }\n");
        expected = ReplaceOnce(expected, Newline(file),
            "  born: { value: { text: \"centuries before the story\" }, from: \"dracula:18\" }\n",
            "  born: { value: { text: \"centuries before the story\" }, from: \"dracula:18\", status: candidate }\n");
        Assert.Equal(expected, edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-10")]
    public void RemovingAFieldRemovesOnlyItsLine()
    {
        var file = Md(Count);

        var edited = file.WithoutField("skills").WithoutField("born");

        var expected = ReplaceOnce(file, "  skills: [command of wolves and rats, English learned from books]\n", "");
        expected = ReplaceOnce(expected, Newline(file), "  born: { value: { text: \"centuries before the story\" }, from: \"dracula:18\" }\n", "");
        Assert.Equal(expected, edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-10")]
    public void RemovingADenYamlKeyKeepsTheRest()
    {
        var file = YamlFile.Parse(TestFiles.ReadRepoFile(Den));

        var edited = file.WithoutValue(["spoiler_checks"]);

        Assert.Equal(ReplaceOnce(file, "spoiler_checks: true\n", ""), edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-11")]
    public void RenamingOneOfThreeSameNamedSectionsLeavesTheOthersAlone()
    {
        var file = Md(Lucy);
        var second = file.Sections.Where(s => s.Name == "Appearance").ElementAt(1);

        var edited = file.WithSectionName(second, "Appearance in death");

        Assert.Equal(
            ReplaceOnce(file, "## Appearance\n<!-- wolfden: from=dracula:15 -->\n", "## Appearance in death\n<!-- wolfden: from=dracula:15 -->\n"),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-12")]
    public void RemovingASubsectionKeepsItsParentAndSiblings()
    {
        var file = Md(Lucy);

        var edited = file.WithoutSection(file.Sections.Single(s => s.Name == "The Un-Dead"));

        Assert.Equal(
            ReplaceOnce(file,
                "### The Un-Dead\n<!-- wolfden: from=dracula:14 until=dracula:17 -->\nChildren on Hampstead Heath are lured away by a \"bloofer lady\" and found\nwith small wounds in their throats. See [[the-bloofer-lady]].\n\n",
                ""),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-12")]
    public void RemovingTheLastSectionKeepsTheFileEnding()
    {
        var file = Md(Lucy);

        var edited = file.WithoutSection(file.Sections.Single(s => s.Name == "Rest"));

        Assert.Equal(
            ReplaceOnce(file,
                "\n\n### Rest\n<!-- wolfden: from=dracula:16 -->\nArthur drives the stake on 29 September. See [[lucy-is-laid-to-rest]].\n",
                "\n"),
            edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-13")]
    public void MovingASectionKeepsItsCommentAndTheSpacing()
    {
        var file = Md(Lucy);

        var edited = file.WithSectionMovedBefore(file.Sections.Single(s => s.Name == "Relationships"), file.Sections.Single(s => s.Name == "Want"));

        var want = "## Want\nTo marry [[arthur-holmwood|Arthur]], and to be kind to the two men she\nrefuses.\n";
        var relationships = "## Relationships\n- [[mina-harker]]: her closest friend.\n- [[mrs-westenra]]: her mother, whose heart is failing.\n- [[arthur-holmwood]]: her fiancé.\n- [[john-seward]] and [[quincey-morris]]: suitors she refuses on the same\n  day, who stay her friends.\n";
        Assert.Equal(ReplaceOnce(file, want + "\n" + relationships, relationships + "\n" + want), edited.Text);
    }

    [Theory]
    [Trait("Scenario", "SC-RT-6")]
    [InlineData("crlf")]
    [InlineData("bom-crlf")]
    [InlineData("crlf-no-final-newline")]
    [InlineData("bom-crlf-no-final-newline")]
    public void ListKeyAndSectionEditsOnAWindowsSavedSampleKeepCrlfBomAndFinalNewline(string variant)
    {
        var file = MarkdownFile.Parse(RoundTripTests.Variant(TestFiles.ReadRepoFile(Lucy), variant), requireFrontmatter: true);
        var entry = new YamlMappingValue { { "value", YamlScalarValue.String("Remembered.") }, { "from", YamlScalarValue.String("dracula:27") } };

        var edited = file
            .WithNewListItem(["aliases"], YamlScalarValue.String("Miss Lucy"))
            .WithNewListItem(["fields", "role"], entry)
            .WithFrontmatterValue(["fields", "died", "from"], YamlScalarValue.String("dracula:13"))
            .WithoutField("heritage");
        edited = edited.WithSectionName(edited.Sections.Single(s => s.Name == "Want"), "Wants");
        // Arc is the last level-2 section, with subsections, at the end of
        // the file: it moves up, and Relationships now ends the file.
        edited = edited.WithSectionMovedBefore(edited.Sections.Single(s => s.Name == "Arc"), edited.Sections.Single(s => s.Name == "Wants"));

        const string nl = "\r\n";
        var expected = ReplaceOnce(file, "aliases: [Lucy]\n", "aliases: [Lucy, Miss Lucy]\n");
        expected = ReplaceOnce(expected, nl, "from: \"dracula:16\" }\n", "from: \"dracula:16\" }\n    - { value: Remembered., from: \"dracula:27\" }\n");
        expected = ReplaceOnce(expected, nl, "from: \"dracula:12\" }\n", "from: \"dracula:13\" }\n");
        expected = ReplaceOnce(expected, nl, "  heritage: The only child of a wealthy widow of Hampstead.\n", "");
        var arcAt = expected.IndexOf("## Arc", StringComparison.Ordinal);
        var arc = expected[arcAt..].TrimEnd('\r', '\n');
        var wantAt = expected.IndexOf("## Want", StringComparison.Ordinal);
        var between = expected[wantAt..arcAt].TrimEnd('\r', '\n');
        expected = expected[..wantAt] + arc + nl + nl + between.Replace("## Want", "## Wants", StringComparison.Ordinal) + (file.EndsWithNewline ? nl : "");
        Assert.Equal(expected, edited.Text);
        Assert.Equal(LineEndingKind.CrLf, edited.LineEndings);
        Assert.Equal(file.HasByteOrderMark, edited.HasByteOrderMark);
        Assert.Equal(file.EndsWithNewline, edited.EndsWithNewline);
    }
}
