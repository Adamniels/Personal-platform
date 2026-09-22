# Working in this repository

Instructions for any coding agent working here. `CLAUDE.md` is a symlink to this file, so Claude
Code and Codex read the same thing.

This is a personal platform: a core, features built one at a time, and a brain. Its owner is
building it to learn, and that shapes most of what follows.

## Where things are

Start at `docs/README.md`. It says how the documentation is organised, how firmly to read it, and
where each decision lives. Nothing is restated here, so read the docs rather than working from
memory of them.

Sprint scope and tickets are not in the docs. They live on GitHub, in the issues and in Project 5,
"Personal-platform-project". A sprint is a parent issue holding its checks, each slice is a parent
issue under it, and the leaves under a slice are the work. How work is cut and ordered is in
`High-level/docs/how-we-build.md`.

## AI and code

The owner writes this repository's code by hand while learning it. What AI is for here is planning,
explaining, and reviewing.

- **Do not write code for a ticket unless asked for that specific piece.** Once the owner knows how
  to write something, they may ask for it to save repetition. That is the owner's call, never a
  default.
- **Explaining may include a generic example** of an API or technique, which is not this
  repository's code and not an answer to the ticket at hand.
- **A review describes what is wrong and why, and asks questions.** It does not supply the fix
  unless asked.
- **When asked to change code, make small edits and explain each one.** Every change to a file that
  is not Markdown waits for the owner's approval, one at a time, enforced for Claude Code by
  `.claude/hooks/ask-on-code.py`. Never write code files through the shell to get around that.
  Codex has no such hook, so for Codex this rule is the only guard.
- **Docs, issues and plans may be edited when asked**, and follow the session's normal permissions.

## Working agreement

- **Git:** read freely. Never stage, commit or push. When a piece of work lands, suggest a commit
  message and stop.
- **GitHub:** read freely. Never create, edit or close anything unless asked for that specific
  action, and show the command first.
- **Commands for the owner** run in zsh, which does not split an unquoted variable into words. Write
  flags out in full rather than keeping them in a shell variable.
- **One item at a time.** Explain it, recommend, and wait before moving to the next.
- **When a doc and a plan disagree**, say so rather than picking one. The docs are tripwires, not
  law, see `docs/README.md`.
