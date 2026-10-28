# Using Wolfden

This is the practical guide: how to set up and keep your den (your Wolfden
folder of notes) so it works well, in Wolfden and in the editor you write
in. It grows as features land. The exact rules are in
[`format.md`](format.md), and the reasons behind them are in
[`design.md`](design.md). For choosing whether a note belongs to the
universe, a series, or a book, see [Where to file things](where-to-file.md).
Things that tend to surprise people are collected in [Gotchas](#gotchas).

## Getting started

Set things up in this order. Each step builds on the ones before it.

1. **Build Wolfden.** There's no installer yet, so you build it from a
   clone of the repository. You need the
   [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and
   [Node.js](https://nodejs.org) 22.12 or later for the web UI. Build the
   web UI first, in the clone's `web/` folder, so the next command
   includes it:

   ```sh
   npm ci
   npm run build
   ```

   Then, from the clone's root, publish the `wolfden` command to a folder
   of your choice:

   ```sh
   dotnet publish src/Wolfden.Cli -c Release -o <folder>
   ```

   Add that folder to your PATH, so `wolfden` works from any folder; the
   steps below assume it does. [mcp-clients.md §1](mcp-clients.md#1-build-wolfden)
   has more detail.
2. **Try the Dracula sample first (optional).** It's a complete den, so
   it's a safe place to try everything below before you use your own
   notes. Copy `samples/dracula` out of the clone to a folder of its own,
   and turn on its history with `wolfden init <copy> --git`. Inside the
   clone, it's part of Wolfden's own repository, so its checkpoints would
   land there ([mcp-clients.md §2](mcp-clients.md#2-pick-a-den)).
3. **Make your den, with history.** History is what lets you keep
   versions, see what changed, and freeze a finished book, so turn it on
   from the start:

   ```sh
   wolfden init my-notes --universe vael --git
   ```

   This makes the folder `my-notes`, with a `den.yaml` and a first
   universe, `vael` (a key: lowercase letters, digits, and hyphens,
   starting with a letter). It makes the folder a git repository, and
   makes a first checkpoint. If git doesn't
   know your name and email yet, add `--author-name` and
   `--author-email`: a pen name is fine, and they're saved for this den
   only. For a den you already have, run `wolfden init --git` in its
   folder instead. Don't put a den with history in Dropbox, OneDrive, or
   iCloud. See [Turning on history](#turning-on-history).
4. **Add your series and books.** There's no command for this yet, so
   you make their folders and files by hand: a `series.md` or a
   `book.md` with a `name`, and the book's `chapters`, laid out as
   [format.md §5.1](format.md#51-layout) shows (§13 there has a complete
   small den). With more than one, list them in reading order: `books` in
   `series.md`, and `order` in `universe.md`. List the chapters now: a
   story position, such as "this character enters at chapter 3", only
   works for a chapter the book lists. Then run `wolfden check --fix`,
   which gives the new files their ids.
5. **Run `wolfden check`.** In the den's folder, it checks every note
   and says what's wrong, file by file. It exits 0 when there are no
   errors (warnings alone pass) and 1 when there are. A file with an
   error is left out of Wolfden until it's fixed, so start from a clean
   check.
6. **Run `wolfden serve`.** It opens the den in your browser, on this
   computer only. From there you can [make notes](#making-a-new-note),
   [edit them](#editing-notes), make
   [checkpoints](#checkpoints), and
   [freeze a finished book](#freezing-a-finished-book). Start with a
   checkpoint of the books you just added. It runs until you press
   Ctrl+C.
7. **Connect an AI assistant (optional).** Wolfden's MCP server lets
   Claude or another assistant read and edit the den through Wolfden's
   own tools, which keep links, ids, and finished books safe. Follow
   [mcp-clients.md](mcp-clients.md), from §3; use full paths there.
8. **Set up your editor (optional).** If you also write in
   [Obsidian](#using-obsidian) or [VS Code](#using-vs-code), open the
   den's top folder (the one with `den.yaml`) in it, and change the
   settings those sections give. Read the
   [gotchas for other editors](#images-and-other-editors) too.
9. **Make it a habit.** Make a [checkpoint](#the-habit-one-checkpoint-per-notes-pass)
   after each notes pass or planning session, with a name that says what
   it was. When a book is published or sent off,
   [freeze it](#freezing-a-finished-book). After a big change in another
   editor, run `wolfden check`.

## Making a new note

In `wolfden serve`, choose **New ▾** in the top bar and pick a type:
character, location, event, and so on, plus any types your den's own
packs add. Or, in the contents tree, open a type's list (Characters, say)
under a book or the universe and choose **New Character** there: the type
and the place are filled in for you.

The form asks for three things:

- **Type.** What kind of note it is. Its template decides the fields and
  sections the note starts with.
- **Name.** What you'll call it, and what links use (`[[Sister Agatha]]`).
  The file is named from it.
- **Where it goes.** The universe, a series, or a book. It starts as the
  place you're looking at: the book on a book or chapter page, the place a
  note is filed on that note's page, and otherwise the universe. A book
  marked final takes nothing new until it's unfrozen.

Choose **Create**, and Wolfden makes the file from the template and opens
the new note, with the file's name at the top. Fill it in there, one
field or section at a time, as below.

A new note is canon from the start of where it goes. If you're viewing the
story at a chapter, the form also offers **Enters the story at** that
chapter: tick it when the reader first meets this character (or place, or
thread) there, and the note gets `from:` that chapter. Leave it off for
something that's been true all along. It's offered only when the note
goes somewhere that chapter belongs: the universe, the chapter's series,
or its own book. A note filed in another book should enter in that book.

A name is one line, up to 200 characters.

**If the name would take over links.** Links go to the nearest note of a
name, so a second "Jonathan Harker" filed in the universe would take the
links in your universe notes away from the Jonathan in the book. Wolfden
won't do that quietly: it lists the links that would move and makes
nothing. Choose another name or place, or choose **Create anyway** if
those links really should reach the new note (check them afterwards).
From the command line, that's `--allow-relinks`.

From the command line, `wolfden new` does the same:

```text
wolfden new character "Sister Agatha" --in dracula/book:dracula --from dracula:8
```

`--in` takes the same places as `wolfden copy --to`; leave it out to file
the note in the universe, when your den has one. `--from` is optional,
and has to be a chapter (or book) your universe has: a typo is refused
rather than making a note no view shows.

## Editing notes

`wolfden serve` lets you edit a note on its page, one piece at a time.

- **In place.** Each section has **Edit**, **Rename**, and **Remove**,
  and the opening (the text before the first section) has **Edit
  opening**. Edit opens the text as Markdown, as it's written in the
  file. Type `[[` and a few letters to pick a note to link to, and use
  **Preview** to see it as the page shows it. **Save** (or Ctrl+Enter,
  ⌘+Enter on a Mac) saves; **Cancel** (or Escape) leaves it as it was.
  **Add section** adds one at the end or before another. Renaming a
  section changes the links to it in every note; removing one asks first.
- **Edit file** opens the whole note, frontmatter and all, for anything
  the other controls don't do, such as a section's
  `<!-- wolfden: ... -->` comment.
- **While you edit, you see everything.** The page shows the whole note,
  whatever the story position, and says "Editing shows everything". The
  position view comes back when you save or cancel.
- **If something else changed the note meanwhile,** such as an AI
  assistant working through Wolfden, or you in another editor, the save
  is refused and you see both versions. Choose **Keep mine** (save yours
  over it), **Take theirs** (drop yours), or **Copy mine and reload**
  (copy yours, then edit the note as it is now).
- **Saving isn't a checkpoint.** It saves the file; it doesn't record a
  version in the den's history. Make a checkpoint as you do now.

Notes in a book marked final can't be edited until the book is
unfrozen; their Edit controls are off, and the page says why. See
[Freezing a finished book](#freezing-a-finished-book).

### Editing fields

Each field on a note's page has its own **Edit**. The form fits the
field: a dropdown for a field with set choices, yes or no, a number, a
date, or a link, where you type a few letters of a note's name and pick
it. **Add field** offers the fields the note's type has that the note
doesn't set yet, and **Clear field** takes one out.

A fact that changes over the story is a list of dated values. Each has
a **From** and an **Until**, picked by book and chapter: "this fact
holds from chapter 3 until chapter 9". Leave From empty for the start
of the story, and Until empty for "until the next value". **Add a dated
entry** gives a field a new value from a later chapter; the one before
it holds until then. A fact that doesn't change can still have a
**Known from**, for a reveal the reader only learns later.

Any value can have its own status, such as `candidate` for an option
you haven't settled, and can be **Pinned** as hard canon.

A field your note's type doesn't define, or one written in a way the
form can't show, has **Edit in file** instead, which says why.

## Images

Notes can show pictures: a map, a sketch of a ship, a family tree. Keep
the image file in your den, and point to it from the note. `wolfden serve`
shows it on the note's page.

### Where to put them

- **Next to the note, or in a folder beside it.** A map of the castle
  sits in the same folder as the castle's note, or in an `images/` folder
  there:

  ```text
  universes/dracula/entities/locations/
    castle-dracula.md
    images/
      castle-map.png
  ```

  The image travels with the note. If you move the folder, the picture
  still shows, because the path from the note to the image hasn't changed.
- **In one shared folder for images used everywhere.** A den-level
  `images/` folder (next to `den.yaml`) suits a world map or a crest that
  many notes show.

Wolfden shows `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.avif`, and `.svg`
files. It never shows anything in a folder or file whose name starts with
a dot (such as `.obsidian/` or `.git/`), and nothing outside the den.

### How to point to them

Write the image the usual Markdown way, `![what it shows](path)`. The text
in the brackets is what shows if the picture can't (and what screen
readers say):

- **From the note's folder:** `![Map of the castle](images/castle-map.png)`.
- **Up a folder:** `![Coach](../images/coach.jpg)`. You can go up, but
  not above the den's own folder.
- **From the den's top folder:** start with `/`, as in
  `![World map](/images/world-map.png)`. This works from any note, so it's
  the easy choice for the shared folder.

A space in a file name is written `%20`
(`![Map](images/castle%20map.png)`), or put the path in angle brackets:
`![Map](<images/castle map.png>)`. Simplest of all is a name without
spaces, such as `castle-map.png`. Avoid `%` and `#` in image file names:
`#` ends the path, and a `%` has to be written `%25` (`100%25.png` for
`100%.png`).

If the picture can't be found, the page shows "Image not found" with the
text from the brackets, so you can see which path to fix. Check the
spelling, and that the path starts from the note's folder.

When Wolfden moves a note to another scope (or an AI assistant does,
through Wolfden), the note's images stay where they are and their paths
aren't changed. Move the images too, or use a `/` path for an image whose
note may move.

Images from websites, such as `![Harbour](https://example.com/harbour.jpg)`,
don't show by default, so opening a note doesn't tell the website about
it. To allow them, add `remote_images: true` to `den.yaml`
([format.md F-DEN-8](format.md)).

### Using Obsidian

You can keep using [Obsidian](https://obsidian.md) to write in your den.

1. **Open the den's top folder as the vault,** the one with `den.yaml`
   in it. Then Obsidian and Wolfden agree on where everything is. Use
   paths relative to the note (the next setting does this for you),
   rather than ones starting with `/`.
2. In **Settings → Files and links**:
   - Set **New link format** to **Relative path to note**. Obsidian then
     writes image paths from the note's folder, as Wolfden reads them.
   - Set **Default location for new attachments** to **Same folder as
     current file**, or to **In subfolder under current folder** (and name
     the subfolder, such as `images`). A picture you paste or drag in then
     lands next to its note.

When a path doesn't lead to the file, Obsidian looks for an image with
that file name anywhere in the vault, so an image can show in Obsidian
and still be "not found" in Wolfden. Make sure the path points at the file
itself.

One thing to watch: Wolfden's links between notes use double brackets,
`[[Mina Harker]]`, as Obsidian's do, but an Obsidian image embed,
`![[castle-map.png]]`, isn't a Wolfden image in this version: Wolfden shows
it as plain text ([format.md F-LINK-2](format.md)). If you keep
Obsidian's **Use [[Wikilinks]]** setting on, so your note links stay
`[[...]]`, Obsidian will write pasted images as `![[...]]` too. Change
those to the `![what it shows](path)` form, such as
`![Map of the castle](images/castle-map.png)`, so they show in Wolfden.

### Using VS Code

VS Code's Markdown preview reads image paths the same way Wolfden does:
from the note's folder, and with `/` from the top of the folder you
opened. Open the den's top folder in VS Code, and a path that shows in its
preview shows in Wolfden too.

## Checkpoints

A **checkpoint** is a saved version of the whole den that you can come
back to. Wolfden saves your edits to the files as you make them, but a
checkpoint is what puts them in the den's history: each note's History
tab lists the checkpoints that changed it, and shows what changed between
them. Saving never makes a checkpoint by itself, so you decide when a
version is worth keeping.

Checkpoints need history turned on. Without it your notes still work,
and the top bar of `wolfden serve` says **No history · Turn on…**.

### Turning on history

Wolfden keeps the history with git, in the den's folder, but you don't
need to know git to use it. In `wolfden serve`, choose **No history ·
Turn on…** in the top bar, then **Turn on history**. Or, from the command
line, in the den's folder:

```text
wolfden init --git
```

Either way, Wolfden makes the den's folder a git repository, adds
`.wolfden/` (its index, which it rebuilds from your notes) to the folder's
`.gitignore`, and makes a first checkpoint, "Start of history", of the den
as it is. Untick **Make a first checkpoint** (or add `--no-checkpoint`) to
start with none. The page picks it up straight away: no restart.

Git records a name and email with each checkpoint. If it doesn't have
them yet, Wolfden asks: fields in the dialog, or `--author-name` and
`--author-email` on the command line:

```text
wolfden init --git --author-name "Ash Calloway" --author-email ash@example.com
```

Any name will do, a pen name included, and the email can be a
placeholder. They're saved for this den only, never for the rest of your
computer. If git already has a name and email, nothing is asked, and if
you give one anyway, Wolfden says it used git's instead. (If your git
settings give a name only inside certain folders, the dialog can't tell
until the den is a repository: it shows the fields, but you can leave
them empty, and Wolfden asks again only if they're really needed.)

A few things to know:

- **Not in a synced folder.** Don't turn on history for a den in Dropbox,
  OneDrive, or iCloud: two machines syncing while git writes can corrupt
  it. Use a private remote for a backup instead.
- **A den that's part of a bigger repository** already has history there,
  so there's nothing to turn on, and Wolfden says so.
- **If a step fails** (a git hook refuses the first checkpoint, say),
  Wolfden says what it did and what it didn't. History is on by then, so
  make the first checkpoint from the dialog once the problem is fixed, or
  run `wolfden init --git` again: it picks up where it stopped.
- **A new den** can start with history: `wolfden init my-notes --git`.
- **For scripts:** `wolfden init` exits 0 when its main job is done. For
  a new den that job is making the den, so a new den inside a larger
  repository, or on a computer without git, still exits 0. For an
  existing den the job is turning on history, so the same situations
  exit 2.

### The habit: one checkpoint per notes pass

The rhythm that works best is a checkpoint at each natural stopping
point:

- **After each chapter's notes pass.** Once you've drafted a chapter,
  go over the notes it touched (characters, threads, the outline), then
  make one checkpoint named for that pass, such as "Notes pass after
  drafting ch 12".
- **After a planning session.** When a session settles new decisions,
  checkpoint them under a name that says what was decided, such as
  "Planning session: harbour subplot".

**Name every checkpoint.** The name is how you'll find this version
later, in a long list of them, so say what the pass was rather than
"edits" or the date (the date is recorded anyway). Wolfden never names a
checkpoint for you, unless you turn on automatic checkpoints (below).

### Making one

In `wolfden serve`, the top bar says when notes have changed since the
last checkpoint: "3 notes changed since the last checkpoint ·
Checkpoint…". It stays hidden when nothing has changed. Click it to see
the changes, grouped by universe and by book, so you can look the pass
over; notes saved on the page are marked "saved in Wolfden", and the
others changed some other way, in your editor or by an AI assistant.
Give the checkpoint a name, and choose **Checkpoint**. If git refuses
(a hook, say), the dialog shows why, and nothing is recorded.

### AI assistants make them too

An AI assistant working through Wolfden's MCP server (see
[mcp-clients.md](mcp-clients.md)) can make checkpoints with the
`checkpoint` tool, so a pass you do together ends the same way: the
assistant updates the notes, then makes one checkpoint with a
descriptive name. It records the assistant as a co-author, and tells you
about any notes in it that changed outside its session, such as edits
you made in your editor meanwhile. Ask it to checkpoint at the end of the
pass, not after every edit.

### Automatic checkpoints

If you'd rather not think about it, `wolfden serve` can make checkpoints
by itself. Add a line to `den.yaml`:

```yaml
auto_checkpoint: 30m
```

Then, while `wolfden serve` runs, once the den has gone 30 minutes
without a change, it makes a checkpoint named for what changed
("Auto-checkpoint: edited 3 notes"). Any time from `5m` to `24h` works,
such as `2h` or `1h30m`. Remove the line, or set it to `false`, to turn
it off. Automatic checkpoints don't replace named ones: their names say
only what changed, not why, so keep naming your passes and planning
sessions. See [format.md F-DEN-9](format.md) for the exact rule.

It waits while git is busy with something of yours, such as a rebase or
a merge, and makes the checkpoint once that's done. If your git signs
commits (with GPG, say), keep the passphrase cached (`gpg-agent` does
this), or an automatic checkpoint waits on the passphrase prompt. Saving
still works while it waits.

## Freezing a finished book

When a book is done (published, or sent off), **freeze** it. A frozen
book's notes, chapters, and outline can't be edited, by you in Wolfden or
by an AI assistant, so what the book says stays as it was published. It
replaces keeping a `_FINAL` copy of your notes.

In `wolfden serve`, open the book's page and choose **Freeze…**. Wolfden
says what will happen and asks first. Or, from the command line:

```text
wolfden freeze dracula
```

(add `--universe <key>` if your den has more than one universe).

Freezing does three things, in order:

1. It marks the book `status: final` in its `book.md`. That's the freeze
   itself.
2. If the den has history, it makes a checkpoint of the whole den, so
   anything you hadn't checkpointed yet goes in too. Before you
   confirm, Wolfden says how many other changed files that is.
3. It tags that checkpoint `book/<universe>/<book>/final`, such as
   `book/dracula/dracula/final`: a snapshot you can always come back to,
   because Wolfden never moves or deletes a tag.

Without history, step 1 is the whole freeze, and Wolfden says no
snapshot was taken.

**To change a frozen book,** unfreeze it: **Unfreeze…** on its page, or
`wolfden unfreeze dracula`. Its `book.md` then says `status: draft`, and
with history Wolfden makes a checkpoint, so the history shows when you
reopened it. When you freeze it again, the new snapshot gets the next
tag, `final-2`, then `final-3`, and so on, and the earlier tags stay, so
each version you froze is kept.

**If git refuses** the checkpoint or the tag (a hook, say), the book is
still frozen, and Wolfden says what went wrong. Fix that, then freeze the
book again: the page offers **Finish freezing…**, and `wolfden freeze`
does the same. It makes only the checkpoint and the tag that are
missing. The same goes for a book you marked `status: final` by hand:
freezing it takes a snapshot of the book as it is now.

AI assistants can't freeze or unfreeze a book. If one tries to edit a
frozen book, it's told the book is frozen, and should ask you.

## Gotchas

Things that surprise people, with what happens, why, and what to do.
Each links to the section with the details.

### Setting up

- **`wolfden serve` says it was built without its web UI.** The web UI
  is built with npm, separately, and `dotnet publish` (or `dotnet
  build`) includes it only if it was built first. Run `npm ci` and `npm
  run build` in the clone's `web/` folder, then publish again and
  restart `serve` ([Getting started](#getting-started), step 1).
- **Anyone using this computer can open the page.** `wolfden serve`
  listens on this computer only, never the network, but this version has
  no accounts, so anyone else signed in to the same computer can open it
  while it runs. Stop it with Ctrl+C when you're done.
- **A new den has no books.** `wolfden init` makes the den and, with
  `--universe`, a first universe. Series and books are folders and
  files you add by hand for now
  ([Getting started](#getting-started), step 4).

### History and git

Git is the tool Wolfden uses to keep your den's history. A *repository*
is a folder whose history git keeps, and a *commit* is one saved version
in it, which Wolfden calls a checkpoint.

- **Saving isn't a checkpoint.** Every save goes to the file at once,
  but only a checkpoint puts it in the den's history. The top bar of
  `wolfden serve` says how many notes have changed since the last one.
  Make one after each notes pass ([Checkpoints](#checkpoints)).
- **Automatic checkpoints are off by default.** Turn them on with
  `auto_checkpoint: 30m` in `den.yaml`. Only `wolfden serve` makes them,
  while it runs. One takes in edits an AI assistant saved and hasn't
  checkpointed yet, without the assistant's name on them, so ask the
  assistant to checkpoint at the end of its pass
  ([Automatic checkpoints](#automatic-checkpoints)).
- **A checkpoint records the whole den.** It takes in every changed
  note: ones you changed in your editor, and ones in other universes when
  `serve --universe` shows only one. Freezing and unfreezing make one
  too. The checkpoint dialog lists the changes before you confirm, and
  the freeze prompt counts them.
- **Your git name and email are for this den only.** Git records a name
  and email on every checkpoint. Wolfden asks only when git has none,
  and saves what you type in this den's repository, never for the rest
  of your computer. A pen name is fine, and the email can be a
  placeholder. If your git settings already give a name, perhaps through
  an `includeIf` rule (one that picks a name by folder), git's is used,
  and Wolfden says the one you typed wasn't
  ([Turning on history](#turning-on-history)).
- **Not in a synced folder.** Git and Dropbox, OneDrive, or iCloud
  together can corrupt the history when two computers sync while git is
  writing. Use one or the other: git with a private remote for a backup,
  or a synced folder without history.
- **A den inside a larger repository uses that repository.** There's no
  history to turn on: checkpoints are commits in the larger repository,
  of the den's files only, and anything else you've staged there (marked
  for your next commit) stays as it was. Freeze tags belong to the whole repository, so two dens in
  it with the same universe and book keys share the tag numbers. That's
  why you try the Dracula sample in a copy outside Wolfden's own clone.
- **`wolfden init --git` exits 0 when its main job is done.** For a new
  den, that job is making the den; for an existing one, it's turning on
  history. So a new den inside a repository, or on a computer without
  git, exits 0, and the same situations exit 2 for an existing den. Read
  what it prints.
- **Automatic checkpoints wait for git.** While you're in the middle of
  a git rebase or merge (ways of combining two lines of history), they
  wait until you've finished. If git signs
  your commits, keep the passphrase cached (`gpg-agent` does this), or
  they wait on the passphrase prompt.

### Editing

- **Two editors on one note.** If the note changed since you opened it
  (in Obsidian, say, or by an AI assistant), Wolfden refuses the save
  and shows both versions, so nothing is lost. Choose which to keep
  ([Editing notes](#editing-notes)). An assistant's edit is refused the
  same way, and it reads the note again.
- **A note with an error disappears.** A file with an error is left out
  of Wolfden until it's fixed: its page, its links, and every view. An
  error in a `book.md` leaves out the whole book. Run `wolfden check` to
  find it.
- **Copying a note's file by hand makes a duplicate.** Both files have
  the same `id`, so Wolfden leaves both out of views, and links to them
  break, until one gets a new id. Copy with `wolfden copy`, or ask an AI
  assistant, either of which gives the copy a new id. To fix a hand-made
  copy, replace its `id` with one from `wolfden new-id`.
- **Notes written by hand need an id.** One without an `id` still works,
  with a warning, but links can't reach it by id. `wolfden check --fix`
  adds the missing ids.
- **Chapter keys are for keeps.** Positions name a chapter by its key
  (`dracula:5`), not its number. To insert a chapter, give it a new key,
  such as `2b`, and renumber with `number:`, so existing positions still
  point where they did ([format.md §6.4](format.md#64-bookmd)).
- **Hidden files and folder links are ignored.** Anything whose name
  starts with a dot (`.obsidian/`, `.trash/`) isn't part of the den, so
  a note moved to `.trash/` is gone from Wolfden. Folder links (symbolic
  links and junctions) aren't followed; `wolfden check` warns about
  them.
- **`.wolfden/` is only an index.** Wolfden keeps its index there and
  rebuilds it from your notes, so never edit it, and nothing is lost if
  it's deleted. Git leaves it out.

### Links

- **A new note can take over links.** Links go to the nearest note of a
  name, so a new note named like an existing one can take its links.
  Wolfden refuses and lists the links that would move. Choose another
  name or place, or **Create anyway** (`--allow-relinks`) if they should
  move ([Making a new note](#making-a-new-note)).
- **Renaming a note by hand doesn't fix its links.** Changing `name:` in
  **Edit file** or in your editor leaves the links that used the old
  name broken. The web UI can't rename a note yet. An AI assistant
  working through Wolfden can, and rewrites the links, or keeps the old
  name as an alias.
- **Moving a note's file can change where links go.** Since the nearest
  note of a name wins, a file moved to another book, or up to the
  universe, can take links from another note, or lose its own. An AI
  assistant's move rewrites links so they reach what they did. After
  moving files yourself, run `wolfden check`.
- **Links don't cross universes.** A link reaches only notes in its own
  universe. `wolfden check` says when a broken link matches a note in
  another one.

### Books and freezing

- **A frozen book refuses edits, but only in Wolfden.** Wolfden and AI
  assistants working through it can't change a frozen book. Your own
  editor can, and Wolfden can't stop it. If that happens, the book's
  page offers **Finish freezing…**, which takes a new snapshot
  ([Freezing a finished book](#freezing-a-finished-book)).
- **Freeze tags never move.** Each freeze keeps its own snapshot, so
  freezing a book again adds `final-2`, then `final-3`, and the earlier
  tags stay.
- **AI assistants can't freeze or unfreeze.** Only you can, on the
  book's page or with `wolfden freeze` and `wolfden unfreeze`. An
  assistant that tries to edit a frozen book should ask you.
- **Spoiler checks are off by default.** `wolfden check` flags a link to
  something the reader hasn't met yet only with `spoiler_checks: true`
  in `den.yaml` ([format.md F-DEN-7](format.md)). It sees links, not
  prose.

### Images and other editors

- **Image paths start from the note's folder,** or from the den's top
  folder if they start with `/`. Not from wherever the image happens to
  be ([Images](#images)).
- **Moving a note doesn't move its images.** Its image paths stay as
  they were, so move the images too, or use a `/` path.
- **Website images don't show.** An image from a website is blocked
  unless `den.yaml` has `remote_images: true`, so opening a note
  doesn't tell the website.
- **Obsidian's `![[image]]` embeds show as text.** Wolfden shows only
  `![what it shows](path)` images. Obsidian writes the embed form when
  **Use [[Wikilinks]]** is on, so change pasted images to the Markdown
  form ([Using Obsidian](#using-obsidian), #189).
- **An image Obsidian shows can be "not found" in Wolfden.** When a path
  is wrong, Obsidian finds an image by its file name anywhere in the
  vault. Wolfden follows the path only.
- **Obsidian's Properties panel can't show `fields`.** It shows a note's
  nested `fields:` as raw text marked unsupported, and a position such
  as `from: dracula:1` as a web link. Editing there may rewrite them. In
  Obsidian's **Settings → Editor**, set **Properties in document** to
  **Source**, so the frontmatter is plain text you edit yourself (#73).
