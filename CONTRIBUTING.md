# Contributing to Wolfden

Thanks for your interest. Wolfden is in early design and there's no code yet,
so the most useful contributions right now are ideas, questions, and feedback
on [`docs/design.md`](docs/design.md).

## Issues

- **Bugs, ideas, design proposals, and questions:** use the
  [issue forms](https://github.com/gavin-wolfe/wolfden/issues/new/choose).
  Each form applies its label for you. A blank issue is fine too, and the
  maintainer will label it.
- Search existing issues first. The backlog is organized by milestone.

## Branches and pull requests

- Fork the repo and work on a branch in your fork, never directly on `main`.
  Name it by kind and topic, for example `docs/contributing` or
  `feat/index-rebuild`.
- Open a pull request and link the issue with `Closes #N`. Keep each PR to
  one change.
- Every PR is reviewed before it merges. PRs merge with a merge commit, and
  the branch is deleted afterwards.
- A CI privacy check scans every PR for personal file paths. In docs, write
  `~` or `%USERPROFILE%` instead of a real home directory.

### Privacy hooks (optional)

The repo ships git hooks that run the same checks locally, before a commit
exists: `pre-commit` scans staged files and `commit-msg` scans the message.
They also accept a private denylist, a plain text file kept **outside** the
repo, with one term per line (case-insensitive; blank lines and lines
starting with `#` are ignored). Matches are reported by file and line only,
never by term. To turn them on in your clone:

```sh
git config core.hooksPath .githooks
git config privacy.denylist "<path to your private list>"   # optional
```

`scripts/denylist-check.sh` checks a denylist without printing it: it flags
short terms, duplicates, and terms that already match a tracked file, by list
line number and match count. `-l` also shows where each match is.

If the list is configured but can't be read, the hooks block the commit
rather than skip the check.

## AI-assisted contributions

AI-assisted contributions are welcome under the same rules: on a branch,
through a PR, and reviewed. If an AI tool wrote a meaningful part of your PR,
say so in the PR description (the template has a line for it). You're
responsible for what you submit, so read it before you open the PR.

The maintainer also uses AI agents in this repo. Anything they post starts
with a line like `> 🤖 **Claude (Developer)**, on Gavin's behalf`, so you can
always tell who wrote what.

## Sample data only

Never commit real manuscripts, notes, or personal details, yours or anyone
else's. Sample data is either from a public-domain work or fully invented.
Sample datasets are licensed under CC-BY-4.0 (license file to come with the
first dataset), and code under [MIT](LICENSE).

## Commits

- Use [Conventional Commits](https://www.conventionalcommits.org/):
  `type(scope): summary`, with a short, imperative, lowercase summary.
  Common types are `feat`, `fix`, `docs`, `test`, `refactor`, `ci`, and
  `chore`. The scope is optional, for example `docs(format): add the
  position grammar` or `fix(index): keep CRLF line endings`. Mark a breaking
  change with `!` (`feat(format)!: ...`) or a `BREAKING CHANGE:` footer.
- Explain the why in the body when it isn't obvious.
- PR titles follow the same style, since the title goes into the merge
  commit. Older commits predate this convention and stay as they are.
- A [DCO](https://developercertificate.org) `Signed-off-by:` line
  (`git commit -s`) is welcome but not required.
  By opening a PR, you agree that your contribution is licensed under the
  project's licenses (MIT for code, MIT-0 for the built-in packs in
  `packs/`, CC-BY-4.0 for sample data).
- Use LF line endings and UTF-8. `.editorconfig` handles this in most editors.

## Proposing format or design changes

The on-disk format (folders, frontmatter fields, section markers) is what
users' notes depend on, so changes to it get discussed first.

1. Open an issue with the **Format or design proposal** form. It applies the
   `area: format` label, and the maintainer may change the area if the
   proposal is about something else (packs, the index, MCP, and so on).
2. If the proposal changes a recorded decision (the decisions list in
   `CLAUDE.md` or a "Decided" note in `docs/design.md`) or opens a new design
   question, the maintainer adds the `decision` label. Those always need the
   maintainer's approval.
3. Once agreed, the PR updates `docs/format.md` (the normative spec) and
   `docs/design.md` in the same change.
