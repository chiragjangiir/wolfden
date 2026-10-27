# Using Wolfden

This is the practical guide: how to set up and keep your den (your Wolfden
folder of notes) so it works well, in Wolfden and in the editor you write
in. It grows as features land. The exact rules are in
[`format.md`](format.md), and the reasons behind them are in
[`design.md`](design.md). For choosing whether a note belongs to the
universe, a series, or a book, see [Where to file things](where-to-file.md).

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
  the other controls don't do yet, such as changing a field or a
  section's `<!-- wolfden: ... -->` comment.
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
unfrozen; their Edit controls are off, and the page says why.

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
