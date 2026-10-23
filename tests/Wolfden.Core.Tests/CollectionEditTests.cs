using Wolfden.Core.Documents;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Tests;

/// <summary>
/// format.md F-RT-1, F-RT-2: adding and removing one list item, editing one
/// key of a flow mapping, and removing a key (#95, #96, #97) change only
/// the bytes they must. Each test spells out the whole expected
/// frontmatter. The same edits on the samples are in <see cref="SampleEditTests"/>.
/// </summary>
public class CollectionEditTests
{
    private static MarkdownFile Md(string frontmatter) => MarkdownFile.Parse(TestFiles.Utf8("---\n" + frontmatter + "---\nBody.\n"));

    private static string Fm(MarkdownFile file) => file.FrontmatterText["---\n".Length..^"---\n".Length];

    private static YamlScalarValue S(string text) => YamlScalarValue.String(text);

    private static YamlMappingValue Entry(string value, string from, string? status = null)
    {
        var entry = new YamlMappingValue { { "value", S(value) }, { "from", S(from) } };
        if (status is not null)
        {
            entry.Add("status", S(status));
        }

        return entry;
    }

    // ---- #95: adding a list item ----

    [Theory]
    [Trait("Scenario", "SC-RT-7")]
    [InlineData("aliases: [Voss]\n", "aliases: [Voss, Mara]\n")]
    [InlineData("aliases: [Voss, Halden]  # surnames\n", "aliases: [Voss, Halden, Mara]  # surnames\n")]
    [InlineData("aliases: [Voss,Halden]\n", "aliases: [Voss,Halden,Mara]\n")]
    [InlineData("aliases: [ Voss, Halden ]\n", "aliases: [ Voss, Halden, Mara ]\n")]
    [InlineData("aliases: [Voss, Halden,]\n", "aliases: [Voss, Halden, Mara,]\n")]
    [InlineData("aliases: []\n", "aliases: [Mara]\n")]
    [InlineData("aliases: [ ]\n", "aliases: [ Mara ]\n")]
    [InlineData("aliases: ['Voss']\n", "aliases: ['Voss', 'Mara']\n")]
    [InlineData("aliases: [\"the Commander\"]\n", "aliases: [\"the Commander\", \"Mara\"]\n")]
    public void AnItemGoesAtTheEndOfAFlowList(string before, string after)
    {
        var edited = Md(before).WithNewListItem(["aliases"], S("Mara"));

        Assert.Equal(after, Fm(edited));
    }

    [Theory]
    [InlineData(0, "aliases: [Mara, Voss, Halden]\n")]
    [InlineData(1, "aliases: [Voss, Mara, Halden]\n")]
    [InlineData(2, "aliases: [Voss, Halden, Mara]\n")]
    public void AnItemCanGoAnywhereInAFlowList(int index, string after)
    {
        var edited = Md("aliases: [Voss, Halden]\n").WithNewListItem(["aliases"], S("Mara"), index);

        Assert.Equal(after, Fm(edited));
    }

    [Theory]
    [InlineData("a, b", "\"a, b\"")]
    [InlineData("ember:22", "\"ember:22\"")]
    [InlineData("[[velari]]", "\"[[velari]]\"")]
    [InlineData("{x}", "\"{x}\"")]
    [InlineData("2291", "\"2291\"")]
    public void FlowItemsAreQuotedWhenFlowSyntaxWouldMisreadThem(string value, string written)
    {
        var edited = Md("aliases: [Voss]\n").WithNewListItem(["aliases"], S(value));

        Assert.Equal($"aliases: [Voss, {written}]\n", Fm(edited));
        Assert.Equal(value, ((YamlSequence)edited.GetFrontmatterValue("aliases")!).Items[1].ToString());
    }

    private const string MultiLineFlow = """
        aliases: [
          Voss,  # surname
          Halden
        ]
        name: Mara

        """;

    [Theory]
    [InlineData(3, "aliases: [\n  Voss,  # surname\n  Halden,\n  Mara\n]\nname: Mara\n")]
    [InlineData(1, "aliases: [\n  Voss,  # surname\n  Mara,\n  Halden\n]\nname: Mara\n")]
    [InlineData(0, "aliases: [\n  Mara,\n  Voss,  # surname\n  Halden\n]\nname: Mara\n")]
    public void AFlowListWithOneItemPerLineGetsANewLine(int index, string after)
    {
        var file = Md(MultiLineFlow);

        var edited = file.WithNewListItem(["aliases"], S("Mara"), index == 3 ? null : index);

        Assert.Equal(after, Fm(edited));
    }

    [Fact]
    public void AMultiLineFlowListWithATrailingCommaKeepsIt()
    {
        var edited = Md("aliases: [\n  Voss,\n  Halden,  # last\n]\n").WithNewListItem(["aliases"], S("Mara"));

        Assert.Equal("aliases: [\n  Voss,\n  Halden,  # last\n  Mara,\n]\n", Fm(edited));
    }

    private const string BlockList = """
        aliases:
          - Voss
          # the rank she earns in Ember
          - the Commander  # since ember:22
        # notes on the name follow
        name: Mara

        """;

    [Theory]
    [Trait("Scenario", "SC-RT-7")]
    [InlineData(null, "aliases:\n  - Voss\n  # the rank she earns in Ember\n  - the Commander  # since ember:22\n  - Mara\n# notes on the name follow\nname: Mara\n")]
    [InlineData(1, "aliases:\n  - Voss\n  - Mara\n  # the rank she earns in Ember\n  - the Commander  # since ember:22\n# notes on the name follow\nname: Mara\n")]
    [InlineData(0, "aliases:\n  - Mara\n  - Voss\n  # the rank she earns in Ember\n  - the Commander  # since ember:22\n# notes on the name follow\nname: Mara\n")]
    public void AnItemInABlockListGetsItsOwnLineAfterTheItemBeforeIt(int? index, string after)
    {
        var edited = Md(BlockList).WithNewListItem(["aliases"], S("Mara"), index);

        Assert.Equal(after, Fm(edited));
    }

    [Theory]
    [InlineData("aliases:\n- Voss\nname: x\n", "aliases:\n- Voss\n- Mara\nname: x\n")]
    [InlineData("aliases:\n    -   Voss\nname: x\n", "aliases:\n    -   Voss\n    -   Mara\nname: x\n")]
    [InlineData("aliases:\n  - 'Voss'\n", "aliases:\n  - 'Voss'\n  - 'Mara'\n")]
    [InlineData("aliases:\n  -\n", "aliases:\n  -\n  - Mara\n")]
    public void ABlockItemCopiesTheListsIndentDashSpacingAndQuoting(string before, string after)
    {
        Assert.Equal(after, Fm(Md(before).WithNewListItem(["aliases"], S("Mara"))));
    }

    private const string Temporal = """
        fields:
          rank:
            - { value: Lieutenant, from: "ember:1" }
            - { value: Commander, from: "ember:22" }  # promoted
          born: "2291"

        """;

    [Fact]
    [Trait("Scenario", "SC-RT-7")]
    public void ANewTemporalEntryCopiesItsNeighborsStyle()
    {
        var edited = Md(Temporal).WithNewListItem(["fields", "rank"], Entry("Captain", "ash:30", "candidate"));

        Assert.Equal(
            Temporal.Replace("# promoted\n", "# promoted\n    - { value: Captain, from: \"ash:30\", status: candidate }\n", StringComparison.Ordinal),
            Fm(edited));
    }

    [Fact]
    public void ANewEntryInACompactFlowMappingStaysCompact()
    {
        var edited = Md("rank:\n  - {value: Lieutenant, from: 'ember:1'}\n").WithNewListItem(["rank"], Entry("Commander", "ember:22"));

        Assert.Equal("rank:\n  - {value: Lieutenant, from: 'ember:1'}\n  - {value: Commander, from: 'ember:22'}\n", Fm(edited));
    }

    [Fact]
    public void ANewItemInAListOfBlockMappingsIsABlockMapping()
    {
        var text = "chapters:\n  - key: 1\n    title: \"Arrival\"\n  - key: 2\n    title: \"The Reach\"\nname: Ember\n";

        var mapping = new YamlMappingValue { { "key", YamlScalarValue.Integer(3) }, { "title", S("Aftermath") }, { "pov", YamlScalarValue.Null } };
        var edited = Md(text).WithNewListItem(["chapters"], mapping);

        Assert.Equal(text.Replace("The Reach\"\n", "The Reach\"\n  - key: 3\n    title: \"Aftermath\"\n    pov:\n", StringComparison.Ordinal), Fm(edited));
    }

    [Fact]
    public void ANestedValueInANewEntryIsWrittenFlow()
    {
        var date = new YamlMappingValue
        {
            { "text", S("20 September") },
            { "sort", new YamlSequenceValue { YamlScalarValue.Integer(0), YamlScalarValue.Integer(9), YamlScalarValue.Integer(20) } },
        };
        var entry = new YamlMappingValue { { "value", date }, { "from", S("a1:12") } };

        var edited = Md("died:\n  - { value: ~, from: \"a1:1\" }\n").WithNewListItem(["died"], entry);

        Assert.Equal("died:\n  - { value: ~, from: \"a1:1\" }\n  - { value: { text: 20 September, sort: [0, 9, 20] }, from: \"a1:12\" }\n", Fm(edited));
    }

    [Theory]
    [InlineData("aliases:\nname: x\n", "aliases: [Mara]\nname: x\n")]
    [InlineData("aliases: ~  # none yet\nname: x\n", "aliases: [Mara]  # none yet\nname: x\n")]
    [InlineData("name: x\n", "name: x\naliases: [Mara]\n")]
    public void AMissingOrNullListBecomesAFlowList(string before, string after)
    {
        Assert.Equal(after, Fm(Md(before).WithNewListItem(["aliases"], S("Mara"))));
    }

    [Theory]
    [InlineData("fields:\n  born: \"2291\"\n", "fields:\n  born: \"2291\"\n  rank:\n    - { value: Captain, from: \"ash:30\" }\n")]
    [InlineData("fields:\n  rank:   # none yet\n  born: \"2291\"\n", "fields:\n  rank:   # none yet\n    - { value: Captain, from: \"ash:30\" }\n  born: \"2291\"\n")]
    [InlineData("name: x\n", "name: x\nfields:\n  rank:\n    - { value: Captain, from: \"ash:30\" }\n")]
    public void AMissingTemporalListBecomesABlockListOfEntries(string before, string after)
    {
        Assert.Equal(after, Fm(Md(before).WithNewListItem(["fields", "rank"], Entry("Captain", "ash:30"))));
    }

    [Fact]
    public void AListInsideAFlowMappingCanGrow()
    {
        var edited = Md("x-tags: { value: [a, b], from: \"a1:1\" }\n").WithNewListItem(["x-tags", "value"], S("c"));

        Assert.Equal("x-tags: { value: [a, b, c], from: \"a1:1\" }\n", Fm(edited));
    }

    [Fact]
    public void AFileWithNoFrontmatterGetsSomeForItsFirstAlias()
    {
        var edited = MarkdownFile.Parse(TestFiles.Utf8("# Mara\n")).WithNewListItem(["aliases"], S("Voss"));

        Assert.Equal("---\naliases: [Voss]\n---\n# Mara\n", edited.Text);
    }

    [Fact]
    public void AddingToSomethingThatIsNotAListIsRefused()
    {
        var file = Md("name: Mara\naliases: [Voss]\nfields: { a: 1 }\n");

        Assert.Throws<InvalidOperationException>(() => file.WithNewListItem(["name"], S("x")));
        Assert.Throws<InvalidOperationException>(() => file.WithNewListItem(["fields"], S("x")));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.WithNewListItem(["aliases"], S("x"), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.WithNewListItem(["aliases"], S("x"), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.WithNewListItem(["x-new"], S("x"), 1));
        Assert.Throws<ArgumentException>(() => file.WithNewListItem([], S("x")));
    }

    [Fact]
    public void ACommentInsideAnEmptyFlowListIsRefused()
    {
        var file = Md("aliases: [  # none yet\n]\n");

        Assert.Throws<NotSupportedException>(() => file.WithNewListItem(["aliases"], S("x")));
    }

    // ---- #95: removing a list item ----

    [Theory]
    [Trait("Scenario", "SC-RT-8")]
    [InlineData("[Voss, Halden, Mara]", "0", "[Halden, Mara]")]
    [InlineData("[Voss, Halden, Mara]", "1", "[Voss, Mara]")]
    [InlineData("[Voss, Halden, Mara]", "2", "[Voss, Halden]")]
    [InlineData("[Voss, Halden,]", "1", "[Voss,]")]
    [InlineData("[Voss]", "0", "[]")]
    [InlineData("[ Voss ]", "0", "[]")]
    [InlineData("[Voss,Halden]", "0", "[Halden]")]
    public void RemovingAFlowItemTakesOneSeparatorWithIt(string before, string index, string after)
    {
        var edited = Md($"aliases: {before}  # names\n").WithoutFrontmatterValue(["aliases", index]);

        Assert.Equal($"aliases: {after}  # names\n", Fm(edited));
    }

    [Theory]
    [InlineData("0", "aliases: [\n  Halden,\n  Mara\n]\n")]
    [InlineData("1", "aliases: [\n  Voss,  # surname\n  Mara\n]\n")]
    [InlineData("2", "aliases: [\n  Voss,  # surname\n  Halden\n]\n")]
    public void RemovingAFlowItemOnItsOwnLineRemovesTheLine(string index, string after)
    {
        var edited = Md("aliases: [\n  Voss,  # surname\n  Halden,\n  Mara\n]\n").WithoutFrontmatterValue(["aliases", index]);

        Assert.Equal(after, Fm(edited));
    }

    [Theory]
    [Trait("Scenario", "SC-RT-14")]
    [InlineData("0")]
    [InlineData("1")]
    public void RemovingAFlowItemThatWouldTakeACommentWithItIsRefused(string index)
    {
        var file = Md("aliases: [Voss,  # surname\n  Halden]\n");

        Assert.Throws<NotSupportedException>(() => file.WithoutFrontmatterValue(["aliases", index]));
    }

    [Theory]
    [Trait("Scenario", "SC-RT-8")]
    [InlineData("0", "aliases:\n  # the rank she earns in Ember\n  - the Commander  # since ember:22\n# notes on the name follow\nname: Mara\n")]
    [InlineData("1", "aliases:\n  - Voss\n  # the rank she earns in Ember\n# notes on the name follow\nname: Mara\n")]
    public void RemovingABlockItemRemovesItsLinesAndKeepsTheCommentsAboveIt(string index, string after)
    {
        Assert.Equal(after, Fm(Md(BlockList).WithoutFrontmatterValue(["aliases", index])));
    }

    [Fact]
    public void RemovingTheOnlyBlockItemLeavesTheKeyUnset()
    {
        var edited = Md("aliases:\n  - Voss\nname: x\n").WithoutFrontmatterValue(["aliases", "0"]);

        Assert.Equal("aliases:\nname: x\n", Fm(edited));
        Assert.True(edited.GetFrontmatterValue("aliases") is YamlScalar { IsNull: true });
    }

    [Fact]
    public void RemovingATemporalEntryRemovesItsLineAndComment()
    {
        var edited = Md(Temporal).WithoutFrontmatterValue(["fields", "rank", "1"]);

        Assert.Equal(Temporal.Replace("    - { value: Commander, from: \"ember:22\" }  # promoted\n", "", StringComparison.Ordinal), Fm(edited));
    }

    [Fact]
    public void RemovingABlockMappingItemRemovesAllItsLines()
    {
        var text = "chapters:\n  - key: 1\n    title: Arrival\n  - key: 2\n    title: The Reach\n";

        var edited = Md(text).WithoutFrontmatterValue(["chapters", "0"]);

        Assert.Equal("chapters:\n  - key: 2\n    title: The Reach\n", Fm(edited));
    }

    [Fact]
    public void RemovingAnItemPastTheEndChangesNothingButAPathThatDoesNotFitIsRefused()
    {
        var file = Md("name: Mara\naliases: [Voss]\n");

        // Nothing there: like a missing key, nothing to remove.
        Assert.Same(file, file.WithoutFrontmatterValue(["aliases", "1"]));
        Assert.Same(file, file.WithoutFrontmatterValue(["aliases", "7", "value"]));

        // The path doesn't fit the file: not an item number, or into a scalar.
        Assert.Throws<InvalidOperationException>(() => file.WithoutFrontmatterValue(["aliases", "first"]));
        Assert.Throws<InvalidOperationException>(() => file.WithoutFrontmatterValue(["aliases", "00"]));
        Assert.Throws<InvalidOperationException>(() => file.WithoutFrontmatterValue(["name", "x"]));
    }

    // ---- Review round 1: comment placement in block lists (M2) ----

    [Fact]
    public void AnItemAtIndexZeroGoesAboveTheCommentsOverTheFirstItem()
    {
        var text = "aliases:\n  # her surname\n  - Voss\n";

        Assert.Equal("aliases:\n  - Mara\n  # her surname\n  - Voss\n", Fm(Md(text).WithNewListItem(["aliases"], S("Mara"), 0)));
    }

    private const string Indented = """
        aliases:
          - Voss
            # her surname, from her father
          # the rank she earns
          - the Commander

        """;

    [Theory]
    [InlineData(1, "aliases:\n  - Voss\n    # her surname, from her father\n  - Mara\n  # the rank she earns\n  - the Commander\n")]
    [InlineData(null, "aliases:\n  - Voss\n    # her surname, from her father\n  # the rank she earns\n  - the Commander\n  - Mara\n")]
    public void CommentsIndentedUnderAnItemStayWithIt(int? index, string after)
    {
        Assert.Equal(after, Fm(Md(Indented).WithNewListItem(["aliases"], S("Mara"), index)));
    }

    [Fact]
    public void RemovingAnItemRemovesTheCommentsIndentedUnderIt()
    {
        Assert.Equal("aliases:\n  # the rank she earns\n  - the Commander\n", Fm(Md(Indented).WithoutFrontmatterValue(["aliases", "0"])));
    }

    [Fact]
    public void RemovingAKeyRemovesTheCommentsIndentedUnderIt()
    {
        var text = "fields:\n  born: \"2291\"\n    # checked against the census\n  # about rank\n  rank: x\n";

        Assert.Equal("fields:\n  # about rank\n  rank: x\n", Fm(Md(text).WithoutField("born")));
    }

    [Theory]
    [InlineData("aliases:\n  - a\n\n  - b\n\n  - c\n", "1", "aliases:\n  - a\n\n  - c\n")]
    [InlineData("a: 1\n\nb: 2\n\nc: 3\n", null, "a: 1\n\nc: 3\n")]
    public void RemovingSomethingBetweenBlankLinesLeavesOneBlankLine(string text, string? index, string after)
    {
        var edited = index is null ? Md(text).WithoutFrontmatterValue(["b"]) : Md(text).WithoutFrontmatterValue(["aliases", index]);

        Assert.Equal(after, Fm(edited));
    }

    // ---- Review round 1: layouts that are refused (M1, L2) ----

    [Fact]
    [Trait("Scenario", "SC-RT-14")]
    public void RemovingAnExplicitBlockKeyIsRefused()
    {
        var file = Md("? a\n: 1\nb: 2\n");

        Assert.Throws<NotSupportedException>(() => file.WithoutFrontmatterValue(["a"]));
        Assert.Equal("? a\n: 1\n", Fm(file.WithoutFrontmatterValue(["b"])));
    }

    [Fact]
    [Trait("Scenario", "SC-RT-14")]
    public void RemovingAnExplicitFlowKeyIsRefused()
    {
        var file = Md("x: { ? a : 1, b: 2 }\n");

        Assert.Throws<NotSupportedException>(() => file.WithoutFrontmatterValue(["x", "a"]));
        Assert.Equal("x: { ? a : 1 }\n", Fm(file.WithoutFrontmatterValue(["x", "b"])));
    }

    [Fact]
    [Trait("Scenario", "SC-RT-14")]
    public void RemovingTheFlowKeyBeforeAnExplicitKeyIsRefused()
    {
        // Deleting `b: 2, ` up to the next key would take its `?` too.
        var file = Md("x: { b: 2, ? a : 1, c: 3 }\n");

        Assert.Throws<NotSupportedException>(() => file.WithoutFrontmatterValue(["x", "b"]));
        Assert.Equal("x: { b: 2, ? a : 1 }\n", Fm(file.WithoutFrontmatterValue(["x", "c"])));
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("99999999999999999999999")]
    [InlineData("2147483647")]
    public void AVeryLargeItemNumberIsPastTheEnd(string index)
    {
        var file = Md("aliases: [Voss]\n");

        Assert.Same(file, file.WithoutFrontmatterValue(["aliases", index]));
        Assert.Null(file.GetFrontmatterValue("aliases", index));
        Assert.Throws<InvalidOperationException>(() => file.WithFrontmatterValue(["aliases", index], S("x")));
    }

    [Theory]
    [Trait("Scenario", "SC-RT-14")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void AddingToOrRemovingFromALeadingCommaFlowListIsRefused(string index)
    {
        var file = Md("aliases: [ a\n  , b\n  , c ]\n");

        Assert.Throws<NotSupportedException>(() => file.WithoutFrontmatterValue(["aliases", index]));
        Assert.Throws<NotSupportedException>(() => file.WithNewListItem(["aliases"], S("d"), int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal("aliases: [ a\n  , x\n  , c ]\n", Fm(file.WithFrontmatterValue(["aliases", "1"], S("x"))));
    }

    // ---- Review round 1: quoting new values (H1, nit) ----

    [Theory]
    [InlineData("Who?", "\"Who?\"")]
    [InlineData("what?now", "\"what?now\"")]
    public void AQuestionMarkIsQuotedInFlowContext(string value, string written)
    {
        var list = Md("aliases: [Voss]\n").WithNewListItem(["aliases"], S(value));
        var mapping = Md("died: { value: x, from: \"a1:1\" }\n").WithFrontmatterValue(["died", "value"], S(value));

        Assert.Equal($"aliases: [Voss, {written}]\n", Fm(list));
        Assert.Equal($"died: {{ value: {written}, from: \"a1:1\" }}\n", Fm(mapping));
        Assert.Equal(value, mapping.GetFrontmatterValue("died", "value")!.ToString());
    }

    [Theory]
    [InlineData("rank:\n  - value: x\n", "rank:\n  - value: x\n  - value: Captain\n    from: \"e:2\"\n")]
    [InlineData("rank:\n  - value: x\n    from: e:1\n", "rank:\n  - value: x\n    from: e:1\n  - value: Captain\n    from: e:2\n")]
    public void ANewPositionIsQuotedUnlessItsNeighborIsPlain(string before, string after)
    {
        var entry = new YamlMappingValue { { "value", S("Captain") }, { "from", S("e:2") } };

        Assert.Equal(after, Fm(Md(before).WithNewListItem(["rank"], entry)));
    }

    [Theory]
    [InlineData("from:\n", "from: \"a1:2\"\n")]
    [InlineData("from: ~\n", "from: \"a1:2\"\n")]
    [InlineData("from: a1:1\n", "from: a1:2\n")]
    public void FillingAnEmptyValueWithAPositionQuotesIt(string before, string after)
    {
        Assert.Equal(after, Fm(Md(before).WithFrontmatterValue(["from"], S("a1:2"))));
    }

    // ---- #96: one key inside a flow mapping ----

    private const string Died = "fields:\n  died: { value: { text: \"20 September\", sort: [0, 9, 20] }, from: \"dracula:12\" }  # see Death\n";

    [Fact]
    [Trait("Scenario", "SC-RT-9")]
    public void ChangingOneKeyOfAFlowMappingChangesOnlyItsValue()
    {
        var edited = Md(Died).WithFrontmatterValue(["fields", "died", "from"], S("dracula:13"));

        Assert.Equal(Died.Replace("\"dracula:12\"", "\"dracula:13\"", StringComparison.Ordinal), Fm(edited));
    }

    [Fact]
    [Trait("Scenario", "SC-RT-9")]
    public void ChangingAKeyOfANestedFlowMappingChangesOnlyItsValue()
    {
        var edited = Md(Died).WithFrontmatterValue(["fields", "died", "value", "text"], S("21 September"));

        Assert.Equal(Died.Replace("\"20 September\"", "\"21 September\"", StringComparison.Ordinal), Fm(edited));
    }

    [Theory]
    [Trait("Scenario", "SC-RT-9")]
    [InlineData("{ value: Commander, from: \"ember:22\" }", "{ value: Commander, from: \"ember:22\", pin: true }")]
    [InlineData("{value: Commander}", "{value: Commander, pin: true}")]
    public void ANewKeyGoesAtTheEndOfAFlowMapping(string before, string after)
    {
        var edited = Md($"rank: {before}  # promoted\n").WithFrontmatterValue(["rank", "pin"], YamlScalarValue.Boolean(true));

        Assert.Equal($"rank: {after}  # promoted\n", Fm(edited));
    }

    [Fact]
    public void ANewKeyInAMultiLineFlowMappingGetsItsOwnLine()
    {
        var text = "died: {\n  value: x,  # the date\n  from: \"a1:1\"\n}\n";

        var edited = Md(text).WithFrontmatterValue(["died", "pin"], YamlScalarValue.Boolean(true));

        Assert.Equal("died: {\n  value: x,  # the date\n  from: \"a1:1\",\n  pin: true\n}\n", Fm(edited));
    }

    [Fact]
    [Trait("Scenario", "SC-RT-9")]
    public void ChangingOrRemovingAKeyInAMultiLineFlowMappingKeepsItsComments()
    {
        var text = "died: {\n  # when she dies\n  value: x,  # the date\n  from: \"a1:1\",  # known from\n  pin: true\n}\n";
        var file = Md(text);

        Assert.Equal(text.Replace("\"a1:1\"", "\"a1:2\"", StringComparison.Ordinal), Fm(file.WithFrontmatterValue(["died", "from"], S("a1:2"))));
        Assert.Equal(text.Replace("  from: \"a1:1\",  # known from\n", "", StringComparison.Ordinal), Fm(file.WithoutFrontmatterValue(["died", "from"])));
        Assert.Equal(text.Replace("\"a1:1\",  # known from\n  pin: true\n", "\"a1:1\"  # known from\n", StringComparison.Ordinal), Fm(file.WithoutFrontmatterValue(["died", "pin"])));
    }

    [Theory]
    [InlineData("{ value: x, from: }", "{ value: x, from: \"a1:2\" }")]
    [InlineData("{ from: , value: x }", "{ from: \"a1:2\", value: x }")]
    [InlineData("{from:, value: x}", "{from: \"a1:2\", value: x}")]
    [InlineData("{ value: x, from: ~ }", "{ value: x, from: \"a1:2\" }")]
    public void AnEmptyFlowValueIsFilledInPlace(string before, string after)
    {
        var edited = Md($"died: {before}\n").WithFrontmatterValue(["died", "from"], S("a1:2"));

        Assert.Equal($"died: {after}\n", Fm(edited));
    }

    [Theory]
    [InlineData("{ value: ~ }", "{ value: { text: spring } }")]
    [InlineData("{ value: {} }", "{ value: { text: spring } }")]
    [InlineData("{ from: \"a1:1\" }", "{ from: \"a1:1\", value: { text: spring } }")]
    public void MissingKeysInsideAFlowMappingAreAddedAsFlow(string before, string after)
    {
        var edited = Md($"died: {before}\n").WithFrontmatterValue(["died", "value", "text"], S("spring"));

        Assert.Equal($"died: {after}\n", Fm(edited));
    }

    [Theory]
    [InlineData("a, b", "\"a, b\"")]
    [InlineData("x: y", "\"x: y\"")]
    [InlineData("plain words", "plain words")]
    public void FlowValuesAreQuotedWhenFlowSyntaxWouldMisreadThem(string value, string written)
    {
        var edited = Md("died: { value: x, from: \"a1:1\" }\n").WithFrontmatterValue(["died", "value"], S(value));

        Assert.Equal($"died: {{ value: {written}, from: \"a1:1\" }}\n", Fm(edited));
        Assert.Equal(value, edited.GetFrontmatterValue("died", "value")!.ToString());
    }

    [Fact]
    public void ANullInAFlowMappingIsWrittenAsATilde()
    {
        var edited = Md("died: { value: x, from: \"a1:1\" }\n").WithFrontmatterValue(["died", "from"], YamlScalarValue.Null);

        Assert.Equal("died: { value: x, from: ~ }\n", Fm(edited));
    }

    [Fact]
    public void ChangingOneKeyOfOneTemporalEntryChangesOnlyThatValue()
    {
        var edited = Md(Temporal).WithFrontmatterValue(["fields", "rank", "1", "from"], S("ember:23"));

        Assert.Equal(Temporal.Replace("\"ember:22\"", "\"ember:23\"", StringComparison.Ordinal), Fm(edited));
    }

    [Theory]
    [InlineData("aliases: [Voss, \"the Commander\"]\n", "aliases: [Voss, \"Cmdr\"]\n")]
    [InlineData("aliases:\n  - Voss\n  - the Commander  # rank\n", "aliases:\n  - Voss\n  - Cmdr  # rank\n")]
    [InlineData("aliases:\n  - Voss\n  -\n", "aliases:\n  - Voss\n  - Cmdr\n")]
    public void ReplacingOneListItemKeepsItsQuotingAndComment(string before, string after)
    {
        Assert.Equal(after, Fm(Md(before).WithFrontmatterValue(["aliases", "1"], S("Cmdr"))));
    }

    [Fact]
    public void AChapterKeyThatLooksLikeANumberIsStillAKey()
    {
        var edited = Md("x-notes:\n  14: draft\n").WithFrontmatterValue(["x-notes", "14"], S("done"));

        Assert.Equal("x-notes:\n  14: done\n", Fm(edited));
    }

    [Fact]
    public void FillingAnEmptyListItemIsRefused()
    {
        var file = Md("rank:\n  -\n");

        Assert.Throws<NotSupportedException>(() => file.WithFrontmatterValue(["rank", "0", "value"], S("x")));
        Assert.Throws<InvalidOperationException>(() => file.WithFrontmatterValue(["rank", "1"], S("x")));
    }

    // ---- #97: removing a key ----

    private const string Keys = """
        # Mara's notes
        name: Mara Voss   # display name
        # Ranks change over time.
        rank:
          - { value: Lieutenant, from: "ember:1" }
          # promoted at the Reach
          - { value: Commander, from: "ember:22" }  # promoted
        x-color: teal
        fields:
          born: "2291"
          died: { value: x, from: "a1:1", pin: true }

        """;

    [Fact]
    [Trait("Scenario", "SC-RT-10")]
    public void RemovingAKeyRemovesItsLinesAndKeepsTheCommentAboveIt()
    {
        var edited = Md(Keys).WithoutFrontmatterValue(["name"]);

        Assert.Equal(Keys.Replace("name: Mara Voss   # display name\n", "", StringComparison.Ordinal), Fm(edited));
    }

    [Fact]
    [Trait("Scenario", "SC-RT-10")]
    public void RemovingAKeyWithABlockValueRemovesTheCommentsInsideIt()
    {
        var edited = Md(Keys).WithoutFrontmatterValue(["rank"]);

        Assert.Equal(
            Keys.Replace("rank:\n  - { value: Lieutenant, from: \"ember:1\" }\n  # promoted at the Reach\n  - { value: Commander, from: \"ember:22\" }  # promoted\n", "", StringComparison.Ordinal),
            Fm(edited));
    }

    [Theory]
    [Trait("Scenario", "SC-RT-10")]
    [InlineData("value", "{ from: \"a1:1\", pin: true }")]
    [InlineData("from", "{ value: x, pin: true }")]
    [InlineData("pin", "{ value: x, from: \"a1:1\" }")]
    public void RemovingAKeyOfAFlowMappingKeepsTheRest(string key, string after)
    {
        var edited = Md(Keys).WithoutFrontmatterValue(["fields", "died", key]);

        Assert.Equal(Keys.Replace("{ value: x, from: \"a1:1\", pin: true }", after, StringComparison.Ordinal), Fm(edited));
    }

    [Fact]
    public void RemovingTheOnlyKeyOfAFlowMappingLeavesItEmpty()
    {
        Assert.Equal("died: {}\n", Fm(Md("died: { value: x }\n").WithoutFrontmatterValue(["died", "value"])));
    }

    [Fact]
    public void RemovingTheLastFieldLeavesFieldsUnset()
    {
        var edited = Md("name: x\nfields:\n  born: \"2291\"\n").WithoutField("born");

        Assert.Equal("name: x\nfields:\n", Fm(edited));
    }

    [Fact]
    public void RemovingTheOnlyKeyLeavesEmptyFrontmatter()
    {
        var edited = Md("# just a comment\nname: x\n").WithoutFrontmatterValue(["name"]);

        Assert.Equal("---\n# just a comment\n---\nBody.\n", edited.Text);
        Assert.True(edited.HasFrontmatter);
        Assert.Null(edited.Frontmatter);
    }

    [Fact]
    public void RemovingTheFirstKeyOfAListItemMovesTheNextOneUp()
    {
        var edited = Md("rank:\n  - value: x\n    from: \"a1:1\"\n").WithoutFrontmatterValue(["rank", "0", "value"]);

        Assert.Equal("rank:\n  - from: \"a1:1\"\n", Fm(edited));
    }

    [Theory]
    [Trait("Scenario", "SC-RT-14")]
    [InlineData("rank:\n  - value: x\n    # about from\n    from: \"a1:1\"\n")]
    [InlineData("rank:\n  - value: x\n")]
    public void RemovingTheFirstKeyOfAListItemIsRefusedWhenItWouldMoveACommentOrEmptyTheItem(string text)
    {
        Assert.Throws<NotSupportedException>(() => Md(text).WithoutFrontmatterValue(["rank", "0", "value"]));
    }

    [Fact]
    public void RemovingWhatIsNotThereChangesNothing()
    {
        var file = Md(Keys);

        Assert.Same(file, file.WithoutFrontmatterValue(["species"]));
        Assert.Same(file, file.WithoutFrontmatterValue(["fields", "species"]));
        Assert.Same(file, file.WithoutFrontmatterValue(["x-missing", "deeper"]));
        var noFrontmatter = MarkdownFile.Parse(TestFiles.Utf8("# Title\n"));
        Assert.Same(noFrontmatter, noFrontmatter.WithoutFrontmatterValue(["name"]));
    }

    [Fact]
    public void RemovingAKeyAtTheEndOfAYamlFileWithNoFinalNewlineKeepsItThatWay()
    {
        var file = YamlFile.Parse(TestFiles.Utf8("format: 1\nname: Starfall\nx-old: v"));

        Assert.Equal("format: 1\nname: Starfall", file.WithoutValue(["x-old"]).Text);
    }

    [Theory]
    [InlineData("name: Starfall\n", "\n")]
    [InlineData("name: Starfall\r\n", "\r\n")]
    [InlineData("name: Starfall", "")]
    public void EmptyingAYamlFileKeepsHowItEnded(string before, string after)
    {
        var edited = YamlFile.Parse(TestFiles.Utf8(before)).WithoutValue(["name"]);

        Assert.Equal(after, edited.Text);
        Assert.Null(edited.Root);
    }

    [Fact]
    public void AListItemAtTheEndOfAYamlFileWithNoFinalNewlineKeepsItThatWay()
    {
        var file = YamlFile.Parse(TestFiles.Utf8("tags:\n  - a"));

        Assert.Equal("tags:\n  - a\n  - b", file.WithNewListItem(["tags"], S("b")).Text);
        Assert.Equal("tags:\n  - a", file.WithNewListItem(["tags"], S("b")).WithoutValue(["tags", "1"]).Text);
        Assert.Equal("tags:", file.WithoutValue(["tags", "0"]).Text);
    }

    [Fact]
    [Trait("Scenario", "SC-RT-6")]
    public void CollectionEditsKeepCrlfBomAndMissingFinalNewline()
    {
        var file = MarkdownFile.Parse(TestFiles.Fixture("bom-crlf-no-final-newline.md"));
        var nl = "\r\n";

        var edited = file
            .WithNewListItem(["aliases"], S("Mara"))
            .WithNewListItem(["fields", "rank"], Entry("Captain", "ash:30"))
            .WithoutFrontmatterValue(["fields", "born"])
            .WithoutFrontmatterValue(["aliases", "0"]);

        var expected = file.Text
            .Replace("aliases: [Voss, \"the Commander\"]", "aliases: [\"the Commander\", \"Mara\"]", StringComparison.Ordinal)
            .Replace("from: \"ember:22\" }" + nl, "from: \"ember:22\" }" + nl + "    - { value: Captain, from: \"ash:30\" }" + nl, StringComparison.Ordinal)
            .Replace("  born: \"2291\"" + nl, "", StringComparison.Ordinal);
        Assert.Equal(expected, edited.Text);
        Assert.Equal(Text.LineEndingKind.CrLf, edited.LineEndings);
        Assert.Equal([0xEF, 0xBB, 0xBF], edited.ToBytes()[..3]);
        Assert.False(edited.EndsWithNewline);
    }
}
