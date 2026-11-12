---
name: cross-feature-calls
description: "Features may call each other's backends under four conditions; being allowed to call is not a reason to split a feature"
metadata:
  type: feedback
---

A feature's backend may call another's, under the four conditions in "How features reach each other" in `High-level/docs/feature-contract.md`: a published contract, a declared direction with no circle, a caller that keeps working when the callee is down, and each feature owning its data. This memory is about how to use that when planning.

**Why:** on 2026-10-08, while designing desk, the owner found the first rule, all cross feature access through the brain, too strict, and it was narrowed to a ban on backend calls held loosely. On 2026-11-12, laying out the features after the core, he turned the ban around: what matters most is that the platform is built in the best way and becomes what he wants, and features that would be better for knowing about each other should be allowed to, done properly.

**How to apply:** when a feature needs another feature's data, pick the path that fits: the brain for knowledge about the owner, the browser for showing another feature's data beside your own, a backend call when the backend itself needs the answer. A new call direction is a decision made with the owner in planning and recorded under "Declared calls" in that doc. Do not split things that need each other to draw every page just because a call would be allowed: that is one feature with modules. Related: [[docs-are-tripwires]].
