---
name: github-board
description: "Project 5 board: auto-add covers every issue and starts it in Backlog; statuses include Blocked; slices grouped by feature parent; Area and Size conventions"
metadata:
  type: reference
---

Project 5, "Personal-platform-project". The "Auto-add to project" workflow adds every issue in this repo at any depth, so sub-issues never need adding by hand, and Backlog is the board's default status, so a new issue starts there.

Statuses: Backlog, Ready, In progress, In review, Blocked, Done. Only `/plan-feature` sets a slice Ready; `/pick-up-slice` and the runner move it through In progress, In review and Blocked with `scripts/agent/board.sh`. Done is set by the merge gate for the pull requests it merges, and by Project 5's "Item closed → Done" workflow for those the owner merges by hand.

Slices are grouped by their feature parent issue, not by a field. Conventions: the feature parent gets Area and nothing else set by hand, no Status and no Size, so it stays in Backlog and is never set Ready, and `next.sh` never picks it; each slice gets Area, for where its main change is, and Size, from XS to M, since a slice that would be L or XL is split. The options are Area Core, Canary, Infra, Docs or Planning, and Size XS, S, M, L or XL; a new area, such as the brain's, is added on the board before its first feature. Sprint and Priority are not set. Field and option IDs come from `gh project field-list 5 --owner Adamniels`.

**Why:** the auto-add behaviour was checked on 2026-09-21 and is not visible through the API. Blocked, Backlog as the default and grouping by feature parent were set up with the owner on 2026-11-10 for unattended runs.

**How to apply:** when creating issues, set Area and Size by these conventions, leave Status to auto-add and `/plan-feature`, and do not add items to the board by hand. Related: [[github-changes]], [[feature-and-slices]].
