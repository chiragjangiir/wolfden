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
- Contributions from AI agents arrive on branches and through pull requests.
  See "Agent workflow" below for who reviews and merges them.

## Agent workflow

Agents may develop, review, and merge routine changes. The maintainer is
brought in only when a change needs human judgment.

**Roles**

- **Orchestrator:** picks the next Ready issue from the project board,
  moves its card, and assigns the work.
- **Developer:** implements the change on a branch (in its own git worktree
  when running alongside other agents) and opens the PR with `Closes #N`.
- **Reviewer:** a separate agent with fresh context. It sees the issue, the
  diff, and the repo, but not the Developer's reasoning. Its brief is
  adversarial: try to break the change, check it against `docs/design.md`
  and this file, and rank findings by severity.

**Loop:** the Developer fixes or rebuts each finding, for at most three
rounds. If the Reviewer is satisfied and CI is green, the Orchestrator merges
(merge commit). Otherwise it escalates.

The maintainer's time goes to writing first, so the default is to merge.
Escalate only when a change needs the maintainer's judgment or can't be
undone.

**Escalate to the maintainer (never auto-merge):**

- **Design decisions with real tradeoffs:** choosing between options that
  differ in what writers see or type (the on-disk format, the domain model,
  query semantics), or changing a recorded decision in `docs/design.md`.
  Present the options with a recommendation.
- **Agent rules:** changes to this file or other agent instructions.
- **Licensing,** including adding a dependency that isn't under MIT, Apache
  2.0, BSD, or a similar permissive license.
- **Security and access:** secrets, tokens, repo settings, Actions
  permissions, and workflows that use secrets or deploy anything.
- **Identity and privacy:** anything that could expose the maintainer's real
  identity or private notes.
- **The project's public voice:** the README pitch and positioning, the repo
  description, and anything posted outside this repo.
- **Outward or irreversible actions:** releases, tags, publishing packages,
  and deleting branches that aren't merged.
- **Disagreement** between Developer and Reviewer after the round limit.

**Auto-merge everything else** once CI is green and the Reviewer is
satisfied. That covers implementation, tests, refactors, docs that follow
recorded decisions (including typo and accuracy fixes to the README), CI
that doesn't touch secrets, and permissively licensed dependencies (the
Reviewer checks the license). Small design choices made while implementing
are fine. Record them in the PR description so the maintainer can revisit
them.

**To escalate,** leave the PR open, say in a PR comment what needs deciding
(with a recommendation), and move the card to In Review. Batch decisions
where possible. One digest of three questions is better than three
interruptions. Work that doesn't depend on the answer keeps going.

**At the end of a session,** the Orchestrator gives the maintainer a short
summary: what merged, which design calls were made along the way, and what's
waiting on the maintainer.

### Marking AI-authored content

Agents post as the maintainer's GitHub account, so everything they post must
be visibly marked. Issue and PR bodies, comments, and reviews **start** with:

```markdown
> 🤖 **Claude (<Role>)**, on Gavin's behalf
```

`<Role>` is Orchestrator, Developer, or Reviewer. An interactive session
working alone uses plain `Claude`. The marker goes first so it shows in
notifications and email previews. PR bodies also end with the "Generated with
Claude Code" footer, and commits keep the `Co-Authored-By` trailer.

### Commit messages

Commits and PR titles use Conventional Commits (`type(scope): summary`),
as `CONTRIBUTING.md` describes, for example
`docs(format): make a missing id a warning`. Branch names keep the
`kind/topic` form (`docs/format-spec`). Don't rewrite older commits to match.

### Privacy check

`.github/workflows/privacy-check.yml` fails a PR or push to `main` when any
tracked file contains a personal user path or a term from the
`PRIVACY_DENYLIST` repository secret. In docs, write `%USERPROFILE%` or `~`
instead of a real user path.

## Environment notes

- Windows development machine. The user runs Claude Code from pwsh 7, and
  Git Bash is also available.
- `.claude/settings.local.json` is gitignored and holds machine-specific
  environment settings. Never commit it.
