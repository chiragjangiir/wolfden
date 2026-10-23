using System.Text.Json;

namespace Wolfden.Cli.Tests;

/// <summary>
/// <c>wolfden query</c>: canon as of a story position, from the index. The
/// view itself is tested in Wolfden.Index (<c>CanonViewTests</c>); these
/// tests cover the command line, the output, and keeping the index fresh.
/// Each test works on its own copy of <c>samples/dracula</c>, since a query
/// writes the index into the den's <c>.wolfden/</c> folder.
/// </summary>
public sealed class QueryCommandTests : IDisposable
{
    private const string Jonathan = "universes/dracula/books/dracula/entities/characters/jonathan-harker.md";

    private readonly TempDen _den = TempDen.DraculaCopy();

    public void Dispose() => _den.Dispose();

    private (int ExitCode, string Output, string Error) Query(params string[] args) =>
        Cli.Run(["query", "--den", _den.Root, .. args]);

    [Fact]
    [Trait("Scenario", "SC-CLI-3")]
    public void AFieldShowsTheValueInEffectAtThePosition()
    {
        var (exitCode, early, error) = Query("--at", "dracula:5", "--entity", "Jonathan Harker");
        var (_, later, _) = Query("--at", "dracula:12", "--entity", "Jonathan Harker");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.StartsWith("Canon of universe 'dracula' as of dracula:5: 1 entity.\n\n# Jonathan Harker\n", early, StringComparison.Ordinal);
        Assert.Contains($"character, canon, filed in book 'dracula', from dracula:1: {Jonathan}\n", early, StringComparison.Ordinal);
        Assert.Contains("\n- occupation: solicitor (from dracula:1)\n", early, StringComparison.Ordinal);
        Assert.DoesNotContain("factions", early, StringComparison.Ordinal);
        Assert.Contains("\n- occupation: solicitor, junior partner in Hawkins & Harker (from dracula:12)\n", later, StringComparison.Ordinal);
        Assert.Contains("\n## Overview\nA young solicitor sent from Exeter", early, StringComparison.Ordinal);
        // The lead's title is the entity's heading, so it isn't repeated.
        Assert.Single(early.Split('\n'), l => l == "# Jonathan Harker");
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-3")]
    public void ACharacterIsLeftOutBeforeTheReaderMeetsThem()
    {
        var (_, before, _) = Query("--at", "dracula:4", "--brief", "--type", "character");
        var (_, after, _) = Query("--at", "dracula:5", "--brief", "--type", "character");

        Assert.DoesNotContain("Arthur Holmwood", before, StringComparison.Ordinal);
        Assert.Contains(
            "- Arthur Holmwood (character, canon, filed in book 'dracula', from dracula:5): universes/dracula/books/dracula/entities/characters/arthur-holmwood.md\n",
            after,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAPositionItShowsTheEndOfTheStory()
    {
        var (exitCode, output, _) = Query("--brief");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.StartsWith("Canon of universe 'dracula' at the end of the story: ", output, StringComparison.Ordinal);
        Assert.Equal(output, Query("dracula", "--brief").Output);
    }

    [Fact]
    public void BriefListsOneLinePerEntity()
    {
        var (_, output, _) = Query("--at", "dracula:5", "--brief");

        var lines = output.TrimEnd('\n').Split('\n');
        Assert.Equal("Canon of universe 'dracula' as of dracula:5: 25 entities.", lines[0]);
        Assert.Equal(25, lines.Length - 1);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("- ", l, StringComparison.Ordinal));
        // F-VIEW-3: universe-scope entities first.
        Assert.StartsWith("- Bistritz (location, canon, filed in universe 'dracula'", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-3")]
    public void PinnedShowsOnlyPinnedFacts()
    {
        var (_, output, _) = Query("--pinned", "--entity", "Count Dracula");

        Assert.StartsWith("Pinned canon of universe 'dracula' at the end of the story: 1 entity.\n\n# Count Dracula\n", output, StringComparison.Ordinal);
        Assert.Contains("\n- species: vampire (Un-Dead) (from dracula:18, pinned)\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\n## Overview", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ABookFilterKeepsTheUniversesEntities()
    {
        var (exitCode, output, _) = Query("--at", "dracula:5", "--book", "dracula", "--brief");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.StartsWith("Canon of universe 'dracula' as of dracula:5, filtered to book 'dracula': 25 entities.\n", output, StringComparison.Ordinal);
        Assert.Contains("- Bistritz (location", output, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonHasAStableShape()
    {
        var (exitCode, output, _) = Query("--at", "dracula:12", "--entity", "jonathan-harker", "--json");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.DoesNotContain("\r", output, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal(["version", "universe", "position", "filter", "pinned", "entities"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("dracula:12", root.GetProperty("position").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("filter").ValueKind);
        var entity = Assert.Single(root.GetProperty("entities").EnumerateArray());
        Assert.Equal(
            ["key", "id", "name", "type", "status", "scopeKind", "scopeKey", "path", "from", "title", "lead", "leadLine", "fields", "sections"],
            entity.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Jonathan, entity.GetProperty("path").GetString());
        var occupation = entity.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "occupation");
        Assert.Equal(
            ["name", "value", "kind", "entryIndex", "status", "pinned", "from", "until", "path", "line"],
            occupation.EnumerateObject().Select(p => p.Name));
        // The value is JSON, not a string holding JSON.
        Assert.Equal(["solicitor", "junior partner in Hawkins & Harker"], occupation.GetProperty("value").EnumerateArray().Select(v => v.GetString()));
        var section = entity.GetProperty("sections")[0];
        Assert.Equal(
            ["name", "level", "headingPath", "body", "status", "pinned", "from", "until", "path", "line"],
            section.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void JsonNamesTheFilter()
    {
        var (_, output, _) = Query("--book", "dracula", "--type", "location", "--json");

        using var json = JsonDocument.Parse(output);
        var filter = json.RootElement.GetProperty("filter");
        Assert.Equal("book", filter.GetProperty("kind").GetString());
        Assert.Equal("dracula", filter.GetProperty("key").GetString());
        Assert.All(json.RootElement.GetProperty("entities").EnumerateArray(), e => Assert.Equal("location", e.GetProperty("type").GetString()));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-6")]
    public void AQueryAfterAnEditSeesTheEdit()
    {
        Query("--entity", "Jonathan Harker");
        var text = _den.Read(Jonathan).Replace("heritage: English, of Exeter", "heritage: English, of Exeter and later Purfleet", StringComparison.Ordinal);
        _den.Write(Jonathan, text);

        var (_, output, _) = Query("--entity", "Jonathan Harker");

        Assert.Contains("\n- heritage: English, of Exeter and later Purfleet\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WATCH-6")]
    public void AQueryWaitsBrieflyForAWatcherThenRebuildsIfItHasnt()
    {
        Query("--entity", "Jonathan Harker");
        // A watcher holds the lock but doesn't apply the edit: the query
        // waits about a second for it, then rebuilds the index itself.
        using var watcher = new FileStream(Path.Combine(_den.Root, ".wolfden", "watch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _den.Write(Jonathan, _den.Read(Jonathan).Replace("heritage: English, of Exeter", "heritage: English, of Exeter and later Purfleet", StringComparison.Ordinal));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var (exitCode, output, _) = Query("--entity", "Jonathan Harker");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(0.9), $"It returned after {clock.Elapsed}.");
        Assert.Contains("\n- heritage: English, of Exeter and later Purfleet\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-6")]
    public void AQueryAfterANewNoteSeesTheNote()
    {
        Query("--brief");
        _den.Write(
            "universes/dracula/entities/locations/varna.md",
            "---\nid: 01M4TF5EZK6918PFXASD6B9V0A\ntype: location\nname: Varna\nstatus: canon\nfrom: \"dracula:24\"\n---\n\n## Overview\nA Black Sea port.\n");

        var (_, output, _) = Query("--brief", "--type", "location");

        Assert.Contains("- Varna (location, canon, filed in universe 'dracula', from dracula:24)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryWritesOnlyTheIndex()
    {
        var before = Cli.Snapshot(_den.Root);

        Query();
        Query("--json");

        Assert.Equal(before, Cli.Snapshot(_den.Root));
        Assert.True(_den.Exists(".wolfden/index.db"));
    }

    // ----- Mistakes: exit 2, a message on standard error, nothing on standard output -----

    [Theory]
    [InlineData(new[] { "nowhere" }, "The den has no universe 'nowhere'. Its universes: dracula.")]
    [InlineData(new[] { "--universe", "nowhere" }, "The den has no universe 'nowhere'. Its universes: dracula.")]
    [InlineData(new[] { "--at", "dracula:99" }, "Book 'dracula' has no chapter '99'.")]
    [InlineData(new[] { "--at", "lost:1" }, "Universe 'dracula' has no book 'lost'.")]
    [InlineData(new[] { "--series", "lost" }, "Universe 'dracula' has no series 'lost'.")]
    [InlineData(new[] { "--type", "starship" }, "there's no type 'starship': no enabled pack defines it, and no entity in universe 'dracula' uses it.")]
    [InlineData(new[] { "--entity", "Sherlock" }, "universe 'dracula' has no entity 'Sherlock' (matched as a link is: by id, file name, name, or alias).")]
    public void SomethingThatDoesntExistExitsTwo(string[] args, string message)
    {
        var (exitCode, output, error) = Query(args);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Equal($"wolfden query: {message}\n", error);
    }

    // ----- --entity matches as a link does (F-LINK-4, F-LINK-6) -----

    [Theory]
    [InlineData("01M4TF5F056Y24P4D8ZT8RMGGD")] // id
    [InlineData("jonathan-harker")] // slug
    [InlineData("Jonathan-Harker")] // slug, in another case (F-LINK-4)
    [InlineData("jonathan harker")] // name, in another case
    [InlineData("  Jonathan   Harker ")] // name, with extra spaces
    [InlineData("Harker")] // alias
    public void AnEntityCanBeNamedAnyWayALinkCan(string name)
    {
        var (exitCode, output, _) = Query("--entity", name, "--brief");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Equal(
            $"Canon of universe 'dracula' at the end of the story: 1 entity.\n- Jonathan Harker (character, canon, filed in book 'dracula', from dracula:1): {Jonathan}\n",
            output);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-6")]
    public void AtAPositionTheEntityIsMatchedAmongThoseTheReaderHasMet()
    {
        // A note named "Mina" outranks Mina Harker's alias as a link target,
        // but the reader meets it only at chapter 20, so at chapter 3 it
        // can't shadow her.
        _den.Write(
            "universes/dracula/entities/characters/mina.md",
            "---\ntype: character\nname: Mina\nstatus: canon\nfrom: \"dracula:20\"\n---\n\nA namesake.\n");

        var (exitCode, output, _) = Query("--entity", "Mina", "--at", "dracula:3", "--brief");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.StartsWith("Canon of universe 'dracula' as of dracula:3: 1 entity.\n- Mina Harker (character", output, StringComparison.Ordinal);

        // Not met yet is an empty view, not an error; no such entity is an error.
        var (lucyExit, lucy, _) = Query("--entity", "Lucy", "--at", "dracula:3", "--brief");
        Assert.Equal(CliApp.ExitOk, lucyExit);
        Assert.StartsWith("Canon of universe 'dracula' as of dracula:3: 0 entities.", lucy, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitUsage, Query("--entity", "Sherlock Holmes", "--at", "dracula:3").ExitCode);
    }

    [Fact]
    public void AMixedCaseSlugMatchesAsItsLinkDoes()
    {
        // [[Count-Dracula]] resolves, so --entity Count-Dracula must too.
        var (exitCode, output, _) = Query("--entity", "Count-Dracula", "--brief");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("- Count Dracula (character", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstTierThatMatchesWins()
    {
        // F-LINK-6: slug, then name, then alias. "Ash" is one entity's slug,
        // another's name, and a third's alias; "Ember" is a name and an alias.
        using var den = TempDen.FromFiles(new Dictionary<string, string>
        {
            ["den.yaml"] = "format: 1\n",
            ["universes/vael/universe.md"] = "---\nid: 01JC5CK7000000000000000001\nname: Vael\n---\n",
            ["universes/vael/entities/characters/ash.md"] = "---\nid: 01JC5CK7000000000000000002\ntype: character\nname: Cinder\n---\n",
            ["universes/vael/entities/characters/the-smith.md"] = "---\nid: 01JC5CK7000000000000000003\ntype: character\nname: Ash\naliases: [Ember]\n---\n",
            ["universes/vael/entities/characters/orrin.md"] = "---\nid: 01JC5CK7000000000000000004\ntype: character\nname: Orrin\naliases: [Ash]\n---\n",
            ["universes/vael/entities/characters/ember.md"] = "---\nid: 01JC5CK7000000000000000005\ntype: character\nname: Ember\n---\n",
        });

        var names = (string name) => Cli.Run("query", "--den", den.Root, "--entity", name, "--brief").Output
            .Split('\n').Skip(1).Where(l => l.Length > 0).Select(l => l[2..l.IndexOf(' ', 2)]).ToList();

        Assert.Equal(["Cinder"], names("ash"));
        Assert.Equal(["Ember"], names("Ember"));
        Assert.Equal(["Orrin"], names("orrin"));
    }

    [Fact]
    public void ANameMatchBeatsAnAliasOnAnotherEntity()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string>
        {
            ["den.yaml"] = "format: 1\n",
            ["universes/vael/universe.md"] = "---\nid: 01JC5CK8000000000000000001\nname: Vael\n---\n",
            ["universes/vael/entities/characters/wren-calder.md"] = "---\nid: 01JC5CK8000000000000000002\ntype: character\nname: The Captain\n---\n",
            ["universes/vael/entities/characters/orrin-fell.md"] = "---\nid: 01JC5CK8000000000000000003\ntype: character\nname: Orrin Fell\naliases: [The Captain]\n---\n",
        });

        var (_, output, _) = Cli.Run("query", "--den", den.Root, "--entity", "the captain", "--brief");

        Assert.Contains("- The Captain (character", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Orrin Fell", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ADefinedTypeWithNoEntitiesGivesAnEmptyView()
    {
        // The core pack defines 'idea', and the Dracula sample has none in canon.
        var (exitCode, output, error) = Query("--type", "idea");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.Equal("Canon of universe 'dracula' at the end of the story: 0 entities.\n", output);
    }

    // ----- The parser: --opt=value and -- -----

    [Fact]
    public void AnOptionValueCanFollowAnEqualsSign()
    {
        Assert.Equal(Query("--at", "dracula:5", "--brief").Output, Query("--at=dracula:5", "--brief").Output);
    }

    [Fact]
    public void ADoubleDashEndsTheOptions()
    {
        // After --, '--brief' is a plain argument: here, a universe name.
        var (exitCode, output, error) = Query("--", "--brief");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Equal("wolfden query: The den has no universe '--brief'. Its universes: dracula.\n", error);
    }

    [Fact]
    public void AnEntityThatExistsButIsntInCanonYetGivesAnEmptyView()
    {
        var (exitCode, output, _) = Query("--at", "dracula:1", "--entity", "Arthur Holmwood");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Equal("Canon of universe 'dracula' as of dracula:1: 0 entities.\n", output);
    }

    [Fact]
    public void AMalformedPositionExitsTwo()
    {
        var (exitCode, _, error) = Query("--at", "dracula:1.2");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith("wolfden query: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesAndBookTogetherExitTwo()
    {
        var (exitCode, _, error) = Query("--series", "a", "--book", "b");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith("wolfden query: give '--series' or '--book', not both.", error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-10")]
    public void ADenWithSeveralUniversesNeedsOneNamed()
    {
        using var den = SeveralUniverses();

        var (exitCode, _, error) = Cli.Run("query", "--den", den.Root);
        var (named, output, _) = Cli.Run("query", "vael", "--den", den.Root);
        var (option, optionOutput, _) = Cli.Run("query", "--universe", "vael", "--den", den.Root);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal(
            "wolfden query: The den has 2 universes (orn, vael); name one. Name it with '--universe <key>', or give '--at' a position whose book only one universe has.\n",
            error);
        Assert.Equal(CliApp.ExitOk, named);
        Assert.Equal("Canon of universe 'vael' at the end of the story: 0 entities.\n", output);
        Assert.Equal(CliApp.ExitOk, option);
        Assert.Equal(output, optionOutput);
        Assert.Equal(CliApp.ExitUsage, Cli.Run("query", "vael", "--universe", "vael", "--den", den.Root).ExitCode);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-10")]
    public void APositionSettlesTheUniverseWhenItsBookIsInOnlyOne()
    {
        using var den = SeveralUniverses();

        var (exitCode, output, _) = Cli.Run("query", "--at", "fen:1", "--den", den.Root);
        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.StartsWith("Canon of universe 'vael' as of fen:1:", output, StringComparison.Ordinal);

        // A book key in both universes doesn't settle it.
        var (shared, _, sharedError) = Cli.Run("query", "--at", "common:1", "--den", den.Root);
        Assert.Equal(CliApp.ExitUsage, shared);
        Assert.Contains("More than one universe has a book 'common'", sharedError, StringComparison.Ordinal);

        // A book key in neither is a position that doesn't resolve.
        var (none, _, noneError) = Cli.Run("query", "--at", "lost:1", "--den", den.Root);
        Assert.Equal(CliApp.ExitUsage, none);
        Assert.Contains("No universe of the den has a book 'lost'", noneError, StringComparison.Ordinal);

        // A canon view is of one universe: '*' doesn't fit.
        var (all, _, allError) = Cli.Run("query", "--universe", "*", "--den", den.Root);
        Assert.Equal(CliApp.ExitUsage, all);
        Assert.Contains("'*' (every universe) doesn't fit here", allError, StringComparison.Ordinal);
    }

    private static TempDen SeveralUniverses() => TempDen.FromFiles(new Dictionary<string, string>
    {
        ["den.yaml"] = "format: 1\n",
        ["universes/vael/universe.md"] = "---\nid: 01JC5CK3000000000000000001\nname: Vael\n---\n",
        ["universes/orn/universe.md"] = "---\nid: 01JC5CK3000000000000000002\nname: Orn\n---\n",
        ["universes/vael/books/fen/book.md"] = "---\nid: 01JC5CK3000000000000000003\nname: Fen\nchapters: [1, 2]\n---\n",
        ["universes/vael/books/common/book.md"] = "---\nid: 01JC5CK3000000000000000004\nname: Common\nchapters: [1]\n---\n",
        ["universes/orn/books/common/book.md"] = "---\nid: 01JC5CK3000000000000000005\nname: Common\nchapters: [1]\n---\n",
    });

    [Fact]
    public void ADenYamlThatDoesntLoadExitsTwo()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string> { ["den.yaml"] = "format: 2\n" });

        var (exitCode, output, error) = Cli.Run("query", "--den", den.Root);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.Equal("wolfden query: den.yaml doesn't load, so there's nothing to query. Run 'wolfden check' to see why.\n", error);
    }

    [Fact]
    public void AFolderThatIsntADenExitsTwo()
    {
        using var folder = TempDen.Empty();

        var (exitCode, _, error) = Cli.Run("query", "--den", folder.Root);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal($"wolfden query: '{folder.Root}' has no den.yaml, so it isn't a den. Create one there with 'wolfden init'.\n", error);
    }

    [Fact]
    public void DraftAndTimedFactsAreMarked()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string>
        {
            ["den.yaml"] = "format: 1\n",
            ["universes/vael/universe.md"] = "---\nid: 01JC5CK4000000000000000001\nname: Vael\n---\n",
            ["universes/vael/books/a1/book.md"] = "---\nid: 01JC5CK4000000000000000002\nname: Book 1\nchapters: [1, 2, 3]\n---\n",
            ["universes/vael/books/a1/entities/characters/orrin-fell.md"] = """
                ---
                id: 01JC5CK4000000000000000003
                type: character
                name: Orrin Fell
                fields:
                  role:
                    - { value: deckhand, until: "a1:3" }
                    - { value: captain, from: "a1:3", status: canon }
                ---

                Orrin's lead paragraph.

                ## Overview
                <!-- wolfden: until=a1:3 pin=true -->
                A deckhand.
                """,
        });

        var (_, output, _) = Cli.Run("query", "--den", den.Root, "--at", "a1:2");

        Assert.Equal(
            """
            Canon of universe 'vael' as of a1:2: 1 entity.

            # Orrin Fell
            character, draft, filed in book 'a1': universes/vael/books/a1/entities/characters/orrin-fell.md

            - role: deckhand (until a1:3, draft)

            Orrin's lead paragraph.

            ## Overview (until a1:3, pinned, draft)
            A deckhand.

            """.ReplaceLineEndings("\n"),
            output);
    }
}

[Collection(nameof(CurrentFolder))]
public class QueryCommandCurrentFolderTests
{
    [Fact]
    [Trait("Scenario", "SC-CLI-5")]
    public void FromAFolderInsideADenItFindsTheDenAbove()
    {
        using var den = TempDen.DraculaCopy();

        var (exitCode, output, _) = Cli.RunIn(Path.Combine(den.Root, "universes", "dracula", "entities"), "query", "--at", "dracula:1", "--brief");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.StartsWith("Canon of universe 'dracula' as of dracula:1: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideAnyDenItSaysSo()
    {
        using var empty = TempDen.Empty();

        var (exitCode, output, error) = Cli.RunIn(empty.Root, "query");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.StartsWith("wolfden query: there's no den here: neither this folder nor any folder above it has a den.yaml.", error, StringComparison.Ordinal);
    }
}
