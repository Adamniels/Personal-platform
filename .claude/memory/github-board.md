---
name: github-board
description: "Project 5 board: auto-add covers every issue at any depth; Size only on leaves; field conventions"
metadata:
  type: reference
---

Project 5, "Personal-platform-project". The "Auto-add to project" workflow adds every issue in this repo at any depth, so sub-issues never need adding by hand. Conventions: Size only on leaves, never on parents; the sprint parent has no Area; Sprint is "Core sprint" or "Later". Field and option IDs come from `gh project field-list 5 --owner Adamniels`.

**Why:** the auto-add behaviour was checked on 2026-09-21 and is not visible through the API. The conventions were decided while cutting the core sprint.

**How to apply:** when creating issues, set Sprint, Area and Size by these conventions and do not add items to the board by hand. Related: [[github-changes]].
