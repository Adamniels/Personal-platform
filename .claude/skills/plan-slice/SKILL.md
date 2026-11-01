---
name: plan-slice
description: 'Use when the owner wants to plan one or more slices ahead of pickup ("plan slice 27", "/plan-slice 26 27", "let us plan the next batch"), so an agent can work them unattended. Runs on the laptop with the owner; ends with a Decisions comment and the slice set to Ready.'
---

# Plan a slice

You plan a slice with the owner so that an agent on the Mac mini can work it later without asking
anything. Planning is the owner's main lever on the code, so every decision the slice leaves open
is made **here, with the owner**, never left for the agent to guess. You write no code.

How this fits the whole workflow is in `High-level/docs/agent-workflow.md`, "Planning ahead, in
batches". Read it once per session.

## Several slices

`/plan-slice 26 27 28` plans them one after another, each to its own Ready, in the order given.
A slice whose blocker is another slice in the batch can still become Ready: `next.sh` keeps it
under Blocked until the blocker closes. With no number, propose the next slices to plan: the open
slices under the current sprint, in their sub-issue order, whose status is Backlog, and wait.

## 0. Read everything first

Before the first recommendation, as the `read-the-plan-first` memory says:

- `AGENTS.md`, `docs/README.md`, and the slice: `gh issue view <n> --json number,title,body,state,comments`.
- Its parent, the sprint, for the check this slice delivers, and its sub-issues in order:
  `gh api repos/Adamniels/Personal-platform/issues/<n>/sub_issues --jq '.[] | "#\(.number) \(.state) \(.title)"'`,
  then each leaf's body, going as deep as the nesting does.
- Every issue body in the repository, once per session, and grep it for what the slice touches:
  `gh issue list --state all --limit 300 --json number,title,body`.
- Every doc passage the slice, its leaves and the sprint point to, and any other passage that
  touches the same thing. Grep the docs, do not work from memory of them.
- State: `bash scripts/agent/board.sh get <n>`, and its blockers:
  `gh api graphql -f query='query{repository(owner:"Adamniels",name:"Personal-platform"){issue(number:<n>){blockedBy(first:50){nodes{number state title}}}}}'`.

| State | What to do |
|---|---|
| Closed, In progress or In review | Stop: it is not plannable. Say why |
| Ready | It was planned before. Ask whether to re-plan it; if yes, the new Decisions comment replaces the old one, which you link |
| Backlog or no status | Plan it |

## 1. Shape the leaves

The leaves are what the agent works through, one commit each, so each must be doable without a
question. With the owner, go through them in order and propose, each with a recommendation:

- **Adjust**: a leaf too big for one commit, too vague to finish, or overlapping another.
- **Order**: the sub-issue order is the order the agent works in. Add blocked-by links inside the
  slice where order is a real dependency, so "not blocked" keeps meaning "can start".
- **Tests**: every leaf whose work meets a criterion in "What gets tests" in
  `High-level/docs/how-we-build.md` names its tests in its own done condition.
- **The check**: the slice's body names the sprint check it delivers, or for a slice without one,
  a done condition written as concretely as a check, as `how-we-build.md` says of the skeleton.
- **What the agent cannot verify**: anything only a browser can show is named, so the agent lists
  it as left to the owner.

## 2. Settle every open question

Collect the questions: the ones the leaves leave open, the ones the docs leave open, and every
collision between the slice and a doc or another issue. Ask them **one at a time**, each with a
proposal and a recommendation, as `AGENTS.md` asks. A collision with a doc is weighed by the cost
of undoing it, see `docs/README.md`: either the doc changes, which the agent then does in the same
pull request, or the plan does.

Also settle up front what the agent would otherwise have to stop for: a new dependency, with its
version and why; a deletion; anything touching the architecture. A decision made here is one the
agent does not need to ask about.

## 3. Write it down

Show every GitHub change as commands first, in one dry run, and apply them on the owner's yes, as
the `github-changes` memory says. Then verify each with a read.

1. **Issue changes**: edited leaf bodies, new leaves (`gh issue create`, then add as a sub-issue
   with `gh api -X POST repos/Adamniels/Personal-platform/issues/<parent>/sub_issues -F sub_issue_id=<id>`,
   the id from `gh api repos/Adamniels/Personal-platform/issues/<n> --jq .id`), order
   (`gh api -X PATCH repos/Adamniels/Personal-platform/issues/<parent>/sub_issues/priority -F sub_issue_id=<id> -F after_id=<id>`),
   and blocked-by links (`gh api -X POST repos/Adamniels/Personal-platform/issues/<n>/dependencies/blocked_by -F issue_id=<id>`).
   New issues follow the `github-board` memory for Sprint, Area and Size.
2. **The Decisions comment** on the slice, from the template below. It is the agent's brief, so it
   holds every answer, not only the hard ones.
3. **The planning leaf**, if the slice has one, closed with
   `gh issue close <leaf> --reason completed --comment "Planned: <link to the Decisions comment>"`.
4. **Ready**: `bash scripts/agent/board.sh status <n> Ready`.

Finish by saying what is now Ready, and what `bash scripts/agent/next.sh` proposes.

## Decisions comment

```markdown
## Decisions

Planned on <YYYY-MM-DD> with the owner. This is the brief `/pick-up-slice` works from.

### The check
<the sprint check this slice delivers, or its done condition, and how the agent walks it with curl>
Left to the owner: <what only a browser can show, or "nothing">

### Branch
`<n>-<slug>`, closed by its pull request. <Or, for a slice that continues its parent's branch:
`<branch>`, and the pull request closes #<parent>.>

### Leaves, in order
1. #<leaf> <title>: <what done means, and its tests if it names any>

### Questions and answers
1. **<question>** Proposed: <proposal>. Decided: <the owner's answer>.

### Settled ahead
- Dependencies: <name and version, and why; or "none new">
- Docs to change in the pull request: <passage and what changes; or "none">
- Out of scope: <what is deliberately left out>
```

## Never

- Write or change code. Planning ends at the Decisions comment.
- Leave a question for the agent "to decide at pickup". If it cannot be decided now, the slice is
  not Ready.
- Set Ready before the Decisions comment is posted and the issue changes are verified.
