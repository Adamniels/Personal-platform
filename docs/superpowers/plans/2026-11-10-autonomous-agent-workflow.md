# Autonomous agent workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agents plan with the owner, then implement, review and merge slices into `main` unattended, with automated acceptance tests and screenshots as verification.

**Architecture:** Small bash scripts with offline tests (fake `gh` and `claude` on `PATH`, as `next.test.sh` already does) hold every deterministic decision: which CI areas run, whether CI passed, what work is next, what the reviewer said, whether a pull request may merge. Claude sessions do the judgement work through two skills and one agent. The runner is a bash loop that calls `claude -p` with a fresh context per step.

**Tech Stack:** bash 3.2 compatible (macOS), `gh`, `jq`, GitHub Actions, Docker Compose, `uv` with `pytest-playwright` and Chromium, Claude Code headless (`claude -p`).

**Spec:** `docs/superpowers/specs/2026-11-10-autonomous-agent-workflow-design.md`

## Global Constraints

- The bot is `mac-mini-agent`, Write only, never Admin, never a ruleset bypass.
- Merge only through `scripts/agent/merge-gate.sh`, only with `gh pr merge --merge`. Claude sessions are denied `gh pr merge` and `merge-gate.sh`.
- Merge conditions, all required: `ci-ok` green on the head commit, latest verdict `approve` for the head commit, slice risk `low`, no `needs-owner` label.
- Review rounds: at most 3 `changes` verdicts, then `needs-owner` and a mention.
- Runner: `claude -p` per step with a 90 minute timeout, poll every 10 minutes when idle, logs in `~/agent-logs/<n>-<timestamp>.log`, stop file `~/.agent-stop`, second consecutive failure on a slice sets Blocked with a mention.
- Screenshots: orphan branch `pr-screenshots`, path `<pr>/<name>.png`, embedded as `https://github.com/Adamniels/Personal-platform/blob/pr-screenshots/<pr>/<name>.png?raw=true`. Viewports: desktop 1280x800, phone 390x844.
- The owner is mentioned as `@Adamniels`, in a comment on the slice issue.
- Commits: on local `main`, a subject and a short body, no `Refs` line, since this rewrite has no tracking issue. No agent attribution, no `Co-Authored-By`, no "Generated with". GitHub verification uses one draft PR from the branch `autonomous-workflow`, pushed from local `main`, which the owner merges at the end.
- Scripts run under bash 3.2: no associative arrays, no `mapfile`, no `${var,,}`; macOS has no `timeout` binary.
- Nothing in the repository points back to the earlier workflow once Task 11 lands.

## Review Focus

1. **A stale approval.** The reviewer approves commit A, a fix pushes commit B, and the gate must not merge B on A's approval. Pinned in Task 6 by `verdict.sh` matching the head SHA and a gate test.
2. **A quoted marker read as a verdict.** The implementer and the reviewer post as the same bot, so a reply quoting the verdict line must not count. Pinned in Task 6: a marker counts only as the first line of a comment.
3. **An unreadable risk.** A slice body without `## Risk`, or with `High`, `high risk` or a typo, must count as high. Pinned in Task 6: anything but exactly `low` fails closed.
4. **A cancelled or failed CI job read as green.** `ci-ok` must pass only on `success` or `skipped`. A PR touching only docs still gets a green `ci-ok`. Pinned in Task 1.
5. **An agent merging around the gate.** A Claude session must not call `gh pr merge` or `merge-gate.sh`. Pinned in Task 8 with a test that reads `.claude/settings.json`.

---

### Task 0: Start

**Files:** the spec and this plan

- [ ] **Step 1:** On local `main`, commit the spec and this plan with the subject "Design the autonomous agent workflow". The owner decided: no tracking issue, and `my-own-notes.md` is deleted in Task 11.

### Task 1: CI as one required check, `ci-ok`

**Files:**
- Create: `scripts/ci/changed-areas.sh`, `scripts/ci/changed-areas.test.sh`, `scripts/ci/ci-ok.sh`, `scripts/ci/ci-ok.test.sh`, `.github/workflows/ci.yml`
- Modify: `.github/workflows/core.yml`, `.github/workflows/canary.yml` (triggers only)

**Interfaces:**
- Produces: `changed-areas.sh` reads changed paths on stdin, one per line, and prints exactly four lines `core=<true|false>`, `canary=<true|false>`, `scripts=<true|false>`, `e2e=<true|false>`, suitable for `$GITHUB_OUTPUT`.
- Produces: `ci-ok.sh` reads the Actions `needs` context as JSON on stdin. It exits 0 when every job's `result` is `success` or `skipped`, and otherwise exits 1 and prints `<job>: <result>` for each other one.
- Produces: `ci.yml` jobs `changes`, `core`, `canary`, `scripts`, `e2e` (wired in Task 3) and `ci-ok`.

Area rules: `core` for `Core/**` and `infra/postgres/**`; `canary` for `canary/**`; `scripts` for `scripts/**` and `.claude/settings.json`; `e2e` for `Core/**`, `canary/**`, `infra/**` and `e2e/**`. Any path under `.github/workflows/` sets all four.

- [ ] **Step 1: Write the failing tests** in `changed-areas.test.sh`, in the `check` style of `next.test.sh`:
  - `Core/src/lib.rs` gives `core=true canary=false scripts=false e2e=true`
  - `docs/README.md` alone gives all four `false`
  - `infra/postgres/initdb/core.sh` gives `core=true` and `e2e=true`
  - `.github/workflows/core.yml` gives all four `true`
  - empty input gives all four `false`

  In `ci-ok.test.sh`:
  - `{"core":{"result":"success"},"canary":{"result":"skipped"}}` exits 0
  - `{"core":{"result":"cancelled"}}` exits 1 and prints `core: cancelled`
  - `{"e2e":{"result":"failure"},"core":{"result":"success"}}` exits 1 and prints `e2e: failure`
  - `{}` exits 0

- [ ] **Step 2: Run** `bash scripts/ci/changed-areas.test.sh && bash scripts/ci/ci-ok.test.sh`. Expected: FAIL, since the scripts do not exist.

- [ ] **Step 3: Implement both scripts.** Use `case` on each path for the area rules, and `jq` for `ci-ok.sh`.

- [ ] **Step 4: Run the tests again.** Expected: every line `ok`.

- [ ] **Step 5: Wire the workflows.**
  - `core.yml` and `canary.yml` replace their `push` trigger with `workflow_call:` and keep `workflow_dispatch`.
  - `ci.yml` runs on `pull_request` and on `push` to `main`.
  - Its `changes` job checks out with `fetch-depth: 0` and pipes `git diff --name-only <base>...HEAD` into `changed-areas.sh`. The base is `origin/${{ github.base_ref }}` on pull requests and `${{ github.event.before }}` on push.
  - `core` and `canary` call the reusable workflows under `if: needs.changes.outputs.<area> == 'true'`.
  - `scripts` runs every `scripts/**/*.test.sh`.
  - `ci-ok` has `needs:` every other job and `if: always()`, and runs `echo '${{ toJSON(needs) }}' | bash scripts/ci/ci-ok.sh`.

- [ ] **Step 6: Verify on GitHub.** The controller pushes local `main` to `autonomous-workflow` and opens or updates the draft pull request. Run `gh pr checks <pr> --watch`. Expected: `ci-ok` passes, and `core` and `canary` ran or were skipped as the diff requires.

- [ ] **Step 7: Commit** with the subject "Gate CI on one always running ci-ok check".

### Task 2: The acceptance suite, `e2e/`

**Files:**
- Create: `e2e/pyproject.toml`, `e2e/.python-version` (copy `canary/.python-version`), `e2e/uv.lock`, `e2e/conftest.py`, `e2e/tests/test_registration.py`, `e2e/README.md`, `e2e/.gitignore` (`screenshots/`)

**Interfaces:**
- Produces: `base_url` is `https://platform.test`, and the browser context ignores HTTPS errors.
- Produces: the fixture `invite_code() -> str` runs `cargo run -q -- invite` in `Core/` and returns stdout stripped.
- Produces: the fixture `viewport` is parametrized over `desktop` (1280x800) and `phone` (390x844), sets the page's viewport size, and ids tests `[desktop]` and `[phone]`.
- Produces: `capture(page, name: str) -> Path` writes `e2e/screenshots/<name>-<viewport>.png` with a full page screenshot and returns the path. Task 4's `screenshots.sh` publishes these files.

- [ ] **Step 1: Create the project.** In `e2e/`, `uv init --bare`, then `uv add --dev pytest pytest-playwright ruff`, then `uv run playwright install chromium`. This is the new dependency the spec approved; record the versions `uv` resolved in `e2e/README.md` under "Libraries", as `canary/README.md` does.

- [ ] **Step 2: Write the failing test** `test_register_then_log_in(page, invite_code, viewport)`:
  - opens `/register?code=<code>`;
  - fills `email` with `e2e-<uuid4>@example.com` and `password` with 20 characters, then submits;
  - expects the URL to end with `/login`;
  - fills the same email and password, submits, and expects the URL to be `https://platform.test/` and the heading `Platform` to be visible;
  - calls `capture(page, "front-signed-in")`.

- [ ] **Step 3: Run it with the stack down.** Run `uv run pytest`. Expected: FAIL with a connection error, which proves the test reaches the network.

- [ ] **Step 4: Start the stack** as `e2e/README.md` will say: compose up, `migrate`, and the core with `cargo run` from `Core/`. Run `uv run pytest`. Expected: 2 passed, and two PNGs in `e2e/screenshots/`.

- [ ] **Step 5: Write `e2e/README.md`:** running the suite, the fixtures above, and the rule that every planned acceptance criterion with a UI is a test here.

- [ ] **Step 6:** Run `uv run ruff check && uv run ruff format --check`. Then commit with the subject "Add the Playwright acceptance suite".

### Task 3: The acceptance suite in CI

**Files:**
- Create: `.github/workflows/e2e.yml`
- Modify: `infra/compose.yaml` (caddy `extra_hosts`), `.github/workflows/ci.yml` (the `e2e` job)

**Interfaces:**
- Consumes: Task 1's `e2e` area output; Task 2's suite.

- [ ] **Step 1: Make Caddy reach the host on Linux.** Add `extra_hosts: ["host.docker.internal:host-gateway"]` to the caddy service, with a comment that Docker Desktop defines the name already and Linux does not.

- [ ] **Step 2: Write `e2e.yml`,** a `workflow_call` workflow. It:
  1. writes `infra/.env` with throwaway passwords;
  2. writes `Core/.env` with `CORE_LISTEN_ADDR=0.0.0.0:8080`, `CORE_PUBLIC_ORIGIN=https://platform.test` and the app URL, then appends a key with `cargo run -q -- keygen`;
  3. adds `127.0.0.1 platform.test` to `/etc/hosts`;
  4. runs `docker compose up -d --wait` from `infra/`, then `migrate` as in `core.yml`;
  5. starts the core in the background and canary with `--host 0.0.0.0 --port 8081`, waiting until `curl -k https://platform.test/` answers;
  6. runs `uv run playwright install --with-deps chromium` and `uv run pytest` in `e2e/`;
  7. uploads `e2e/screenshots/` as an artifact with `if: always()`.

  `0.0.0.0` is needed because traffic from the container arrives on the bridge, not on loopback; say so in a comment. The runner is thrown away afterwards, so binding there is safe.

- [ ] **Step 3: Wire the `e2e` job** in `ci.yml` under its area condition, and add it to `ci-ok`'s `needs`.

- [ ] **Step 4: Verify.** The controller pushes local `main` to `autonomous-workflow` and runs `gh pr checks <pr> --watch`. Expected: `e2e` passes, the screenshots artifact holds two PNGs, and `ci-ok` passes.

- [ ] **Step 5: Commit** with the subject "Run the acceptance suite in CI".

### Task 4: Screenshots on pull requests

**Files:**
- Create: `scripts/agent/screenshots.sh`, `scripts/agent/screenshots.test.sh`

**Interfaces:**
- Produces: `screenshots.sh <pr> <png>...` commits each file to `pr-screenshots` at `<pr>/<basename>`, pushes, and prints one line per file: `![<basename without .png>](https://github.com/Adamniels/Personal-platform/blob/pr-screenshots/<pr>/<basename>?raw=true)`. It creates the orphan branch when `origin` has none. It works in a temporary `git worktree` and never touches the current branch or working tree. Exit codes: 0 ok, 1 git error, 2 usage.

- [ ] **Step 1: Write the failing tests.** Run them against a local bare repository as `origin`, in a temp clone:
  - the first call creates `pr-screenshots` with no parent commit, holding `12/a.png`;
  - a second call for PR 13 adds `13/b.png` and keeps `12/a.png`;
  - the output line matches the format exactly;
  - the current branch, `HEAD` and `git status --short` are unchanged afterwards;
  - no arguments exits 2.

- [ ] **Step 2: Run** `bash scripts/agent/screenshots.test.sh`. Expected: FAIL.
- [ ] **Step 3: Implement** with `git worktree add --detach`, then `git switch --orphan pr-screenshots` when the remote branch is missing, otherwise check it out; then copy, commit, push, and `git worktree remove`, the last inside a `trap`.
- [ ] **Step 4: Run the tests.** Expected: all `ok`.
- [ ] **Step 5: Commit** with the subject "Publish pull request screenshots to an orphan branch".

### Task 5: Board status Blocked, and the work picker

**Files:**
- Modify: `scripts/agent/board.sh` (`STATUSES`), `scripts/agent/board.test.sh`, `scripts/agent/next.sh`, `scripts/agent/next.test.sh`
- Create: `scripts/agent/pending.sh`, `scripts/agent/pending.test.sh`

**Interfaces:**
- Produces: `board.sh status <n> Blocked`.
- Produces: `next.sh --number` prints the proposed slice's number, or nothing when there is none. Without the flag, output is unchanged except that "In flight" also lists Blocked.
- Consumes: Task 6's `verdict.sh`, called by path, so tests put a fake on `PATH` through `AGENT_SCRIPTS`.
- Produces: `pending.sh` prints at most one line, the highest priority work item:
  1. `fix <pr> <slice>`: an open bot PR without `needs-owner` whose verdict for the head commit is `changes` and that has fewer than 3 `changes` verdicts; or any open bot PR, `needs-owner` included, with a comment by `Adamniels` newer than both its latest verdict and its head commit, so the owner's review of a high risk PR is answered;
  2. `cap <pr> <slice>`: such a PR with 3 `changes` verdicts and no `needs-owner` label;
  3. `review <pr> <slice>`: an open bot PR without `needs-owner` with no verdict for its head commit;
  4. `merge <pr> <slice>`: verdict `approve` for the head commit;
  5. `resume <slice>`: status In progress with no open PR;
  6. `new <slice>`: `next.sh --number`.

  The slice is the PR's `closingIssuesReferences[0]`. Exit 0 with no output means idle.

- [ ] **Step 1: Write the failing tests.**
  - `board.test.sh`: `Blocked` is accepted.
  - `next.test.sh`: `--number` prints `26` for the existing order fixture and nothing for an all-blocked one.
  - `pending.test.sh`, with fake `gh` and fake `verdict.sh`:
    - one test per priority, showing it beats every lower one;
    - an owner comment newer than an `approve` verdict gives `fix`;
    - a `needs-owner` PR without a new owner comment yields no line for that PR;
    - a `needs-owner` PR with a new owner comment gives `fix`.
- [ ] **Step 2: Run them.** Expected: the new checks FAIL.
- [ ] **Step 3: Implement.** Read PRs with `gh pr list --state open --author mac-mini-agent --json number,headRefOid,labels,closingIssuesReferences`, and comments with `gh api repos/Adamniels/Personal-platform/issues/<pr>/comments`.
- [ ] **Step 4: Run every `scripts/agent/*.test.sh`.** Expected: all `ok`.
- [ ] **Step 5: Commit** with the subject "Add the Blocked status and a work picker for the runner".

### Task 6: The verdict and the merge gate

**Files:**
- Create: `scripts/agent/verdict.sh`, `scripts/agent/verdict.test.sh`, `scripts/agent/merge-gate.sh`, `scripts/agent/merge-gate.test.sh`

**Interfaces:**
- Produces: the verdict marker, which must be the comment's first line exactly: `<!-- verdict: approve sha=<40 hex> -->` or `<!-- verdict: changes sha=<40 hex> -->`.
- Produces: `verdict.sh <pr>` prints `<approve|changes> <sha> <count of changes verdicts>` for the newest comment by `mac-mini-agent` whose first line is a marker, or `none 0 <count>`. Exit 1 on a GitHub error.
- Produces: `merge-gate.sh <pr>` exits 0 after merging, 3 when not eligible, printing one reason line per unmet condition, and 1 on error. Conditions:
  - the `ci-ok` check run on the head SHA has conclusion `success`;
  - `verdict.sh` gives `approve` with the head SHA;
  - the slice body's `## Risk` section has `low` as the first word of its first non-empty line, case-sensitive;
  - there is no `needs-owner` label.

  On success it runs `gh pr merge <pr> --merge` and `board.sh status <slice> Done`.

- [ ] **Step 1: Write the failing tests,** with fake `gh` fixtures.
  - `verdict.sh`:
    - newest marker wins;
    - a marker followed by anything but exactly `approve` or `changes` and a 40 hex SHA is ignored;
    - a comment with the marker on line 2, such as a quoting reply, is ignored;
    - a marker from another author is ignored;
    - the changes count counts all `changes` markers.
  - `merge-gate.sh`:
    - every condition holds, so it exits 0 and the log shows `pr merge <pr> --merge`;
    - an approve for an older SHA exits 3 with reason `verdict is not for the head commit`;
    - `## Risk` missing, `High`, `high`, or `low-ish` each exits 3 with `risk is not low`, while `low, a copy change` passes this condition;
    - `ci-ok` `failure` or missing exits 3;
    - `needs-owner` present exits 3;
    - in no exit 3 case does the log contain `pr merge`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Use `gh api repos/Adamniels/Personal-platform/commits/<sha>/check-runs --jq` for `ci-ok`, and `gh issue view <slice> --json body` for risk, extracted with `awk` between `## Risk` and the next `## `.
- [ ] **Step 4: Run the tests.** Expected: all `ok`.
- [ ] **Step 5: Commit** with the subject "Merge only through a deterministic gate".

### Task 7: The reviewer agent

**Files:**
- Modify: `.claude/agents/reviewer.md` (full rewrite)

**Interfaces:**
- Consumes: Task 6's marker format.
- Produces: one PR comment per run. The first line is the marker, with the SHA from `gh pr view <pr> --json headRefOid`. Then `## Blocking` and `## Nits`, each finding as `path:line`, what is wrong, why it matters. Then `## Replies` to the implementer's answers from the previous round.

- [ ] **Step 1: Rewrite** with tools `Read, Grep, Glob, Bash`. Inputs to read:
  - the slice body;
  - `gh pr diff`, the full diff and not a delta;
  - the CI results;
  - the screenshots linked in the PR body, read as images;
  - the linked docs;
  - earlier verdicts and replies.

  Judge in this order: acceptance met and tested; security that breaks silently; the "What gets tests" rules; the repository rules; design worth fixing now. Blocking means a criterion unmet, a bug, a missing named test, or a rule broken. Everything else is a nit. It never edits files and never merges. `approve` means no blocking findings.
- [ ] **Step 2: Verify on a real pull request.** Run `claude -p "Use the reviewer agent on pull request <draft pr from Task 1>" --permission-mode auto`, then `bash scripts/agent/verdict.sh <pr>`. Expected: one line naming the head SHA.
- [ ] **Step 3: Commit** with the subject "Make the reviewer a judge with a parseable verdict".

### Task 8: Pick up a slice, rewritten

**Files:**
- Modify: `.claude/skills/pick-up-slice/SKILL.md`, `.claude/skills/pick-up-slice/templates.md` (full rewrites), `.claude/settings.json`
- Create: `scripts/agent/settings.test.sh`

**Interfaces:**
- Consumes: `screenshots.sh` (Task 4), `board.sh` (Task 5), the `e2e` fixtures and `capture` (Task 2), the marker format (Task 6).
- Produces: `/pick-up-slice <n>` runs in **new** mode (status Ready), **resume** mode (In progress, branch exists), or **fix** mode (an open PR with a `changes` verdict or owner comments). It ends at an open or updated PR with status In review. On preflight failure it sets Blocked, comments `@Adamniels` with the reason, and exits.

- [ ] **Step 1: Write the failing test** `settings.test.sh`. It uses `jq` to check that `.permissions.deny` in `.claude/settings.json` contains `Bash(gh pr merge *)` and `Bash(*merge-gate.sh*)`, and keeps the push to `main` and `--admin` rules.
- [ ] **Step 2: Run it.** Expected: FAIL on the `merge-gate.sh` rule.
- [ ] **Step 3: Add the deny rule.** Run the test again. Expected: `ok`.
- [ ] **Step 4: Rewrite `SKILL.md`** as section 3 of the spec. It keeps the identity checks and the stack startup from the current skill, word for word where they still hold. It drops the leaf, Decisions comment and self review steps. It never blocks on a design question: it decides, records the decision, and adds `needs-owner` when the decision is high risk. Its gate step names `bash scripts/ci/changed-areas.sh` to pick what to run, and always runs `e2e/` when the `e2e` area is true. In fix mode, each blocking finding is fixed as "Address review: <what>" or answered with reasoning, nits are fixed when cheap and in files already changed, and every finding gets a reply in one PR comment that does not start with a marker.
- [ ] **Step 5: Rewrite `templates.md`:** a PR body with Summary, `Closes #<slice>`, Decisions the agent made, Verification (gate results, acceptance test output, and the `screenshots.sh` lines), and Doc changes; a fix reply comment; and a Blocked comment.
- [ ] **Step 6:** Run `bash scripts/agent/settings.test.sh`. Then commit with the subject "Rewrite pickup for unattended runs".

### Task 9: Plan a feature

**Files:**
- Create: `.claude/skills/plan-feature/SKILL.md`
- Delete: `.claude/skills/plan-slice/` (approved by the spec)

**Interfaces:**
- Produces: a feature parent issue and its slice sub-issues. Each slice body uses exactly the headings in spec section 2, in that order, and `## Risk` starts with `low` or `high`, because Task 6 parses it. Blocked-by links run between slices, and slices are set to Ready with `board.sh`.

- [ ] **Step 1: Write `SKILL.md`** as section 1 of the spec. Keep from `plan-slice` what still holds:
  - read everything first, as the `read-the-plan-first` memory says;
  - one question at a time, with a proposal and a recommendation;
  - a dry run of every GitHub change, run on the owner's yes, verified with a read;
  - the issue commands for sub-issues and blocked-by links, copied exactly.

  Add the slice body template, the sizing rule (1 to 5 tasks, a few hundred lines excluding tests, one run, one read), the high-risk list from spec section 2, and the rule that a slice whose acceptance cannot be written as tests is not Ready.
- [ ] **Step 2: Delete** `.claude/skills/plan-slice/` with `git rm -r`.
- [ ] **Step 3: Verify.** Run `grep -rn "plan-slice" . --include='*.md' --include='*.sh' --include='*.json' | grep -v docs/superpowers`. Expected: matches only in files Task 11 rewrites, listed there.
- [ ] **Step 4: Commit** with the subject "Plan features instead of slices".

### Task 10: The runner

**Files:**
- Create: `scripts/agent/run.sh`, `scripts/agent/run.test.sh`

**Interfaces:**
- Consumes: `pending.sh` (Task 5), `verdict.sh` and `merge-gate.sh` (Task 6), `board.sh`.
- Produces: `bash scripts/agent/run.sh`, which loops until `~/.agent-stop` exists. It honours `AGENT_HOME` (default `$HOME`) and `AGENT_POLL_SECONDS` (default 600) so tests can redirect them. Actions per `pending.sh` line:
  - `new|resume|fix`: `claude -p "/pick-up-slice <slice>" --permission-mode auto`;
  - `review`: `claude -p "Use the reviewer agent on pull request <pr>" --permission-mode auto`;
  - `merge`: `merge-gate.sh <pr>`. On exit 3 with a high risk reason, comment `@Adamniels` once that the PR waits for them, and add `needs-owner` so `pending.sh` stops offering it;
  - `cap`: add `needs-owner` and comment `@Adamniels`;
  - idle: comment `@Adamniels` once per empty queue on the most recently merged slice, then sleep.

  Each `claude` call runs under `with_timeout 5400`, a bash function (background plus `kill`, since macOS has no `timeout`), and logs to `$AGENT_HOME/agent-logs/<slice>-<YYYYmmdd-HHMMSS>.log`. Failure counts live in `$AGENT_HOME/agent-logs/failures/<slice>`. A nonzero exit or timeout increments the count, the second sets Blocked and mentions the owner, and a success resets it.

- [ ] **Step 1: Write the failing tests,** with fake `claude`, `gh` and `pending.sh` on `PATH`, `AGENT_POLL_SECONDS=0`, and a fake that creates the stop file after N calls:
  - `new 41` calls `claude -p "/pick-up-slice 41"`;
  - `merge 50 41` calls `merge-gate.sh 50`;
  - two failing `claude` runs on 41 call `board.sh status 41 Blocked`, and one failure then one success does not;
  - a `claude` sleeping past a 1 second timeout (`AGENT_TIMEOUT_SECONDS=1`) is killed and counted as a failure;
  - the stop file ends the loop with exit 0;
  - `cap 50 41` adds `needs-owner`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run every `scripts/**/*.test.sh`.** Expected: all `ok`.
- [ ] **Step 5: Commit** with the subject "Add the unattended runner".

### Task 11: The docs and memory, rewritten

**Files:**
- Modify: `High-level/docs/agent-workflow.md` (full rewrite, including the Mac mini runbook); `High-level/docs/how-we-build.md` (the checks paragraphs, "On the board", and "No automated browser tests yet"); `AGENTS.md` ("Where things are", "AI and code", "Working agreement"); `docs/README.md` (its line for `agent-workflow.md`, and `e2e/`, `scripts/ci/` and `docs/superpowers/` in the layout); `.claude/memory/small-nested-tickets.md` (rewritten as feature, slice and tasks, renamed `feature-and-slices.md`); `.claude/memory/github-board.md` (Blocked, feature parent instead of Sprint); `.claude/memory/long-run-over-slice-simplest.md` (`/plan-feature`); `.claude/memory/MEMORY.md`
- Delete: `.claude/memory/first-time-teaching.md`; `my-own-notes.md`, as the owner decided

- [ ] **Step 1: Rewrite `agent-workflow.md`** to describe the spec's sections as the current state, with its reasoning and a "When this would be the wrong call" section: autonomy loses when merged slices keep needing rework the reviewer should have caught. The runbook adds `uv run playwright install chromium` in `e2e/`, starting `run.sh` in tmux, `~/.agent-stop`, and `~/agent-logs/`.
- [ ] **Step 2: Rewrite the `how-we-build.md` passages.** Acceptance criteria are automated tests written first, with the Playwright suite running in CI on every PR that touches service code. The final walk at the end of a feature is the full suite on a fresh stack. Work is a feature, then slices, then a task checklist.
- [ ] **Step 3: Rewrite `AGENTS.md`, `docs/README.md` and the memories** as listed. Delete the files above with `git rm`.
- [ ] **Step 4: Verify nothing points back.** Run `grep -rniE "plan-slice|decisions comment|leaf|leaves|only the owner merges|first-time-teaching|walked by hand|self review" --include='*.md' . | grep -v docs/superpowers`. Expected: no line about the workflow; the matches in `infra/README.md`, `Core/docs/`, `Brain/docs/` and `feature-contract.md` are ordinary English, such as a TLS leaf, and stay.
- [ ] **Step 5: Commit** with the subject "Describe the autonomous workflow as the way this repository is built".

### Task 12: The switch, outside the repository

**Files:** none in the repository. Every GitHub command is shown to the owner first and run on their yes, as the `github-changes` memory says.

- [ ] **Step 1: The owner reviews and merges the `autonomous-workflow` PR by hand.** The new gate is not live yet.
- [ ] **Step 2: Labels and board.** Run `gh label create needs-owner --color B60205 --description "Waits for the owner before merging"`. On Project 5, add a Blocked status option, which the board UI does. The Sprint field is left for the owner to remove once no open issue uses it, since deleting a field cannot be undone.
- [ ] **Step 3: The ruleset "Protect main".** Require a PR, require the status check `ci-ok`, remove the required approval, keep merge commits only and the force push and deletion protections, and remove any bypass that includes the bot. The owner does this in the settings UI, since the bot cannot read rulesets. Verify with `gh api repos/Adamniels/Personal-platform/rulesets` from the owner's account.
- [ ] **Step 4: On the Mac mini.** Run `git pull`, then `cd e2e && uv run playwright install chromium`, then `bash scripts/agent/run.sh` inside `tmux new -s runner`.
- [ ] **Step 5: First real run.** Plan one small low-risk feature with `/plan-feature`, and watch it go through new, review, merge on the runner's logs. Expected: the PR merged by `mac-mini-agent` with a merge commit, its slice Done, and a screenshot embedded in the PR if the slice has UI.
- [ ] **Step 6: The owner's context, in claude-config, left uncommitted.** Rewrite `project-context/personal-platform/overview.md` ("Now", "What's done", "Defining decisions", "What it is not") for the autonomous workflow, and the Personal Platform line in `NOW.md`, dated.
