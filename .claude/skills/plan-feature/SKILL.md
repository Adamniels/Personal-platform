---
name: plan-feature
description: 'Use when the owner wants to plan a feature for the platform ("plan a feature", "/plan-feature", "let us plan X", "I want the platform to ..."), so agents can build it unattended. Runs on the laptop with the owner, logged into gh as himself; ends with a feature issue and its slices Ready.'
---

# Plan a feature

The owner describes what he wants in his own words, as loosely as he likes. He never thinks in
slices: cutting the feature is your job. You ask until nothing is open, then cut the feature into
slices that an agent on the Mac mini builds, reviews and merges without asking anything. Every
decision a slice leaves open is made **here, with the owner**, never left for the agent to guess.
You write no code.

How this fits the whole workflow is in `High-level/docs/agent-workflow.md`. Read it once per
session.

## 0. Check who you are

Slices must be created from the owner's account. The merge gate trusts a slice's Risk only when the
issue was written by `Adamniels` and never edited by anyone else, so a slice created by any other
account is never auto-merged. Before anything else, run:

```sh
gh api user --jq .login
```

If it does not print exactly `Adamniels`, stop and say so: planning on the bot's account, or any
other, produces slices the gate refuses.

## 1. Read everything first

Before the first recommendation, as the `read-the-plan-first` memory says:

- The feature's page in the owner's planning notes, when the session has the Notion connector.
  The notes are the Features database on the "Personal Platform" page in his Notion: one page per
  thing he might build, with the headings What I want, Why, Rough shape, Open questions and Notes
  for planning. Find the page for this feature by its name and read it. If there is no page, or
  no connector, plan from what the owner says, and tell him which of the two it was. The page is
  intent, never design: where it and a doc disagree, that is a question for step 2, not something
  to pick a side on.

  Something built in several rounds, such as the brain, is one page for the area with a page per
  part under it, linked by Part of. Each part has its own Status and Issue, and one that cannot be
  planned yet says why in Waits for. Read the area's page and every part's: the part being planned
  is the scope, the area's page is what the owner wants from the whole, and the other parts say
  what is deliberately left for later. A part whose Waits for still holds is not planned: say so
  and ask.
- `AGENTS.md`, `docs/README.md`, and every doc passage the feature touches. Grep the docs, do not
  work from memory of them.
- Every issue body in the repository, once per session, and grep it for what the feature touches:
  `gh issue list --state all --limit 300 --json number,title,body`.
- The code the feature builds on: the modules, routes, migrations and tests it will change or call.
- An existing feature issue, if the owner names one: its body, and its slices in order with
  `gh api repos/Adamniels/Personal-platform/issues/<n>/sub_issues --jq '.[] | "#\(.number) \(.state) \(.title)"'`,
  each slice's state with `bash scripts/agent/board.sh get <slice>`, and its blockers with
  `gh api graphql -f query='query{repository(owner:"Adamniels",name:"Personal-platform"){issue(number:<n>){blockedBy(first:50){nodes{number state title}}}}}'`.
  Replanning may add slices and edit slices that are not yet In progress, with
  `gh issue edit <n> --repo Adamniels/Personal-platform --body-file <file>` run as `Adamniels`; the gate
  accepts the owner's own edits. Never edit a slice that is In progress or later: In review, Done,
  closed, or Blocked once an agent has started it.

Then say back, in a few sentences, what you understood the owner to want and what the feature
touches, so he can correct it before the questions start.

## 2. Settle every open question

Collect the questions: what the owner left open, the Open questions on his planning page, what
the docs leave open, and every collision between the feature and a doc or another issue. Cover at least behaviour, edge cases, what the UI
looks like and where it lives, scope and what is deliberately left out, new dependencies with exact
versions, and doc changes.

Ask them **one at a time**, each with a proposal and a recommendation, and wait for the answer
before the next. For each proposal:

- Recommend what holds up in the long run and what a professional team would choose, tested
  against the later slices and issues that touch the same thing, as the
  `long-run-over-slice-simplest` memory says. Cheap to reverse still beats complex.
- A collision with a doc is weighed by the cost of undoing it, see `docs/README.md` and the
  `docs-are-tripwires` memory: either the doc changes, which the slice then does in its own pull
  request, or the plan does.
- Where a feature needs another feature, settle how it reaches it, as the `cross-feature-calls`
  memory says. A backend call in a new direction is a decision for the owner, and a slice then
  records it under "Declared calls" in `High-level/docs/feature-contract.md`.
- Settle up front what the agent would otherwise stop for: a new dependency, with its version and
  why; a deletion; anything touching the architecture.

Trivial points the owner need not weigh, such as a name, a file location or a default that follows
an existing pattern, are not asked one by one. Show them together as "assumptions I will record in
Settled unless you object", and record them once the owner has seen the list.

Keep going until nothing is open. If something cannot be decided now, the slice that needs it is
still created, but stays in Backlog and is not set Ready, see step 5.

## 3. Cut the feature into slices

A **feature** is a parent issue. Each **slice** is a sub-issue of it, one branch and one pull
request. Propose the whole cut at once: each slice's title, goal, tasks, risk and what blocks it.

**Size.** One pull request that an agent finishes in one run and a reviewer judges in one read:
1 to 5 tasks, a few hundred lines excluding tests. Split anything larger. Each task is one commit.

**Tests.** Every acceptance criterion is a named test the agent writes first: a cargo test, a
pytest test, an xUnit test, or a Playwright test in `e2e/`. UI acceptance also names the screenshots to capture,
at desktop and at 390px phone width. A slice whose acceptance cannot be written as named tests is
not Ready: cut it differently or settle what is missing, and otherwise leave it in Backlog.

**Risk.** Each slice is `low` or `high`, and the tier is the owner's choice. High means he wants
to read the pull request before it lands, and nothing else: it goes through the same build and
review loop, then waits for him to merge it.

- **The default is low**, including for auth and token logic, a migration, and a contract between
  the core, the brain and features. The reviewer and the tests carry the checking there, and the
  owner's read adds little to them. Ask him only where a slice is unusual enough that he might want
  to see it, and recommend.
- **A slice that touches `.github/`, `scripts/`, `.claude/`, `AGENTS.md` or `CLAUDE.md` waits for
  him whatever its tier**, since the merge gate never merges those paths. Mark it high, so the
  issue says what will happen, and gather such changes into as few slices as the work allows, early
  in the order, so the rest of the feature merges unattended.
- **A low tier makes thorough slices matter more.** The implementer decides whatever Settled
  leaves open, by the standard in step 8 of `pick-up-slice`, and the reviewer judges the decision.
  It sets `has-decision`, which makes the pull request wait for the owner, only in three cases: the
  decision goes against something the slice's Settled or a doc says; it changes what the owner
  sees or does as a user in a way the slice did not plan; or it is a matter of the owner's taste,
  where no option is better on the merits. Being about auth, a migration or a contract is not a
  reason by itself. So settle here what he would want a say in.

The default was high for all of that ground until 2026 11 12. It is worth going back to when the
platform has users besides the owner, or holds data that cannot be entered again.

**Order.** Slices run in their sub-issue order. Add a blocked-by link wherever one slice needs
another to land first, so "not blocked" keeps meaning "can start". Put the slices the owner can try
soonest first.

Show the cut and let the owner change it. When he agrees, write the bodies.

## 4. The slice body

Every slice body uses exactly these headings, in this order, and nothing else at that level. The
merge gate reads `## Risk`: its first non-empty line must start with exactly `low` or `high`,
lowercase. The body holds no other `## ` heading outside these seven, and never a second
`## Risk`. Quoted doc text goes in a fenced block, and any subheading uses `###`.

```markdown
## Goal
<One or two sentences, user visible.>

## Context
<The doc passages and code it builds on, linked: `path/to/file.rs`, `High-level/docs/x.md`, "Section".>

## Acceptance
- `<test name>` (<cargo | pytest | Playwright in e2e/>): <what it shows>
- Screenshots: `<name>` at desktop and 390px, of <what>. <Or "none".>

## Tasks
- [ ] <first task, one commit>
- [ ] <next task>

## Settled
- <every decision made in planning that this slice depends on: the question and the answer>
- Dependencies: <name, exact version, and why; or "none new">
- Docs to change in this pull request: <passage and what changes; or "none">

## Out of scope
- <what is deliberately left out, and where it goes if anywhere>

## Risk
<low | high>, <and why, when high>
```

`## Settled` holds every answer the slice needs, not only the hard ones, so the implementer never
has to ask. Repeat a decision in each slice that depends on it rather than pointing at another
slice.

The feature parent's body holds the owner's description in his words, the decisions that span
slices, and the slices in order. What his planning page says under What I want and Why is copied
into it, since the agents that build the slices cannot read Notion.

It also holds a section, "Not in this feature": everything the owner wants from the area that
this feature leaves out, one line each, with which of the three tests in "What is built now" in
`High-level/docs/how-we-build.md` it failed and what it waits for. This list is where the
remaining parts in his planning notes come from, see step 6, so nothing left out is left
unrecorded.

## 5. Write it to GitHub

Show every GitHub change as one dry run first, run it on the owner's yes, then verify each change
with a read, as the `github-changes` memory says. Commands for the owner run in zsh, so flags are
written out in full and never kept in a variable.

Write each issue body to a file and pass it with `--body-file`, never as an inline string. Creating
several issues is a script: show it, run it once with a fake `gh` first on `PATH` or a `DRY=1`
mode, and run it for real only on the owner's yes. It runs on macOS's bash 3.2, and a partial
failure gets a resume step, never a rerun.

1. **The feature parent**: `gh issue create --repo Adamniels/Personal-platform --title "<title>" --body-file <file>`.
2. **Each slice**, in order: `gh issue create` the same way, then add it as a sub-issue with
   `gh api -X POST repos/Adamniels/Personal-platform/issues/<parent>/sub_issues -F sub_issue_id=<id>`,
   the id from `gh api repos/Adamniels/Personal-platform/issues/<n> --jq .id`.
3. **Order**, when it differs from creation order:
   `gh api -X PATCH repos/Adamniels/Personal-platform/issues/<parent>/sub_issues/priority -F sub_issue_id=<id> -F after_id=<id>`.
4. **Blocked-by links** between slices:
   `gh api -X POST repos/Adamniels/Personal-platform/issues/<n>/dependencies/blocked_by -F issue_id=<id>`.
5. **Board fields.** Auto-add puts every new issue on the board, so nothing is added by hand.
   Slices are grouped by their parent issue instead of a Sprint field. The feature parent gets no
   Status and no field set by hand beyond Area, as the `github-board` memory says; `next.sh` only
   proposes Ready slices, so a parent without a status is never picked. Slices get their fields
   as that memory says.

Then verify with reads: each slice's body and author
(`gh issue view <n> --repo Adamniels/Personal-platform --json author,body`), the parent's
sub-issues in order, and each slice's blockers. Fix anything that differs from the plan before
going on. A body fix is only ever
`gh issue edit <n> --repo Adamniels/Personal-platform --body-file <file>` run as `Adamniels`.

6. **Ready**, only after every change above is verified, and only for a complete slice, one with
   nothing undecided and every acceptance criterion a named test:
   `bash scripts/agent/board.sh status <n> Ready`. A slice with something still undecided stays in
   Backlog. The feature parent is never set Ready, since it is not work an agent picks up.

## 6. Finish

Say what is now Ready, in order, which slices will wait for the owner, those marked high and
those touching a protected path, and what
`bash scripts/agent/next.sh` proposes first. List every slice left in Backlog as not Ready, with
what is still missing for it.

Tell the owner that any later edit to a slice body should be his own: an edit by any other account,
an agent's included, makes the merge gate refuse the slice for good.

**Then update his planning notes**, when the session has the Notion connector. Show the change
first, like any other, and make it on his yes:

- If the feature has a page, set its Issue to the feature parent's URL and its Status to
  `Building` when its slices were set Ready, since the runner starts on them at once, or to
  `Planned` when the issue exists and nothing is Ready yet. Where what was planned is one part
  of an area, the part's page gets the Status and the Issue, and the area's page gets neither an
  Issue nor a change beyond a Status that says where the area is as a whole.
- **What was left out becomes parts.** For each line under "Not in this feature" in the feature
  parent, make sure the area has a part page for it: Status `Idea`, Part of the area, What I want
  in a sentence or two of the owner's intent, and Waits for saying what it waits on. Update a part
  that exists rather than adding a second. If the area had no parts before, the page that was
  planned becomes its first part, under a page for the area. This is what keeps "what is left" true
  without the owner writing it down.
- Leave Order alone. The order across the platform is the owner's, and a new part has none until
  he places it.
- If it has no page, do not create one. The notes hold what the owner wants the platform to do,
  not every piece of work: something that turned up while building, such as a fix or a CI check,
  is an issue and nothing more.
- Add nothing else to a page. From here on the feature's scope lives on GitHub. Property changes
  are sent on their own, never together with a change to a page's content, and read back.
- An idea that came up while planning and was left out of scope goes to the Inbox on the
  "Personal Platform" page, one line, if the owner wants it kept.

Without the connector, tell the owner to set the page himself, with the issue's URL to paste.

## Never

- Write or change code. Planning ends at Ready slices.
- Create or edit issues from any account but `Adamniels`.
- Leave a question for the agent "to decide at pickup". If it cannot be decided now, the slice
  stays in Backlog.
- Set a slice Ready before every GitHub change is verified, set an incomplete slice Ready, or set
  the feature parent Ready.
- Edit a slice that is In progress or later.
- Write design or scope into the planning notes. Notion holds what the owner wants and why; design
  belongs in the docs, through a slice, and scope belongs in the issues.
