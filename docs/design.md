# Wolfden Design

**Status:** draft v0.1 (2026-10-10). Everything here is open to revision until
there's code that depends on it. Decisions already made are recorded in
[`CLAUDE.md`](../CLAUDE.md). This doc explains them and fills in the shape.

## 1. Goals and non-goals

### Goals

1. **Keep a series' lore consistent across many books.** Characters,
   settings, timelines, plot threads, and invented things of any kind.
2. **Know when each fact becomes true.** Every fact can carry the story
   position where it enters canon, so "what's true as of Book 2, chapter 14?"
   has an answer.
3. **Plain files, no lock-in.** Notes are markdown files in a git repo you
   own. You can read them, edit them in any editor, and keep them forever.
4. **AI-native.** AI assistants are first-class users, connected through an
   MCP server and working from structured data instead of pasted documents.
5. **Works with notes edited outside the app.** You can edit the files in
   VS Code, an AI agent can rewrite a section, and Wolfden picks up the change.

### Non-goals (for now)

- **Not a manuscript editor.** Wolfden holds the story bible, not the prose.
  (Scrivener, Word, and plain markdown already do prose well.)
- **No publishing or formatting.**
- **No real-time collaboration in v0.** Multi-user hosting is a later phase
  (§10). The v0 design must not *rule it out*, but doesn't build for it.

## 2. Core concepts

| Concept | What it is |
|---|---|
| **Den** | A Wolfden workspace: one git repository of notes. It can hold one or many universes. To give a universe its own private remote, give it its own den. |
| **Author Identity** | Who a work is by: a real name, a pen name, or a collaboration. Metadata in v0, and the anchor for ownership in multi-user mode later. |
| **Universe** | A shared setting for one or more related works. |
| **Series** | An *ordered* sequence of books within a universe. |
| **Book** | A single work, either in a series or standalone in a universe. It has an ordered list of chapters. Scenes within chapters come later. |
| **Entity** | A single note about one thing: a character, a planet, a faction, a plot thread. Every entity has a **type**. |
| **Type** | A schema for entities: frontmatter fields, standard sections, and a template. |
| **Pack** | A bundle of types for a genre or purpose (core, space opera, epic fantasy, ...). |
| **Story position** | A point in the story: a book key plus a chapter key, written `ember:14`. v0 positions are chapter-level. `ember:14.2` (scene 2 of chapter 14) is reserved for later. |

### Scope and inheritance

Every entity lives at exactly one **scope**: Universe, Series, or Book. A view
of a Book includes its own entities plus everything inherited from its Series
and Universe. A character who appears across a whole series lives at Series
(or Universe) scope. A one-book side character lives at Book scope.

Entities can **reference across scopes** in any direction. Book 3 can link to
a character introduced in a standalone book in the same universe. A reference
to a *later* book is allowed, but the continuity check (§7) flags it, since
it's usually a spoiler or a mistake.

### Entities don't span universes

A universe is the boundary for story entities, links, and story positions.
Positions are ordered only among one universe's books, so an entity shared
between universes would have no clear "canon as of" answer.

- **Crossovers mean it's one universe.** If two series share characters and
  their history matters (a Cosmere-style shared cosmology), model them as two
  Series in one Universe.
- **Incarnations are copies.** The same idea recast per setting (an
  archetype, a recurring narrator voice) is a separate entity in each
  universe. A `see_also` field can point between the copies.
- **Links resolve within the universe.** A link that would match only an
  entity in another universe is flagged, not resolved.

**Planned (#45): Author and den notes.** Timeless material that isn't story
canon, such as voice and style rules or real-world research, will be able to
live above Universe: at Author Identity scope (`authors/<slug>/notes/`) or
den scope (`notes/`). Universes will inherit them, and they can't carry story
positions. Until then, keep such notes at Universe scope. Moving one up later
is a file move, since ids don't depend on paths.

## 3. On-disk format

The normative rules for §3 and §5 are in [`format.md`](format.md). This
section explains the shape. Where the two differ, `format.md` wins.

### Den layout

```text
my-den/
  den.yaml                      # den settings, author identities, enabled packs
  packs/                        # den-local custom types (optional)
  # notes/ and authors/ are reserved for Author and den notes (§2, #45)
  universes/
    starfall/
      universe.md               # frontmatter + overview prose
      entities/                 # universe-scope entities, grouped by type
        species/
          velari.md
        locations/
          kessan-reach.md
      series/
        long-dark/
          series.md             # frontmatter lists book order
          entities/
            characters/
              mara-voss.md
          books/
            ember/
              book.md           # frontmatter lists chapters
              entities/
              outline.md        # outline rows (see §5)
      books/                    # standalone books in this universe
        a-quiet-moon/
          book.md
```

Folder names are slugs for humans. **Identity comes from the `id` in
frontmatter, not from the path**, so files can be renamed or moved between
scopes without breaking links. The app rewrites slug links and positions
when it renames something. After a rename by hand, links by id, name, or
alias still work (format.md F-RT-5).

### An entity file

```markdown
---
id: 01JA7Q8M4XK3V9R2D5W8HN6TQB   # ULID, assigned on creation, never changes
type: character
name: Mara Voss
aliases: [Voss, "the Commander"]
status: canon                    # idea | draft | canon | retconned
fields:
  species: "[[velari]]"
  rank:
    - { value: Lieutenant, from: "ember:1" }
    - { value: Commander,  from: "ember:22" }
  born: "2291"
---

# Mara Voss

## Overview
Executive officer of the *Tessaly*...

## Appearance
Silver-streaked hair, a burn scar along her left forearm.

## Shoulder injury
<!-- wolfden: from=ember:19 -->
Takes a flechette round at Kessan Reach; the arm never fully recovers.

## Custom: Things she never says aloud
...
```

Key points:

- **Fields can change over time.** A plain value means it's always true. A list
  of `{value, from, until?}` entries means it's true at story positions.
- **Sections can carry positions too.** A `<!-- wolfden: from=... -->`
  comment on the line directly under a heading (with an optional `until=`)
  marks the whole section as entering canon at that point. GitHub,
  Obsidian, and VS Code hide HTML comments, so headings render cleanly and
  heading links keep working. Other HTML comments are left alone. This is a
  structured version of "Book 3, chapter 23 additions" sections in a
  hand-kept character file.
- **Links** are wiki-style: `[[mara-voss]]`, `[[Mara Voss]]`, or an alias.
  They resolve by slug, name, or alias to an `id` within the same universe
  (§2), and the index stores the id. Ambiguous links are flagged.
- **Status** keeps undecided material out of canon views without moving it
  elsewhere. `idea` entities are what a hand-kept "idea bank" holds.
- **Entities enter canon at a position.** An optional top-level `from`
  says where the reader first meets the entity. It defaults to the start of
  the entity's book, series, or the story, by scope, and canon-as-of views
  leave the entity out before it.
- **Sections and field values have a status too.** The section comment
  takes `status=` (`<!-- wolfden: status=candidate from=ember:19 -->`), and
  a field entry takes `status:`. Entities and facts share one vocabulary:
  `idea`, `undecided` (a question with options banked), `candidate` (one
  option), `draft`, `canon`, and `retconned`. Canon views leave out
  non-canon sections and values, and a section can't be more canon than its
  entity.
- **Pinned facts are hard canon.** `pin=true` on a section or `pin: true`
  on a field entry marks the facts most important to check before drafting.
  A **pinned canon** view lists them for a scope as of a position.
- Unknown frontmatter keys and sections are kept intact. The app must never
  discard what a human or an agent wrote.

## 4. Types and packs

A type is a YAML file:

```yaml
# packs/core/types/character.yaml
name: character
plural: characters
fields:
  species:  { type: ref, to: species }
  rank:     { type: string, temporal: true }
  born:     { type: string }            # in-world dates are free text in v0
  pov:      { type: bool }
sections:
  required: [Overview]
  suggested: [Appearance, Voice, Relationships, Arc]
template: character.md
```

Field types: `string`, `text`, `number`, `bool`, `enum`, `ref` (to a type),
`list` of any of those, and `temporal: true` for values that change over time.

**Planned packs:**

- **core** (always on): character, location, faction, item, event, thread
  (a plot thread with open/resolved state), idea, note (free-form).
- **space-opera**: star-system, planet, settlement, species, polity,
  ship-class, ship, technology.
- **epic-fantasy**: realm, magic-system, creature, deity, artifact, order.

Dens can define their own types in `packs/`, or extend a pack's type by
adding fields and sections.

## 5. Structure and time

The normative rules for books, chapters, positions, and outlines are in
[`format.md`](format.md) §6, §8, and §11.

### Books and chapters

`book.md` frontmatter holds the ordered chapter list. Each chapter has a
stable key, a number and an optional title. Story positions refer to chapter
keys, so renumbering chapters doesn't break positions. Book and chapter keys
can't contain `:` or `.`, which the position syntax uses as separators.

### Outline rows

An outline is a list of **rows** (beats) with stable IDs that keep counting
across books in a series. Book 1 might end at row 36, so Book 2 starts at row
37. That way a reference like "row 69a" is never ambiguous. Rows can be
split (`69a`, `69b`) without renumbering anything. A row can map to a
chapter and carry custom fields such as POV or tags.

Positions stay `book:chapter`: rows aren't positions in v0. An event can
point at rows with a plain `row:` field, and `ember:r75` is reserved for
row positions later, the same way `ember:14.2` is reserved for scenes.

### Timeline

Timeline entries are `event` entities with two independent axes:

- **In-world time:** when it happens in the story world. Free text plus a
  sortable key in v0. Custom calendars (fantasy months, stardates) come later.
- **Story position:** where the reader learns of it. Flashbacks and reveals
  make these differ, and the difference is often the interesting part.

### Freezing a finished book

When a book is done, **freezing** it adds a git tag (`book/ember/final`) and
marks it `status: final`. After that, edits to that book's scope are refused
unless explicitly unfrozen. This replaces hand-made `_FINAL` copies.

## 6. History and versioning

Git is the history. Wolfden doesn't keep its own audit trail.

- **Per-entity history** is `git log` on the entity's file, with diffs, shown
  in the UI and exposed through MCP.
- **Saving vs. committing:** edits are written to the working tree straight
  away. **Checkpoints** are commits with a message, made by the user ("end of
  Ch 14 session") or automatically after a configurable idle period. Commits
  made by AI agents through MCP record the agent as `Co-Authored-By`.
- **Remotes:** optional. Wolfden can push and pull a configured remote but
  never requires one.
- **The index is never the only copy of anything.** Deleting it loses
  nothing (§7).

## 7. Index, queries, and continuity checks

A SQLite database (`.wolfden/index.db`, gitignored inside the den) built
from the files:

- Tables for entities, field values (with from/until positions), sections
  (with positions), links, outline rows, and positions, plus FTS5 for
  full-text search.
- **Rebuilt** fully on demand and incrementally through a file watcher, so
  edits made outside the app show up within moments.

**Canon as of** is the main query. Given a position, it returns each
entity's field values and sections in effect at that point, leaving out
`idea` and `retconned` material. Point-in-story reading, spoiler-safe
reference and continuity checks all build on it.

**`wolfden check`** (continuity lint), v0 rules:

- Broken or ambiguous links, including a link that matches only an entity
  in another universe.
- Missing required fields or sections.
- Positions that refer to books or chapters that don't exist.
- Overlapping temporal values for the same field.
- A reference from an earlier book to an entity that first appears in a later
  one.
- Duplicate IDs (for example, from copying files by hand).

Later rules: a character appearing after their death, travel times that
don't fit the timeline, and open threads at a frozen book's end.

## 8. AI integration (MCP)

Wolfden ships an MCP server, using the official C# MCP SDK, over the same core
library the API uses. Draft tool surface:

| Tool | Purpose |
|---|---|
| `search` | Full-text and field search across a den or scope |
| `get_entity` | One entity, optionally `as_of` a position |
| `list_entities` | By type, scope, or status |
| `canon_as_of` | A summary of canon at a position, for a scope |
| `get_history` | Commit history and diffs for an entity |
| `create_entity` | From a type template |
| `update_fields` / `append_section` | Structured edits that keep everything else intact |
| `add_idea` | Record undecided material with `status: idea` |
| `check` | Run continuity checks and return findings |
| `checkpoint` | Commit with a message |

Writes go through the same validation as the UI. An agent can't save a file
the parser would reject.

`canon_as_of` and `get_entity` serve **pinned canon** first
([`format.md`](format.md) §9.4), so an agent sees the hard facts before
the rest.

## 9. Architecture

```text
src/
  Wolfden.Core/     # model, markdown and frontmatter parsing, positions, canon-as-of logic
  Wolfden.Index/    # EF Core + SQLite index, file watcher, FTS
  Wolfden.Git/      # history, checkpoints, tags (LibGit2Sharp or git CLI)
  Wolfden.Api/      # ASP.NET Core minimal API (.NET 10)
  Wolfden.Mcp/      # MCP server
  Wolfden.Cli/      # `wolfden init | index | check | serve | mcp`
web/                # React + TypeScript + Vite + TanStack Query
samples/            # sample dens (CC-BY-4.0)
docs/
```

- **v0 runs locally as one process.** `wolfden serve` hosts the API and the
  built web UI on localhost and opens a browser. `wolfden mcp` runs the MCP
  server over stdio for AI clients.
- **Parsing round-trips.** Read-modify-write keeps formatting, ordering, and
  unknown content. This gets heavy test coverage, since it decides whether
  people trust the tool with their notes.
- **Docker comes with hosting (§10).** It isn't needed for local use.

## 10. Phases

| Phase | Delivers |
|---|---|
| **M0: Format** | This doc settled; the format spec written; the Dracula sample written *by hand* in the format. The format can be used with any editor and an AI assistant before any code exists. |
| **M1: Core + CLI** | Parse, index, `wolfden check`, and canon-as-of queries from the command line. Round-trip test suite over the samples. |
| **M2: MCP** | The MCP server. Ships early because it's useful on its own: an AI assistant can work with a den before any UI exists. |
| **M3: Web UI (read)** | Browse the hierarchy, entity pages, search, a story-position "as of" slider, history and diffs. |
| **M4: Web UI (edit)** | Edit fields and sections, create from templates, checkpoints, freeze and unfreeze. |
| **M5: Packs + full sample** | Space opera and fantasy packs, plus the invented multi-book sample with history. |
| **Later** | Import from existing markdown notes; custom calendars; multi-user hosting (accounts, GitHub and Google OAuth, tenants, roles), Docker, collaboration. |

## 11. Sample datasets

These live under `samples/` and are licensed CC-BY-4.0. They're used for docs,
demos, and test fixtures.

- **`dracula`** (small): Bram Stoker's *Dracula* (1897, public domain). It's
  told in dated journals and letters by several narrators, which makes it a
  well-known, checkable test of timelines, POV, and story position vs.
  in-world time. Readers can verify its accuracy for themselves.
- **Invented series** (full; name TBD): an original two-book series, Book 1
  frozen and Book 2 in progress. It shows ideas, open threads, a retcon,
  outline rows continuing across books, and packs. It ships with **history**:
  a script replays a series of snapshots into a fresh git repo, so the
  versioning features have something real to show.
- **Possible later sample:** the Oz series, whose author contradicted
  himself often across 14 books. It would be a good stress test for
  continuity checks.

## 12. Open questions

1. ~~**Positions on sections:** is `{from="..."}` on headings the right
   syntax, or would HTML comments render more cleanly in other markdown
   tools?~~ **Decided (#3):** an HTML comment on the line under the heading,
   `<!-- wolfden: from=ember:19 until=ember:30 -->`. Attribute braces show up
   as literal text in GitHub, Obsidian, and VS Code and break heading links.
2. ~~**Positions finer than a chapter:** are scenes needed in v0?~~
   **Decided (#4):** no. v0 positions are chapter-level. The syntax
   `ember:14.2` is reserved for scenes, and v0 rejects it with a clear
   "scenes not supported yet" error instead of misreading it.
3. ~~**Entities that span universes** (the same author's recurring
   narrator, say): are they needed, or are copies across universes fine?~~
   **Decided (#5):** not in v0. Crossovers mean one universe, incarnations
   are copies, and links resolve within a universe (§2). Timeless Author and
   den notes are planned (#45), and their folder names are reserved now.
4. **Auto-checkpoint defaults:** an idle-time commit, or manual only by
   default?
5. **Desktop packaging:** is a localhost web app enough, or wrap it later
   (Tauri, Electron, .NET MAUI)?
6. **Name for the invented sample universe.**
7. ~~**Undecided and must-check material inside an entity:** how to mark a
   section or field value as an option still being weighed, or as a fact
   that must not be broken?~~ **Decided (#2):** sections and field entries
   carry their own `status`, from one vocabulary shared with entities that
   now includes `undecided` and `candidate`, capped by the entity's status.
   `pin` marks hard canon, served first as **pinned canon**. Positions stay `book:chapter`, and `ember:r75` is reserved for
   outline-row positions, which v0 rejects.
