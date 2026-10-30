# Working in this repository

Instructions for any coding agent working here. `CLAUDE.md` is a symlink to this file, so Claude
Code and Codex read the same thing.

This is a personal platform: a core, features built one at a time, and a brain. Agents write the
code, and its owner plans every slice and reviews every pull request, learning the code by
reviewing it. That shapes most of what follows.

## Where things are

Start at `docs/README.md`. It says how the documentation is organised, how firmly to read it, and
where each decision lives. Nothing is restated here, so read the docs rather than working from
memory of them.

Sprint scope and tickets are not in the docs. They live on GitHub, in the issues and in Project 5,
"Personal-platform-project". A sprint is a parent issue holding its checks, each slice is a parent
issue under it, and the leaves under a slice are the work. How work is cut and ordered is in
`High-level/docs/how-we-build.md`.

Project memory lives in `.claude/memory/`, indexed by `.claude/memory/MEMORY.md`. Read the index at
the start of a session. When something should outlive the session, propose adding it there rather
than writing it silently, and keep memory to what the docs and the code do not already record.

## AI and code

Agents implement, and the owner plans, reviews and merges. Who does what, from planning a slice to
merging its pull request, is in `High-level/docs/agent-workflow.md`.

- **Implementation runs through the workflow skills.** `/plan-slice` plans a slice with the owner,
  on the laptop, and `/pick-up-slice` works a slice that is Ready, on the Mac mini. Code written
  outside them, in an interactive session, is written when the owner asks for it.
- **Only the owner merges.** Never `gh pr merge`, never an admin bypass, even when asked: a pull
  request is approved by reading it, and the owner merges it after that.
- **A pull request is written to be learned from.** It explains what changed, in the order to read
  it, and why, as the template in `.claude/skills/pick-up-slice/templates.md` lays out.
- **Explaining stays a first class use.** Asked to explain or review, teach: concept first, then
  this repository's code, and ask questions. The `reviewer` agent does this for a pull request and
  never writes the fix.
- **No agent attribution.** No `Co-Authored-By` naming an agent and no "Generated with" lines, in
  commits or pull requests. The bot as author already says who wrote it.

## Working agreement

- **Git:** read freely. Commit and push only on a slice's branch, through the workflow skills or when
  the owner asks. Never push to `main`, never force push, and never rewrite a pushed commit. Commits
  follow the owner's convention: a subject, a short body, and `Refs #<leaf>`.
- **GitHub:** read freely. The workflow skills may move a board status, close a leaf, comment on an
  issue and open or update a pull request, as their steps say. Anything else, creating, editing or
  closing an issue among them, only when asked for that specific action, with the command shown
  first.
- **Ask first**, even mid-run: a deletion, a new dependency, or a change to the architecture.
- **Commands for the owner** run in zsh, which does not split an unquoted variable into words. Write
  flags out in full rather than keeping them in a shell variable.
- **One item at a time** in an interactive session: explain it, recommend, and wait before moving
  to the next. A pickup on the Mac mini runs without waiting, and stops only where its skill says.
- **When a doc and a plan disagree**, say so rather than picking one. The docs are tripwires, not
  law, see `docs/README.md`.
