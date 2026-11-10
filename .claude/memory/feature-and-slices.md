---
name: feature-and-slices
description: "Work is a feature parent issue, slices as its sub-issues (one branch and PR each), and a task checklist in each slice; slices are the owner's, planned with /plan-feature"
metadata:
  type: feedback
---

Work has two issue levels and a checklist. A **feature** is a parent issue holding the owner's description in his words and the decisions that span its slices. Each **slice** is a sub-issue of it: one branch, one pull request, small enough for an agent to finish in one run and a reviewer to judge in one read, about 1 to 5 tasks. A slice's **tasks** are a checklist in its body, one commit each, never issues of their own. The slice body's headings are fixed by `.claude/skills/plan-feature/SKILL.md`, and every acceptance criterion in it is a named test.

Slices run in sub-issue order, with blocked-by links wherever one needs another to land first, so "not blocked" keeps meaning "can start". The pull request closes exactly its slice. The feature parent has no pull request and is closed by hand once its slices are done and the full suite passes on a fresh stack. The full flow is in `High-level/docs/agent-workflow.md`.

**Why:** decided with the owner on 2026-11-10, so agents can build unattended. Every open question is settled in planning, since nobody answers one on the Mac mini, and a slice is written and only ever edited from the owner's account, since the merge gate trusts its Risk only then. That is also why agents tick tasks in the pull request body, never in the slice.

**How to apply:** propose work in this shape, through `/plan-feature`. A slice with something undecided stays in Backlog; only a complete one is set Ready. Never edit a slice body from an agent's account. Related: [[github-changes]], [[github-board]].
