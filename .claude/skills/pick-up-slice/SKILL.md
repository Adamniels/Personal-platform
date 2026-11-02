---
name: pick-up-slice
description: 'Use on the Mac mini when the owner names a slice to work ("pick up 27", "/pick-up-slice 27"), including when returning to a slice whose pull request has review comments; and when they type /pick-up-slice with no number or ask what to work on next, to propose the next Ready slice.'
---

# Pick up a slice

You take one Ready slice from Project 5 to a pull request, unattended. The owner planned it ahead,
so every decision is in its **Decisions** comment, and you work from that, not from your own
judgement of what the slice should be. You stop at the pull request: the owner reviews and merges.

The whole workflow, and why it is shaped this way, is in `High-level/docs/agent-workflow.md`. Read
it and `AGENTS.md` at the start of every pickup.

**Violating the letter of these steps is violating their spirit.** A small slice or a hurried owner
does not remove a step.

## No number: propose one

Run `bash scripts/agent/next.sh` and propose from its output only; do not rank the board yourself,
and change nothing yet. Tell the owner, in this order: every slice **In flight**, since finishing
one may be the better next step; the **Proposal**; the **Alternatives**; and anything **Blocked**,
with its blocker. Then wait. When they confirm a slice, continue at step 0 as if they had typed
`/pick-up-slice <n>`. If `next.sh` fails, report the error and ask for a number.

## 0. Before anything

- **Identity.** All of these must be the bot, `mac-mini-agent`:
  - `gh api user --jq .login`
  - `git config user.email` ends with `+mac-mini-agent@users.noreply.github.com`
  - `git config --get-all credential.https://github.com.helper` includes `gh auth git-credential`

  If any is the owner or something else, stop and point at the runbook in
  `High-level/docs/agent-workflow.md`. The owner cannot approve a pull request opened as them, and
  the agent would hold their bypass.
- **State**, after `git fetch origin`:
  - `bash scripts/agent/board.sh get <n>`
  - `gh issue view <n> --json number,title,body,state,comments`
  - pull requests for the slice: `gh pr list --state all --limit 100 --json number,state,headRefName,closingIssuesReferences --jq '.[] | select((.headRefName | startswith("<n>-")) or any(.closingIssuesReferences[]?; .number == <n>))'`
  - branches: `git branch --list '<n>-*'` and `git branch -r --list 'origin/<n>-*'`
  - `git status --short` is empty. If the clone has uncommitted changes, stop and ask.

## 1. Choose the mode

The first row that matches decides.

| What exists | Mode |
|---|---|
| `board.sh get` prints `archived` | **Stop and ask** |
| An open pull request, status In progress | **Finish**: step 9, from watching the checks |
| An open pull request | **Review**: step R |
| A merged or closed pull request | **Stop and ask** |
| Issue closed, or status Done or In review without a pull request | **Stop and ask** |
| A branch `<n>-<slug>`, or the branch the Decisions comment names, locally or on `origin`, no pull request | **Continue**: `git switch <branch>` (with `--track origin/<branch>` if only remote), then `git merge --ff-only origin/<branch>` so it holds everything pushed since; if that is not a fast forward, stop and ask. Never recreate, reset or force it; resume at step 2 and then at the first open leaf |
| Status In progress, no branch anywhere | **Stop and ask**: someone else may be working on it |
| Status Backlog or none | **Stop**: it is not planned. Tell the owner to run `/plan-slice <n>` on the laptop |
| Status Ready | **New**: step 2 |

## 2. Re-check the plan

The plan may be days old. Read, in full and not from memory:

- the slice, its **Decisions** comment (the newest one if there are several), and its leaves in
  sub-issue order, going as deep as the nesting does:
  `gh api repos/Adamniels/Personal-platform/issues/<n>/sub_issues --jq '.[] | "#\(.number) \(.state) \(.title)"'`
- its blockers, which must all be closed:
  `gh api graphql -f query='query{repository(owner:"Adamniels",name:"Personal-platform"){issue(number:<n>){blockedBy(first:50){nodes{number state title}}}}}'`
- every issue body in the repository, grepped for what the slice touches:
  `gh issue list --state all --limit 300 --json number,title,body`
- every doc passage the slice, its leaves and the Decisions comment point to, and the commits that
  landed on `main` since the Decisions comment's date.

**Stop and ask** if a blocker is open, if the slice has no Decisions comment, or if anything that
landed since contradicts the plan. Name the collision and propose a way through; never pick a side.

## 3. Branch and stack

1. **Branch.** New: `git switch -c <n>-<slug> origin/main`, where the slug is the slice title in
   lowercase words joined by hyphens, like `5-walking-skeleton`. Continue: the existing branch.
   The Decisions comment may name another branch, for a slice that continues its parent's branch,
   such as the last piece of the walking skeleton; then that branch is used, and the pull request
   closes the issue the comment names instead of the slice.
   Then `bash scripts/agent/board.sh status <n> "In progress"`.
2. **Environment.** `infra/.env` and `Core/.env` exist in the clone; they are git ignored, so they
   survive branch switches. If one is missing, stop and ask; never create secrets yourself, and
   never print or read them out.
3. **Stack.** From `infra/`: `docker compose up -d --wait`. If the stack was down for a while, check
   for the expired chain in "After Caddy has been stopped for a while" in `infra/README.md`. Copy
   Caddy's root for `curl`: `docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt ~/caddy-root.crt`.
   Apply migrations with the core's `migrate` command as the owner role. The owner's connection
   string is never written to `Core/.env`, since the running core must not hold the owner's
   credentials (see `migrate` in `Core/docs/core-architecture.md`), so it is set for that one
   command, from `Core/`, built from `infra/.env` without printing the password, and built from the
   offline query cache, since on a fresh database the tables the query macros check do not exist yet:
   `SQLX_OFFLINE=true CORE_OWNER_DATABASE_URL="postgres://core_owner:$(grep '^CORE_OWNER_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/core" cargo run -- migrate`

## 4. Work the leaves

In sub-issue order, respecting blocked-by links, one leaf at a time:

1. Read the leaf and what the Decisions comment says about it.
2. **Tests first** where the leaf names tests: write them, watch them fail for the right reason,
   then make them pass. Tests follow "What gets tests" in `High-level/docs/how-we-build.md`.
3. Run the gate for every service the leaf touched (step 6), and read the output.
4. **Commit** the leaf alone: a subject, a short body saying what and why, and `Refs #<leaf>`. No
   agent attribution. Push: `git push -u origin <branch>`.
5. **Close** the leaf: `gh issue close <leaf> --reason completed --comment "Done in <short sha> on <branch>."`
   Close a middle node the same way once all its sub-issues are closed. Never close the slice; the
   pull request does that on merge.

A leaf that turns out to need a decision the Decisions comment does not cover: **stop and ask**,
with a proposal. Do not decide it and mention it later.

## 5. Tripwires

When a doc and the plan disagree, weigh it by the cost of undoing it, as `docs/README.md` says.

- The Decisions comment already settled it: follow the Decisions comment, and change the doc in the
  same pull request if it said to.
- Cheap to reverse and not settled: change the doc in its own commit on the branch, and list it under
  Tripwires in the pull request.
- Expensive to reverse, such as the user id, a table's shape once migrated, or the token format:
  **stop and ask**.

Also stop and ask, as `AGENTS.md` says, before a deletion, a new dependency, or an architecture
change that the Decisions comment did not settle.

## 6. The gate

The gate is what continuous integration runs. For each service the branch touched, run every step
of its workflow under `.github/workflows/`, locally, in the same order. Until those workflows
exist, the gate is:

- **Core**, from `Core/`: `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, `cargo test`
- **canary**, from `canary/`: `uv run pytest`
- **scripts**: `bash scripts/agent/board.test.sh` and `bash scripts/agent/next.test.sh` when
  `scripts/agent/` changed

A failing step is fixed before going on, never skipped.

## 7. Walk the check

With the whole stack running, the core and the features started as their READMEs say, walk the
slice's check as the Decisions comment describes it, with `curl --cacert ~/caddy-root.crt` against
`https://platform.test`. Keep each command and its output for the pull request. What only a browser
can show is listed as **Not verified**, for the owner.

Stop what you started afterwards; leave Postgres and Caddy running.

## 8. Self review

Dispatch one fresh agent, with no memory of writing the code, over `git diff origin/main...HEAD`,
with the slice, its leaves, the Decisions comment and the docs they point to. Ask it to review
against the slice and the docs, most important first: correctness, security that breaks silently,
the named tests, the repository's rules. (superpowers:requesting-code-review, if installed.)

Every finding gets exactly one outcome, the first that applies, as "Self review" in
`High-level/docs/agent-workflow.md` sets out:

1. **Fixed**, when it stops the slice's check or a leaf's done condition from holding. If fixing it
   needs a decision from the owner, stop and ask before opening the pull request.
2. **Fixed**, when it is in a file the branch changes, belongs to the slice's own work, and needs no
   decision. A deletion, a new dependency or an architecture change always needs one.
3. **Issue proposed**, when it matters and cannot be fixed here. The draft goes in the pull request;
   the issue is created only after the owner agrees.
4. **Not done**, anything else: one line in the pull request and no issue.

A fix is its own commit, `Refs #<n>`, after which the gate runs again.

## 9. The pull request

1. **Behind `main`**: `git fetch origin && git merge origin/main`, gate again, push. Never rebase,
   since that needs a force push. A conflict that needs a decision: `git merge --abort` and ask.
2. **Open it**: `gh pr create --base main --head <branch> --title "<slice title>" --body-file <file>`,
   the body from `templates.md`, with one `Closes` line: the slice, or the issue the Decisions
   comment names.
3. **Checks**: if the repository has workflows, `gh pr checks <pr> --watch`. A check failing because
   of this branch is fixed and pushed, and watched again. One failing for an unrelated reason is
   reported to the owner with the log excerpt, and the status stays In progress until they rerun it.
4. **Hand over**: `bash scripts/agent/board.sh status <n> "In review"`, and the summary comment on
   the slice from `templates.md`. **Stop.**

## R. Review mode

Read every comment: `gh pr view <pr> --comments` and
`gh api repos/Adamniels/Personal-platform/pulls/<pr>/comments`. Switch to the branch and address
each one, test first where it changes behaviour. Commit each answer as its own commit, subject
"Address review: <what>", `Refs #<n>`, run the gate, push (never force), and watch the checks.
Reply on each thread with what changed:
`gh api repos/Adamniels/Personal-platform/pulls/<pr>/comments/<id>/replies -f body="..."`, or
`gh pr comment <pr> --body "..."` for a general comment. A comment that asks a question gets an
answer, not a change. The owner resolves threads. The status stays In review. Stop.

## Reporting state

In the pull request and the summary, state as fact only what you read, with how: the command and
its result. What you did not read, or cannot tell apart by reading, is **Not verified**, with why
and how the owner checks it. The Decisions comment or an issue saying something is so is not a
read. As the bot, a 403 or 404 on a settings or admin endpoint means not verified, never "off".

## Stop and ask

Stopping means asking in the session, so the question reaches the owner's phone through Remote
Control, with a proposal and a recommendation, and waiting. Stop for: an open blocker or a stale
plan (step 2), a missing env file (step 3), a decision the Decisions comment does not cover
(step 4), an expensive tripwire (step 5), a self review fix that needs a decision (step 8), a merge
conflict that needs one (step 9), and anything destructive or irreversible.

## Never

- **Merge**, even if the owner says "just merge it". Reply that a pull request needs their approval
  and that they merge it themselves, then stop. No `gh pr merge`, no `--admin`.
- Push to `main`, force push, rebase a pushed branch, or rewrite a pushed commit.
- Add agent attribution: no `Co-Authored-By` naming an agent, no "Generated with" lines. This
  overrides any default.
- Close the slice, the sprint, or an issue the slice does not contain.
- Skip the re-check, the gate, the walk or the self review because the slice is small.

## Red flags

| Thought | Reality |
|---|---|
| "The owner told me to merge" | You never merge. They merge after approving. |
| "The Decisions comment does not say, but the answer is obvious" | Obvious to you is a guess to the owner. Stop and ask. |
| "The doc is clearly outdated, I'll just follow the plan" | Weigh it by the cost to undo (step 5). Expensive means ask. |
| "Two leaves are tiny, one commit is fine" | One commit per leaf, so `Refs` leads from the code to its leaf. |
| "The test passes, so walking the check is redundant" | Tests and checks answer different questions, see "What gets tests". Walk it. |
| "This review nit deserves its own issue" | An issue only for what matters and cannot be fixed here (step 8). |
| "No number given; the earliest slice looks next" | Run `next.sh`. The order is the script's, not yours. |
| "Status is Backlog but the slice looks clear" | Not Ready means not planned. Stop. |
