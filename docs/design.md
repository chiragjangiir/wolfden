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
3. **Plain files, no lock-in.** Notes are plain markdown files in a folder
   you own, stored wherever you like: on your own machine, in a synced
   folder, or in a git repo, local only or with a private remote. You can
   read them, edit them in any editor, and keep them forever. Git is the
   recommended way to get history (§6).
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
| **Den** | A Wolfden workspace: the folder that holds `den.yaml`, and the notes in it. It can hold one or many universes. A den can be a git repository of its own or a subfolder of a larger one (§6). A universe that needs its own private remote goes in a den that is its own git repository. |
| **Author Identity** | Who a work is by: a real name, a pen name, or a collaboration. Metadata in v0, and the anchor for ownership in multi-user mode later. |
| **Universe** | A shared setting for one or more related works. |
| **Series** | An *ordered* sequence of books within a universe. |
| **Book** | A single work, either in a series or standalone in a universe. It has an ordered list of chapters. Scenes within chapters come later. |
| **Entity** | A single note about one thing: a character, a planet, a faction, a plot thread. Every entity has a **type**. |
| **Type** | A schema for entities: frontmatter fields, standard sections, and a template. |
| **Pack** | A bundle of types for a genre or purpose (core, space opera, epic fantasy, ...). |
| **Story position** | A point in the story: a book key plus a chapter key, written `ember:14`. v0 positions are chapter-level. `ember:14.2` (scene 2 of chapter 14) is reserved for later. **Decided (#2):** a bare `ember` means the start of that book. |

### Scope and inheritance

Every entity lives at exactly one **scope**: Universe, Series, or Book.
Where an entity is filed decides where it enters canon when it has no
`from`, which entities a series or book filter keeps, and which match wins
when links tie. It doesn't decide whether a canon view shows the entity: a
canon view at a position covers every scope of the universe, in story
order, so a Book 4 view shows a side character filed under Book 1
([`format.md`](format.md) F-VIEW-1). A character who appears across a whole
series lives at Series (or Universe) scope. A one-book side character lives
at Book scope.

Entities can **reference across scopes** in any direction. Book 3 can link to
a character introduced in a standalone book in the same universe. A reference
to a *later* book is allowed. A den that turns on spoiler checks gets it
flagged (§7), since it's usually a spoiler or a mistake.

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
  # anything else at the root (a README, a LICENSE) is ignored
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
frontmatter, not from the path** (once a file has one; tools add it), so files
can be renamed or moved between scopes without breaking links. The app rewrites
slug links and positions when it renames something. After a rename by hand,
links by id, name, or alias still work (format.md F-RT-5).

### An entity file

```markdown
---
id: 01JA7Q8M4XK3V9R2D5W8HN6TQB   # ULID, assigned on creation, never changes
type: character
name: Mara Voss
aliases: [Voss, "the Commander"]
status: canon                    # see format.md §9.3 for all six
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
  **Decided (#66):** a single entry with a `from` is allowed on any field,
  even one that isn't temporal (except `row`). It means "known from here":
  a death or a hidden identity stays out of canon views from before the
  reader learns it.
- **Sections can carry positions too.** A `<!-- wolfden: from=... -->`
  comment on the line under a heading (one blank line allowed; with an
  optional `until=`)
  marks the whole section as entering canon at that point. GitHub,
  Obsidian, and VS Code hide HTML comments, so headings render cleanly and
  heading links keep working. Other HTML comments are left alone. This is a
  structured version of "Book 3, chapter 23 additions" sections in a
  hand-kept character file. **Decided (#71):** a later section with the
  same standard heading (a new `## Appearance` for a new era) takes over
  from the earlier one at its `from`, the way a new field entry does, so
  the earlier one needs no `until`. Custom sections don't hand over, so
  cumulative ones can repeat. **Decided (#68):** a thread's `## Payoff`
  enters canon at the thread's `paid_off` unless it says otherwise.
- **Links** are wiki-style: `[[mara-voss]]`, `[[Mara Voss]]`, or an alias.
  They resolve by slug, name, or alias to an `id` within the same universe
  (§2), and the index stores the id (or the path, for a file that has
  no id yet). Ambiguous links are flagged.
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
  option), `draft`, `canon`, and `retconned`. An entity or outline row
  without a status is `draft`. Canon views leave out
  non-canon sections and values, and a section can't be more canon than its
  entity.
- **Pinned facts are hard canon.** `pin=true` on a section or `pin: true`
  on a field entry marks the facts most important to check before drafting.
  A **pinned canon** view lists them as of a position, for the whole
  universe or filtered to a series or book.
- Unknown frontmatter keys and sections are kept intact. The app must never
  discard what a human or an agent wrote.

## 4. Types and packs

The normative rules for packs, types, field types, and templates are in
[`types.md`](types.md), and the core pack's files are in
[`packs/core/`](../packs/core/).

A pack is a folder with a `pack.yaml` manifest, a YAML file per type in
`types/`, and markdown templates in `templates/`. A type is a YAML file
named by its key:

```yaml
# packs/core/types/character.yaml, shortened
name: Character
plural: characters                    # its folder under entities/
fields:
  role:     { type: text, temporal: true }
  born:     date                      # in-world date: free text plus a sort key
  species:  text                      # plain text, or a link to a species entity
  factions: { type: ref, to: faction, list: true, temporal: true }
sections:                             # standard sections, in section order
  - Overview
  - Appearance
  - Want
template: character.md
```

Field types: `string`, `text`, `number`, `bool`, `date`, `enum`, `ref` (a
link, optionally to given types), and `position`, each optionally a `list`.
`temporal: true` marks values that change over time, and `required` and
`default` are available. Every type also has the `row` and `placeholder`
fields.

**Planned packs:**

- **core** (always on): character, location, faction, item, event, thread
  (a plot thread with planted and paid-off positions), idea, note
  (free-form).
- **space-opera**: star-system, planet, settlement, species, polity,
  ship-class, ship, technology.
- **epic-fantasy**: realm, magic-system, creature, deity, artifact, order.

Dens can define their own types in `packs/`, or extend any enabled type
by adding fields, sections, or a template (`extensions/`). Extensions
add; they don't change what another pack defines.

**Decided (#6): the den wins.** When a den-local definition clashes with
a built-in one (a pack key, a type, a `plural`, an extension's field or
section, or a chapter field), the den-local one is used and the built-in
one is ignored, with a warning that names the clash. A den-local pack
named like a built-in pack shadows it. So an upgrade never overrides a
den's own definitions. Built-in packs change only by adding: changing an
existing built-in field's type, renaming a section, or adding or changing
a default is a breaking change, while template prompts and headings may
change in any release, since they only affect new entities. A writer who
relies on a custom field should define it in a den-local pack (types.md
§2.4). Versioning policy is #56. Clashes between two den-local packs are
settled by pack order (see types.md F-PACK-11).

**Decided (#6): your writing is yours.** Wolfden's licenses place no
conditions on the notes, manuscripts, or other content writers create with
it. The built-in packs in `packs/` are licensed MIT-0 (no attribution
required), so headings and prompts copied from a template carry no
conditions either.

## 5. Structure and time

The normative rules for books, chapters, positions, and outlines are in
[`format.md`](format.md) §6, §8, and §11.

### Books and chapters

`book.md` frontmatter holds the ordered chapter list. Each chapter has a
stable key, a number, an optional title, and optional fields from packs:
core defines POV, narration (first person, third limited, and so on),
tense, and an in-world date. A universe, series, or book can set
`chapter_defaults` once (say, third person past tense for a whole series),
and a chapter's own fields override them. Story positions refer to chapter
keys, so renumbering chapters doesn't break positions. Book and chapter keys
can't contain `:` or `.`, which the position syntax uses as separators.

### Outline rows

An outline is a list of **rows** (beats) with stable IDs that keep counting
across books in a series. Book 1 might end at row 36, so Book 2 starts at row
37. That way a reference like "row 69a" is never ambiguous. Rows can be
split (`69a`, `69b`) without renumbering anything. **Decided (#2):** an
outline is a set of GFM tables with `row` and `beat` columns, plus
`chapter`, `pov`, `status`, and any custom columns. Rows stay one line, and
details live in an entity linked from the row.

Positions stay `book:chapter`: rows aren't positions in v0. An event can
point at rows with a plain `row:` field, and `ember:r75` is reserved for
row positions later, the same way `ember:14.2` is reserved for scenes.

### Timeline

Timeline entries are `event` entities with two independent axes:

- **In-world time:** when it happens in the story world. Free text plus a
  sortable key in v0 (the `when` field, a `date`, types.md). A date such
  as `2291-05-14 06:40`, with an optional time of day, sorts by itself.
  Custom calendars (fantasy months, stardates) come later (#37).
- **Story position:** where the reader learns of it. Flashbacks and reveals
  make these differ, and the difference is often the interesting part.

### Freezing a finished book

When a book is done, **freezing** it marks it `status: final` (format.md
F-BOOK-1). That is the freeze: after it, edits to that book's scope are
refused unless explicitly unfrozen. When the den has git history (§6),
freezing also adds a tag (`book/ember/final`) as an extra snapshot. This
replaces hand-made `_FINAL` copies.

## 6. History and versioning

Wolfden doesn't keep its own audit trail. It keeps the format apart from
where a den is stored and how its history is kept:

- **Format:** a den is plain files in a folder (format.md). Nothing in the
  format depends on where the folder is stored or how its history is kept.
- **History:** a capability that a backend provides. Git is the v0 backend
  and the recommended default. Per-entity history, diffs, checkpoints, and
  freeze tags all come from it.
- **No backend:** a den without one still loads, validates, and indexes.
  History features say that history isn't available, instead of erroring.

The den store and the history provider are interfaces in `Wolfden.Core`
(#121; v0 behaviour, not a recorded decision):

- **Den store.** `IDenSource` is the read half, and everything that only
  reads a den takes it. `IDenStore` adds the write half: write a file
  (create, replace, or either), delete a file, move or rename a file or
  folder, and delete an empty folder. Paths are den-relative with `/`.
  Every write checks its paths first (`DenPaths`): nothing absolute, no
  `..`, nothing hidden (so never `.git/` or `.wolfden/`), never through a
  folder link, and only names that are the same file on every OS (no
  `\`, `:`, or other characters Windows doesn't allow, no trailing space
  or dot, no device names like `CON`). A name that differs only in case
  from one that's there is refused too, except in a rename of that very
  file or folder. Refusals are a `DenStoreException` with a reason code
  for the API and MCP tools to map. A store that can't be written says so
  (`IsReadOnly`) and refuses every write: the built-in packs are one.
  `IDenChangeFeed` is the watch half: it reports changes with den-relative
  paths, lost events, and a broken watch, and the index's watcher listens
  through it.
- **On disk,** `FileSystemDenStore` is all three. A write goes to a hidden
  temporary file in the same folder, then replaces the file in one move,
  so a reader or a crash sees the old file or the new one, and the bytes
  are exactly those given. The temporary file is hidden, so the den and
  the watcher ignore it (F-LAY-2).
- **History provider.** `IHistoryProvider` gives log, diff, checkpoint,
  and tag (and lists tags), scoped to the den's paths, with
  `HistoryCapabilities` flags for what the backend can do. An operation
  the backend can't do returns a result that says why, for the writer,
  instead of throwing; a backend that fails while doing one throws a
  `HistoryException`. `NoHistory` is the provider of a den without a
  backend. Freezing adds its tag only when the provider has the `Tag`
  capability. `Wolfden.Git` implements the interface (#19).

**A den inside a larger repository.** The den is the folder that holds
`den.yaml`, and it can be a subfolder of a larger git repository. History
is then scoped to the den's paths: log and diff cover only the den's
files, and checkpoints commit only them.

**Git or a synced folder, not both.** A git repository inside a synced
folder (Dropbox, OneDrive, iCloud) can be corrupted when two machines
sync while git is writing. Use git with a private remote, or a synced
folder without git, but not both.

With git as the backend:

- **Per-entity history** is `git log` on the entity's file, with diffs, shown
  in the UI and exposed through MCP.
- **Saving vs. committing:** edits are written to the working tree straight
  away. **Checkpoints** are commits with a message, made by the user ("end of
  Ch 14 session") or automatically after a configurable idle period. Commits
  made by AI agents through MCP record the agent as `Co-Authored-By`.
- **Remotes:** optional. Wolfden can push and pull a configured remote but
  never requires one.

With or without a backend, **the index is never the only copy of
anything.** Deleting it loses nothing (§7).

## 7. Index, queries, and continuity checks

A SQLite database (`.wolfden/index.db`, gitignored inside the den) built
from the files:

- Tables for entities, field values (with from/until positions), sections
  (with positions), links, outline rows, and positions, plus FTS5 for
  full-text search.
- **Rebuilt** fully on demand and incrementally through a file watcher, so
  edits made outside the app show up within moments.
- **Self-healing.** A full rebuild writes a new file and swaps it in, so a
  crash never leaves a half-built index. An index that's missing,
  unreadable (say, after a sync conflict), or from another schema version
  is rebuilt, not repaired. Wolfden puts a `.gitignore` inside `.wolfden/`
  so git skips the folder without changes to the den's own `.gitignore`.
- **Folder links are skipped** (#102; v0 behaviour, not a recorded
  decision). Every walk of a den (the loader, the files hash, the watcher,
  and `init`'s search for dens below) skips symbolic links and junctions
  to folders, as git does, so links can't loop and each note has one
  path. `check` warns about each link where the den reads files
  (format.md F-LAY-2), so linked notes aren't dropped silently.
  Cloud-sync placeholders (OneDrive, Dropbox) aren't links and are read
  as usual.
- **A file that can't be read is absent** (#112; v0 behaviour, not a
  recorded decision). A link to a file that's gone or that loops, a file
  deleted between listing and reading, one another program holds open,
  or one the user may not read is left out of `check`, the files hash,
  and the index, as if it weren't there, rather than stopping the
  command. Nothing warns about it yet. A file the index left out isn't in
  its files hash, so once the file can be read the index is stale and is
  rebuilt. A folder that can't be listed (the user may not list it, or
  it's deleted while it's read) is absent in the same way (#116): it isn't
  even a folder to the den, so a book folder that can't be listed isn't
  reported as one without its `book.md`.
- **Watched** (#14; v0 behaviour, not a recorded decision). The file
  watcher collects a burst of saves (an editor's temporary file and
  rename, a git checkout) into one batch, and ignores hidden paths such as
  `.wolfden/` and `.git/` (format.md F-LAY-2). For a batch of changed
  entity files and outlines it reads only those files again, then redoes
  the steps that span files: duplicate ids and resolving every link. A
  change to `den.yaml`, a pack, or a universe, series, or book file, a
  folder added, moved, or deleted where it can change the structure, lost
  events, or an index someone else rebuilt meanwhile gets a full rebuild
  instead. Files the index doesn't read, such as a `README.md` at the den
  root, change nothing, not even the files hash (#103). Each
  batch is written in one transaction, so a query sees the index before
  the batch or after it. After it commits, the watcher checks that the
  index file it wrote is still the one in place, since on Linux another
  process's rebuild can replace it mid-batch (#106), and rebuilds if it
  isn't. A window remains, as known v0 behaviour: a rebuild in another
  process that read the files before a save, but swaps its index in
  after the watcher's check, leaves an index without that save. It
  heals itself: the watcher's next batch finds the index isn't the one it
  wrote and rebuilds, and a `query` meanwhile sees that the files hash
  differs and rebuilds too. Closing the window would need a lock shared
  across processes for the whole rebuild. The index always ends up as a full rebuild of the
  same files would make it, apart from the ids of rows that came from
  single files, which queries never order by.

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
- Duplicate IDs (for example, from copying files by hand).
- **Spoiler checks**, off by default: a link to an entity the reader
  hasn't met yet at the link's position, such as a reference from an
  earlier book to an entity that first appears in a later one.

**Decided (#68): spoiler checks are opt-in.** The positions on facts record
where the reader learns each one, so a writer can pace the reveals. Not
every writer tracks that, so the check that helps them is off unless
`den.yaml` sets `spoiler_checks: true` (format.md F-DEN-7). It sees links,
not prose, so summary prose that runs ahead of the story is the writer's
to place (format.md §9.5).

Later rules: a character appearing after their death, travel times that
don't fit the timeline, and open threads at a frozen book's end.

### The command line (v0)

**v0 behaviour, not a recorded decision** (#17). The CLI has six
commands, and `wolfden <command> --help` describes each one:

| Command | What it does |
|---|---|
| `wolfden init [<folder>] [--name <name>] [--universe <key>]` | Creates a den: a `den.yaml`, and with `--universe` a first universe with an id. It never overwrites anything, and refuses a folder inside another den or one that holds a den up to four folder levels below it (format.md F-LAY-1; the downward search skips hidden folders and folder links, and stops after 2,000 folders). The new den passes `check` with no warnings. |
| `wolfden check [<den>] [--universe <key>] [--fix]` | The continuity lint above. It reads the files directly, not the index. It checks the whole den, every universe included, since it validates files rather than a story view; `--universe` checks only that universe's files and the den-level ones (see "Which universe" below), and `*` means the whole den. Without `--fix` it never writes. `--fix` first gives every container and entity file that loads without an `id` a new one (format.md F-ID-2, F-ID-6), skipping files in a final book (F-BOOK-1), and then checks the den as it is after the fix. |
| `wolfden index [<den>] [--watch]` | Rebuilds `.wolfden/index.db` from the files and says what it holds. `--watch` then keeps it up to date as files change (#14), printing one line for each batch of changes it applies (with `--json`, one JSON object per line), until Ctrl+C. Only one watcher runs per den; a second exits 2. |
| `wolfden query [<universe>] [--universe <key>] [--at <position>]` | Canon as of a position (format.md F-VIEW-1), from the index, of one universe (see "Which universe" below; `*` is refused, since a view is of one universe). `--series` or `--book` narrows the view, `--type` (any type an enabled pack defines) and `--entity` (matched as a link is, F-LINK-6, among the entities in the view, so one the reader meets later can't shadow one they've met) pick entities out of it, `--pinned` gives pinned canon (F-VIEW-2), and `--brief` lists one line per entity. |
| `wolfden new-id [<count>]` | Prints new ids for files made by hand (format.md F-ID-5). |
| `wolfden mcp [<den>] [--universe <key>] [--no-watch]` | Runs the MCP server over stdio for an AI client (§8, #20) until the client disconnects or Ctrl+C. `--universe` pins the session to one universe (§8); a key the den doesn't have exits 2. Standard output carries only the protocol; log messages go to standard error. It exits 2 if there's no den and 3 if it stops because of a failure after it started, but serves a den whose `den.yaml` doesn't load, so the client can call `check` to find out why. |

They share one surface:

- **Which den.** `--den <folder>` names it (`check` and `index` also take
  it as a plain argument). Otherwise it's the current folder, or the
  nearest folder above it that holds a `den.yaml`, as git finds its
  repository.
- **Which universe** (maintainer decision, 2026-10-23). A den with one
  universe uses it. In a den with several, a command that reads a story
  view (`query`, and the MCP tools, §8) needs one: named with
  `--universe` (or `query`'s plain argument), or settled by a position
  whose book key only one universe has (a book belongs to one universe,
  format.md F-POS-5). A key in more than one universe, or no universe
  and no position, is an error that lists the universes and nothing
  else. `*` (every universe) is accepted only where a search or list
  makes sense, and never with a position. `check` is the exception: it
  validates files, not a story view, so it checks the whole den unless
  `--universe` names one (`*` is the whole den too). Then it reads that
  universe's files and the den-level ones every universe shares
  (`den.yaml`, packs, authors, and the den's layout), and of the other
  universes only their container files, so positions resolve as in the
  whole den; their notes and outlines aren't read, and their findings
  are left out. Ids are still compared across universes (maintainer
  decision, 2026-10-23): the ids the other universes' notes use come
  from the index, in one query, after the index is brought up to date
  (hashing the whole den, which costs more than the check of one
  universe, but a stale index could miss a duplicate). An id shared with
  a note in another universe is a duplicate in the scoped check as in
  the whole den: the same F-ID-3 warning, naming the other copy, and the
  same broken links. If the index can't be built, say because
  `den.yaml` doesn't load, the rest of the check runs and a note says
  the id comparison was skipped. The one remaining difference from a
  whole-den check: a broken link that only another universe's note
  matches is reported without the hint that names that note. `--fix`
  works on the whole den, so it doesn't go with `--universe`.
- **Output.** Text by default. `check` prints one line per diagnostic,
  `path:line: severity rule: message`, with paths relative to the den
  root, then a summary. `query` prints each entity as a heading, its
  fields as a list, then its lead and sections, each marked with any
  `from`, `until`, `pinned`, or `draft`. `--json` prints one JSON
  document instead, with a `version` for its shape. Within a `version`,
  fields may be added, but renaming or removing a field, or changing what
  one means, needs a new `version`. Results go to standard output, and
  mistakes and failures to standard error.
- **Exit codes.** 0 when the command did its job; for `check`, when the
  den has no errors (warnings alone pass, format.md F-GEN-1); for `index
  --watch`, when it's stopped with Ctrl+C; for `mcp`, when the client
  disconnects or it's stopped with Ctrl+C. 1 only from
  `check`, when the den has at least one error. 2 when the command
  couldn't do its job: a wrong command line, no den, a `den.yaml` that
  doesn't load, a universe, position, filter, type, or entity that doesn't
  exist (for `query`, a den with no universes yet, or with several and
  none named), or an unexpected failure. A `query` whose view is empty,
  such as one for a character the reader hasn't met yet, exits 0. 3 only
  from `mcp`, when the server started but then stopped because of a
  failure.
- **A fresh index.** `query` reads the index, but first compares a hash
  of the files the index reads (`den.yaml`, packs, container files,
  entity files, and outlines) with the one recorded when the index was
  built, and rebuilds it if they differ, so an answer never
  lags behind an edit. `index --watch` keeps both the index and that hash
  up to date as files change, so while it runs a query rarely needs to
  rebuild: when the hash differs and a watcher is running, `query` first
  waits up to about a second for it to catch up (#108). The long-running
  `mcp` hosts the same watcher (unless `--no-watch`, or another Wolfden
  is already watching the den), and checks the hash the same way before
  each tool call; `serve` will do the same.
  `--watch` keeps going when `den.yaml` stops loading, so the writer can
  fix it, and reports a batch that fails on standard error, then rebuilds.

## 8. AI integration (MCP)

Wolfden ships an MCP server, using the official C# MCP SDK, over the same core
library the API uses. `wolfden mcp` runs it over stdio (§7). Tool surface:

| Tool | Status | Purpose |
|---|---|---|
| `search` | shipped (#20) | Full-text search across the den, or one universe or type |
| `get_entity` | shipped (#20) | One entity, optionally `as_of` a position |
| `list_entities` | shipped (#20) | By universe, type, status, series, or book |
| `canon_as_of` | shipped (#20) | Canon at a position, with an optional series or book filter |
| `check` | shipped (#20) | Run continuity checks and return findings |
| `get_history` | planned (#19) | Commit history and diffs for an entity |
| `create_entity` | planned (#21) | From a type template |
| `update_fields` / `append_section` | planned (#21) | Structured edits that keep everything else intact |
| `add_idea` | planned (#21) | Record undecided material with `status: idea` |
| `checkpoint` | planned (#21) | Commit with a message |

Writes go through the same validation as the UI. An agent can't save a file
the parser would reject.

`canon_as_of` and `get_entity` serve **pinned canon** first
([`format.md`](format.md) §9.4), so an agent sees the hard facts before
the rest.

**The read tools (#20; v0 behaviour, not a recorded decision).** They
reuse the index's queries and `check`, so they answer as `wolfden query`
and `wolfden check` do:

- **Positions are opt-in.** Without one, `get_entity`, `list_entities`,
  and `search` give the author's full view: every entity and fact,
  whatever its status, each marked with its status, pin, and `from` and
  `until`. `canon_as_of` without a position is the end of the story, as
  in `query`. With a position, `canon_as_of` and `get_entity` give the
  canon-as-of view (format.md F-VIEW-1), so nothing the reader meets later
  is in the result. `get_entity` then matches the
  name, every tier of it (F-LINK-6), among the entities the reader has met
  by then, so an entity met later can't shadow one met already, tie with
  it, or show up in an `ambiguous_entity` error. Only when nothing there
  matches does it ask all the entities, to tell one the reader hasn't met
  (`in_view: false`, and nothing about it) from one that doesn't exist
  (`unknown_entity`). Aliases aren't positional (format.md §9.5).
- **Pinned first.** Results of `canon_as_of` and `get_entity` start with
  `pinned`, the pinned canon view (F-VIEW-2), and list the same facts in
  place too, so each entity is complete.
- **Naming things.** `get_entity` takes a name, alias, file name, or id,
  matched as a link is (F-LINK-6), or a key or path. A series or book
  filter means what it means in a canon view (F-VIEW-1), for
  `list_entities` too.
- **One universe per call** (maintainer decision, 2026-10-23; §7, "Which
  universe"). Every tool but `check` reads one universe: the den's only
  one, the one named in `universe`, or the one a position's book belongs
  to; otherwise `universe_required`, which lists the universe keys and
  never entities. So an ambiguity never spans universes. `universe: "*"`
  reads every universe, in `search` and `list_entities` only, never with
  a position or a series or book filter; elsewhere it's `bad_argument`.
  `check` checks the whole den unless given a universe, and `"*"` means
  the whole den; a scoped check compares ids across universes through
  the index, as in §7, and its `note` says when it couldn't.
  `wolfden mcp --universe <key>` pins the session (`*` can't be pinned): every
  call reads that universe (`check` checks it and the den-level files),
  a call that names another universe or `"*"` gets `bad_argument`, and
  the server instructions say so.
- **Results** are JSON with snake_case names, like the tools' arguments,
  sent both as text and as structured content, with a `version` for
  their shape (the same rule as `--json`). Errors are error results whose
  JSON holds a stable `code` (such as `bad_position`, `unknown_entity`,
  `ambiguous_entity`, `den_not_loaded`) and a message that says what to
  do next. Arguments that don't bind to a tool's parameters (a required
  one missing, or a value of the wrong type) are reported by the SDK in
  its own words; the server wraps that as `bad_argument` in the same
  shape. In every result, `count` is how many items it holds, `truncated`
  says whether `limit` cut the list short, and `list_entities` and
  `check` also give the `total` that matched. `canon_as_of` is never cut
  short.
- **Fresh answers.** Each call checks the files hash first and rebuilds
  the index if it differs, waiting briefly for the watcher the server
  hosts (§7). `check` reads the files, not the index.

## 9. Architecture

```text
src/
  Wolfden.Core/     # model, markdown and frontmatter parsing, positions, canon-as-of logic
  Wolfden.Index/    # EF Core + SQLite index, file watcher, FTS
  Wolfden.Git/      # git history provider: log, diffs, checkpoints, tags (git CLI, #19)
  Wolfden.Api/      # ASP.NET Core minimal API (.NET 10)
  Wolfden.Mcp/      # MCP server
  Wolfden.Cli/      # `wolfden init | check | index | query | new-id | serve | mcp`
web/                # React + TypeScript + Vite + TanStack Query
samples/            # sample dens (CC-BY-4.0)
docs/
```

- **Storage and history are separate.** The core reads and writes a den
  through the den store (`IDenSource`, `IDenStore`) and asks a history
  provider for log, diffs, checkpoints, and tags (§6). `Wolfden.Git` is the v0 history
  provider, so a different store or provider can replace either later.
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
   `pin` marks hard canon, served first as **pinned canon**. Positions stay
   `book:chapter`, and `ember:r75` is reserved for outline-row positions,
   which v0 rejects. A bare `ember` means the start of
   the book. A section headed like a change log or session notes
   (built-in patterns plus any in `den.yaml`), or one marked `canon=false`, is
   bookkeeping and stays out of canon views. There's no separate "locked"
   state in v0: writers use `status: canon` plus `pin`. If agents need write
   protection, it belongs to the MCP write tools (M2), not the format.
