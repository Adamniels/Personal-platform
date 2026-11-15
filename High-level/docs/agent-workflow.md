# Agent workflow

Status: exploratory. Built, and not yet run end to end on a real feature.

Last updated: 2026 11 15

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
chooses to. What he marks high risk still waits for him. The bet is that a good plan and a strict gate catch
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

Something built in several rounds, such as the brain, is one page for the area with a page per
part under it. A feature issue is one part. What a plan leaves out is written in the feature issue
under "Not in this feature", each line with what it waits for, and planning ends by making each
of those lines a part in the notes. So the notes always say what is built, what is being built and
what is left, and why each remaining part waits. The order across the platform is a number on each
page there, set by the owner.

Planning happens ahead of the agent's run, and all of it with the owner, because an unattended run
has nobody to ask. A slice that still has a question in it stays in Backlog rather than leaving
the question for the agent to guess, and a slice whose acceptance cannot be written as named tests
is not Ready either.

**Slices are written, and only ever edited, by the owner's account.** The merge gate reads a
slice's `## Risk` to decide whether it may merge, so it trusts that tier only from an issue the
owner wrote and nobody else ever edited. That is why planning runs on the laptop as `Adamniels`, and
why no agent edits a slice body, not even to tick a task: the pull request body carries the Tasks
checklist and ticks it as each commit lands. What it costs is that the issue never shows progress.

**A slice's risk tier is the owner's choice**, made in planning. High means he wants to read the
pull request before it lands, and it then waits for him after the same build and review loop. The
default is low, also on ground where a mistake is silent or expensive to reverse: auth and token
logic, a migration, a contract between the core, the brain and features. That ground was high by
default until 2026 11 12. It changed because the reviewer and the tests were doing the checking
and his read of every such pull request added little to them, while each one stopped the runner.
What still waits for him regardless of tier is a pull request touching a path that steers the
agents, see "The merge gate", and a decision the implementer may not make alone, which is no
longer a matter of the ground it is on, see "Building a slice". The exact rule is in step 3 of
`plan-feature`.

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
something open, the agent decides it and records it in the pull request under "Decisions the agent
made", with the alternative and why. It sets the label `has-decision`, which keeps the pull request
from merging until the owner has answered, and marks the decision **owner-decision** in the body,
only in three cases:

1. it goes against something the slice's Settled or a doc says;
2. it changes what the owner sees or does as a user in a way the slice did not plan;
3. it is a matter of the owner's taste, where no option is better on the merits.

Being about auth, a migration or a contract is no longer a reason by itself. Until 2026 11 13 it
was, and every backend slice of the brain then asked for the owner, on details such as the fields
of an API answer: the rule fired on where a decision was, not on how much it mattered.

Every other decision is made by a written standard, and the reviewer judges each recorded decision
against it:

- correct and secure first, and on security the stricter option when unsure;
- what holds up in the long run over what is quickest for this slice;
- consistent with the code and the docs that exist;
- simple, and easy to change later where that is possible.

A decision that does not meet the standard is a blocking finding, argued on its merits. A decision
marked for the owner is not blocked for being unconfirmed, since no fix round can confirm it: the
reviewer lists it for him with its own view, and blocks only when one of the three cases applies
and `has-decision` is missing. How a doc that disagrees with the plan is handled is step 8 of
`pick-up-slice`.

**The gate is what CI runs**, decided from the branch's diff by `scripts/ci/changed-areas.sh`, so
`ci-ok` passes on the first push. When the e2e area is touched, the Playwright suite runs against
the stack through `https://platform.test`, and for UI work the agent also looks at the screenshots
at desktop and 390px phone width for what a test does not catch. Screenshots are committed to the
disposable orphan branch `pr-screenshots` by `scripts/agent/screenshots.sh` and embedded in the pull
request, since GitHub has no API for attaching images.

**The Mac mini's dev databases are disposable.** Each run recreates every service's database in
the dev stack empty with `scripts/agent/reset-db.sh` and applies the branch's migrations from scratch.
Migrations are forward only, so on a shared database one slice's unmerged migration would stall
every other branch until it merged. What it costs is that nothing in the dev stack keeps data. The
machine now also holds the hosted copy, the platform run for real, as a separate compose project,
`platform-hosted`, released from its own clone, `~/platform`, see `infra/hosted/README.md`. No run
touches it: the reset addresses only the dev project, which `reset-db.test.sh` holds, and the agents'
own check of the containers, `infra/hosted/check.sh`, runs as `hostedcheck`.

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

**An approve is settled in the same turn.** When a review ends in `approve` for the head commit,
the runner sets `review-ok` and asks the merge gate straight away, before it looks for any other
work: review, then gate, then merged or handed over, as one unit. The pull request is merged, or
handed to the owner at once with its owner label and a mention, see "What waits for the owner".
Only a `ci-ok` that has not reported yet postpones this: the turn ends without waiting, and the
pull request is settled by its `merge` or `owner` item once CI has reported.

## The merge gate

`scripts/agent/merge-gate.sh` is the only way code reaches `main` unattended, and the merge decision
is deterministic code, never a language model. It merges only when `ci-ok` is green on the head
commit, the latest verdict is `approve` for that commit, the slice is the open issue the branch is
named for, written and only edited by the owner, its Risk is `low`, it carries no owner label and
no `has-decision`, the pull request is the bot's, comes from this repository rather than a fork,
targets `main` and is not a draft, and no changed file is under a protected path. Its header lists
each condition with the reason line it prints, which the runner reads. It fails closed: anything it
cannot read is not a merge.

**`has-decision` refuses like an owner label**, with the same reason line, `an owner label is
set`, so a decision the implementer marked for the owner cannot merge unread. `in-review` never
refuses: it only says where a pull request is.

**A pull request that is already merged gets one answer**, the single reason line `pull request is
already merged`, and nothing else is read. GitHub's listing can hand the runner a pull request the
gate merged a moment ago, and its slice is closed by then, so the gate used to answer "slice is not
open" and the runner told the owner not to merge something that was already on `main`.

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
the implementer marked a decision for him, or when it is stuck. **One rule says when it is his
turn: an owner label and a green merge button mean go.** The labels say where a pull request is:

| Label | Set by | What it means | What the owner does |
|---|---|---|---|
| `in-review` | The runner | An open pull request whose head the reviewer has not approved. It is in the build, review and fix loop | Nothing. It is not his turn |
| `has-decision` | The implementer | It made a decision he must answer, one of the three cases in "Building a slice". Never removed, and read only by the gate, the picker and the runner | Nothing yet. It becomes `owner-decision` when the reviewer approves |
| `owner-merge` | The runner | Approved, waits for him, nothing to decide | Reads it and merges |
| `owner-decision` | The runner | Approved, waits for him, and carries `has-decision` | Reads "Decisions the agent made" and the reviewer's "For the owner". Merging confirms the decisions, and a comment asking for a change starts a fix round |
| `owner-blocked` | The runner | Not mergeable as it is: the review rounds ran out, the slice was set Blocked, or the gate refused it for a reason such as a failed `ci-ok` | Does not merge. Reads the runner's mention for the reason, fixes the slice or the cause, and comments on the pull request to run it again |

**The runner owns `in-review` and the three owner labels**, and the implementer sets `has-decision`
and nothing else. So a label starting with `owner-` always means the reviewer has approved the
head, or the pull request is stuck, and it is the owner's turn. A pull request carries at most one
of them, and never one beside `in-review`. A new commit takes the owner label off and puts
`in-review` back, since the new head has no approve; `owner-blocked` alone stays while the head is
still the one whose review rounds ran out, or while its slice is Blocked. A pull request the gate
will merge on its own, low risk, no protected path and no `has-decision`, gets no owner label:
`in-review` comes off when it merges, so an approved pull request can still show it while `ci-ok`
finishes.

**The merge button says the rest.** The ruleset requires `ci-ok` and `review-ok`, and the runner
sets `review-ok` to success only on the exact commit the reviewer approved, see "The runner". So
the button is green only when CI passed on that commit and the reviewer approved it, for the owner
as much as for the gate. Until 2026 11 13 a pull request carried `owner-decision` from the moment
it was opened, with a green button, and the owner merged one before the reviewer had read it.

An owner label, like `has-decision`, blocks the gate's merge and nothing else: every waiting pull
request goes through review and the fix rounds first, so what he reads has already been through
the reviewer. The gate treats the four alike and only reads them.

Once the reviewer approves a pull request that waits for him, the runner hands it over in the same
turn, directly after the review and before it looks for other work: it asks the gate, the owner
label replaces `in-review`, and he is mentioned, once per head commit. The handover waits only for
`ci-ok`. Until that has finished on the approved commit the gate cannot say whether the pull
request is ready or stuck, so it keeps `in-review`, the review's turn ends without waiting, and
the picker's `owner` item is what hands it over: the runner looks again every 10 minutes, before
it resumes or starts a slice. Until 2026 11 13 the handover was the last thing the runner did, and
a pull request sat approved under `in-review` for an hour while a new slice was built. After that
it was still a later item than the review, so #215 sat approved, its button green, under
`in-review` and with no mention while another pull request was reviewed first.
A pull request whose review rounds ran out is handed over once. He merges it
on GitHub, or comments, and a comment newer than the pull request's last handling starts a fix run
that answers it, followed by a new review. On a pull request whose review rounds ran out, a fix run
that only replies, with no new commit, gets no new review, and the pull request waits for him again.

The owner is mentioned on the slice when an approved pull request waits for him, once per head
commit, when a pull request's review rounds run out, when the gate refuses for any other reason,
when a slice goes Blocked, when an interruption has lasted over 30 minutes, and once when the queue
empties. A Blocked notice for a pull request's item goes on that pull request instead, and a usage
limit pause mentions him only once it has lasted 6 hours or hit 10 limits in a row on one item.

## Looking at a whole feature

Every slice is reviewed alone, against its own ticket. Nothing above looks at a feature as one
piece: whether its slices fit together, whether a rule held everywhere or only where a test checked
it, whether the docs still match the code, and whether it became what the owner wanted. The owner
no longer reads every line that lands, and since the agents decide routine things by a standard
rather than asking, their decisions are judged one at a time and never read together.

`/review-feature` is where that can happen. The owner starts it, on the laptop, when he wants a
feature looked at: it reads everything the feature merged, what the agents decided and left
unverified, the code as it now is, and what he asked for, and reports what a review of one slice
cannot see. He goes through the findings and decides which become work, and those become follow up
slices the runner picks up like any others. A problem that shows up more than once points at how
features are planned or built, and is fixed there. The procedure is
`.claude/skills/review-feature/SKILL.md`.

**It is optional, and it is his.** It is not a stage in the life of a feature. Nothing waits for
it, a feature is done when its last slice merges whether or not it is ever reviewed, and no agent
starts it or says it is due. It sits beside planning as something he does when he judges it worth
doing: after a large feature, before building on one, or not at all for a small one.

It is best done in a session that did not plan the feature, since a planner's blind spots are in
the plan itself.

## The runner

`scripts/agent/run.sh` runs in tmux on the Mac mini and loops. Each turn it asks `pending.sh` for
the most important item: a pull request to fix, one whose rounds ran out, one to review, one to
merge, an approved pull request to hand to the owner, a slice to resume, and the next Ready slice
from `next.sh`. A review that ends in `approve` is followed by the gate in the same turn, so the
pull request is merged, or handed to the owner with its label and a mention, before the picker is
asked again. The picker's `merge` and `owner` items settle what that turn could not: a pull
request whose `ci-ok` was still pending when it was approved, one whose handover was interrupted,
and one approved before 2026 11 13. They come before a slice is resumed or started. That does not hold the queue up: once it carries
`owner-merge` or `owner-decision` it has been handed over for that head and is not picked again,
so the next turn takes the slice. While `ci-ok` is still running on it, as on a pull request the
gate is about to merge, the runner waits on that item and starts nothing new; the review's own
turn never waits for it. Last of all comes an approved pull request that carries
`owner-blocked`: it is looked at every turn, since the gate's reasons say whether it has become
ready, and for that reason only when there is nothing else to do. A slice blocked by a slice still
waiting for the owner is not picked; the runner takes other work. Implementer and reviewer each run in a fresh `claude -p` session under a
timeout of 90 minutes, so no run carries another's context. With nothing to do it waits 10 minutes
and looks again. The script's header is the exact behaviour.

**Each turn begins by making the labels and `review-ok` true** for every open pull request by the
bot, before the runner asks for the next item. `review-ok` is a commit status that only the runner
posts: success, "approved by the reviewer", when the latest verdict is `approve` for that exact
commit, and pending, "waiting for the reviewer", otherwise. Never failure, since a `changes`
verdict is still waiting. The runner reads each pull request's verdict, labels and status and
writes only what differs, so one that is already right costs no write, and it posts the status
again right after a review. It calls the gate only for a head it found approved in the same turn,
at its start or by the review it has just run, so the status is written before the gate's own
merge needs it. A label or status write that fails
is an interruption like any other below. This pass is also what carried the open pull requests
over when the labels changed: one approved earlier got its status and its label without a new
review, and one opened with `owner-decision` and no approve became `has-decision` and `in-review`.

**After a merge the runner is done with a pull request.** One that is merged or closed when an
item for it is picked, or after its run, and one the gate answers as already merged, is dropped
with a log line: no label, no mention, no pause and no count against the slice. Before 2026 11 13
a listing that lagged behind a merge got three merged pull requests labelled `owner-blocked`, and
a review of one the owner had merged meanwhile counted as a failed run.

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

Slices are worked one at a time. The gate needs the whole stack, Caddy on 80 and 443, Postgres on
5432 and every service on the port `infra/services.json` gives it, and two runs would collide on
every one of them. Running
several at once would need a stack per run, separate ports and compose projects, and a git worktree
each with its own copy of the git ignored `.env` files. A serial runner that never idles while work
waits is enough until that changes.

## Safety

- **The bot.** The Mac mini works as `mac-mini-agent`, a collaborator with Write and never Admin,
  with Write on Project 5. It cannot change the ruleset or bypass it.
- **The ruleset** "Protect main" requires a pull request and the two status checks `ci-ok` and
  `review-ok`, requires no approval, allows merge commits only, forbids force pushing or deleting
  `main`, and gives the bot no bypass. `ci-ok` says CI passed on the commit, and why one
  aggregating check stands for all of CI is in "What it costs, and how it is paid" in
  `platform-architecture.md`. `review-ok` says the reviewer approved that exact commit, so GitHub
  lets nobody merge a commit that has not been reviewed, the owner included. His own pull requests
  are never read by the reviewer and get no status, so he bypasses the check for those, as he can
  for any rule.
- **One bot identity.** Implementer and reviewer both post as `mac-mini-agent`, so an implementer
  could in principle post its own approval. Three layers stop that: the runner starts implementer
  runs with `--disallowedTools` patterns refusing the marker text, comment edits and merges; it puts
  `scripts/agent/gh-guard.sh` first on their `PATH` as `gh`, refusing the same; and `verdict.sh`
  ignores an edited comment. A second account for the reviewer is the upgrade if a self approval
  ever shows up in the logs. The guard also refuses an implementer's call to any `statuses` path
  and its adding of any label but `has-decision`, so it cannot set `review-ok`, `in-review` or an
  owner label. The guard is the only layer there, and `review-ok` set by hand would still not get
  past the gate, which reads the verdict itself.
- **Deny rules** in `.claude/settings.json` refuse merging, running the gate, pushing to `main`,
  force pushing and any admin flag in every Claude session, so a confused agent fails rather than
  tries.
- **The hosted copy.** It shares the machine and Docker with the runner's agents, accepted with
  guardrails until the runner is retired. Three more deny rules refuse a command whose text holds
  `docker` and then `platform-hosted`, the project or a container of it, or `~/platform` or
  `$HOME/platform`, its clone; `scripts/agent/settings.test.sh` holds them. They are patterns over
  a command's text, not a wall: an agent that builds the name another way, or writes the clone's
  absolute path, gets through. The real protection is the nightly backups, encrypted and uploaded
  off the machine to a bucket that cannot delete them for 30 days, see "Backups" in
  `infra/hosted/README.md`. This arrangement ends when the runner is retired.
- **Auto mode.** Runs use Claude Code's auto permission mode, so routine commands do not wait for an
  answer nobody is there to give, and the deny rules still hold.

## When this would be the wrong call

Autonomy loses when merged slices keep needing rework the reviewer should have caught: a feature
the owner tries and finds broken in ways the acceptance tests and the review both passed. The sign
is follow up slices that fix what an earlier one shipped, more than once a feature. The first answer
is a better plan and a stricter reviewer; if that does not hold, high risk becomes the default tier,
so every pull request waits for the owner while the gate stays the same. The low default for auth,
migrations and contracts is the first thing to take back, and it should also be taken back when the
platform has users besides the owner or holds data that cannot be entered again.

A second reviewer identity would win over the guards if an implementer run ever posts a verdict.
Running slices in parallel would win once the queue regularly holds more Ready slices than one
runner clears between planning sessions.

---

## Mac mini runbook

### Once, on GitHub (the owner)

1. Invite `mac-mini-agent` as a collaborator with Write, and accept the invitation as the bot.
2. Project 5, Settings, Manage access: add `mac-mini-agent` with Write. Repository access does not
   include the board.
3. The "Protect main" ruleset, as described under Safety, in the settings UI, with `ci-ok` as its
   only required check for now; `review-ok` is added in step 6. The bot cannot read rulesets, so
   check it from the owner's account with `gh api repos/Adamniels/Personal-platform/rulesets`.
4. The labels `in-review`, `has-decision`, `owner-merge`, `owner-decision` and `owner-blocked`, the
   Blocked status on the board, and "Automatically delete head branches" in the repository
   settings. The labels come before the runner starts: it sets `in-review` on its first turn, and
   a label that does not exist makes that write fail, which pauses it.
5. GitHub notifications on for mentions, since that is how the runner reaches the owner.
6. **`review-ok`, last and by hand.** Add `review-ok` to the required status checks of "Protect
   main" only after the runner that posts it is on `main` and running, and the status has been
   seen on a pull request, under its checks as "approved by the reviewer" or "waiting for the
   reviewer". Not before: until the runner posts it, a required `review-ok` would block every pull
   request, the one that brings the runner's posting included. From then on the owner's own pull
   requests need his bypass, since they get no status.

### Once, on the Mac mini

1. **Tools.** rustup with the stable toolchain, uv, Docker Desktop started at sign in with its
   automatic updates off and its memory at 2 to 4 GB (an update quits Docker Desktop, and it does
   not always start again; the runner starts it itself when its probe fails), Node 26 for the
   frontends (`brew install node`, linked into `/opt/homebrew/bin`, with no other Node ahead of it on
   `PATH`; every run checks `node --version`, see step 4 of `pick-up-slice`), the .NET SDK 10.0.4xx
   for the C# folders (`brew install dotnet@10`, Homebrew's versioned formula, which stays on major
   10 through `brew upgrade`, and not the `dotnet-sdk` cask, which follows the newest major; every
   run checks `dotnet --version`, see step 4 of `pick-up-slice`), `gh`, `jq`, tmux and Claude
   Code, with `~/.docker/bin` and `. "$HOME/.cargo/env"` in `~/.zprofile`, and for .NET
   `export DOTNET_ROOT="/opt/homebrew/opt/dotnet@10/libexec"`,
   `export PATH="$DOTNET_ROOT:$HOME/.dotnet/tools:$PATH"` and `export DOTNET_CLI_TELEMETRY_OPTOUT=1`.
   Docker Desktop's credential helper needs the login keychain, which a plain SSH session cannot unlock,
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
7. **Stack.** From `infra/`, `docker compose up -d --wait`. Each run resets the databases and
   applies the migrations itself. A volume created before `infra/.env` had the brain's two
   passwords has no brain database yet: run `brain.sh` once by hand, as "The databases" in
   `infra/README.md` says, or the reset fails. The same holds for every service that brings a
   database into `infra/services.json` later: add its two passwords to `infra/.env`, then run its
   init script once on the existing volume, before the first run that resets it.
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

### Looking at a whole feature, on the laptop

`/review-feature` in a fresh Claude Code session in the owner's clone, when he wants one looked at.
Optional: see "Looking at a whole feature" above.

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
| A slice was set Blocked | Read its Blocked notice and fix the slice from the owner's account, or the cause. Then, by what exists: with an open pull request, comment on it, and the runner moves the slice to In review; with a branch that has commits but no pull request, set the slice In progress; with neither, set it Ready. `owner-blocked` comes off by itself: the runner swaps it for `in-review` once the slice is off Blocked and the head has moved past the verdict it was stuck on, or for `owner-merge` or `owner-decision` when the reviewer approves a head the gate leaves to the owner |
| The runner pauses with "could not make #n true" | A label or status write failed. Read `~/agent-logs/runner.log`: a label that does not exist is GitHub step 4, and the runner carries on once it is there |
| A pull request the reviewer approved shows `review-ok` as expected and never reported | The runner posts it at the start of its next turn; if the runner is stopped, start it. It is never set by hand |
| Every pull request waits with "risk record is not the owner's" | The slices were created or edited from another account; recreate them as `Adamniels` |
