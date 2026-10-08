---
name: cross-feature-calls
description: "The ban on backends calling each other is a default to challenge: raise any case where a direct call would make the platform better"
metadata:
  type: feedback
---

When a design would work better with one feature's backend calling another directly, bring it up and discuss it, rather than routing around it through the brain or the browser. The rule itself, and the shape an allowed call probably takes, are in "How features reach each other" in `High-level/docs/feature-contract.md`; this memory is only about how firmly to hold it.

**Why:** on 2026-10-08, while designing desk, the owner found the old rule, all cross feature access through the brain, too strict. Routing a record lookup through the brain removed no coupling, and extracting a feature later matters far less to the owner than the whole system working well. The rule was narrowed, and the owner asked for the remaining ban to be held loosely as well.

**How to apply:** when a feature needs another feature's data, check the three sanctioned paths first (brain for knowledge about the owner, browser composition for display, address plus snapshot for references). If a direct call would clearly be better, say so with the concrete case and the cost, and let the owner decide. Related: [[docs-are-tripwires]].
