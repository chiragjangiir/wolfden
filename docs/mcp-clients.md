# Connecting Claude and other MCP clients

Wolfden has a built-in [MCP](https://modelcontextprotocol.io) server, so
an AI assistant can read your den and edit it through Wolfden's own tools
instead of guessing from pasted files. This page shows how to add it to
Claude Code, Claude Desktop, and other MCP clients, and gives example
prompts to try against the [Dracula sample](../samples/dracula/).

How the server behaves is described in [design.md §8](design.md#8-ai-integration-mcp),
and the `wolfden mcp` command in [§7](design.md#the-command-line-v0).

## 1. Build Wolfden

There's no installer or published package yet, so you build Wolfden from
source. You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
From the root of a clone, publish the command-line tool to a folder of
your choice:

```sh
dotnet publish src/Wolfden.Cli -c Release -o <folder>
```

The folder then holds `wolfden.exe` on Windows, or `wolfden` on macOS and
Linux. It needs the .NET 10 runtime and the ASP.NET Core 10 runtime; the
SDK includes both. Check that it runs:

```sh
<folder>/wolfden mcp --help
```

You can also run the server straight from the clone, with the command
`dotnet` and the arguments
`run --project <clone>/src/Wolfden.Cli -- mcp --den <den>`, where
`<clone>` is the clone's full path (a client doesn't start the server in
the clone). But that builds the project each time the client starts it,
which is slow. A published folder is better for everyday use.

## 2. Pick a den

The server works on one den, named with `--den <folder>`. If you leave
`--den` out, it uses the folder the client starts it in, or the nearest
folder above it with a `den.yaml`. MCP clients don't always say which
folder that is, so give `--den` with a full path.

To try the write tools, **copy the Dracula sample out of the clone first**,
to a new folder that doesn't exist yet, and turn on its history. From the clone's
root (in PowerShell, use `Copy-Item -Recurse` for `cp -r`):

```sh
cp -r samples/dracula <copy>
<folder>/wolfden init <copy> --git
```

`init --git` makes the copy a git repository and a first checkpoint. If
git doesn't have your name and email yet, add `--author-name` and
`--author-email`; they're saved for that den only
([using-wolfden.md](using-wolfden.md#turning-on-history)).

Inside the clone, the sample is part of Wolfden's own repository, so
`checkpoint` would commit to Wolfden and `get_history` would show
Wolfden's commits. In a copy with its own repository, history is the
den's. A checkpoint is a commit made with the git identity configured for
that repository. A den without git still works; `get_history` and
`checkpoint` then say history isn't available.

`samples/dracula` in the clone is fine for the read tools. The server
keeps its index in the den's `.wolfden/` folder, which the sample's
`.gitignore` already leaves out.

## 3. Add the server to your client

Every client needs the same two things: a command, the full path of
`wolfden` (or `wolfden.exe`), and its arguments, `mcp --den <den>`. The
server speaks MCP over stdio. It needs no network access, keys, or
environment variables. Use full paths in client settings, and don't count
on `~` or `%USERPROFILE%` being expanded there.

The examples below use `C:\Wolfden\bin\wolfden.exe` and
`C:\Wolfden\dracula` on Windows, and `/opt/wolfden/wolfden` and
`/opt/dens/dracula` on macOS and Linux. Replace them with your own
paths.

### Claude Code

Add it with `claude mcp add`. Everything after `--` is the command and its
arguments, so the server's own options don't get read as Claude Code's.
On Windows, in PowerShell or the Command Prompt:

```powershell
claude mcp add wolfden -- C:\Wolfden\bin\wolfden.exe mcp --den C:\Wolfden\dracula
```

On macOS and Linux:

```sh
claude mcp add wolfden -- /opt/wolfden/wolfden mcp --den /opt/dens/dracula
```

`--scope` (before the name) says where the setting is kept:

- `local`, the default: only you, only in the current project.
- `user`: you, in every project.
- `project`: everyone who works in the project, through a `.mcp.json`
  file at its root that you can commit. Claude Code asks each person to
  approve a project's servers before it starts them.

```sh
claude mcp add --scope user wolfden -- /opt/wolfden/wolfden mcp --den /opt/dens/dracula
```

You can also write `.mcp.json` by hand:

```json
{
  "mcpServers": {
    "wolfden": {
      "command": "C:\\Wolfden\\bin\\wolfden.exe",
      "args": ["mcp", "--den", "C:\\Wolfden\\dracula"]
    }
  }
}
```

A committed `.mcp.json` only works for people who have Wolfden and the den
at the same paths, so for a den you share, `local` or `user` scope is
usually the better fit.

`claude mcp list` shows the servers and whether they connect, and `/mcp`
inside a session shows Wolfden's status and tools.

### Claude Desktop

Open **Settings → Developer → Edit Config**, which opens (and creates, if
needed) `claude_desktop_config.json`:

- Windows: `%APPDATA%\Claude\claude_desktop_config.json`
- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`

Add Wolfden under `mcpServers`, keeping any servers already there:

```json
{
  "mcpServers": {
    "wolfden": {
      "command": "C:\\Wolfden\\bin\\wolfden.exe",
      "args": ["mcp", "--den", "C:\\Wolfden\\dracula"]
    }
  }
}
```

On macOS:

```json
{
  "mcpServers": {
    "wolfden": {
      "command": "/opt/wolfden/wolfden",
      "args": ["mcp", "--den", "/opt/dens/dracula"]
    }
  }
}
```

In JSON, a backslash in a Windows path is written twice. Save the file,
then quit Claude Desktop completely and start it again. If Wolfden doesn't
appear among the connectors, Claude Desktop's logs say why: the server's
own messages are in `mcp-server-wolfden.log`, in `%APPDATA%\Claude\logs`
on Windows or `~/Library/Logs/Claude` on macOS.

### Other MCP clients

Any client that can start a local (stdio) MCP server can use Wolfden.
Give it the command `wolfden` (full path) and the arguments
`mcp --den <den>`, plus any of these:

| Option | What it does |
|---|---|
| `--universe <key>` | Pins the session to one universe. Every call reads it, and a call that names another universe, or `*`, is refused. Useful in a den with several universes. |
| `--no-watch` | Doesn't watch the files. Each call still checks them first and rebuilds the index if they changed. |

Standard output carries only the protocol, and log messages go to
standard error. The server handles both the current MCP handshake and the
older `initialize` one. It exits when the client disconnects.

You can run more than one client against the same den. Only one Wolfden
watches a den's files at a time; the others check the files before each
call instead.

## 4. The tools

The server's own instructions and each tool's description tell the
assistant how to use them, so you don't need to name tools in your
prompts. This list is for knowing what the assistant can do.

**Read tools** change nothing:

| Tool | What it does |
|---|---|
| `search` | Full-text search over names, aliases, leads, section text, and field values (a link in a field matches its note's name and aliases too); `field` searches one field's values. A hit in a field has `field` (its name) and `line` (the entry's line), with no `section`, such as `{"field": "occupation", "line": 10, "snippet": "[physician], professor", …}`. A query's words match anywhere in any order; words in double quotes (`"silver key"`) match as a phrase, side by side and in order. |
| `get_entity` | One entity, by name, alias, file name, id, key, or path; with `as_of`, only what the reader knows at that position. |
| `list_entities` | Entities by type, status, series, or book. |
| `canon_as_of` | Canon at a story position (or the end of the story), pinned canon first. |
| `get_chapter` | A book's chapters and their fields, with each value's source (the chapter, or `chapter_defaults` above it). |
| `get_chapter_defaults` | A universe's, series', or book's `chapter_defaults`, and the books they reach. |
| `check` | The continuity check, as `wolfden check` runs it. |
| `get_history` | The checkpoints that changed an entity's file, and optionally a diff. |

**Write tools** change the den's files:

| Tool | What it does |
|---|---|
| `create_entity` | A new entity from its type's template. If its name would take links from another entity, it's refused unless `allow_relinks`. |
| `update_fields` | Set, add to, or remove anything in an entity's frontmatter (its fields, `status`, `from`), and add aliases. |
| `append_section` | Add text to a section, or add a new section. |
| `add_idea` | Record undecided material with `status: idea`, as its own entity or as a section of one. A new idea entity whose name would take links from another entity is refused unless `allow_relinks`. |
| `rename_section` | Rename a section and rewrite the links to it, den-wide. |
| `rename_entity` | Rename an entity and rewrite the links that used the old name, or keep the old name as an alias. |
| `change_alias` | Change or remove an alias, rewriting its links the same way. |
| `copy_entity` | Copy an entity under a new name and id, beside it or into another scope or universe. |
| `move_entity` | Move an entity's file to another scope of its universe (a series, a book, or the universe itself), keeping canon views as they were. Links to it that would stop reaching it, and its own links that would change target, are rewritten to ids. |
| `update_chapter_fields` | Change one chapter's own fields. |
| `update_chapter_defaults` | Change a universe's, series', or book's `chapter_defaults`. |
| `checkpoint` | Commit the den (or some paths) to git, with the assistant as co-author. |

## 5. What keeps the writes safe

- **Nothing is saved that the den couldn't read.** Each edit is checked
  as a load of the den would check it, and refused if it would leave an
  error. The result lists any warnings.
- **No lost edits.** A write to an existing file takes the hash the
  assistant read (`get_entity`, `get_chapter`, or `get_chapter_defaults`
  give it), and is refused if the file has changed since, for example
  because you edited it in your text editor. The assistant reads it again
  and retries.
- **Finished books stay finished.** Nothing in a book with `status: final`
  is written: its entities, `book.md`, or outline. A rename that would
  have to rewrite a link in one is refused as a whole, and so is a change
  to a series' or universe's `chapter_defaults` that would change what a
  final book's chapters have.
- **Effects, not just bytes.** Results say what an edit means for the
  story: a status that takes an entity out of canon views, a chapter left
  with no POV, the links a rename rewrote, and any link that would now
  reach a different entity (a change like that is refused unless the
  assistant is told to allow it).
- **Files only, until a checkpoint.** Writes go to the files, not to git.
  `checkpoint` commits them, and lists which files the session wrote and
  which were changed outside it, so your own edits are never hidden in an
  assistant's commit. Until then, `git diff` shows exactly what changed.
  The server's instructions ask the assistant to checkpoint at natural
  stopping points, such as the end of a notes pass or a planning
  session, with a message that says what the pass was ("Notes pass after
  drafting ch 12"), rather than after every edit.
- **One universe at a time.** Every tool but `check` and `checkpoint`
  reads one universe; `search` and `list_entities` also take `*` for
  every universe. `--universe` pins a session to one.

Your MCP client may also ask you to approve each tool call. That's the
client's setting, not Wolfden's.

## 6. Example prompts

These work against the Dracula sample. The sample's one book has the key
`dracula`, so `dracula:9` is chapter 9. Prompts that write need the copy
described in [Pick a den](#2-pick-a-den).

| Prompt | Tools it uses |
|---|---|
| "Which characters has the reader met by chapter 9? Just the names." | `canon_as_of` |
| "What does the reader know about Renfield as of chapter 8? No spoilers from later chapters." | `get_entity` with `as_of` |
| "Where do my notes mention the bloofer lady?" | `search` |
| "Which characters are physicians?" | `search` with `field: occupation` |
| "Whose point of view is chapter 21, and when is it set?" | `get_chapter` |
| "Run a continuity check on the den and explain anything it finds." | `check` |
| "Add to Renfield's Want section: he wants to be trusted with something alive and large." | `get_entity`, then `append_section` |
| "What changed in Renfield's notes since the last checkpoint?" | `get_history` with `diff` |
| "Rename the faction 'The little band' to 'The Crew of Light', and fix any links." | `get_entity`, then `rename_entity` |
| "Note an idea, not canon: what if Renfield kept a diary of his own?" | `add_idea` |
| "Checkpoint this session as 'Renfield notes'." | `checkpoint` |

## 7. Gotchas for AI assistants

What an assistant must do, and must avoid, when it works on a writer's
den. The server's instructions and tool descriptions say the same in
fewer words; this list is for you, and for an assistant that reads the
docs. The writer's own list is in
[using-wolfden.md](using-wolfden.md#gotchas).

- **Work through the tools, not the files.** An assistant that can
  also edit files, such as Claude Code, should still change notes with
  Wolfden's tools. They check each edit as the den reads it, keep
  links, ids, and finished books safe, and remember what the session
  wrote for `checkpoint`. A file edited directly gets none of that.
  No tool yet adds a series, a book, or a chapter, or edits an outline;
  edit those files directly only when the writer asks, then run `check`.
- **Never touch `.wolfden/` or `.git/`.** `.wolfden/` is Wolfden's
  index, rebuilt from the files; `.git/` is the den's history. Neither
  holds anything to edit.
- **`frozen` means ask the writer.** A book with `status: final` is
  finished, and nothing in it can be written. There's no tool to unfreeze
  it, and an assistant mustn't work around the refusal, by editing the
  file directly or by copying a note out of the book to change it there.
  Ask the writer, who can unfreeze it in the web UI or with `wolfden
  unfreeze`.
- **`stale_file` means read again, then retry.** The file changed after
  the assistant read it, often because the writer is editing it. Read it
  again (`get_entity`, `get_chapter`, or `get_chapter_defaults`), and
  make the edit on what's there now. Never resend the old text over the
  writer's change.
- **`allow_relinks` only with the writer's say-so.** A create, copy,
  rename, or move that would send existing links to a different note is
  refused, with the links listed. Show them to the writer, and set
  `allow_relinks` only if they agree. The same goes for
  `entry_outside_scope` from `move_entity`: the writer picks
  `keep_entry` or `enter_at_new_scope`.
- **Positions must resolve.** A position is `book:chapter`, by key
  (`dracula:5`), and must be a chapter the book lists; `get_chapter`
  lists them. A read, or `create_entity`'s `from`, at one that doesn't
  is `bad_position`. A `from` or `until` written by `update_fields` or
  `append_section` isn't checked: it's saved, `check` warns (F-POS-4),
  and that fact is left out of every canon view.
- **Read by position to avoid spoilers.** Without a position,
  `get_entity`, `list_entities`, and `search` give the author's full
  view, ideas and later reveals included. To draft or review a chapter,
  use `canon_as_of` at that chapter, or `get_entity` with `as_of`.
  `search` and `get_history` have no position.
- **Writes land at once; only `checkpoint` records them.** The writer
  sees each write straight away, in the web UI and their editor, but it
  isn't in the den's history until a checkpoint. Make one at the end of
  a pass, not after each edit, with a message that says what the pass
  was and the assistant as `co_author`. It records the whole den unless
  `paths` names some files, so tell the writer what's in
  `changed_outside_session`. After `move_entity`, `paths` needs both the
  old and the new path.
- **Pinned canon: check with the writer.** An edit to a pinned fact is
  made, and `pinned_canon` in the result lists it. Confirm it was meant.
- **A pinned session sees one universe.** With `--universe`, every call
  reads that universe, and one that names another universe, or `*`, is
  refused. `checkpoint` still records the whole den.
- **Images stay put.** `move_entity` and `copy_entity` don't move a
  note's images or change their paths, so tell the writer when a moved
  note's images need moving too.
