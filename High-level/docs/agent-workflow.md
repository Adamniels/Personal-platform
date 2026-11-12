# Agent workflow

Status: exploratory. Built, and not yet run end to end on a real feature.

Last updated: 2026 11 12

How agents build this repository and what the owner does around them. How work is cut, features,
slices, the walking skeleton and what gets tests, is in `how-we-build.md`. This document says who
does each part of it and what keeps it safe. The rules agents follow are in `AGENTS.md`, and the
exact procedures are the skills and scripts this document points at.

---

## Why agents build, review and merge

The goal is to build the platform fast, so its functionality can be tried and iterated on. The
owner works as a project manager: he describes what he wants in a planning session, and agents
implement it, review it and merge it into `main` without him in the loop. He judges the result by
using it, and steps in only where a slice is high risk or an agent escalates.

Two things decide how well that works, and the workflow spends its effort on both. The first is
the quality of the input an agent gets: every question a slice could raise is settled with the
owner before an agent sees it, so nothing waits half a day for an answer. The second is how much an
agent can verify by itself: acceptance criteria are automated tests written first, the Playwright
suite in `e2e/` drives the real stack the way a person would, and a reviewer agent judges every
pull request before deterministic code decides whether it merges.

What it costs is that the owner does not read every line that lands. Understanding moves to
planning, where every decision is his, to using the platform, and to reading a pull request when he
chooses to. High risk work still waits for him. The bet is that a good plan and a strict gate catch
more than a tired read of every diff would.

## The life of a feature

| Stage | Who | Where | Result |
|---|---|---|---|
| Plan | Owner, with an agent | Laptop, `/plan-feature` | A feature issue and its slices, every question settled, the slices Ready |
| Pick | The runner | Mac mini, `scripts/agent/run.sh` | The most important work next, from `scripts/agent/pending.sh` |
| Build | An implementer agent | Mac mini, `/pick-up-slice <n>` | Tests first, one commit per task, the gate green, a pull request; status In review |
| Review | The reviewer agent | Mac mini | One verdict comment, `approve` or `changes`, for the head commit |
| Fix | An implementer agent | Mac mini, `/pick-up-slice <n>` again | Every blocking finding fixed or argued, one reply; at most 3 rounds |
| Merge | `scripts/agent/merge-gate.sh` | Mac mini | A merge commit on `main`; the slice closes; status Done |
| Owner | The owner | GitHub | Merges what waits for him, or comments, which starts a fix run |

**Board statuses** carry the handoffs: Backlog, Ready, In progress, In review, Blocked and Done.
Backlog means not planned, or planned with something still undecided. Ready means planned and free
for an agent, and an agent never starts anything that is not Ready. Blocked means a run hit a
problem in the slice itself and the owner has been told. How the board is set up is in the
`github-board` memory.

## Planning a feature

The owner describes a feature in his own words, as loosely as he likes, and never thinks in
slices. `/plan-feature` reads the docs, issues and code the feature touches, asks every open
question one at a time with a recommendation, then cuts the feature into slices, gives each a risk
tier, orders them with blocked-by links, and writes them to GitHub after a dry run. The procedure,
and the slice body every agent works from, are in `.claude/skills/plan-feature/SKILL.md`.

What the owner might build is kept in his planning notes in Notion, not here, see "Candidate
features" in `High-level/docs/platform-architecture.md`. Where the feature has a page there,
planning starts from it and ends by marking it planned with the feature issue's link. The page's
intent is copied into the feature issue, since the agents that build it cannot read Notion.

Planning happens ahead of the agent's run, and all of it with the owner, because an unattended run
has nobody to ask. A slice that still has a question in it stays in Backlog rather than leaving
the question for the agent to guess, and a slice whose acceptance cannot be written as named tests
is not Ready either.

**Slices are written, and only ever edited, by the owner's account.** The merge gate reads a
slice's `## Risk` to decide whether it may merge, so it trusts that tier only from an issue the
owner wrote and nobody else ever edited. That is why planning runs on the laptop as `Adamniels`, and
why no agent edits a slice body, not even to tick a task: the pull request body carries the Tasks
checklist and ticks it as each commit lands. What it costs is that the issue never shows progress.

**High risk** is ground where a mistake is silent or expensive to reverse: auth and token logic, a
migration, a contract between the core, the brain and features, and any slice whose main change is
in a path that steers the agents. The list is in step 3 of `plan-feature`. A high risk slice goes
through the same build and review loop and then waits for the owner.

## Building a slice

The runner calls `/pick-up-slice <n>` in a fresh `claude -p` session. The skill,
`.claude/skills/pick-up-slice/SKILL.md`, is the procedure, and it works in three modes: new, from
`main`; resume, a slice left In progress by an interrupted run; and fix, answering a `changes`
verdict or the owner's comments.

**One branch per slice**, `<n>-<slug>`, and one pull request that closes exactly that slice. The
gate refuses a pull request where the two disagree.

**Tests first.** The agent writes the acceptance tests the slice names, in cargo, pytest or
Playwright, and watches them fail for the right reason before writing the code. Tests follow "What
gets tests" in `how-we-build.md`.

**One commit per task**, with a subject and a short body saying what and why. Commits in a fix
round have the subject "Address review: <what>". No commit or pull request names an agent, since
the bot as author already says who wrote it.

**The agent decides rather than asks.** Where the slice's Settled section and the docs leave
something open, the agent decides it the way the docs lean and records it in the pull request under
"Decisions the agent made", with the alternative and why. A decision on high risk ground also gets
the label `owner-decision`, which keeps the pull request from merging until the owner has answered
it. The reviewer does not block on such a decision for being unconfirmed, since no fix round can
confirm it: it lists it for the owner with its own view, and blocks only when the label is missing.
How a doc that disagrees with the plan is handled is step 8 of `pick-up-slice`.

**The gate is what CI runs**, decided from the branch's diff by `scripts/ci/changed-areas.sh`, so
`ci-ok` passes on the first push. When the e2e area is touched, the Playwright suite runs against
the stack through `https://platform.test`, and for UI work the agent also looks at the screenshots
at desktop and 390px phone width for what a test does not catch. Screenshots are committed to the
disposable orphan branch `pr-screenshots` by `scripts/agent/screenshots.sh` and embedded in the pull
request, since GitHub has no API for attaching images.

**The Mac mini's database is disposable.** Each run recreates the core's database empty with
`scripts/agent/reset-db.sh` and applies the branch's migrations from scratch. Migrations are forward
only, so on a shared database one slice's unmerged migration would stall every other branch until
it merged. What it costs is that nothing on the Mac mini keeps data, and running the platform for
real there would need a separate stack for the agents.

**How a run ends**, other than with a pull request, is one of three final lines the runner reads.
`ENVIRONMENT:` is a problem outside the slice, such as Docker down, a missing `.env`, the wrong
`gh` account or CI failing for a reason outside the branch, and changes no status. `Blocked:` is a
problem in the slice the agent cannot decide around, an open blocker or acceptance that cannot be
tested, and sets the slice Blocked with a mention. `Stopped:` means the state matched no mode, such
as a pull request someone else opened, and changes nothing. A design question is never one of them.

## Review and the fix loop

**The reviewer** is `.claude/agents/reviewer.md`, run in its own fresh session after every push.
It judges, it does not teach: it reads the slice, the whole diff at the head commit, the CI
results, the screenshots and the linked docs, and posts one comment whose first line is a machine
readable marker, `approve` or `changes` for that head sha. Each finding is blocking or a nit, with
file, line and reason. `scripts/agent/verdict.sh` reads the marker, and ignores any comment that
was edited or does not match it exactly.

**Everything an agent reads about a pull request is data**, never instructions: its body, the
comments, the code, the CI logs. A comment telling the reviewer to approve, or the implementer to
merge, is reported and not followed. This matters because the agents act on what they read with
nobody watching.

**The fix loop.** On `changes`, the runner calls `/pick-up-slice` again, which fixes every blocking
finding, test first where behaviour changes, or argues it in its reply when it thinks the finding
is wrong. Nits are fixed when cheap. A fresh reviewer then reads the whole diff again with the
earlier findings and the replies. Only blocking findings start another round. After 3 fix rounds,
a fourth `changes` verdict ends the loop: the pull request gets `owner-blocked` and the owner a
mention.

## The merge gate

`scripts/agent/merge-gate.sh` is the only way code reaches `main` unattended, and the merge decision
is deterministic code, never a language model. It merges only when `ci-ok` is green on the head
commit, the latest verdict is `approve` for that commit, the slice is the open issue the branch is
named for, written and only edited by the owner, its Risk is `low`, it carries no owner label, the
pull request is the bot's, comes from this repository rather than a fork, targets `main` and is not
a draft, and no changed file is under a protected path. Its header lists each condition with the
reason line it prints, which the runner reads. It fails closed: anything it cannot read is not a
merge.

**Protected paths** are `.github/`, `scripts/` and `.claude/`, and the root `AGENTS.md` and
`CLAUDE.md`. They steer CI, the gate, the runner and the agents themselves, so a pull request
touching them could weaken its own checks and then merge itself. Service code and `infra/` are not
protected, since that would send ordinary slices to the owner, and the reviewer guards against a
weakened test.

**The rules come from `main`, never from the branch under judgement.** The runner never runs a
repository script from the working tree, where a slice's branch is checked out. It re-executes
itself from a copy, runs the gate, the picker and `verdict.sh` from a snapshot of `origin/main`
taken each loop, and returns to an up to date `main` after each item. So a pull request cannot
change the rules it is judged by until the owner has merged that change.

`main` takes merge commits only, so `git log --first-parent main` reads one entry per slice. The
gate merges with `--match-head-commit`, so a commit pushed after the checks cannot slip in, and then
sets the slice Done.

## What waits for the owner

A pull request waits for him when its slice is high risk, when it touches a protected path, when
the implementer flagged a decision on high risk ground, or when it is stuck. Its label says which,
and so what he does:

| Label | What it means | What the owner does |
|---|---|---|
| `owner-merge` | Approved, CI green, nothing open | Reads it and merges |
| `owner-decision` | The implementer decided something on high risk ground that Settled does not cover | Reads "Decisions the agent made" and the reviewer's "For the owner". Merging confirms the decisions, and a comment asking for a change starts a fix round |
| `owner-blocked` | Not mergeable as it is: the review rounds ran out, the slice was set Blocked, or the gate refused it for a reason such as a failed `ci-ok` | Does not merge. Reads the runner's mention for the reason, fixes the slice or the cause, and comments on the pull request to run it again |

When a pull request carries more than one, the most severe is the one to act on: `owner-blocked`,
then `owner-decision`, then `owner-merge`. `owner-decision` is the implementer's, set when it makes
the decision, and nothing removes it. `owner-merge` and `owner-blocked` are the runner's, and it
keeps them true: one replaces the other, and a pull request with `owner-decision` never gets
`owner-merge`. So an `owner-decision` pull request the reviewer has not approved yet is still in
the loop, and the runner's mention on the slice, "approved and waits for your decision", is the
signal that it is ready for him.

An owner label blocks merging and nothing else: every waiting pull request goes through review and
the fix rounds first, so what he reads has already been through the reviewer. The gate treats the
three alike and only reads them. It also refuses on `needs-owner`, the one label that pull requests
opened before 2026-11-12 carry instead, which the runner reads like `owner-decision`; that can go
once no open pull request carries it.

Once the reviewer approves a pull request that waits for him, the runner announces it to the owner
once per head commit. A pull request whose review rounds ran out is handed over once. He merges it
on GitHub, or comments, and a comment newer than the pull request's last handling starts a fix run
that answers it, followed by a new review. On a pull request whose review rounds ran out, a fix run
that only replies, with no new commit, gets no new review, and the pull request waits for him again.

The owner is mentioned on the slice when an approved pull request waits for him, once per head
commit, when a pull request's review rounds run out, when the gate refuses for any other reason,
when a slice goes Blocked, when an interruption has lasted over 30 minutes, and once when the queue
empties. A Blocked notice for a pull request's item goes on that pull request instead, and a usage
limit pause mentions him only once it has lasted 6 hours or hit 10 limits in a row on one item.

## The runner

`scripts/agent/run.sh` runs in tmux on the Mac mini and loops. Each turn it asks `pending.sh` for
the most important item: a pull request to fix, one whose rounds ran out, one to review, one to
merge, a slice to resume, the next Ready slice from `next.sh`, and last, an approved pull request to
announce to the owner. A slice blocked by a slice still waiting for the owner is not picked; the
runner takes other work. Implementer and reviewer each run in a fresh `claude -p` session under a
timeout of 90 minutes, so no run carries another's context. With nothing to do it waits 10 minutes
and looks again. The script's header is the exact behaviour.

**Interruptions pause, they never count against a slice.** A usage limit, a network or API outage,
or an `ENVIRONMENT:` ending pauses the runner and retries the same item, sleeping until a usage
limit resets or backing off between tries, and changes nothing on the board. Before every run it
probes GitHub, Docker and free disk, so a broken environment costs no usage. Only a failure about
the slice counts: a run of at least two minutes that fails, a `Blocked:` or `Stopped:` ending, or
the same item coming back with no progress. Two in a row set the slice Blocked with a mention. A
slice failing for reasons outside itself should not need the owner.

**Interrupted work is kept.** Before resuming or fixing, a dirty tree on the slice's branch is
committed locally as "Checkpoint: interrupted run", and the next run inspects, finishes and gates
it. All of the runner's state is on disk under `~/agent-logs/`, and a lock file keeps a second
runner from starting, so after a restart the owner starts the runner again and it carries on where
it was.

## One agent at a time

Slices are worked one at a time. The gate needs the whole stack, Caddy on 80 and 443, the core on
8080, canary on 8081 and Postgres on 5432, and two runs would collide on every one of them. Running
several at once would need a stack per run, separate ports and compose projects, and a git worktree
each with its own copy of the git ignored `.env` files. A serial runner that never idles while work
waits is enough until that changes.

## Safety

- **The bot.** The Mac mini works as `mac-mini-agent`, a collaborator with Write and never Admin,
  with Write on Project 5. It cannot change the ruleset or bypass it.
- **The ruleset** "Protect main" requires a pull request and the one status check `ci-ok`, requires
  no approval, allows merge commits only, forbids force pushing or deleting `main`, and gives the
  bot no bypass. Why one aggregating check is the required one is in "What it costs, and how it
  is paid" in `platform-architecture.md`.
- **One bot identity.** Implementer and reviewer both post as `mac-mini-agent`, so an implementer
  could in principle post its own approval. Three layers stop that: the runner starts implementer
  runs with `--disallowedTools` patterns refusing the marker text, comment edits and merges; it puts
  `scripts/agent/gh-guard.sh` first on their `PATH` as `gh`, refusing the same; and `verdict.sh`
  ignores an edited comment. A second account for the reviewer is the upgrade if a self approval
  ever shows up in the logs.
- **Deny rules** in `.claude/settings.json` refuse merging, running the gate, pushing to `main`,
  force pushing and any admin flag in every Claude session, so a confused agent fails rather than
  tries.
- **Auto mode.** Runs use Claude Code's auto permission mode, so routine commands do not wait for an
  answer nobody is there to give, and the deny rules still hold.

## When this would be the wrong call

Autonomy loses when merged slices keep needing rework the reviewer should have caught: a feature
the owner tries and finds broken in ways the acceptance tests and the review both passed. The sign
is follow up slices that fix what an earlier one shipped, more than once a feature. The first answer
is a better plan and a stricter reviewer; if that does not hold, high risk becomes the default tier,
so every pull request waits for the owner while the gate stays the same.

A second reviewer identity would win over the guards if an implementer run ever posts a verdict.
Running slices in parallel would win once the queue regularly holds more Ready slices than one
runner clears between planning sessions.

---

## Mac mini runbook

### Once, on GitHub (the owner)

1. Invite `mac-mini-agent` as a collaborator with Write, and accept the invitation as the bot.
2. Project 5, Settings, Manage access: add `mac-mini-agent` with Write. Repository access does not
   include the board.
3. The "Protect main" ruleset, as described under Safety, in the settings UI. The bot cannot read
   rulesets, so check it from the owner's account with
   `gh api repos/Adamniels/Personal-platform/rulesets`.
4. The labels `owner-merge`, `owner-decision` and `owner-blocked`, the Blocked status on the board, and "Automatically delete head
   branches" in the repository settings.
5. GitHub notifications on for mentions, since that is how the runner reaches the owner.

### Once, on the Mac mini

1. **Tools.** rustup with the stable toolchain, uv, Docker Desktop started at sign in with its
   automatic updates off and its memory at 2 to 4 GB (an update quits Docker Desktop, and it does
   not always start again; the runner starts it itself when its probe fails), `gh`, `jq`,
   tmux and Claude Code, with `~/.docker/bin` and `. "$HOME/.cargo/env"` in `~/.zprofile`. Docker
   Desktop's credential helper needs the login keychain, which a plain SSH session cannot unlock,
   so a new image is pulled by hand once, over `ssh -t` so the unlock can ask for the password,
   with `~/.docker/bin` on `PATH` so docker finds the helper:
   `ssh -t <mac-mini> 'security unlock-keychain ~/Library/Keychains/login.keychain-db && export PATH="$HOME/.docker/bin:$PATH" && docker pull postgres:18 && docker pull caddy:2'`.
   Compose uses the images from then on. The Mac mini should not sleep, and its clock should stay
   synced, since the runner compares its time with GitHub's.
2. **Hostname.** The `/etc/hosts` line from `infra/README.md`.
3. **Clone.** `git clone https://github.com/Adamniels/Personal-platform.git ~/dev/Personal-platform`
4. **Log in as the bot.** `gh auth login` with the bot account and the scopes `repo`, `workflow`,
   `project` and `read:org`, then `gh auth setup-git`, so pushes use the bot.
5. **Git identity, this clone only.**
   ```zsh
   git config user.name "mac-mini-agent"
   git config user.email "340709305+mac-mini-agent@users.noreply.github.com"
   ```
6. **Environment.** Copy `infra/.env.example` to `infra/.env` and `Core/.env.example` to
   `Core/.env`, and fill in the passwords. Then give this machine its own signing key, from `Core/`:
   `cargo run -q -- keygen >> .env`. They are local to this machine and never leave it.
7. **Stack.** From `infra/`, `docker compose up -d --wait`. Each run resets the database and applies
   the migrations itself.
8. **Browser.** From `e2e/`, `uv run playwright install chromium`.
9. **Caddy's root.** Not trusted in the keychain here, since an SSH session cannot change trust. The
   agent copies the root out of the volume and passes it to `curl` with `--cacert`, see "Trusting
   Caddy's root" in `infra/README.md`, and the browser in `e2e/` ignores TLS errors.
10. **Claude Code.** Log in once as the bot's macOS user, at the machine or over `ssh -t`. Claude
    Code keeps its credentials in the login keychain, so over SSH unlock it in the tmux session the
    runner will use, as for Docker: `security unlock-keychain ~/Library/Keychains/login.keychain-db`.
    Then check that `claude -p "say ok" --permission-mode auto` answers over SSH before starting
    the runner.
11. **Automatic login** of the bot's macOS user, in System Settings, since Docker Desktop only
    starts at a sign in.

### Check

In the clone, each of these holds. Every implementer run checks the first three itself and ends
with `ENVIRONMENT: not the bot` if one fails; the reviewer checks none of them:

- `gh api user --jq .login` prints `mac-mini-agent`
- `git config user.email` ends with `+mac-mini-agent@users.noreply.github.com`
- `git config --get-all credential.https://github.com.helper` includes `gh auth git-credential`

The board access is checked by hand once, since no run checks it before its first board change:
`gh project view 5 --owner Adamniels --format json --jq .title` prints the board's title.

### Planning, on the laptop

`/plan-feature` in a Claude Code session in the owner's clone, with `gh` logged in as `Adamniels`.
Any later change to a slice body is made from that account too, or the gate refuses the slice.

### Running

```zsh
ssh -t <mac-mini>
tmux new -s runner
security unlock-keychain ~/Library/Keychains/login.keychain-db
cd ~/dev/Personal-platform
caffeinate -is bash scripts/agent/run.sh
```

Start `run.sh` as the clone is. It checkpoints any interrupted work and returns to an up to date
`main` by itself, so never `git switch main` over a dirty tree first. Only on the first start in a
fresh clone, switch to `main` before it. Detach with `Ctrl-b d`; the runner keeps going.
`caffeinate` keeps the machine awake while it runs.

- **Stop:** `touch ~/.agent-stop`, and the runner ends after its current step. Remove the file
  before starting it again, or it stops at once.
- **Logs:** `~/agent-logs/runner.log` for what the runner decided, and one
  `<slice>-<timestamp>.log` per agent run.
- **After a reboot:** nothing starts the runner by itself. First the bot's macOS user must be signed
  in again, by automatic login, so Docker Desktop starts; then unlock the login keychain in the
  tmux session; then start `run.sh` in tmux as above. It resumes what was In progress and keeps
  checkpointed work.
- **Already running:** a second `run.sh` exits with "already running" while the first holds the
  lock.

### Troubleshooting

| Symptom | Fix |
|---|---|
| A run ends with `ENVIRONMENT: not the bot` | Redo steps 4 and 5 in the clone |
| A board change fails with a scope error | `gh auth refresh -s project` |
| A board change fails with "Could not resolve to a ProjectV2" | The bot has no access to the board, GitHub step 2 |
| Runs fail at once asking to log in, over SSH | Unlock the login keychain in the runner's tmux session, step 10 |
| `curl` reports an expired certificate after the Mac mini was off | "After Caddy has been stopped for a while" in `infra/README.md` |
| The runner pauses and mentions an environment problem | Read the last agent log in `~/agent-logs/`; fix what it names, and the runner carries on |
| A slice was set Blocked | Read its Blocked notice and fix the slice from the owner's account, or the cause. Then, by what exists: with an open pull request, comment on it, and the runner moves the slice to In review; with a branch that has commits but no pull request, set the slice In progress; with neither, set it Ready. `owner-blocked` stays on the pull request until the reviewer approves it again, when the runner replaces it with `owner-merge`, or drops it where `owner-decision` is set. The pull request waits for the owner's merge either way |
| Every pull request waits with "risk record is not the owner's" | The slices were created or edited from another account; recreate them as `Adamniels` |
