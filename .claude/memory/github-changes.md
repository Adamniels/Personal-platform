---
name: github-changes
description: "Bulk GitHub changes are scripted, dry-run, run on the owner's word, then verified; macOS bash 3.2 pitfalls"
metadata:
  type: feedback
---

Bulk changes on GitHub are scripted. Show the script first, dry-run it (a fake `gh` first on `PATH`, or a `DRY=1` mode for API calls), run it only on the owner's word, then verify on GitHub against what was planned. Scripts run on macOS's bash 3.2: no heredocs inside `$( )`, and no tab as a `read` separator, since empty fields collapse. A partial failure gets a resume step, never a rerun, because a rerun creates duplicates.

**Why:** this was how every issue in the core sprint got created, and each of those bash pitfalls actually happened.

**How to apply:** for any change touching more than a couple of issues, follow this sequence. Related: [[zsh-commands]], [[github-board]].
