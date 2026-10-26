# Wolfden

**A story bible for writers of long, interconnected fiction.**

Wolfden keeps the lore of your series in one place: characters, settings,
timelines, plot threads, outlines, and every invented thing a world needs
(star systems, factions, ships, spells). Every fact knows where in the story it
becomes true, so you can always ask *"what's canon as of the end of Book 2?"*

> **Status: early.** The file format and a command-line tool are working:
> `wolfden check` lints a den for continuity, and `wolfden query` shows canon
> as of any point in the story. There's no app yet, and nothing to install.
> You build it from source (see [Try it](#try-it)). Ideas and feedback are
> welcome: see [CONTRIBUTING.md](CONTRIBUTING.md).

## Why

Writing a series means tracking hundreds of facts across books, and keeping
them consistent. General-purpose note apps and wikis don't know what a series
is, what order the books go in, or that a character's eye color was set in
Book 1, chapter 3. Wolfden is built around that.

## Who it's for

Novelists and other writers of long fiction who keep serious notes: series
bibles, timelines, plot threads, worldbuilding. You shouldn't have to be
technical to use it.

- **Where it's headed:** no git or command line needed. Wolfden will offer
  history, checkpoints, and "freeze this book" as ordinary buttons, with git
  doing the work underneath. Backing up to a private remote, such as GitHub,
  will be optional, with guided setup.
- **Where it is today:** early. There's a file format and a command-line
  tool, but no app yet. For now, it suits writers who are comfortable editing markdown files in a
  text editor such as [Obsidian](https://obsidian.md) or VS Code. Git is
  optional. To see what a den looks like, browse the
  [Dracula sample](samples/dracula/).

## Try it

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
Clone the repo, then from its root:

```sh
dotnet run --project src/Wolfden.Cli -- check samples/dracula
dotnet run --project src/Wolfden.Cli -- query --den samples/dracula --at dracula:9 --brief
```

The first command checks the sample den and should find nothing wrong. The
second lists everything a reader knows by chapter 9 of *Dracula*.
`wolfden init` starts a den of your own. The commands are described in
[design.md §7](docs/design.md).
To connect Claude or another AI assistant, see
[Connecting MCP clients](docs/mcp-clients.md).

## Planned features

- **Story hierarchy:** Author Identity → Universe → Series (ordered) → Book.
  Notes live at the level they belong to. A point-in-story view shows
  everything the reader has met so far, wherever it's filed, and can be
  narrowed to one series or book.
- **Canon over time:** facts are tagged with where they enter canon (book and
  chapter), which gives point-in-story views for continuity checks and
  spoiler-safe reference.
- **Typed entities:** Character, Location, Faction, and so on, each with a
  consistent structure plus your own custom sections. Bundles of types for a
  genre come as **packs** (space opera, epic fantasy, and so on).
- **Plain markdown files:** your notes are markdown files with YAML
  frontmatter in a folder you control. Keep it in a git repository, local or
  pushed to a private remote, and version history comes from git. Nothing
  locks you in.
  SQLite is used only as a rebuildable index for search, links, and queries.
- **AI-native:** a built-in [MCP](https://modelcontextprotocol.io) server lets
  AI assistants query canon and update notes, working from structured data
  rather than pasted files.
- **Sample universes:** ready-made datasets to explore, in the spirit of
  Northwind and AdventureWorks. One is based on a public-domain classic and
  one is an invented multi-book series.

## Tech stack (planned)

- ASP.NET Core (.NET 10), with EF Core and SQLite for the index
- React and TypeScript (Vite), with TanStack Query
- Docker for self-hosting (later)

## License

Code: [MIT](LICENSE). The built-in packs in `packs/` (types and
templates): [MIT-0](packs/LICENSE). Sample datasets in `samples/`:
[CC-BY-4.0](samples/LICENSE).

**Your writing is yours.** Wolfden's licenses cover Wolfden itself. They
place no conditions on the notes, manuscripts, or anything else you create
with it, including content copied from its built-in templates.

---

Created by [Gavin Wolfe](https://github.com/gavin-wolfe).
