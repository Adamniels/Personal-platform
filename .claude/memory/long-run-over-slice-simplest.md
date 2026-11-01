---
name: long-run-over-slice-simplest
description: "Recommend what is best in the long run and professional, not the simplest thing that works for the current slice; check the next slices before proposing"
metadata:
  type: feedback
---

When proposing a design, recommend the option that holds up in the long run and that a professional
team would choose, not the simplest one that satisfies the current slice. Before proposing, read the
next slices that will build on it and check the option still works for them. Say plainly when the
simple option is also the long run one, and name what the long run option costs.

**Why:** planning #26 on 2026-11-01, the agent first proposed isolating database tests with
transactions that are never committed. It was the cheapest fit for #26, and it breaks at #27, where
handler tests commit. The owner asked what is best in the long run and most professional, "that is
what we are always going for", and the plan changed to a per-test database cloned from a migrated
template (#111). Asked the same of the stylesheet, it gained cache headers.

**How to apply:** in `/plan-slice`, for each question, look at which later slices touch the same
thing (the issue grep from [[read-the-plan-first]] already finds them) and test the proposal
against them before recommending it. Cheap to reverse still beats complex: long run does not mean
building for slices that do not exist yet. Related: [[docs-are-tripwires]].
