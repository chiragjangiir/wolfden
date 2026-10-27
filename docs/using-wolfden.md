# Using Wolfden

This is the practical guide: how to set up and keep your den (your Wolfden
folder of notes) so it works well, in Wolfden and in the editor you write
in. It grows as features land. The exact rules are in
[`format.md`](format.md), and the reasons behind them are in
[`design.md`](design.md). For choosing whether a note belongs to the
universe, a series, or a book, see [Where to file things](where-to-file.md).

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

Checkpoints need history turned on: the den's folder must be a git
repository. Run `git init` in the den's folder (or start a new den with
`wolfden init --git`). Don't keep a git repository in a synced folder
(Dropbox, OneDrive, iCloud); use a private remote for a backup instead.
Without history your notes still work, and `wolfden serve` says how to
turn it on.

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
checkpoint for you.

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
