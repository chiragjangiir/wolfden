using Wolfden.Core.Documents;

namespace Wolfden.Core.Tests;

/// <summary>
/// format.md F-RT-1, F-RT-2: renaming, removing, and moving a section
/// (#97) change only the bytes they must. Each test spells out the whole
/// expected file. The same edits on the samples are in <see cref="SampleEditTests"/>.
/// </summary>
public class SectionEditTests
{
    private const string Entity = """
        ---
        name: Mara Voss
        ---

        # Mara Voss

        ## Appearance
        Silver-streaked hair.

        ## Appearance
        <!-- wolfden: from=ember:19 -->
        Her left arm in a brace.

        ## Shoulder injury ##
        <!-- wolfden: from=ember:19 -->
        Takes a flechette round.

        ### Recovery
        Months of therapy.

        ### Scar
        A pale line.

        ## Notes
        Last.
        """;

    private static MarkdownFile Md(string text) => MarkdownFile.Parse(TestFiles.Utf8(text));

    private static Section Named(MarkdownFile file, string name, int nth = 0) => file.Sections.Where(s => s.Name == name).ElementAt(nth);

    // ---- Rename ----

    [Fact]
    [Trait("Scenario", "SC-RT-11")]
    public void RenamingChangesOnlyTheHeadingText()
    {
        var file = Md(Entity);

        var edited = file.WithSectionName(Named(file, "Appearance", 1), "Appearance in Ember");

        Assert.Equal(Entity.Replace("## Appearance\n<!--", "## Appearance in Ember\n<!--", StringComparison.Ordinal), edited.Text);
        Assert.Equal("Appearance", edited.Sections[0].Name);
    }

    [Theory]
    [Trait("Scenario", "SC-RT-11")]
    [InlineData("## Old ##\ntext\n", "## New ##\ntext\n")]
    [InlineData("##   Old   \ntext\n", "##   New   \ntext\n")]
    [InlineData("## Old <!-- keep -->\ntext\n", "## New <!-- keep -->\ntext\n")]
    [InlineData("Old\n---\ntext\n", "New\n---\ntext\n")]
    [InlineData("Old\nand long\n------\ntext\n", "New\n------\ntext\n")]
    [InlineData("##\ntext\n", "## New\ntext\n")]
    [InlineData("## *Old*\n<!-- wolfden: pin=true -->\n", "## New\n<!-- wolfden: pin=true -->\n")]
    [InlineData("## Old", "## New")]
    public void RenamingKeepsTheHeadingsMarkup(string before, string after)
    {
        var file = Md(before);

        Assert.Equal(after, file.WithSectionName(file.Sections[0], "New").Text);
    }

    [Fact]
    public void RenamingASubsectionKeepsItsParent()
    {
        var file = Md(Entity);

        var edited = file.WithSectionName(Named(file, "Recovery"), "Rehabilitation");

        Assert.Equal(Entity.Replace("### Recovery", "### Rehabilitation", StringComparison.Ordinal), edited.Text);
        Assert.Equal("Shoulder injury", edited.Sections.Single(s => s.Name == "Rehabilitation").Parent!.Name);
    }

    [Theory]
    [InlineData("Act #")]
    [InlineData(" padded")]
    [InlineData("two\nlines")]
    [InlineData("")]
    [InlineData("Name <!-- c -->")]
    public void RenamingToANameThatWouldNotReadBackIsRefused(string name)
    {
        var file = Md(Entity);

        Assert.Throws<ArgumentException>(() => file.WithSectionName(file.Sections[0], name));
    }

    [Fact]
    public void RenamingToTheSameNameChangesNothing()
    {
        var file = Md(Entity);

        Assert.Same(file, file.WithSectionName(file.Sections[0], "Appearance"));
    }

    [Fact]
    public void RenamingASectionOfAnotherFileIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Md("## A\n").WithSectionName(Md("## A\n").Sections[0], "B"));
    }

    // ---- Remove ----

    [Fact]
    [Trait("Scenario", "SC-RT-12")]
    public void RemovingASectionKeepsTheSpacingAroundIt()
    {
        var file = Md(Entity);

        var edited = file.WithoutSection(Named(file, "Appearance", 1));

        Assert.Equal(Entity.Replace("## Appearance\n<!-- wolfden: from=ember:19 -->\nHer left arm in a brace.\n\n", "", StringComparison.Ordinal), edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-12")]
    public void RemovingASectionRemovesItsSubsections()
    {
        var file = Md(Entity);

        var edited = file.WithoutSection(Named(file, "Shoulder injury"));

        Assert.Equal(
            Entity.Replace("## Shoulder injury ##\n<!-- wolfden: from=ember:19 -->\nTakes a flechette round.\n\n### Recovery\nMonths of therapy.\n\n### Scar\nA pale line.\n\n", "", StringComparison.Ordinal),
            edited.Text);
        Assert.Equal(["Appearance", "Appearance", "Notes"], edited.Sections.Select(s => s.Name));
    }

    [Fact]
    public void RemovingASubsectionKeepsItsSiblingsAndParent()
    {
        var file = Md(Entity);

        var edited = file.WithoutSection(Named(file, "Recovery"));

        Assert.Equal(Entity.Replace("### Recovery\nMonths of therapy.\n\n", "", StringComparison.Ordinal), edited.Text);
    }

    [Theory]
    [Trait("Scenario", "SC-RT-12")]
    [InlineData("## A\na\n\n## B\nb\n", "## A\na\n")]
    [InlineData("## A\na\n\n\n## B\nb", "## A\na")]
    [InlineData("## A\na\n## B", "## A\na")]
    [InlineData("---\nname: x\n---\n\n## B\nb\n", "---\nname: x\n---\n")]
    [InlineData("---\nname: x\n---\n## B", "---\nname: x\n---\n")]
    [InlineData("# Title\n\nLead.\n\n## B\nb\n", "# Title\n\nLead.\n")]
    [InlineData("## B\nb\n", "")]
    public void RemovingTheLastSectionRemovesTheBlankLinesBeforeIt(string before, string after)
    {
        var file = Md(before);

        Assert.Equal(after, file.WithoutSection(file.Sections[^1]).Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-14")]
    public void RemovingASectionThatWouldMergeTheTextAroundItIsRefused()
    {
        // Without `## B`, "text" would run into the setext heading C and
        // become part of its name.
        var file = Md("## A\ntext\n## B\n\nC\n---\n");

        Assert.Throws<NotSupportedException>(() => file.WithoutSection(file.Sections[1]));
    }

    // ---- Move ----

    [Fact]
    [Trait("Scenario", "SC-RT-13")]
    public void MovingASectionKeepsItsTextAndTheSpacingBetweenSections()
    {
        var file = Md("## A\na\n\n## B\nb\n\n\n## C\nc\n");

        var edited = file.WithSectionMovedBefore(file.Sections[2], file.Sections[0]);

        Assert.Equal("## C\nc\n\n## A\na\n\n\n## B\nb\n", edited.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-13")]
    public void MovingTheLastSectionKeepsTheFilesEnding()
    {
        var file = Md(Entity);

        var edited = file.WithSectionMovedBefore(Named(file, "Notes"), Named(file, "Shoulder injury"));

        Assert.Equal(
            Entity.Replace("## Shoulder injury ##\n<!-- wolfden: from=ember:19 -->\nTakes a flechette round.\n\n### Recovery\nMonths of therapy.\n\n### Scar\nA pale line.\n\n## Notes\nLast.",
                "## Notes\nLast.\n\n## Shoulder injury ##\n<!-- wolfden: from=ember:19 -->\nTakes a flechette round.\n\n### Recovery\nMonths of therapy.\n\n### Scar\nA pale line.", StringComparison.Ordinal),
            edited.Text);
        Assert.False(edited.EndsWithNewline);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-13")]
    public void MovingASectionToTheEndTakesItsSubsections()
    {
        var file = Md(Entity);

        var edited = file.WithSectionMovedBefore(Named(file, "Shoulder injury"), null);

        Assert.Equal(
            Entity.Replace("## Shoulder injury ##\n<!-- wolfden: from=ember:19 -->\nTakes a flechette round.\n\n### Recovery\nMonths of therapy.\n\n### Scar\nA pale line.\n\n## Notes\nLast.",
                "## Notes\nLast.\n\n## Shoulder injury ##\n<!-- wolfden: from=ember:19 -->\nTakes a flechette round.\n\n### Recovery\nMonths of therapy.\n\n### Scar\nA pale line.", StringComparison.Ordinal),
            edited.Text);
        Assert.Equal("Shoulder injury", edited.Sections.Single(s => s.Name == "Scar").Parent!.Name);
    }

    [Fact]
    public void MovingASubsectionAmongItsSiblings()
    {
        var file = Md(Entity);

        var edited = file.WithSectionMovedBefore(Named(file, "Scar"), Named(file, "Recovery"));

        Assert.Equal(Entity.Replace("### Recovery\nMonths of therapy.\n\n### Scar\nA pale line.\n", "### Scar\nA pale line.\n\n### Recovery\nMonths of therapy.\n", StringComparison.Ordinal), edited.Text);
    }

    [Fact]
    public void MovingOneOfTwoSameNamedSectionsMovesThatOne()
    {
        var file = Md(Entity);

        var edited = file.WithSectionMovedBefore(Named(file, "Appearance", 1), Named(file, "Appearance", 0));

        Assert.Equal(
            Entity.Replace("## Appearance\nSilver-streaked hair.\n\n## Appearance\n<!-- wolfden: from=ember:19 -->\nHer left arm in a brace.\n",
                "## Appearance\n<!-- wolfden: from=ember:19 -->\nHer left arm in a brace.\n\n## Appearance\nSilver-streaked hair.\n", StringComparison.Ordinal),
            edited.Text);
    }

    [Fact]
    public void MovingASectionWhereItAlreadyIsChangesNothing()
    {
        var file = Md(Entity);

        Assert.Same(file, file.WithSectionMovedBefore(file.Sections[0], file.Sections[0]));
        Assert.Same(file, file.WithSectionMovedBefore(file.Sections[0], file.Sections[1]));
        Assert.Same(file, file.WithSectionMovedBefore(Named(file, "Notes"), null));
    }

    [Fact]
    public void MovingASectionOutsideItsSiblingsIsRefused()
    {
        var file = Md(Entity);

        Assert.Throws<ArgumentException>(() => file.WithSectionMovedBefore(Named(file, "Recovery"), Named(file, "Notes")));
        Assert.Throws<ArgumentException>(() => file.WithSectionMovedBefore(Named(file, "Notes"), Named(file, "Recovery")));
        Assert.Throws<ArgumentException>(() => file.WithSectionMovedBefore(file.Sections[0], Md(Entity).Sections[1]));
    }

    [Fact]
    [Trait("Scenario", "SC-RT-14")]
    public void MovingASectionThatWouldMergeWithTheTextBeforeItIsRefused()
    {
        // C is a setext heading. After A's "text" with no blank line, it
        // would read as part of a paragraph-turned-heading.
        var file = Md("## A\ntext\n## B\nb\n\nC\n---\nc\n");

        Assert.Throws<NotSupportedException>(() => file.WithSectionMovedBefore(file.Sections[2], file.Sections[1]));
    }

    // ---- Review round 1: refusals at the lead, and link references (M1, L1) ----

    [Fact]
    public void RenamingAHeadingIntoSomethingElseIsRefused()
    {
        // `- x` over `---` is a list item and a thematic break, not a heading.
        var file = Md("Old\n---\ntext\n");

        Assert.Throws<ArgumentException>(() => file.WithSectionName(file.Sections[0], "- x"));
    }

    [Fact]
    [Trait("Scenario", "SC-RT-14")]
    public void RemovingASectionThatWouldMakeAStrayHeadingPartOfTheLeadIsRefused()
    {
        var file = Md("# Title\n\n## A\na\n\n# Stray\ns\n");

        Assert.Throws<NotSupportedException>(() => file.WithoutSection(file.Sections[0]));
    }

    [Fact]
    [Trait("Scenario", "SC-RT-14")]
    public void MovingASetextSectionAgainstTheLeadIsRefused()
    {
        var file = Md("Lead.\n## A\na\n\nB\n---\nb\n");

        Assert.Throws<NotSupportedException>(() => file.WithSectionMovedBefore(file.Sections[1], file.Sections[0]));
    }

    [Fact]
    [Trait("Scenario", "SC-RT-14")]
    public void RemovingASectionWithALinkReferenceDefinitionIsRefused()
    {
        var file = Md("## A\nSee [the map][map].\n\n## B\n[map]: https://example.com/map\n\n## C\nc\n");

        Assert.Throws<NotSupportedException>(() => file.WithoutSection(file.Sections[1]));
        Assert.Equal("## A\nSee [the map][map].\n\n## B\n[map]: https://example.com/map\n", file.WithoutSection(file.Sections[2]).Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-14")]
    public void MovingSectionsThatDefineTheSameLinkReferenceIsRefused()
    {
        // The first [map] wins. Swapping A and B would change where links go.
        var file = Md("## A\n[map]: https://example.com/a\n\n## B\n[MAP]: https://example.com/b\n\n## C\nSee [the map][map].\n");

        Assert.Throws<NotSupportedException>(() => file.WithSectionMovedBefore(file.Sections[1], file.Sections[0]));
    }

    [Fact]
    public void MovingSectionsWithDifferentLinkReferencesIsFine()
    {
        var file = Md("## A\n[a]: https://example.com/a\n\n## B\n[b]: https://example.com/b\n");

        Assert.Equal("## B\n[b]: https://example.com/b\n\n## A\n[a]: https://example.com/a\n", file.WithSectionMovedBefore(file.Sections[1], file.Sections[0]).Text);
    }

    [Fact]
    public void AReferenceDefinitionInsideCodeDoesNotBlockRemoval()
    {
        var file = Md("## A\na\n\n## B\n```\n[map]: https://example.com/map\n```\n\n## C\nc\n");

        Assert.Equal("## A\na\n\n## C\nc\n", file.WithoutSection(file.Sections[1]).Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-6")]
    public void SectionEditsKeepCrlf()
    {
        var file = MarkdownFile.Parse(TestFiles.Utf8(Entity.Replace("\n", "\r\n", StringComparison.Ordinal)));

        var edited = file.WithSectionName(Named(file, "Notes"), "Open questions");
        edited = edited.WithSectionMovedBefore(Named(edited, "Open questions"), edited.Sections[0]);
        edited = edited.WithoutSection(Named(edited, "Recovery"));

        var expected = Entity
            .Replace("## Appearance\nSilver-streaked hair.", "## Open questions\nLast.\n\n## Appearance\nSilver-streaked hair.", StringComparison.Ordinal)
            .Replace("### Recovery\nMonths of therapy.\n\n", "", StringComparison.Ordinal)
            .Replace("\n\n## Notes\nLast.", "", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Equal(expected, edited.Text);
        Assert.Equal(Text.LineEndingKind.CrLf, edited.LineEndings);
    }
}
