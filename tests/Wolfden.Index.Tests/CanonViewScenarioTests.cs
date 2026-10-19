using Wolfden.Core.Structure;

namespace Wolfden.Index.Tests;

/// <summary>
/// Canon-as-of views in the situations writers meet (format.md F-VIEW-1),
/// on a hand-written, invented den: universe <c>vael</c> with the series
/// <c>saltmarch</c> (books <c>a1</c> to <c>a5</c>, 24 chapters each) and the
/// spin-off series <c>lantern</c> (book <c>b1</c>), after it in story order.
/// Each test is named for the writer's situation.
/// </summary>
public sealed class CanonViewScenarioTests : IClassFixture<CanonViewScenarioTests.Dens>
{
    private const string Universe = "vael";
    private const string Dessa = "01JC5CENA00000000000000002";
    private const string Compass = "01JC5CENA00000000000000003";

    private readonly Dens _dens;

    public CanonViewScenarioTests(Dens dens)
    {
        _dens = dens;
    }

    private DenIndex Index => _dens.Base.Index;

    [Fact]
    public void TheScenarioDensLoadWithoutDiagnostics()
    {
        foreach (var den in new[] { _dens.Base, _dens.Moved, _dens.MovedWithoutFrom })
        {
            // Most entities have no id yet (F-ID-2), on purpose. Nothing else is reported.
            var diagnostics = den.Index.Context.Diagnostics.Where(d => d.RuleId != "F-ID-2").ToList().Select(d => $"{d.Path}:{d.Line} {d.RuleId} {d.Message}").ToList();
            Assert.True(diagnostics.Count == 0, string.Join(Environment.NewLine, diagnostics));
        }
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-1")]
    public void ASideCharacterFiledUnderBookOneReturnsInBookFourWithoutBeingMoved()
    {
        foreach (var position in new[] { "a1", "a1:12", "a2:1", "a3:10", "a4", "a4:24" })
        {
            Assert.Contains(Names(position), n => n == "Orrin Fell");
        }

        var orrin = Entity("a4:24", "Orrin Fell");
        Assert.Equal(("book", "a1"), (orrin.ScopeKind, orrin.ScopeKey));
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-2")]
    public void ThrowawayCharacterInBook3ReturnsInBook5()
    {
        // (a) Left in a3 with no from: absent before a3, present from a3 on, and in a5.
        foreach (var position in new[] { "a1", "a1:24", "a2:24" })
        {
            Assert.DoesNotContain(Keys(Index, position), k => k == Dessa);
        }

        foreach (var position in new[] { "a3", "a3:1", "a4:9", "a5", "a5:24", null })
        {
            Assert.Contains(Keys(Index, position), k => k == Dessa);
        }

        // (b) Moved up to the series with from: "a3" written in, every view
        // is the same, but for the scope and path it reports.
        AssertSameViews(_dens.Base.Index, _dens.Moved.Index);

        // (c) Moved up without a from, she inherits the series' start
        // (F-ENT-6), so she shows from a1. That's the rule as specified; an
        // app or MCP move to a wider scope should write the inherited from.
        Assert.Contains(Keys(_dens.MovedWithoutFrom.Index, "a1"), k => k == Dessa);
        Assert.Contains(Keys(_dens.MovedWithoutFrom.Index, "a2:24"), k => k == Dessa);
        Assert.Equal("series", _dens.MovedWithoutFrom.Index.CanonAsOf(Universe, "a1").Entities.Single(e => e.Key == Dessa).ScopeKind);

        // A link from an a5 file resolves to her in all three setups.
        foreach (var index in new[] { _dens.Base.Index, _dens.Moved.Index, _dens.MovedWithoutFrom.Index })
        {
            var link = index.Backlinks(Dessa).Single();
            Assert.Equal("resolved", link.Outcome);
            Assert.StartsWith("universes/vael/series/saltmarch/books/a5/", link.Path, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-3")]
    public void ACharacterEntersPartwayThroughABook()
    {
        Assert.DoesNotContain(Names("a1:10"), n => n == "Ysolde Carrow");
        Assert.DoesNotContain(Names("a1:19"), n => n == "Ysolde Carrow");
        Assert.Contains(Names("a1:20"), n => n == "Ysolde Carrow");
        Assert.Contains(Names("a2"), n => n == "Ysolde Carrow");
        Assert.Contains(Names("a2:5"), n => n == "Ysolde Carrow");
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-4")]
    public void ASeriesLevelCharacterArrivesLateInTheSeries()
    {
        Assert.DoesNotContain(Names("a1"), n => n == "Captain Hesk");
        Assert.DoesNotContain(Names("a3:13"), n => n == "Captain Hesk");
        Assert.Contains(Names("a3:14"), n => n == "Captain Hesk");
        foreach (var chapter in Enumerable.Range(1, 24))
        {
            Assert.Contains(Names($"a4:{chapter}"), n => n == "Captain Hesk");
        }
    }

    [Fact]
    public void ABookLocalPlaceWithoutFromEntersAtTheStartOfItsBook()
    {
        foreach (var chapter in Enumerable.Range(1, 24))
        {
            Assert.DoesNotContain(Names($"a2:{chapter}"), n => n == "The Drowned Stair");
        }

        Assert.DoesNotContain(Names("a2"), n => n == "The Drowned Stair");
        Assert.Contains(Names("a3"), n => n == "The Drowned Stair");
        Assert.Contains(Names("a3:1"), n => n == "The Drowned Stair");
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-5")]
    public void AnOccupationChangesInBookTwo()
    {
        string Occupation(string position) => Entity(position, "Brann Tolley").Fields.Single(f => f.Name == "occupation").ValueJson;

        Assert.Equal("[\"ferryman\"]", Occupation("a1:5"));
        Assert.Equal("[\"ferryman\"]", Occupation("a2:2"));
        Assert.Equal("[\"harbour pilot\"]", Occupation("a2:3"));
        Assert.Equal("[\"harbour pilot\"]", Occupation("a4:24"));
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-6")]
    public void AHiddenIdentityStaysHiddenUntilTheReveal()
    {
        var before = Entity("a3:19", "Mirela Vance");
        Assert.Equal("\"A foundling from the river docks.\"", before.Fields.Single(f => f.Name == "heritage").ValueJson);
        Assert.DoesNotContain(before.Fields, f => f.Name == "born");
        Assert.DoesNotContain(before.Sections, s => s.Name == "Who she is");
        Assert.DoesNotContain("Carrow", string.Join("\n", before.Fields.Select(f => f.ValueJson).Concat(before.Sections.Select(s => s.Body))), StringComparison.Ordinal);

        var after = Entity("a3:20", "Mirela Vance");
        Assert.Equal("\"The lost heir of House Carrow.\"", after.Fields.Single(f => f.Name == "heritage").ValueJson);
        Assert.Contains(after.Fields, f => f.Name == "born");
        Assert.Contains(after.Sections, s => s.Name == "Who she is");
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-7")]
    public void ASpinOffSeriesStaysOutOfTheMainSeries()
    {
        string[] spinOff = ["Pell Ashby", "Lantern House"];
        foreach (var position in new[] { "a1", "a3:12", "a5:24" })
        {
            Assert.Empty(Names(position).Intersect(spinOff));
        }

        Assert.Equal(spinOff.Order(), Names(null).Intersect(spinOff).Order());
        Assert.Equal(spinOff.Order(), Names("b1").Intersect(spinOff).Order());

        // A filter of the main series leaves the spin-off out, even at the end of the story.
        var main = Index.CanonAsOf(Universe, null, CanonFilter.ForSeries("saltmarch")).Entities.Select(e => e.Name);
        Assert.Empty(main.Intersect(spinOff));
        Assert.Contains("Orrin Fell", main);
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-8")]
    public void RetconnedAndIdeaMaterialNeverShows()
    {
        foreach (var position in new[] { "a1", "a2:10", "a5:24", "b1", null })
        {
            Assert.DoesNotContain(Names(position), n => n is "Old Garrow" or "The Ninth Bell");
        }
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-9")]
    public void TwoCharactersShareANameInSiblingBooks()
    {
        var tamsins = Index.CanonAsOf(Universe, "a4:1").Entities.Where(e => e.Name == "Tamsin Reed").ToList();

        Assert.Equal(["a1", "a2"], tamsins.Select(t => t.ScopeKey));
        Assert.Equal(2, tamsins.Select(t => t.Path).Distinct().Count());
        Assert.Single(Index.CanonAsOf(Universe, "a1:24").Entities, e => e.Name == "Tamsin Reed");
    }

    [Fact]
    public void MovingAnEntityWithAFromToAWiderScopeChangesNoView()
    {
        // The compass has from: "a2:7". In the moved den it's filed at
        // universe scope, and every view still holds it from a2:7 on.
        Assert.DoesNotContain(Keys(_dens.Moved.Index, "a2:6"), k => k == Compass);
        Assert.Contains(Keys(_dens.Moved.Index, "a2:7"), k => k == Compass);
        Assert.Equal("universe", _dens.Moved.Index.CanonAsOf(Universe, "a2:7").Entities.Single(e => e.Key == Compass).ScopeKind);
        AssertSameViews(_dens.Base.Index, _dens.Moved.Index);
    }

    [Fact]
    public void AFilterCoversTheScopeChainAndEverythingInsideIt()
    {
        var book = Index.CanonAsOf(Universe, "a4:24", CanonFilter.ForBook("a3")).Entities;
        Assert.Contains(book, e => e.Name == "The Drowned Stair");
        Assert.Contains(book, e => e.Name == "Captain Hesk");
        Assert.DoesNotContain(book, e => e.Name == "Orrin Fell");
        Assert.All(book, e => Assert.Contains(e.ScopeKey, new[] { "vael", "saltmarch", "a3" }));

        // A position outside the filter is fine: story order runs across the universe.
        var spinOff = Index.CanonAsOf(Universe, "a2:1", CanonFilter.ForSeries("lantern")).Entities;
        Assert.DoesNotContain(spinOff, e => e.Name == "Pell Ashby");
        Assert.All(spinOff, e => Assert.Equal("universe", e.ScopeKind));
    }

    /// <summary>
    /// Asserts two dens give the same views at every position: the same
    /// entities with the same fields and sections, whatever scope and path
    /// they're filed at. The entity's own <c>from</c> is left out, since a
    /// move may write it.
    /// </summary>
    private static void AssertSameViews(DenIndex expected, DenIndex actual)
    {
        foreach (var position in Positions())
        {
            Assert.Equal(Comparable(expected.CanonAsOf(Universe, position)), Comparable(actual.CanonAsOf(Universe, position)));
        }

        static List<string> Comparable(CanonView view) => view.Entities
            .Select(e => string.Join(" | ",
                new[] { e.Key, e.Name, e.Type, e.Status, e.Lead }
                    .Concat(e.Fields.Select(f => $"{f.Name}={f.ValueJson} {f.Kind} {f.Status} {f.Pinned} {f.From} {f.Until}"))
                    .Concat(e.Sections.Select(s => $"{string.Join('>', s.HeadingPath)} {s.Status} {s.From} {s.Until} {s.Body}"))))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string?> Positions() =>
        new[] { "a1", "a2", "a3", "a4", "a5" }
            .SelectMany(b => Enumerable.Range(1, 24).Select(c => $"{b}:{c}").Prepend(b))
            .Append("b1").Append("b1:1").Append(null);

    private List<string> Names(string? position) => Index.CanonAsOf(Universe, position).Entities.Select(e => e.Name).ToList();

    private static List<string> Keys(DenIndex index, string? position) => index.CanonAsOf(Universe, position).Entities.Select(e => e.Key).ToList();

    private CanonEntity Entity(string? position, string name) => Index.CanonAsOf(Universe, position).Entities.Single(e => e.Name == name);

    /// <summary>The den, and two copies with Dessa Morrow and the compass filed at wider scopes.</summary>
    public sealed class Dens : IDisposable
    {
        public Dens()
        {
            Base = new Built(Files(Placement.InBook));
            Moved = new Built(Files(Placement.WiderWithFrom));
            MovedWithoutFrom = new Built(Files(Placement.WiderWithoutFrom));
        }

        public enum Placement
        {
            InBook,
            WiderWithFrom,
            WiderWithoutFrom,
        }

        public Built Base { get; }

        public Built Moved { get; }

        public Built MovedWithoutFrom { get; }

        public void Dispose()
        {
            Base.Dispose();
            Moved.Dispose();
            MovedWithoutFrom.Dispose();
        }

        private static Dictionary<string, string> Files(Placement placement)
        {
            const string U = "universes/vael";
            const string A = U + "/series/saltmarch";
            const string B = U + "/series/lantern";
            var chapters = $"[{string.Join(", ", Enumerable.Range(1, 24))}]";
            var files = new Dictionary<string, string>
            {
                ["den.yaml"] = "format: 1\nname: Vael Notes\n",
                [$"{U}/universe.md"] = "---\nname: Vael\norder: [saltmarch, lantern]\n---\n\nAn invented river kingdom.\n",
                [$"{A}/series.md"] = "---\nname: The Saltmarch Cycle\nbooks: [a1, a2, a3, a4, a5]\n---\n",
                [$"{B}/series.md"] = "---\nname: The Lantern Years\nbooks: [b1]\n---\n",
                [$"{B}/books/b1/book.md"] = $"---\nname: Lantern One\nchapters: {chapters}\n---\n",

                [$"{A}/books/a1/entities/characters/orrin-fell.md"] = Character("Orrin Fell", "status: canon", "A ferry hand who pulls Ysolde from the river."),
                [$"{A}/books/a1/entities/characters/ysolde-carrow.md"] = Character("Ysolde Carrow", "status: canon\nfrom: \"a1:20\"", "A courier who arrives in the floods."),
                [$"{A}/books/a1/entities/characters/brann-tolley.md"] = Character("Brann Tolley", """
                    status: canon
                    fields:
                      occupation:
                        - { value: [ferryman], from: "a1" }
                        - { value: [harbour pilot], from: "a2:3" }
                    """, "Orrin's uncle."),
                [$"{A}/books/a1/entities/characters/mirela-vance.md"] = """
                    ---
                    type: character
                    name: Mirela Vance
                    status: canon
                    fields:
                      heritage:
                        - { value: A foundling from the river docks., from: "a1" }
                        - { value: The lost heir of House Carrow., from: "a3:20" }
                      born: { value: { text: "the year of the great flood" }, from: "a3:20" }
                    ---

                    A dockside thief with a talent for locks.

                    ## Backstory
                    Raised by the dock wardens.

                    ### Who she is
                    <!-- wolfden: from=a3:20 -->
                    The heir House Carrow thought it had lost to the flood.

                    """,
                [$"{A}/books/a1/entities/characters/tamsin-reed.md"] = Character("Tamsin Reed", "status: canon", "A lock keeper on the upper river."),
                [$"{A}/books/a2/entities/characters/tamsin-reed.md"] = Character("Tamsin Reed", "status: canon", "A singer in the harbour taverns."),
                [$"{A}/books/a2/entities/items/the-ninth-bell.md"] = Item("The Ninth Bell", "status: idea", "Maybe a bell that rings for the drowned."),
                [$"{A}/entities/characters/old-garrow.md"] = Character("Old Garrow", "status: retconned", "Cut: the first draft's harbour master."),
                [$"{A}/entities/characters/captain-hesk.md"] = Character("Captain Hesk", "status: canon\nfrom: \"a3:14\"", "Master of the salt barge *Grey Wren*."),
                [$"{A}/books/a3/entities/locations/the-drowned-stair.md"] = """
                    ---
                    type: location
                    name: The Drowned Stair
                    status: canon
                    ---

                    Steps cut into the river wall, under water at high tide.

                    """,
                [$"{A}/books/a5/entities/events/the-reckoning.md"] = """
                    ---
                    type: event
                    name: The Reckoning
                    status: canon
                    from: "a5:22"
                    ---

                    The barges burn, and [[Dessa Morrow]] comes back to pay her debt.

                    """,
                [$"{B}/entities/characters/pell-ashby.md"] = Character("Pell Ashby", "status: canon", "Keeper of the lantern house, a generation on."),
                [$"{B}/books/b1/entities/locations/lantern-house.md"] = """
                    ---
                    type: location
                    name: Lantern House
                    status: canon
                    ---

                    A lighthouse at the river mouth.

                    """,
            };

            foreach (var book in new[] { "a1", "a2", "a3", "a4", "a5" })
            {
                files[$"{A}/books/{book}/book.md"] = $"---\nname: Saltmarch {book[1..]}\nchapters: {chapters}\n---\n";
            }

            var dessa = $"---\nid: {Dessa}\ntype: character\nname: Dessa Morrow\nstatus: canon\n"
                + (placement == Placement.WiderWithFrom ? "from: \"a3\"\n" : "")
                + "---\n\nA barge girl who owes Captain Hesk a favour.\n";
            files[placement == Placement.InBook ? $"{A}/books/a3/entities/characters/dessa-morrow.md" : $"{A}/entities/characters/dessa-morrow.md"] = dessa;

            var compass = $"---\nid: {Compass}\ntype: item\nname: The brass compass\nstatus: canon\nfrom: \"a2:7\"\n---\n\nIt points to the drowned, not to the north.\n";
            files[placement == Placement.InBook ? $"{A}/books/a2/entities/items/the-brass-compass.md" : $"{U}/entities/items/the-brass-compass.md"] = compass;
            return files;
        }

        private static string Character(string name, string frontmatter, string lead) =>
            $"---\ntype: character\nname: {name}\n{frontmatter}\n---\n\n{lead}\n";

        private static string Item(string name, string frontmatter, string lead) =>
            $"---\ntype: item\nname: {name}\n{frontmatter}\n---\n\n{lead}\n";
    }

    /// <summary>One den and its index.</summary>
    public sealed class Built : IDisposable
    {
        public Built(Dictionary<string, string> files)
        {
            Den = TempDen.FromFiles(files);
            Index = DenIndex.Open(Den.Root);
            Structure = DenStructure.Load(Den.Root);
        }

        public TempDen Den { get; }

        public DenIndex Index { get; }

        public DenStructure Structure { get; }

        public void Dispose()
        {
            Index.Dispose();
            Den.Dispose();
        }
    }
}
