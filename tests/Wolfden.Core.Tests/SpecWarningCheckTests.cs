using Wolfden.Core.Checks;
using Wolfden.Core.Documents;

namespace Wolfden.Core.Tests;

/// <summary>
/// The spec warnings <c>wolfden check</c> reports beyond the continuity
/// rules (#93, #100, #80): field values and undefined fields (F-FLD-2,
/// F-FLD-3, F-FT-*), F-FLD-6, F-FLD-7, F-TMP-2, F-TMP-8, F-ENT-6, F-SEC-9,
/// the layout (F-LAY-*), outlines (F-OUT-*), <c>den.yaml</c>'s
/// <c>authors</c> (F-DEN-3, F-CTR-2), container files without an id
/// (F-ID-2), statuses and pins (F-STAT-3, F-PIN-3), bookkeeping sections
/// (F-SEC-7, F-SEC-12), and the core pack's threads and events (types.md
/// F-CORE-2, F-CORE-3). The den is the scenarios' example world (docs/scenarios.md),
/// from <see cref="ContinuityCheckTests.Vael"/>.
/// </summary>
public class SpecWarningCheckTests
{
    private const string Vael = "universes/vael";
    private const string A = Vael + "/series/a";
    private const string A1 = A + "/books/a1";
    private const string A2 = A + "/books/a2";
    private const string B1 = Vael + "/series/b/books/b1";
    private const string Orrin = A1 + "/entities/characters/orrin-fell.md";

    private static MemoryDenSource Den(string denYaml = "") => ContinuityCheckTests.Vael(denYaml: denYaml);

    /// <summary>Adds an entity with an id at <c>{container}/entities/{folder}/{slug}.md</c>. Frontmatter line n is file line n + 2.</summary>
    private static MemoryDenSource Note(MemoryDenSource source, string container, string slug, string frontmatter, string body = "",
        string folder = "characters")
    {
        var path = $"{container}/entities/{folder}/{slug}.md";
        return source.With(path, $"---\nid: {MemoryDenSource.ContainerId(path)}\n{frontmatter}\n---\n{body}");
    }

    private static string Character(string name, params string[] extra) =>
        string.Join("\n", new[] { "type: character", $"name: {name}", "status: canon" }.Concat(extra));

    /// <summary>The rules these tests expect as errors. Every other diagnostic must be a warning.</summary>
    private static readonly HashSet<string> Errors = new(StringComparer.Ordinal) { "F-YAML-1" };

    /// <summary>The diagnostics as (path, rule, line), after asserting each one's severity: an error only for <see cref="Errors"/>.</summary>
    private static (string Path, string Rule, int? Line)[] Found(DenCheck check)
    {
        Assert.All(check.Diagnostics, d => Assert.Equal(
            Errors.Contains(d.Diagnostic.RuleId) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, d.Diagnostic.Severity));
        return check.Diagnostics.Select(d => (d.Path, d.Diagnostic.RuleId, d.Diagnostic.Line)).ToArray();
    }

    private static (string Path, string Rule, int? Line)[] Found(DenCheck check, string rule) =>
        Found(check).Where(d => d.Rule == rule).ToArray();

    private static string Message(DenCheck check, string rule)
    {
        var diagnostic = Assert.Single(check.Diagnostics, d => d.Diagnostic.RuleId == rule).Diagnostic;
        Assert.Equal(Errors.Contains(rule) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, diagnostic.Severity);
        return diagnostic.Message;
    }

    /// <summary>An outline: a header and one line per row, each "row | chapter | beat".</summary>
    private static string Outline(params string[] rows) =>
        "# Outline\n\n| row | chapter | beat |\n|---|---|---|\n" + string.Join("", rows.Select(r => $"| {r} |\n"));

    // ----- Fields: F-FLD-2, F-FLD-3 with F-FT-*, F-FLD-6, F-TMP-8, F-TMP-2 -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-13")]
    public void AFieldTheTypeDoesntDefineIsWarned()
    {
        var source = Den();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "fields:\n  occupaton: [deckhand]\n  row:\n  placeholder: true"));

        var check = DenCheck.Run(source);

        Assert.Equal([(Orrin, "F-FLD-2", 7)], Found(check));
        Assert.Equal(
            "Type 'character' doesn't define the field 'occupaton', so its value is kept and indexed as written. Check the spelling, or define the field in a pack.",
            Message(check, "F-FLD-2"));
    }

    [Fact]
    public void AnUntypedEntityGetsNoFieldWarnings()
    {
        // F-ENT-2: the checks that need a type are skipped.
        var source = Den();
        Note(source, A1, "orrin-fell", "type: charcter\nname: Orrin Fell\nfields:\n  occupaton: [deckhand]\n  born: [1, 2]");

        Assert.Equal([(Orrin, "F-ENT-2", 3)], Found(DenCheck.Run(source)));
    }

    private const string Ship = """
        name: Ship
        plural: ships
        fields:
          crew: number
          condition: { type: enum, values: [active, damaged, lost], temporal: true }
          captain: { type: ref, to: character }
          armed: bool
          launched: date
          refits: { type: position, list: true }
        """;

    [Fact]
    [Trait("Scenario", "SC-CHECK-14")]
    public void ValuesThatDontMatchTheirFieldTypesAreWarned()
    {
        var source = Den("packs: [fleet]\n")
            .With("packs/fleet/pack.yaml", "name: Fleet\n")
            .With("packs/fleet/types/ship.yaml", Ship);
        Note(source, A1, "kestrel", """
            type: ship
            name: Kestrel
            status: canon
            fields:
              crew: "140"
              condition:
                - { value: active, from: "a1:1" }
                - { value: damagd, from: "a1:9" }
              captain: Orrin Fell
              armed: yes
              launched: { text: the old year, calendar: reach }
              refits: ["a1:3", "a1:3.2"]
            """, folder: "ships");

        var check = DenCheck.Run(source);

        const string path = A1 + "/entities/ships/kestrel.md";
        Assert.Equal(
            [(path, "F-FT-6", 7), (path, "F-FT-8", 10), (path, "F-FT-9", 11), (path, "F-FT-7", 12), (path, "F-FT-11", 13), (path, "F-FT-10", 14)],
            Found(check));
        Assert.Equal(0, check.ErrorCount);
        Assert.Contains("condition is one of active, damaged, lost, not 'damagd'. Did you mean 'damaged'?", Message(check, "F-FT-8"), StringComparison.Ordinal);
        Assert.StartsWith("crew is a number", Message(check, "F-FT-6"), StringComparison.Ordinal);
        Assert.StartsWith("refits: 'a1:3.2': scenes are not supported yet", Message(check, "F-FT-10"), StringComparison.Ordinal);
        Assert.Contains("calendar", Message(check, "F-FT-11"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("species: \"[[velari]]\"")]
    [InlineData("species: ~")]
    [InlineData("species: { value: ~, from: \"a1:2\" }")]
    [InlineData("born: { text: late spring, sort: [2291, 5] }")]
    [InlineData("occupation: deckhand")]
    [InlineData("factions: [\"[[tessaly-crew]]\"]")]
    public void ValuesThatMatchTheirFieldTypesAreQuiet(string field)
    {
        var source = Den();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", $"fields:\n  {field}"));

        Assert.DoesNotContain(DenCheck.Run(source).Diagnostics, d => d.Diagnostic.RuleId.StartsWith("F-FT-", StringComparison.Ordinal) || d.Diagnostic.RuleId == "F-FLD-6");
    }

    [Theory]
    [Trait("Scenario", "SC-CHECK-15")]
    [InlineData("species: [[velari]]", "[[velari]]")]
    [InlineData("factions: [[tessaly-crew]]", "[[tessaly-crew]]")]
    [InlineData("species: { value: [[velari]], from: \"a1:4\" }", "[[velari]]")]
    [InlineData("kin: [[orrin]]", "[[orrin]]")]
    public void AnUnquotedLinkIsAListInsideAListAndSaysToQuoteIt(string field, string link)
    {
        var source = Den();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", $"fields:\n  {field}"));

        var check = DenCheck.Run(source);

        // One warning that says what went wrong, not a second one about the field type.
        var found = check.Diagnostics.Where(d => d.Diagnostic.RuleId != "F-FLD-2").ToList();
        Assert.Equal(0, check.ErrorCount);
        var d = Assert.Single(found);
        Assert.Equal("F-FLD-6", d.Diagnostic.RuleId);
        Assert.EndsWith($"without quotes, YAML reads {link} as a list inside a list, not a link. Write \"{link}\".", d.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Scenario", "SC-CHECK-16")]
    [InlineData("born:\n    - { value: \"2291\" }\n    - { value: \"2290\", from: \"a2:1\" }", 8, "it has a list of entries")]
    [InlineData("died: { value: \"2301\", from: \"a1:5\", until: \"a2:1\" }", 7, "this entry has an until")]
    [InlineData("placeholder:\n    - { value: true }\n    - { value: false, from: \"a2:1\" }", 8, "placeholder isn't temporal on any type")]
    public void EntriesOnAFieldThatIsntTemporalAreWarned(string field, int line, string text)
    {
        var source = Den();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", $"fields:\n  {field}"));

        var check = DenCheck.Run(source);

        Assert.Equal([(Orrin, "F-TMP-8", line)], Found(check));
        Assert.Contains(text, Message(check, "F-TMP-8"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("died: { value: \"2301\", from: \"a2:5\", pin: true, status: canon }")]
    [InlineData("died: { value: \"2301\", from: \"a2:5\", until: ~ }")]
    [InlineData("role:\n    - { value: deckhand }\n    - { value: captain, from: \"a2:1\", until: \"a3:1\" }")]
    public void ASingleEntryWithoutUntilOrATemporalFieldsEntriesAreFine(string field)
    {
        var source = Den();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", $"fields:\n  {field}"));

        Assert.Empty(Found(DenCheck.Run(source)));
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-17")]
    public void AnEntryKeyThatIsntOneIsWarned()
    {
        // A typo: 'form' for 'from'. The entry starts at the start of the story until it's fixed.
        var source = Den();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "fields:\n  role:\n    - { value: deckhand, form: \"a1:3\" }"));

        var check = DenCheck.Run(source);

        Assert.Equal([(Orrin, "F-TMP-2", 8)], Found(check));
        Assert.Equal("role: 'form' isn't an entry key (an entry has value, from, until, status, and pin). It's kept, but does nothing.", Message(check, "F-TMP-2"));
    }

    // ----- F-FLD-7: row -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-18")]
    public void ARowFieldThatDoesntMatchTheOutlineIsWarned()
    {
        var source = Den()
            .With($"{A1}/outline.md", Outline("1 | 1 | Departure.", "2 | 2 | The storm."))
            .With($"{A2}/outline.md", Outline("3 | 1 | Landfall."));
        Note(source, A1, "the-storm", "type: event\nname: The storm\nstatus: canon\nfields:\n  row: [2, 3]", folder: "events");
        Note(source, A1, "the-wreck", "type: event\nname: The wreck\nstatus: canon\nfields:\n  row: 4", folder: "events");
        Note(source, A1, "the-calm", "type: event\nname: The calm\nstatus: canon\nfields:\n  row: 02", folder: "events");
        Note(source, A1, "the-fog", "type: event\nname: The fog\nstatus: canon\nfields:\n  row: { value: 1, from: \"a1:1\" }", folder: "events");

        var check = DenCheck.Run(source);

        Assert.Equal(
            [
                (A1 + "/entities/events/the-calm.md", "F-FLD-7", 7),
                (A1 + "/entities/events/the-fog.md", "F-FLD-7", 7),
                (A1 + "/entities/events/the-wreck.md", "F-FLD-7", 7),
            ],
            Found(check));
        Assert.Contains(check.Diagnostics, d => d.Diagnostic.Message == "row: there's no row 4 in the outlines of series 'a', so this entity isn't tied to a row.");
        Assert.Contains(check.Diagnostics, d => d.Diagnostic.Message.StartsWith("row: '02' isn't a row id", StringComparison.Ordinal));
        Assert.Contains(check.Diagnostics, d => d.Diagnostic.Message == "row: rows have no positions, so a row entry shouldn't have a from or until. Remove it.");
    }

    [Fact]
    public void ARowIdFoundInTwoSeriesIsAmbiguousForAUniverseEntityButNotForABookEntity()
    {
        var source = Den()
            .With($"{A1}/outline.md", Outline("7 | 1 | Departure."))
            .With($"{B1}/outline.md", Outline("7 | 1 | Arrival."));
        Note(source, Vael, "the-crossing", "type: event\nname: The crossing\nstatus: canon\nfields:\n  row: 7", folder: "events");
        Note(source, A1, "departure", "type: event\nname: Departure\nstatus: canon\nfields:\n  row: 7", folder: "events");

        var check = DenCheck.Run(source);

        Assert.Equal([(Vael + "/entities/events/the-crossing.md", "F-FLD-7", 7)], Found(check));
        Assert.StartsWith("row: row 7 is ambiguous: it's in more than one place in the outlines of universe 'vael'", Message(check, "F-FLD-7"), StringComparison.Ordinal);
    }

    // ----- F-ENT-6 and F-SEC-9 -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-19")]
    public void ABookEntityWhoseFromIsInAnotherBookIsWarned()
    {
        var source = Den();
        Note(source, A2, "the-stranger", Character("The Stranger", "from: \"a3:2\""));
        Note(source, A2, "wren", Character("Wren", "from: \"a2:5\""));
        Note(source, A, "hale", Character("Hale", "from: \"a3:2\""));

        var check = DenCheck.Run(source);

        Assert.Equal([(A2 + "/entities/characters/the-stranger.md", "F-ENT-6", 6)], Found(check));
        Assert.StartsWith("from a3:2 is in book 'a3', but this entity is filed in book 'a2'.", Message(check, "F-ENT-6"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-20")]
    public void ASubsectionOutsideItsParentIsWarned()
    {
        var source = Den();
        Note(source, A1, "orrin-fell", Character("Orrin Fell"), """

            ## Shoulder injury
            <!-- wolfden: from=a1:10 -->
            Hit at the reef.

            ### Recovery
            <!-- wolfden: from=a1:5 -->
            Months in a sling.

            ## Appearance
            Young.

            ### Scar
            <!-- wolfden: until=a3:1 -->
            A scar on his cheek.

            ### Limp
            <!-- wolfden: from=a2:5 -->
            He limps.

            ## Appearance
            <!-- wolfden: from=a2:1 -->
            Grey.
            """);

        var check = DenCheck.Run(source);

        Assert.Equal([(Orrin, "F-SEC-9", 13), (Orrin, "F-SEC-9", 20), (Orrin, "F-SEC-9", 24)], Found(check));
        var messages = check.Diagnostics.Select(d => d.Diagnostic.Message).ToList();
        Assert.Equal("'Recovery': from a1:5 is before its parent 'Shoulder injury' starts (a1:10), so the subsection starts with its parent instead.", messages[0]);
        Assert.Equal("'Scar': until a3:1 is after its parent 'Appearance' ends, so the subsection ends with its parent instead.", messages[1]);
        Assert.Equal("'Limp': from a2:5 isn't before its parent 'Appearance' ends, so the subsection is never in effect.", messages[2]);
    }

    // ----- Layout: F-LAY-1, F-LAY-3, F-LAY-6, F-LAY-7 -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-21")]
    public void AMarkdownFileDirectlyInAContainerFolderIsWarned()
    {
        var source = Den()
            .With($"{A1}/scratch.md", "Ideas for the storm.\n")
            .With($"{A1}/outline.md", Outline("1 | 1 | Departure."))
            .With($"{A1}/cover.png", "not really a png")
            .With($"{A1}/.draft.md", "hidden\n")
            .With($"{A}/outline.md", Outline("1 | 1 | Departure."));

        var check = DenCheck.Run(source);

        Assert.Equal([(A1 + "/scratch.md", "F-LAY-7", null), (A + "/outline.md", "F-LAY-7", null)], Found(check));
        Assert.Contains(check.Diagnostics, d => d.Diagnostic.Message.StartsWith("An outline is read only in a book folder", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-22")]
    public void AnEntityInAnotherTypesFolderIsWarned()
    {
        var source = Den();
        Note(source, A1, "orrin-fell", Character("Orrin Fell"), folder: "locations");
        Note(source, A1, "hale", Character("Hale"), folder: "crew");
        Note(source, A1, "pike", Character("Pike"), folder: "characters/minor");
        Note(source, A1, "thing", "type: gizmo\nname: Thing", folder: "locations");

        var check = DenCheck.Run(source);

        Assert.Equal(
            [(A1 + "/entities/locations/orrin-fell.md", "F-LAY-6", 3), (A1 + "/entities/locations/thing.md", "F-ENT-2", 3)],
            Found(check));
        Assert.Contains(check.Diagnostics, d => d.Diagnostic.Message.StartsWith("This character is in entities/locations/, the folder for type 'location'.", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEntityDirectlyInEntitiesIsWarned()
    {
        var source = Den().With($"{A1}/entities/wren.md", $"---\nid: {MemoryDenSource.ContainerId("wren")}\n{Character("Wren")}\n---\n");

        var check = DenCheck.Run(source);

        Assert.Equal([(A1 + "/entities/wren.md", "F-LAY-6", 3)], Found(check));
        Assert.Equal("This character is directly in entities/. Notes go in their type's folder: move it to entities/characters/.", Message(check, "F-LAY-6"));
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-23")]
    public void ADenYamlBelowTheRootIsWarned()
    {
        var source = Den()
            .With("archive/old-den/den.yaml", "format: 1\n")
            .With($"{A1}/entities/characters/den.yaml", "format: 1\n")
            .With(".backup/den.yaml", "format: 1\n");

        var check = DenCheck.Run(source);

        Assert.Equal([("archive/old-den/den.yaml", "F-LAY-1", null), (A1 + "/entities/characters/den.yaml", "F-LAY-1", null)], Found(check));
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-24")]
    public void NotesAndAuthorsAtTheDenRootAreReserved()
    {
        var source = Den()
            .With("notes/style-guide.md", "# Style\n")
            .With("authors/wren.md", "# Wren\n")
            .With("drafts/chapter-one.md", "Draft.\n");

        var check = DenCheck.Run(source);

        Assert.Equal([("authors", "F-LAY-3", null), ("notes", "F-LAY-3", null)], Found(check));
    }

    // ----- Outlines: F-OUT-*, and #80 -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-25")]
    public void OutlineRowsOutOfOrderOrRepeatedAreWarned()
    {
        // Splitting row 38, the writer typed 38 twice and put 36 after them.
        var source = Den()
            .With($"{A1}/outline.md", Outline("37 | 1 | Wakes.", "38 | 2 | Lands.", "38 | 2 | The ambush.", "36 | 3 | Hit."))
            .With($"{A2}/outline.md", Outline("40 | 1 | Recovers.", "37 | 2 | Takes command."));

        var check = DenCheck.Run(source);

        const string a1 = A1 + "/outline.md", a2 = A2 + "/outline.md";
        Assert.Equal(
            [
                (a1, "F-OUT-5", 5), (a1, "F-OUT-5", 6), (a1, "F-OUT-5", 7), (a1, "F-OUT-4", 8),
                (a2, "F-OUT-4", 6), (a2, "F-OUT-5", 6), (a2, "F-OUT-6", 6),
            ],
            Found(check));
        Assert.Contains(check.Diagnostics, d => d.Diagnostic.Message == "Row 36 comes after row 38 (line 7) in the file, but before it in row order. Keep rows in ascending order.");
        Assert.Contains(check.Diagnostics, d => d.Path == a2 && d.Diagnostic.RuleId == "F-OUT-5"
            && d.Diagnostic.Message.StartsWith($"Row id 37 is used more than once in the outlines of series 'a' (also {a1}:5)", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-26")]
    public void RowsThatRestartInTheNextBookAreWarned()
    {
        var source = Den()
            .With($"{A1}/outline.md", Outline("1 | 1 | Departure.", "2 | 2 | The storm."))
            .With($"{A2}/outline.md", Outline("1 | 1 | Landfall.", "4 | 2 | The wreck."));

        var check = DenCheck.Run(source);

        Assert.Equal(
            "Row 1, the lowest in book 'a2', isn't after row 2, the highest in book 'a1' before it. Row ids keep counting across a series, so every row of a book should come after the rows of the book before it.",
            Message(check, "F-OUT-6"));
        Assert.Equal([(A2 + "/outline.md", "F-OUT-6", 5)], Found(check, "F-OUT-6"));
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-27")]
    public void OutlineRowsWithMissingOrOutOfOrderChaptersAreWarned()
    {
        // Book 1 was cut to 24 chapters; and a beat moved up the table without its chapter.
        var source = Den().With($"{A1}/outline.md", Outline("1 | 5 | Departure.", "2 | 3 | Backstory.", "3 | 30 | Cut.", "4 | 6 | The storm."));

        var check = DenCheck.Run(source);

        const string path = A1 + "/outline.md";
        Assert.Equal([(path, "F-OUT-10", 6), (path, "F-OUT-9", 7)], Found(check));
        Assert.Equal("Row 2 is mapped to chapter '3', which comes before chapter '5' of row 1 (line 5) above it. Rows should follow chapter order.", Message(check, "F-OUT-10"));
        Assert.Equal("Row 3: '30' isn't a chapter of book 'a1', so the row isn't mapped to a chapter.", Message(check, "F-OUT-9"));
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-28")]
    public void AnOutlineTableThatDoesntReadAsMeantIsWarned()
    {
        var source = Den().With($"{A1}/outline.md", """
            # Outline

            | row | chapter | pov | POV | # | beat | status |
            |---|---|---|---|---|---|---|
            | 1 | 1 | [[orrin-fell|Orrin]] | | | Wakes. | |
            | 069 | 1 | | | | Bad id. | |
            | 2 | 2 | | | | Lands. | maybe |
            | | 2 | | | | No id. | |

            | row | beat |
            |---|---|
            | 3 | A second table in the same section. |

            ## Notes

            | row | chapter |
            |---|---|
            | 4 | 3 |
            """);

        var check = DenCheck.Run(source);

        // Row 1's unescaped | splits its pov cell and shifts "Wakes." into the status column. F-OUT-12 says so,
        // and the shifted status and chapter aren't checked as well.
        const string path = A1 + "/outline.md";
        Assert.Equal(
            [
                (path, "F-OUT-11", 3), (path, "F-OUT-11", 3),
                (path, "F-OUT-12", 5), (path, "F-OUT-12", 5),
                (path, "F-OUT-9", 6), (path, "F-OUT-9", 7), (path, "F-OUT-9", 8),
                (path, "F-OUT-3", 10),
                (path, "F-OUT-2", 16),
            ],
            Found(check).Where(d => d.Rule.StartsWith("F-OUT-", StringComparison.Ordinal)).ToArray());
        Assert.Equal(0, check.ErrorCount);
        var messages = check.Diagnostics.Select(d => d.Diagnostic.Message).ToList();
        Assert.Contains("The header 'POV' is the column 'pov' again, so this later column is ignored.", messages);
        Assert.Contains("The header '#' doesn't make a column name (lowercase letters, digits, and '_', starting with a letter), so the column is ignored.", messages);
        Assert.Contains("Row 2: 'maybe' isn't a status, so the row is read as draft. Use idea, undecided, candidate, draft, canon, or retconned.", messages);
        Assert.Contains("This table has a row column but no beat column, so it's a free-form note, not an outline table. Did you mean an outline table? Add a beat column.", messages);
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-29")]
    public void AWolfdenCommentInAnOutlineIsAWarningAndTheOutlineStillLoads()
    {
        // #80: wolfden comments do nothing in outline.md (F-SEC-10), so a bad value in one isn't an error.
        var source = Den().With($"{A1}/outline.md", """
            # Outline

            ## Act one
            <!-- wolfden: from=a1:14.2 status=maybe -->

            | row | chapter | beat |
            |---|---|---|
            | 1 | 1 | [[Nobody Here]] wakes. |
            """);

        var check = DenCheck.Run(source);

        const string path = A1 + "/outline.md";
        Assert.Equal(0, check.ErrorCount);
        Assert.Equal([(path, "F-SEC-10", 4), (path, "F-LINK-8", 8)], Found(check));
        Assert.StartsWith("A wolfden comment does nothing in outline.md", Message(check, "F-SEC-10"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutlineWithAnErrorIsReportedAndItsRowsAreLeftOut()
    {
        // F-GEN-2: an error excludes the outline, so its rows don't resolve row fields.
        var source = Den().With($"{A1}/outline.md", "---\ntitle: [Outline\n---\n" + Outline("1 | 1 | Departure."));
        Note(source, A1, "departure", "type: event\nname: Departure\nstatus: canon\nfields:\n  row: 1", folder: "events");

        var check = DenCheck.Run(source);

        Assert.Equal([(A1 + "/entities/events/departure.md", "F-FLD-7", 7), (A1 + "/outline.md", "F-YAML-1", 3)], Found(check));
        Assert.Equal(
            "row: row 1 isn't in the outlines that load, and the outline of book 'a1' has errors, so its rows aren't read. Until that's fixed, this entity isn't tied to row 1.",
            Message(check, "F-FLD-7"));
    }

    [Fact]
    public void ARowEntryWithUntilIsOnlyARowWarning()
    {
        // F-FLD-7 covers a row entry's until, so it doesn't get F-TMP-8 as well.
        var source = Den().With($"{A1}/outline.md", Outline("7 | 1 | Departure."));
        Note(source, A1, "departure", "type: event\nname: Departure\nstatus: canon\nfields:\n  row: { value: 7, until: \"a1:3\" }", folder: "events");

        var check = DenCheck.Run(source);

        Assert.Equal([(A1 + "/entities/events/departure.md", "F-FLD-7", 7)], Found(check));
    }

    [Fact]
    public void ABookWhoseLowestRowEqualsThePreviousBooksHighestIsWarned()
    {
        var source = Den()
            .With($"{A1}/outline.md", Outline("1 | 1 | Departure.", "2 | 2 | The storm."))
            .With($"{A2}/outline.md", Outline("2 | 1 | Landfall.", "3 | 2 | The wreck."));

        var check = DenCheck.Run(source);

        Assert.Equal([(A2 + "/outline.md", "F-OUT-6", 5)], Found(check, "F-OUT-6"));
    }

    [Fact]
    public void LinksInCodeSpansAndShiftedRowsDontAddOutlineWarnings()
    {
        var source = Den().With($"{A1}/outline.md", """
            # Outline

            | row | chapter | beat | status |
            |---|---|---|---|
            | 1 | 1 | Type `[[` to start a link, or ``a ` [[ b``. | canon |
            | 2 | [[x|y]] | 2 | Shifted. | |
            """);

        var check = DenCheck.Run(source);

        // Row 2's stray | gives its extra-cell and unclosed-link warnings, not a chapter or status one.
        const string path = A1 + "/outline.md";
        Assert.Equal([(path, "F-OUT-12", 6), (path, "F-OUT-12", 6)], Found(check));
    }

    [Fact]
    public void AFieldWhoseDefinitionIsBrokenSaysSo()
    {
        // types.md F-FT-2: a broken definition is ignored, so the field is undefined until it's fixed.
        var source = Den("packs: [fleet]\n")
            .With("packs/fleet/pack.yaml", "name: Fleet\n")
            .With("packs/fleet/extensions/character.yaml", "fields:\n  rank: { type: rnak }\n");
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "fields:\n  rank: Captain"));

        var check = DenCheck.Run(source);

        Assert.Equal(
            "Type 'character' doesn't define the field 'rank': its definition in packs/fleet/extensions/character.yaml is broken, so it's ignored (see the warning there). Until that's fixed, the value is kept and indexed as written.",
            Message(check, "F-FLD-2"));
    }

    [Fact]
    public void AnOutlineParsedAsAnOutlineHasNoSectionCommentsOrCommentErrors()
    {
        var text = "## Act one\n<!-- wolfden: from=a1:14.2 -->\n\n# Act two\n"u8;

        var outline = MarkdownFile.ParseOutline(text);
        var note = MarkdownFile.Parse(text);

        Assert.True(outline.IsOutline);
        Assert.False(outline.HasErrors);
        Assert.All(outline.Sections, s => Assert.Null(s.Comment));
        Assert.Equal([("F-SEC-10", DiagnosticSeverity.Warning)], outline.Diagnostics.Select(d => (d.RuleId, d.Severity)));
        Assert.Contains(note.Diagnostics, d => d is { RuleId: "F-POS-2", Severity: DiagnosticSeverity.Error });
        Assert.Contains(note.Diagnostics, d => d.RuleId == "F-SEC-2");
    }

    // ----- den.yaml authors and container authors: F-DEN-3, F-CTR-2 -----

    [Theory]
    [Trait("Scenario", "SC-CHECK-30")]
    [InlineData("authors: [wren-calder]", "F-DEN-3", 2)]
    [InlineData("authors: wren-calder", "F-DEN-3", 2)]
    [InlineData("authors:\n  wren-calder: Wren Calder", "F-DEN-3", 3)]
    [InlineData("authors:\n  wren-calder: { pen: true }", "F-DEN-3", 3)]
    [InlineData("authors:\n  wren-calder: { name: \"  \" }", "F-DEN-3", 3)]
    [InlineData("authors:\n  Wren Calder: { name: Wren Calder }", "F-KEY-1", 3)]
    public void AnAuthorsThatIsntAMappingOfNamedIdentitiesIsWarnedAndTheDenStillLoads(string authors, string rule, int line)
    {
        var check = DenCheck.Run(MemoryDenSource.Den().With("den.yaml", $"format: 1\n{authors}\n"));

        Assert.True(check.IsLoaded);
        Assert.Equal(1, check.Structure.Format);
        Assert.Empty(check.Structure.Authors);
        Assert.Equal([("den.yaml", rule, line)], Found(check));
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-30")]
    public void ASkippedAuthorIsUnknownToTheContainersThatNameIt()
    {
        var source = Den("authors:\n  wren-calder: Wren Calder\n  ash-morrow: { name: Ash Morrow }\n")
            .Series("vael", "a", "books: [a1, a2, a3, a4, a5]", "authors: [wren-calder, ash-morrow]");

        var check = DenCheck.Run(source);

        Assert.True(check.IsLoaded);
        Assert.Equal(["ash-morrow"], check.Structure.Authors);
        Assert.Equal([("den.yaml", "F-DEN-3", 4), (A + "/series.md", "F-CTR-2", 4)], Found(check));
        Assert.Equal("Author 'wren-calder' must be a mapping with a name, such as '{ name: Wren Calder }', so it's skipped.", Message(check, "F-DEN-3"));
        Assert.Equal("'wren-calder' isn't an author in den.yaml (den.yaml's authors are ash-morrow).", Message(check, "F-CTR-2"));
    }

    [Theory]
    [InlineData("packs: [nope]", "F-DEN-4")]
    [InlineData("packs: nope", "F-DEN-4")]
    [InlineData("bookkeeping_sections: 7", "F-DEN-6")]
    public void ADenYamlThatDoesntLoadStillHasTheFormatItParsed(string line, string rule)
    {
        var check = DenCheck.Run(MemoryDenSource.Den().With("den.yaml", $"format: 1\nauthors: [wren-calder]\n{line}\n"));

        Assert.False(check.IsLoaded);
        Assert.Equal(1, check.Structure.Format);

        // Every key is still read, so the authors warning is reported too (F-GEN-3).
        Assert.Equal([("F-DEN-3", DiagnosticSeverity.Warning), (rule, DiagnosticSeverity.Error)],
            check.Diagnostics.Select(d => (d.Diagnostic.RuleId, d.Diagnostic.Severity)));
    }

    [Fact]
    public void AuthorsWithNamesAndOtherKeysLoad()
    {
        var check = DenCheck.Run(MemoryDenSource.Den().With("den.yaml", "format: 1\nauthors:\n  wren-calder: { name: Wren Calder, x-pen: true }\n  ash: { name: Ash }\n"));

        Assert.True(check.IsLoaded);
        Assert.Equal(["wren-calder", "ash"], check.Structure.Authors);
        Assert.Empty(check.Diagnostics);
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-31")]
    public void AContainerAuthorThatDenYamlDoesntListIsWarned()
    {
        var source = Den("authors:\n  wren-calder: { name: Wren Calder }\n")
            .Series("vael", "a", "books: [a1, a2, a3, a4, a5]", "authors: [wren-calder, ash-morrow]")
            .Series("vael", "b", "books: [b1]", "authors: wren-calder");

        var check = DenCheck.Run(source);

        Assert.Equal([(A + "/series.md", "F-CTR-2", 4), (Vael + "/series/b/series.md", "F-CTR-2", 4)], Found(check));
        Assert.Contains(check.Diagnostics, d => d.Diagnostic.Message == "'ash-morrow' isn't an author in den.yaml (den.yaml's authors are wren-calder).");
        Assert.Contains(check.Diagnostics, d => d.Diagnostic.Message.StartsWith("authors must be a list of author keys", StringComparison.Ordinal));
    }

    // ----- F-ID-2 for container files (#100) -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-32")]
    public void AContainerFileWithoutAnIdIsWarnedLikeAnEntity()
    {
        var source = Den()
            .Universe("vael", "order: [a, b]", "id:")
            .Book("vael", "a", "a2", "chapters: [1, 2]", "status: final", "id:");

        var check = DenCheck.Run(source);

        Assert.Equal([(A2 + "/book.md", "F-ID-2", 1), (Vael + "/universe.md", "F-ID-2", 1)], Found(check));
        Assert.Equal("no id; run `wolfden check --fix` to add one. Until then, the index knows this universe by its path.", check.Diagnostics[1].Diagnostic.Message);
        Assert.Equal(
            "no id, and book 'a2' is final (frozen), so `wolfden check --fix` won't add one until the book is unfrozen. Until then, the index knows this book by its path.",
            check.Diagnostics[0].Diagnostic.Message);
    }

    // ----- Statuses and pins: F-STAT-3, F-PIN-3 -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-34")]
    public void ACanonFactInsideAnIdeaIsWarned()
    {
        var source = Den();
        Note(source, A1, "wren", "type: character\nname: Wren\nstatus: idea\nfields:\n  role: { value: deckhand, status: canon }", """

            ## Backstory
            <!-- wolfden: status=canon -->
            Born on Vael.

            ### Childhood
            Grew up on the docks.

            ## Voice
            <!-- wolfden: status=candidate -->
            Clipped.
            """);
        Note(source, A1, "orrin-fell", Character("Orrin Fell"), """

            ## Rumors
            <!-- wolfden: status=idea -->

            ### The wreck
            <!-- wolfden: status=draft -->
            He sank the Kestrel himself.
            """);

        var check = DenCheck.Run(source);

        const string wren = A1 + "/entities/characters/wren.md";
        Assert.Equal([(Orrin, "F-STAT-3", 12), (wren, "F-STAT-3", 7), (wren, "F-STAT-3", 11)], Found(check));
        var messages = check.Diagnostics.Select(d => d.Diagnostic.Message).ToList();
        Assert.Equal("'The wreck' is marked draft, but its parent 'Rumors' is idea, so it's read as idea: a fact can't be more canon than what holds it.", messages[0]);
        Assert.Equal("role: this entry is marked canon, but the entity is idea, so it's read as idea: a fact can't be more canon than what holds it.", messages[1]);
        Assert.Equal("'Backstory' is marked canon, but the entity is idea, so it's read as idea: a fact can't be more canon than what holds it.", messages[2]);
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-35")]
    public void APinnedFactThatsOutOfCanonIsWarned()
    {
        var source = Den();
        Note(source, A1, "orrin-fell", Character("Orrin Fell", "fields:\n  died: { value: \"2301\", status: candidate, pin: true }"), """

            ## Shoulder injury
            <!-- wolfden: status=undecided pin=true -->

            ### Hit at the reef
            <!-- wolfden: status=candidate -->
            He fell on the rocks.

            ## Scar
            <!-- wolfden: status=draft pin=true -->
            A scar on his cheek.
            """);

        var check = DenCheck.Run(source);

        // The candidate subsection is pinned through its parent (F-PIN-2), but the warning goes where pin=true is written.
        Assert.Equal([(Orrin, "F-PIN-3", 7), (Orrin, "F-PIN-3", 11)], Found(check));
        var messages = check.Diagnostics.Select(d => d.Diagnostic.Message).ToList();
        Assert.Equal("died: this entry is pinned, but it's candidate, which is out of canon, so pinned canon leaves it out.", messages[0]);
        Assert.Equal("'Shoulder injury' is pinned, but it's undecided, which is out of canon, so pinned canon leaves it out.", messages[1]);
    }

    [Fact]
    public void APinnedSectionInAnIdeaIsWarnedOnceForItsStatus()
    {
        // The section takes idea from its entity, so it's out of canon. With status=canon written, F-STAT-3 says so instead.
        var source = Den();
        Note(source, A1, "wren", "type: character\nname: Wren\nstatus: idea", """

            ## Backstory
            <!-- wolfden: pin=true -->
            Born on Vael.

            ## Voice
            <!-- wolfden: status=canon pin=true -->
            Clipped.
            """);

        var check = DenCheck.Run(source);

        const string wren = A1 + "/entities/characters/wren.md";
        Assert.Equal([(wren, "F-PIN-3", 9), (wren, "F-STAT-3", 13)], Found(check));
    }

    // ----- Bookkeeping: F-SEC-12, F-SEC-7 -----

    [Fact]
    [Trait("Scenario", "SC-CHECK-36")]
    public void PinningABookkeepingSectionIsWarned()
    {
        var source = Den("bookkeeping_sections: [\"Scratch\"]\n");
        Note(source, A1, "orrin-fell", Character("Orrin Fell"), """

            ## Change log
            <!-- wolfden: pin=true -->
            - Moved the wreck to the reef.

            ### Old notes
            <!-- wolfden: pin=true -->
            Kept for reference.

            ## Scratch
            <!-- wolfden: canon=true pin=true -->
            He hates the cold.
            """);
        const string universe = Vael + "/universe.md";
        source.With(universe, System.Text.Encoding.UTF8.GetString(source.ReadFile(universe)) + "\n## Changelog 2026\n<!-- wolfden: pin=true -->\n");

        var check = DenCheck.Run(source);

        Assert.Equal([(Orrin, "F-SEC-12", 9), (Orrin, "F-SEC-12", 13), (universe, "F-SEC-12", 8)], Found(check));
        Assert.Equal("'Change log' is a bookkeeping section, so pin=true does nothing: pinned canon leaves bookkeeping out.", check.Diagnostics[0].Diagnostic.Message);
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-37")]
    public void CanonTrueUnderABookkeepingSectionIsWarned()
    {
        var source = Den();
        Note(source, A1, "orrin-fell", Character("Orrin Fell"), """

            ## Session notes
            Worked on the reef chapter.

            ### The reef
            <!-- wolfden: canon=true -->
            Orrin was hit at the reef.

            ## Changelog 2026
            <!-- wolfden: canon=true -->
            Kept as story.

            ## Sources
            <!-- wolfden: canon=false -->

            ### Reading list
            <!-- wolfden: canon=true -->
            """);

        var check = DenCheck.Run(source);

        Assert.Equal([(Orrin, "F-SEC-7", 12), (Orrin, "F-SEC-7", 23)], Found(check));
        Assert.Equal(
            "'The reef': canon=true does nothing here, since its parent 'Session notes' is bookkeeping, and so are its subsections. To keep it in the story, move it out of 'Session notes'.",
            check.Diagnostics[0].Diagnostic.Message);
    }

    // ----- The core pack: F-CORE-2, F-CORE-3 -----

    private const string Manifest = A1 + "/entities/threads/the-manifest.md";

    private static MemoryDenSource Thread(string fields) =>
        Note(Den(), A1, "the-manifest", $"type: thread\nname: The manifest\nstatus: canon\nfields:\n{fields}", folder: "threads");

    [Fact]
    [Trait("Scenario", "SC-CHECK-38")]
    public void AThreadPaidOffBeforeItsPlantedIsWarned()
    {
        var check = DenCheck.Run(Thread("  planted: [\"a1:5\", \"a1:3\"]\n  paid_off: \"a1:2\""));

        Assert.Equal([(Manifest, "F-CORE-2", 8)], Found(check));
        Assert.Equal("paid_off a1:2 comes before the thread is first planted, at a1:3, in story order. Check the positions.", Message(check, "F-CORE-2"));
    }

    [Theory]
    [InlineData("  planted: \"a1:5\"\n  paid_off: { value: \"a1:2\", status: canon }", true)]
    [InlineData("  planted: [\"a1:3\"]\n  paid_off: \"a1:3\"", false)]
    [InlineData("  planted: [\"a1:3\"]\n  paid_off: \"a2:1\"", false)]
    [InlineData("  planted: [\"a1:5\"]\n  paid_off: { value: \"a1:2\", status: candidate }", false)]
    [InlineData("  planted:\n    - { value: [\"a1:5\"] }\n    - { value: [\"a1:6\"], from: \"a1:4\" }\n  paid_off: \"a1:2\"", false)]
    [InlineData("  paid_off: \"a1:2\"", false)]
    public void APaidOffIsComparedOnlyWithPlantedPositionsThatAreSet(string fields, bool warned)
    {
        var check = DenCheck.Run(Thread(fields));

        Assert.Equal(warned, check.Diagnostics.Any(d => d.Diagnostic.RuleId == "F-CORE-2"));
    }

    private static MemoryDenSource Event(string fields) =>
        Note(Den(), A1, "the-storm", $"type: event\nname: The storm\nstatus: canon\nfields:\n{fields}", folder: "events");

    [Fact]
    [Trait("Scenario", "SC-CHECK-39")]
    public void AnEventThatEndsBeforeItStartsIsWarned()
    {
        var check = DenCheck.Run(Event("  when: \"2291-05-14\"\n  ends: { text: the spring before, sort: [2291, 4] }"));

        Assert.Equal([(A1 + "/entities/events/the-storm.md", "F-CORE-3", 8)], Found(check));
        Assert.Equal("ends the spring before is earlier than when 2291-05-14, so the event ends before it starts. Check the dates.", Message(check, "F-CORE-3"));
    }

    [Theory]
    [InlineData("  when: \"2291-05-14\"\n  ends: \"2291-05-02\"", true)]
    [InlineData("  when: \"-450\"\n  ends: \"-452\"", true)]
    [InlineData("  when: \"2291-05-14 06:40\"\n  ends: \"2291-05-14\"", false)]
    [InlineData("  when: \"2291-05\"\n  ends: \"2291\"", false)]
    [InlineData("  when: \"2291-05-14\"\n  ends: \"2291-05-14 23:00\"", false)]
    [InlineData("  when: \"2291-05-14\"\n  ends: the next spring", false)]
    [InlineData("  when: { sort: [2291, 5] }\n  ends: { sort: 2290 }", true)]
    public void AnEventsDatesAreComparedOnlyOverTheNumbersBothHave(string fields, bool warned)
    {
        var check = DenCheck.Run(Event(fields));

        Assert.Equal(warned, check.Diagnostics.Any(d => d.Diagnostic.RuleId == "F-CORE-3"));
    }

    [Fact]
    public void ADenLocalThreadOrEventIsNotCheckedByTheCoreRules()
    {
        // Like F-CORE-4, the core rules cover only the core pack's thread and event.
        var source = Den("packs: [saga]\n")
            .With("packs/saga/pack.yaml", "name: Saga\n")
            .With("packs/saga/types/arc.yaml", "name: Arc\nplural: arcs\nfields:\n  planted: { type: position, list: true }\n  paid_off: position\n");
        Note(source, A1, "the-manifest", "type: arc\nname: The manifest\nstatus: canon\nfields:\n  planted: [\"a1:5\"]\n  paid_off: \"a1:2\"", folder: "arcs");

        Assert.Empty(Found(DenCheck.Run(source)));
    }
}
