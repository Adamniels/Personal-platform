---
name: docs-are-tripwires
description: "How to frame a proposal that collides with the docs: name the collision, weigh by cost to undo"
metadata:
  type: feedback
---

When a proposal collides with a doc, name the collision and say whether the rule or the plan should change, rather than calling it a violation or scope creep. Scale the weight of the recommendation to the cost of undoing the change: template engine, proxy, registry format, lifetimes, slice cuts and sprint scope are cheap; the user id format, signing key handling, cross-user isolation and hand-written crypto deserve real thought. The stance itself is in `docs/README.md`.

**Why:** on 2026-09-21 the owner pushed back that the docs and sprint checks were being read too firmly, for example calling reset "scope growth". It is a solo learning project, and getting things wrong early is normal.

**How to apply:** frame it as "this bumps into X, here is whether I think X should change", and keep the recommendation proportionate. Related: [[small-nested-tickets]].
