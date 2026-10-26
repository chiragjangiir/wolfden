# Where to file things

Every note in a den (your Wolfden folder of notes) is filed at one level:
the **universe**, a **series**, or a **book**. This page helps you pick.
The full rules are in [`design.md`](design.md#scope-and-inheritance) and
[`format.md`](format.md) (F-ENT-6, F-LINK-6, F-LINK-7, F-VIEW-1).

The examples use an invented universe, *Brackwater*, with one series,
*Tidewater*, whose three books have the keys `book-1`, `book-2`, and
`book-3`.

## Filing doesn't decide what the story has shown

A **canon view** is what the reader knows at a point in the story, such as
the end of Book 2. Where a note is filed doesn't decide whether a canon
view shows it. A view shows everything the reader has met by then, at
every level. A sailor filed under Book 1 still shows in a view at the end
of Book 3. A note from Book 3 is left out of a Book 1 view because the
reader hasn't met it yet, not because of its folder.

Where the reader first meets something is its `from`: a line at the top of
a note. `from: "book-2:7"` means Book 2, chapter 7. A bare book key,
`from: "book-2"`, means the start of Book 2.

## What filing does decide

1. **Where the note enters canon when it has no `from`.** A book note
   enters at the start of its book, a series note at the start of the
   series, and a universe note at the start of the story. A `from`
   overrides this.
2. **What a series or book filter keeps.** You can narrow a view to one
   series or book: `wolfden query --book book-3`, or ask your AI assistant
   for a view of one book (see [Connecting MCP clients](mcp-clients.md)).
   A **book filter** keeps that book's notes, plus its series' and its
   universe's. It leaves out the other books, even earlier ones. So a
   sailor filed under Book 1 doesn't appear in a view filtered to Book 3.
   A **series filter** keeps the universe's notes, the series', and all its
   books', and leaves out other series and standalone books.
3. **Which note a link means when two match it the same way** (by file
   name, name, or alias). If `[[Lantern]]` matches two notes' names, the
   one filed nearest to the linking note wins: the same book, then its
   series, then the universe. If neither is on that path, or both are
   filed at the same level, the link is ambiguous and `wolfden check` warns
   about it. Nearness only breaks ties: a match on the file name beats a
   match on the name, wherever each note is filed.

## A habit that works

- **Anything that could recur goes at series level,** with `from` set to
  where the reader first meets it.
- **Things that really belong to one book go at book level.** A book note's
  `from` should be inside that book. `wolfden check` warns if it isn't.
- **Unsure? File it at series level.** A wrong guess is cheap to fix: ask
  your AI assistant to move the note (Wolfden's `move_entity` tool, see
  [Connecting MCP clients](mcp-clients.md)). It keeps where the note enters
  canon and where its links lead, and tells you when a move needs a
  decision from you.

A deckhand the reader first meets in Book 2, chapter 7, filed at series
level in
`universes/brackwater/series/tidewater/entities/characters/nell-harrow.md`:

```markdown
---
id: 01JB2K7Q9M3XR5T8V0W4YZ6HCD
type: character
name: Nell Harrow
status: canon
from: "book-2:7"
---

# Nell Harrow

## Overview
Deckhand on the [[Lantern]]. ...
```

Without that `from`, Nell would enter at the start of Book 1, and every
view before Book 2, chapter 7 would show her too early. A one-scene
harbormaster from Book 2 goes in
`universes/brackwater/series/tidewater/books/book-2/entities/characters/`.
Without a `from` he enters at the start of Book 2. Add one, such as
`from: "book-2:12"`, if he shouldn't show before his chapter.

## The cost of filing high: name clashes

Filing at series level puts more notes on the same level, and that's where
names tie. Say the ship *Lantern* (`lantern.md` in `entities/items/`) and a
tavern called the Lantern (`lantern.md` in `entities/locations/`) are both
series notes. Now `[[Lantern]]` is ambiguous everywhere. (If the tavern
were filed under Book 2 instead, `[[Lantern]]` in a Book 2 note would mean
the tavern and in a Book 1 or Book 3 note would mean the ship, with no
warning, which is easy to get wrong without noticing.)

Two fixes:

- **Give one of them a fuller name,** such as "The Lantern Inn", in its
  `name` and its file name (`lantern-inn.md`). Then `[[Lantern]]` means the
  ship and `[[The Lantern Inn]]` means the tavern. Before you rename, note
  the ambiguous links `wolfden check` lists: once the clash is gone, every
  one of them leads to the ship, so change the ones that meant the tavern
  to `[[The Lantern Inn]]`.
- **Link by id,** with a label for readers. Every note's id is on the
  `id:` line at the top of its file. If the ship's is
  `01JB2K8D4F6GH7J9KMNPQRSTVW`, then
  `[[01JB2K8D4F6GH7J9KMNPQRSTVW|the Lantern]]` always means the ship. An id
  never clashes.

`wolfden check` lists every ambiguous link, so you'll know when a clash
happens.

## Standalone books and universe-level notes

- **Standalone books** sit directly under the universe
  (`universes/brackwater/books/<book>/`). Their level above is the
  universe, not a series. A note used only in that book goes in the book.
  A note it shares with a series goes at universe level, since a series
  filter leaves out standalone books and a filter on the standalone book
  leaves out the series.
- **Universe-level notes** (`universes/brackwater/entities/`) are for what
  holds across every series and standalone book: world rules, a species, a
  port that turns up in more than one series. Without a `from` they're in
  canon from the start of the story.
- **Notes that aren't story canon,** such as style rules or research, use
  the `note` type and also go at universe level for now
  (`universes/brackwater/entities/notes/`). A place above the universe for
  them is planned ([design.md §2](design.md#entities-dont-span-universes)).
