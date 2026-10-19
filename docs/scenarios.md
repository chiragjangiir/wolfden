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

Spec: F-VIEW-1, F-ENT-6, F-ORD-1, F-LINK-5, F-LINK-7. Covered by: not yet
(#86).

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

Spec: F-VIEW-1, F-ENT-6, F-ORD-3, F-LINK-5, F-LINK-7. Covered by: not yet
(#86).

### SC-CANON-3. A character enters partway through a book

A character filed under `a1` has `from: a1:20`.

- Absent from a view at `a1:10`. Present at `a1:20` and in every later
  book.

Spec: F-ENT-6, F-VIEW-1. Covered by: not yet (#86).

### SC-CANON-4. A series-wide character who arrives late

A character filed at the series level (`a`) has `from: a3:14`.

- Absent at `a3:13` and from all earlier books, even though the file sits
  above every book. Present from `a3:14` on.

Spec: F-ENT-6, F-VIEW-1. Covered by: not yet (#86).

### SC-CANON-5. A fact changes over time

A character's occupation is a temporal field with two entries: one with no
`from`, which starts at the start of the story, and a new value
`from: a2:5`.

- A Book 1 view shows the old occupation. A Book 4 view shows the new one.

Spec: F-TMP-2, F-TMP-3, F-TMP-4, F-TMP-5, F-VIEW-1. Covered by: not yet (#86).

### SC-CANON-6. A secret identity the reader learns at a set chapter

A Book 1 character has a non-temporal field for their true identity,
written as a single entry with `from: a3:20`. Here `from` means the reader
learns the fact at that point, not that the value changes.

- Views before `a3:20` leave the field out. Views from `a3:20` on show it.

Spec: F-TMP-8, F-TMP-4, F-VIEW-1. Covered by: not yet (#86). See also #58
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
decision. Covered by: not yet (#86).

### SC-CANON-8. Retconned and idea material stays out of canon views

One entity is marked `retconned` and another is still an `idea`.

- Neither appears in any canon-as-of or pinned canon view, including the
  end-of-story view.
- Both are still indexed, and views that ask for out-of-canon material
  (such as an idea bank) show them.

Spec: F-VIEW-1, F-VIEW-2, F-STAT-3, F-STAT-4, §9.3. Covered by: not yet
(#86).

### SC-CANON-9. Two different side characters share a name

Book 1 and Book 2 each have their own minor character named "Officer
Hale". They're different people, each filed in their own book.

- A Book 4 view shows both, told apart by where each is filed.
- `[[Officer Hale]]` from a Book 1 file resolves to Book 1's Hale. From a
  Book 4 file it's ambiguous: a warning that lists both, and the link
  doesn't resolve.

Spec: F-VIEW-1, F-VIEW-3, F-LINK-7. Covered by: not yet (#86).
