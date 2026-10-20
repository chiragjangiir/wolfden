# Wolfden Writer Scenarios

This is a running list of situations that real writers run into, written
from the writer's side. Each one says what the writer does and what
Wolfden must show them. The scenarios aren't normative: the rules are in
[`format.md`](format.md) and [`types.md`](types.md). They are what the
rules have to get right in practice, and they're the starting point for
tests at every layer (Core, index, CLI, MCP, and the web UI).

## How to use this list

- Each scenario has a stable id (`SC-<AREA>-<n>`). Ids are never reused or
  renumbered. A scenario that no longer applies is marked **Withdrawn** and
  says why.
- A test that covers a scenario carries its id. In .NET tests, use
  `[Trait("Scenario", "SC-CANON-1")]`. One scenario can have tests at
  several layers.
- **Covered by** lists the test classes or files that exercise the scenario.
  "Not yet" is fine: a scenario can be written before the code exists.
- To add one, describe the situation in the writer's words, then the
  expected behavior, then the spec rules involved. Each scenario has a
  single expected outcome, so a test can check it. Invented names only. No
  real manuscripts or notes.

## The example world

The scenarios share one invented universe, and test fixtures use the same
keys:

| Key | What |
|---|---|
| `a` | The main series, five books in story order: `a1` to `a5` ("Book 1" to "Book 5") |
| `b` | A spin-off series in the same universe, after `a` in story order: `b1` |

Positions are written `a3:14` (Book 3, chapter 14), or `a3` for the start
of Book 3 (format.md §8).

## Areas

| Area | What it covers |
|---|---|
| CANON | What a canon-as-of view shows at a point in the story |
| CHECK | What `wolfden check` reports, and how it exits |
| RT | What saving a note keeps, and what an edit changes |
| CLI | Using the other `wolfden` commands: init, index, query, check --fix, new-id |
| WATCH | Keeping the index up to date while notes are edited outside Wolfden |

## CANON: what's canon at a point in the story

Background: a canon-as-of view is decided by **position, not folder**
(maintainer decision, 2026-10-19, #87). At a given chapter it shows
everything a reader has met by then, in story order, at every scope of the
universe. In the default, unfiltered view, where an entity is filed never
decides whether the view can include it. Filing does decide which entities a
scope filter keeps (#87), and it sets where an entity with no `from` enters canon (F-ENT-6),
how views order and label it (F-VIEW-3), and which match wins when links
tie (F-LINK-7).

### SC-CANON-1. A minor character returns in a later book

A side character first appears in Book 1, filed under `a1` with no `from`.
The writer brings them back in Book 4 and never moves the file out of
Book 1's folder.

- Views at any point in Book 2, 3, or 4 include the character.
- Links from Book 4 files to the character resolve.

Spec: F-VIEW-1, F-ENT-6, F-ORD-1, F-LINK-5, F-LINK-7. Covered by:
`CanonViewScenarioTests.ASideCharacterFiledUnderBookOneReturnsInBookFourWithoutBeingMoved`.

### SC-CANON-2. A throwaway character in Book 3 comes back in Book 5

The writer meant a Book 3 character as a one-off and filed them under `a3`
with no `from`. Later they decide to bring the character back in Book 5.

- Left in place: absent from Book 1 and 2 views, present from `a3` on,
  including Book 5.
- Moved up to the series folder with `from: a3` written in: every view
  includes the character at the same positions as before the move, with the
  same values. Only the character's place in the view's order and the scope it
  shows under change (F-VIEW-3).
- Moved up with no `from`: the character inherits the start of the series,
  which is the start of Book 1, and now appears from Book 1. That's the
  specified behavior. It's why a move made by the app or MCP should write
  the inherited `from` explicitly (#89).
- Links from Book 5 resolve in all three cases.

Spec: F-VIEW-1, F-ENT-6, F-ORD-3, F-LINK-5, F-LINK-7. Covered by:
`CanonViewScenarioTests.ThrowawayCharacterInBook3ReturnsInBook5`.

### SC-CANON-3. A character enters partway through a book

A character filed under `a1` has `from: a1:20`.

- Absent from a view at `a1:10`. Present at `a1:20` and in every later
  book.

Spec: F-ENT-6, F-VIEW-1. Covered by:
`CanonViewScenarioTests.ACharacterEntersPartwayThroughABook`.

### SC-CANON-4. A series-wide character who arrives late

A character filed at the series level (`a`) has `from: a3:14`.

- Absent at `a3:13` and from all earlier books, even though the file sits
  above every book. Present from `a3:14` on.

Spec: F-ENT-6, F-VIEW-1. Covered by:
`CanonViewScenarioTests.ASeriesLevelCharacterArrivesLateInTheSeries`.

### SC-CANON-5. A fact changes over time

A character's occupation is a temporal field with two entries: one with no
`from`, which starts at the start of the story, and a new value
`from: a2:5`.

- A Book 1 view shows the old occupation. A Book 4 view shows the new one.

Spec: F-TMP-2, F-TMP-3, F-TMP-4, F-TMP-5, F-VIEW-1. Covered by:
`CanonViewScenarioTests.AnOccupationChangesInBookTwo`.

### SC-CANON-6. A secret identity the reader learns at a set chapter

A Book 1 character has a non-temporal field for their true identity,
written as a single entry with `from: a3:20`. Here `from` means the reader
learns the fact at that point, not that the value changes.

- Views before `a3:20` leave the field out. Views from `a3:20` on show it.

Spec: F-TMP-8, F-TMP-4, F-VIEW-1. Covered by:
`CanonViewScenarioTests.AHiddenIdentityStaysHiddenUntilTheReveal`. See also #58
(who-knows-what tracking). A cover story that's later replaced by the truth
is a different case: on a temporal field it's SC-CANON-5, and in a section
it's covered by F-SEC-8 and F-SEC-9.

### SC-CANON-7. A spin-off series in the same world

Series `b` comes after series `a` in story order. Its entities have no
`from`, or a `from` inside `b`.

- Its entities are absent from views at any position in `a`, and present
  in the end-of-story view.
- A view filtered to series `a` leaves them out, even at the end of the
  story. Entities filed at universe scope stay in that filtered view.

Spec: F-VIEW-1, F-ORD-1, F-ORD-3, F-ENT-6; the filter rule is the #87
decision. Covered by:
`CanonViewScenarioTests.ASpinOffSeriesStaysOutOfTheMainSeries`.

### SC-CANON-8. Retconned and idea material stays out of canon views

One entity is marked `retconned` and another is still an `idea`.

- Neither appears in any canon-as-of or pinned canon view, including the
  end-of-story view.
- Both are still indexed, and views that ask for out-of-canon material
  (such as an idea bank) show them.

Spec: F-VIEW-1, F-VIEW-2, F-STAT-3, F-STAT-4, §9.3. Covered by:
`CanonViewScenarioTests.RetconnedAndIdeaMaterialNeverShows`, except
the out-of-canon view, which doesn't exist yet.

### SC-CANON-9. Two different side characters share a name

Book 1 and Book 2 each have their own minor character named "Officer
Hale". They're different people, each filed in their own book.

- A Book 4 view shows both, told apart by where each is filed.
- `[[Officer Hale]]` from a Book 1 file resolves to Book 1's Hale. From a
  Book 4 file it's ambiguous: a warning that lists both, and the link
  doesn't resolve.

Spec: F-VIEW-1, F-VIEW-3, F-LINK-7. Covered by:
`CanonViewScenarioTests.TwoCharactersShareANameInSiblingBooks`.

## CHECK: what `wolfden check` reports

Background: `wolfden check` (design.md §7) lists every error and warning
in a den, one per line with its file, line, and rule id, or as JSON with
`--json`. It exits 0 when there are no errors, even with warnings
(F-GEN-1), 1 when there's at least one error, and 2 when it couldn't
check the den at all: a wrong command line, a missing folder, or a
`den.yaml` that doesn't load. It reads the files as they are now, never
the index, and never changes them unless asked to with `--fix`
(SC-CLI-4).

### SC-CHECK-1. I renamed a character and old links broke

The writer renamed `mara-voss.md` to `mara-ellery.md` and changed her
`name` to "Mara Ellery" by hand, without keeping the old name as an alias.
Other notes still say `[[mara-voss]]` and `[[Mara Voss]]`.

- Each old link is a broken-link warning on its own line, and check still
  exits 0. Adding `aliases: [Mara Voss]` fixes the name link. The slug
  link stays broken until it's rewritten.

Spec: F-LINK-6, F-LINK-8, F-RT-5, F-GEN-1. Covered by:
`ContinuityCheckTests.RenamingACharacterWithoutAnAliasBreaksTheOldLinks`,
`CheckCommandTests.WarningsAloneExitZero`.

### SC-CHECK-2. A Book 2 note mentions someone who doesn't appear until Book 4

A location filed under `a2` links to a character filed under `a4` with no
`from`.

- With `spoiler_checks: true` in `den.yaml`, the link is a spoiler warning
  that says the character enters canon at the start of `a4`. Without it,
  there's no warning.

Spec: F-LINK-13, F-DEN-7, F-ENT-6. Covered by:
`ContinuityCheckTests.ABookTwoNoteThatNamesABookFourCharacterIsASpoilerOnlyWhenTheDenAsks`.

### SC-CHECK-3. A link matches two characters

Books 1 and 2 each have their own "Officer Hale". A Book 3 note links to
`[[Officer Hale]]`.

- The link is an ambiguous-link warning that names both files, and it
  doesn't resolve.

Spec: F-LINK-7. Covered by:
`ContinuityCheckTests.ALinkThatMatchesTwoCharactersIsAmbiguous`. See also
SC-CANON-9.

### SC-CHECK-4. I copied a character file and kept its id

To start a new character, the writer copied `orrin-fell.md` to
`orrin-fell-copy.md` and changed the name, but not the `id`.

- Both files get a duplicate-id warning. Until one gets a new id, links to
  either are broken, and check still exits 0.

Spec: F-ID-3, F-LINK-8. Covered by:
`ContinuityCheckTests.ACopiedFileWithTheSameIdIsCaughtOnBothCopies`.

### SC-CHECK-5. I cut chapters and a fact still points at one

Book 2 had 30 chapters, and the writer cut it to 24 in `book.md`. A
character's role still changes `from: "a2:27"`.

- That line is a warning that names `a2:27` and says Book 2 has no chapter
  27. The value stays in the file, out of canon views, until it's fixed.

Spec: F-POS-4. Covered by:
`ContinuityCheckTests.AFactThatPointsAtACutChapterIsWarned`.

### SC-CHECK-6. Two values for one field claim the same chapters

A character's occupation is "deckhand" from `a1:1` until `a2:10`, and
"harbourmaster" from `a2:5`. The writer moved the promotion earlier and
forgot the `until`.

- The first entry is an overlap warning that names the second one's line
  and `from`.

Spec: F-TMP-5, F-TMP-4. Covered by:
`ContinuityCheckTests.TwoPostingsThatClaimTheSameChaptersOverlap`.

### SC-CHECK-7. A note is missing what its type requires

The den's own `ship` type requires a `class` and a `crew` field and a
`## Crew` section. A quickly written ship note has none of them.

- Each missing field and the missing section is a warning naming it.

Spec: F-FLD-3, F-SEC-5, types.md F-FT-14, F-TYPE-11. Covered by:
`ContinuityCheckTests.AShipNoteWithoutItsRequiredFieldsAndSectionIsWarned`.

### SC-CHECK-8. A typo in a note's type

A note says `type: charcter`.

- It's an unknown-type warning that quotes `charcter`. The note still
  loads, untyped, and links to it work.

Spec: F-ENT-2. Covered by:
`ContinuityCheckTests.ATypoInATypeIsAnUnknownType`.

### SC-CHECK-9. A note doesn't parse

A note's frontmatter has a YAML mistake, such as `from: a1: 3`.

- It's an error with the file and line, the note is left out, and check
  exits 1.

Spec: F-YAML-1, F-GEN-1, F-GEN-2. Covered by:
`ContinuityCheckTests.ANoteThatDoesntParseIsAnErrorAndIsLeftOut`,
`CheckCommandTests.AnErrorExitsOne`.

### SC-CHECK-10. I reordered chapters and a range now ends before it starts

A value holds `from: "a1:5"` `until: "a1:12"`. The writer moved chapter
12 before chapter 5 in `book.md`.

- The entry is a warning that says its `until` isn't after its `from`, so
  it's never in effect.

Spec: F-TMP-3, F-GEN-4. Covered by:
`ContinuityCheckTests.AnEntryWhoseUntilIsNoLongerAfterItsFromIsNeverInEffect`.

### SC-CHECK-11. A new Appearance for a time skip, without its `from`

The writer added a second `## Appearance` for the character after a time
skip, but forgot the `<!-- wolfden: from=... -->` comment under it.

- The second section is an overlap warning, since both start where the
  character enters canon. Views show both until it's fixed.

Spec: F-SEC-4, F-SEC-8. Covered by:
`ContinuityCheckTests.ASecondAppearanceWithoutAFromOverlapsTheFirst`.

### SC-CHECK-12. My AI assistant fixes what check finds

The writer asks an AI assistant to clean up the den's warnings. The
assistant runs `wolfden check --json`.

- It gets one JSON document with a version number, the counts, and each
  diagnostic's path, line (or null), severity, rule id, and message, in a
  fixed order.

Spec: F-GEN-3. Covered by:
`CheckCommandTests.JsonOutputMatchesTheGoldenFile`.

### SC-CHECK-13. A typo in a field name

A character's note says `occupaton: [deckhand]`.

- It's a warning that the character type doesn't define `occupaton`. The
  value is kept and indexed as written. The universal `row` and
  `placeholder` never get this warning, and an untyped note gets no field
  warnings at all.

Spec: F-FLD-2, F-ENT-2, types.md §5.5. Covered by:
`SpecWarningCheckTests.AFieldTheTypeDoesntDefineIsWarned`.

### SC-CHECK-14. A value that doesn't fit its field

The den's own `ship` type has a number `crew`, an enum `condition`, and a
`ref` to a captain. A ship note says `crew: "140"`, `condition: damagd`,
and `captain: Orrin Fell` without a link.

- Each value is a warning that names the field and what it should be,
  and the misspelled enum value suggests `damaged`. In a temporal list,
  each entry's value is checked. Nothing is an error, and the values are
  kept as written.

Spec: F-FLD-3, types.md F-FT-4 to F-FT-12. Covered by:
`SpecWarningCheckTests.ValuesThatDontMatchTheirFieldTypesAreWarned`.

### SC-CHECK-15. A link without quotes

A character's note says `species: [[velari]]`, which YAML reads as a list
inside a list.

- It's one warning that says to write `"[[velari]]"` in quotes, not a
  second one about the field's type.

Spec: F-FLD-6. Covered by:
`SpecWarningCheckTests.AnUnquotedLinkIsAListInsideAListAndSaysToQuoteIt`.

### SC-CHECK-16. Values that change, on a field that doesn't

The writer gave a character's `born` (not a temporal field) a list of
values from different chapters.

- It's a warning that the field isn't temporal. It's still read as
  temporal. A single value with only a `from`, such as a death the reader
  learns about later, gets no warning.

Spec: F-TMP-8, types.md F-FT-13. Covered by:
`SpecWarningCheckTests.EntriesOnAFieldThatIsntTemporalAreWarned`.

### SC-CHECK-17. A typo in an entry

A value is written `{ value: deckhand, form: "a1:3" }`.

- It's a warning that `form` isn't an entry key, so the writer sees why
  the value holds from the start of the story.

Spec: F-TMP-2. Covered by:
`SpecWarningCheckTests.AnEntryKeyThatIsntOneIsWarned`.

### SC-CHECK-18. An event points at an outline row that isn't there

An event's `row: 4` names a row the writer deleted from the outline. Another
says `row: 02`.

- Each is a warning: row 4 isn't in the series' outlines, and `02` isn't
  a row id. A row id in the outlines of two series is ambiguous for a
  universe-level note. A `row` entry with a `from` is a warning too.

Spec: F-FLD-7, F-OUT-5. Covered by:
`SpecWarningCheckTests.ARowFieldThatDoesntMatchTheOutlineIsWarned`,
`SpecWarningCheckTests.ARowIdFoundInTwoSeriesIsAmbiguousForAUniverseEntityButNotForABookEntity`.

### SC-CHECK-19. A Book 2 character who first appears in Book 3

A character filed under `a2` has `from: "a3:2"`.

- It's a warning that the `from` is outside the book the character is
  filed in. Views still use it.

Spec: F-ENT-6. Covered by:
`SpecWarningCheckTests.ABookEntityWhoseFromIsInAnotherBookIsWarned`.

### SC-CHECK-20. A subsection placed outside its section

A `### Recovery` subsection has `from=a1:5`, but its `## Shoulder injury`
starts at `a1:10`.

- It's a warning that the subsection starts before its parent, so it
  starts with its parent instead. A subsection that runs past its parent,
  or starts after its parent hands over, is a warning too.

Spec: F-SEC-9, F-SEC-8. Covered by:
`SpecWarningCheckTests.ASubsectionOutsideItsParentIsWarned`.

### SC-CHECK-21. A note saved straight into a book folder

The writer saved `scratch.md` next to `book.md`.

- It's a warning that the file is ignored, with a hint to move it into
  `entities/`. A book's `outline.md` is fine, but an `outline.md` in a
  series folder is a warning, since outlines are read only in books.

Spec: F-LAY-7, F-OUT-1. Covered by:
`SpecWarningCheckTests.AMarkdownFileDirectlyInAContainerFolderIsWarned`.

### SC-CHECK-22. A character filed with the locations

A character's note is in `entities/locations/`.

- It's a warning that names the folder it belongs in,
  `entities/characters/`. A folder that isn't any type's, such as
  `entities/crew/`, gets no warning.

Spec: F-LAY-6. Covered by:
`SpecWarningCheckTests.AnEntityInAnotherTypesFolderIsWarned`,
`SpecWarningCheckTests.AnEntityDirectlyInEntitiesIsWarned`.

### SC-CHECK-23. An old copy of the den inside the den

The writer keeps `archive/old-den/` in the den, with its own `den.yaml`.

- That `den.yaml` is a warning that dens don't nest, and it's ignored.

Spec: F-LAY-1, F-LAY-8. Covered by:
`SpecWarningCheckTests.ADenYamlBelowTheRootIsWarned`.

### SC-CHECK-24. A notes folder at the den root

The writer made `notes/` at the den root for a style guide.

- It's a warning that `notes/` is reserved and ignored for now, with a
  hint to keep notes in a universe's `entities/notes/`.

Spec: F-LAY-3. Covered by:
`SpecWarningCheckTests.NotesAndAuthorsAtTheDenRootAreReserved`.

### SC-CHECK-25. I split an outline row and typed the same id twice

Splitting row 38, the writer typed `38` twice and put row 36 after them.

- Each repeated id is a warning on every row that has it, across the
  series' outlines, and row 36 is a warning that it's out of order.

Spec: F-OUT-4, F-OUT-5. Covered by:
`SpecWarningCheckTests.OutlineRowsOutOfOrderOrRepeatedAreWarned`.

### SC-CHECK-26. Book 2's outline starts again at row 1

Book 1's outline ends at row 2, and the writer started Book 2's at row 1.

- It's a warning on Book 2's outline: row ids keep counting across a
  series, so its first row should come after Book 1's last.

Spec: F-OUT-6. Covered by:
`SpecWarningCheckTests.RowsThatRestartInTheNextBookAreWarned`.

### SC-CHECK-27. Outline rows and chapters disagree

Book 1 was cut to 24 chapters, but a row still says chapter 30, and a
beat moved up the table still says a later chapter than the rows below
it.

- The row for chapter 30 is a warning that the book has no such chapter.
  The row mapped to an earlier chapter than a row above it is a warning
  that names that row.

Spec: F-OUT-9, F-OUT-10. Covered by:
`SpecWarningCheckTests.OutlineRowsWithMissingOrOutOfOrderChaptersAreWarned`.

### SC-CHECK-28. An outline table that doesn't read as I meant

An outline row has `[[orrin-fell|Orrin]]` with an unescaped `|`, a row id
`069`, a status `maybe`, a header typed twice, and a second table that
has a `row` column but no `beat`.

- Each is a warning on its line: the extra cell and the unclosed `[[`,
  the bad row id (the row is ignored), the unknown status (read as
  `draft`), the ignored column, and "did you mean an outline table?".
  None is an error.

Spec: F-OUT-2, F-OUT-3, F-OUT-9, F-OUT-11, F-OUT-12. Covered by:
`SpecWarningCheckTests.AnOutlineTableThatDoesntReadAsMeantIsWarned`.

### SC-CHECK-29. A section comment pasted into an outline

The writer pasted `<!-- wolfden: from=a1:14.2 status=maybe -->` under a
heading in `outline.md`.

- It's a warning that wolfden comments do nothing in an outline. Its bad
  values aren't errors, and the outline's rows and links are still read.

Spec: F-SEC-10, F-GEN-2 (#80). Covered by:
`SpecWarningCheckTests.AWolfdenCommentInAnOutlineIsAWarningAndTheOutlineStillLoads`,
`SpecWarningCheckTests.AnOutlineParsedAsAnOutlineHasNoSectionCommentsOrCommentErrors`.

### SC-CHECK-30. `den.yaml` lists an author without a name

`den.yaml` says `authors: { wren-calder: Wren Calder }` instead of giving
the author a `name`.

- It's an error in `den.yaml`, so the den doesn't load and check exits
  2. So is an author key that isn't a key, such as `Wren Calder`.

Spec: F-DEN-3, F-KEY-1, F-GEN-2. Covered by:
`SpecWarningCheckTests.AnAuthorsThatIsntAMappingOfNamedIdentitiesKeepsTheDenFromLoading`.

### SC-CHECK-31. A series names an author `den.yaml` doesn't have

A series says `authors: [wren-calder, ash-morrow]`, and `den.yaml` lists
only `wren-calder`.

- `ash-morrow` is a warning that lists the den's authors.

Spec: F-CTR-2. Covered by:
`SpecWarningCheckTests.AContainerAuthorThatDenYamlDoesntListIsWarned`.

### SC-CHECK-32. A book file written by hand has no id

The writer made `book.md` and `universe.md` by hand, without ids.

- Each is the same no-id warning an entity gets, suggesting
  `wolfden check --fix`. In a final book, the warning says `--fix` won't
  add one until the book is unfrozen.

Spec: F-ID-2, F-ID-6, F-BOOK-1 (#100). Covered by:
`SpecWarningCheckTests.AContainerFileWithoutAnIdIsWarnedLikeAnEntity`,
`CheckCommandTests.FixAddsMissingIdsAndReportsEachFile`.

## RT: saving a note keeps what the writer wrote

Background: the files are the writer's, and Wolfden is only one of the
tools that edits them. When Wolfden or an AI assistant saves a note, it
changes only what the writer asked for and keeps everything else byte for
byte: comments, key order, quoting, blank lines, sections it doesn't
know, line endings, a byte order mark, and the final newline (format.md
§12). The sample dens under `samples/` are the test fixtures, so a new
sample is checked too (#18). That's why the RT scenarios use the
public-domain Dracula sample instead of the invented example world.

### SC-RT-1. I open a note and save it without changing anything

The writer opens a note and saves it, or a tool reads it and writes it
back, with no edits. Some of the writer's notes were saved on Windows,
with CRLF line endings, a byte order mark, or no newline at the end.

- The file's bytes are exactly the same as before, so version history
  shows no change.

Spec: F-RT-3, F-RT-1, F-ENC-2, F-ENC-3, F-ENC-4. Covered by:
`RoundTripTests.RepoFileRoundTrips`,
`RoundTripTests.SampleFileRoundTripsWithOtherEncodings`,
`RoundTripTests.FixtureRoundTrips`.

### SC-RT-2. I change one field and the rest of the frontmatter stays as I wrote it

The writer changes the Count's `role`, his quoted `from`, or a book's
`status` that sits just above a block of YAML comments.

- Only that value changes. The other fields, their order, the YAML
  comments, the quotes (`"dracula:1"` becomes `"dracula:2"`, still
  quoted), and the flow lists and mappings are untouched.

Spec: F-RT-1, F-RT-2. Covered by:
`SampleEditTests.ChangingOneFieldKeepsCommentsFlowValuesAndTheOtherFields`,
`SampleEditTests.ChangingAQuotedValueKeepsItsQuotes`,
`SampleEditTests.ChangingATopLevelValueKeepsTheYamlCommentsAroundIt`,
`SampleEditTests.ChangingANestedValueChangesOnlyThatValue`,
`SampleEditTests.ChangingDenYamlValuesKeepsTheRest`.

### SC-RT-3. I add a field the note didn't have

The writer gives the Count a `height`. His last field has a YAML comment
above it.

- The new field goes on its own line after the last field, so the comment
  stays with the field it describes. Nothing else moves.

Spec: F-RT-4, F-RT-2. Covered by:
`SampleEditTests.ANewFieldGoesAfterTheLastFieldAndItsComment`,
`SampleEditTests.ANewTopLevelKeyGoesAfterTheLongChaptersList`,
`SampleEditTests.ANewNestedDenYamlKeyGoesAfterItsSiblings`.

### SC-RT-4. I rewrite one section

The Count has two `## Appearance` sections, the second one for London
from chapter 13. The writer rewrites the second one.

- Only the second section's text changes. Its heading, its
  `<!-- wolfden: from=dracula:13 -->` comment, the first Appearance, and
  every other section stay as they were. Rewriting a subsection leaves its
  parent's text alone.

Spec: F-RT-2, F-SEC-4, F-SEC-6. Covered by:
`SampleEditTests.EditingOneOfTwoSameNamedSectionsLeavesTheOtherAlone`,
`SampleEditTests.EditingASubsectionKeepsItsCommentAndItsParent`,
`SampleEditTests.EditingASectionWithAListKeepsTheSectionsAroundIt`.

### SC-RT-5. I add a section at the end of a note

The writer adds `## Open questions` to Lucy's note.

- The new section goes after everything else, with a blank line before
  it. Everything above it is unchanged.

Spec: F-RT-2. Covered by:
`SampleEditTests.AddingASectionAtTheEndKeepsEverythingBeforeIt`.

### SC-RT-6. I edit a note that was saved on Windows

The note has CRLF line endings and a byte order mark, and maybe no
newline at the end. The writer changes a field, adds one, rewrites a
section, and adds a section at the end.

- The new and changed lines end in CRLF like the rest, the byte order
  mark stays, and the file still ends the way it did.

Spec: F-RT-1, F-ENC-2, F-ENC-3, F-ENC-4. Covered by:
`SampleEditTests.EditsOnAWindowsSavedSampleKeepCrlfBomAndFinalNewline`.

### SC-RT-7. I add an alias, or a new value for a field that changes over time

The writer adds `Miss Lucy` to Lucy's `aliases`, or adds another entry to
a temporal field's list.

- The new item goes at the end of the list, in the list's own style
  (flow or block), and the other items, their quoting, and any comments
  are unchanged.

Spec: F-RT-2. Covered by: not yet. Wolfden can't add or remove a single
list item yet. A path that treats a list as a mapping is refused and
leaves the file unchanged
(`SampleEditTests.TreatingAListAsAMappingIsRefusedAndChangesNothing`).

## CLI: working with a den from the command line

Background: besides `check`, the CLI has `init`, `index`, `query`, and
`new-id` (design.md §7). Every command finds the den from the current
folder unless `--den` names one, prints one JSON document with `--json`,
and exits 2 when it couldn't do its job.

### SC-CLI-1. I start a new den

The writer runs `wolfden init` in an empty folder, with
`--universe the-long-dark`.

- The folder gets a `den.yaml` and `universes/the-long-dark/universe.md`
  with an id and the name "The Long Dark". `wolfden check` then reports
  no errors or warnings, and `wolfden query` shows an empty view. Running
  `init` again, inside another den, or in a folder that already holds a
  den a few levels down (say `init` in `writing/` when
  `writing/the-long-dark/` is a den), changes nothing, says which den is
  in the way, and exits 2.

Spec: F-DEN-1, F-ID-6, F-LAY-1. Covered by:
`InitCommandTests.ANewDenWithAUniversePassesCheckAndCanBeQueried`,
`InitCommandTests.ANewDenWithoutAUniverseIsJustDenYaml`,
`InitCommandTests.ADenInsideADenIsRefused`,
`InitCommandTests.ADenAroundADenIsRefused`.

### SC-CLI-2. I delete the index, or it gets corrupted

The writer deletes `.wolfden/` (or a sync conflict garbles it) and runs
`wolfden index`.

- The index is rebuilt from the files with the same content, the command
  says how many universes, entities, links, and files it holds, and no
  file outside `.wolfden/` changes.

Spec: F-LAY-2, F-ID-6, design.md §7. Covered by:
`IndexCommandTests.ItRebuildsTheIndexAndSaysWhatItHolds`,
`IndexCommandTests.ADeletedIndexIsRebuiltWithTheSameContent`.

### SC-CLI-3. What does the reader know at Book 2, chapter 14?

Before drafting `a2:15`, the writer runs `wolfden query --at a2:14` to
see canon so far, or adds `--entity "Mara Voss"` to see one character.

- Each field shows the value in effect at `a2:14`, marked with its
  `from`. A character the reader meets later isn't listed, and nor are
  sections that start later. `--pinned` shows only the pinned facts, and
  `--brief` one line per entity.

Spec: F-VIEW-1, F-VIEW-2, F-VIEW-3, F-TMP-5. Covered by:
`QueryCommandTests.AFieldShowsTheValueInEffectAtThePosition`,
`QueryCommandTests.ACharacterIsLeftOutBeforeTheReaderMeetsThem`,
`QueryCommandTests.PinnedShowsOnlyPinnedFacts`.

### SC-CLI-4. Notes I wrote by hand have no ids

The writer made a few notes in a text editor without ids, and `wolfden
check` warns about each one. They run `wolfden check --fix`.

- Each container and entity file without an id gets one as its first
  frontmatter key, after any opening comment lines, and nothing else in
  it changes, line endings included. Each change is listed, then the
  check runs on the fixed files. Files in a final book and files with
  errors are left alone, and the final book's files are listed as not
  fixed.

Spec: F-ID-2, F-ID-6, F-BOOK-1, F-RT-2. Covered by:
`CheckCommandTests.FixAddsMissingIdsAndReportsEachFile`,
`IdTests.TheIdGoesFirstAfterOpeningCommentLines`.

### SC-CLI-5. I run a command from deep inside the den

The writer is in `universes/vael/series/a/books/a1/entities/characters/`
and runs `wolfden check` or `wolfden query`.

- The command uses the den whose `den.yaml` is in the nearest folder
  above. Outside any den, `query` and `index` say there's no den and exit
  2.

Spec: F-LAY-1. Covered by:
`CheckCommandCurrentFolderTests.FromAFolderInsideADenItFindsTheDenAbove`,
`QueryCommandCurrentFolderTests.FromAFolderInsideADenItFindsTheDenAbove`.

### SC-CLI-6. I edit a note, then query again

The writer runs a query, edits a character's field in a text editor, adds
a new location file, and runs the query again.

- The second query shows the edit and the new location. The index is
  rebuilt only when the files have changed since it was built.

Spec: design.md §7. Covered by:
`QueryCommandTests.AQueryAfterAnEditSeesTheEdit`,
`QueryCommandTests.AQueryAfterANewNoteSeesTheNote`,
`FreshnessTests.EnsureUpToDateRebuildsAStaleIndexAndThenItIsCurrent`.

### SC-CLI-7. I need an id for a note I'm writing by hand

The writer runs `wolfden new-id` and pastes the result into a new file's
frontmatter.

- It prints one valid id, a ULID in canonical form, or as many as asked
  for, all different.

Spec: F-ID-1, F-ID-5. Covered by:
`NewIdCommandTests.ItPrintsOneValidId`.

## WATCH: keeping the index up to date while notes change

Background: `wolfden index --watch` (design.md §7, #14) keeps the index up
to date while the writer, or an AI agent, edits notes in other tools. It
reads only the notes that changed where that's safe, and rebuilds the
index where it isn't. Either way the index ends up as a full rebuild of the
same files would make it.

### SC-WATCH-1. I edit notes in my editor while Wolfden watches

The writer runs `wolfden index --watch` in a terminal, then edits Mara
Voss's note in a text editor and saves it a few times, adds a location,
and moves the `characters/` folder of series `a` to `crew/`.

- Within moments each burst of saves shows up in the index as one
  change, and the terminal prints one line for it: the note that changed,
  how long it took, and what the index holds. Only the changed notes are
  read again, so a single save is quick even in a den of thousands of
  notes. Ctrl+C stops watching and exits 0.

Spec: design.md §7. Covered by:
`IncrementalUpdateTests.AnEditedNoteIsUpdatedWithoutARebuild`,
`IncrementalUpdateTests.AMovedFolderOfNotesIsFollowed`,
`IncrementalUpdatePropertyTests.AnyChangeSequenceGivesTheSameIndexAsAFullRebuild`,
`ChangeBatcherTests.ABurstOfSavesIsOneBatchOnceEventsStop`,
`DenWatcherTests.AnEditIsInTheIndexWithinMoments`,
`DenWatcherTests.ANewNoteAndADeletedNoteAreFollowed`,
`IndexWatchTests.ItRebuildsThenPrintsALineForEachChangeUntilStopped`,
`IndexWatchTests.JsonPrintsOneObjectPerLine`,
`PerformanceTests.OneEditOnALargeDenIsUpdatedQuickly`.

### SC-WATCH-2. I rename a character while Wolfden watches

With the watcher running, the writer renames Halden Pike to Halden Marsh.
Other notes still say `[[Halden Pike]]`. Then they add `Halden Pike` to
his aliases.

- After the rename, the links that used the old name are broken in the
  index, as `wolfden check` would report them, though only his note
  changed. After the alias, they resolve again. The index matches a full
  rebuild at every step.

Spec: F-LINK-6, F-LINK-8. Covered by:
`IncrementalUpdateTests.RenamingACharacterBreaksTheOldLinksAndAnAliasMendsThem`,
`IncrementalUpdatePropertyTests.AnyChangeSequenceGivesTheSameIndexAsAFullRebuild`.

### SC-WATCH-3. I change the den's settings or a book's chapters while Wolfden watches

The writer turns on `spoiler_checks` in `den.yaml`, or edits `book.md` of
`a2` to add a chapter.

- The watcher rebuilds the whole index, since such a change can change
  what every note means, and its line says why, such as "den.yaml
  changed". If a typo leaves `den.yaml` unable to load, the line says so,
  and the watcher keeps going until it's fixed.

Spec: F-DEN-1, F-DEN-7, F-BOOK-3, design.md §7. Covered by:
`IncrementalUpdateTests.AStructureChangeRebuilds`,
`IncrementalUpdateTests.AContainerFileChangeRebuilds`,
`IncrementalUpdateTests.AFolderThatCanBeAContainerRebuilds`,
`IndexWatchTests.ItKeepsWatchingWhileDenYamlIsBrokenSoItCanBeFixed`.

### SC-WATCH-4. My tools write files that aren't notes

While the watcher runs, the editor writes swap files, git updates
`.git/`, Obsidian saves its workspace, and the writer drops a map image
into an `entities/` folder, keeps loose notes in `notes/`, edits the
den's `README.md`, and drafts chapters in a book's `drafts/` folder.

- None of it changes what the index holds, and the watcher prints
  nothing for it. `wolfden query` still finds the index up to date, and
  doesn't rebuild it.

Spec: F-LAY-2. Covered by:
`IncrementalUpdateTests.FilesTheIndexDoesntReadChangeNothing`,
`FingerprintTests.FilesTheIndexDoesntReadDontChangeARebuild`,
`ChangeBatcherTests.HiddenPathsAreIgnored`.

### SC-WATCH-5. Something else rebuilds the index while Wolfden watches

With the watcher running, the writer runs `wolfden index` in another
terminal, deletes `.wolfden/`, or starts a second `wolfden index --watch`
on the same den.

- The watcher notices that the index isn't the one it wrote and rebuilds
  it at the next change, rather than patching rows it no longer knows. A
  second watcher on the same den says another one is already watching and
  exits 2. If the operating system stops reporting changes, say because
  the den is on a drive that went away, the watcher says so, listens
  again, and rebuilds, and keeps saying so while it can't.

Spec: design.md §7. Covered by:
`DenWatcherTests.WhenFileEventsStopItListensAgainRebuildsAndSaysSo`,
`IncrementalUpdateTests.AnIndexRebuiltByAnotherCommandIsRebuiltNotPatched`,
`IncrementalUpdateTests.ADeletedIndexIsRebuilt`,
`DenWatcherTests.OnlyOneWatcherRunsPerDen`,
`IndexWatchTests.ASecondWatcherOnTheSameDenExits2`.
