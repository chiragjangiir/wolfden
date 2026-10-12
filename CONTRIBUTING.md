# Contributing to Wolfden

Thanks for your interest. Wolfden is in early design and there's no code yet,
so the most useful contributions right now are ideas, questions, and feedback
on [`docs/design.md`](docs/design.md).

## Issues

- **Bugs, ideas, and design proposals:** use the
  [issue forms](https://github.com/gavin-wolfe/wolfden/issues/new/choose).
  A blank issue is fine too.
- **Questions:** open an issue labeled `question`.
- Search existing issues first. The backlog is organized by milestone.

## Branches and pull requests

- Work on a branch, never directly on `main`. Name it by kind and topic, for
  example `docs/contributing` or `feat/index-rebuild`.
- Open a pull request and link the issue with `Closes #N`. Keep each PR to
  one change.
- Every PR is reviewed before it merges. PRs merge with a merge commit, and
  the branch is deleted afterwards.
- A CI privacy check scans every PR for personal file paths. In docs, write
  `~` or `%USERPROFILE%` instead of a real home directory.

## AI-assisted contributions

AI-assisted contributions are welcome under the same rules: on a branch,
through a PR, and reviewed. If an AI tool wrote a meaningful part of your PR,
say so in the PR description (the template has a checkbox). You're
responsible for what you submit, so read it before you open the PR.

The maintainer also uses AI agents in this repo. Anything they post starts
with a line like `> 🤖 **Claude (Developer)**, on Gavin's behalf`, so you can
always tell who wrote what.

## Sample data only

Never commit real manuscripts, notes, or personal details, yours or anyone
else's. Sample data is either from a public-domain work or fully invented.
Sample datasets are licensed under CC-BY-4.0, and code under [MIT](LICENSE).

## Commits

- Write a short, imperative subject line ("Add issue templates"), and explain
  the why in the body when it isn't obvious.
- A DCO `Signed-off-by:` line (`git commit -s`) is welcome but not required.
  By opening a PR, you agree that your contribution is licensed under the
  project's licenses (MIT for code, CC-BY-4.0 for sample data).
- Use LF line endings and UTF-8. `.editorconfig` handles this in most editors.

## Proposing format or design changes

The on-disk format (folders, frontmatter fields, section markers) is what
users' notes depend on, so changes to it get discussed first.

1. Open an issue with the **Format or design proposal** form. It applies the
   `area: format` label.
2. If the proposal changes a recorded decision (the decisions list in
   `CLAUDE.md` or a "Decided" note in `docs/design.md`), the maintainer adds
   the `decision` label. Those always need the maintainer's sign-off.
3. Once agreed, the PR updates `docs/design.md` (and later `docs/format.md`,
   once the format spec exists) in the same change.
