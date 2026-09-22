---
name: small-nested-tickets
description: "Tickets are small nested sub-issues; each slice starts with a planning leaf; one branch and PR per slice"
metadata:
  type: feedback
---

Break work into small sub-issues, nested as deep as helps, with no fixed size rule. A slice is a parent issue that closes when its check is walked by hand. Each slice starts with a "Plan slice" leaf that blocks the others: re-read and adjust the leaves, order them, add blocked-by links inside the slice, and confirm the new dependencies, so "not blocked" keeps meaning "can start". One branch and one PR per slice. Leaves are closed by hand as they are done, and the PR closes the slice and its middle nodes, one closing keyword per issue.

This flow gets written into `High-level/docs/how-we-build.md` after the walking skeleton; until then this memory holds it.

**Why:** small tickets force planning before work (decided 2026-09-21). The planning leaf was added on 2026-09-22, after list order alone did not show which leaves could start.

**How to apply:** propose ticket breakdowns in this shape, and when a slice starts, begin with its planning leaf. Related: [[github-changes]], [[github-board]].
