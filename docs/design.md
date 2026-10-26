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
freezing also adds a tag as an extra snapshot. This replaces hand-made
`_FINAL` copies. **Decided (#31):** the tag is
`book/<universe>/<book>/final` (`book/vael/ember/final`), since book keys
are unique only within a universe (format.md F-KEY-5). Tags belong to the
whole repository, so if the tag is already there (from an earlier freeze,
or another den in the same repository), freezing says so and leaves it
where it is: Wolfden never moves or replaces a tag.

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
  or dot, no device names like `CON`). A name being made mustn't be
  disguised by invisible characters (a zero-width space, a byte order
  mark, a direction control, or one at either end), though emoji and
  the joiners some scripts need are fine; existing names aren't checked
  for these. A name that differs only in case or Unicode form from one
  that's there is refused too, except in a rename of that very file or
  folder. Refusals
  are a `DenStoreException` with a reason code for the API and MCP tools
  to map, including a read-only file and one another program has open.
  The check and the write aren't one step, so another program can change
  the den in between; the store still never writes outside the den, and
  never overwrites a file made in between. A store that can't be written says so
  (`IsReadOnly`) and refuses every write: the built-in packs are one.
  `IDenChangeFeed` is the watch half: it reports changes with den-relative
  paths, lost events, and a broken watch, and the index's watcher listens
  through it.
- **On disk,** `FileSystemDenStore` is all three. A write goes to a hidden
  temporary file in the same folder, then replaces the file in one move,
  so a reader or a crash sees the old file or the new one, and the bytes
  are exactly those given. The temporary file is hidden, so the den and
  the watcher ignore it (F-LAY-2). A store lives as long as its host, so
  each read looks at the disk afresh; a whole-den load reads through a
  `Snapshot()`, which keeps what it lists for that load (#119).
- **History provider.** `IHistoryProvider` gives log, diff, checkpoint,
  and tag (and lists tags), scoped to the den's paths, with
  `HistoryCapabilities` flags for what the backend can do. An operation
  the backend can't do returns a result that says why, for the writer,
  instead of throwing; a backend that fails while doing one throws a
  `HistoryException`. Hidden paths are never recorded or reported, and a
  checkpoint commits only den files, leaving anything else the user has
  staged as it was. Tags belong to the whole repository, not the den.
  The log pages with a limit and a `before` revision, a diff without a
  `from` starts at the latest checkpoint, and every operation can be
  cancelled, up to a step that can't be stopped part way without harm,
  such as git writing a commit. `NoHistory` is the provider of a den without a backend.
  Freezing adds its tag only when the provider has the `Tag` capability.
  `Wolfden.Git` implements the interface (#19).
- **The git provider** (#19; v0 behaviour, not a recorded decision,
  apart from the git CLI). **Decided (2026-10-22):** Wolfden runs the
  user's own `git` command, not a library, so their configuration
  applies: commit signing, hooks, credentials, and `safe.directory`. Git
  runs in the den's folder, with that folder as the pathspec and hidden
  paths excluded, so paths in and out are den-relative and nothing
  outside the den is listed, compared, or committed. A den outside any
  repository has no history (`NoHistory`); when git isn't installed, or
  refuses the repository, every history operation says so instead.
  A checkpoint is `git commit --only -- <den paths>`: new den files are
  first marked with `git add --intent-to-add`, which records no content,
  and whatever else the user staged stays staged. A checkpoint records
  each den file as it is on disk, so a den file the user staged in part
  (`git add -p`) goes in whole, and a den file the user stopped tracking
  with `git rm --cached` is tracked again. A checkpoint with nothing to
  record makes no commit. If git refuses the commit (a hook, signing, or
  a merge in progress), the checkpoint says so and takes its
  intent-to-add marks off again, so the user's index is as it was.
  Every git command has a time limit, since git can wait outside the
  terminal on a signing passphrase, a hook, or a credential helper
  (#143): a command that only reads is stopped after a minute, but one
  that writes the user's index or refs is never stopped once started,
  since that would leave `.git/index.lock` behind (#145). After two
  minutes Wolfden stops waiting, leaves git running, and says so; if git
  then ends without committing, the marks come off. Cancelling a
  checkpoint works up to its commit. Wolfden never deletes git's lock
  files. A diff against the files as they
  are now includes new files git doesn't track yet (not ignored ones),
  through a temporary index, so it never changes the user's index, and
  it writes nothing into the repository's objects (#142). Clean filters
  still run on the files compared, and Git LFS's stores what it cleans in
  `.git/lfs/objects`. In a sparse checkout, files marked skip-worktree
  (the ones it keeps off the disk, or any marked by hand) count as
  unchanged; without one, a file marked by hand is compared as it is on
  disk. Edits inside a submodule aren't the den's, and a submodule isn't
  a den file: log and diff leave out its additions, deletions, and
  commit moves (#158), and a checkpoint that changed only submodules is
  still listed in the log, with no files. A note that becomes a symbolic link, or the other
  way round, is modified, with git's patch of the link's target path; a
  note that becomes a submodule is deleted, and one that replaces a
  submodule is added. Git doesn't see what a link leads to, so a note
  replaced by a link to a folder, or by a broken one, is still modified,
  though the den skips the first and takes the second as absent. The
  log of a file follows its current path, not back through renames
  (`--follow` can't be paged). The log pages by position: each page
  walks the same log from HEAD with the same path filter and starts
  after the `before` entry, since a history with merges isn't a line
  and the cursor's ancestors alone would miss main-line commits. A
  `before` that isn't an entry of that log is refused. Revisions from
  callers are resolved to commit hashes first, so one can't be read as
  an option. "Not a repository" is told from "repository refused" by
  looking for a `.git` above the den, stopping at
  `GIT_CEILING_DIRECTORIES` as git does. Wolfden needs git 2.31 or
  later (`--diff-merges`). Tests run git with a configuration of their
  own, never the developer's.

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

**v0 behaviour, not a recorded decision** (#17). The CLI has seven
commands, and `wolfden <command> --help` describes each one:

| Command | What it does |
|---|---|
| `wolfden init [<folder>] [--name <name>] [--universe <key>] [--git]` | Creates a den: a `den.yaml`, and with `--universe` a first universe with an id. It never overwrites anything, and refuses a folder inside another den or one that holds a den up to four folder levels below it (format.md F-LAY-1; the downward search skips hidden folders and folder links, and stops after 2,000 folders). The new den passes `check` with no warnings. By default the den is a plain folder; `--git` also runs `git init` in it (maintainer decision, #57, #122), unless the folder is already inside a repository, which it then names and leaves as it is (§6, "A den inside a larger repository"). Without git installed, the den is still made, and `init` says the repository wasn't (exit 0); if git fails, the den is kept and `init` exits 2. |
| `wolfden check [<den>] [--universe <key>] [--no-index] [--fix]` | The continuity lint above. It reads the files directly, not the index. It checks the whole den, every universe included, since it validates files rather than a story view; `--universe` checks only that universe's files and the den-level ones (see "Which universe" below), and `*` means the whole den. Without `--fix` it never writes the den's own files; `--universe` writes only the index (derived data, `.wolfden/`), and `--no-index` keeps it from doing even that. `--fix` first gives every container and entity file that loads without an `id` a new one (format.md F-ID-2, F-ID-6), skipping files in a final book (F-BOOK-1), and then checks the den as it is after the fix. |
| `wolfden index [<den>] [--watch]` | Rebuilds `.wolfden/index.db` from the files and says what it holds. `--watch` then keeps it up to date as files change (#14), printing one line for each batch of changes it applies (with `--json`, one JSON object per line), until Ctrl+C. Only one watcher runs per den; a second exits 2. |
| `wolfden query [<universe>] [--universe <key>] [--at <position>]` | Canon as of a position (format.md F-VIEW-1), from the index, of one universe (see "Which universe" below; `*` is refused, since a view is of one universe). `--series` or `--book` narrows the view, `--type` (any type an enabled pack defines) and `--entity` (matched as a link is, F-LINK-6, among the entities in the view, so one the reader meets later can't shadow one they've met) pick entities out of it, `--pinned` gives pinned canon (F-VIEW-2), and `--brief` lists one line per entity. |
| `wolfden copy <entity> <new name> [--to <scope>] [--universe <key>] [--slug <slug>] [--allow-relinks]` | Copies an entity under a new name, with a new id, as the MCP tool `copy_entity` does (§8, #129): beside the original, or `--to` a universe (`<universe>`, or `<universe>/`) or one of its series or books (`<universe>/<key>`, or `<universe>/series:<key>` or `<universe>/book:<key>` when a series and a book share the key). In a den with one universe, a bare series or book key (or `series:<key>`, `book:<key>`) will do (as for `check`, #127), unless it's also the universe's key, which is refused with every spelling suggested. It writes the den's files only. Its messages name its options (`--allow-relinks`, `--slug`) and commands (`wolfden check`), not the tool's parameters: the tools take the words their advice uses as a setting, and names the writer gave come through as written. It exits 2 when the copy is refused. |
| `wolfden new-id [<count>]` | Prints new ids for files made by hand (format.md F-ID-5). |
| `wolfden mcp [<den>] [--universe <key>] [--no-watch]` | Runs the MCP server over stdio for an AI client (§8, #20) until the client disconnects or Ctrl+C. `--universe` pins the session to one universe (§8); a key the den doesn't have exits 2. Standard output carries only the protocol; log messages go to standard error. It exits 2 if there's no den and 3 if it stops because of a failure after it started, but serves a den whose `den.yaml` doesn't load, so the client can call `check` to find out why. |

They share one surface:

- **Which den.** `--den <folder>` names it (`check`, `index`, and `mcp`
  also take it as a plain argument; `init` takes the new den's folder as
  one, and has no `--den`). Otherwise it's the current folder, or the
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
  universe, but a stale index could miss a duplicate). The universe key
  is checked first, from `den.yaml` and the container files, so an
  unknown key is reported without that wait and leaves the index alone
  (#130). An id shared with
  a note in another universe is a duplicate in the scoped check as in
  the whole den: the same F-ID-3 warning, naming the other copy, and the
  same broken links. If the index can't be built, or `den.yaml` doesn't
  load (then no index is built), the rest of the check runs and a note
  says the id comparison was skipped. `--no-index` skips it on purpose,
  with the same note, for a check that writes nothing at all, not even
  the index. The one remaining difference from a
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
library the API uses. `wolfden mcp` runs it over stdio (§7); how to add
it to Claude Code, Claude Desktop, and other clients is in
[`mcp-clients.md`](mcp-clients.md). Tool surface:

| Tool | Status | Purpose |
|---|---|---|
| `search` | shipped (#20) | Full-text search across the den, or one universe or type |
| `get_entity` | shipped (#20) | One entity, optionally `as_of` a position |
| `list_entities` | shipped (#20) | By universe, type, status, series, or book |
| `canon_as_of` | shipped (#20) | Canon at a position, with an optional series or book filter |
| `check` | shipped (#20) | Run continuity checks and return findings |
| `get_history` | shipped (#19) | Commit history and diffs for an entity |
| `get_chapter` | shipped (#21) | A book's chapters with each chapter field's own, effective, and inherited value, and `book.md`'s hash |
| `create_entity` | shipped (#21) | From a type template |
| `update_fields` / `append_section` | shipped (#21) | Structured edits that keep everything else intact |
| `add_idea` | shipped (#21) | Record undecided material with `status: idea` |
| `rename_section` | shipped (#21) | Rename a section and rewrite the links to it, den-wide (format.md F-RT-5) |
| `rename_entity` / `change_alias` | shipped (#150) | Change an entity's name, or change or remove an alias, rewriting the links that used the old text den-wide, or keep the old text as an alias (F-RT-5) |
| `copy_entity` | shipped (#129) | Copy an entity under a new name, with a new id and file, beside it or into another scope or universe |
| `update_chapter_fields` | shipped (#21) | Set, add to, remove from, or clear a chapter's own fields |
| `get_chapter_defaults` | shipped (#151) | A universe's, series', or book's `chapter_defaults`, what its books would inherit from above, the books it reaches, and its file's hash |
| `update_chapter_defaults` | shipped (#151) | Set, add to, remove from, or clear a field of a universe's, series', or book's `chapter_defaults` |
| `checkpoint` | shipped (#21) | Commit with a message, naming the agent as co-author |

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
  `check` also give the `total` that matched. A `search` with a filter
  (`universe` in a den with several, `type`, or `canon_only`) filters a
  capped set of raw hits (1,000, or 20 times `limit` if that's more), so
  its `truncated` is also true when there were raw hits past that cap,
  which may hold more matches. `canon_as_of` is never cut short.
- **Fresh answers.** Each call checks the files hash first and rebuilds
  the index if it differs, waiting briefly for the watcher the server
  hosts (§7). `check` reads the files, not the index, except that a
  check of one universe takes the other universes' ids from the index
  (brought up to date first) to find ids shared across universes
  (F-ID-3, §7 "Which universe").
- **History** (#19). `get_history` names an entity as `get_entity`
  does, without a position, since history isn't a story view, and gives
  the checkpoints that changed its file, newest first, a page at a time:
  `limit`, then `before` set to the result's `next_before`. With `diff`
  it adds what changed in the file since the latest checkpoint, and with
  `diff_from`, since that checkpoint. A den without history isn't an
  error: `available` is false, and `reason` says why in the writer's
  words. A `before` or `diff_from` that isn't a checkpoint of that
  history is `unknown_checkpoint`; a git failure is `history_failed`.
  `wolfden mcp` looks for the den's repository on each call, so a den
  made a repository while it runs gets history.
- **Hashes.** `get_entity` gives the SHA-256 of the entity's file as
  the index read it (`hash`), `get_chapter` the hash of `book.md`, and
  `get_chapter_defaults` the hash of the container's file: the
  `expected_hash` the write tools take.
- **Serve first, watcher in the background.** The server answers the
  client before the watcher's first build of the index is done, so a
  slow build never delays the handshake. A client that waits too long
  for `server/discover` falls back to `initialize`, which the server
  refuses once it has answered the late probe (#135). If the watcher
  can't start because of an error that can clear, such as a call holding
  the index open on Windows while the first build replaces it, or SQLite
  finding the index busy, the server tries again a few times before
  going on without it. Other errors, such as `.wolfden` being a file,
  don't clear by themselves, so it goes on without a watcher at once
  (#137). The server doesn't pin
  an MCP protocol version: it serves both the 2026-07-28 revision and
  the `initialize` handshake, so older clients keep working.

**The write tools (#21; v0 behaviour, apart from the maintainer
decisions marked).** They reuse the document edit API and the den
store, so an edit changes only what it means to (format.md §12):

- **Checked as the den reads it.** Before anything is saved, each file
  is read as a load of the den would read it (`DenWriteCheck`): an edit
  that would leave an error is refused (`invalid_edit`, with the
  errors), so an agent can't save a file the parser would reject. The
  result lists the file's warnings, from the same checks as `check`
  for that file's own fields.
- **Stale files** (maintainer decision, 2026-10-23). Every edit of an
  existing file takes the hash it was based on, and is refused
  (`stale_file`) if the file has changed since; the file is compared
  again just before it's written. Each result gives the new hash, so
  edits can follow one another without reading again. Writes are made
  one at a time within the server. Between processes a small window
  remains, as with the den store (§6): another program can save the
  file after that last comparison and before the write replaces it.
  Closing it would need a lock every editor honours.
- **Frozen and pinned** (maintainer decision, 2026-10-22). Nothing in
  a final book's scope is written (`frozen`): its entities, `book.md`,
  or outline (format.md F-BOOK-1). Edits to pinned canon are made, and
  `pinned_canon` in the result lists each pinned fact touched. A
  series' or universe's `chapter_defaults` isn't in a book's scope, but
  reaches its chapters, so `update_chapter_defaults` refuses an edit
  there that would change the effective value (types.md F-CHAP-5) of
  any chapter of a final book, and makes one the final book doesn't
  see, because its chapters or its own defaults set the field (#151; v0
  behaviour, as `rename_section` refuses to break links in a final
  book). The check fails closed: books are matched by path, since two
  can share a key (F-KEY-5); links are compared by their text and by
  the entity they reach from the container that holds the value
  (types.md F-CHAP-4), so a different spelling of the same link also
  counts as a change; a `book.md` below that the den leaves out for
  errors stops the edit if it says `status: final` or its frontmatter
  doesn't parse, and a draft one is named in the `effects`; and the
  `book.md` and `series.md` files below are compared again just before
  the write, so a book frozen meanwhile is `stale_file`, with a message
  to call `get_chapter_defaults` again.
- **Files only; commits by `checkpoint`** (maintainer decision,
  2026-10-22). Writes go to the working tree. `checkpoint` records the
  den's files (or the paths given) as one commit through the history
  provider, with the agent's `co_author` as a `Co-Authored-By` line
  (§6). A den without history isn't an error: `available` is false.
  **Decided (2026-10-25):** by default a checkpoint records the whole
  den, so it can hold edits the writer made elsewhere, and its result
  tells them apart. The server remembers the bytes its write tools last
  saved to each file. `written_by_session` lists the recorded files it
  wrote that weren't recorded since (a write the writer already
  committed some other way, which the latest commit holds byte for
  byte, is forgotten), and `changed_outside_session` lists the recorded
  files that hold anything else: ones the session never wrote, and
  ones it wrote that were changed after, which are in both lists, so
  the writer's edits are always surfaced. A file a failed write put
  back as it was doesn't count as the session's. With `paths`, only the
  files named are recorded and listed. Paths compare as the file
  system does, ignoring case on Windows and macOS. The comparison with
  the latest commit is the history provider's `HoldsAtLatest` (for
  git, the blob at HEAD against `git hash-object --path` of the bytes).
- **Ids** (maintainer decision, 2026-10-23). `create_entity` gives a
  new id that no file in the den uses, checked through the index even
  in a session pinned to one universe. `update_fields` refuses an `id`
  edit (F-ID-4). An edited entity or container file without an id gets one
  in the same write (F-ID-6), unless the edit changes nothing, which
  writes nothing. Files a rename rewrites only for their links don't.
- **Effects, not just bytes** (maintainer decision, 2026-10-23). A
  result's `effects` say what the edit means where that depends on
  more than the file: a chapter left with `pov: []` "now has no POV",
  with what it would inherit from `chapter_defaults` without its own
  value; a field whose type default now shows; an entity whose status
  takes it out of canon views; a new standard section that hands over
  from, or overlaps, an earlier one. `update_chapter_fields`'s `clear`
  goes back to inheriting (format.md F-RT-6, types.md F-CHAP-5).
  `update_chapter_defaults` (maintainer decision, 2026-10-25, #151)
  works the same way on a universe's, series', or book's
  `chapter_defaults`: removing a list's last item leaves `[]`, and
  `clear` goes back to inheriting from the container above. Its
  `effects` give each book below in runs of chapters, such as "Book
  'ember': chapters 1–2b now have pov [[mara-voss]]; chapters prologue,
  3 keep their own", and name a nearer default that keeps chapters as
  they were.
- **Creating** follows types.md F-TPL-6: the path from the type's
  folder, a slug made from the name (`-2`, `-3` when it's taken; a
  slug asked for that's taken is `already_exists`), and frontmatter in
  template order with the caller's values in place of the template's.
  `update_fields` puts a new top-level key in template order too
  (F-TPL-7); `append_section` puts a new standard section in the
  type's section order and a custom one at the end (F-TPL-8).
- **Renames** (maintainer decision, 2026-10-23). `rename_section`
  rewrites every link that reaches the section by heading, in any file
  (F-RT-5), as one change: if any of those files is in a final book,
  nothing is renamed, and if a write fails part way, the files already
  written are put back, newest first, and the result names any that
  couldn't be, or that another program changed meanwhile (those are
  left as they are). The files are written one after another, so a
  reader, such as the watcher or an editor, can briefly see some
  rewritten and others not; the next batch the watcher reads settles it. A link written verbatim in a frontmatter value is
  spliced in place, so the value keeps its style; one the YAML escapes
  is written anew. A new name that would change where existing links
  go is refused: one that a later section has (its links would reach
  the renamed one first, F-LINK-9), or an earlier one has while links
  name the section. Files the den leaves out for errors can't be
  edited, so an entity or container file whose text looks like it
  links to the section is named in `effects` for the writer to fix by
  hand (outlines with errors aren't searched).
- **Entity renames** (maintainer decision, 2026-10-25, #150).
  `rename_entity` changes an entity's `name`, and `change_alias`
  changes or removes one of its aliases; `update_fields` refuses both
  (`unsupported_edit`) and only adds aliases. By default every link that
  reached the entity by the old text (by name for a name, by alias for
  an alias) is rewritten, in any file, with the same machinery and
  guarantees as `rename_section`: links in bodies, frontmatter values,
  `book.md`, and outline rows, spliced in place with their spacing,
  `#heading`, and label kept. A removed alias's links get the name.
  Links by id or file name don't depend on the text and are left
  alone, as is the file's name; a title heading that reads the old name
  follows. Instead, `keep_old_name` (or `keep_old_alias`) keeps the old
  text as an alias and changes no link, which suits an in-story name
  change. If a link to rewrite is in a final book, nothing changes and
  the error suggests keeping the old text. Before anything is written,
  every link in the den is resolved again as it would be after the
  change: a rewritten link that would no longer reach the entity (the
  new text is another entity's file name, say) refuses the change. So
  does one that would change the target of a link that reached a
  different entity (a new name that's another entity's alias takes its
  links, since a name matches before an alias), with or without
  keeping the old text (maintainer decision, 2026-10-26), and so does
  one that would send a link that reached the entity to another one (a
  kept alias loses to another entity's name): the error
  lists each such link, its file, and its old and new target, and
  `allow_relinks` makes the change anyway. For now `allow_relinks`
  covers links in final books too; that case is a separate check, so
  it can become an always-refuse. Any other link that would resolve
  differently (a kept alias another entity also has makes it
  ambiguous, F-LINK-7), and any relinked link allowed, is listed in
  `effects`. A link that was ambiguous or broken and now resolves
  reached no entity before, so it's only reported, never refused. A change that only alters case or spacing changes no link
  (F-LINK-4). Renaming an entity to one of its own aliases takes that
  alias out.
- **Copies** (maintainer decisions, 2026-10-23, #129). `copy_entity`
  (and `wolfden copy`) starts a new entity from an existing one, where
  `create_entity` starts from the type's template. The copy gets a new id
  that no file in the den uses, checked as `create_entity`'s is, the new
  `name`, a slug made from it (`-2`, `-3` when it's taken, or one asked
  for, `already_exists` if taken), and no `aliases`, since an alias
  usually belongs to the original (comment lines inside a block
  `aliases` list go with it, and `effects` count them). A title heading that reads the
  original's name takes the new one. Everything else is the original's,
  byte for byte: the type, fields, sections, status, and `from` and
  `until` values, and the links in it, which still reach what they
  reached. It goes beside the original, or into another universe,
  series, or book, at the original's folder path under `entities/`
  (its type's folder, and any folders below it). Nothing is copied into a final book (`frozen`); copying out
  of one is fine. The copy is written as a new file only, and checked as
  the den reads it. Before it's written, every link is resolved again
  with the copy there: one that reached an entity and would reach the
  copy instead (its name or file name, in a nearer scope, or a name
  beating an alias) or become ambiguous (a tie with the copy, F-LINK-7)
  refuses the copy unless `allow_relinks`, as for a rename; the error
  lists each one. A link that was broken or ambiguous and now reaches
  the copy reached nothing before, so it's only listed in `effects`. Copied into another universe, links in
  the copy can break (F-LINK-5, F-LINK-8): that's allowed, and `effects`
  lists each link in the copy that doesn't reach what it reaches in the
  original, and the positions in it that don't resolve there (F-POS-4).
  A copy in another scope without a `from` enters canon at that scope's
  start (F-ENT-6), and `effects` say so; a `from` outside the book it's
  filed in is said instead. `effects` also count the links in the copy that
  still reach the original, such as its own `[[Name#Section]]` links.
  Moving entities comes later (#89).

## 9. Architecture

```text
src/
  Wolfden.Core/     # model, markdown and frontmatter parsing, positions, canon-as-of logic
  Wolfden.Index/    # EF Core + SQLite index, file watcher, FTS
  Wolfden.Git/      # git history provider: log, diffs, checkpoints, tags (git CLI, #19)
  Wolfden.Api/      # ASP.NET Core minimal API (.NET 10)
  Wolfden.Mcp/      # MCP server
  Wolfden.Cli/      # `wolfden init | check | index | query | copy | new-id | serve | mcp`
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
