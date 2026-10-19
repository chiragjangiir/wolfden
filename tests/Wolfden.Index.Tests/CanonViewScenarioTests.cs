using System.Text.Json;

namespace Wolfden.Index.Tests;

/// <summary>
/// Canon-as-of views in the situations writers meet (docs/scenarios.md,
/// format.md F-VIEW-1), on a hand-written, invented den with the keys of
/// the scenarios' example world: universe <c>vael</c>, the main series
/// <c>a</c> (books <c>a1</c> to <c>a5</c>, 24 chapters each), and the
/// spin-off series <c>b</c> (book <c>b1</c>), after it in story order.
/// Each test is named for the writer's situation.
/// </summary>
public sealed class CanonViewScenarioTests : IClassFixture<CanonViewScenarioTests.Dens>
{
    private const string Universe = "vael";
    private const string Dessa = "01JC5CENA00000000000000002";
    private const string Compass = "01JC5CENA00000000000000003";
    private const string A = "universes/vael/series/a";

    private readonly Dens _dens;

    public CanonViewScenarioTests(Dens dens)
    {
        _dens = dens;
    }

    private DenIndex Index => _dens.Base.Index;

    [Fact]
    public void TheScenarioDensLoadWithOnlyTheExpectedWarnings()
    {
        foreach (var den in new[] { _dens.Base, _dens.Moved, _dens.MovedWithoutFrom })
        {
            // Most entities have no id yet (F-ID-2), on purpose. The one
            // other warning is SC-CANON-9's ambiguous link from Book 4.
            var diagnostics = den.Index.Context.Diagnostics.Where(d => d.RuleId != "F-ID-2").ToList()
                .Select(d => $"{d.Path} {d.RuleId}").ToList();
            Assert.Equal([$"{A}/books/a4/entities/events/the-harbour-trial.md F-LINK-7"], diagnostics);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-1")]
    public void ASideCharacterFiledUnderBookOneReturnsInBookFourWithoutBeingMoved()
    {
        // No from, so Orrin enters at the start of a1 and is in every view after.
        AssertPresentFrom("Orrin Fell", "a1");
        var orrin = Entity("a4:24", "Orrin Fell");
        Assert.Equal(("book", "a1"), (orrin.ScopeKind, orrin.ScopeKey));

        // A link from a Book 4 file resolves to him.
        var link = Assert.Single(Index.Backlinks(orrin.Key), l => l.Path.StartsWith($"{A}/books/a4/", StringComparison.Ordinal));
        Assert.Equal("resolved", link.Outcome);
        Assert.Equal("[[Orrin Fell]]", link.Text);
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-2")]
    public void ThrowawayCharacterInBook3ReturnsInBook5()
    {
        // (a) Left in a3 with no from: absent before a3, present from a3 on, Book 5 included.
        AssertPresentFrom(_dens.Base, Dessa, "a3");

        // (b) Moved up to the series with from: "a3" written in, every view
        // is the same, but for the scope and path it reports.
        AssertSameViews(_dens.Base.Index, _dens.Moved.Index);

        // (c) Moved up without a from, she inherits the series' start
        // (F-ENT-6), which is the start of a1, so she shows from a1. That's
        // the rule as specified; an app or MCP move to a wider scope should
        // write the inherited from (#89).
        AssertPresentFrom(_dens.MovedWithoutFrom, Dessa, "a1");
        Assert.Equal("series", _dens.MovedWithoutFrom.View("a1").Entities.Single(e => e.Key == Dessa).ScopeKind);

        // A link from a Book 5 file resolves to her in all three setups.
        foreach (var den in new[] { _dens.Base, _dens.Moved, _dens.MovedWithoutFrom })
        {
            var link = den.Index.Backlinks(Dessa).Single();
            Assert.Equal("resolved", link.Outcome);
            Assert.StartsWith($"{A}/books/a5/", link.Path, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-3")]
    public void ACharacterEntersPartwayThroughABook()
    {
        // Absent at a1:10 and up to a1:19; present at a1:20 and in every later book.
        AssertPresentFrom("Ysolde Carrow", "a1:20");
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-4")]
    public void ASeriesLevelCharacterArrivesLateInTheSeries()
    {
        // Filed above every book, but absent from a1, a2, and a3 up to a3:13.
        AssertPresentFrom("Captain Hesk", "a3:14");
        Assert.Equal("series", Entity(null, "Captain Hesk").ScopeKind);
    }

    [Fact]
    public void ABookLocalPlaceWithoutFromEntersAtTheStartOfItsBook()
    {
        // Absent from every a2 view, present from the bare a3 position on.
        AssertPresentFrom("The Drowned Stair", "a3");
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-5")]
    public void AnOccupationChangesInBookTwo()
    {
        // The first entry has no from, so it holds from the start of the
        // story until the next live entry's from (F-TMP-4).
        string Occupation(string position) => Entity(position, "Brann Tolley").Fields.Single(f => f.Name == "occupation").ValueJson;

        foreach (var position in new[] { "a1", "a1:5", "a1:24", "a2", "a2:4" })
        {
            Assert.Equal("[\"ferryman\"]", Occupation(position));
        }

        foreach (var position in new[] { "a2:5", "a3:1", "a4", "a4:24" })
        {
            Assert.Equal("[\"harbour pilot\"]", Occupation(position));
        }

        Assert.Null(Entity("a1", "Brann Tolley").Fields.Single(f => f.Name == "occupation").From);
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-6")]
    public void AHiddenIdentityStaysHiddenUntilTheReveal()
    {
        foreach (var position in Ordered().TakeWhile(p => p != "a3:20"))
        {
            var before = Entity(position, "Mirela Vance");
            Assert.DoesNotContain(before.Fields, f => f.Name == "born");
            Assert.DoesNotContain(before.Sections, s => s.Name == "Who she is");
        }

        var justBefore = Entity("a3:19", "Mirela Vance");
        Assert.Equal("\"A foundling from the river docks.\"", justBefore.Fields.Single(f => f.Name == "heritage").ValueJson);
        Assert.DoesNotContain("Carrow", string.Join("\n", justBefore.Fields.Select(f => f.ValueJson).Concat(justBefore.Sections.Select(s => s.Body))), StringComparison.Ordinal);

        foreach (var position in Ordered().SkipWhile(p => p != "a3:20"))
        {
            var after = Entity(position, "Mirela Vance");
            Assert.Equal("\"The lost heir of House Carrow.\"", after.Fields.Single(f => f.Name == "heritage").ValueJson);
            Assert.Contains(after.Fields, f => f.Name == "born");
            Assert.Contains(after.Sections, s => s.Name == "Who she is");
        }
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-7")]
    public void ASpinOffSeriesStaysOutOfTheMainSeries()
    {
        // No from (series b, book b1), or a from inside b1. Absent at every
        // position in a, present from where each enters, and at the end.
        AssertPresentFrom("Pell Ashby", "b1");
        AssertPresentFrom("Lantern House", "b1");
        AssertPresentFrom("Corin Vell", "b1:6");

        // A filter of series a leaves the spin-off out, even at the end of
        // the story, and keeps what's filed at universe scope.
        var main = Index.CanonAsOf(Universe, null, CanonFilter.ForSeries("a")).Entities;
        Assert.DoesNotContain(main, e => e.Name is "Pell Ashby" or "Lantern House" or "Corin Vell");
        Assert.Contains(main, e => e.Name == "The River Vael" && e.ScopeKind == "universe");
        Assert.Contains(main, e => e.Name == "Orrin Fell");
        Assert.DoesNotContain(main, e => e.ScopeKey == "b" || e.ScopeKey == "b1");
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-8")]
    public void RetconnedAndIdeaMaterialNeverShows()
    {
        foreach (var position in Ordered())
        {
            var view = _dens.Base.View(position);
            Assert.DoesNotContain(view.Entities, e => e.Name is "Old Garrow" or "The Ninth Bell");
            Assert.DoesNotContain(view.Pinned().Entities, e => e.Name is "Old Garrow" or "The Ninth Bell");
        }

        // Both are still indexed (F-STAT-4), with their statuses.
        Assert.Equal("retconned", Index.FindEntity($"{A}/entities/characters/old-garrow.md")!.Status);
        Assert.Equal("idea", Index.FindEntity($"{A}/books/a2/entities/items/the-ninth-bell.md")!.Status);
    }

    [Fact]
    [Trait("Scenario", "SC-CANON-9")]
    public void TwoCharactersShareANameInSiblingBooks()
    {
        var hales = Index.CanonAsOf(Universe, "a4:1").Entities.Where(e => e.Name == "Officer Hale").ToList();
        Assert.Equal(["a1", "a2"], hales.Select(h => h.ScopeKey));
        Assert.Equal([$"{A}/books/a1/entities/characters/officer-hale.md", $"{A}/books/a2/entities/characters/officer-hale.md"], hales.Select(h => h.Path));
        Assert.Single(Index.CanonAsOf(Universe, "a1:24").Entities, e => e.Name == "Officer Hale");

        var links = Index.Context.Links.Where(l => l.Text == "[[Officer Hale]]").OrderBy(l => l.Path).ToList();

        // From a Book 1 file, the nearest scope wins: a1's Hale (F-LINK-7).
        var fromBook1 = Assert.Single(links, l => l.Path.StartsWith($"{A}/books/a1/", StringComparison.Ordinal));
        Assert.Equal(("resolved", hales[0].Key), (fromBook1.Outcome, fromBook1.TargetKey));

        // From a Book 4 file neither is in the scope chain: ambiguous, listing both, unresolved.
        var fromBook4 = Assert.Single(links, l => l.Path.StartsWith($"{A}/books/a4/", StringComparison.Ordinal));
        Assert.Equal("ambiguous", fromBook4.Outcome);
        Assert.Null(fromBook4.TargetKey);
        Assert.Equal(hales.Select(h => h.Key).Order(StringComparer.Ordinal), JsonSerializer.Deserialize<string[]>(fromBook4.MatchesJson!)!.Order(StringComparer.Ordinal));
        var warning = Assert.Single(Index.Context.Diagnostics.ToList(), d => d.RuleId == "F-LINK-7");
        Assert.Equal(("warning", fromBook4.Path), (warning.Severity, warning.Path));
    }

    [Fact]
    public void MovingAnEntityWithAFromToAWiderScopeChangesNoView()
    {
        // The compass has from: "a2:7". In the moved den it's filed at
        // universe scope, and every view still holds it from a2:7 on.
        AssertPresentFrom(_dens.Moved, Compass, "a2:7");
        Assert.Equal("universe", _dens.Moved.View("a2:7").Entities.Single(e => e.Key == Compass).ScopeKind);
        AssertSameViews(_dens.Base.Index, _dens.Moved.Index);
    }

    [Fact]
    public void AFilterCoversTheScopeChainAndEverythingInsideIt()
    {
        var book = Index.CanonAsOf(Universe, "a4:24", CanonFilter.ForBook("a3")).Entities;
        Assert.Contains(book, e => e.Name == "The Drowned Stair");
        Assert.Contains(book, e => e.Name == "Captain Hesk");
        Assert.Contains(book, e => e.Name == "The River Vael");
        Assert.DoesNotContain(book, e => e.Name == "Orrin Fell");
        Assert.All(book, e => Assert.Contains(e.ScopeKey, new[] { "vael", "a", "a3" }));

        // A position outside the filter is fine: story order runs across the universe.
        var spinOff = Index.CanonAsOf(Universe, "a2:1", CanonFilter.ForSeries("b")).Entities;
        Assert.NotEmpty(spinOff);
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
        foreach (var position in Ordered())
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

    private void AssertPresentFrom(string name, string first) =>
        AssertPresentFrom(_dens.Base, Index.CanonAsOf(Universe).Entities.Single(e => e.Name == name).Key, first);

    /// <summary>
    /// Asserts the entity is in the unfiltered view at every position from
    /// <paramref name="first"/> on, in story order, and in none before it.
    /// </summary>
    private static void AssertPresentFrom(Built den, string key, string first)
    {
        var positions = Ordered().ToList();
        var from = positions.IndexOf(first);
        Assert.True(from >= 0, $"'{first}' isn't a scenario position.");
        for (var i = 0; i < positions.Count; i++)
        {
            var present = den.View(positions[i]).Entities.Any(e => e.Key == key);
            Assert.True(present == i >= from, $"{key} should be {(i >= from ? "in" : "out of")} the view at '{positions[i] ?? "the end of the story"}'.");
        }
    }

    /// <summary>Every position of the den in story order: each bare book, then its chapters, and the end of the story last.</summary>
    private static IEnumerable<string?> Ordered() =>
        new[] { "a1", "a2", "a3", "a4", "a5", "b1" }
            .SelectMany(b => Enumerable.Range(1, 24).Select(c => $"{b}:{c}").Prepend(b))
            .Append(null);

    private CanonEntity Entity(string? position, string name) => _dens.Base.View(position).Entities.Single(e => e.Name == name);

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
            const string B = U + "/series/b";
            var chapters = $"[{string.Join(", ", Enumerable.Range(1, 24))}]";
            var files = new Dictionary<string, string>
            {
                ["den.yaml"] = "format: 1\nname: Vael Notes\n",
                [$"{U}/universe.md"] = "---\nname: Vael\norder: [a, b]\n---\n\nAn invented river kingdom.\n",
                [$"{U}/entities/locations/the-river-vael.md"] = Entity("location", "The River Vael", "status: canon", "The river the kingdom is named for."),
                [$"{A}/series.md"] = "---\nname: The Saltmarch Cycle\nbooks: [a1, a2, a3, a4, a5]\n---\n",
                [$"{B}/series.md"] = "---\nname: The Lantern Years\nbooks: [b1]\n---\n",
                [$"{B}/books/b1/book.md"] = $"---\nname: Lantern One\nchapters: {chapters}\n---\n",

                [$"{A}/books/a1/entities/characters/orrin-fell.md"] = Entity("character", "Orrin Fell", "status: canon",
                    "A ferry hand who drinks with [[Officer Hale]]."),
                [$"{A}/books/a1/entities/characters/ysolde-carrow.md"] = Entity("character", "Ysolde Carrow", "status: canon\nfrom: \"a1:20\"", "A courier who arrives in the floods."),
                [$"{A}/books/a1/entities/characters/brann-tolley.md"] = Entity("character", "Brann Tolley", """
                    status: canon
                    fields:
                      occupation:
                        - { value: [ferryman] }
                        - { value: [harbour pilot], from: "a2:5" }
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
                [$"{A}/books/a1/entities/characters/officer-hale.md"] = Entity("character", "Officer Hale", "status: canon", "A watchman on the upper river."),
                [$"{A}/books/a2/entities/characters/officer-hale.md"] = Entity("character", "Officer Hale", "status: canon", "A harbour constable, no kin to the other."),
                [$"{A}/books/a2/entities/items/the-ninth-bell.md"] = Entity("item", "The Ninth Bell", "status: idea", "Maybe a bell that rings for the drowned."),
                [$"{A}/entities/characters/old-garrow.md"] = """
                    ---
                    type: character
                    name: Old Garrow
                    status: retconned
                    ---

                    Cut: the first draft's harbour master.

                    ## Overview
                    <!-- wolfden: pin=true -->
                    Ran the harbour for forty years.

                    """,
                [$"{A}/entities/characters/captain-hesk.md"] = Entity("character", "Captain Hesk", "status: canon\nfrom: \"a3:14\"", "Master of the salt barge *Grey Wren*."),
                [$"{A}/books/a3/entities/locations/the-drowned-stair.md"] = Entity("location", "The Drowned Stair", "status: canon", "Steps cut into the river wall, under water at high tide."),
                [$"{A}/books/a4/entities/events/the-harbour-trial.md"] = Entity("event", "The Harbour Trial", "status: canon\nfrom: \"a4:3\"",
                    "[[Orrin Fell]] testifies, and [[Officer Hale]] gives evidence."),
                [$"{A}/books/a5/entities/events/the-reckoning.md"] = Entity("event", "The Reckoning", "status: canon\nfrom: \"a5:22\"",
                    "The barges burn, and [[Dessa Morrow]] comes back to pay her debt."),
                [$"{B}/entities/characters/pell-ashby.md"] = Entity("character", "Pell Ashby", "status: canon", "Keeper of the lantern house, a generation on."),
                [$"{B}/books/b1/entities/locations/lantern-house.md"] = Entity("location", "Lantern House", "status: canon", "A lighthouse at the river mouth."),
                [$"{B}/books/b1/entities/characters/corin-vell.md"] = Entity("character", "Corin Vell", "status: canon\nfrom: \"b1:6\"", "Pell's apprentice, who arrives in a storm."),
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

        private static string Entity(string type, string name, string frontmatter, string lead) =>
            $"---\ntype: {type}\nname: {name}\n{frontmatter}\n---\n\n{lead}\n";
    }

    /// <summary>One den and its index, with its unfiltered views cached by position.</summary>
    public sealed class Built : IDisposable
    {
        private readonly Dictionary<string, CanonView> _views = new(StringComparer.Ordinal);

        public Built(Dictionary<string, string> files)
        {
            Den = TempDen.FromFiles(files);
            Index = DenIndex.Open(Den.Root);
        }

        public TempDen Den { get; }

        public DenIndex Index { get; }

        /// <summary>The unfiltered view at <paramref name="position"/>, or at the end of the story for null.</summary>
        public CanonView View(string? position)
        {
            lock (_views)
            {
                if (!_views.TryGetValue(position ?? "", out var view))
                {
                    view = Index.CanonAsOf(Universe, position);
                    _views[position ?? ""] = view;
                }

                return view;
            }
        }

        public void Dispose()
        {
            Index.Dispose();
            Den.Dispose();
        }
    }
}
