# Autonomous agent workflow: design

Design record, frozen on 2026-11-10 and never updated. It records the design as agreed with the
owner before it was built. The live description of how this repository is built is
`High-level/docs/agent-workflow.md`, and where the two differ, that document is right.

---

## Goal

Build the platform fast, so its functionality can be tried and iterated on. The owner works as a
project manager: he describes what he wants in planning sessions, and agents implement, review and
merge it into `main` without him in the loop. He judges the result by using it, and steps in only
where a slice is high risk or an agent escalates.

Two things decide how well this works, and the design spends its effort on both: the quality of
the input an agent gets, and how much an agent can verify by itself.

## Constraints

- The Mac mini runs every agent, as the bot `mac-mini-agent`, which keeps Write and never Admin.
- GitHub issues and Project 5 are the source of truth for work.
- Slices are worked one at a time. The stack's ports allow one running stack, and a serial runner
  that never idles is enough.
- The merge decision is made by deterministic code, never by a language model.

## Overview

```
/plan-feature (owner + agent, laptop)
        │  feature issue + Ready slices
        ▼
run.sh (Mac mini, tmux) ── loop ──────────────────────────────┐
  pick work → /pick-up-slice → PR → reviewer → verdict          │
                    ▲                              │            │
                    └──── changes (≤ 3 rounds) ────┘            │
                                   approve → merge-gate.sh → merge → next
```

## 1. Planning: `/plan-feature`

Runs on the laptop with the owner. The owner describes what he wants in his own words, as loosely
as he likes, and never needs to think in slices. The skill:

1. Reads the docs, open issues and code the feature touches.
2. Asks questions one at a time, each with a proposal and a recommendation, until nothing is open:
   behaviour, edge cases, UI expectations, scope, new dependencies with versions, doc changes.
3. Cuts the feature into slices, gives each a risk tier, and orders them with blocked-by links.
4. Shows every GitHub change as a dry run, applies it on the owner's yes, verifies each with a
   read, and sets the slices to Ready.

A slice whose acceptance cannot be written as tests is not Ready.

## 2. Tickets

Two levels. A **feature** is a parent issue. Its **slices** are sub-issues, and each slice is one
branch and one pull request. Everything an agent needs is in the slice body:

```markdown
## Goal
One or two sentences, user visible.

## Context
Doc passages and code it builds on, linked.

## Acceptance
Each criterion as a named test the agent writes first (cargo, pytest or Playwright), and the
screenshots to capture.

## Tasks
- [ ] Ordered checklist, one commit each

## Settled
Decisions made in planning, new dependencies with versions, doc changes to make.

## Out of scope

## Risk
low | high, and why if high.
```

**Size:** one pull request an agent finishes in one run and a reviewer judges in one read, about 1
to 5 tasks and a few hundred lines excluding tests.

**High risk** means auth and token logic, a migration (migrations are forward only), a change to a
contract between the core, the brain and features, or anything the docs call expensive to reverse.

**Board statuses:** Backlog, Ready, In progress, In review, Blocked, Done. The feature parent
replaces the Sprint field.

## 3. Implementation and verification: `/pick-up-slice`

Called headless by the runner, once per slice, in a fresh context.

1. **Preflight**: bot identity, clean tree, stack up, migrations applied. A failure sets the slice
   to Blocked, mentions the owner, and returns.
2. **Re-read** the slice, its linked docs, and what landed on `main` since it was planned. If
   something changed underneath the plan, adapt and record how.
3. **Branch** `<n>-<slug>` from `main`, status In progress.
4. **Tests first**: write the acceptance tests the slice names, and watch them fail for the right
   reason.
5. **Tasks** in order, one commit each: a subject, a short body, `Refs #<n>`. Tick the checklist
   as each lands.
6. **Gate**: exactly what CI runs, including the acceptance suite against
   `https://platform.test`. Loop until green.
7. **Explore and capture**: for UI work, drive the browser with Playwright, look at the result at
   desktop and 390px phone width, before and after for changed pages, and push the screenshots
   with `screenshots.sh`.
8. **Pull request** opened, status In review.

**Decisions mid-run.** The agent never blocks on a design question. It decides, and records the
decision in the pull request under "Decisions the agent made". A decision on high risk ground adds
the label `needs-owner`, which overrides auto-merge.

**Stops** are only for what deciding cannot solve: a missing `.env`, broken infrastructure, CI
failing for a reason outside the branch. The slice goes to Blocked with a mention, and the runner
moves on.

**Pull request body:** Summary; Decisions the agent made; Verification, the test output and the
embedded screenshots; Doc changes.

### Verification tooling

- **`e2e/`**: a `uv` project with `pytest-playwright` and Chromium, holding the acceptance suite.
  It runs against the stack through Caddy, with TLS errors ignored in the browser, since Caddy's
  TLS is covered by `curl --cacert`.
- **`.github/workflows/e2e.yml`**: brings up the compose stack in Actions with `platform.test` in
  `/etc/hosts` and runs the suite, on pull requests that touch service code.
- **`ci-ok`**: an aggregator job that always runs and passes when every relevant job passed or was
  skipped by its path filter. It is the one required check.
- **Screenshots**: on the orphan branch `pr-screenshots`, at `<pr>/<name>.png`, embedded as
  `https://github.com/Adamniels/Personal-platform/blob/pr-screenshots/<pr>/<name>.png?raw=true`,
  which renders for anyone signed in with access. GitHub has no API for attaching images, so they
  are committed. The branch is disposable.

## 4. Review, iteration and merge

**The reviewer** is a fresh headless session running `.claude/agents/reviewer.md`, which judges
rather than teaches. It reads the slice body, the whole diff, the CI results, the screenshots and
the linked docs, and posts a verdict comment with a machine readable marker: `approve` or
`changes`, each finding **blocking** or **nit**, with file, line and reason.

**The fix loop:**

1. On `changes`, the runner calls `/pick-up-slice <n>` in a fresh context, which sees the open
   pull request and an unanswered verdict and works in fix mode.
2. Every blocking finding is fixed, test first where behaviour changes, one commit each, "Address
   review: <what>", or answered with reasoning when the agent disagrees. Nits are fixed when cheap
   and in files already changed, and listed otherwise. Gate, push.
3. A new reviewer reads the whole diff again, with the earlier findings and replies.
4. At most 3 rounds. Blocking findings left after that add `needs-owner` and a mention, and the
   runner moves on.

Only blocking findings start another round.

**`merge-gate.sh`** merges with `gh pr merge --merge` only when `ci-ok` is green, the latest
verdict is `approve`, the slice is low risk, and `needs-owner` is absent. Then the status goes to
Done and the runner pulls `main`. Merge commits keep `git log --first-parent main` at one entry
per slice. Claude sessions keep a deny rule on `gh pr merge`, so only the gate merges.

**High risk** pull requests go through the same loop, then wait. The owner merges them on GitHub,
or comments, and his comments are handled like a `changes` verdict.

**The ruleset "Protect main":** pull request required, `ci-ok` required, no required approval,
merge commits only, no force push or deletion of `main`, no bypass for the bot.

## 5. The runner: `scripts/agent/run.sh`

Runs in tmux on the Mac mini, started by the owner after a planning session. Each iteration picks
work in this order:

1. a pull request in fix mode, with a `changes` verdict or the owner's comments;
2. a slice left In progress by a crashed run, resumed from its branch;
3. the next Ready slice from `next.sh`.

With nothing pickable, it sleeps 10 minutes and looks again.

- Each `claude -p` call runs under a timeout of about 90 minutes. A timeout or crash is resumed on
  the next iteration, and a second consecutive failure sets Blocked with a mention.
- Each run logs to `~/agent-logs/<n>-<timestamp>.log`.
- `~/.agent-stop` ends the runner after the current step.
- A slice blocked by a high risk pull request waiting on the owner stays Blocked in `next.sh`, and
  the runner takes other Ready work.

**The owner is mentioned** when a slice goes to Blocked, when a pull request still has blocking
findings after 3 rounds, when a high risk or `needs-owner` pull request is ready, and when the
queue empties.

## 6. Changes

**Rewritten:** `docs/README.md`, its description of `agent-workflow.md` and its layout table
(`e2e/`, `docs/superpowers/specs/`); `High-level/docs/agent-workflow.md`; the checks and tests parts of
`High-level/docs/how-we-build.md`; `AGENTS.md` (`CLAUDE.md`); `.claude/skills/pick-up-slice/`;
`.claude/agents/reviewer.md`; `.claude/settings.json`; the memories `small-nested-tickets` and
`github-board`; `scripts/agent/next.sh` and its tests.

**New:** `.claude/skills/plan-feature/`; `scripts/agent/run.sh`, `merge-gate.sh`,
`screenshots.sh`; `e2e/`; `.github/workflows/e2e.yml` and the `ci-ok` job.

**Deleted:** `.claude/skills/plan-slice/`; the memory `first-time-teaching`.

**Outside the repository:** the ruleset; Blocked on the board and the Sprint field replaced; the
`pr-screenshots` branch; the `needs-owner` label; Chromium on the Mac mini; the owner's
`project-context/personal-platform/overview.md` and its line in `NOW.md`.

Nothing in the repository points back to the earlier workflow.
