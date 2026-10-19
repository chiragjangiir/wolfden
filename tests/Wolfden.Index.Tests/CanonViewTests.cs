using Wolfden.Core.Entities;

namespace Wolfden.Index.Tests;

/// <summary>
/// The rules of canon-as-of views (format.md F-VIEW-1 to F-VIEW-3) one at a
/// time: errors, filters, defaults, unset values, overlaps, bookkeeping, and
/// pinned canon. Uses the Starfall den and a small invented den.
/// </summary>
public sealed class CanonViewTests : IClassFixture<CanonViewTests.Built>
{
    private const string Mara = "01JA7Q8M4XK3V9R2D5W8HN6TQB";
    private const string Pike = "01JA7Q8M4XK3V9R2D5W8HN6TQC";
    private const string Tessaly = "01JA7Q8M4XK3V9R2D5W8HN6TQE";
    private const string LanternShip = "01JA7Q8M4XK3V9R2D5W8HN6TQF";

    private readonly Built _built;

    public CanonViewTests(Built built)
    {
        _built = built;
    }

    private DenIndex Starfall => _built.Starfall;

    private DenIndex Kit => _built.Kit;

    [Theory]
    [InlineData("nowhere", null, null)]
    [InlineData("starfall", null, "series:nope")]
    [InlineData("starfall", null, "book:nope")]
    [InlineData("starfall", null, "book:long-dark")]
    [InlineData("starfall", "ember", "series:ember")]
    [InlineData("starfall", "ember:14.2", null)]
    [InlineData("starfall", "ember:r7", null)]
    [InlineData("starfall", "Ember:1", null)]
    [InlineData("starfall", "ember:", null)]
    [InlineData("starfall", "ember:99", null)]
    [InlineData("starfall", "nope:1", null)]
    [InlineData("starfall", "nope", null)]
    [InlineData("starfall", "tide:1", null)] // a book of universe drift (F-POS-5)
    [InlineData("starfall", "drift-war", null)] // a series key, not a book
    public void AViewThatCantBeMadeSaysWhy(string universe, string? position, string? filter)
    {
        var parsed = filter?.Split(':') switch
        {
            ["series", var key] => CanonFilter.ForSeries(key),
            ["book", var key] => CanonFilter.ForBook(key),
            _ => null,
        };
        var error = Assert.Throws<CanonViewException>(() => Starfall.CanonAsOf(universe, position, parsed));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void ABookWithNoChaptersStillHasABarePosition()
    {
        // k2 has no chapters yet; its bare position is its start (F-POS-7),
        // after everything in k1.
        Assert.Contains(Kit.CanonAsOf("coast", "k2").Entities, e => e.Name == "Gull Light");
        Assert.Throws<CanonViewException>(() => Kit.CanonAsOf("coast", "k2:1"));
    }

    [Fact]
    public void ALaterBookShowsAnEntityFiledUnderAnEarlierSiblingBook()
    {
        // Pike is filed under ash; a-quiet-moon is a later standalone book.
        var view = Starfall.CanonAsOf("starfall", "a-quiet-moon:1");

        var pike = Assert.Single(view.Entities, e => e.Id == Pike);
        Assert.Equal(("book", "ash"), (pike.ScopeKind, pike.ScopeKey));
        Assert.DoesNotContain(Starfall.CanonAsOf("starfall", "ember:3").Entities, e => e.Id == Pike);
    }

    [Fact]
    public void ABookFilterLeavesOutASiblingBooksEntities()
    {
        var quietMoon = Starfall.CanonAsOf("starfall", "a-quiet-moon:1", CanonFilter.ForBook("a-quiet-moon")).Entities;
        Assert.DoesNotContain(quietMoon, e => e.Id is Pike or Mara or Tessaly);
        Assert.Contains(quietMoon, e => e.Id == LanternShip);
        Assert.Contains(quietMoon, e => e.Name == "Kessan Reach");

        var ash = Starfall.CanonAsOf("starfall", null, CanonFilter.ForBook("ash")).Entities;
        Assert.Contains(ash, e => e.Id is Pike);
        Assert.Contains(ash, e => e.Id is Mara);
        Assert.DoesNotContain(ash, e => e.Id == LanternShip);
    }

    [Fact]
    public void ASeriesFilterCoversItsBooksButNotOtherWorks()
    {
        var longDark = Starfall.CanonAsOf("starfall", null, CanonFilter.ForSeries("long-dark")).Entities;

        Assert.Contains(longDark, e => e.Id == Pike);
        Assert.Contains(longDark, e => e.Id == Mara);
        Assert.DoesNotContain(longDark, e => e.Id == LanternShip);
        Assert.Equal(["universe", "series", "book"], longDark.Select(e => e.ScopeKind).Distinct());
    }

    [Theory]
    [MemberData(nameof(CanonCrossCheckTests.Seeds), MemberType = typeof(CanonCrossCheckTests))]
    public void ASeriesFilterLeavesOutAnotherSeriesInAGeneratedDen(int seed)
    {
        var (files, _) = DenGenerator.Generate(seed);
        using var den = TempDen.FromFiles(files);
        using var index = DenIndex.Open(den.Root);
        var universe = EntityCatalog.Load(den.Root).Structure.Universes.Single(u => u.Series.Count(s => s.Books.Count > 0) == 2);
        var series = universe.Series.Where(s => s.Books.Count > 0).ToList();

        var all = index.CanonAsOf(universe.Key);
        var excluded = 0;
        foreach (var (mine, other) in new[] { (series[0], series[1]), (series[1], series[0]) })
        {
            // Generated keys are unique across containers, so a scope key names one container.
            var covered = mine.Books.Select(b => b.Key).Append(mine.Key).Append(universe.Key).ToHashSet(StringComparer.Ordinal);
            var otherScopes = other.Books.Select(b => b.Key).Append(other.Key).ToHashSet(StringComparer.Ordinal);
            var filtered = index.CanonAsOf(universe.Key, null, CanonFilter.ForSeries(mine.Key));

            Assert.DoesNotContain(filtered.Entities, e => otherScopes.Contains(e.ScopeKey));
            Assert.Equal(all.Entities.Where(e => covered.Contains(e.ScopeKey)).Select(e => e.Key), filtered.Entities.Select(e => e.Key));
            excluded += all.Entities.Count(e => otherScopes.Contains(e.ScopeKey));
        }

        // The unfiltered view has entities of both series, so the filters had something to leave out.
        Assert.True(excluded > 0);
    }

    [Fact]
    public void AFieldTheFileDoesntWriteShowsItsTypesDefault()
    {
        // types.md F-FT-15: the starfall pack's ship.condition defaults to active.
        var tessaly = Starfall.CanonAsOf("starfall", "ember:1").Entities.Single(e => e.Id == Tessaly);

        var condition = Assert.Single(tessaly.Fields);
        Assert.Equal(("condition", "\"active\"", "default"), (condition.Name, condition.ValueJson, condition.Kind));
        Assert.True(condition.IsDefault);
        Assert.Null(condition.Status);
        Assert.False(condition.Pinned);
        Assert.Equal("packs/starfall/types/ship.yaml", condition.Path);
        Assert.True(condition.Line > 0);
    }

    [Fact]
    public void AGapLeavesAFieldOut()
    {
        // F-TMP-6: posting ends at ash:2 and the next starts at ash:4.
        string[] Posting(string position) => Starfall.CanonAsOf("starfall", position).Entities.Single(e => e.Id == Mara)
            .Fields.Where(f => f.Name == "posting").Select(f => f.ValueJson).ToArray();

        Assert.Equal(["\"[[tessaly]]\""], Posting("ash:1"));
        Assert.Empty(Posting("ash:2"));
        Assert.Empty(Posting("ash:3"));
        Assert.Equal(["\"[[kessan-reach]]\""], Posting("ash:4"));
    }

    [Fact]
    public void FieldsComeInFrontmatterOrderWithTheirSources()
    {
        var mara = Starfall.CanonAsOf("starfall", "ember:2b").Entities.Single(e => e.Id == Mara);

        Assert.Equal(["rank", "posting", "species"], mara.Fields.Select(f => f.Name));
        var rank = mara.Fields[0];
        Assert.Equal(("\"Commander\"", "temporal", 1, "canon", true, "ember:2b"), (rank.ValueJson, rank.Kind, rank.EntryIndex, rank.Status, rank.Pinned, rank.From));
        Assert.Equal("entry", mara.Fields[2].Kind);
        Assert.EndsWith("mara-voss.md", rank.Path, StringComparison.Ordinal);
        Assert.Equal(11, rank.Line);
        Assert.Equal("Mara Voss", mara.Title);
        Assert.Equal("ember:1", mara.From);
    }

    [Fact]
    public void ACandidateEntryNeverWins()
    {
        // Captain is a candidate from ash:3, so Commander runs on (F-TMP-4).
        var rank = Starfall.CanonAsOf("starfall", "ash:3").Entities.Single(e => e.Id == Mara).Fields.Single(f => f.Name == "rank");
        Assert.Equal("\"Commander\"", rank.ValueJson);
    }

    [Theory]
    [InlineData("k0:1", "lit", "default")] // before any entry: the default
    [InlineData("k1:1", "dark", "temporal")] // two entries from k1:1: the later in the file
    [InlineData("k1:2", "lit", "temporal")]
    [InlineData("k1:3", "dim", "temporal")] // lit's until reaches past dim's from: the later from
    [InlineData("k1:4", "dim", "temporal")]
    [InlineData("k1:5", "lit", "default")] // value: null unsets it, and the default shows in its place
    [InlineData(null, "lit", "default")]
    public void OverlapsAndNullsFollowTheTieBreaks(string? position, string mode, string kind)
    {
        var gull = Kit.CanonAsOf("coast", position).Entities.Single(e => e.Name == "Gull Light");

        Assert.Equal(["mode", "range", "label"], gull.Fields.Select(f => f.Name));
        Assert.Equal(($"\"{mode}\"", kind), (gull.Fields[0].ValueJson, gull.Fields[0].Kind));
        Assert.Equal(("\"unnamed\"", "default"), (gull.Fields[2].ValueJson, gull.Fields[2].Kind));
    }

    [Fact]
    public void AnUnsetFieldWithoutADefaultIsLeftOut()
    {
        var tern = Kit.CanonAsOf("coast").Entities.Single(e => e.Name == "Tern Light");

        // range: ~ is unset and has no default, so it's left out. label: ~
        // shows its default in label's place, and mode, which the file
        // doesn't write, shows its default after the written fields.
        Assert.Equal(["label", "mode"], tern.Fields.Select(f => f.Name));
        Assert.All(tern.Fields, f => Assert.True(f.IsDefault));
    }

    [Fact]
    public void BookkeepingAndOutOfCanonSectionsAreLeftOut()
    {
        var gull = Kit.CanonAsOf("coast").Entities.Single(e => e.Name == "Gull Light");

        // Change log and Scratch notes are bookkeeping (F-SEC-12, F-DEN-6),
        // Old notes has canon=false, and Rumours is an idea. Change log
        // (keep) has canon=true, so it stays.
        Assert.Equal(["Overview", "Keepers", "Change log (keep)"], gull.Sections.Select(s => s.Name));
        Assert.Equal(["Overview", "Keepers"], gull.Sections[1].HeadingPath);
        Assert.Equal(3, gull.Sections[1].Level);
        Assert.Equal("draft", gull.Sections[1].Status);
    }

    [Fact]
    public void ADraftEntityIsInTheViewMarkedAsDraft()
    {
        var gull = Kit.CanonAsOf("coast").Entities.Single(e => e.Name == "Gull Light");
        Assert.True(gull.IsDraft);
        Assert.DoesNotContain(Kit.CanonAsOf("coast").Entities, e => e.Name == "Petrel Light");
    }

    [Fact]
    public void PinnedCanonKeepsOnlyPinnedEntriesAndSections()
    {
        // F-VIEW-2: at ember:2b Mara's Commander is pinned; at ember:2 her
        // Lieutenant isn't, and nothing else of hers is pinned.
        var pinned = Starfall.CanonAsOf("starfall", "ember:2b").Pinned();
        var mara = Assert.Single(pinned.Entities, e => e.Id == Mara);
        Assert.Equal("", mara.Lead);
        Assert.Equal(["rank"], mara.Fields.Select(f => f.Name));
        Assert.Empty(mara.Sections);
        Assert.All(pinned.Entities, e => Assert.All(e.Fields, f => Assert.True(f.Pinned)));
        Assert.All(pinned.Entities, e => Assert.All(e.Sections, s => Assert.True(s.Pinned)));
        Assert.DoesNotContain(Starfall.CanonAsOf("starfall", "ember:2").Pinned().Entities, e => e.Id == Mara);

        // Defaults are never pinned, and a pinned section pins its subsections (F-PIN-2).
        var gull = Assert.Single(Kit.CanonAsOf("coast").Pinned().Entities);
        Assert.Empty(gull.Fields);
        Assert.Equal(["Overview", "Keepers"], gull.Sections.Select(s => s.Name));
        Assert.Equal("starfall", pinned.Universe);
        Assert.Null(pinned.Filter);
    }

    [Fact]
    public void ViewsListEntitiesByScopeTypeNameAndPath()
    {
        var view = Starfall.CanonAsOf("starfall");

        // The universe's, then long-dark's, then its book ash (ember has
        // none), then a-quiet-moon (F-VIEW-3). Within a scope, by type.
        Assert.Equal(["starfall", "long-dark", "ash", "a-quiet-moon"], view.Entities.Select(e => e.ScopeKey).Distinct());
        Assert.Equal(["character", "ship"], view.Entities.Where(e => e.ScopeKey == "long-dark").Select(e => e.Type));
        Assert.Equal(["character", "item"], view.Entities.Where(e => e.ScopeKey == "ash").Select(e => e.Type));
    }

    /// <summary>The Starfall den and the small coast den, indexed once for the class.</summary>
    public sealed class Built : IDisposable
    {
        private readonly TempDen _starfall = TempDen.Starfall();
        private readonly TempDen _kit = TempDen.FromFiles(new Dictionary<string, string>
        {
            ["den.yaml"] = "format: 1\nname: Coast\npacks: [kit]\nbookkeeping_sections: [\"Scratch *\"]\n",
            ["packs/kit/pack.yaml"] = "name: Kit\ndescription: Invented types for the canon view tests.\n",
            ["packs/kit/types/beacon.yaml"] = """
                name: Beacon
                plural: beacons
                fields:
                  mode:
                    type: enum
                    values: [lit, dim, dark]
                    default: lit
                    temporal: true
                  range: string
                  label: { type: string, default: unnamed }
                sections:
                  - Overview

                """,
            ["universes/coast/universe.md"] = "---\nname: Coast\norder: [k0, k1, k2]\n---\n",
            ["universes/coast/books/k0/book.md"] = "---\nname: Before\nchapters: [1]\n---\n",
            ["universes/coast/books/k1/book.md"] = "---\nname: After\nchapters: [1, 2, 3, 4, 5, 6]\n---\n",
            ["universes/coast/books/k2/book.md"] = "---\nname: Later\nchapters: []\n---\n",
            ["universes/coast/entities/beacons/gull-light.md"] = """
                ---
                type: beacon
                name: Gull Light
                status: draft
                fields:
                  mode:
                    - { value: dim, from: "k1:1" }
                    - { value: dark, from: "k1:1" }
                    - { value: lit, from: "k1:2", until: "k1:4" }
                    - { value: dim, from: "k1:3" }
                    - { value: ~, from: "k1:5" }
                  range: 12 leagues
                ---

                A lamp on the north mole.

                ## Overview
                <!-- wolfden: pin=true -->
                It burns whale oil.

                ### Keepers
                Two brothers, turn and turn about.

                ## Change log
                - Renamed from Gannet Light.

                ## Scratch notes
                Check the oil.

                ## Old notes
                <!-- wolfden: canon=false -->
                Superseded.

                ## Rumours
                <!-- wolfden: status=idea -->
                Maybe haunted.

                ## Change log (keep)
                <!-- wolfden: canon=true -->
                Not a log at all: the name of the keepers' boat.

                """,
            ["universes/coast/entities/beacons/tern-light.md"] = """
                ---
                type: beacon
                name: Tern Light
                status: canon
                fields:
                  range: ~
                  label: ~
                ---

                A buoy light.

                """,
            ["universes/coast/entities/beacons/petrel-light.md"] = """
                ---
                type: beacon
                name: Petrel Light
                status: idea
                ---

                Maybe a third light.

                """,
        });

        public Built()
        {
            Starfall = DenIndex.Open(_starfall.Root);
            Kit = DenIndex.Open(_kit.Root);
        }

        public DenIndex Starfall { get; }

        public DenIndex Kit { get; }

        public void Dispose()
        {
            Starfall.Dispose();
            Kit.Dispose();
            _starfall.Dispose();
            _kit.Dispose();
        }
    }
}
