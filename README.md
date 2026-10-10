# Wolfden

**A story bible for writers of long, interconnected fiction.**

Wolfden keeps the lore of your series in one place: characters, settings,
timelines, plot threads, outlines, and every invented thing a world needs
(star systems, factions, ships, spells). Every fact knows where in the story it
becomes true, so you can always ask *"what's canon as of the end of Book 2?"*

> **Status: early design.** Nothing to install yet. Watch the repo to follow
> along as the design takes shape.

## Why

Writing a series means tracking hundreds of facts across books, and keeping
them consistent. General-purpose note apps and wikis don't know what a series
is, what order the books go in, or that a character's eye color was set in
Book 1, chapter 3. Wolfden is built around that.

## Planned features

- **Story hierarchy:** Author Identity → Universe → Series (ordered) → Book.
  Notes live at the level they belong to and are inherited down. A Book sees
  its own notes plus its Series' and Universe's.
- **Canon over time:** facts are tagged with where they enter canon (book and
  chapter), which gives point-in-story views for continuity checks and
  spoiler-safe reference.
- **Typed entities:** Character, Location, Faction, and so on, each with a
  consistent structure plus your own custom sections. Bundles of types for a
  genre come as **packs** (space opera, epic fantasy, and so on).
- **Plain markdown in git:** your notes are markdown files with YAML
  frontmatter in a git repository you control, kept local or pushed to a
  private remote. Version history comes from git, and nothing locks you in.
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

Code: [MIT](LICENSE). Sample datasets: CC-BY-4.0 (license file to come with
the data).

---

Created by [Gavin Wolfe](https://github.com/gavin-wolfe).
