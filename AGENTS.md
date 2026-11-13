# Working in this repository

Instructions for any coding agent working here. `CLAUDE.md` is a symlink to this file, so Claude
Code and Codex read the same thing.

This is a personal platform: a core, features built one at a time, and a brain. Its owner plans
every feature with an agent, and agents implement, review and merge it unattended, so he can try
the result and iterate. That shapes most of what follows.

## Where things are

Start at `docs/README.md`. It says how the documentation is organised, how firmly to read it, and
where each decision lives. Nothing is restated here, so read the docs rather than working from
memory of them.

Feature scope and tickets are not in the docs. They live on GitHub, in the issues and in Project 5,
"Personal-platform-project". A feature is a parent issue, each slice is a sub-issue of it with one
branch and one pull request, and the slice's task checklist is the work. How work is cut and
ordered is in `High-level/docs/how-we-build.md`.

Project memory lives in `.claude/memory/`, indexed by `.claude/memory/MEMORY.md`. Read the index at
the start of a session. When something should outlive the session, propose adding it there rather
than writing it silently, and keep memory to what the docs and the code do not already record.

## AI and code

Agents implement, review and merge through the merge gate. The owner plans every feature, reviews
the high risk pull requests, and merges those himself. Who does what, from planning a feature to
merging its slices, is in `High-level/docs/agent-workflow.md`.

- **Implementation runs through the workflow skills.** `/plan-feature` plans a feature with the
  owner, on the laptop, and `/pick-up-slice` works a slice that is Ready, on the Mac mini, called by
  the runner, `scripts/agent/run.sh`. `/review-feature` reads a whole feature after its slices
  have merged, on the laptop, only when the owner starts it. Code written outside them, in an
  interactive session, is written when the owner asks for it.
- **Only the merge gate merges.** `scripts/agent/merge-gate.sh` is run by the runner, never by an
  agent. Never `gh pr merge`, never an admin bypass, even when asked. What the gate does not merge,
  the owner merges on GitHub.
- **Unattended runs decide and record, they do not ask.** Nobody answers a question on the Mac mini.
  Where the slice and the docs leave something open, decide it the way the docs lean and record it
  in the pull request under "Decisions the agent made", with `has-decision` only in the three cases
  where the owner must answer, as step 8 of `.claude/skills/pick-up-slice/SKILL.md` says.
- **A pull request explains itself.** It says what changed, in the order to read it, and why, as
  the template in `.claude/skills/pick-up-slice/templates.md` lays out, so the reviewer and the
  owner can judge it from the body and the diff alone.
- **The `reviewer` agent judges, it does not teach.** It posts one verdict, `approve` or `changes`,
  that the merge gate reads, and never writes the fix.
- **Explaining stays a first class use.** Asked by the owner to explain code or a pull request in an
  interactive session, teach: concept first, then this repository's code, and ask questions.
- **No agent attribution.** No `Co-Authored-By` naming an agent and no "Generated with" lines, in
  commits or pull requests. The bot as author already says who wrote it.

## Working agreement

- **Git:** read freely. Commit and push only on a slice's branch, through the workflow skills or
  when the owner asks. Never push to `main`, never force push, and never rewrite a pushed commit.
  Commits follow the owner's convention: a subject and a short body saying what and why, one commit
  per task.
- **GitHub:** read freely. The workflow skills may move a board status, comment on an issue or pull
  request, add `has-decision`, and open or update a pull request, as their steps say. Every other
  label on a pull request, and the `review-ok` status, are the runner's. Slices are
  created and edited only from the owner's account, through `/plan-feature`; an agent on the Mac
  mini never edits a slice's body, title or labels, since the merge gate trusts a slice's Risk only
  while the owner is its only editor. Anything else, creating, editing or closing an issue among
  them, only when asked for that specific action, with the command shown first.
- **Paths that steer the agents**, `.github/`, `scripts/`, `.claude/`, `AGENTS.md` and `CLAUDE.md`,
  change in an unattended run only when the slice names them, and the merge gate leaves every pull
  request touching them to the owner.
- **New dependencies, deletions and architecture changes** are settled with the owner in planning.
  In an interactive session, ask first. In an unattended run, one the slice did not settle is
  decided and recorded like any other decision, and is high risk where the docs call it expensive
  to reverse.
- **Commands for the owner** run in zsh, which does not split an unquoted variable into words. Write
  flags out in full rather than keeping them in a shell variable.
- **One item at a time** in an interactive session: explain it, recommend, and wait before moving
  to the next. A run on the Mac mini never waits, and ends early only where its skill says.
- **When a doc and a plan disagree**, weigh it by the cost of undoing it: the docs are tripwires,
  not law, see `docs/README.md`. In an interactive session, say so rather than picking one. In an
  unattended run, follow step 8 of `.claude/skills/pick-up-slice/SKILL.md`.
