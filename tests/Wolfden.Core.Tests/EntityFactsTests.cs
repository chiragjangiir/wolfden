using System.Text;
using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Outlines;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>
/// The facts the index stores: entry ranges with the F-TMP-4 handover,
/// section ranges (F-SEC-8, F-SEC-9), pins and statuses, outline rows, and
/// story-order ordinals.
/// </summary>
public class EntityFactsTests
{
    private const string Path = LinkDens.Ember + "/entities/characters/mara.md";

    [Fact]
    public void LiveEntriesHandOverAtTheNextLiveFrom()
    {
        var facts = Facts(LinkDens.Character("Mara",
            "fields:",
            "  rank:",
            "    - { value: Ensign }",
            "    - { value: Lieutenant, from: \"ember:1\" }",
            "    - { value: Captain, from: \"ember:2\", status: candidate }",
            "    - { value: Commander, from: \"ash:1\", pin: true }"));
        var rank = facts.Fields.Single();

        Assert.Equal(FieldValueKind.Temporal, rank.Kind);
        Assert.Equal((Start, P("ember:1")), Range(rank.Entries[0]));
        Assert.Equal((P("ember:1"), P("ash:1")), Range(rank.Entries[1]));

        // The candidate isn't live: it runs to the end, and nothing hands over to it.
        Assert.False(rank.Entries[2].IsLive);
        Assert.Equal(FactStatus.Candidate, rank.Entries[2].Status);
        Assert.Equal((P("ember:2"), End), Range(rank.Entries[2]));
        Assert.Equal((P("ash:1"), End), Range(rank.Entries[3]));
        Assert.True(rank.Entries[3].Pinned);
    }

    [Fact]
    public void AnExplicitUntilWins()
    {
        var facts = Facts(LinkDens.Character("Mara",
            "fields:",
            "  posting:",
            "    - { value: Tessaly, from: \"ember:1\", until: \"ember:2\" }",
            "    - { value: Station, from: \"ash:1\" }"));
        var posting = facts.Fields.Single().Entries;

        Assert.Equal((P("ember:1"), P("ember:2")), Range(posting[0]));
        Assert.Equal((P("ash:1"), End), Range(posting[1]));
    }

    [Fact]
    public void EntriesWithTheSameFromShareTheirRange()
    {
        var facts = Facts(LinkDens.Character("Mara",
            "fields:",
            "  rank:",
            "    - { value: A, from: \"ember:1\" }",
            "    - { value: B, from: \"ember:1\" }",
            "    - { value: C, from: \"ash:1\" }"));
        var rank = facts.Fields.Single().Entries;

        // An F-TMP-5 overlap: views take the later one in the file, by Index.
        Assert.Equal(Range(rank[0]), Range(rank[1]));
        Assert.Equal((P("ember:1"), P("ash:1")), Range(rank[1]));
    }

    [Fact]
    public void AnUnresolvedPositionLeavesNoRangeAndIsntLive()
    {
        var facts = Facts(LinkDens.Character("Mara",
            "fields:",
            "  rank:",
            "    - { value: A, from: \"ember:1\" }",
            "    - { value: B, from: \"ember:99\" }",
            "    - { value: C, from: \"ash:1\", until: \"nowhere:1\" }"));
        var rank = facts.Fields.Single().Entries;

        Assert.Equal((P("ember:1"), End), Range(rank[0]));
        Assert.All(rank.Skip(1), e =>
        {
            Assert.False(e.PositionsResolve);
            Assert.False(e.IsLive);
            Assert.Equal(((long?)null, (long?)null), Range(e));
        });
    }

    [Fact]
    public void AnEmptyRangeIsNeverLive()
    {
        var facts = Facts(LinkDens.Character("Mara",
            "fields:",
            "  rank:",
            "    - { value: A, from: \"ember:1\" }",
            "    - { value: B, from: \"ember:2\", until: \"ember:2\" }"));
        var rank = facts.Fields.Single().Entries;

        Assert.True(rank[1].IsEmptyRange);
        Assert.False(rank[1].IsLive);
        Assert.Equal((P("ember:1"), End), Range(rank[0]));
    }

    [Fact]
    public void EntriesTakeTheirEntitysStatus()
    {
        var facts = Facts(LinkDens.Character("Mara", "status: idea",
            "fields:",
            "  rank: { value: Captain, status: canon }",
            "  born: \"2291\""));

        var rank = facts.Fields[0];
        Assert.Equal(FieldValueKind.Entry, rank.Kind);
        Assert.Equal(FactStatus.Canon, rank.Entries[0].OwnStatus);
        Assert.Equal(FactStatus.Idea, rank.Entries[0].Status);
        Assert.Equal(FieldValueKind.Plain, facts.Fields[1].Kind);
        Assert.Empty(facts.Fields[1].Entries);
        Assert.Equal(1, facts.Fields[1].Order);
    }

    [Fact]
    public void SectionsGetEffectiveRanges()
    {
        var facts = Facts(LinkDens.Character("Mara", "from: \"ember:1\""), """

            ## Overview
            Text.

            ### Later
            <!-- wolfden: from=ash:1 -->
            More.

            ## Injury
            <!-- wolfden: from=ember:2 until=ash:2 pin=true -->
            Hurt.

            ### Recovery
            <!-- wolfden: from=ember:1 until=ash:3 status=candidate -->
            Better.

            ## Change log
            - Edited.

            """);
        var s = facts.Sections.ToDictionary(f => f.Section.Name);

        Assert.Equal((P("ember:1"), End), Range(s["Overview"]));
        Assert.Equal((P("ash:1"), End), Range(s["Later"]));
        Assert.Equal((P("ember:2"), P("ash:2")), Range(s["Injury"]));

        // F-SEC-9: the later from and the earlier until of a subsection and its parent.
        Assert.Equal((P("ember:2"), P("ash:2")), Range(s["Recovery"]));
        Assert.True(s["Recovery"].Pinned);
        Assert.Equal(FactStatus.Candidate, s["Recovery"].Status);
        Assert.True(s["Change log"].IsBookkeeping);
        Assert.False(s["Overview"].IsBookkeeping);
        Assert.Equal("ember:2", s["Injury"].EffectiveFrom!.Description);
    }

    [Fact]
    public void AnUnresolvedUntilKeepsTheFrom()
    {
        var facts = Facts(LinkDens.Character("Mara"), """

            ## Injury
            <!-- wolfden: from=ember:2 until=ember:99 -->
            Hurt.

            ### Recovery
            Better.

            """);

        Assert.Equal(P("ember:2"), facts.Sections[0].EffectiveFrom!.Point.Ordinal);
        Assert.Null(facts.Sections[0].EffectiveUntil);
        Assert.Null(facts.Sections[1].EffectiveUntil);
    }

    [Fact]
    public void AnUnresolvedFromLeavesNoRange()
    {
        var facts = Facts(LinkDens.Character("Mara"), """

            ## Injury
            <!-- wolfden: from=ember:99 -->
            Hurt.

            ### Recovery
            Better.

            """);

        Assert.All(facts.Sections, f =>
        {
            Assert.Null(f.EffectiveFrom);
            Assert.Null(f.EffectiveUntil);
        });
    }

    [Fact]
    public void OrdinalsSortInStoryOrder()
    {
        var universe = DenStructure.Load(LinkDens.Den()).FindUniverse("starfall")!;
        var points = new[] { "ember", "ember:prologue", "ember:1", "ember:3", "ash", "ash:3", "a-quiet-moon:1", "a-quiet-moon:2" }
            .Select(p => universe.Resolve(StoryPosition.Parse(p)).Resolved!.Point)
            .ToList();

        Assert.Equal(points.Select(p => p.Ordinal).Order(), points.Select(p => p.Ordinal));
        Assert.Equal(points[0].Ordinal, points[1].Ordinal);
        Assert.Equal(points[4].Ordinal, universe.Resolve(StoryPosition.Parse("ash:1")).Resolved!.Point.Ordinal);
        Assert.All(points, p => Assert.InRange(p.Ordinal, universe.StartOfStory.Ordinal + 1, universe.EndOfStory.Ordinal - 1));
        Assert.All(points.Zip(points.Skip(1)), pair => Assert.Equal(pair.First.CompareTo(pair.Second) < 0, pair.First.Ordinal < pair.Second.Ordinal));
    }

    [Fact]
    public void OutlineRowsAreRead()
    {
        var outline = MarkdownFile.Parse(Encoding.UTF8.GetBytes("""
            # Outline

            ## Act one

            | Row # | Chapter | POV | Beat | Status | Progress | POV |
            |---|---|---|---|---|---|---|
            | 69 | 1 | [[mara\|Voss]] | Wakes. | Candidate | done | ignored |
            | 69a | | | | | | |
            | 069 | 1 | | Bad id, skipped. | | | |
            | 70 | 2 | | Lands. | maybe | | |

            | notes | text |
            |---|---|
            | not | an outline |

            """));

        var rows = OutlineReader.ReadRows(outline);

        Assert.Equal(["69", "69a", "70"], rows.Select(r => r.RowId));
        Assert.Equal((69L, ""), (rows[0].Number!.Value, rows[0].Suffix));
        Assert.Equal("a", rows[1].Suffix);
        Assert.Equal("[[mara|Voss]]", rows[0].Pov);
        Assert.Equal(FactStatus.Candidate, rows[0].Status);
        Assert.Equal(FactStatus.Draft, rows[2].Status);
        Assert.Equal("done", rows[0].Cells["progress"]);
        Assert.Equal("", rows[1].Beat);
        Assert.Null(rows[1].Chapter);
        Assert.Equal("Act one", rows[0].Heading);
        Assert.Equal(7, rows[0].Line);
        Assert.Equal([0, 1, 2], rows.Select(r => r.Order));
        Assert.False(OutlineReader.IsExcluded(outline));
    }

    private static readonly long Start = long.MinValue;
    private static readonly long End = long.MaxValue;

    private static EntityFacts Facts(string frontmatter, string body = "")
    {
        var source = LinkDens.Den().With(Path, $"---\n{frontmatter}\n---\n{body}");
        var catalog = EntityCatalog.Load(source);
        Assert.DoesNotContain(catalog.Diagnostics, d => d.Diagnostic.Severity == DiagnosticSeverity.Error);
        return EntityFacts.For(catalog.FindByPath(Path)!, new Bookkeeping());
    }

    private static long P(string position) =>
        DenStructure.Load(LinkDens.Den()).FindUniverse("starfall")!.Resolve(StoryPosition.Parse(position)).Resolved!.Point.Ordinal;

    private static (long?, long?) Range(EntryFact entry) => (entry.EffectiveFrom?.Ordinal, entry.EffectiveUntil?.Ordinal);

    private static (long?, long?) Range(SectionFact section) => (section.EffectiveFrom?.Point.Ordinal, section.EffectiveUntil?.Ordinal);
}
