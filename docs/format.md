# Wolfden Format Specification

**Format version:** 1. **Status:** draft (#2), open to revision until M1
code depends on it.

This document is the normative definition of a den on disk: its folders,
`den.yaml`, the container files, entity files, story positions, section
comments, status and pins, wiki-links, and outlines. It's meant to
be precise enough to hand-write a valid den and to test a parser
against. The reasons behind the
format are in [`design.md`](design.md) and aren't repeated here. Type and
pack files are defined in the core pack spec (#6).

## Contents

1. [Conventions](#1-conventions)
2. [Files and encoding](#2-files-and-encoding)
3. [YAML and frontmatter](#3-yaml-and-frontmatter)
4. [Keys and ids](#4-keys-and-ids)
5. [The den](#5-the-den)
6. [Containers: universe, series, book](#6-containers-universe-series-book)
7. [Entities](#7-entities)
8. [Story positions and order](#8-story-positions-and-order)
9. [Sections](#9-sections)
10. [Links](#10-links)
11. [Outlines](#11-outlines)
12. [Preservation](#12-preservation)
13. [Appendix: a minimal den](#13-appendix-a-minimal-den)

## 1. Conventions

### 1.1 Requirement words

The key words MUST, MUST NOT, REQUIRED, SHOULD, SHOULD NOT, and MAY are to
be interpreted as described in BCP 14 ([RFC 2119], [RFC 8174]) when, and only
when, they appear in all capitals.

[RFC 2119]: https://www.rfc-editor.org/rfc/rfc2119
[RFC 8174]: https://www.rfc-editor.org/rfc/rfc8174

### 1.2 Rule ids

Every rule has an id of the form `F-<AREA>-<n>`, such as `F-POS-2`. Ids are
stable: a rule that is dropped keeps its number retired, and new rules get
new numbers. Tests, diagnostics, and issues cite rules by id.

### 1.3 Errors and warnings

A rule that a validator enforces ends with its severity.

- **[error]**: the file is invalid. Wolfden reports it and doesn't index it
  (F-GEN-2 says how far that reaches). The app and MCP tools MUST refuse to
  write a file that would have an error.
- **[warning]**: the file is valid and indexed. `wolfden check` reports the
  warning.

A rule with no severity binds whoever writes files (a person, the app, or
an agent) and has no diagnostic.

**F-GEN-1.** `wolfden check` exits non-zero when the den has any error.
Warnings alone don't make it fail.

**F-GEN-2.** An error in an entity file or `outline.md` excludes that file.
An error in `universe.md`, `series.md`, or `book.md` excludes that container
and everything under it, since scopes and positions there can't be
interpreted. An error in `den.yaml` means the den can't be loaded.

**F-GEN-3.** Validators MUST report every diagnostic they find, not just the
first. Each diagnostic SHOULD carry the rule id, the file path, and a line
number when there is one.

### 1.4 Notation and terms

Grammars use ABNF ([RFC 5234]), except that quoted strings are
case-sensitive. `lower` is `%x61-7A` (a to z). `DIGIT`, `WSP`, and `DQUOTE`
are the RFC 5234 core rules.

[RFC 5234]: https://www.rfc-editor.org/rfc/rfc5234

- **Den root:** the folder that holds `den.yaml`.
- **Container:** a universe, series, or book, each a folder with a
  container file (`universe.md`, `series.md`, `book.md`).
- **Scope:** the container an entity belongs to (design.md §2).
- **Scope chain:** a scope and the scopes that enclose it, nearest first.
  For a book in a series, that's book, series, universe.
- **Writer:** anything that writes den files: the app, the CLI, MCP tools.

## 2. Files and encoding

**F-ENC-1.** `den.yaml` and every `.md` file Wolfden reads MUST be UTF-8.
Invalid UTF-8 is an [error].

**F-ENC-2.** Readers MUST accept a leading byte order mark and keep it when
rewriting. Writers MUST NOT add one to a new file.

**F-ENC-3.** LF is the canonical line ending, and new files use it. Readers
MUST accept CRLF. A writer that rewrites a file MUST keep its line endings.
A file that mixes LF and CRLF is a [warning]. A CR not followed by LF is an
[error].

**F-ENC-4.** Files SHOULD end with a newline. Writers MUST keep a file's
final newline, or its absence.

**F-ENC-5.** Text is compared (names, aliases, headings, link targets) after
Unicode NFC normalization. Writers SHOULD write NFC, and MUST NOT
re-normalize text they aren't changing.

## 3. YAML and frontmatter

**F-YAML-1.** `den.yaml` and frontmatter are a single YAML 1.2 document
using the core schema. A parse failure is an [error].

**F-YAML-2.** The top level MUST be a mapping. [error]

**F-YAML-3.** A mapping MUST NOT repeat a key. [error]

**F-YAML-4.** Anchors (`&a`), aliases (`*a`), merge keys (`<<`), and
explicit tags (`!x`, `!!str`) MUST NOT be used. [error]

**F-YAML-5.** Keys, positions, `name`, `aliases`, chapter `title`, and
`row` values (F-FLD-7) are read as the scalar's text as written, not after
type resolution. So `14`, `"14"`, and `'14'` are the same chapter key,
`014` stays `014`, and `name: 1984` is the name "1984".

**F-FM-1.** Frontmatter starts on the file's first line (after any byte order
mark) with a line that is exactly `---`, and ends at the next line that is
exactly `---`. Trailing spaces or tabs on either line are allowed. `...`
doesn't close frontmatter. A container or entity file with no frontmatter is
an [error], and so is frontmatter that never closes.

**F-FM-2.** The **body** is everything after the closing `---` line. Where
this spec talks about headings, code, HTML comments, and list items, it
means them as [CommonMark 0.31.2] defines them. Wolfden doesn't restrict
any other markdown.

[CommonMark 0.31.2]: https://spec.commonmark.org/0.31.2/

## 4. Keys and ids

### 4.1 Keys

Universes, series, books, chapters, entity slugs, author identities, packs,
and types are named by **keys**.

```abnf
key        = word *("-" word)          ; 1 to 64 characters
word       = 1*(lower / DIGIT)
```

**F-KEY-1.** A key MUST match `key` and be 1 to 64 characters long:
lowercase ASCII letters and digits, with single hyphens between words.
No uppercase, spaces, underscores, `:`, or `.`. A key that breaks this rule
is an [error] where it names a container, chapter, author, pack, or type.
For entity slugs, see F-KEY-4.

**F-KEY-2.** Universe, series, and book keys MUST start with a letter.
[error] Chapter keys and entity slugs MAY start with a digit, so `14` is a
valid chapter key. A chapter key MUST NOT be `r` followed by a digit
(`r75`), since that form is reserved for outline-row positions (F-POS-6).
[error]

**F-KEY-3.** A universe, series, or book's key is its folder name.

**F-KEY-4.** An entity's **slug** is its file name without `.md`. A file
name that isn't a valid key (`Mara Voss.md`) is a [warning]: the entity is
indexed but has no slug, so links reach it only by id, name, or alias.

**F-KEY-5.** Book keys MUST be unique within a universe, across all of its
series and standalone books, since positions name a book without its
series. A series key MUST NOT equal a standalone book key in the same
universe, since `order` (F-UNI-1) mixes them. Chapter keys MUST be unique
within a book. Each is an [error], reported on every file involved.

| Key | Valid? |
|---|---|
| `kessan-reach`, `long-dark`, `14`, `2b`, `ch-07` | yes |
| `Kessan-Reach`, `kessan_reach`, `kessan reach` | no: case, `_`, space |
| `-ember`, `ember-`, `ember--ash` | no: hyphen placement |
| `ember.1`, `ember:1` | no: `.` and `:` are position separators |
| `7th-fleet` as a book key | no: books start with a letter |
| `r75` as a chapter key | no: reserved for row positions |
| `rescue`, `r-2` as chapter keys | yes |

### 4.2 Ids

```abnf
ulid       = %x30-37 25crockford       ; first character 0 to 7
crockford  = DIGIT / %x41-48 / %x4A-4B / %x4D-4E / %x50-54 / %x56-5A
                                       ; A-H J K M N P-T V-Z
```

**F-ID-1.** An `id` MUST be a [ULID] in canonical form: 26 characters of
uppercase Crockford base32. Lowercase, hyphens, or any other length is an
[error]. Readers MUST NOT give the timestamp part any meaning.

[ULID]: https://github.com/ulid/spec

**F-ID-2.** Every container file and entity file MUST have an `id`. [error]

**F-ID-3.** An `id` MUST be unique within the den. A duplicate is an
[error] on every file that has it, and none of them is indexed until it's
fixed. (This is how a file copied by hand gets caught.)

**F-ID-4.** An `id` never changes. Writers MUST NOT change an existing
`id`. Moving or renaming a file keeps its `id`, and a copy needs a new one.

Valid: `01JA7Q8M4XK3V9R2D5W8HN6TQB`. Invalid: `01ja7q8m4xk3v9r2d5w8hn6tqb`
(lowercase), `01JA7Q8M4XK3V9` (too short), `01JA7Q8M4XK3V9R2D5W8HN6TQI`
(`I` isn't Crockford), `81JA7Q8M4XK3V9R2D5W8HN6TQB` (starts above 7).

## 5. The den

### 5.1 Layout

```text
<den root>/
  den.yaml                        required (§5.2)
  packs/<pack>/...                den-local packs, optional (#6)
  universes/<universe>/
    universe.md                   required (§6.2)
    entities/<type-folder>/.../<slug>.md
    series/<series>/
      series.md                   required (§6.3)
      entities/...
      books/<book>/
        book.md                   required (§6.4)
        outline.md                optional (§11)
        entities/...
    books/<book>/                 standalone book, same shape as above
  notes/                          reserved (#45)
  authors/                        reserved (#45)
  .wolfden/                       reserved: index and local state
```

**F-LAY-1.** The den root is the folder that holds `den.yaml`. Dens don't
nest: a `den.yaml` anywhere below the root is a [warning] and is ignored.

**F-LAY-2.** Files and folders whose names start with `.` are ignored at
any depth (`.git`, `.obsidian`, `.vscode`). `.wolfden/` at the root is
reserved for the index and local state, SHOULD be gitignored, and is never
a source of truth (design.md §7).

**F-LAY-3.** `notes/` and `authors/` at the den root are reserved for
Author and den notes (#45). If either exists, v0 ignores its contents and
reports a [warning].

**F-LAY-4.** Every folder directly under the den's `universes/`, or under
a container's `series/` or `books/`, is a container and MUST hold its
container file. A folder that doesn't is an [error], and it is skipped.

**F-LAY-5.** Every `.md` file under an `entities/` folder, at any depth, is
an entity file. An `entities/` folder counts only directly inside a
container folder. The entity's scope is that container.

**F-LAY-6.** The first folder under `entities/` SHOULD be the plural name of
the entity's type, as the type defines it (#6): characters go in
`entities/characters/`. The file's `type` decides its type, not the folder.
A file in another type's folder, or directly in `entities/`, is a
[warning]. Folders below the type folder are free for the writer to use
(`entities/characters/crew/mara-voss.md`) and mean nothing to Wolfden.

**F-LAY-7.** Other files and folders in a container folder (images, a
`drafts/` folder) are ignored, with one exception: a `.md` file directly in
a container folder, other than its container file and a book's
`outline.md`, is a [warning], since it's probably a note that belongs in
`entities/`. Non-`.md` files under `entities/` are ignored.

### 5.2 `den.yaml`

```yaml
format: 1
name: Starfall Notes
authors:
  wren-calder:
    name: Wren Calder
packs: [space-opera]
```

**F-DEN-1.** `format` is REQUIRED and is an integer. This spec is format
`1`. A missing or non-integer `format` is an [error]. A `format` higher
than the reader supports is an [error] that says so ("den format 2 is newer
than this version of Wolfden"). Additive changes, such as a new optional
key, don't change the format number.

**F-DEN-2.** `name` is an optional display name. It defaults to the den
root's folder name.

**F-DEN-3.** `authors` is an optional mapping from author key (F-KEY-1) to
an Author Identity. Each identity is a mapping with a REQUIRED non-empty
`name`. Any other shape is an [error]. Other keys in an identity are kept.

**F-DEN-4.** `packs` is an optional list of pack keys to enable. The `core`
pack is always enabled, and listing it is allowed. A pack that is neither
built in nor in `packs/<key>/` is an [error]. Pack contents and how types
from several packs combine are defined in the core pack spec (#6).

**F-DEN-5.** Other keys are kept and ignored, with no diagnostic.

## 6. Containers: universe, series, book

### 6.1 Common rules

**F-CTR-1.** A container file's frontmatter MUST have an `id` (F-ID) and a
non-empty string `name`. [error]

**F-CTR-2.** `authors` is an optional list of author keys from `den.yaml`.
A book without `authors` takes its series' list, and a series without one
takes its universe's. An unknown author key is a [warning].

**F-CTR-3.** The body is the container's overview prose. Its links resolve
from the container's own scope (§10), and its sections follow §9.

**F-CTR-4.** Other frontmatter keys are kept and ignored, with no
diagnostic (F-ENT-9 applies here too).

### 6.2 `universe.md`

```markdown
---
id: 01JAM9S346Q3D25VT4F5V37E3S
name: Starfall
authors: [wren-calder]
order: [long-dark, a-quiet-moon]
---

The Kessan Reach and the long war over it.
```

**F-UNI-1.** `order` is a list of the universe's series keys and standalone
book keys, in **story order**: the order in which a reader is meant to meet
the works. Positions are ordered by it (§8.2).

**F-UNI-2.** An `order` entry that names no series or standalone book of
this universe is a [warning] and is ignored. A repeated entry is an
[error].

**F-UNI-3.** `order` MAY be omitted when the universe has at most one series
or standalone book. Otherwise each series or standalone book missing from
`order` is a [warning], and the missing ones come after the listed ones, by
key in ASCII order.

### 6.3 `series.md`

```yaml
---
id: 01JA3E28JT97KB6CQ643DZVMXX
name: The Long Dark
books: [ember, ash]
---
```

**F-SER-1.** `books` lists the series' book keys in story order.

**F-SER-2.** An entry that names no book folder in this series is a
[warning] and is ignored. A repeated entry is an [error].

**F-SER-3.** `books` MAY be omitted when the series has at most one book.
Otherwise each book missing from `books` is a [warning], and the missing
ones come after the listed ones, by key in ASCII order.

### 6.4 `book.md`

```yaml
---
id: 01JAQKFBF5KZNWJ47TAN9ZT24M
name: Ember
status: draft
chapters:
  - { key: prologue, title: Before the Dark }
  - { key: "1", number: 1, title: Kessan Reach }
  - { key: "2", number: 2 }
  - { key: 2b, number: 3, title: The Long Watch }   # added in revision
  - { key: "3", number: 4 }
---
```

Chapter `2b` was inserted after `2`, so the chapters after it were
renumbered. Their keys didn't change, so positions such as `ember:3` still
point at the same chapter.

**F-BOOK-1.** `status` is optional: `draft` (the default) or `final`. Any
other value is an [error]. `final` marks a frozen book (design.md §5).
Writers MUST refuse edits within a final book's scope (its `book.md`,
`outline.md`, and entities) until it's unfrozen. The git tag made on
freezing (#31) isn't part of the format.

**F-BOOK-2.** `chapters` is an optional list. The list order is the chapter
order. A missing or empty list means the book has no chapters yet, and
positions in it don't resolve (F-POS-4).

**F-BOOK-3.** Each item is a mapping with:

- `key`, REQUIRED: the chapter key (F-KEY-1, F-KEY-2).
- `number`, optional: a non-negative integer used for display only. It
  never affects order or positions. A prologue or interlude can leave it
  out.
- `title`, optional: a string.

Other keys are kept. An item MAY instead be a plain scalar, short for
`{ key: <scalar> }`, so `chapters: [prologue, 1, 2, 3]` is valid. An item of
any other shape, or one with no `key`, is an [error].

**F-BOOK-4.** A repeated chapter key is an [error] (F-KEY-5).

**F-BOOK-5.** `number` values, where present, SHOULD increase strictly down
the list. A `number` that doesn't is a [warning].

**F-BOOK-6.** Renumbering changes `number` only. Writers MUST NOT change a
chapter key as a side effect of renumbering or reordering.

## 7. Entities

### 7.1 Frontmatter

```markdown
---
id: 01JA7Q8M4XK3V9R2D5W8HN6TQB
type: character
name: Mara Voss
aliases: [Voss, "the Commander"]
status: canon
fields:
  species: "[[velari]]"
  rank:
    - { value: Lieutenant, from: "ember:1" }
    - { value: Commander,  from: "ember:22" }
  born: "2291"
x-color: teal
---
```

**F-ENT-1.** `id` is REQUIRED (F-ID).

**F-ENT-2.** `type` is REQUIRED and names a type enabled in the den: from
`core`, an enabled pack, or `packs/` (#6). A missing or unknown type is an
[error] ("unknown type 'charcter'").

**F-ENT-3.** `name` is REQUIRED and is a non-empty string after trimming.
[error]

**F-ENT-4.** `aliases` is an optional list of non-empty strings. Any other
shape, including a single string, is an [error].

**F-ENT-5.** `status` is optional and defaults to `canon`. Any value other
than the four below is an [error]. Sections, field entries, and outline
rows have their own status, with two more values (§9.3).

| `status` | Meaning | In canon-as-of views |
|---|---|---|
| `idea` | Undecided material (an idea bank) | left out |
| `draft` | Meant as canon, not settled | included, marked as draft |
| `canon` | Settled | included |
| `retconned` | Was canon, has been replaced | left out, kept for history |

**F-ENT-6.** `from` is an optional position (§8) where the entity enters
canon, that is, where the reader first meets it. Without `from`, a
book-scope entity enters at the start of its book, and any other entity at
the start of the story (F-ORD-3). A canon-as-of view at position `p` leaves
out an entity whose `from` is after `p`. A book-scope entity whose `from`
is outside its own book is a [warning]. There is no entity-level `until`:
use `status` for material that leaves canon.

**F-ENT-7.** `fields` is an optional mapping from field name to value
(§7.2). Any other shape is an [error].

**F-ENT-8.** The body holds the entity's sections (§9).

**F-ENT-9.** Other top-level keys are kept and ignored, with no diagnostic.
Keys a writer invents SHOULD start with `x-`. Future format versions won't
define top-level keys with that prefix.

### 7.2 Field values

**F-FLD-1.** Field names MUST match `lower *(lower / DIGIT / "_")`, up to
64 characters (`species`, `see_also`). [error]

**F-FLD-2.** A field the entity's type doesn't define is a [warning]
(usually a typo). Its value is kept and indexed as written.

**F-FLD-3.** How a value must look for its field type (`string`, `ref`,
`enum`, ...) is defined with the field types in #6. A value that doesn't
match its field type is a [warning], and so is a missing required field.

**F-FLD-4.** A null value (`born:` with nothing after it) means the field is
unset, the same as leaving it out.

**F-FLD-5.** A value is **plain** (true at every position), a single
**entry**, or a **temporal** list of entries (§7.3). F-TMP-1 decides
which.

**F-FLD-6.** In YAML, `species: [[velari]]` without quotes is a list inside
a list, not a link. A value that is a one-item list whose only item is a
list of strings is a [warning] that suggests quoting it:
`species: "[[velari]]"`.

**F-FLD-7.** A field named `row`, on any type, ties an entity (usually an
`event`) to outline rows. Its value is a row id (F-OUT-2) or a list of row
ids. A `row` entry with `from` or `until` is a [warning]. A row id resolves
among the outlines of the entity's series, or of its standalone book. For a
universe-scope entity it resolves among every outline in the universe, and
a row id found in more than one series is ambiguous. A row id that doesn't
resolve, or is ambiguous, is a [warning]. `row` is not a position: v0
positions stay `book:chapter` (F-POS-6).

### 7.3 Entries and temporal values

```yaml
fields:
  born: { value: "2291", pin: true }
  rank:
    - { value: Lieutenant, from: "ember:1" }
    - { value: Commander, from: "ember:22", pin: true }
    - { value: Captain, from: "ash:30", status: candidate }
  posting:
    - { value: "[[tessaly]]", from: "ember:1", until: "ash:9" }
    - { value: "[[kessan-reach]]", from: "ash:12" }
```

`born` is a plain fact, pinned. As of `ember:21`, `rank` is Lieutenant.
From `ember:22` on, it's Commander, a pinned fact. Captain is a banked
option, so canon views leave it out and Commander doesn't end at `ash:30`.
`posting` is unset from `ash:9` up to `ash:12`.

**F-TMP-1.** An **entry** is a mapping with a `value` key. A field's value
is a single entry, a **temporal list** (a non-empty list in which every
item is an entry), or else a plain value. A list in which some items are
entries and others aren't is an [error]. A single entry means the same as a
one-item temporal list. The shape decides, not the type, so a parser can
tell these apart without loading packs.

**F-TMP-2.** An entry has:

- `value`, REQUIRED: any YAML value, including a list. A `value` that is
  itself an entry or a temporal list is an [error]. `value: null` means the
  field is unset from that entry's `from`.
- `from`, optional: a position (§8). Without it, the entry starts at the
  start of the story.
- `until`, optional: a position.
- `status`, optional: the entry's status (F-STAT-1). Without it, the entry
  takes its entity's status.
- `pin`, optional: `true` pins the entry (F-PIN-1).

Other keys in an entry are a [warning], and are kept.

**F-TMP-3.** An entry holds at positions `p` with `from <= p < until`.
`from` is inclusive and `until` is exclusive. An `until` that isn't after
its `from` is an [error].

**F-TMP-4.** An in-canon entry (F-STAT-3) without `until` holds until the
`from` of the next in-canon entry in story order, or to the end of the
story if there is none. So in the example above, Lieutenant and Commander
don't overlap. An out-of-canon entry without `until` runs to the end of
the story.

**F-TMP-5.** Two in-canon entries **overlap** if they share a `from` (two
entries without `from` included), or if an explicit `until` reaches past
the next in-canon entry's `from`. Overlap is a [warning]. Where entries
overlap, a canon-as-of view uses the one with the later `from`, and for
equal `from`s, the one later in the file. Out-of-canon entries never
overlap, so several `candidate` values can share a `from`.

**F-TMP-6.** An explicit `until` that comes before the next entry's `from`
leaves a gap in which the field is unset. Gaps are allowed.

**F-TMP-7.** The order of entries in the file doesn't change their meaning,
except for the tie in F-TMP-5. Writers SHOULD list entries in story order
and MUST NOT reorder them.

**F-TMP-8.** A temporal list, or an entry with `from` or `until`, on a
field the type defines as not temporal is a [warning]. It's still read as
temporal. A single entry with only `value`, `status`, or `pin` is fine on
any field.

## 8. Story positions and order

### 8.1 Position syntax

```abnf
position   = book-key ":" chapter-key
book-key   = key                       ; starts with a letter (F-KEY-2)
chapter-key = key
scene-form = book-key ":" chapter-key "." 1*DIGIT   ; reserved for scenes
row-form   = book-key ":" "r" row-id             ; reserved for rows
```

**F-POS-1.** A position is a string matching `position`, with no
whitespace. Positions appear in `from` and `until` values in frontmatter and
in section comments (§9.2). In YAML they SHOULD be quoted.

**F-POS-2.** A position in the scene form (`ember:14.2`) is an [error] with
the message "scenes are not supported yet; v0 positions are chapter-level".
It MUST NOT be read as chapter `14` or as a chapter key `14.2`. Any position
with a `.` after the `:` gets this error.

**F-POS-3.** Any other string that doesn't match `position` is an [error]
("invalid story position").

**F-POS-4.** A position resolves to a book of the universe the file belongs
to (any series or standalone) and a chapter in that book's `chapters`. A
book or chapter that doesn't exist is a [warning], not an error, so writers
can plan ahead. The fact that uses it (a temporal entry, a section, or an
entity's `from`) stays indexed but is left out of canon-as-of views, and
range checks (F-TMP-3) skip it.

**F-POS-5.** Positions don't cross universes (design.md §2). A book key
from another universe doesn't resolve, even if it exists there.

**F-POS-6.** A position whose chapter part is `r` followed by a digit
(`ember:r75`) is in the reserved row form. It is an [error] with the
message "outline-row positions are not supported yet; v0 positions are
chapter-level". This check comes before F-POS-2, so `ember:r75.1` gets this
error too. F-KEY-2 keeps chapter keys out of this form. To tie a fact to a
row in v0, use a `row` field (F-FLD-7).

| Position | Result |
|---|---|
| `ember:14`, `ember:prologue`, `a-quiet-moon:2b` | valid |
| `ember:14.2` | [error] F-POS-2, scenes not supported yet |
| `ember:r75`, `ember:r69a` | [error] F-POS-6, rows not supported yet |
| `ember`, `ember:`, `:14` | [error] F-POS-3, needs a book and a chapter |
| `Ember:14`, `ember: 14`, `ember:14:2` | [error] F-POS-3 |
| `ember:99`, where `ember` has no chapter `99` | [warning] F-POS-4 |

`from: ember: 14`, with a space after the second colon, isn't a position
at all: it's a YAML syntax error (F-YAML-1).

### 8.2 Order

**F-ORD-1.** The universe's books are put in **story order** by walking its
`order` (F-UNI-1, F-UNI-3) and putting each series' books in place, in the
order of its `books` (F-SER-1, F-SER-3). That gives one total order over
every book in the universe.

**F-ORD-2.** Position `a` is before position `b` if `a`'s book is earlier
in story order, or if both are in the same book and `a`'s chapter is
earlier in that book's `chapters` list.

**F-ORD-3.** Three more points complete the order. The **start of the
story** comes before every position. The **start of a book** comes after
every chapter of earlier books and before the book's first chapter. The
**end of the story** comes after every position. Positions can't name these
points. They are only what a missing `from` or `until` means.

**F-ORD-4.** Story order is reading order, not in-world chronology. When
something happens in-world is a field of `event` entities (#6).

With `order: [long-dark, a-quiet-moon]` and `books: [ember, ash]`, every
`ember` position is before every `ash` position, which is before every
`a-quiet-moon` position.

## 9. Sections

### 9.1 Structure

```markdown
# Mara Voss

Executive officer of the *Tessaly*.

## Appearance
Silver-streaked hair.

## Shoulder injury
<!-- wolfden: from=ember:19 -->
Takes a flechette round at [[kessan-reach]].

### Recovery
<!-- wolfden: from=ash:3 until=ash:20 -->
Months of physical therapy aboard the hospital ship.
```

**F-SEC-1.** A **section** is a heading of level 2 to 6 plus everything
after it up to the next heading of the same or a higher level (fewer `#`
signs). Level-2 sections are the entity's sections, and deeper ones are
subsections of the section that holds them. Both ATX (`##`) and setext
headings count. Text inside code blocks and HTML blocks is never a heading.

**F-SEC-2.** Content before the first level-2 heading is the **lead**. A
level-1 heading in the lead is the display title. It's optional and doesn't
have to match `name`. A level-1 heading after the first level-2 heading is
a [warning].

**F-SEC-3.** A section's **name** is its heading text, without the `#`
markers or closing `#`s, trimmed, with any inline markup kept as written.
Names match (against a type's standard sections, and in `[[target#Heading]]`
links) under the normalization in F-LINK-4. The `Custom:` prefix in
design.md's example has no special meaning. Any section that the type
doesn't define is a custom section, and it's kept like any other (§12).

**F-SEC-4.** Two sections MAY have the same name, for example one
`## Appearance` per era, each with its own position. Two sections with the
same name and level, under the same parent, that are both in canon and in
effect at some position are a [warning]. Out-of-canon sections don't count,
so `open` and `candidate` sections can share a name.

**F-SEC-5.** A required section (#6) that's missing is a [warning].

### 9.2 Section comments

```abnf
pos-line   = *WSP "<!--" *WSP "wolfden:" 1*(1*WSP attr) 1*WSP "-->" *WSP
attr       = attr-name "=" attr-value
attr-name  = lower *(lower / DIGIT / "-")
attr-value = 1*vchar / DQUOTE *qchar DQUOTE
vchar      = %x21 / %x23-7E            ; visible ASCII except " and space
qchar      = <any character except DQUOTE, CR, LF>
```

**F-SEC-6.** A **section comment** is a line matching `pos-line`, placed on
the line directly after a heading of level 2 to 6 (after the underline, for
a setext heading). There is no blank line between them. It applies to that
heading's section.

**F-SEC-7.** Attributes are space-separated `key=value` pairs, in any
order, and a value MAY be double-quoted. A section's comment takes:

- `from` and `until`: positions (§8).
- `status`: the section's status (F-STAT-1).
- `pin`: `true` or `false` (F-PIN-1).

A repeated attribute is an [error]. Any other attribute is a [warning], and
is kept as written.

```markdown
## Shoulder injury
<!-- wolfden: status=candidate from=ember:19 -->
```

**F-SEC-8.** A section with `from` or `until` is in effect at positions
`p` with `from <= p < until`, the same as F-TMP-3. A missing `from` means
from the entity's own `from` (F-ENT-6). A missing `until` means to the end
of the story: unlike temporal entries, sections never end on their own when
a later section starts. An `until` that isn't after its `from` is an
[error]. A section with neither is in effect wherever its
entity is.

**F-SEC-9.** A subsection is in effect only where both it and every section
that holds it are in effect. A subsection whose range reaches outside its
parent's range is a [warning].

**F-SEC-10.** Outside code blocks, any line starting with `<!-- wolfden:`
(after optional whitespace, with optional whitespace after `<!--`) that
doesn't match `pos-line`, including one that doesn't close on the same
line, is an [error]. A well-formed one that isn't directly under a level 2
to 6 heading, or under an outline row (F-OUT-9), is a [warning] and does
nothing.

**F-SEC-11.** All other HTML comments are kept and ignored, wherever they
are, including directly under a heading. A comment inside a code block or
code span is just text.

| Under `## Shoulder injury` | Result |
|---|---|
| `<!-- wolfden: from=ember:19 -->` | valid |
| `<!-- wolfden: from="ember:19" until=ember:30 -->` | valid |
| `<!--wolfden: until=ash:1 -->` | valid |
| `<!-- wolfden: from=ember:19 from=ember:20 -->` | [error] F-SEC-7 |
| `<!-- wolfden: from=ember:14.2 -->` | [error] F-POS-2 |
| `<!-- wolfden: from=ember:19` (no `-->`) | [error] F-SEC-10 |
| `<!-- wolfden: from=ember:19-->` | [error] F-SEC-10, needs a space |
| `<!-- wolfden: -->` | [error] F-SEC-10, no attributes |
| `<!-- wolfden: status=open pin=true -->` | valid ([warning] F-PIN-3) |
| `<!-- wolfden: status=maybe -->` | [error] F-STAT-1 |
| `<!-- wolfden: pin=yes -->` | [error] F-PIN-1 |
| `<!-- wolfden: pov=mara -->` | [warning] F-SEC-7 |
| a blank line, then `<!-- wolfden: from=ember:19 -->` | [warning] F-SEC-10 |
| `<!-- todo: check the date -->` | kept, ignored |

### 9.3 Status and pins

Sections, field entries (§7.3), and outline rows (§11) are **facts**. Each
can carry its own status, and sections and entries can be pinned.

| `status` | Meaning | In canon |
|---|---|---|
| `idea` | Undecided material | no |
| `open` | An open question, with options banked under it | no |
| `candidate` | One option for an open question | no |
| `draft` | Meant as canon, not settled | yes, marked as draft |
| `canon` | Settled | yes |
| `retconned` | Was canon, has been replaced | no |

**F-STAT-1.** A fact's `status` MUST be one of the six values above.
[error] Entities use only four of them (F-ENT-5).

**F-STAT-2.** A fact without a `status` takes the status of what holds it:
a subsection its parent section's, a section or field entry its entity's.
An outline row without one is `canon`.

**F-STAT-3.** A fact can't be more canon than what holds it. Its
**effective status** is its own, except that inside a holder whose
effective status is out of canon the fact is out of canon too, and inside
a `draft` holder a `canon` fact counts as `draft`. A fact that says `draft`
or `canon` inside an out-of-canon holder is a [warning]. "In canon" always
means effective status `draft` or `canon`.

**F-STAT-4.** Out-of-canon facts are kept and indexed, and views that ask
for them (an idea bank, a list of open questions) show them. Canon views
leave them out (§9.4), just as they leave out out-of-canon entities.

**F-PIN-1.** A **pinned** fact is hard canon: one of the facts most
important to check before drafting. A section is pinned with `pin=true` in
its comment, and a field entry with `pin: true`, including a plain fact
written as a single entry (`born: { value: "2291", pin: true }`). `pin` is
`true` or `false`, and defaults to `false`. Any other value is an [error].

**F-PIN-2.** Pinning a section pins its subsections.

**F-PIN-3.** A pinned fact whose effective status is out of canon is a
[warning]. A pinned `draft` fact is allowed, and is marked as draft.

### 9.4 Canon views

**F-VIEW-1.** A **canon-as-of** view for scope `S` at position `p` covers
the entities of `S` and of the scopes that enclose it (design.md §2). It
includes an entity if its `status` is `draft` or `canon` (F-ENT-5) and `p`
isn't before its `from` (F-ENT-6). For each included entity it shows the
lead, each field's value at `p` from its in-canon entries (§7.3), and the
in-canon sections in effect at `p` (F-SEC-8, F-SEC-9). Facts whose
positions don't resolve are left out (F-POS-4).

**F-VIEW-2.** A **pinned canon** view for `S` at `p` is the part of the
canon-as-of view that is pinned: every pinned section and field entry that
the canon-as-of view includes, grouped by entity. The MCP server serves it
ahead of the rest of canon (design.md §8).

## 10. Links

### 10.1 Syntax

```abnf
link       = "[[" target [ "#" heading ] [ "|" label ] "]]"
target     = 1*tchar
heading    = 1*tchar
label      = 1*lchar
tchar      = <any character except "[", "]", "|", "#", CR, LF>
lchar      = <any character except "[", "]", CR, LF>
```

`[[mara-voss]]` links to an entity. `[[mara-voss|Voss]]` shows "Voss".
`[[mara-voss#Shoulder injury]]` links to a section. Without a label, the
link shows its text as written.

**F-LINK-1.** In a body (entity, container, or outline), links are
recognized anywhere except in code spans, code blocks, and HTML comments,
other than outline row fields (F-OUT-9). A `[[` preceded by a backslash
isn't a link. In frontmatter, links are
recognized in every string under `fields`, including list items and
temporal `value`s, and nowhere else (not in `name` or `aliases`).

**F-LINK-2.** `![[...]]` (an Obsidian embed) isn't a link in v0. It's left
as text, with no diagnostic.

**F-LINK-3.** The target, heading, and label are trimmed. A link with an
empty target (`[[]]`, `[[ |Voss]]`) is a [warning] and isn't a link.

### 10.2 Resolution

**F-LINK-4.** Targets match slugs, names, and aliases, and headings match
section names, after this normalization of both sides: NFC (F-ENC-5), trim,
collapse each run of whitespace to one space, then Unicode default case
folding. So matching is case-insensitive. Hyphens and spaces stay distinct:
`[[mara voss]]` matches the name "Mara Voss", not the slug `mara-voss`.

**F-LINK-5.** The candidates are the entities of the linking file's
universe, at every scope and of every status. Container files aren't link
targets in v0.

**F-LINK-6.** Resolution tries these tiers in order and stops at the first
one with any match:

1. **id:** the target is a ULID (F-ID-1) equal to an entity's `id`.
2. **slug:** the target matches an entity's slug.
3. **name:** the target matches an entity's `name`.
4. **alias:** the target matches one of an entity's `aliases`.

**F-LINK-7.** If the winning tier has one match, the link resolves to it.
If it has several, the match in the nearest scope of the linking file's
scope chain wins. If none of the matches is in the scope chain, or two tie
at the nearest scope, the link is **ambiguous**: a [warning] that lists the
matches, and the link doesn't resolve.

**F-LINK-8.** A link with no match is **broken**, a [warning]. Broken links
are allowed, since they often mark notes not yet written. If the target
would match an entity in another universe of the den, the warning says so
("matches only 'Mara Voss' in universe 'drift'; links don't cross
universes", design.md §2). If it matches a book or series key or name, the
warning says containers aren't link targets yet.

**F-LINK-9.** A `#heading` matches the first section of the resolved entity,
at any level, whose name matches (F-LINK-4). No match is a [warning], and
the link still resolves to the entity.

**F-LINK-10.** The index stores each resolved link's `id` (design.md §3).
Indexing MUST NOT rewrite link text. Writers MAY update link text when a
user renames or moves an entity, as part of that action.

**F-LINK-11.** A `ref` field (#6) holds a single link per value, with
nothing else in the string. A resolved entity whose type isn't the one the
field names is a [warning].

### 10.3 Link checks

**F-LINK-12.** A link in an in-canon fact (F-STAT-3) to an `idea` or
`retconned` entity is a [warning].

**F-LINK-13.** A link's **position** is the effective `from` of the
temporal entry or section that holds it, or else the linking entity's
`from` (F-ENT-6). A link whose target enters canon after the link's
position is a [warning], since it's usually a spoiler or a mistake
(design.md §7). Links whose position is the start of the story aren't
checked.

## 11. Outlines

```markdown
# Ember outline

## Act one

- 37: Mara wakes aboard the *Tessaly* to a distress call.
  <!-- wolfden: chapter=1 pov="[[mara-voss]]" -->
- 38: The away team lands at [[kessan-reach]].
  <!-- wolfden: chapter=2 pov="[[mara-voss]]" progress=drafted -->
  A continuation line is part of row 38.
  - So is a nested list.
- 39a: The ambush.
  <!-- wolfden: chapter=2 tags="action, ambush" -->
- 39b: Mara is hit.
  <!-- wolfden: chapter=2b status=candidate -->
- 40:
```

```abnf
row-id     = row-number *lower         ; 37, 69a, 69ab
row-number = %x31-39 *DIGIT            ; no leading zeros
row-start  = list-marker 1*WSP row-id ":" [ 1*WSP text ]
list-marker = "-" / "*" / "+"
```

**F-OUT-1.** `outline.md` is optional and is only read in a book folder.
Frontmatter is optional, needs no `id`, and its keys are kept. An outline
isn't an entity.

**F-OUT-2.** A **row** is a top-level bullet list item that starts with
`row-id ":"`. The row's text is the rest of the item, including
continuation lines and nested blocks, except its fields line (F-OUT-9).
Headings and prose between rows are free-form grouping and are ignored.

**F-OUT-3.** A top-level list item that isn't a row is a [warning]. This
includes ordered list items (`37. ...`), which markdown renumbers when it
renders them.

**F-OUT-4.** Rows are ordered by number, then by letter suffix as an ASCII
string, with no suffix first: `69 < 69a < 69aa < 69b < 70`. Rows that
aren't in ascending order in the file are a [warning].

**F-OUT-5.** Row ids MUST be unique across all outlines of a series, or
within the outline of a standalone book. A repeated row id is an [error] on
every outline involved.

**F-OUT-6.** Row ids keep counting across a series: every row of a book
SHOULD come after every row of the book before it in series order. A book
whose first row isn't after the previous book's last row is a [warning].
Gaps between books are allowed.

**F-OUT-7.** To split a row, add letter suffixes (`69` becomes `69a` and
`69b`, or `69` stays and `69a` follows it). Writers MUST NOT renumber
existing rows, and SHOULD NOT reuse the id of a deleted row.

**F-OUT-8.** Links in rows resolve from the book's scope (§10). A reference
such as "row 69a" in prose is plain text in v0. Rows don't have positions
(F-POS-6). Entities point at rows with a `row` field (F-FLD-7).

**F-OUT-9.** A row's **fields** are an optional comment in the `pos-line`
grammar (§9.2) on the line directly after the row's first line, indented
as part of the item. Each `key=value` pair is a field, kept in the order
written:

- `chapter`: a chapter key of this book, mapping the row to that chapter.
  A chapter that doesn't exist is a [warning].
- `status`: the row's status (F-STAT-1). It defaults to `canon`.
- `from`, `until`, and `pin` don't apply to rows. They're a [warning], and
  are kept.
- Any other key is a custom field, such as `pov`, `progress`, or `tags`,
  with no diagnostic. Its value is a string. Links in it are recognized
  (F-LINK-1). Wolfden doesn't split values, so a list such as
  `tags="action, ambush"` stays one string in v0.

A repeated key is an [error]. A `<!-- wolfden:` line anywhere else in an
outline is a [warning] and does nothing (F-SEC-10).

**F-OUT-10.** Rows with a `chapter` SHOULD follow chapter order: a row
mapped to an earlier chapter than a row above it is a [warning].

## 12. Preservation

**F-RT-1.** Writers MUST keep everything they don't mean to change: unknown
frontmatter keys, YAML comments, key order, quoting style, unknown and
custom sections, HTML comments, whitespace, line endings (F-ENC-3), the
final newline (F-ENC-4), and any byte order mark (F-ENC-2).

**F-RT-2.** An edit to one field, entry, or section changes only the bytes
of that field, entry, or section.

**F-RT-3.** Reading a file and writing it back unchanged MUST produce the
same bytes (#18 tests this).

**F-RT-4.** A writer that adds a frontmatter key puts it after the existing
keys, unless the type's template gives an order (#6).

## 13. Appendix: a minimal den

A complete, valid den with one universe, one series, one book, and two
entities.

```text
starfall-den/
  den.yaml
  universes/starfall/
    universe.md
    entities/species/velari.md
    series/long-dark/
      series.md
      entities/characters/mara-voss.md
      books/ember/
        book.md
        outline.md
```

`den.yaml`:

```yaml
format: 1
authors:
  wren-calder: { name: Wren Calder }
packs: [space-opera]
```

`universes/starfall/universe.md`:

```markdown
---
id: 01JAM9S346Q3D25VT4F5V37E3S
name: Starfall
authors: [wren-calder]
---

A frontier war at the edge of settled space.
```

`universes/starfall/series/long-dark/series.md`:

```markdown
---
id: 01JA3E28JT97KB6CQ643DZVMXX
name: The Long Dark
books: [ember]
---
```

`universes/starfall/series/long-dark/books/ember/book.md`:

```markdown
---
id: 01JAQKFBF5KZNWJ47TAN9ZT24M
name: Ember
chapters: [prologue, 1, 2, 3, 4]
---
```

`universes/starfall/entities/species/velari.md`:

```markdown
---
id: 01JANPZX45HY43KWJRP1XPA7Z3
type: species
name: Velari
---

## Overview
Long-lived, silver-haired, and rarely seen off their homeworld.
```

`universes/starfall/series/long-dark/entities/characters/mara-voss.md`:

```markdown
---
id: 01JA7Q8M4XK3V9R2D5W8HN6TQB
type: character
name: Mara Voss
aliases: [Voss]
from: "ember:1"
fields:
  species: "[[velari]]"
  rank:
    - { value: Lieutenant, from: "ember:1" }
    - { value: Commander, from: "ember:4" }
---

# Mara Voss

## Overview
Executive officer of the *Tessaly*.

## Shoulder injury
<!-- wolfden: from=ember:3 -->
Takes a flechette round in the ambush. The arm never fully recovers.
```

`universes/starfall/series/long-dark/books/ember/outline.md`:

```markdown
- 1: A distress call from the edge of the Reach.
- 2: [[Mara Voss]] leads the away team.
- 3: The ambush.
- 4: Voss takes command.
```

As of `ember:2`, Mara Voss is a Velari lieutenant with an Overview and no
Shoulder injury. As of `ember:4`, she's a commander and the injury section
is in effect. As of `ember:prologue`, she isn't in canon yet (F-ENT-6).
