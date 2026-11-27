# Documentation index

Status: exploratory. The walking skeleton is being built.

Last updated: 2026 11 22

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
| `devflow/`          | Dev flow, the C# feature for automating development on connected repositories, with its page in `devflow/web/` |
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
  authenticates a request, the attention endpoint behind the nav's dot, the two contracts, and
  where reminders sit
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

**Dev flow**

- `devflow/README.md` — what Dev flow serves, its pages and its tabs, usage and history among them,
  a project's settings page, what Waiting for you
  shows, the schema it pins, how to run it and its tests, its settings, its database and roles, migrating, the door, the vault that
  seals a user's secrets and its key, the code host's interface, its fake and how a test stages a
  world in it, setting up a project, with the setup run, its command, its report and its pull request, the check at registration, the settings step, sending a failed check back, a project's values, sealed in the vault, their step and routes, carried by the jobs that run its code and scrubbed from what is stored and posted, its steps and its routes, linking a user's GitHub account and the one way to a token that acts as them,
  connecting a repository with its routes and tables, a project's settings and its cards with
  their routes and tables, its features, labelled parents with their sub-issues as cards, with
  their table, watching each project's default branch and each open feature's with the resume
  route and its tables, what waits for the owner, the loop that
  reads it and refreshing a project, merging and sending back from the page and what each refusal
  means, a project's worker key, the worker scheme and the worker API with their routes and
  tables, the pull request Dev flow opens for a built card, with its body, the screenshots the worker
  takes of a built card's pages, with `start`, `url` and `DEVFLOW=1`, and the developer's own look at
  its pages before it reports, a card's page, a feature's page with sending a landed card back
  from it, and
  fixing, with its rounds, the owner's comment as the one input read and sending a card back, the
  merge check, with its conditions in order, merging as the app and the issues for decisions marked
  for the owner, a feature's card checked through its parent and its issue closed by the app once it
  lands, the end of a feature, with bringing the default branch in, the feature's pull request,
  keeping it current and a conflict resolved by an agent in a job of its own, reviewing a whole
  feature, with when it is reviewed, its job, the diff from the merge base and the remerge diff
  apart, its brief, its verdict kept and posted, when it is ready and its group on Waiting for you,
  fixing a feature, with its rounds, its job and brief, the re-review, the owner's comment on
  its pull request and sending the whole feature back, merging a feature, with when it is offered,
  its confirmation, and what follows a merge or a pull request closed without merging,
  stops, with what stops a card, its mark on GitHub, running it again on the owner's
  comment, running again or dropping what the owner stopped and a project's wait after failures outside its cards, starting a worker, with the base
  image, the format of `.devflow/`, the recipe route, the
  two commands, the memory rule and when a worker needs a rebuild, the worker program with its
  settings, its loop, its exit codes, the agent interface, when a run succeeds, a build job step by
  step with the git rules it keeps, publishing it and its tests, the Claude login, its routes and its table, jobs, with handing out a card, a feature's cards one at a time and its branch, the job, a
  fresh push key, a job's progress and result and how each moves its card, a run's log, its heartbeat and its feed, stopping a run and what it leaves, pausing a project or everything and stopping a project now, a lost run, the brief,
  the report, the fake worker and the runs table, the review, with its job, the read only worker, the
  verdict's form, the reviews table and the comment the app posts, registering the GitHub App, how it
  authenticates a request with `PlatformAuth` and with a worker's key, and the libraries it is built
  on
- `devflow/docs/devflow-architecture.md` — what Dev flow is, what of it exists, the code host seam
  and why its fake sits there, acting as the owner on GitHub, projects, a project's summary and its card groups, and how a
  loop learns whose they are, reading this repository's runner and the conventions it knows, reading every project
  into a stored picture the pages read instead of GitHub, cards and how one is picked, with why only
  the owner's own count and why the settings table holds only what is read, acting from the page as the owner and
  never as the app, the program's own writes as the app with a key narrowed to one purpose,
  watching the default branch, what is fine there, what stops a project and how it is resumed,
  and a feature's branch, watched from its creation, with why a job's own push is judged when its
  run ends and why a stop on one branch is never replaced by another's, workers
  and how a worker's key leads to its project's user, the worker
  program, the agent interface and the rule a run succeeds by, setting up a project, with why it is a
  worker's job, why every version of `.devflow/` is checked once and first and why the version covers
  `project.json`, why a project's values are a step that holds nothing back, sealed in their project's own table and scrubbed in two places with one function, a build job's two users, why its
  check runs on a clean copy, why its branch is pushed whatever the check said and why a worker
  running a check stays heard, the Claude login Dev flow holds for
  a user's runs, handing out work, with the lock that takes a card, the pauses as the rule's first reasons, a feature's cards one at a time and why a card waits for every earlier one not done, stopped or held, a feature's branch and why one Dev flow did not make is never taken, why nothing is read from GitHub
  under it, the brief quoting the card as picked, how a card reaches its pull request and
  how the waiting list reads Dev flow's own cards beside the runner's, why the report decides nothing, where a failure
  lies and why a run is lost when its worker goes quiet, a run's log kept each line once and its lines reaching a live page from memory, days counted in one zone,
  read through `DevFlowTime`, the worker's pictures and why they never decide a merge, why the developer looks at its own pages too, the review and why its verdict lives in the database tied to its
  commit, fixing, why rounds are limited and why the owner's comment is the one input read from
  GitHub, the merge check, each condition and why it fails closed, a card of a feature, why the app
  closes its issue and why its decisions open no issue, sending a card back, with why only his
  comment sends it back and why it gets a new branch, the end of a feature, with why it is a merge
  and never a rebase, why its pull request is opened at the end, not as a draft as the spec has it,
  and why the default branch is kept in a feature that waits, a conflict, with a run for a feature,
  why the files in conflict are those both sides changed, why the worker accepts exactly one merge
  commit and pushes it only once its check passed, and why the resolution is kept apart for the
  review, reviewing a feature, which the spec has not, with why its diff is from the merge base, why
  a clean bringing in needs no new review and a resolved conflict does, and why its readiness is
  decided each minute from what Dev flow stored, fixing a feature, with why its rounds end with the
  owner rather than a stop, why its fix is pushed only once its check passed, and why his comment is
  taken by id and not by time, merging a feature, with why it is offered without the review's
  approval but never without CI, and why a closed pull request gives the cards back, stops, with
  what lies in the card
  and what outside it, why a failure outside never counts, why a stop by the owner is no failure and is not marked on the issue, why "Stop now" on a project pauses it first, why one failure in the card is tried
  again, why a stop is marked on the card, running it again on his comment and reading the usage
  limit's reset, and its security decisions and accepted risks, kept for an audit,
  the vault's, the fake's, the worker key's, the agent run's environment and token, the Claude
  token's and a job's, a project's values' and their scrubbing, with the risk accepted, a run's log scrubbed of the token and the values only, the brief's, the report's text in a pull request body, push keys before the watch, the project run as `agent` in a clean copy, its test mode off
  without `DEVFLOW=1`, pictures served through part 1's checks, root never running git in the
  agent's copy, the copy without credentials, a push never forced, the reviewer that cannot write, a fix started only from the commit handed, only the owner's comment read,
  a verdict that is never a comment, no language model deciding a merge, CI that never reports not
  being a pass, a merge naming the approved commit, only a recorded merge being fine on the default
  branch, a decision issue's text, a stop's comment carrying a run's reason as data, a stopped
  card's issue read for his comment alone, a worker's `resets_at` believed within limits and the
  one job that pushes a merge of the default branch held to one merge commit, and a feature
  reaching the default branch only by the owner's own merge, among
  them
- `docs/superpowers/specs/2026-11-14-development-flow-design.md` — the design of the whole
  development flow, frozen on 2026 11 15 when the slices of its first part were cut; the
  architecture document above is the living description from then on

**Libs**

- `libs/python/platform-auth/README.md` — checking the core's token and the `Origin` header, the one
  copy every Python service installs: what it holds, what it must never grow into, how to run its
  tests and how a service installs it
- `libs/dotnet/PlatformAuth/README.md` — the same for every C# service: what it holds, what it must
  never grow into, how to run its tests, how a service references it, and the libraries and the
  SDK it is built on
- `libs/dotnet/PlatformData/README.md` — the database code every C# service with a database
  shares: the door, with its one transaction on behalf of nobody, the guard, the migrator and the migrate command, the test harness and the
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

- `e2e/README.md` — running the acceptance suite, its fixtures, the look checks every page is held
  to and the list of every page they run on, and the libraries it is built on

**Design records**

- `docs/superpowers/specs/` and `docs/superpowers/plans/` — each one records a design as agreed
  and the plan that built it, and is never updated. The living description is the doc it names
