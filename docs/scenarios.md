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
| MCP | An AI assistant reading the den through `wolfden mcp` |
| STORE | Saving, moving, and deleting notes through Wolfden's den store |
| HIST | Version history, checkpoints, and tags, with or without a history backend |

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
  specified behavior. It's why a move made by the app or MCP writes the
  inherited `from` explicitly (F-RT-5, SC-MCP-28).
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
  either are broken, and check still exits 0. That holds even for a link
  by the shared id, or by a name both copies still have: the broken-link
  warning names the copies and says to give each copy a new id, rather
  than suggesting a link by slug or id that couldn't work.

Spec: F-ID-3, F-LINK-8. Covered by:
`ContinuityCheckTests.ACopiedFileWithTheSameIdIsCaughtOnBothCopies`,
`LinkResolutionTests.TiedCopiesWithTheSameIdBreakTheLinkAndSayToGiveThemNewIds`.

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

- It's a warning in `den.yaml`. That author is skipped, and the den
  still loads. So is an author key that isn't a key, such as
  `Wren Calder`.
- A series that names the skipped author gets the F-CTR-2 warning
  (SC-CHECK-31), which says the author's identity in `den.yaml` isn't
  valid.

Spec: F-DEN-3, F-KEY-1, F-CTR-2. Covered by:
`SpecWarningCheckTests.AnAuthorsThatIsntAMappingOfNamedIdentitiesIsWarnedAndTheDenStillLoads`,
`SpecWarningCheckTests.ASkippedAuthorIsUnknownToTheContainersThatNameIt`.

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

### SC-CHECK-33. I linked a shared folder of notes into my den

To share locations between two dens, the writer made
`universes/vael/entities/locations/shared` a symbolic link (or a
junction on Windows) to a folder outside the den.

- The notes in the linked folder aren't read, since Wolfden doesn't
  follow folder links, and `check` warns about the link: it isn't
  followed, so move the folder into the den or copy it there. A link
  somewhere Wolfden doesn't read, such as a `drafts/` folder at the den
  root, gets no warning.

Spec: F-LAY-2 (#102). Covered by:
`FolderLinkTests.ANoteReachedOnlyThroughALinkIsNotReadAndCheckSaysSo`,
`FolderLinkTests.LinksThatLoopAreSkippedByTheLoaderAndTheIndex`,
`FolderLinkTests.ALinkedFolderWhoseTargetWasDeletedIsWarnedAboutAndSkipped`.

### SC-CHECK-34. A canon section in a character who is still an idea

Wren is `status: idea`, but her `## Backstory` says `status=canon`, and
so does an entry in her `role`.

- Each is a warning that says it's read as `idea`, since a fact can't be
  more canon than what holds it. A subsection that only takes the status
  from its parent gets no warning of its own.

Spec: F-STAT-3, F-STAT-2. Covered by:
`SpecWarningCheckTests.ACanonFactInsideAnIdeaIsWarned`.

### SC-CHECK-35. I pinned a question that's still undecided

Orrin's `## Shoulder injury` says `status=undecided pin=true`, and a
`candidate` entry in `died` has `pin: true`.

- Each is a warning: pinned canon leaves them out until they're in
  canon. A pinned `draft` is fine, and a candidate subsection pinned only
  through its parent gets no warning of its own.

Spec: F-PIN-3, F-PIN-2. Covered by:
`SpecWarningCheckTests.APinnedFactThatsOutOfCanonIsWarned`,
`SpecWarningCheckTests.APinnedSectionInAnIdeaIsWarnedOnceForItsStatus`.

### SC-CHECK-36. I pinned the change log

The writer put `pin=true` on a character's `## Change log`, and on the
`## Changelog 2026` of `universe.md`.

- Each is a warning: bookkeeping is never pinned canon, so the pin does
  nothing. A section that matches a bookkeeping pattern but has
  `canon=true` can be pinned.

Spec: F-SEC-12. Covered by:
`SpecWarningCheckTests.PinningABookkeepingSectionIsWarned`.

### SC-CHECK-37. A story note filed inside my session notes

Under `## Session notes`, the writer added `### The reef` with
`canon=true` to keep it in the story.

- It's a warning: a subsection of a bookkeeping section is bookkeeping,
  so `canon=true` does nothing there, and the message says to move it
  out.

Spec: F-SEC-7, F-SEC-12. Covered by:
`SpecWarningCheckTests.CanonTrueUnderABookkeepingSectionIsWarned`.

### SC-CHECK-38. A thread paid off before it's planted

A thread says `planted: ["a1:5", "a1:3"]` and `paid_off: "a1:2"`.

- It's a warning on `paid_off` that names the earliest planted position.
  A `candidate` entry for the payoff, a temporal list, or a position that
  doesn't resolve isn't compared. If either side names only a book
  (`paid_off: "a1"`), only the books are compared, so a payoff later in
  the same book isn't warned.

Spec: types.md F-CORE-2. Covered by:
`SpecWarningCheckTests.AThreadPaidOffBeforeItsPlantedIsWarned`,
`SpecWarningCheckTests.APaidOffIsComparedOnlyWithPlantedPositionsThatAreSet`.

### SC-CHECK-39. An event that ends before it starts

The storm says `when: "2291-05-14"` and ends "the spring before", with
`sort: [2291, 4]`.

- It's a warning on `ends`. Dates are compared only over the numbers
  both have, so `ends: "2291-05"` with `when: "2291-05-14"` is fine, and
  a date without a sort key is skipped.

Spec: types.md F-CORE-3. Covered by:
`SpecWarningCheckTests.AnEventThatEndsBeforeItStartsIsWarned`,
`SpecWarningCheckTests.AnEventsDatesAreComparedOnlyOverTheNumbersBothHave`.

### SC-CHECK-40. A universe of thousands of standalone books

The writer keeps a long-running anthology as one universe, with 2,000
standalone books and no series, so every book's folder is in the same
`books/` folder.

- `check` and `index` take about as long per book as in a den of the
  same size whose books are spread over series: seconds, not minutes.
  Twice as many books take about twice as long.

Spec: design.md §7 (#119). Covered by:
`DenStructureTests.APathLookupListsEachFolderOnTheWayAboutOnce` (Core),
`PerformanceTests.AUniverseOfManyStandaloneBooksIsCheckedAndRebuiltQuickly`.

### SC-CHECK-41. An empty value typed right up against the bracket

The writer types `died: {value: drowned, from:}`, meaning a death with no
`from` yet, or ends an alias list with `[Voss, Orrin:]`. YAML readers
don't agree on a colon that runs straight into `}` or `]`: most take
`from` as a key with an empty value, but Wolfden's reader would keep the
colon and read the key `from:`.

- It's one F-YAML-1 error on that line that quotes `from:` and says to
  add a space after the colon, or to quote the text if the colon is part
  of it. The note is left out until it's fixed, and Wolfden won't edit
  the file. With a space (`from: }`), or before a comma (`{from:, value:
  drowned}`), every reader agrees and there's no diagnostic.

Spec: F-YAML-1. Covered by:
`QuotingCorpusTests.APlainScalarEndingInAColonBeforeAFlowIndicatorIsAnError`,
`QuotingCorpusTests.ColonsThatEveryReaderAgreesOnAreFine`,
`ContinuityCheckTests.AnEmptyFlowValueWithNoSpaceIsOneErrorAndTheNoteIsLeftOut`.

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
`SampleEditTests.EditsOnAWindowsSavedSampleKeepCrlfBomAndFinalNewline`,
`SampleEditTests.ListKeyAndSectionEditsOnAWindowsSavedSampleKeepCrlfBomAndFinalNewline`,
`CollectionEditTests.CollectionEditsKeepCrlfBomAndMissingFinalNewline`,
`SectionEditTests.SectionEditsKeepCrlf`.

### SC-RT-7. I add an alias, or a new value for a field that changes over time

The writer adds `Miss Lucy` to Lucy's `aliases`, or adds another entry to
a temporal field's list, or another point-of-view character to a
chapter's `pov` list.

- The new item goes at the end of the list, in the list's own style: on
  the same line in a flow list (`[Lucy, Miss Lucy]`), or on a line of its
  own at the same indent in a block list. A new temporal entry is written
  like the entry before it (`- { value: ..., from: "dracula:27" }`). The
  other items, their quoting, and any comments are unchanged.

Spec: F-RT-2, F-TMP-7. Covered by:
`SampleEditTests.AnAliasGoesAtTheEndOfTheFlowList`,
`SampleEditTests.ANewTemporalEntryGoesAfterTheLastOneInItsStyle`,
`SampleEditTests.ANestedBlockListGetsANewItemAtItsIndentAndQuoting`,
`SampleEditTests.ANewChapterIsABlockMappingLikeTheOthers`,
`CollectionEditTests.AnItemGoesAtTheEndOfAFlowList`,
`CollectionEditTests.AnItemInABlockListGetsItsOwnLineAfterTheItemBeforeIt`,
`CollectionEditTests.ANewTemporalEntryCopiesItsNeighborsStyle`.

### SC-RT-8. I remove an alias, or one value of a field that changes over time

The writer drops `the Count` from the Count's aliases, or deletes one
entry of Lucy's `role`.

- Only that item goes: in a flow list, the item and the comma next to
  it; in a block list, the item's line, with a comment on that line. The
  other items, and comment lines above the removed one, are unchanged.

Spec: F-RT-2. Covered by:
`SampleEditTests.RemovingAnAliasKeepsTheOthers`,
`SampleEditTests.RemovingATemporalEntryRemovesOnlyItsLine`,
`CollectionEditTests.RemovingAFlowItemTakesOneSeparatorWithIt`,
`CollectionEditTests.RemovingABlockItemRemovesItsLinesAndKeepsTheCommentsAboveIt`.

### SC-RT-9. I change when a fact becomes known, or pin it

Lucy's death is written `died: { value: ..., from: "dracula:12" }`. The
writer moves its `from` to chapter 13, or unpins the Count's `species`.

- Only that value changes, inside the braces. The date, its quotes, the
  other keys, and the comment above the field stay as they were. A new
  key, such as `status`, goes at the end of the braces.

Spec: F-RT-2, F-TMP-8. Covered by:
`SampleEditTests.ChangingWhenAFactIsKnownChangesOnlyItsFrom`,
`SampleEditTests.UnpinningAFactKeepsTheCommentAboveIt`,
`CollectionEditTests.ChangingOneKeyOfAFlowMappingChangesOnlyItsValue`,
`CollectionEditTests.ANewKeyGoesAtTheEndOfAFlowMapping`,
`CollectionEditTests.ChangingOrRemovingAKeyInAMultiLineFlowMappingKeepsItsComments`.

### SC-RT-10. I remove a field

The writer removes the Count's `skills` and `born` fields, or
`spoiler_checks` from `den.yaml`.

- The field's lines go, with any comments inside its value and on its
  last line. Comment lines above it, and every other field, stay.

Spec: F-RT-2. Covered by:
`SampleEditTests.RemovingAFieldRemovesOnlyItsLine`,
`SampleEditTests.RemovingADenYamlKeyKeepsTheRest`,
`CollectionEditTests.RemovingAKeyRemovesItsLinesAndKeepsTheCommentAboveIt`,
`CollectionEditTests.RemovingAKeyWithABlockValueRemovesTheCommentsInsideIt`,
`CollectionEditTests.RemovingAKeyOfAFlowMappingKeepsTheRest`.

### SC-RT-11. I rename a section

Lucy has three `## Appearance` sections. The writer renames the second
one to `Appearance in death`.

- Only that heading's text changes. Its `<!-- wolfden: from=dracula:15 -->`
  comment, its text, and the other two Appearance sections are untouched.
  Closing `#`s, a setext underline, or a trailing HTML comment on the
  heading line stay.
- When the app or an AI assistant makes the rename, links to the old
  heading in other notes (`[[lucy-westenra#Appearance]]`) are rewritten
  in the same change (F-RT-5). That part comes with the write tools (#21).

Spec: F-RT-2, F-RT-5, F-SEC-3, F-SEC-4. Covered by (the one-file edit):
`SampleEditTests.RenamingOneOfThreeSameNamedSectionsLeavesTheOthersAlone`,
`SectionEditTests.RenamingChangesOnlyTheHeadingText`,
`SectionEditTests.RenamingKeepsTheHeadingsMarkup`.

### SC-RT-12. I remove a section

The writer removes Lucy's `### The Un-Dead` subsection, or her last
section, `### Rest`.

- The section goes with its comment, text, and subsections. The spacing
  between the sections around it doesn't change, and the file still ends
  the way it did, with or without a final newline.

Spec: F-RT-2, F-ENC-4. Covered by:
`SampleEditTests.RemovingASubsectionKeepsItsParentAndSiblings`,
`SampleEditTests.RemovingTheLastSectionKeepsTheFileEnding`,
`SectionEditTests.RemovingASectionKeepsTheSpacingAroundIt`,
`SectionEditTests.RemovingASectionRemovesItsSubsections`,
`SectionEditTests.RemovingTheLastSectionRemovesTheBlankLinesBeforeIt`.

### SC-RT-13. I move a section

The writer moves Lucy's `## Relationships` above `## Want`.

- The section moves with its comment, text, and subsections, among its
  siblings. Each section's text is unchanged, the blank lines between
  sections stay where they were, and the file ends the way it did.

Spec: F-RT-2, F-ENC-4. Covered by:
`SampleEditTests.MovingASectionKeepsItsCommentAndTheSpacing`,
`SectionEditTests.MovingASectionKeepsItsTextAndTheSpacingBetweenSections`,
`SectionEditTests.MovingTheLastSectionKeepsTheFilesEnding`,
`SectionEditTests.MovingASectionToTheEndTakesItsSubsections`.

### SC-RT-14. An edit that can't be made cleanly is refused

The writer, or an AI assistant, asks for an edit that Wolfden can't make
without reformatting something else: removing a flow list item whose
comma sits before a comment on the same line, removing an explicit
`? key`, editing a list that puts its commas at the start of lines,
removing or moving a section so that its neighbors (or the lead) would
run together into one heading, removing a section that holds a link
reference definition (`[map]: https://…`) other sections may use, moving
sections that define the same reference label (the first one wins), or
treating a list as a mapping.

- The edit is refused with a message that says why, and the file is left
  exactly as it was. Wolfden never rewrites the rest of the file to make
  an edit fit.

Spec: F-RT-1, F-RT-2. Covered by:
`SampleEditTests.TreatingAListAsAMappingIsRefusedAndChangesNothing`,
`CollectionEditTests.RemovingAFlowItemThatWouldTakeACommentWithItIsRefused`,
`CollectionEditTests.RemovingTheFirstKeyOfAListItemIsRefusedWhenItWouldMoveACommentOrEmptyTheItem`,
`CollectionEditTests.RemovingAnExplicitBlockKeyIsRefused`,
`CollectionEditTests.RemovingAnExplicitFlowKeyIsRefused`,
`CollectionEditTests.RemovingTheFlowKeyBeforeAnExplicitKeyIsRefused`,
`CollectionEditTests.AddingToOrRemovingFromALeadingCommaFlowListIsRefused`,
`SectionEditTests.RemovingASectionThatWouldMergeTheTextAroundItIsRefused`,
`SectionEditTests.MovingASectionThatWouldMergeWithTheTextBeforeItIsRefused`,
`SectionEditTests.RemovingASectionThatWouldMakeAStrayHeadingPartOfTheLeadIsRefused`,
`SectionEditTests.MovingASetextSectionAgainstTheLeadIsRefused`,
`SectionEditTests.RemovingASectionWithALinkReferenceDefinitionIsRefused`,
`SectionEditTests.MovingSectionsThatDefineTheSameLinkReferenceIsRefused`.

### SC-RT-15. I remove the last point-of-view character from a chapter, or from the defaults

`book.md` gives chapter 27 four `pov` characters as a block list, and
chapter 1 one as a flow list. The writer removes them one by one until
none are left. Later, the writer removes the only `pov` from a series'
`chapter_defaults`.

- Each chapter ends with `pov: []`, "none here", block and flow alike, so
  it doesn't fall back to the book's `chapter_defaults`. The series'
  `chapter_defaults` also ends with `pov: []`, so its books stop
  inheriting a narrator from the universe. A comment on the `pov:` line
  stays. Every other field that loses its last item is left as `key:`
  with no value. Inheriting again is a separate action, clearing the
  field.

Spec: F-RT-6, F-CHAP-5. Covered by:
`SampleEditTests.EmptyingAChaptersPovListLeavesNoneHere`,
`CollectionEditTests.RemovingTheLastItemOfABlockChapterListLeavesAnEmptyList`,
`CollectionEditTests.RemovingTheLastItemOfAFlowChapterListLeavesAnEmptyList`,
`CollectionEditTests.AnEmptiedChapterListKeepsCrlf`,
`CollectionEditTests.RemovingTheLastChapterDefaultLeavesAnEmptyList`,
`CollectionEditTests.AnEmptiedChapterDefaultKeepsCrlf`.

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
`FreshnessTests.EnsureUpToDateRebuildsAStaleIndexAndThenItIsCurrent`,
`FingerprintTests.AnEditToAFileTheIndexDoesntReadDoesntMakeItStale`.

### SC-CLI-7. I need an id for a note I'm writing by hand

The writer runs `wolfden new-id` and pastes the result into a new file's
frontmatter.

- It prints one valid id, a ULID in canonical form, or as many as asked
  for, all different.

Spec: F-ID-1, F-ID-5. Covered by:
`NewIdCommandTests.ItPrintsOneValidId`.

### SC-CLI-8. A note Wolfden can't read

The writer keeps a symbolic link to a note that has since moved, so the
link points nowhere. Another time, a sync tool deletes a note just as
`wolfden check` reads the den, or another program holds the note open, or
its permissions don't let the writer read it.

- `check`, `index`, and `query` still work. They read the den as if that
  note weren't there, so links to it are reported as they would be if it
  were deleted. Once the note can be read again, the next query sees that
  the files changed and rebuilds the index with it.

Spec: design.md §7 (#112). Covered by:
`UnreadableFileTests.AFileThatCantBeReadIsCheckedAsIfItWerentThere` (Core),
`UnreadableFileTests.APackFileThatCantBeReadIsCheckedAsIfItWerentThere` (Core),
`FolderLinkTests.ALinkedFolderWhoseTargetWasDeletedIsWarnedAboutAndSkipped`,
`UnreadableFileTests.BrokenAndLoopingFileLinksAreAbsentToCheckAndTheIndex`,
`UnreadableFileTests.ANoteReplacedByABrokenLinkIsCheckedAndIndexedAsIfItWereDeleted`,
`UnreadableFileTests.ANoteThatCantBeReadIsLeftOutAndTheIndexIsStaleOnceItCanBe`,
`UnreadableFileTests.ANoteThatCantBeReadIsLeftOutAndTheCommandsStillWork` (CLI).

### SC-CLI-9. A folder Wolfden can't list

The writer changes the permissions of a book's folder, or of a
`characters/` folder, so their own account may no longer list what's in
it. Another time, a sync tool deletes a series folder just as
`wolfden check` reads the den.

- `check`, `index`, and `query` still work. They read the den as if the
  folder weren't there: a book folder that can't be listed isn't reported
  as a book folder without its `book.md`, and links to the notes in it are
  reported as they would be if it were deleted. Once the folder can be
  listed again, the next query sees that the files changed and rebuilds
  the index with what's in it.

Spec: design.md §7 (#116). Covered by:
`UnlistableFolderTests.AFolderDeletedAfterItsParentWasListedIsCheckedAsIfItWerentThere` (Core),
`UnlistableFolderTests.AFolderThatCantBeListedIsCheckedAndIndexedAsIfItWereDeleted`,
`UnlistableFolderTests.AFolderThatCantBeListedIsntWarnedAboutByName`,
`UnlistableFolderTests.AFolderTheLoadCantListAfterTheTreeWasTakenMakesTheIndexStale`,
`UnlistableFolderTests.AFolderDeletedAfterTheLoadReadItLeavesTheIndexStale`,
`UnlistableFolderTests.AFolderThatCantBeListedIsLeftOutAndTheCommandsStillWork` (CLI).

### SC-CLI-10. I query a den with two universes

The writer's den has two universes, `vael` and `orn`. They run `wolfden
query`, then `wolfden query --at fen:1` (`fen` is a book of `vael`), then
`wolfden query --at common:1`, where both universes have a book
`common`.

- With no universe and no position, the query says the den has two
  universes, lists their keys, and exits 2. `--at fen:1` settles it:
  `fen` is only in `vael`, so it shows `vael`'s canon as of `fen:1`.
  `common:1` doesn't settle it, so the query asks for `--universe` and
  exits 2, as it does for a book in neither universe. `--universe vael`
  (or `vael` as a plain argument) always works. `--universe '*'` is
  refused, since a canon view is of one universe.

Spec: F-POS-5, design.md §7 (maintainer decision, 2026-10-23). Covered by:
`QueryCommandTests.ADenWithSeveralUniversesNeedsOneNamed`,
`QueryCommandTests.APositionSettlesTheUniverseWhenItsBookIsInOnlyOne`.

### SC-CLI-11. I check one universe of my den

The writer's den has two universes, and they're working in `vael`. They
run `wolfden check --universe vael`.

- It reports `vael`'s findings and the den-level ones (`den.yaml`, packs),
  and nothing from `orn`, whose notes it doesn't read. A note copied
  from `vael` into `orn` with its id is still a duplicate (F-ID-3, naming
  the `vael` copy), and links to it break, as in a whole-den check: the
  other universes' ids come from the index. A broken `den.yaml` is still
  reported, with a note that ids weren't compared across universes,
  since there are no notes to compare with; no index is built. Without
  `--universe`, or with `--universe '*'`, it checks the whole den. A
  universe the den doesn't have, or `--fix` with `--universe`, exits 2;
  an unknown universe is reported before the index is brought up to
  date, and leaves `.wolfden/` alone. With `--no-index`, nothing is
  written under `.wolfden/`: ids aren't compared across universes, and
  the same note says so.

Spec: F-DEN-1, F-ID-3, design.md §7 (maintainer decision, 2026-10-23; #130). Covered by:
`CheckUniverseTests.WithAUniverseOnlyItsFindingsAreReported`,
`CheckUniverseTests.AnIdSharedWithAnotherUniverseIsFound`,
`CheckUniverseTests.ABrokenDenYamlIsStillReported`,
`CheckUniverseTests.AnUnknownUniverseOrFixWithAUniverseExitsTwo`,
`CheckUniverseTests.AnUnknownUniverseOrABrokenDenYamlLeavesTheIndexAlone`,
`CheckUniverseTests.NoIndexLeavesTheIndexAloneAndSaysIdsWerentCompared`.

### SC-CLI-12. I want history from the start

The writer runs `wolfden init kestrel-notes --universe the-long-dark
--git`. Another time they run `wolfden init vael-notes --git` inside
`writing/`, which is already a git repository. A third time, git isn't
installed.

- With git, the new folder is also a git repository, with nothing
  committed yet, and the output says so; the den checks clean, and its
  first checkpoint records `den.yaml` and the universe. Inside
  `writing/`, no new repository is made: the output names the
  repository the den is in, and its history is scoped to the den's
  files there. Without git, the den is still made, the output says the
  repository wasn't and how to make one later, and `init` exits 0.
  Without `--git`, the den is a plain folder.

Spec: F-LAY-1, design.md §6, §7 (maintainer decision, #57, #122). Covered by:
`InitGitTests.WithGitTheNewDenIsARepositoryWithHistory`,
`InitGitTests.InsideARepositoryItSaysSoAndMakesNoNewOne`,
`InitGitTests.WithoutGitInstalledTheDenIsStillMadeAndItExplains`,
`InitGitTests.WithoutTheFlagTheDenIsAPlainFolder`.

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
`ChangeBatcherTests.HiddenPathsAreIgnored`,
`ChangeBatcherTests.AStoresAtomicWriteIsBatchedAsTheNoteAlone`.

### SC-WATCH-5. Something else rebuilds the index while Wolfden watches

With the watcher running, the writer runs `wolfden index` in another
terminal, deletes `.wolfden/`, or starts a second `wolfden index --watch`
on the same den.

- The watcher notices that the index isn't the one it wrote and rebuilds
  it at the next change, rather than patching rows it no longer knows,
  and a change it was writing when the index was replaced isn't lost. A
  second watcher on the same den says another one is already watching and
  exits 2. If the operating system stops reporting changes, say because
  the den is on a drive that went away, the watcher says so, listens
  again, and rebuilds, and keeps saying so while it can't.

Spec: design.md §7. Covered by:
`DenWatcherTests.WhenFileEventsStopItListensAgainRebuildsAndSaysSo`,
`IncrementalUpdateTests.AnIndexRebuiltByAnotherCommandIsRebuiltNotPatched`,
`IncrementalUpdateTests.AnIndexReplacedWhileABatchIsWrittenIsRebuilt`,
`IncrementalUpdateTests.ADeletedIndexIsRebuilt`,
`DenWatcherTests.OnlyOneWatcherRunsPerDen`,
`IndexWatchTests.ASecondWatcherOnTheSameDenExits2`.

### SC-WATCH-6. I query right after a save while Wolfden watches

With the watcher running on a large den, the writer saves Mara Voss's
note and runs `wolfden query --entity "Mara Voss"` at once, before the
watcher has applied the save.

- The query shows the saved note. It waits a moment (up to about a
  second) for the watcher to apply the save, rather than rebuilding the
  whole index itself, which on a large den takes several seconds and
  makes the watcher rebuild too. If the watcher hasn't caught up by
  then, the query rebuilds the index, so the answer still matches the
  files.

Spec: design.md §7. Covered by:
`FreshnessTests.EnsureUpToDateWaitsForARunningWatcherInsteadOfRebuilding`,
`FreshnessTests.EnsureUpToDateRebuildsWhenTheWatcherDoesntCatchUpInTime`,
`QueryCommandTests.AQueryWaitsBrieflyForAWatcherThenRebuildsIfItHasnt`.

### SC-WATCH-7. A note can't be read while Wolfden watches

With the watcher running, the writer replaces a note with a link to a
file that has moved away. Later, another program holds Mara Voss's note
open just after the writer saves it.

- The broken link is left out at once, as if the note were deleted. The
  note that's held open is tried again a few times; if it still can't be
  read, the watcher rebuilds the index without it, as `wolfden index`
  would, and its next save brings it back. Either way the index matches a
  full rebuild of the same files. If the link starts working again
  because its target comes back, often outside the den, the link itself
  gets no file event, so the watcher doesn't see it; the next query sees
  that the files changed and rebuilds the index with the note.

Spec: design.md §7 (#112). Covered by:
`UnreadableFileTests.TheWatcherTreatsABrokenLinkAsAbsentAtOnce`,
`IncrementalUpdateTests.AFileThatStaysUnreadableIsLeftOutByARebuildUntilItCanBeRead`,
`IncrementalUpdatePropertyTests.AnyChangeSequenceGivesTheSameIndexAsAFullRebuild`.

### SC-WATCH-8. A folder can't be listed while Wolfden watches

With the watcher running, the writer takes away their own permission to
list Mara Voss's `characters/` folder, and later that of the book folder
`ember/`.

- Whenever the watcher next gets an event for the folder or for anything
  in it, such as a note saved in place, it leaves the whole folder out,
  as if it were deleted: all the notes in `characters/` drop out of the
  index, and the book folder gets a full rebuild without it. Either way
  the index matches a full rebuild of the same files. A change of
  permissions gets no file event of its own, so until such an event the
  watcher doesn't notice; the next query sees that the files changed and
  rebuilds the index. The same happens when the folder can be listed
  again.

Spec: design.md §7 (#116). Covered by:
`UnlistableFolderTests.TheWatcherLeavesOutAnEntitiesFolderItCantList`,
`UnlistableFolderTests.TheWatcherRebuildsWithoutAContainerFolderItCantList`,
`UnlistableFolderTests.TheWatcherLeavesOutAFolderItCantListWhenAFileInItChanges`.

## MCP: an AI assistant reads the den

Background: `wolfden mcp` (design.md §8, #20) runs an MCP server over
stdio, so an AI assistant can read the den with `search`, `get_entity`,
`list_entities`, `canon_as_of`, `get_chapter`, `get_chapter_defaults`,
`check`, and `get_history`, and write to it with the write tools (#21,
SC-MCP-15 on).
Without a story position, a reading tool gives the author's full view;
with one, it gives canon as the reader knows it there. Pinned canon comes
first. The examples use the Dracula sample, whose book is `dracula`.

### SC-MCP-1. My assistant reads canon as of the chapter I'm drafting

The writer is drafting chapter 4 and asks the assistant to check what's
established so far. It calls `canon_as_of` with `position: "dracula:4"`.

- The result holds every entity the reader has met by chapter 4, at any
  scope, with the field values and sections in effect there: the same
  view `wolfden query --at dracula:4` shows. Lucy and Renfield, who enter
  at chapter 5, and what the Count is, which comes out in chapter 18,
  aren't in it anywhere. Ideas, undecided questions, and retconned or
  bookkeeping material are left out. `type` narrows it, and `brief`
  lists the entities without their content.

Spec: F-VIEW-1, F-VIEW-3. Covered by:
`DraculaToolTests.CanonAsOfAnEarlyChapterLeaksNothingTheReaderMeetsLater`,
`DraculaToolTests.CanonAsOfMatchesTheIndexsViewEntityForEntity`,
`DraculaToolTests.CanonAsOfLeavesOutIdeasAndUndecidedQuestions`,
`DraculaToolTests.CanonAsOfCanBeBriefAndNarrowedToAType`.

### SC-MCP-2. My assistant asks about a character the reader hasn't met yet

Drafting chapter 4, the assistant calls `get_entity` for "Lucy" with
`as_of: "dracula:4"`.

- The result says Lucy isn't in canon as of chapter 4 (`in_view` is
  false) and gives nothing about her: not even her full name. At chapter
  5 it gives her as the reader first meets her, without her death or what
  follows.

Spec: F-VIEW-1, F-ENT-6, format.md §9.5. Covered by:
`DraculaToolTests.GetEntityAsOfAChapterBeforeTheReaderMeetsItReturnsNothingAboutIt`,
`DraculaToolTests.GetEntityAsOfAChapterShowsOnlyWhatsKnownThen`,
`McpServerTests.OverTheWireAnEarlyViewHoldsNoSpoilers`.

### SC-MCP-3. My assistant sees the hard facts first

The writer has pinned the Count's species and the rules of the Un-Dead.
The assistant calls `canon_as_of` at chapter 18, and `get_entity` for the
Count.

- Each result starts with `pinned`: the pinned facts in effect, grouped
  by entity, ahead of the rest. They're listed in place as well, so each
  entity is complete. At chapter 17 the Count's species isn't among them,
  since the reader learns it at chapter 18.

Spec: F-PIN-1, F-VIEW-2, design.md §8. Covered by:
`DraculaToolTests.CanonAsOfServesPinnedCanonFirst`,
`DraculaToolTests.PinnedCanonKeepsToThePositionToo`,
`DraculaToolTests.GetEntityServesPinnedFactsFirst`.

### SC-MCP-4. My assistant reads a whole note, ideas and all

The writer asks the assistant to help decide an open question in the
thread "The Count's end". It calls `get_entity` with no position.

- The result is the author's full view: every field entry and section,
  whatever its status, each with its status, pin, and the positions
  where it starts and ends, so the undecided question and both candidate
  answers are there, marked as such. The entity can be named by its
  name, an alias, its file name, its id, or its path.

Spec: F-STAT-1, F-STAT-4, F-LINK-6. Covered by:
`DraculaToolTests.GetEntityWithoutAPositionIsTheAuthorsFullView`,
`DraculaToolTests.GetEntityTakesANameAnAliasAFileNameAnIdOrAPath`.

### SC-MCP-5. My assistant looks for notes

The assistant searches for "Voivode", and lists the den's characters.

- `search` finds the Count's "Who he was" section, with the match marked
  in a snippet, and says for each hit whether it's canon text;
  `canon_only` leaves out ideas, candidates, and retconned material.
  `list_entities` lists entities of every status as one line each, and
  narrows by type, status, series, or book.

Spec: design.md §7, §8. Covered by:
`DraculaToolTests.SearchFindsNotesAndSaysWhatsCanon`,
`DraculaToolTests.ListEntitiesFiltersByTypeStatusAndScope`.

### SC-MCP-6. My assistant gets a position or a name wrong

The assistant asks for canon as of `dracula:99`, then for "Sherlock
Holmes", then for "Lucy Westenra" in a den where two notes have that
name.

- Each answer is an error result with a code the assistant can act on
  (`bad_position`, `unknown_entity`, `ambiguous_entity`, and so on) and a
  message saying what to do: a bad position lists the universe's books in
  story order, and an ambiguous name lists the matches. With a position,
  a name is matched, tier by tier, among what the reader has met by
  then: a namesake met later can't shadow the entity the reader knows,
  tie with it, or show in the error. `wolfden query --entity --at`
  matches the same way. A den with several universes asks which one
  (SC-MCP-11). An argument of the wrong type, or a missing one, is
  `bad_argument` in the same shape.

Spec: F-POS-2, F-POS-3, F-POS-4, F-LINK-6. Covered by:
`ToolErrorTests`, `OtherDenErrorTests.ANameThatMatchesTwoEntitiesListsThemWithoutSpoilingEarlierViews`,
`OtherDenErrorTests.WithAPositionALaterNamesakeCantShadowAnEntityTheReaderHasMet`,
`OtherDenErrorTests.AnUnknownTypeAtAPositionDoesntListTheTypesInUse`,
`OtherDenErrorTests.ADenWithSeveralUniversesNeedsOneNamed`,
`QueryCommandTests.AtAPositionTheEntityIsMatchedAmongThoseTheReaderHasMet`,
`McpServerTests.ABadCallIsAnErrorResultWithACode`,
`McpServerTests.AnArgumentOfTheWrongTypeIsABadArgument`.

### SC-MCP-7. I edit a note while my assistant is connected

With `wolfden mcp` running, the writer adds a section to Mr Swales's note
in their editor, and the assistant reads him again.

- The next call shows the new section. The server runs the same watcher
  as `wolfden index --watch`, and each call also checks the files first,
  as `wolfden query` does, so an answer never lags behind a save. If
  another Wolfden is already watching the den, the server uses that one.

Spec: design.md §7, §8. Covered by:
`McpServerTests.AnEditWhileConnectedShowsInTheNextCall`.

### SC-MCP-8. My assistant runs the continuity checks

The assistant calls `check` after helping with an edit.

- It gets the same findings as `wolfden check`, each with a path, line,
  severity, rule id, and message, and the den's error and warning
  counts. `severity` and `path` narrow the list. It never changes a
  file.

Spec: F-GEN-1, design.md §7. Covered by:
`DraculaToolTests.CheckReportsTheSamplesFindings`.

### SC-MCP-9. `den.yaml` stops loading while my assistant is connected

The writer makes a typo in `den.yaml`.

- The reading tools answer `den_not_loaded` and say to call `check`,
  which reports what's wrong with `den.yaml`. Once it's fixed, the tools
  work again without restarting the server.

Spec: F-DEN-1. Covered by:
`OtherDenErrorTests.ADenWhoseSettingsDontLoadIsDenNotLoadedButCheckSaysWhy`.

### SC-MCP-10. I connect my AI client to my den

The writer adds `wolfden mcp --den <folder>` to their AI client's MCP
settings.

- The client starts the server, sees its tools, the reading ones
  marked read-only, with
  descriptions that explain dens, scopes, and positions, and can call
  each one, several at once even before the den has an index. The
  server doesn't make the client wait for its first index build
  (#135). Nothing
  but the protocol goes to standard output, and when the client closes
  standard input the server exits 0. Without a den, the server doesn't
  start, says why on standard error, and exits 2; one that fails after
  starting exits 3.

Spec: design.md §7, §8, §9. Covered by:
`McpCommandTests.AnAIClientRunsItOverStdio`,
`McpCommandTests.OverRawPipesStdoutHoldsOnlyJsonRpcAndStdinEofEndsTheServer`,
`McpCommandTests.AServerThatFailsAfterStartingIsExitThree`,
`McpCommandTests.WithoutADenItDoesntStart`,
`ParallelCallTests.ParallelFirstCallsOnADenWithNoIndexAllSucceed`,
`McpServerTests.TheClientSeesTheServerAndItsTools`,
`McpServerTests.EachToolAnswersWithJson`,
`McpHandshakeTests.TheServerAnswersWhileTheWatchersFirstBuildIsHeldUp`.

### SC-MCP-11. My assistant works in a den with two universes

The writer's den holds two universes, `starfall` and `drift`, and each
has a Lantern. The assistant calls `canon_as_of`, then `canon_as_of` at
`ember:1`, then `get_entity` for "Lantern" at `ash:2`.

- With no universe and no position, every tool but `check` answers
  `universe_required`, listing the universe keys and nothing else: no
  entity from either universe. A position settles the universe when its
  book is in only one (`ember` and `ash` are Starfall's), so the third
  call gives Starfall's Lantern as the reader knows it at `ash:2`. A book
  key both universes have doesn't settle it (`universe_required`), and
  one neither has is `bad_position`. `search` and `list_entities` take
  `universe: "*"` to read both universes; the other tools refuse it, and
  so does any tool given a position with it.

Spec: F-POS-5, design.md §8 (maintainer decision, 2026-10-23). Covered by:
`UniverseChoiceTests.WithoutAUniverseOrPositionEveryToolButCheckAsksForOne`,
`UniverseChoiceTests.APositionsBookSettlesTheUniverse`,
`UniverseChoiceTests.ABookKeyInTwoUniversesOrInNoneDoesntSettleIt`,
`UniverseChoiceTests.StarReadsEveryUniverseInSearchAndListOnly`.

### SC-MCP-12. I pin my assistant to one universe

The writer starts the server with `wolfden mcp --universe drift`.

- Every call reads `drift` without naming it, and the server's
  instructions say the session is pinned. A call that names `starfall`,
  or `"*"`, is refused with `bad_argument`, so the assistant can't leave
  its universe; `check` checks `drift` and the den-level files. A
  `--universe` the den doesn't have stops the server from starting (exit
  2).

Spec: design.md §7, §8 (maintainer decision, 2026-10-23). Covered by:
`UniverseChoiceTests.APinnedSessionReadsOnlyItsUniverse`,
`McpCommandTests.AUniverseTheDenDoesntHaveCantBePinned`.

### SC-MCP-13. My assistant checks one universe

The assistant calls `check` with `universe: "drift"`.

- It gets `drift`'s findings and the den-level ones, and none from
  `starfall`, whose notes aren't read. Mara Voss copied from `starfall`
  into `drift`, id and all, is still a duplicate (F-ID-3) and links to
  her still break, as in the whole den; only the hint on a broken link
  that names a match in another universe is missing. A broken pack or
  `den.yaml` is still reported, and `note` says when ids couldn't be
  compared across universes. Without a universe, or with `"*"`, it
  checks the whole den, whose findings are those of each universe plus
  the den-level ones, each once. `path` still narrows the list.

Spec: F-DEN-1, F-ID-3, design.md §7, §8 (maintainer decision, 2026-10-23). Covered by:
`UniverseChoiceTests.CheckOfAUniverseLeavesOutTheOthers`,
`UniverseChoiceTests.TheWholeDenIsTheUnionOfItsUniversesAndTheDenLevel`,
`UniverseChoiceTests.ACheckOfOneUniverseFindsAnIdSharedWithAnotherUniverse`,
`UniverseChoiceTests.WhenTheIndexCantBeBuiltTheScopedCheckSaysTheIdComparisonWasSkipped`,
`UniverseChoiceTests.ABrokenPackOrDenYamlIsReportedWhenAUniverseIsGiven`.

### SC-MCP-14. My assistant looks at a note's history

The writer's den is a git repository. They ask the assistant what changed
in Lucy Westenra's note, and what they've changed since the last
checkpoint.

- `get_history` with `entity: "Lucy Westenra"` lists the checkpoints that
  changed her file, newest first, each with its id, time, author,
  message, and files, and none that only changed other notes. With
  `limit: 1`, the result is `truncated` and gives `next_before`; asking
  again with `before` set to it gives the older checkpoint. `diff: true`
  adds what changed in her file since the latest checkpoint, and
  `diff_from` with a checkpoint's id, what changed since that one. In a
  den without history, the result isn't an error: `available` is false
  and `reason` says the den has no history. A `before` or `diff_from`
  that isn't one of the checkpoints is `unknown_checkpoint`.

Spec: design.md §6, §8 (#19). Covered by:
`HistoryToolTests.TheAssistantSeesAnEntitysCheckpointsAPageAtATimeAndWhatChangedSince`,
`HistoryToolTests.WithoutHistoryTheAnswerSaysSoAndIsntAnError`,
`McpCommandTests.OverStdioHistoryComesFromGit`.

The scenarios below are about the write tools (design.md §8, #21). Each
edit of an existing note takes the hash the assistant read it with
(`expected_hash`, from `get_entity` or `get_chapter`), and every write
goes to the files only, until a checkpoint.

### SC-MCP-15. My assistant adds a character

The writer asks the assistant to add Sister Agatha, the nun who writes to
Mina from Buda-Pesth, to the book, from chapter 8.

- `create_entity` makes `entities/characters/sister-agatha.md` in the
  book from the character template: a new id that no note in the den
  uses, `type`, `name`, the alias and fields given, `status: draft`
  written out, `from`, the template's other fields, and its sections
  with their prompts. The note passes `check`. A second Sister Agatha
  gets `sister-agatha-2.md`; a slug asked for that's taken is refused
  (`already_exists`), and so is a type no pack defines (`unknown_type`).
  A name with nothing to make a slug from uses the type key.

Spec: types.md F-TPL-6, F-TPL-7, F-ID-6. Covered by:
`WriteToolTests.ANewEntityComesFromItsTemplateWithAnIdNoFileUses`,
`WriteToolTests.ASlugIsMadeFromTheNameAsTheTemplateRuleSays`.

### SC-MCP-16. My assistant changes a note's fields

The assistant adds an alias to Mr. Swales, gives him an occupation, and
marks him an idea; then it tries to save a status that isn't one, and a
scene position. Later it moves when the reader learns the Count's
pinned species.

- `update_fields` makes each change in place, keeping every other line
  as written, with the new alias in template order, and says that
  Swales is now out of canon views. The old hash is now `stale_file`;
  the result's hash works. An edit that would leave an error
  (`status: maybe`, `from: dracula:7.2`) is refused as `invalid_edit`
  with the rule, and the file is unchanged. An `id` edit is refused
  (F-ID-4); renaming the entity or changing an alias is left to
  `rename_entity` and `change_alias`, so links follow (F-RT-5,
  SC-MCP-26, SC-MCP-27). The change to the pinned species is made,
  and `pinned_canon` says it touched pinned canon, as it does for a
  status change on the Count, whose pinned facts take his status. An
  edit that changes nothing writes nothing, not even a missing id. An
  alias that's another note's name or slug says what links by it now
  reach; an alias with control characters, or a slug Windows can't
  hold (`nul`), is refused, and a name like "Con" gets `con-2`.

Spec: format.md §1.3, F-ID-4, F-RT-2, F-RT-5, F-PIN-1, types.md F-TPL-7
(maintainer decisions, 2026-10-22 and 2026-10-23). Covered by:
`WriteToolTests.FieldEditsKeepTheRestAndSayWhatTheyMean`,
`WriteToolTests.AnEditThatWouldLeaveAnErrorIsRefusedAndNothingIsWritten`,
`WriteToolTests.AnEditOfPinnedCanonIsMadeButFlagged`,
`WriteToolTests.AStatusChangeOnAnEntityWithPinnedFactsIsFlagged`,
`WriteToolTests.APinnedSubsectionUnderAnUnpinnedSectionIsCounted`,
`WriteToolTests.AnEditThatChangesNothingAddsNoId`,
`WriteToolTests.BadNamesAndAliasesAreRefusedAndAnAliasThatCollidesSaysSo`,
`McpServerTests.OverTheWireAWriteTakesItsChangesAsJsonAndAStaleHashIsRefused`,
`WriteEditTests.InTemplateOrderANewTopLevelKeyGoesInItsPlace`,
`WriteEditTests.AWholeTemporalListIsWrittenOneEntryPerLine`.

### SC-MCP-17. My assistant writes into a note's sections

The assistant adds a line to Swales's Voice, an Appearance he didn't
have, and a subsection under his Arc that the reader meets at chapter 7;
then it fills the Overview of a character it just created.

- `append_section` adds the text after what's in the section. The new
  Appearance goes before Voice, in the character type's section order,
  and the subsection goes at the end of Arc with `from=dracula:7` as its
  section comment. A template's empty section is filled, prompt comment
  kept, never repeated. Text added to a section with subsections goes
  before them. A note saved on Windows keeps its CRLF line endings and
  byte order mark. Status or positions for a section that's already
  there are refused, as is a parent that isn't there.

Spec: format.md §9.2, types.md F-TPL-5, F-TPL-8. Covered by:
`WriteToolTests.TextGoesIntoASectionOrANewOneInItsPlace`,
`WriteToolTests.ATemplateSectionIsFilledNotRepeated`,
`WriteToolTests.TextAddedToASectionGoesBeforeItsSubsections`,
`WriteToolTests.ANoteSavedOnWindowsKeepsItsLineEndingsAndByteOrderMark`,
`WriteEditTests.ASectionGoesBeforeAnotherWithItsComment`.

### SC-MCP-18. My assistant banks an idea

The writer muses that the Count might keep a diary, and that Swales might
have seen the dog leap ashore.

- `add_idea` makes an `idea` entity, "The Count's diary", with `status:
  idea`, `about` linking the Count, and the text in its lead. With an
  entity named, it adds a section marked `status=idea` to Swales
  instead. Canon views leave both out.

Spec: format.md §9.3, types.md §9.1. Covered by:
`WriteToolTests.AnIdeaIsRecordedOutOfCanon`,
`WriteEditTests.TextGoesAtTheEndOfTheLead`.

### SC-MCP-19. My assistant renames a section

A note on Whitby dialect links `[[mr-swales#Voice]]` in a field and
`[[Mr. Swales # voice |how he talks]]` in its text. The assistant
renames Swales's Voice to Dialect.

- The heading changes, and both links now name Dialect, with their
  spacing and label kept, in the same change; a link to another of his
  sections is left alone. The result lists both files with their new
  hashes, and `check` finds no heading that doesn't match. A link in a
  block scalar, or in a quoted value with an escape, is spliced in, so
  the value keeps its style. If a note in a final book links to the
  section, nothing is renamed (`frozen`); if a write fails part way,
  what was written is put back. A new name that would move existing
  links (a later section's name, or an earlier one's while links name
  this one) is refused, and a note with errors that links to the
  section is named in the result for the writer to fix by hand.

Spec: format.md F-RT-5, F-LINK-9, F-BOOK-1 (maintainer decision,
2026-10-23). Covered by:
`WriteToolTests.RenamingASectionRewritesTheLinksToIt`,
`WriteToolTests.ALinkInAFrozenBookStopsTheRename`,
`WriteToolTests.ARenameThatFailsPartWayPutsBackWhatItWrote`,
`WriteToolTests.ARenameStoppedByAnyFailureIsPutBackThenRethrown`,
`WriteToolTests.ARollbackLeavesAFileSomeoneElseSavedMeanwhile`,
`WriteToolTests.ALinkInAFrontmatterValueIsSplicedSoTheValueKeepsItsStyle`,
`WriteToolTests.ANameThatWouldMoveExistingLinksIsRefused`,
`WriteToolTests.AFileWithErrorsThatLinksToTheSectionIsNamed`.

### SC-MCP-20. My assistant takes the last POV off a chapter

The book's `chapter_defaults` give every chapter Mina's POV, and chapter
1 has its own, Jonathan's. The assistant removes it.

- `get_chapter` shows chapter 1's `pov` as its own, with Mina's as the
  default from the book. After `update_chapter_fields` removes the item,
  `book.md` says `pov: []`, and the result says chapter 1 now has no
  POV, and that without its own value it would inherit Mina's from the
  book's `chapter_defaults`. Clearing the field goes back to that
  default; clearing a field the chapter doesn't set is refused. An item
  can go in at a given place, and come out by its text, compared
  ignoring case and spacing: `[[Mina]]` doesn't match
  `[[mina-harker]]`. A field named like the chapter's own keys (`key`)
  is refused (types.md F-CHAP-1).

Spec: format.md F-RT-6, types.md F-CHAP-5 (maintainer decisions,
2026-10-23). Covered by:
`WriteToolTests.RemovingAChaptersLastPovSaysWhatItNoLongerInherits`,
`WriteToolTests.ChapterListItemsGoInAtAnIndexAndComeOutByValue`.

### SC-MCP-21. My assistant tries to edit a finished book

The writer has frozen the book (`status: final`).

- Adding to Swales, creating a character in the book, and changing a
  chapter's fields are each refused as `frozen`, and nothing changes.
  The universe isn't frozen, so a new location there is made.

Spec: format.md F-BOOK-1 (maintainer decision, 2026-10-22). Covered by:
`WriteToolTests.NothingInAFinalBookIsWritten`.

### SC-MCP-22. My assistant checkpoints a session's edits

After adding Whitby Abbey and a line about Lucy, the assistant makes a
checkpoint.

- The edits weren't commits. `checkpoint` records both in one commit
  whose message ends with the assistant's `Co-Authored-By` line; a
  second straight after has nothing to record. In a den without
  history, the result says so and that the edits are still saved in
  the files. A co-author that isn't `Name <email>` on one line is
  refused. With `paths`, only those files are recorded, and the new
  location stays uncommitted; a `paths` list with nothing in it is
  refused rather than read as the whole den.
- Meanwhile the writer fixed a typo in Mina's note in their editor. The
  checkpoint records it too, and its result lists Whitby Abbey and
  Lucy under `written_by_session` and Mina under
  `changed_outside_session`, so the assistant can say "Mina's note
  changed outside this session". After the checkpoint, the session
  starts afresh: a later checkpoint lists only what was written since.
  If the writer adds a line to Lucy after the assistant's edit, Lucy is
  in both lists. If the writer commits the assistant's edit in their
  own git client and then edits Lucy by hand, Lucy is only under
  `changed_outside_session`. A note a failed write put back isn't the
  session's.

Spec: design.md §6, §8 (maintainer decisions, 2026-10-22 and
2026-10-25). Covered by:
`HistoryToolTests.WritesWaitForACheckpointThatNamesTheAgentAsCoAuthor`,
`HistoryToolTests.ACheckpointOfSomePathsRecordsOnlyThem`,
`HistoryToolTests.ACheckpointSaysWhichFilesTheSessionWroteAndWhichChangedOutsideIt`,
`HistoryToolTests.ASessionWriteTheWriterCommittedElsewhereIsntTheSessionsAnyMore`,
`HistoryToolTests.AFileEditedByTheSessionAndTheWriterIsInBothLists`,
`HistoryToolTests.AFileAFailedWritePutBackIsntTheSessions`,
`WriteToolTests.WithoutHistoryACheckpointSaysSoAndTheEditsStay`.

### SC-MCP-23. My pinned assistant creates a note

The server is pinned to universe `drift`, and the new id it draws first
happens to be one a note in `starfall` uses.

- The id is drawn again, so the new note's id is unused anywhere in the
  den, though the session can't read `starfall`. It can't create a
  note in `starfall` either (`bad_argument`).

Spec: F-ID-3 (maintainer decision, 2026-10-23). Covered by:
`WriteToolTests.APinnedSessionChecksNewIdsAcrossTheWholeDen`.

### SC-MCP-24. My assistant makes Mara the default POV for a book

In the Starfall den, `ember`'s prologue says `pov: []` and its chapter 3
has Tomas Reyes's POV; its other chapters set none. The writer asks the
assistant to make Mara Voss the book's default POV.

- `get_chapter_defaults` for the book gives `book.md`'s hash (the same
  as `get_chapter`'s), its defaults, and the books they reach. After
  `update_chapter_defaults` adds `[[mara-voss]]` to `pov`, the result
  says "Book 'ember': chapters 1–2b now have pov [[mara-voss]];
  chapters prologue, 3 keep their own". Removing the item leaves
  `pov: []` and says those chapters now have no POV; clearing the field
  goes back to inheriting, here from nothing. Clearing a field that
  isn't set, and an old hash, are refused, and asking for what the file
  already says writes nothing.
- A series' defaults reach both its books: the result names each book's
  chapters, and a book whose own defaults set the field keeps that
  value, named with where it comes from. A universe's defaults reach the
  standalone book too, and chapters of the series' books keep the
  series' value. Clearing the series' value hands its books the
  universe's.
- A series with no books yet can have defaults ("no chapter's tense
  changes now"), and a series file without an id gets one (F-ID-6).
- A value that doesn't fit the field (`tense: sideways`) is saved, as
  in `update_chapter_fields`, with the F-CHAP-3 warning, and the result
  says it's ignored, so chapters inherit as if it weren't there.
  `get_chapter_defaults` lists it as `ignored`, with the value as
  written. A value the same as the one above is said to be so.

Spec: types.md F-CHAP-5, format.md F-CTR-5, F-RT-6 (maintainer
decisions, 2026-10-23, 2026-10-25). Covered by:
`ChapterDefaultsToolTests.ABooksDefaultPovReachesTheChaptersThatDontSetTheirOwn`,
`ChapterDefaultsToolTests.ASeriesDefaultSaysWhichBooksAndChaptersItReaches`,
`ChapterDefaultsToolTests.ASeriesWithNoBooksYetCanHaveDefaultsAndGetsAnId`,
`ChapterDefaultsToolTests.AValueThatDoesntFitTheFieldIsSavedWithAWarningAndSaysItsIgnored`.

### SC-MCP-25. My assistant changes a default a finished book inherits

`ash` is final, and its own `chapter_defaults` set `tense: past`.

- Adding a default POV to the series `long-dark` would change what
  `ash`'s chapters have, so it's refused as `frozen`, naming the book
  and chapters, and nothing changes. So is any edit of `ash`'s own
  defaults, and setting the universe's `narration`, which `ash` would
  inherit.
- Setting the universe's `tense` is made: `ash`'s own default shadows
  it, and the result says `ash`'s chapters keep `past`.
- A second book named `ash`, in another series, doesn't hide the final
  one: books are matched by folder, and the result names each `ash` by
  its folder.
- A universe-scope Mara Voss means `[[mara-voss]]` in the universe's
  defaults isn't the series' Mara Voss, so setting the same text in the
  series' defaults changes `ash`'s POV, and is refused. Links are
  compared by their text and by the entity they reach, so a different
  spelling of the same link counts as a change too.
- If `ash` is frozen while the edit is being made, the write is refused
  as `stale_file`, saying to call `get_chapter_defaults` again. If
  `ash`'s `book.md` has errors, so the den leaves it out, but it says
  `status: final`, the edit is refused too; a draft book with errors
  doesn't stop it, and the result says its chapters will inherit the
  edit once it's fixed.

Spec: format.md F-BOOK-1, F-KEY-5, types.md F-CHAP-4, F-CHAP-5 (#151).
Covered by:
`ChapterDefaultsToolTests.ADefaultThatWouldChangeAFinalBooksChaptersIsRefused`,
`ChapterDefaultsToolTests.AFinalBookIsFoundByItsPathWhenAnotherBookSharesItsKey`,
`ChapterDefaultsToolTests.ALinkInADefaultIsComparedByTheEntityItReaches`,
`ChapterDefaultsToolTests.ABookFrozenWhileTheEditIsMadeStopsIt`,
`ChapterDefaultsToolTests.ABookLeftOutForErrorsThatSaysFinalStopsTheEdit`.

### SC-MCP-26. My assistant renames a character

A note on Whitby dialect links `[[Mr. Swales]]`, `[[mr. swales # voice
|how he talks]]`, and `[[mr-swales]]`, and a field links
`[[Mr. Swales#Voice]]`; a chapter's `pov` in `book.md` and a row of the
outline (`[[Mr. Swales\|Swales]]`) link him by name too. The assistant
renames him Old Swales.

- `rename_entity` changes his `name`, and his title heading, which read
  the old name, and rewrites every link that reached him by the old
  name to `[[Old Swales]]`, keeping each link's spacing, heading, and
  label, in the same change. The link by file name is left alone, and
  so is the file's name. The result lists every file written with its
  new hash, and `check` finds no broken or ambiguous link.
- With `keep_old_name`, "Mr. Swales" becomes an alias instead and no
  link changes, which suits a name that changes in the story. If
  another character in the book goes by "Mr. Swales" too, the result
  says that links by it are now ambiguous (F-LINK-7), naming the link
  in the note. A new name that only changes case or spacing changes no
  link.
- If a note in a final book uses the old name, nothing is renamed
  (`frozen`), and the error suggests `keep_old_name`, which leaves the
  frozen book alone. Swales in a final book isn't renamed at all. A new
  name that would send the rewritten links elsewhere ("Renfield", R. M.
  Renfield's file name) is refused, and so is one a link can't hold
  (`#`). A file with errors that uses the old name is named in the
  result for the writer to fix by hand, and a write that fails part way
  puts back what it wrote, as does one where `book.md` changed after
  the rename was planned. The id still never changes.
- Links in code, HTML comments, embeds, and escaped links are left
  alone. A link in a block or folded scalar, or a quoted value, is
  spliced in place; a plain value the new name ("Swales: the elder")
  would make read differently is written anew, quoted. Renamed in the
  universe, a character whose new name a character in the book has
  ("Mr. Swales") would lose the book's links to that one, so the
  rename is refused, naming them. Renaming the Count to his own alias
  "Dracula" takes it out of his aliases.
- Renamed "the Count", Swales would take a note's `[[the Count]]` from
  Count Dracula, since a name matches before an alias, even with
  `keep_old_name`. That's refused, listing the link with its old and
  new target (and saying if it's in a final book); with
  `allow_relinks`, the rename is made and the result lists the link. A
  new name another note already has (Renfield's file name) is said in
  the result even when the old name is kept. If a sailor in the
  universe is also named Mr. Swales, keeping "Mr. Swales" only as an
  alias would send a book note's `[[Mr. Swales]]` to the sailor, whose
  name now wins: refused the same way, unless `allow_relinks`.

Spec: format.md F-RT-5, F-LINK-1, F-LINK-4, F-LINK-6, F-LINK-7, F-SEC-2,
F-BOOK-1 (maintainer decisions, 2026-10-25 and 2026-10-26). Covered by:
`RenameEntityToolTests.RenamingAnEntityRewritesTheLinksThatUsedTheOldName`,
`RenameEntityToolTests.KeepingTheOldNameAsAnAliasLeavesTheLinksAlone`,
`RenameEntityToolTests.ALinkInAFrozenBookStopsTheRewriteButNotKeepingTheOldName`,
`RenameEntityToolTests.AKeptAliasThatAnotherEntityHasIsSaidToBeAmbiguous`,
`RenameEntityToolTests.ANewNameThatWouldSendARewrittenLinkElsewhereIsRefused`,
`RenameEntityToolTests.AFileWithErrorsThatUsesTheOldNameIsNamed`,
`RenameEntityToolTests.ARenameThatFailsPartWayPutsBackWhatItWrote`,
`RenameEntityToolTests.RenamingToOneOfItsOwnAliasesTakesThatAliasOut`,
`RenameEntityToolTests.ANewNameThatCollidesIsSaidEvenWhenTheOldNameIsKept`,
`RenameEntityToolTests.ABookEntityWithTheNewNameShadowsItForLinksInTheBook`,
`RenameEntityToolTests.LinksInCodeCommentsAndEmbedsAreLeftAlone`,
`RenameEntityToolTests.LinksInFrontmatterValuesKeepTheirStyleOrAreQuotedAnew`,
`RenameEntityToolTests.AContainerFileChangedWhileTheRenameIsMadeStopsIt`,
`RenameEntityToolTests.ANameThatTakesAnotherEntitysLinksIsRefusedUnlessAllowed`,
`RenameEntityToolTests.ARelinkInAFinalBookIsListedAndAllowedOnlyWithTheFlag`,
`RenameEntityToolTests.AKeptNameThatLosesItsLinksToAnotherEntitysNameIsRefusedUnlessAllowed`,
`EntityRenameEditTests.ALinksTargetIsReplacedKeepingItsSpacingHeadingAndLabel`,
`EntityRenameEditTests.TheTitleChangesAndNothingElse`,
`EntityRenameEditTests.ASetextTitleOrOneWithACommentKeepsItsForm`.

### SC-MCP-27. My assistant changes one of the Count's aliases

A note links `[[the Count]]`, `[[The  count|him]]`, `[[Count de
Ville]]`, and `[[Dracula]]`. The assistant changes the alias "the
Count" to "the Voivode", then removes "Count de Ville".

- `change_alias` changes the alias in place and rewrites the two links
  that used it to `[[the Voivode]]`, label kept. Removing "Count de
  Ville" takes it out of `aliases` and rewrites its link to the
  Count's name. With `keep_old_alias`, the new alias goes in beside the
  old one and no link changes. An alias he doesn't have, a new alias
  that's already his name or another of his aliases, and
  `keep_old_alias` without a new alias are refused.
- If a note in a final book uses the alias, it isn't removed or
  changed (`frozen`); a change suggests `keep_old_alias`. If a legend
  note in the universe is also named Count Dracula, a universe note's
  `[[the Count]]` rewritten to his name would reach the legend, so
  removing the alias is refused, suggesting keeping it or linking by id
  or file name first.
- A new alias "Lucy" would make a note's `[[Lucy]]`, which reaches Lucy
  Westenra, ambiguous: refused, even with `keep_old_alias`, unless
  `allow_relinks`, and then the result lists the link. A link that was
  ambiguous before (`[[the sailor]]`, which Swales and Young Swales
  both go by) and reaches Young Swales once Swales's alias changes is
  only reported.

Spec: format.md F-RT-5, F-RT-6, F-ENT-4, F-LINK-7, F-BOOK-1 (maintainer
decisions, 2026-10-25 and 2026-10-26). Covered by:
`RenameEntityToolTests.ChangingOrRemovingAnAliasRewritesItsLinks`,
`RenameEntityToolTests.RemovingAnAliasALinkInAFrozenBookUsesIsRefused`,
`RenameEntityToolTests.RemovingAnAliasWhoseLinksWouldReachAnotherEntityIsRefusedWithItsOwnHint`,
`RenameEntityToolTests.AnAliasThatTakesAnotherEntitysLinksIsRefusedUnlessAllowed`,
`RenameEntityToolTests.AnAmbiguousLinkThatNowResolvesIsReportedNotRefused`.

### SC-MCP-28. My assistant moves a note to another scope

In the invented Starfall den, The Long Dark is a series of two books,
Ember then Ash, followed by the standalone A Quiet Moon and The Drift
War, a series with no books yet. The assistant moves notes between them.

- Ilse Brandt, filed in Ash with no `from`, moves up to the series:
  `move_entity` moves the file to the series' `entities/characters/`
  and writes `from: ash`, the point where she entered canon, so she's
  still out of Ember's views and in from the start of Ash. The result
  gives the new path and hash, and marks the file with its old path.
  Moved from the series into Ember, its first book, a character with no
  `from` gets none: the series starts where Ember does.
- Mara Voss, a series character from `ember:1`, moved into Ash is
  refused (`entry_outside_scope`): the reader has already met her when
  Ash starts, and the error names both ways forward. With `keep_entry`
  she moves and keeps `from: "ember:1"`, and `check` warns that it's
  outside her book (F-ENT-6). With `enter_at_new_scope` her `from` is
  taken out, so she enters at the start of Ash, and since she holds
  pinned canon the result flags it. A series character with no `from`
  kept this way gets `from: ember`. A character of The Drift War with no
  `from` can't keep its entry point, since no position names it, so only
  `enter_at_new_scope` moves it.
- With a second Tessaly in A Quiet Moon, the Ember outline's
  `[[Tessaly]]` and Mara's `[[tessaly]]` reach the series' Tessaly only
  because it's nearer. Moving it into Ash rewrites both to its id with
  the same text (`[[01JA...\|Tessaly]]` in the table row), in the same
  change; a link from Ash, which still reaches it, is left alone. If
  Ember is final, that outline can't be rewritten, so nothing moves.
  Nothing moves into or out of a final book either.
- The Lantern ship moved into Ash would tie with the Lantern item there,
  so Halden Pike's `[[Lantern]]`, which reaches the item, would become
  ambiguous: refused, naming the link, unless `allow_relinks`, and then
  the result lists it. Pike himself moved to the universe would lose his
  own link to the item: refused the same way. The ship moved to the
  universe makes the Reach's ambiguous `[[Lantern]]` reach it, which is
  only reported.
- A file already at the new path stops the move (`already_exists`). A
  move that fails part way, writing a file or deleting the old one, puts
  everything back and leaves no new file. A note without an id gets one.
  An event whose `row` ids would now resolve among other outlines
  (moved from the series to the universe) is said in the result.

Spec: format.md F-RT-5, F-ENT-6, F-POS-7, F-ORD-3, F-LINK-7, F-BOOK-1
(maintainer decisions, 2026-10-24 and 2026-10-26). Covered by:
`MoveEntityToolTests.ABookEntityWithNoFromMovedUpGetsTheEntryPointItHadAsItsFrom`,
`MoveEntityToolTests.AMoveThatStartsAtTheSamePointWritesNoFrom`,
`MoveEntityToolTests.ANarrowingMoveOutsideTheEntryPointIsRefusedNamingBothWaysForward`,
`MoveEntityToolTests.KeepEntryKeepsTheFromAndItsWarning`,
`MoveEntityToolTests.EnterAtNewScopeTakesTheFromOutAndFlagsPinnedCanon`,
`MoveEntityToolTests.ASeriesEntityWithNoFromMovedIntoItsSecondBookWritesTheSeriesStartWithKeepEntry`,
`MoveEntityToolTests.AnEntryPointNoPositionCanNameNeedsEnterAtNewScope`,
`MoveEntityToolTests.LinksThatWouldStopReachingItAreRewrittenToItsId`,
`MoveEntityToolTests.ALinkToRewriteInAFinalBookStopsTheMove`,
`MoveEntityToolTests.ALinkThatWouldReachAnotherEntityIsRefusedUnlessAllowed`,
`MoveEntityToolTests.TheMovedFilesOwnLinksCountToo`,
`MoveEntityToolTests.AnAmbiguousLinkThatNowResolvesIsReported`,
`MoveEntityToolTests.AFileAlreadyThereStopsTheMove`,
`MoveEntityToolTests.AMoveThatFailsPartWayPutsBackWhatItWrote`,
`MoveEntityToolTests.AnEntityWithoutAnIdGetsOneAndOtherArgumentsAreChecked`,
`MoveEntityToolTests.RowIdsThatNowResolveElsewhereAreSaid`,
`EntityMoveEditTests.ALinkRewrittenToAnIdShowsTheSameText`,
`EntityMoveEditTests.AnOverlayCanLeaveAFileOut`.

## STORE: Wolfden saves, moves, and deletes notes

Background: the den store (design.md §6, #121) is how Wolfden writes a
den's files: the write API, and the MCP write tools to come (#21), save
through it. A den on disk is written in place, atomically, byte for byte
as given. The store refuses a path that could leave the den, or that
wouldn't be the same file on every OS, before anything is touched. The
examples use the invented Starfall den from the tests.

### SC-STORE-1. Wolfden saves a note while something else is reading it, or the power goes out

Wolfden saves Mara Voss's note while the index's watcher, or another
program, reads it, and once the machine loses power part way through a
save.

- Whatever reads the note sees the old note or the new one, never part
  of either, and after a failed save the old note is still there,
  unchanged. No temporary file is left among the notes, and the watcher
  never indexes one. The saved bytes are exactly the ones given: line
  endings, a byte order mark, and a missing final newline are kept.

Spec: F-RT-1, F-ENC-2, F-LAY-2, design.md §6. Covered by:
`DenStoreTests.AWriteKeepsTheBytesExactlyAsGiven`,
`DenStoreTests.AWriteLeavesNoTemporaryFileBehind`,
`DenStoreTests.AWriteThatFailsPartWayLeavesTheOldFileAndNoTemporaryFile`,
`DenStoreTests.AReaderSeesTheOldFileOrTheNewOneNeverPartOfEither`,
`DenWatcherTests.AWriteThroughTheDenStoreIsFollowedLikeAnyEdit`.

### SC-STORE-2. My assistant asks to write a file outside the den

An AI assistant asks to save `../outside/escaped.md`, an absolute path,
`.git/hooks/pre-commit`, or a note inside a folder that's a link to a
folder outside the den.

- The write is refused, saying why, and nothing outside the den, or in
  `.git/` or `.wolfden/`, is created, changed, or deleted. The same holds
  for deleting and moving.

Spec: F-LAY-2, design.md §6. Covered by:
`DenPathTests.APathThatCouldLeaveTheDenOrIsHiddenIsRefused`,
`DenPathTests.ANameWindowsDoesntKeepAsWrittenIsRefusedOnEveryOs`,
`DenStoreTests.AWriteOutsideTheDenOrIntoAHiddenFolderIsRefusedAndTouchesNothing`,
`DenStoreTests.AnAbsolutePathIsRefused`,
`DenStoreTests.EveryWriteChecksItsPaths`,
`DenStoreTests.AWriteNeverGoesThroughAFolderLink`.

### SC-STORE-3. A new note whose name differs from another only in case

Series `long-dark` has `mara-voss.md`. Wolfden is asked to save
`Mara-Voss.md` beside it, or a note under `universes/Starfall/`.

- The save is refused on every OS, naming the file or folder that's
  already there. On Windows and macOS the two names are one file, so the
  save would overwrite Mara's note; on Linux the den would no longer
  clone onto those systems. A Windows short name such as `CHARAC~1`, and
  a name that differs only in Unicode form (`café` typed two ways), are
  refused the same way. So is a new name that an invisible character
  disguises: a zero-width space, a byte order mark, or a text-direction
  control anywhere in it, an invisible character at its start or end, or
  one that looks empty or hidden without them. Emoji (a red heart with
  its variation selector, a family joined with zero-width joiners) and
  the zero-width non-joiner of Persian and Indic spelling are fine, and a
  file already named with any of these, made outside Wolfden, can still
  be saved, moved, and deleted.

Spec: design.md §6. Covered by:
`DenStoreTests.ANameThatDiffersOnlyInCaseIsRefusedOnEveryOs`,
`DenStoreTests.AWindowsShortNameIsAnotherNameAndIsRefused`,
`DenStoreTests.ANameInAnotherUnicodeFormIsRefused`,
`DenStoreTests.ACaseOnlyRenameOntoAnotherFileWithThatNameIsRefused`,
`DenPathTests.ANewNameThatInvisibleCharactersDisguiseIsRefused`,
`DenPathTests.EmojiAndScriptsThatNeedJoinersAreFine`,
`DenStoreTests.ANewNameDisguisedByAnInvisibleCharacterIsRefused`,
`DenStoreTests.ANoteNamedWithEmojiOrAJoinerCanBeMadeMovedAndDeleted`,
`DenStoreTests.AFileAlreadyNamedWithAnInvisibleCharacterCanStillBeSavedMovedAndDeleted`.

### SC-STORE-4. Wolfden is asked to change a built-in pack

Something asks the store of the built-in packs to change
`core/types/thread.yaml`.

- It's refused as read-only, with a message that says to extend the type
  from a pack in the den's `packs/` folder instead. The built-in pack is
  unchanged.

Spec: F-PACK-4, design.md §6. Covered by:
`DenStoreTests.TheBuiltInPacksRefuseEveryWrite`.

### SC-STORE-5. I rename a note or a book's folder

The writer renames `mara-voss.md` to `Mara-Voss.md`, which changes only
the case, and moves book `ember` out of its series.

- The note keeps its bytes under its new name, and the book's folder
  moves with everything in it. A rename that changes only the case works
  on Windows and macOS too. A move never overwrites a file or folder
  that's there, and a folder can't be moved into itself. A case-only
  rename that fails part way leaves the folder under its old name.

Spec: design.md §3, §6. Covered by:
`DenStoreTests.AFileIsMovedOrRenamedWithItsBytes`,
`DenStoreTests.ARenameThatChangesOnlyTheCaseIsAllowed`,
`DenStoreTests.AFolderIsMovedWithEverythingInIt`,
`DenStoreTests.AMoveDoesntOverwriteOrMoveAFolderIntoItself`,
`DenStoreTests.ACaseOnlyFolderRenameGoesThroughAHiddenNameWhenThePlainMoveIsRefused`,
`DenStoreTests.ACaseOnlyFolderRenameThatFailsHalfWayIsPutBack`.

## HIST: version history

Background: history is a capability that a backend provides (design.md
§6). Git is the v0 backend (#19); a den can also be a plain folder, with
no history at all.

### SC-HIST-1. My den has no history

The writer keeps the den in a synced folder without git, and asks for a
note's history, a diff, a checkpoint, or a freeze tag.

- Each says, in words, that the den has no history, so that isn't
  available, and that Wolfden can keep history with git if the den is in
  a git repository that isn't in a synced folder, with a private remote
  for backup. None of them is an error, and none points the writer at
  a design document or a command that doesn't exist. A checkpoint's
  message adds that the edits are still saved in the files. The den still loads, checks,
  and indexes, and freezing a book is still `status: final` (F-BOOK-1);
  only the tag is missing.

Spec: F-BOOK-1, design.md §5, §6. Covered by:
`HistoryProviderTests.ADenWithoutABackendHasNoHistoryCapabilities`,
`HistoryProviderTests.EveryOperationSaysHistoryIsntAvailableWithoutThrowing`,
`HistoryProviderTests.TheMessagesSendTheWriterNowhereTheyCantGo`,
`HistoryProviderTests.ADenWithoutHistoryStillLoadsAndChecks`,
`GitHistoryTests.ADenOutsideAnyRepositoryHasNoHistory`,
`GitHistoryTests.AGitAboveACeilingDirectoryDoesntCount`,
`FreezeTagTests.WithoutHistoryTheBookIsStillFrozenButNotTagged`.

The scenarios below have a den in a git repository, read through the git
provider (design.md §6), which runs the writer's own `git`.

### SC-HIST-2. I look back through a note's history

The writer has checkpointed Mara Voss's note three times over a month,
once on a branch they later merged, and asks for its history.

- The checkpoints that changed her file are listed newest first, each
  with its message (all of it), author, time, and the files it changed;
  checkpoints that only changed other notes aren't. The den's whole
  history lists every checkpoint. Asking a page at a time, with each
  page starting after the last page's final checkpoint, lists every
  checkpoint once, those that came in through the merge included, in
  the same order as the whole list, even when a page ends on a
  checkpoint from the merged branch. A starting point that isn't one of
  the listed checkpoints is refused. Names with spaces, accents, or
  brackets come back as they are.

Spec: design.md §6 (#19). Covered by:
`GitHistoryTests.ANotesHistoryListsTheCheckpointsThatChangedItNewestFirst`,
`GitHistoryTests.TheLogPagesWithALimitAndABeforeRevisionThroughAMerge`,
`GitHistoryTests.APageThatEndsOnAMergedBranchsCommitLosesNoOlderMainLineCommit`,
`GitHistoryTests.NamesWithSpacesAndAccentsComeBackAsTheyAre`.

### SC-HIST-3. I make a checkpoint at the end of a session

The writer edited Mara's note, deleted Fen Harbour's, and added a new
item, then makes a checkpoint, "End of chapter 14 session".

- One commit records all three changes, and the checkpoint lists them.
  A second checkpoint straight after has nothing to record and makes no
  commit. A checkpoint of only Mara's note records only it, and the new
  note that wasn't named stays as it was. The first checkpoint of a new
  repository works too. A checkpoint needs a message, without control
  characters other than line breaks and tabs. If the writer's
  pre-commit hook refuses the commit, the checkpoint says git refused
  it and why, nothing is recorded, and the new note is untracked again,
  as it was before.

Spec: design.md §6 (#19). Covered by:
`GitHistoryTests.ACheckpointRecordsNewChangedAndDeletedNotes`,
`GitHistoryTests.ACheckpointCanRecordOnlySomeNotes`,
`GitHistoryTests.TheFirstCheckpointOfANewRepositoryWorks`,
`GitHistoryTests.ACheckpointNeedsAMessage`,
`GitHistoryTests.ACheckpointMessageCantHoldControlCharacters`,
`GitHistoryTests.ACheckpointAHookRefusesLeavesTheNewFilesUntracked`,
`GitHistoryTests.ACheckpointCancelledAfterGitCommittedKeepsTheNewFilesTracked`.

### SC-HIST-4. My AI assistant makes a checkpoint

An assistant writing through MCP makes a checkpoint of the notes it
added.

- The commit is the writer's, with their name and signing as git is set
  up, and the message ends with a `Co-Authored-By:` line naming the
  assistant. A co-author that isn't `Name <email>` on one line is
  refused, and nothing is committed.

Spec: design.md §6, §8 (#19, #21). Covered by:
`GitHistoryTests.AnAgentsCheckpointRecordsItAsACoAuthor`,
`GitHistoryTests.ACoAuthorIsANameAndAnEmailOnOneLine`,
`HistoryToolTests.WritesWaitForACheckpointThatNamesTheAgentAsCoAuthor`.

### SC-HIST-5. My den is one folder of a bigger repository

The writer keeps `writing/vael-notes/` as a den inside a repository that
also holds a README and a blog. They've staged a README change by hand,
and edited a blog post without staging it. Their editor keeps settings
in `.obsidian/`, and swap files next to notes.

- The den's history lists only commits that changed the den's files,
  with paths relative to the den. A checkpoint commits only the den's
  files: the staged README change stays staged and out of it, and the
  blog post stays as it was. Hidden files and folders (`.obsidian/`,
  `.wolfden/`, swap files) are never committed, compared, or listed,
  and a commit made by hand that only touched them isn't in the den's
  history. Asking for a hidden path's history is refused.

Spec: F-LAY-2, design.md §6 (maintainer decision, #57). Covered by:
`GitHistoryTests.ADenInALargerRepositoryHasOnlyItsOwnHistory`,
`GitHistoryTests.ACheckpointLeavesWhatTheUserStagedOutsideTheDenAsItWas`,
`GitHistoryTests.HiddenPathsAreNeverRecordedOrReported`.

### SC-HIST-6. What have I changed since my last checkpoint?

The writer edited one note, deleted another, and made a new one, and
hasn't checkpointed yet.

- A diff since the latest checkpoint shows all three, the new note
  included although git doesn't track it yet, each with a unified diff.
  Asking for the diff doesn't stage anything: `git status` is the same
  afterwards. A diff of one file shows only it, and a diff between two
  checkpoints shows a moved note as a rename. A checkpoint that isn't
  in the repository is an error that says so, and a revision is never
  read as a git option.

Spec: design.md §6 (#19). Covered by:
`GitHistoryTests.ADiffSinceTheLatestCheckpointShowsNewChangedAndDeletedNotesAndLeavesTheIndexAlone`,
`GitHistoryTests.ADiffBetweenTwoCheckpointsShowsARename`,
`GitHistoryTests.ARevisionThatIsntOneIsAHistoryException`.

### SC-HIST-7. I freeze a finished book

The writer freezes Ember, book `ember` of universe `vael`, and later
adds more notes. Another den in the same repository also has a book
`ember` in a universe `vael`, and is frozen afterwards.

- The tag `book/vael/ember/final` names the latest checkpoint of the
  den. Freezing again, or freezing the other den's Ember, leaves the
  tag where it is and says the repository already has it; Wolfden never
  moves or replaces a tag. In a repository where nothing in the den has
  been checkpointed yet, there's nothing to tag, and freezing says so
  (the book is still frozen). A tag name git doesn't accept, or one
  git would read as something else (`HEAD`, `@`, `refs/...`), is
  refused. A tag without a message works even when the writer's git
  signs tags.

Spec: F-BOOK-1, design.md §5 (maintainer decision, #31). Covered by:
`FreezeTagTests.TheTagIsNamedForTheUniverseAndTheBook`,
`FreezeTagTests.FreezingTagsTheLatestCheckpointOnceAndNeverMovesTheTag`,
`FreezeTagTests.AnotherDenInTheRepositoryWithTheSameKeysDoesntMoveTheTag`,
`FreezeTagTests.BeforeAnyCheckpointThereIsNothingToTag`,
`GitHistoryTests.ATagNamesTheLatestCheckpointAndIsNeverMoved`,
`GitHistoryTests.ATagNameMustBeOneGitAccepts`,
`GitHistoryTests.ALightweightTagWorksWhenGitSignsTags`.

### SC-HIST-8. Git isn't installed

The writer's den is in a repository, but this machine has no `git` on the
PATH.

- Each history operation says that git isn't installed, or can't be
  found, so that isn't available, and how to get history; none throws.
  A checkpoint's message adds that the edits are still saved in the
  files.

Spec: design.md §6 (maintainer decision, 2026-10-22). Covered by:
`GitHistoryTests.WithoutGitEveryOperationSaysSoWithoutThrowing`.
