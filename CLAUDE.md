# CLAUDE.md

Guidance for AI coding agents working in this repository.

## Project

Wolfden is an open-source story bible / writer's wiki for long, interconnected
fiction. See `README.md` for the pitch. Design docs live in `docs/`.

**Status:** pre-code, in design. Nothing is built yet.

## Decisions made (2026-10-10)

- **License:** MIT for code. Sample datasets are licensed separately under CC-BY-4.0.
- **v0 scope:** local-first and single-user. No accounts or multi-tenancy in
  v0. Multi-user, multi-tenant hosting (accounts, OAuth with GitHub and
  Google, roles) comes in a later phase. Don't build for it early, but don't
  design anything that rules it out.
- **Source of truth:** markdown files with YAML frontmatter, in a git repo
  the user controls (local only or with a private remote). Version history
  and audit trail come from git, not from tables we build ourselves.
- **Index:** SQLite via EF Core, rebuildable at any time from the files. It
  never holds data that exists only in the database.
- **Domain model:** Author Identity → Universe → Series (ordered) → Book.
  Standalone Books sit directly under a Universe. Notes are scoped to a
  Universe, Series, or Book and inherited downward.
- **Canon over time:** facts can be tagged with where they enter canon (book
  and chapter) to support point-in-story views.
- **Entity types:** user-definable schemas (frontmatter fields, standard
  sections, custom sections). Genre bundles of types are called **packs**.
- **AI integration:** an MCP server is a first-class feature, not an add-on.
- **Stack:** ASP.NET Core (.NET 10 LTS) backend. React, TypeScript, Vite and
  TanStack Query frontend (no Redux unless a real need appears). LibGit2Sharp
  or the `git` CLI for git operations. Docker comes later, for self-hosting.

## Rules

- **Sample data only.** Never put real manuscripts, notes, or personal
  details from any real project into this repo. Sample datasets are either
  public-domain works or fully invented.
- Commits are authored as Gavin Wolfe (the git config is handled by the
  machine). Don't change the git identity.
- Contributions from AI agents arrive on branches and through pull requests,
  so a human reviews every change.

## Environment notes

- Windows development machine. The user runs Claude Code from pwsh 7, and
  Git Bash is also available.
- `.claude/settings.local.json` is gitignored and holds machine-specific
  environment settings. Never commit it.
