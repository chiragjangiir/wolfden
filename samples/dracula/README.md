# Dracula sample den

A small, hand-written Wolfden den for Bram Stoker's *Dracula* (1897). It
is the first test of whether the format is pleasant to write by hand, and
the fixture corpus for Wolfden's parser and index tests. It follows
[`docs/format.md`](../../docs/format.md) and the core pack
([`docs/types.md`](../../docs/types.md)), format 1.

The novel is a good test case because it's told entirely in dated
journals, letters, and cuttings by several narrators, so story order and
in-world order often differ, and readers can check every fact against the
public-domain text ([Project Gutenberg ebook #345][pg]).

[pg]: https://www.gutenberg.org/ebooks/345

## What's here

```text
den.yaml
universes/dracula/
  universe.md
  entities/locations/      places, at universe scope
  entities/notes/          how dates work; the rules of the Un-Dead
  books/dracula/
    book.md                27 chapters and the closing Note, with pov,
                           narration, tense, and in-world dates
    outline.md             one table row per beat, by chapter
    entities/characters/
    entities/events/       dated events: in-world `when` vs. story `from`
    entities/factions/
    entities/items/
    entities/threads/      planted and paid-off threads, open questions
```

Start with `book.md`, then a character such as
`entities/characters/lucy-westenra.md`, which uses temporal fields and
sections that come into effect at different chapters.

## Choices made in this den

- **The year.** The novel never states it. Dates use a sort key of
  year, month, and day, with year `0` for the year of the main action and
  `7` for the closing Note. A stated year (1893 is the usual reading) is
  banked as an undecided question in
  `universes/dracula/entities/notes/dates-and-the-year.md`.
- **POV and narration.** Every document is first person, so the book sets
  `narration: first` and `tense: past` once in `chapter_defaults`. Each
  chapter's `pov` lists the main characters whose documents make it up.
  Minor writers (solicitors, a nun, newspapers) appear only in the
  outline's `pov` column, which is per beat.
- **Status.** Facts from the novel are `canon`. The writer's own open
  questions and interpretations are `undecided`, with the options banked
  as `candidate` subsections.

## License

The text in this den (summaries, notes, and outline) is by Gavin Wolfe,
licensed under [CC BY 4.0](../LICENSE). Short quotations are from Bram
Stoker's *Dracula*, which is in the public domain.
