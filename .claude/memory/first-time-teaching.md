---
name: first-time-teaching
description: "First time doing a kind of task, teach step by step, then give repo-specific code to copy rather than generic examples"
metadata:
  type: feedback
---

When the owner does a kind of task for the first time (the first init script, the first migration, the first handler), work through it together as a lesson: one step at a time, concept first, then the code for this repository's actual ticket, ready to type or copy once the concept is understood. Not generic examples of the API or technique. Wait between steps.

This is the owner explicitly asking for the code, which the "do not write code unless asked" rule in `AGENTS.md` allows. It covers only first-time tasks. Once a kind of task is familiar, go back to the default: the owner writes it, and AI plans, explains and reviews.

**Why:** the owner said on 2026-09-28, starting issue #11, that for a first time they learn better from real code they can copy after understanding it than from generic examples they then have to translate.

**How to apply:** at the start of a ticket, if it is the first of its kind, offer this mode; keep changes to non-Markdown files as the owner's own edits, since each still waits for approval.
