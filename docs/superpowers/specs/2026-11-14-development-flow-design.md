# Development flow: design

Status: approved by the owner on 2026 11 14, after nine rounds of planning and a builder's read.
It stays living, with dated changes, until the slices of the first part are cut. It is frozen on
that day, and from then on the feature's own docs are the truth. It was frozen on 2026 11 15, when
the slices of the first part were cut, and `devflow/docs/devflow-architecture.md` is the living
description from then on.

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
   merge. This is prevented by how the thing is built, and never only by rules an agent is asked to
   follow.
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

- **The agent has no access to the code host.** The software around it creates branches, pushes,
  opens pull requests, comments and merges. The agent edits files in a working copy and may save
  its work there as commits, which are local, so it cannot merge or touch a card even if it tried.
- **A project's own tests and CI are in its repository**, so an agent can weaken a test in the
  change that test should judge. That is not forbidden. The reviewer reads the diff, and a project
  may set paths whose changes wait for the owner. A fully automatic project leaves that empty.

---

## Open, by round

Each round is a discussion with the owner. What he has already said is listed, and is not yet a
design.

### Round 2: the workflow from the owner's seat

Held on 2026 11 14 and done. It settled the life of a card, every setting and its values, the
presets, how he iterates on a result, and what waits for him and how he answers. What he said going
in is kept first, and what was agreed follows it.

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

Agreed in round 2 so far:

- **A decision the owner would want a say in is a setting** (2026 11 14): the pull request waits for
  him, as in this repository, or the agent goes on and tells him by opening an issue with an
  attention label, as in Fodr.
- **Where a finished card lands is a setting, and it follows the card** (2026 11 14). Every card
  has a short branch and a pull request of its own. A card that belongs to a feature not yet in
  `main` starts from that feature's branch and lands there; any other card starts from `main` and
  lands there. The owner never picks a branch, he only says which feature a card belongs to. That
  is the default for features. The other value sends every card straight to `main`, which suits a
  new project with no users. A card on its own, such as a single fix in a mature project, always
  goes to `main` through its own pull request. What it costs: a feature's branch can drift from
  `main`, and two features built at once can conflict where they meet.
- **The owner always merges a feature into `main` himself** (2026 11 14), in every setting. That
  merge is his review of the feature. "Who merges" is a setting for card pull requests only: in a
  fully automatic project the program merges every card into the feature's branch. So the branch
  exists for as long as he might still want a change, and nothing from a feature reaches `main`
  before he has accepted the feature. A fix he thinks of after merging is a card on its own.
- **The program may merge a card that belongs to no feature into `main`, where the project's
  settings allow it** (2026 11 14). In a fully automatic project such a card, a single fix say, is
  built, reviewed and merged by the program; in a project where only the owner merges it waits for
  him like everything else. The alternative was that the program never merges into `main` in any
  project, safer and slower, and right only where `main` publishes straight to real users.
- **Every run is a fresh agent session** (2026 11 14), as in this repository today. What carries
  over is the card, the branch, the pull request and the comments. Whether related cards should
  share a session to save usage is round 4b.
- **Work is sent back in three ways, which are one mechanism** (2026 11 14): a card in the pick up
  state with a new comment from the owner gets a new run, and the rule above picks the branch.
  Where a card's pull request is still open and waits for him, he comments on it and the run
  continues on that branch. Where the card has already merged into the feature's branch, he sends
  it back from the feature's page with a comment, one action, and the program reopens and queues
  it; the new agent gets the card, what was built for it and the comment. Where the fix needs
  planning, he adds a new card to the feature. The feature's pull request to `main` stays open
  until every card of the feature is done again.
- **A feature is a parent issue with its cards as sub-issues** (2026 11 14), and a card on its own
  is an issue with no parent. It is what `/plan-feature` already creates, sub-issues carry an order
  and GitHub's "blocked by" links carry dependencies, the parent links the spec, and the feature's
  pull request to `main` closes it. A card is added to a feature by creating a sub-issue, on GitHub,
  from the feature's page or in a planning session. A label was the alternative: simpler to type,
  but with no order, no dependencies, and a typo makes a new feature.
- **Cards are picked by a label, and only by a label** (2026 11 14). Each project names its label.
  On a parent issue it covers every sub-issue, so a planned feature is switched on in one step; on a
  single card it covers that card. A card carrying the project's hold label is skipped, with
  everything that depends on it. Taking the label off stops the automation after the card being
  worked on. A board column was the alternative and was dropped: the label needs no board, which
  many projects will not have, and every code host has labels. Where a board exists the program may
  move cards on it, to show status and nothing else.
- **A card is picked only when it is free to start** (2026 11 14): everything it is blocked by is
  finished, and inside a feature the cards go in their sub-issue order. A card blocked by one that
  is not in the automation waits, and the queue says which card it waits for.
- **The order is priority, then oldest** (2026 11 14), for features and for cards on their own.
  Priority is read from a small fixed set of labels, high, none and low; round 6 settles where
  exactly it is read from.
- **Inside a feature one card runs at a time** (2026 11 14), since its cards build on each other
  and land in one branch. Different features and different projects run at the same time, as far as
  there are workers. Running a feature's independent cards side by side is left for later.
- **Every card is read before anything is built** (2026 11 14). A fresh agent reads the cards of a
  feature together, or a card on its own alone, with the code they touch, and answers for each
  card ready, has questions, each with its own recommendation, or too
  thin, with what is missing. With questions on, a card with questions or one that is too thin
  waits for the owner. With questions off, a card with questions starts and the agent goes with its
  recommendations and records them, and a card that is too thin is refused: the card itself gets
  the attention label and a comment listing what is missing. A card that waits or is refused holds back only the
  cards that depend on it.
- **All cards of a feature are read at once, when it is switched on** (2026 11 14), not each as its
  turn comes. The owner gets every question in one batch while he is still there, a refusal comes
  before anything is built, and cards that contradict each other are caught. It costs one more run
  per feature, which round 4b weighs.
- **Questions are answered on the page of what waits for him** (2026 11 14): each shows the agent's
  recommendation, with one action to accept it and a box for another answer. The answers are added
  to the card as a comment, where the building agent and a later reader find them.
- **A card stops only when the program cannot produce a working result** (2026 11 14): its tests
  still fail when the fix rounds run out, the card contradicts itself or cannot be built, or
  something it needs is missing. Only that card and the cards depending on it stop, the rest of the
  feature goes on, and the owner gets an attention issue saying why. A problem outside the card,
  such as a usage limit, the network or a broken machine, pauses and retries without reaching him.
- **Stopping for something missing must be rare, so it is checked before work starts**
  (2026 11 14). When a project is registered, the program checks that a worker can build and test
  it on a clean copy: the tools, the secrets it needs, the tests running. Before each card starts,
  it checks the card can be built from, which is the same check that refuses a thin card. A stop
  that still happens is a reason to add a check.

- **The settings of one project** (2026 11 14):

  | Setting | Values |
  |---|---|
  | Pick up label, hold label, attention label | The three label names |
  | Questions before starting | On, or off |
  | Where a feature's cards land | The feature's branch, or straight to `main` |
  | A card's pull request waits for the owner | Never, sometimes, or always |
  | What "sometimes" means | Any of: the card is marked for his review, the change touches paths he listed, the agent made a decision he would want a say in |

  A decision he would want a say in makes the pull request wait where that is chosen under
  "sometimes" or the project is on "always". Otherwise the card merges and he gets an attention
  issue. A card that waits holds back only the cards that depend on it.
- **Three things hold in every project and are not settings** (2026 11 14): the agent never asks
  during the work; a reviewer with fresh context reads every card's pull request, and the developer
  gets a limited number of rounds to fix what it finds; and the owner merges a feature into `main`
  himself.
- **Presets** (2026 11 14). A preset fills in the settings, and any single one can be changed
  afterwards.

  | Preset | Questions | Lands in | Waits for the owner |
  |---|---|---|---|
  | Fully automatic | Off | Feature branch | Never |
  | Guided | Off | Feature branch | Sometimes, all three |
  | Only I merge | On | Feature branch | Always |

### Round 3: trust and safety

Held on 2026 11 14 and done. It settled which secrets a run may have, what a run may reach on the
machine it runs on, the worker model, whose words count as instructions, what keeps usage from
being burned, and who the program acts as on the code host.

Agreed in round 3:

- **A run never gets a real secret** (2026 11 14). A project must be testable with throwaway
  values: a local database with a made up password, fake keys, or outside services mocked. Where a
  test truly needs a real service, the project gets a key made for the automation, with a low
  spending limit, that can be cancelled without touching production. The program holds these values
  and hands them to a run, so they are in no repository and are never copied to a machine by hand.
  It removes known secret values from everything it stores or posts. A project that can only be
  tested against production cannot be connected until that is fixed, and the check at registration
  catches it. Allowing real secrets and relying on scrubbing was the alternative: scrubbing only
  catches values it knows, and does nothing about an agent using a secret instead of printing it.
- **Every worker belongs to exactly one project, from the moment it is created** (2026 11 14). A
  project's cards go only to that project's workers, so no project waits for another project's
  worker, and more workers for a project means more of its features built at once. How many agents
  run at once across all projects is a separate limit, see "Clarified after the builder check". Shared workers, with tags or
  lists to match them to projects, were considered and dropped at the owner's wish: a dedicated
  worker has exactly its project's tools, never holds another project's code, and can keep things
  from one card to the next, which round 4b may use.
- **Where a worker is installed decides what a run can reach** (2026 11 14), the owner's idea. The
  normal worker runs in a Linux container and reaches only what is in it. A project that needs
  macOS itself, such as Tick or Operation Rollout, has its worker installed directly on a Mac, with
  weaker separation, accepted since it is the owner's own code on his own machine. One machine holds
  several workers, limited by memory. The program hands a run nothing beyond its working copy and
  the values it needs.
- **A container is kept between cards, and each card gets a clean working copy** (2026 11 14). The
  installed tools and downloaded packages stay, which makes the next card faster.
- **The program watches the code host, and a worker only asks the program for work** (2026 11 14).
  The one thing a worker does on the code host is fetch and push code, see "How code reaches a
  worker and comes back" under round 4.
- **A project's tools come from a recipe in its own repository** (2026 11 14), a Dockerfile, built
  on top of a base image holding the worker and Claude Code. It follows the code: a card that makes
  the project need a new tool adds it to the recipe in the same pull request, and a changed recipe
  rebuilds the worker. Inside a container the agent may install what it is missing so it does not
  get stuck, and must then add it to the recipe. On a worker installed directly on a Mac the agent
  installs no system tools: the check at registration says what is missing and the owner installs
  it.
- **Connecting a project starts with a setup run** (2026 11 14). An agent reads the repository,
  opens a pull request with the recipe and suggests the settings. The owner merges it and accepts or
  changes the settings, the program builds the recipe and runs the project's tests in it, which is
  the check at registration, and he starts a worker. The setup run happens in a plain container from
  the base image.
- **A worker is started by one command the page shows**, with the project's key in it, run on the
  machine (2026 11 14). The step beyond it is a machine program, installed once per machine, that
  builds, starts, stops and rebuilds workers when the page asks, so no terminal is needed and adding
  a machine is installing it once. The owner confirmed the order on 2026 11 14: the command is built first, and
  the machine program is designed for from the start so it is not forgotten. Round 9 places it.
- **The worker and the machine program are part of this feature and live in this repository**
  (2026 11 14), since they and the program must agree on how they talk. They reach a machine as
  published packages, the base image and the machine program, so nothing is cloned there.
- **Pausing is a switch on the project** (2026 11 14). Paused, the program hands out no more of its
  cards: the card in progress finishes and nothing new starts. "Stop now" ends the running card at
  once and keeps its work on the branch. One switch pauses every project. A worker that asks and
  gets nothing spends no usage.
- **Only a project's trusted accounts give instructions** (2026 11 14). Each project has a list of
  them, by default only the owner; a shared project such as Operation Rollout can add a teammate. A
  card is picked up only if a trusted account wrote it and nobody outside the list has edited it.
  Only a trusted account's label switches the automation on, and only its comment sends work back
  or answers a question. Everything else is data: an agent may read a stranger's comment, the code,
  a log or a web page as information, never as something to obey. A card ignored for this reason
  says so in the queue.
- **A run and a card have stops that are on by default** (2026 11 14), taken from this
  repository's runner: a time limit per run, a limit on fix rounds per card, and a card that comes
  back twice with nothing new stops instead of trying again. A stopped card gets an attention
  issue. A problem outside the card never counts against it.
- **Every run is measured from the first day** (2026 11 14): how long it took and the tokens it
  used, per card, feature and project. The usage page shows it, and no limit can be set sensibly
  without it.
- **A project can have a daily budget, and it starts switched off** (2026 11 14). When it is
  reached the project pauses itself and tells the owner. It stays off until there are real numbers
  to set it from.
- **Active projects share the allowance by taking equal turns** (2026 11 14). When usage is tight
  they take turns starting a run, and a project with nothing to do gives its turn away. The owner
  controls the split by choosing which projects are active. Priority between projects was proposed
  and dropped by him, since a low priority project could then starve. Priority labels order
  features and cards inside one project only, which narrows the order agreed in round 2.
- **The program acts on GitHub as a GitHub App** (2026 11 14), installed on each connected
  repository from GitHub's own screen. Its permissions are narrow and per repository, it holds no
  long lived password, since it makes itself a short lived key when it needs one, and what it does
  is marked as the app's. GitHub Apps are free on a personal account, private repositories
  included, checked against GitHub's pricing page on 2026 11 14. A bot user, as this repository has
  today, was the alternative: each repository needs an invitation by hand, and its login is a long
  lived secret.
- **What the owner does from the page is recorded on GitHub as done by him** (2026 11 14). He
  connects his own GitHub account to the platform once. So "the owner merges a feature into `main`"
  is true in GitHub's own history.
- **Developer and reviewer share the app's identity** (2026 11 14). No agent has access to the code
  host, the program posts for both, and it knows which run wrote what, so the guards this
  repository needs against an agent approving its own work have nothing to guard.
- **Nothing depends on branch protection** (2026 11 14). Rulesets and protected branches on a
  private repository need a paid GitHub plan, so the program enforces its rules itself: that only
  the owner merges a feature into `main` holds because the program never performs that merge. A
  project that has branch protection keeps it as an extra layer.
- **Which addresses a run may call is not restricted for now** (2026 11 14). It needs the network
  for packages and the model, and while a run holds no real secret there is little to gain. The
  Claude login is the one credential a run must hold; round 5 looks at how to narrow it.

### Round 4: architecture

Held on 2026 11 14 and done. It settled the program and the worker and what each does, the brief a
developer agent gets and the report it returns, where the truth is kept, what it is built in, how
code reaches a worker, and the states of a card.

From the owner, 2026 11 14: build more around the agent, in a real language, leaning C#. The agent
reports something the program can act on, such as the pages a change affects.

Agreed in round 4:

- **The brief a developer agent gets** (2026 11 14): the card, with the owner's answers to its
  questions; the feature it belongs to, with the link to the spec and a short account of what the
  earlier cards built; where the project's own facts are, its `CLAUDE.md` and how it is built and
  tested; how to work, tests first and one commit per task, which comes from the program and is the
  same for every project; and for work sent back, what was built before and the owner's comment.
- **The report it hands back is a fixed form the program reads** (2026 11 14), not free text: the
  outcome, done or stopped with the reason; what was done, task by task; the decisions it made, each
  with what it chose, the alternative and why, and marked where the owner would want a say; what it
  tested and the result; the pages or screens the change affects; anything it had to install; and
  the follow ups it suggests, which the program can turn into attention issues.
- **The software around the agent does not take the report on trust** (2026 11 14). The worker runs
  the project's tests itself and takes the screenshots of the pages the report names, the program
  uses those results and not the agent's claim, and it gives the reviewer the report together with
  the real diff. The report guides the program, and
  nothing merges because an agent said so, which is how the second anti goal is met where nothing
  waits for the owner.
- **Where the truth is kept** (2026 11 14). GitHub holds the work itself: the cards, their labels,
  which feature each belongs to and what blocks what, and the code, branches and pull requests. The
  owner edits those there, so the program reads them and never keeps a copy as the authority. The
  program's database holds everything about doing the work: projects, settings and workers, the
  queue and the state of every card and run, reports, questions and answers, logs and usage. The
  program leaves a short readable trail on GitHub: a summary of the report on each pull request,
  attention issues as real issues, a refusal or a stop as the attention label and a comment on the
  card itself, and the owner's answers as a comment on the card. So
  GitHub alone explains what happened, and everything the program did leads back to a card. The
  trail is a copy for reading: for runs the database is right, for cards GitHub is. Everything on
  GitHub, as this repository's runner does it, cannot hold usage, logs or a queue across projects;
  everything in the database would leave GitHub unable to explain itself.
- **The first part reads this repository's labels and comments straight from GitHub** (2026 11 14),
  since the current runner has no database. It is the same reading the program does for cards.
- **The page can do everything, and GitHub works for what is naturally a GitHub action**
  (2026 11 14). Answering a question is done on the page, where the answer is tied to the exact
  question; a comment from the owner on a waiting card counts as more information, and the program
  reads the card again. Work is sent back from the page, or by commenting on the open pull request,
  or by reopening the card with a comment. Merging works from the page and with GitHub's own button,
  which the program notices. A feature is switched on and a card held from the page or by the label.
  Pausing a project, settings, runs and usage exist only in the program.
- **What it is built in** (2026 11 14). The program is C# on ASP.NET Core, as the owner wants so he
  can read and control it, following the pattern Review set: a database of its own in Postgres,
  `PlatformAuth` for who is asking, the same way of testing. The worker and the later machine
  program are C# too, so they share with the program the definitions of what they say to each other,
  and a change that breaks that fails when it is compiled. The worker is published as a single file
  carrying its own .NET runtime, so a project's recipe needs no .NET. The pages are React with
  `@platform/ui`, like the other features' pages. A smaller language for the worker was the
  alternative and was dropped: its size does not matter beside a running agent, and a second
  language costs the shared definitions.
- **The program is the manager and a worker is the hands at one workbench** (2026 11 14). The
  program, one and always on, knows the projects, settings and trusted accounts, watches the code
  host, decides which card is next and what kind of job it is, writes the brief, judges every result
  by the project's settings, counts fix rounds and usage, does everything on the code host except
  moving code, and serves the pages. A worker asks for work, gets one job, fetches the code into a
  clean working copy, starts the agent with the brief and watches the time limit, then runs the
  project's tests, takes the screenshots, pushes the branch, sends the report and the results back,
  and forgets everything. It knows nothing of settings or of what comes next, and it does not loop
  over a card: a fix is a new job the program hands out. This repository's `run.sh` does both in
  one script on one machine; they are split because the platform and the machines are different
  places.
- **How code reaches a worker and comes back** (2026 11 14). For each job the program gives the
  worker a key made by the GitHub App. It lasts an hour, which is GitHub's own limit, reaches one
  repository, and can only read and push code: it cannot merge, edit a card or change a label. The
  worker asks for a fresh one when it fetches and when it pushes, so the length of a run does not
  matter. The agent runs as a different user in the container and cannot read it. The program
  watches `main`: anything landing there that neither it nor the owner put there pauses the project
  and reaches him. A repository with branch protection has it as one more layer. The alternative
  kept every key out of the container, with the program passing code in and commits out as git
  bundles and checking them before pushing. It is safer and was first recommended, then dropped as
  more to build than the risk is worth: for a bad push to `main` the agent must misbehave and the
  wall between the two users must fail together, and the result is a commit the owner can see and
  revert. The worker's fetching and handing back are one small place, so it can be changed later.
- **The states of a card** (2026 11 14), which are what the pages show:

  | State | What it means | Whose turn |
  |---|---|---|
  | Reading | The reading step is queued or running | The program |
  | Needs answers | It has questions, and the project has questions on | The owner |
  | Refused | Too thin to build, with what is missing listed | The owner |
  | Queued | Ready, and waiting for its turn, a free worker or a card it depends on; the page says which | The program |
  | Building | An agent is writing the code | The program |
  | Checking | The worker is running the tests and taking screenshots | The program |
  | Reviewing | The reviewer is reading it | The program |
  | Fixing | Back with the developer after a review, in round 1, 2 or 3 | The program |
  | Waiting for the owner | The pull request waits for him, by the project's settings | The owner |
  | Done | Merged into the feature's branch or `main` | Nobody |
  | Stopped | It could not produce a working result, and an attention issue says why | The owner |

  A feature is in progress, ready for the owner's review, which is every card done and the pull
  request to `main` open, or merged. On top of any state a card can be held and a project paused.
  The page of what waits for him is every card and feature whose turn is his, across all projects.
- **The agent runs behind one narrow interface** (2026 11 14): the worker starts a program, gives it
  the brief and reads the report in its fixed form. Two things are built behind it: Claude Code run
  headless with `claude -p`, and a fake agent returning a prepared report, so the worker's own tests
  spend no usage. The Agent SDK is not used now: it exists for Python and TypeScript, and the
  subscription login is documented for Claude Code itself. The interface keeps it possible later, as
  a small side program in one of those languages taking the same brief and returning the same
  report, at the cost of that language in the base image. It would give control from code during a
  run, such as tools of the owner's own, and the same slot takes another agent altogether.

### Round 4b: cost and efficiency

Held on 2026 11 14 and done. It settled how context is carried between a feature's cards, where
card size is decided, which model does which job, subscription against API key, and when a project
may run.

From the owner, 2026 11 14: a completely new session for each small card that builds on the last
one cannot be the best way.

Agreed in round 4b:

- **A fresh session per card, with a handover note, is the default** (2026 11 14). Each card's
  report ends with a short note for the next card: what was built, where it lives and what to watch
  out for. The next card's brief holds the notes from the feature's earlier cards. The reading step,
  which reads all of a feature's cards at once, also writes down which parts of the code the feature
  touches, and that goes into every brief. The agent still reads the code it changes, but no longer
  has to search for it.
- **One session kept through a whole feature is a switch to try later** (2026 11 14). The agent
  then remembers everything, but resuming sends the whole conversation again each time, cheaper
  only while a cache that lasts an hour holds, and it grows with every card. An early mistake also
  stays with it. Whether it saves anything depends on the size of the cards and how soon one follows
  another, so it is tried on one feature and judged by the numbers. Dedicated workers make it
  possible. The reviewer is a fresh session either way, since its worth is that it has not seen the
  developer's reasoning.
- **Card size belongs to planning, not to the program** (2026 11 14). The owner wants cards of a
  good size, not too small, since fewer and larger cards mean fewer fresh starts, against being
  harder to review and to send back one at a time. The usage page shows per card the tokens, the fix
  rounds and whether it was sent back, so the size that works is found by trying and goes back into
  the planning skill.
- **The model is a setting per job, per project** (2026 11 14), for the three jobs reading, building
  and reviewing, and every job starts on the strongest model. A cheaper model on a job only saves
  anything if the result stays good, which is found by changing it for one project and reading the
  usage page. The reviewer is the last job to weaken, since in a fully automatic project it is the
  last judge before a card merges; reading is the likelier first candidate. A reviewer on another
  company's model is left out for now: the narrow interface allows it, at the cost of another
  subscription and login per worker.
- **The Claude login is held by the program, like any other secret** (2026 11 14). The owner runs
  `claude setup-token` once and gives the token to the platform; it lasts a year. The program hands
  it to each run, so a new worker needs no login step, and it warns him a month before it runs out.
- **Each project runs on the subscription or on an API key** (2026 11 14), the subscription by
  default. The key is also the safety net: if the subscription login ever stops working for
  unattended runs, a project is switched to the key and nothing else changes.
- **At the subscription's limit the default is to wait** (2026 11 14) until it resets, which costs
  nothing. A project can instead continue on an API key, up to an amount per day the owner sets.
- **A project runs always or by schedule** (2026 11 14). Unattended runs and the owner's own use
  draw from one allowance, and nothing reports how much of it is left, so hours are what the program
  can enforce. A project has any number of schedules, each a name, the days of the week and a from
  and to time, and he chooses which are active; night and a few hours in the day are two active
  schedules. A project on "by schedule" works only inside its active schedules, and with none
  active does nothing. Outside a schedule the card in progress finishes and nothing new starts.
  "Run now anyway" overrides it for one feature. One setting of active hours for the whole
  installation was proposed first, and he chose schedules per project instead.

### Round 5: where it runs

Held on 2026 11 14 and done. Workers, containers, the recipe, the Claude login and subscription
against API key were settled in rounds 3 and 4b. This round settled where the platform is hosted.

- **The platform is hosted on the Mac mini to begin with, reachable only by the owner's own
  devices** (2026 11 14), through a private network between them, such as Tailscale. Its services
  and database run there apart from the workers' containers. Nothing is exposed to the internet,
  which matters because the platform's login is its own. So GitHub cannot call the platform, and
  the program asks GitHub for changes about once a minute, well inside GitHub's limits at this
  scale. Workers call the program and are never called, so moving the platform to a rented server
  later changes an address and nothing in the design. What it costs: one machine holds everything,
  and a platform that stays up holds data that cannot be entered again, so its databases need
  backing up, which does not exist today. A rented server was the alternative: independent of the
  owner's home, and on the open internet from the first day.
- **Hosting the platform and backing it up is a piece of work of its own** (2026 11 14), since
  every feature needs it. Round 9 places it.

From the owner, 2026 11 14: many repositories, perhaps one agent each. The Mac mini, more machines,
perhaps cloud. Perhaps a container per project with its tools installed in it. The subscription most
of the time, the API possible.

Research done on 2026 11 14, read from the official Claude Code documentation and support
articles through a tool that summarises pages. Nothing here is decided, and the exact wording
should be checked on the live pages before the round relies on it, the support article first.

- **Headless runs do what the design needs.** `claude -p` takes a JSON schema and returns the
  result in a field of its own, which fits the fixed form of the report, and a session can be
  resumed by id. A worker must check both the exit status and the answer, since a failure is
  printed as the result. Source: `code.claude.com/docs/en/headless.md`.
- **A subscription login works in a container.** `claude setup-token` makes a token that lasts a
  year and is given to the run as `CLAUDE_CODE_OAUTH_TOKEN`, with no keychain or browser. It can
  only make model requests. It needs rotating and a warning before it runs out. Source:
  `code.claude.com/docs/en/authentication.md`.
- **Whether unattended sessions started by a program are allowed on a subscription is not
  stated.** The worker is always on but uses no Claude while it waits; a session starts for a card
  and ends with it. The terms say the login is for "ordinary use" and "ordinary, individual usage",
  do not define it, and reserve enforcement without notice. One person's own sessions on his own
  subscription are not what they aim at, but many in a row through a night are a grey zone, the
  same one this repository's runner is already in. Source:
  `code.claude.com/docs/en/legal-and-compliance.md`. So the design keeps an API key as a second way
  to run, per project or as a fallback.
- **An API key in the environment silently wins** over the subscription in a headless run, and the
  run is then billed to the API. The worker must control which credential a run has and check it.
- **Bare mode cannot use the subscription**, and the documentation says it will become the default
  for headless runs. The worker must set the mode itself and check at start that the login works.
- **Runs on a subscription draw from the plan's limits**, and there is no separate allowance for
  them. Max plans are said to include monthly API credits for runs made with an API key through a
  linked Console organisation; the amounts were not confirmed. Source:
  `support.claude.com/en/articles/15036540`.
- **Usage per run can be read**, tokens and an estimated cost, from the JSON answer. It is an
  estimate, and nothing says how much of the subscription's limit a run used. So the usage page
  shows tokens and estimates, never a share of the allowance.
- **Running without permission prompts is documented only for an isolated container, as a user that
  is not root.** Code in the container can read the Claude login stored there, which is the one
  credential a run holds, as round 3 said.
- **Cloud hosted sessions are not a fit yet.** A program can start one, but the documentation gives
  no way to read its result, and the one way to trigger them by API is a research preview.
- **Resuming a session sends the whole conversation again.** What is unchanged is read from a
  cache that lasts an hour on a subscription, and a session resumed after a longer gap is paid for
  in full. Two runs in different directories share no cache. The documentation gives no break even
  between resuming and starting fresh. This is input to round 4b.

### Round 6: the code host

Closed on 2026 11 14 without questions of its own, since the earlier rounds settled it: a GitHub
App, in round 3; asking GitHub for changes, in round 5; features as parent issues with sub-issues,
dependencies as "blocked by" links, and labels for picking, holding, attention and priority, in
round 2. What is left is in "Calls made without the owner".

### Round 7: the pages

Held on 2026 11 14 and done. The mockups the owner looked at are in
`2026-11-14-development-flow-mockups/` beside this file. Their content is made up; what they show
of layout is what was agreed.

- **Six pages, with what waits for the owner as the home** (2026 11 14): waiting for him; running
  now; projects, and a project's own page; a feature's page; a card's page; usage and history. The
  first four are tabs across the top. The platform's front page shows one number from the feature,
  how many things wait for him, and the nav bar shows its attention dot on the feature's link when
  that number is not zero.
- **Waiting for him is one list, acted on in place** (2026 11 14), grouped by kind: questions before
  starting, pull requests waiting, features ready for his review, and stopped cards. Each item shows
  the decision and the agent's reasoning with the action beside it, so most are cleared without
  opening anything. A list with a detail panel beside it was the alternative: more room per
  decision and a click per item, and two screens on a phone. That fuller detail is the card's page.
- **A feature's page** (2026 11 14) holds its name and state with "Merge into main" and "Send the
  whole feature back"; what the agent wants him to know, which is each decision it flagged with the
  alternative and the reviewer's view, and "Fine as it is" or "Send back" on each; the branch to
  check out and the screenshots the program took; and the cards, each with its fix rounds, tokens
  and decisions and a "Send back" that opens a comment box.
- **Trying a feature is its branch and its screenshots for now** (2026 11 14). The feature is not in
  `main`, so trying it means checking the branch out. Starting a feature's branch somewhere he can
  open it would be better, and is a piece of work of its own, left out.
- **Running now** (2026 11 14) has a panel per busy worker, with the card, its state, the time and
  tokens so far, the agent's log as it happens and "Stop now", and under them what comes next, each
  card with the reason it is not running: it waits for another card, it is outside its schedule, or
  its project has no worker.
- **A project's page** (2026 11 14) has its state and preset with "Pause" and "Stop now" at the top;
  its features and cards; every setting in one panel on the page; when it may run, with its
  schedules and an on and off for each; and its workers, with the command to start another. For a
  project just connected, the steps of setting it up stand in place of the features until the last
  is done.
- **Tokens and time are shown, not cost** (2026 11 14), since on a subscription a cost is an estimate
  of money that is not paid.
- **A card's page and the usage page were agreed without a mockup** (2026 11 14). A card's page is
  its whole story: each run, the report, the decisions, the screenshots and the review's findings.
  Usage and history is a table per project, feature and card.

### Round 8: verification

Held on 2026 11 14 and done. That the worker runs the tests itself, that a fresh reviewer reads
every pull request and that the worker takes the screenshots were agreed in rounds 2 and 4. This
round settled what the program checks before it merges.

- **A card merges unattended only when all of this holds** (2026 11 14), the successor of this
  repository's merge gate:
  1. the project's checks passed when the worker ran them, the build, the tests and the linters as
     one command the project defines, by the worker's own result and not the agent's claim;
  2. the project's own CI passed on the pull request, where the project has CI;
  3. the reviewer approved exactly this commit, so a commit pushed after the review needs a new one;
  4. the card is still valid: written by a trusted account, not held, and not edited by anyone
     outside the list since it was read;
  5. the project's settings do not make it wait for the owner.

  Anything that fails or cannot be read means no merge. The program never guesses in favour of
  merging.
- **Three things are deliberately not on that list** (2026 11 14). Screenshots are evidence for the
  reviewer and the owner: the reviewer looks at them and can fail a card for them, but the program
  cannot judge a picture. Whether a card had tests written for it is the reviewer's to judge, by
  whether the card's acceptance is covered, since counting tests rewards the wrong thing. And the
  agent's report guides and never decides.
- **A project's commands live in a small file in its repository** (2026 11 14), written by the setup
  run with the recipe: the one command that checks it, and how to start it so its pages can be
  opened for screenshots. A project with no pages, such as Tick, leaves the second empty.

### Round 9: parts and order

Held on 2026 11 14 and done.

- **An experiment comes before the parts** (2026 11 14). The engine rests on one claim nobody has
  tried: a worker in a container on the Mac mini can run Claude Code on the owner's subscription
  token and get a report back in the fixed form. It is tried by hand, on Fodr, and thrown away. It
  is also the check of the research in round 5 against the real thing.
- **The parts, in order** (2026 11 14). Each ends in something the owner can use, and each becomes a
  feature issue with slices of its own when its turn comes.

  | # | Part | What he can do when it is done |
  |---|---|---|
  | 0 | Host the platform on the Mac mini, with backups | Open the platform from his laptop and phone at any time |
  | 1 | Connect and see | Connect this repository and use the page of what waits for him against the current runner: read decisions, merge, send back |
  | 2 | One card, end to end | Label a single Fodr card and get a pull request, built by a worker in a container |
  | 3 | Review, fixing and merging | That card is reviewed, fixed and merged by the program, with the stops in place |
  | 4 | Features | Label a parent issue: cards built in order into a feature's branch, the feature's page, sending back, and his merge into `main` |
  | 5 | Reading, questions and settings | The reading step, questions and refusals, every setting and the three presets |
  | 6 | Setup run and recipe | Connect a new project without writing its recipe by hand |
  | 7 | Running now, usage and history | Watch runs as they happen and see what everything used |
  | 8 | Schedules, pausing and limits | Schedules per project, the budget, the API key at the limit, turns between projects |
  | 9 | The machine program | Start and stop workers from the page |
  | 10 | The platform moves over | This repository is built by the program, and `scripts/agent/` is retired |

  Parts 0 and 1 give the daily benefit first, on this repository. Part 2 is the riskiest thing built
  as thinly as it can be. Fodr is useful from part 4. Measuring starts in part 2, though its pages
  come in part 7, so no numbers are lost.
- **Part 0 is planned in a session of its own** (2026 11 14). The Mac mini's current runner uses
  fixed ports for its test copy of the platform, and a copy that stays up on the same machine would
  collide with it. It is solvable, for example by an address and ports of its own, and it is a
  different kind of work from the rest.
- **Four batches, each built without the owner, with a short real use between them** (2026 11 14).
  The feature's automated tests must use a fake GitHub and a fake agent, since real ones cost usage
  and need secrets, so every test can pass while the real thing has never run. Waiting until all
  eleven parts are done would build ten parts on an assumption nobody tried, which is what the
  third test in "What is built now" in `High-level/docs/how-we-build.md` exists to prevent. He does
  not review code at a check, he uses the thing for half an hour.

  | Batch | Parts | His check afterwards |
  |---|---|---|
  | 1 | 0 and 1 | Open the page on his phone and merge a real pull request from it |
  | 2 | 2 and 3 | Label one real Fodr card and watch it built, reviewed and merged |
  | 3 | 4, 5 and 6 | Label one real Fodr feature in the evening and review it in the morning |
  | 4 | 7 to 10 | Use it normally, then move the platform over |

  Each batch is planned when the one before it has been used, so its plan can use what was learned.
- **The current runner builds all of it, and it still stops for the owner in three places**
  (2026 11 14), since it is not the fully automatic mode designed here: setup only he can do, such
  as registering the GitHub App, making the Claude token, and creating a database on the Mac mini
  before the slice that needs it, collected per batch so he does it in one sitting before the cards
  go in; changes under `.github/` or `scripts/`, which wait for his merge and are gathered into as
  few slices as possible, early; and the three cases where an agent may not decide alone.
- **Left out of every part** (2026 11 14): one session kept through a feature, a place to try a
  feature's branch, a second code host, and a login for other people.

---

## Calls made without the owner

Check these. Each was said to him in passing, and none was asked as a question.

- **At most two agents run at once across the installation to begin with**, one number on the page
  that the owner can raise. It protects the Mac mini's memory and the subscription's limits.
- **A project's database runs inside its own worker container**, installed by the recipe. A project
  whose tests must start containers of their own, as this platform's do, gets Docker inside its
  worker, decided per project in its setup run.
- **Priority is read from labels**, and the program may move a card on a board where one exists.
- **The code host sits behind one interface with GitHub the only one built.** What the interface
  needs for a second host is left to the builder.
- **The research in round 5 is checked against the live pages** before the first slices that depend
  on it are cut.
- **A worker reaches the program with ordinary HTTPS requests that it starts itself**, carrying
  its project's key, and sends the agent's log up while a run is going, so the page of what is
  running can show it. A machine at home cannot be reached from a host elsewhere, so the worker
  always calls and is never called. Round 5 confirms it.
- **The program keeps the label and comment conventions of the current runner** where it can, so
  the waiting list built first keeps working when the engine arrives. Round 6 confirms it.

## Clarified after the builder check

On 2026 11 14 a fresh reader went through this document as someone who has to plan the build from
it. What it found unclear is settled here. Each of these is a call made without the owner, for him
to check, and where one differs from an earlier passage, this one holds.

- **How many agents run at once, and when projects take turns.** There is one limit for the whole
  installation, two to begin with. When more cards are ready than there are free places, the
  projects that are active take turns, always, and not only when usage is tight: nothing reports
  how much of the allowance is left, so the program cannot wait for that. The dispatcher in part 2
  is built this way from the start.
- **Reading is one run per feature, and a card is read again when it changes.** The run reads all
  of a feature's cards and answers for each. The program remembers what each card said when it was
  read. A card edited or commented on by a trusted account after that is read again before it
  starts, alone, with the feature's notes; so is a card added to a feature that is already running.
  "Not edited since it was read" in the merge check compares against the same record.
- **A stopped or refused card is marked on the card itself**, with the attention label and a
  comment. A new issue with the attention label is opened only for a decision the agent flagged on
  a card that has already merged, which is what the owner described for Fodr.
- **A feature's branch.** The program creates it from `main` when the feature's first card starts,
  and opens the pull request to `main` at once, as a draft, so GitHub shows the feature growing.
  When the last card is done the program brings `main` into the branch. If that conflicts, bringing
  it in is a job for an agent, checked and reviewed like a card. Then the pull request is marked
  ready and the feature is ready for the owner's review. Merges are merge commits throughout, as in
  this repository, so one entry on `main` is one feature or one card on its own.
- **The program watches a feature's branch as it watches `main`.** A commit there that it did not
  merge itself pauses the project and reaches the owner. A card's own branch needs no watching: the
  merge check asks for a review of exactly the commit being merged.
- **The worker's key cannot change a project's CI workflows**, which GitHub guards with a
  permission of its own. A card that needs to change one stops and says so. Whether a project may
  allow it is a setting that is off, and can be added when a project needs it.
- **The trusted checks run on a clean copy of the pushed commit**, in the same container, not in
  the working copy the agent left. What the agent installed in the container is still there, which
  is accepted; where a project has CI on GitHub, that is the independent environment.
- **Units.** The daily budget of a project on the subscription is in tokens. The daily amount for
  continuing on an API key is in money, by the estimate Claude Code reports for each run.
- **Who a worker and the program's own loop act as.** Every project belongs to one user. A worker's
  key leads to its project and so to that user, and the loop that watches GitHub works one user's
  projects at a time, so every query still runs as a user, as "Multi user, and how isolation is
  enforced" in `High-level/docs/platform-architecture.md` requires. "How a feature authenticates"
  in `High-level/docs/feature-contract.md` speaks only of a user's token, so the slice that adds
  the worker's key also adds this second way to that passage.
- **What part 1 holds of the real program.** It registers the real GitHub App and links the owner's
  GitHub account, both kept. It reads this repository's runner through one small piece that knows
  the runner's labels and comments, which is thrown away in part 10. From the page the owner merges
  and comments, and a comment starts the runner's own fix run; the page never writes a label the
  runner owns. Which of the runner's conventions the program keeps for good is decided when part 2
  is planned.
- **"Guided" is a preset, not a copy of this repository**, whose cards land in `main` today. How
  the platform is set up as a project is decided in part 10.

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
- 2026 11 14: the owner approved the spec, and settled that the program may merge a card on its
  own into `main` where a project's settings allow it.
- 2026 11 14: builder check. Six passages that contradicted a later round were corrected in place,
  and its questions are answered under "Clarified after the builder check".
- 2026 11 14: round 9 added. Every round is done.
- 2026 11 14: round 8 added.
- 2026 11 14: round 7 added, with the mockups.
- 2026 11 14: rounds 5 and 6 closed. Round 6 had no questions of its own.
- 2026 11 14: round 4b added.
- 2026 11 14: round 4 added, with the research for round 5. "A worker never talks to the code host"
  became: it fetches and pushes code with a short lived key and does nothing else there. The first
  anti goal now reads "never only by rules".
- 2026 11 14: round 3 added. Every worker is dedicated to one project, which replaced an earlier
  draft with shared workers and tags.
- 2026 11 14: round 2 added, and round 4b, cost and efficiency, added at the owner's wish. The call
  that every card has a pull request left "Calls made without the owner", since round 2 agreed it.
