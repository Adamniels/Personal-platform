---
name: read-the-plan-first
description: "Before proposing or recording any decision, read every issue body and the docs that touch it, not only the ticket at hand"
metadata:
  type: feedback
---

Before proposing, teaching or recording a decision (a dependency, its features, a tool, a workflow, where config lives, how something is run), read what already decides it: every issue body in the repo, not only the current ticket and its parent, and every doc passage that touches the topic. Dump all issue bodies once (`gh issue list --state all --limit 200 --json number,title,body`) and grep them, then grep the docs. Do this at the start of the ticket, before the first recommendation, and again before any doc edit.

**Why:** on 2026-10-01, working #14, the agent recommended dropping sqlx's `migrate` feature and recorded in `Core/docs/core-architecture.md` and `Core/sqlx.toml` that migrations run through `sqlx-cli`, without reading #16, which already planned a `migrate` subcommand of the core binary on sqlx's embedded migrator, and #24, which builds on it. It surfaced only when about to comment on #16, after two commits, and took a correction commit. The owner was rightly angry: a decision taught as settled had contradicted the plan all along.

**How to apply:** a ticket's siblings and later tickets often decide things the current ticket touches, so the current ticket is never the whole context. When something collides, stop and say so before recommending, framed as in [[docs-are-tripwires]].
