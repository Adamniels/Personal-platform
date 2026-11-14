# Development flow: design

Status: living. The feature is being planned with the owner, round by round, and this document
grows with each round. It is frozen on the day the slices are cut, and from then on the feature's
own docs are the truth.

Last updated: 2026 11 14

What the development flow is, why it is wanted, and every decision made about it so far, each with
its date. Where a round of planning has not happened yet, the section says what it will settle and
what the owner has already said about it, so nothing here reads as decided before it is.

Which rounds exist and where each stands is kept in the owner's own planning notes, outside this
repository. The owner's intent is on the "Development flow" page in his planning notes in Notion.

---

## Why

Today the owner finds out what the agents did by asking an agent to read GitHub, and he answers by
pasting commands into a terminal. The flow that builds this repository, the scripts in
`scripts/agent/` described in `High-level/docs/agent-workflow.md`, works, but it is bash written for
one repository on one machine, and nothing else can use it.

He wants the same thing for every project he runs: connect a project, and have its development
automated as far as that project's settings say, from a card to merged code. He plans, the program
and its agents build, and he controls and follows all of it from a page in the platform. The first
projects are Fodr and new project ideas that he wants built this way from their first day.

## What it is

A feature of the platform, with a backend, a database and pages of its own, and workers that run on
the owner's machines.

- **The program** is the feature's backend. It knows the connected projects and their settings,
  decides what is worked on next, owns everything that happens on the code host, and decides what
  merges.
- **A worker** is a small program on a machine. It asks the program for work, runs an agent on it
  in a working copy of the repository, and reports back.
- **The developer agent** only develops: it reads a card, edits files, runs the project's tests,
  and reports what it did and what it decided. It creates no branch, pushes nothing and merges
  nothing.

## Goals

- Connect a GitHub repository as a project and give it settings.
- Carry a card as far as the project's settings say: refuse it, ask about it first, build it,
  review it, fix it, merge it or leave it for the owner.
- Let projects differ. One project runs fully automatic, another waits for the owner on some pull
  requests, a third lets only him merge, all on the same developer agent.
- Run agents on the owner's machines, several at once, with a new machine easy to add.
- Verify the work with the project's tests, a reviewer with fresh context, and screenshots.
- Tell the owner when a whole feature is finished, so he can review it as one piece.
- Show him what waits for him, what is running, the queue, the history, and usage and cost.
- Let him answer, send work back and merge from those pages, without a terminal.

## Non goals

- **Writing the cards.** Planning stays in a Claude Code session, and the cards land on the code
  host. So the program has to cope with whatever quality of card it is given.
- **Putting a project into production after a merge.** That is each project's own setup.
- **The project pages and boards of Workbench.** They belong to that feature and link here.
- **A login for other people.** Later, as a separate frontend on the same backend.
- **Azure.** GitHub is the only code host built. The code host stays behind one interface so
  another can be added.
- **Esatto and client code.** It is for the owner's own projects, on his machines and his
  subscription.
- **Phone notifications.** GitHub's mentions carry that until the platform has notifications of its
  own, so this feature waits on no other.
- **An agent that plays the owner.** It would run on the same model as the developer agent and
  answer what the developer already recommended.

## Anti goals

What would make it a failure even if it works. Later decisions are tested against these.

1. **An agent gets around a project's settings**, for example by merging where only the owner may
   merge. This is prevented by how the thing is built, never by rules an agent is asked to follow.
2. **Something merges unchecked** in a project whose settings say it must be checked.
3. **A secret leaks** into a pull request, a log or a page.
4. **It burns usage silently.** A stuck run loops for hours without anyone being told.
5. **The owner still needs a terminal** to learn what happened or to answer.
6. **Adding a project or a machine takes an afternoon.**
7. **One project stops the others.** A bad card or a broken run in one project holds up another's
   queue.
8. **Something happens on the code host that cannot be traced** back to a card and a run.

## Definitions

- **Project**: one connected repository with its settings.
- **Card**: one piece of work on the code host, for GitHub an issue.
- **Feature**: a group of cards that are finished together, such as a parent issue and its
  sub-issues.
- **Run**: one agent working on one card, once. A card may take several runs.
- **Settings**: what a project chose for each thing the program can do alone or leave to the owner.
- **Preset**: a named combination of settings.

These are working definitions. Rounds 2 and 6 make them exact.

---

## Decided

### It is for the owner's own projects (2026 11 14)

He is the only user. The platform scopes every feature's data by user id anyway, so another account
could connect projects of its own later without a redesign.

### One program for every project, and the platform moves onto it last (2026 11 14)

There is one program, and each project has its own settings. Fodr is the first project, since a
mistake there cannot break the tool itself. The scripts in `scripts/agent/` keep building this
repository, this feature included, until the program has proven itself on another project. Then the
platform moves onto it, as a part of its own, and the scripts are retired.

The alternatives were to keep the platform on the scripts for good, which leaves two systems doing
one job, and to make the platform the first project, where a merged mistake breaks the thing that
would fix it.

**The running program is always a released copy, never the branch it is judging.** Putting a new
version of the platform into production is the owner's step. So when the platform is itself a
project, an agent's change to the program's own code decides nothing until he has released it.

### Everything is planned and built, the waiting list first (2026 11 14)

The whole feature is planned before slices are cut, and all of it is built. The first part is the
list of what waits for the owner, with the agent's decisions spelled out and the actions to answer,
working against this repository's current runner, which already records everything on GitHub. The
engine that runs agents follows directly after.

### It depends on no other feature, and needs the platform hosted (2026 11 14)

No other feature has to exist first. The program must run all the time, so the platform has to be
hosted somewhere that stays up. Where is not decided, see round 5.

### Workers are separate from the hosted platform (2026 11 14)

Workers run on the owner's machines, the Mac mini first, whether or not the platform is hosted
there. Adding a machine so more work runs in parallel must be easy.

### The rules live in the program, the project's facts live in its repository (2026 11 14)

In the program, the same for every project: how the developer agent works, its skills and the
format of its report, how the reviewer works, the logic that decides what merges, and each
project's settings.

In the project's repository: what is true about that project, its `CLAUDE.md` or `AGENTS.md`, its
docs, how it is built and tested, and its own tests and CI. They version with the code, and the
owner's interactive sessions use them too. An agent may edit them like any other file.

This is why the first anti goal is narrow. In this repository the rules sit in `.claude/`,
`scripts/` and `.github/`, inside what the agent edits, and that is what protected paths guard. An
agent working on a connected project cannot reach rules that live in the program.

What is left of that problem, and the answer to each:

- **The agent has no access to the code host.** The program creates branches, pushes, opens pull
  requests, comments and merges. The agent only edits files in a working copy, so it cannot merge
  or touch a card even if it tried.
- **A project's own tests and CI are in its repository**, so an agent can weaken a test in the
  change that test should judge. That is not forbidden. The reviewer reads the diff, and a project
  may set paths whose changes wait for the owner. A fully automatic project leaves that empty.

---

## Open, by round

Each round is a discussion with the owner. What he has already said is listed, and is not yet a
design.

### Round 2: the workflow from the owner's seat

Settles the life of a card, every setting and its values, the presets, how he iterates on a result,
and what waits for him and how he answers.

From the owner, 2026 11 14:

- A card does not travel one fixed way. A project's settings decide how much the program does alone,
  and there will be many combinations.
- Questions before a run starts are a setting. Where they are off and a card is too thin to build
  from, the program refuses the card and says what is missing.
- Questions during the work never happen, in any project. The agent goes with its own
  recommendation and records it.
- Who merges is a setting: the program merges everything, the program merges but some pull requests
  wait for him, or only he merges. It can be set so nothing ever waits for him.
- When he looks is a setting: at each pull request, or only when every card of a feature is done.
- Three examples, to become presets: fully automatic, where he plans up front and reviews the
  finished feature; like this repository today, where some pull requests wait for him; and only he
  merges.
- How cards are picked is a setting: everything in a chosen column, or everything with a chosen
  label, in dependency order.
- When a result is not what he wanted, he says so and a run continues from the same branch,
  probably with a new agent.
- Fodr runs fully automatic. With six cards planned and the automation switched on in the evening,
  he wants to wake up to every card implemented as well as it can be. It stops only in an
  emergency. Even something critical is solved as well as the agent can, and it then opens an issue
  with an attention label instead of waiting. He reads that issue and decides whether the solution
  stands. If not, he plans the fix as a new card and puts it back into the flow.

### Round 3: trust and safety

Settles who may merge and on what evidence, what a run may touch on the machine it runs on,
credentials and bot identities per project, how text an agent reads is kept as data, and how each
anti goal is met when nothing waits for the owner.

### Round 4: architecture

Settles the program's parts, the brief a developer agent gets and the structured report it returns,
the states a card and a run go through, and what is stored against what is read from the code host.

From the owner, 2026 11 14: build more around the agent, in a real language, leaning C#. The agent
reports something the program can act on, such as the pages a change affects.

### Round 5: where it runs

Settles where the platform is hosted, how a worker is installed and joins, containers per project
against projects that need macOS itself, subscription against API, and how many runs go at once.

From the owner, 2026 11 14: many repositories, perhaps one agent each. The Mac mini, more machines,
perhaps cloud. Perhaps a container per project with its tools installed in it. The subscription most
of the time, the API possible.

Needs checking against current documentation first: what a Claude subscription allows for
unattended runs, whether cloud hosted sessions can be started and read by a program, and running
Claude Code in a Linux container on a Mac.

### Round 6: the code host

Settles how the program connects to GitHub, as an app or a bot account, by webhooks or polling, how
it reads boards, labels and dependencies, and what the interface must hold for a second code host.

### Round 7: the pages

Settles each page, what it shows and what the owner can do from it: what waits for him, what is
running, the queue, history, and usage and cost.

### Round 8: verification

Settles which checks exist and which of them a project's settings can require: the project's tests,
the reviewer, and screenshots of the pages a change affects.

### Round 9: parts and order

Settles the cut into parts by the three tests in "What is built now" in
`High-level/docs/how-we-build.md`, the order, how the platform moves over, and then the slices of
the first part through `/plan-feature`.

---

## Calls made without the owner

Check these. Each was said to him in passing, and none was asked as a question.

- **A worker connects outward and asks the program for work.** A machine at home cannot be reached
  from a host elsewhere, and it makes adding a machine a matter of installing the worker and giving
  it a key. Round 4 confirms it.
- **The program keeps the label and comment conventions of the current runner** where it can, so
  the waiting list built first keeps working when the engine arrives. Round 6 confirms it.
- **A pull request exists for every card, in every preset**, also where nothing waits for the
  owner, since the project's CI runs on it and it is the record of the change. Round 2 confirms it.

## How this document is kept

Decided with the owner on 2026 11 14, to be tried on this feature and then written into
`docs/README.md` and `/plan-feature`:

- A feature's design is one spec in `docs/superpowers/specs/`, living while the feature is planned,
  with dated decisions, and frozen when its slices are cut.
- The spec takes the place of the description and decisions in the feature's parent issue, which
  becomes a link to it and the slices in order, so nothing is written twice.
- There is no plan file, since the slices are the plan, and no notes file, since "Decisions the
  agent made" in each pull request and `/review-feature` do that job.

This differs from "Design records" in `docs/README.md`, which says a spec is never updated. That
passage changes when this way of working has been tried.

## Changes

- 2026 11 14: first version, after rounds 0 and 1.
