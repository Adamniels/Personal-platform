# Agent workflow

Status: exploratory. Not yet run; the first run is the pilot on #23.

Last updated: 2026 10 30

How agents build this repository and what the owner does around them. How work is cut, slices,
the walking skeleton and what gets tests, is in `how-we-build.md` and holds unchanged. This
document only says who does each part of it. The rules agents follow are in `AGENTS.md`.

---

## Why agents write the code

The owner has less time than when the repository started, and wants it finished. Code was written
by hand so that the owner would understand all of it. That goal stays, and moves: the owner plans
every slice and reviews every pull request, and merges only what the owner can explain. Reviewing is where the
learning happens now, and a high risk slice, such as the token or refresh logic, gets more review
time rather than being written by hand.

What it costs is that reading code is a weaker teacher than writing it, unless the pull request is
built to be read. That is why a pull request here carries a walkthrough, see below, and why the
`reviewer` agent exists to explain a pull request rather than to judge it.

## The life of a slice

| Stage | Who | Where | Result |
|---|---|---|---|
| Plan | Owner, with an agent | Laptop, `/plan-slice <n>` | Leaves adjusted and ordered, open questions answered, a Decisions comment on the slice, status Ready |
| Pick up | Owner starts it | Phone or SSH, `/pick-up-slice [n]` | Next Ready slice proposed, or the named one taken; branch checked out; status In progress |
| Re-check | Agent | Mac mini | The plan read again against everything that landed since; a collision stops it |
| Implement | Agent | Mac mini | Leaf by leaf, tests first where the leaf names them, one commit per leaf, each leaf closed as its commit lands |
| Verify | Agent | Mac mini | Tests, lints and the slice's check walked on the running stack |
| Self review | A fresh agent | Mac mini | Every finding gets one outcome, see below |
| Pull request | Agent, as the bot | GitHub | One pull request closing the slice; status In review |
| Review | Owner | Laptop | Reads it, walks the check, comments |
| Revise | Agent | Mac mini | `/pick-up-slice <n>` again answers each comment and pushes |
| Merge | Owner | GitHub | Merge commit; the slice closes; status Done |

The board statuses carry the handoffs. Backlog means not yet planned, Ready means planned and free
for an agent, and an agent never takes anything that is not Ready.

## Planning ahead, in batches

Slices are planned before pickup, one or several in a sitting, not at the start of the agent's run.
Planning and reviewing are the owner's two activities, and keeping them apart from the agent's run
means a pickup needs nobody present and a question never waits half a day for an answer.

Planning a slice is its "Plan slice" leaf, the first leaf of every slice, which blocks the others:
re-read and adjust the leaves, order them, add blocked-by links, and settle every question the
leaves leave open. It ends in a **Decisions** comment on the slice issue, which is what the agent
works from, and the slice moving to Ready. A slice cut before planning leaves existed, such as the
walking skeleton's, gets the comment and the status without a leaf to close.

What it costs is staleness: a plan made last week may collide with something that landed since.
The agent re-reads the slice, its leaves, the Decisions comment, every issue body and every doc
they touch before it starts, since a slice's siblings often decide what it touches, and stops on a collision rather than
picking a side.

## Working a slice

**One branch per slice**, named `<number>-<slug>` like every branch so far, and one pull request
that closes the slice with a single closing keyword. If the branch already exists, pickup
continues it. A slice that finishes its parent's work, such as the last piece of the walking
skeleton, can instead continue the parent's branch and close the parent, when its Decisions
comment says so.

**One commit per leaf**, with the owner's convention: a subject, a short body and `Refs #<leaf>`.
The agent closes the leaf once its commit is pushed. Commits made in a review round say what they
answer. No commit or pull request names an agent, since the bot as author already says who wrote
it.

**Tests** follow "What gets tests" in `how-we-build.md`. A leaf that names tests gets them written
first.

**Tripwires.** When a doc and the plan disagree, the agent weighs it by the cost of undoing it, as
`docs/README.md` says. A decision cheap to reverse is changed in the doc in the same pull request
and named in its description. One expensive to reverse, or one the owner already decided, stops the
run and asks.

**Verifying** means everything continuous integration would run, and then the slice's check walked
on the running stack with `curl`, its output kept for the pull request. The agent does not walk a
check in a browser, so a check that only a browser can show is left to the owner and said so.

### Self review

Before the pull request, a fresh agent with no memory of writing the code reviews the branch
against the slice, its leaves, the Decisions comment and the docs. Every finding gets exactly one
outcome, the first that applies:

1. **Fixed**, when it stops the slice's check or a leaf's done condition from holding. If fixing
   it needs a decision from the owner, the run stops and asks.
2. **Fixed**, when it is in a file the branch changes, belongs to the slice's own work, and needs
   no decision. A deletion, a new dependency or an architecture change always needs one.
3. **Issue proposed**, when it matters and cannot be fixed in the branch. The draft goes in the
   pull request and the issue is created after the owner agrees.
4. **Not done**, anything else: one line in the pull request and no issue, so the backlog does not
   grow from nits.

### The pull request

The pull request is built to be reviewed by someone learning the code:

- **Walkthrough**: what changed, in the order to read it, and why it is shaped that way.
- **Rests on**: the doc passages and decisions it implements, linked.
- **Look hardest here**: the parts where a mistake would be silent or expensive.
- **Check**: the walk's commands and output.
- **Tripwires**: any doc changed, and why.
- **Review**: every self review finding and its outcome.

## Review and merge

The owner reviews on the laptop. For explanation rather than judgement, the `reviewer` agent in
`.claude/agents/` walks through the pull request, points at what is risky and asks questions, and
never writes a fix. The owner walks the slice's check again before approving.

Only the owner merges. `main` takes merge commits only, so each leaf's commit and its `Refs` survive
on `main`, and `git log --first-parent main` still reads one entry per slice. The sprint's final
walk stays the owner's, by hand.

## One agent at a time

Slices are worked one at a time. Walking a check needs the whole stack, Caddy on 80 and 443, the
core on 8080, canary on 8081 and Postgres on 5432, and two runs would collide on every one of
them. Running several at once would need a stack per run, separate ports and compose projects, and
a git worktree each with its own copy of the git ignored `.env` files. None of that is built until
running in parallel is wanted.

A runner that picks up the next Ready slice without being started is left for later too. It pays
once several slices wait in Ready and pull requests come back needing only small comments, and it
raises questions this does not have yet: a stuck run, an unanswered question, and a slice whose
blocker is an unmerged pull request.

## Safety

- **The bot.** The Mac mini works as `mac-mini-agent`, a collaborator with Write and never Admin,
  with Write on Project 5. Its pull requests are authored by the bot, so the owner can approve them.
- **The ruleset** "Protect main" requires a pull request with one approval, dismisses an approval
  when new commits arrive, allows merge commits only, and forbids deleting or force pushing `main`.
  The admin bypass is for pull requests only, so nobody pushes to `main` directly. Continuous
  integration's checks become required once #23 lands.
- **Deny rules** in `.claude/settings.json` refuse merging a pull request, pushing to `main` and any
  admin flag, so a confused agent fails rather than tries.
- **Auto mode.** Runs on the Mac mini use Claude Code's auto permission mode, so routine commands do
  not wait for an answer nobody is there to give, and the deny rules still hold.

## When this would be the wrong call

Writing by hand would win again if the learning stopped happening, if pull requests were approved
without being understood. The sign is approving one and being unable to explain a part of it a
week later. Planning at pickup rather than ahead would win if most slices raised questions only
visible once code exists, which the docs deciding things up front is meant to prevent.

---

## Mac mini runbook

### Once, on GitHub (the owner)

1. Invite `mac-mini-agent` as a collaborator with Write, and accept the invitation as the bot.
2. Project 5, Settings, Manage access: add `mac-mini-agent` with Write. Repository access does not
   include the board.
3. The "Protect main" ruleset, described under Safety.

### Once, on the Mac mini

1. **Tools.** rustup with the stable toolchain, uv, Docker Desktop started at sign in, `gh`, `jq`,
   tmux and Claude Code. Docker Desktop's keychain helper cannot unlock from an SSH session, so a
   new image is pulled once from the Mac's own Terminal, `docker pull postgres:18` and
   `docker pull caddy:2`, and compose uses it from then on.
2. **Hostname.** The `/etc/hosts` line from `infra/README.md`.
3. **Clone.** `git clone https://github.com/Adamniels/Personal-platform.git ~/dev/personal-platform`
4. **Log in as the bot.** `gh auth login` with the bot account and the scopes `repo`, `workflow`,
   `project` and `read:org`, then `gh auth setup-git`, so pushes use the bot.
5. **Git identity, this clone only.**
   ```zsh
   git config user.name "mac-mini-agent"
   git config user.email "340709305+mac-mini-agent@users.noreply.github.com"
   ```
6. **Environment.** Copy `infra/.env.example` to `infra/.env` and `Core/.env.example` to
   `Core/.env`, and fill in the passwords. They are local to this machine and never leave it.
7. **Stack.** From `infra/`, `docker compose up -d --wait`, then the core's `migrate` command as
   the owner role, with the connection string set for that command only, as step 3 of
   `.claude/skills/pick-up-slice/SKILL.md` shows.
8. **Caddy's root.** Not trusted in the keychain here, since an SSH session cannot change trust and
   nothing on the Mac mini uses a browser. The agent copies the root out of the volume and passes it
   to `curl` with `--cacert`, see "Trusting Caddy's root" in `infra/README.md` for the copy step.

### Check

In the clone, each of these holds, and `pick-up-slice` refuses to start if one does not:

- `gh api user --jq .login` prints `mac-mini-agent`
- `git config user.email` ends with `+mac-mini-agent@users.noreply.github.com`
- `git config --get-all credential.https://github.com.helper` includes `gh auth git-credential`
- `gh project view 5 --owner Adamniels --format json --jq .title` prints the board's title

### A working session

```zsh
ssh <mac-mini>
tmux new -s platform
cd ~/dev/personal-platform && git fetch
claude --permission-mode auto --remote-control
```

Then `/pick-up-slice` for a proposal, or `/pick-up-slice <n>` for a named slice. Detach with
`Ctrl-b d`, and answer the agent's questions from the phone through Remote Control. After a merge,
`git switch main && git pull` and delete the slice's branch.

### Troubleshooting

| Symptom | Fix |
|---|---|
| The agent warns that `gh` is the owner's account | Redo steps 4 and 5 in the clone |
| A board change fails with a scope error | `gh auth refresh -s project` |
| A board change fails with "Could not resolve to a ProjectV2" | The bot has no access to the board, GitHub step 2 |
| `curl` reports an expired certificate after the Mac mini was off | "After Caddy has been stopped for a while" in `infra/README.md` |
| The owner cannot approve the pull request | It was opened as the owner, check step 4 |
