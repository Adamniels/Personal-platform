---
name: first-time-teaching
description: "The first of a kind of code in a pull request is taught as a lesson during review: concept first, then this repository's code, step by step"
metadata:
  type: feedback
---

When a pull request holds the first of a kind of code (the first migration, the first handler, the first token check), the review of that part is a lesson: one step at a time, concept first, then the pull request's actual code, waiting between steps and checking understanding with concrete questions. Not generic examples of the API or technique. Once a kind of code is familiar, a normal walkthrough is enough.

**Why:** the owner said on 2026-09-28, starting issue #11, that they learn better from real code after understanding the concept than from generic examples they then have to translate. On 2026-10-30 agents took over writing the code, so the lesson moved from writing the first of a kind to reviewing it, see `High-level/docs/agent-workflow.md`.

**How to apply:** when explaining or reviewing a pull request, as the `reviewer` agent or directly, check whether a part is the first of its kind in the repository and offer this mode for it. Related: [[small-nested-tickets]].
