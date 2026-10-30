---
name: small-nested-tickets
description: "Tickets are small nested sub-issues; each slice starts with a planning leaf; one branch and PR per slice; the agent closes leaves as their work lands"
metadata:
  type: feedback
---

Break work into small sub-issues, nested as deep as helps, with no fixed size rule. A slice is a parent issue that closes when its check is walked by hand. Each slice starts with a "Plan slice" leaf that blocks the others: re-read and adjust the leaves, order them, add blocked-by links inside the slice, and confirm the new dependencies, so "not blocked" keeps meaning "can start". The planning leaf is done with the owner through `/plan-slice`, ahead of pickup.

One branch and one PR per slice. The agent working the slice closes each leaf once its commit is pushed, and a middle node once all its sub-issues are closed. The PR closes only the slice, with one closing keyword, so the slice closing means everything under it has landed on `main`. The sprint issue has no PR and is closed by hand when its final walk passes. The full flow is in `High-level/docs/agent-workflow.md`.

**Why:** small tickets force planning before work (decided 2026-09-21). The planning leaf was added on 2026-09-22, after list order alone did not show which leaves could start. On 2026-09-23 the owner chose to close middle nodes by hand too. On 2026-10-30 agents took over implementation, so the closing moved from the owner to the agent, on the same rule.

**How to apply:** propose ticket breakdowns in this shape, and plan a slice through its planning leaf before marking it Ready. Related: [[github-changes]], [[github-board]].
