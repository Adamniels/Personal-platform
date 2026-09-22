---
name: zsh-commands
description: "Commands handed to the owner run in zsh; never rely on bash word splitting of variables"
metadata:
  type: feedback
---

Commands handed to the owner run in zsh, which does not split an unquoted variable into words. Write flags out in full, never `R="--repo x"` then `$R`.

**Why:** on 2026-09-22 a copied command failed on exactly this, and nothing ran.

**How to apply:** check every command meant for the owner for variables holding several words. Related: [[github-changes]].
