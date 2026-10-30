# Documentation index

Status: exploratory. The walking skeleton is being built.

Last updated: 2026 10 08

These are planning notes, not specifications. Everything here is the current best idea with the
reasoning attached, and it is expected to change as building teaches things planning could not.
Some of it will turn out wrong, and that is normal.

Where something is written as a rule, it is a tripwire rather than a law. When work runs into one,
stop and decide which is wrong, the rule or the plan, using the reasoning beside it. If the rule
loses, change the document in the same pull request as the code, so the docs keep describing what
is true.

How long to think before changing something depends on how expensive it is to undo. Most
decisions are cheap to reverse and should simply be changed when a better idea appears. A few are
not, such as the user id, and the documents say so where they apply.

Planning is split by **level**, not by subsystem. The test for where something belongs:

> Who is constrained by it? If it constrains features or the platform, it is high level. If you
> only need it when you sit down to write that service, it belongs to that service.

**Nothing is written in two places.** There are no summaries or restatements, because a copy
goes stale and then quietly lies. Documents point at each other instead, in both directions.

Each document describes the current state of its area, including the parts deliberately left
open and what would settle them. There is no separate list of loose ends.

**Sprint scope is not in these docs.** What a sprint must prove and the tickets that get it there
live on the GitHub Project, in issues, and nowhere else: each item has a state, matters for one
sprint, and then is history. These docs keep only what outlasts a sprint, which includes decisions
about how the work gets done as well as the architecture.

---

## Where to start

1. `High-level/docs/platform-architecture.md` — what the platform is and how its parts relate
2. `High-level/docs/feature-contract.md` — what a feature is and what it may depend on
3. `Core/docs/` or `Brain/docs/` — whichever you are about to build

## Layout

| Folder             | Holds                                                                       |
| ------------------ | --------------------------------------------------------------------------- |
| `High-level/docs/` | The platform, the services that exist, and anything shaping more than one   |
| `Core/`            | The core's code, a Rust crate, with its docs in `Core/docs/`                |
| `Core/docs/`       | The core: auth, accounts, routing, the registry, and its own frontend       |
| `Brain/docs/`      | The brain: memory, the trust model, events, retrieval                       |
| `canary/`          | Canary, the Python feature proving another language can verify the token    |
| `desk/docs/`       | Desk, the feature for working through the day, described but not yet built  |
| `infra/`           | The Caddyfile and the compose file, and later the feature registry          |
| `docs/`            | This index, and anything that fits nowhere else                             |

## The files

**High level**

- `platform-architecture.md` — the brain as a service, dependency and extraction, languages,
  multi user and isolation, the user id, the core's frontend, notifications, databases and
  migrations, the one repository, the first milestone, memory layers, candidate features, and
  what is deliberately not being done
- `feature-contract.md` — what a feature is, what it owns, what it may depend on, how it
  authenticates a request, the two contracts, and where reminders sit
- `how-we-build.md` — how work is cut and ordered: a walking skeleton, then vertical slices
  ordered by risk, with a sprint's checks as acceptance criteria
- `agent-workflow.md` — who does what: the owner plans slices ahead and reviews, agents implement
  on the Mac mini; the life of a slice, self review, the pull request, safety, and the runbook

**Core**

- `core-architecture.md` — what the core owns, what stays out of it, how authentication gets
  built, the session and token model, the signing key, password reset, the core's own tables and
  the user id, routing, the feature registry, how its frontend is built, what a feature
  exposes for the front page, and the libraries it is built on

**Canary**

- `canary/README.md` — how to run canary and its tests, and the libraries it is built on

**Desk**

- `desk-architecture.md` — the problem and the Notion evidence behind it, why desk is one
  feature, projects, sessions, the calendar on top of Google, views, review, and what is open

**Infra**

- `infra/README.md` — the local hostname, running Caddy and what its data volume holds, and trusting its root

**Brain**

- `brain-architecture.md` — per feature modules, retrieval, evaluation, consolidation, and the
  brain's own surface
- `trust-model.md` — which store holds what, who can change it, scope, confidence and currency,
  editing, and the review queue
- `knowledge-model.md` — what the brain is trying to know about a person, and the filter for
  what gets in at all
- `event-model.md` — the event shape, verbs, relations, and what consolidation does
