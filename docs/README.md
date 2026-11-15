# Documentation index

Status: exploratory. The walking skeleton is being built.

Last updated: 2026 11 15

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

**Feature scope is not in these docs.** What a feature must deliver and the slices that get it
there live on the GitHub Project, in issues, and nowhere else: each item has a state, matters for
one feature, and then is history. These docs keep only what outlasts a feature, which includes
decisions about how the work gets done as well as the architecture.

**Nor is the list of what might get built.** Ideas and candidate features live in the owner's
planning notes in Notion, at the level of what he wants and why. They reach this repository when
they are planned, as an issue and, where design outlasts the feature, as a doc. See "Candidate
features" in `High-level/docs/platform-architecture.md`.

---

## Where to start

1. `High-level/docs/platform-architecture.md` — what the platform is and how its parts relate
2. `High-level/docs/feature-contract.md` — what a feature is and what it may depend on
3. `Core/docs/` or `Brain/docs/` — whichever you are about to build

## Layout

| Folder              | Holds                                                                        |
| ------------------- | ---------------------------------------------------------------------------- |
| `High-level/docs/`  | The platform, the services that exist, and anything shaping more than one    |
| `Core/`             | The core's code, a Rust crate, with its docs in `Core/docs/`                 |
| `Core/docs/`        | The core: auth, accounts, routing, the registry, and its own frontend        |
| `Brain/`            | The brain's code, a Python service, with its docs in `Brain/docs/`           |
| `Brain/docs/`       | The brain: memory, the trust model, events, retrieval, and its tables        |
| `Brain/web/`        | The memory centre, the brain's own React application, served at `/brain`     |
| `libs/`             | Shared libraries owned by no service: token verification, and the C# services' database code |
| `design/`           | The platform's visual style, its fonts and sprites, and the shared nav bar, served at `/design/` |
| `design/ui/`        | `@platform/ui`, the React components every frontend installs, and their gallery |
| `canary/`           | Canary, the Python feature proving another language can verify the token     |
| `review/`           | Review, the C# feature of cards that come back, with its page in `review/web/` |
| `desk/docs/`        | Desk, the feature for working through the day, described but not yet built   |
| `infra/`            | The Caddyfile, the compose file and the feature registry, and `hosted/`, the platform in containers |
| `contracts/`        | Contract fixtures, the bytes services in two languages test against          |
| `e2e/`              | The acceptance suite, Playwright against the running stack                   |
| `scripts/agent/`    | The runner, the merge gate and the other scripts the agents' workflow runs   |
| `scripts/ci/`       | The change detection and the `ci-ok` judgement that `ci.yml` runs            |
| `scripts/deploy/`   | The release script, the owner's one command that puts a commit into the hosted copy |
| `docs/`             | This index, and anything that fits nowhere else                              |
| `docs/superpowers/` | Design specs and implementation plans, each frozen on the day it was agreed  |

## The files

**High level**

- `platform-architecture.md` — the brain as a service, dependency and extraction, languages,
  multi user and isolation, the user id, the core's frontend, notifications, databases and
  migrations, the hosted copy's backups, the one repository, the first milestone, memory layers, where candidate features
  are kept, and what is deliberately not being done
- `feature-contract.md` — what a feature is, what it owns, what it may depend on, how it
  authenticates a request, the two contracts, and where reminders sit
- `how-we-build.md` — how work is cut and ordered: a walking skeleton, then features cut into
  vertical slices ordered by risk, with acceptance criteria as automated tests written first,
  and the three tests for what is built now
- `agent-workflow.md` — who does what: the owner plans features, agents implement, review and
  merge them on the Mac mini; planning, building a slice, the review loop, the merge gate, what
  waits for the owner, looking at a whole feature when he chooses to, the runner, safety, and the
  runbook

**Core**

- `core-architecture.md` — what the core owns, what stays out of it, how authentication gets
  built, the session and token model, the signing key, password reset, the core's own tables and
  the user id, routing, the feature registry, how its frontend is built, what a feature
  exposes for the front page, the backup line on the front page, and the libraries it is built on

**Canary**

- `canary/README.md` — what canary serves, how to run canary and its tests, its settings, how it
  authenticates a request with `platform-auth`, and the libraries it is built on

**Review**

- `review/README.md` — what review serves, what a card is, when a card comes back by FSRS and the grade log, what it tells the brain of each grade and through which client, its page and its sections, the schema it pins, how to run
  it and its tests, its settings, its database and roles, migrating, the one door every query goes
  through, how it authenticates a request with `PlatformAuth`, and the libraries it is built on

**Libs**

- `libs/python/platform-auth/README.md` — checking the core's token and the `Origin` header, the one
  copy every Python service installs: what it holds, what it must never grow into, how to run its
  tests and how a service installs it
- `libs/dotnet/PlatformAuth/README.md` — the same for every C# service: what it holds, what it must
  never grow into, how to run its tests, how a service references it, and the libraries and the
  SDK it is built on
- `libs/dotnet/PlatformData/README.md` — the database code every C# service with a database
  shares: the door, the guard, the migrator and the migrate command, the test harness and the
  isolation checks, what it must never grow into, how to run its tests and why the ones needing a
  database live in the services, how a service references it, and the libraries it is built on

**Contracts**

- `contracts/README.md` — what a contract fixture is, the token fixture's three files, that they
  hold public material only, canary's and review's display data, the brain's schema, and how each
  is regenerated

**Desk**

- `desk-architecture.md` — the problem and the Notion evidence behind it, why desk is one
  feature, projects, sessions, the calendar on top of Google, views, review, and what is open

**Design**

- `design/DESIGN.md` — how every page must look: the hard rules, layout, each component, the agent
  avatars and the fonts. It decides style and never what a page contains, which comes from its
  slice; section 0 draws that line
- `design/tokens.css` — every colour, size and type value as a named variable, light and dark
- `design/fonts.css` and `design/fonts/` — the three fonts, hosted here with their licences, and
  where they came from
- `design/sprites/` — the agent avatars
- `design/nav.js`, `design/theme.js` and `design/page.css` — the shared nav bar, `<platform-nav>`,
  the script that applies a remembered theme before the page paints, and the page shell around
  the bar with the page container a page opts into. What the bar holds and how a page includes it is "Navigation" in
  `platform-architecture.md`
- `design/reference/` — designed screens as examples of the style, their content illustrative only
- `design/ui/README.md` — the component package: what may go in it, how a frontend installs it, how
  to add a component and its gallery section, and the gate every frontend runs

**Infra**

- `infra/README.md` — the local hostname, running Caddy and what its data volume holds, the
  databases and the scripts that create them, the design folder and the paths Caddy serves from
  it, the feature registry, the service manifest, the hosted copy, and trusting Caddy's root
- `infra/hosted/README.md` — the hosted copy: every service in a container as one compose project
  apart from the dev stack, its files, the images, setting the real copy up and releasing to it,
  after a reboot and what to keep elsewhere, its nightly backups, the bucket and the key they
  need and restoring one, and `check.sh`, which proves on a throwaway copy that it builds, every
  service answers and a backup restores

**Brain**

- `Brain/README.md` — what the brain serves, how it knows who is asking, the memory centre and
  its sections, how its client calls the brain and how its API types are generated, how to run
  it and its tests, measuring its answers against a fictional person's memory, its settings, its
  database and roles, migrating, the one door every query goes through, exporting a memory to a
  file and importing it, purging an entry for good, and the libraries it is built on
- `brain-architecture.md` — per feature modules, retrieval, evaluation, consolidation, and the
  brain's own surface, the memory centre, with its sections and their addresses
- `trust-model.md` — which store holds what, who can change it, scope, confidence and currency,
  editing, and the review queue
- `knowledge-model.md` — what the brain is trying to know about a person, and the filter for
  what gets in at all
- `event-model.md` — the event shape, verbs, relations, and what consolidation does
- `data-model.md` — the tables in their final shape, which kind goes in which store, the verb
  list, the memory file's format, and what is computed and never stored

**E2E**

- `e2e/README.md` — running the acceptance suite, its fixtures, and the libraries it is built on

**Design records**

- `docs/superpowers/specs/` and `docs/superpowers/plans/` — each one records a design as agreed
  and the plan that built it, and is never updated. The living description is the doc it names
