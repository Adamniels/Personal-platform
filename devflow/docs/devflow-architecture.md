# Dev flow architecture

Status: living. It describes what is built, and changes in the same pull request as the code.

Last updated: 2026 11 27

## What Dev flow is

Dev flow is the platform's feature for automating development on connected repositories. The owner
connects a project, plans its work as cards, and agents carry each card as far as the project's
settings say, from building it to merging it; he follows all of it, and answers what waits for
him, from Dev flow's pages rather than a terminal. Its first part gives him one list of what waits
for him, against this repository's current runner, `scripts/agent/`.

`docs/superpowers/specs/2026-11-14-development-flow-design.md` is the design of the whole
development flow, the program, its workers and its developer agent, with every decision and its
reasoning. It was frozen on 2026 11 15, when the slices of the first part were cut, and is never
updated. This document describes what exists, and grows as each slice lands; where the two
differ, this one is right about what is built and the spec records what was planned. The scope of
each part and its slices are in the feature issue, #268, and its sub issues.

## What exists

The service, `devflow/`, a C# minimal API app like `review/`, and its page, `devflow/web/`, with
two tabs, "Waiting for you" at `/devflow`, the list of what waits for the owner, and "Projects" at
`/devflow/projects`.
It answers through the proxy under `/devflow` in the dev stack and in the hosted copy, verifies the
core's token itself through `PlatformAuth`, and has a database of its own, `devflow`, migrated by
its `migrate` command. Its tables are the vault's, `secrets`, which seals a secret for a user,
the GitHub link's, see "Acting as the owner" below, the projects', see "Projects" below, and the
workers' and the Claude login's, see "Workers" below. Everything it will ask of a code host goes
through one interface, with an in memory fake behind it and a GitHub adapter that reads as the app,
and can write as the app with a key narrowed to one purpose, which nothing uses yet, see "The code
host seam" and "GitHub" below. On the Projects tab the owner links his GitHub
account, and Dev flow then holds a token that acts as him, sealed in the vault and renewed by
itself; with it he connects a repository the app is installed on, which makes it a project. A
loop reads every project about once a minute into a stored list of what waits for him, which the
API answers without asking GitHub, see "Reading and the snapshot" below, and "Waiting for you"
shows, with each decision and the reviewer's view spelled out; the platform's front page shows
how many wait from the same picture, through the display data the core reads. What it serves, its settings, its database, the vault, the code host and how it
authenticates are in `devflow/README.md`. Whether a pull request waits for the owner, and why, is
read by one pure function that knows this repository's runner, see "Reading this repository's
runner" below.

From the list the owner merges a waiting pull request, after a confirmation, or sends one back with
a comment, both as himself on GitHub, see "Acting from the page" below. Everything else the tabs
will show arrives with the later slices of the first part.

Each project has a page of its own, under the Projects tab, where the owner makes the project's
worker key, shown once, and sees the workers that have introduced themselves with it; a worker
holding the key says hello and asks for work, and is handed its project's next card as a job, see
"Workers" and "Handing out work" below.
The worker program that does so exists, with the agent it will run behind one narrow interface,
and publishes as one file for each Linux a worker runs on. Every release builds the image it runs
in, and the project's page shows the two commands that build a project's worker from its own
recipe and start it, and says when one needs building again.
On the Projects tab the owner also gives Dev flow his Claude login once, which it keeps sealed and
warns him about a month before it is due.

A project's page also holds its settings, the labels that pick and hold its cards, whether a
card's pull request waits for the owner, never, sometimes on the conditions he ticks, or always,
and its trusted accounts, and lists its cards: each read of the project picks the
open issues he wrote that carry the pick-up label, and each card shows as queued, held, waiting for
what blocks it, or ignored with why; a labelled issue with sub-issues is a feature, listed above
the cards on their own with its sub-issues as its cards, see "Features and their cards" below. A worker asking for work is handed the
next queued card with a brief written for it, and what it sends back is recorded on the run and
moves the card on, see "Handing out work" below. A card that built and passed its check gets its
pull request, opened as the app; a fresh reviewer agent reads that commit and its verdict is kept
for it, see "Review" below, with the pictures the worker took of the project started from that
commit, see "The worker's pictures" below; a `changes` verdict, or the owner's comment on its pull request, sends
it back to a fresh developer on the same branch, see "Fixing" below; and once approved, the merge
check merges it as the app when every condition holds, opening an issue for each decision the agent
marked for the owner, or has it wait for him on "Waiting for you" with why, where he merges it as
himself, see "The merge check" below; each card has a page with its whole story so far, see "How a card reaches
its pull request" below, and each feature one with its cards, its branch and the decisions its cards
marked for the owner, answered there, see "A feature's page" below. When every card of a feature is
done, the default branch is brought into its branch as the app and its pull request to the default
branch opened, kept up to the default branch while it waits for the owner, a conflict resolved by an
agent in a job of its own, and the whole feature reviewed by a fresh reviewer, fixed on its branch
in up to the project's fix rounds, three by default, when that review asks for changes or on the owner's comment, and ready for the
owner once a review approves its head and CI passed there, see "The end of a feature", "A
conflict", "Reviewing a feature" and "Fixing a feature" below. The owner merges a feature into the
default branch himself, from its page or from "Waiting for you", once CI passed on its head; then
its cards are closed, or, when its pull request is closed without merging, opened again, see
"Merging a feature" below. A card that cannot be made to work stops, is marked on its issue and
shows under "Stopped", and his comment runs it again; a failure outside the card makes its project
wait instead, see "Stops" below.

Every read of a project also watches its default branch, and each open feature's branch: anything
that lands on one that neither Dev flow put there nor the owner put there himself stops the
project, which then hands out no work and merges nothing, and shows on "Waiting for you" until he
has looked and resumes it, see "Watching the default branch" below, with "A feature's branch"
under it.

## The code host seam

Everything Dev flow asks of a code host goes through `ICodeHost`, in `src/DevFlow/CodeHost/`:
today the reads the waiting list needs, open pull requests, one pull request, a conversation's
comments, a commit's statuses and check runs, and a file on a branch, and listing what a person
can connect, read with their own token; the owner's two writes, merging and commenting, made only
with his token; and the program's own writes, opening a pull request or an issue, commenting,
labelling and merging, made as the app with a key for one purpose, with where git reaches a
repository. Signing a person in is a second interface beside it,
`ICodeHostSignIn`, since its reads are as the app and signing in is as a person; both
implementations implement both. It names things as the spec does, so a second
host can sit behind it, and GitHub is the only one built. Every operation answers its value or one
of two failures the caller must handle, not found and unavailable, so a page or the poll can say
what went wrong instead of failing on an exception it did not expect. What each operation returns
is in "The code host" in `devflow/README.md`.

Two implementations sit behind it, chosen by `DEVFLOW_CODE_HOST` when the app is built: the GitHub
adapter, see "GitHub" below, and `FakeCodeHost`, a world per platform account in memory, which is
the default. The spec requires that automated tests never touch the real GitHub, since it
costs usage and needs secrets; with the fake as the default the laptop's tests, CI and the runner's
stack need nothing set, and every acceptance test stages the repositories and pull requests it
needs in its own account through `/devflow/api/fake/`.

**The fake sits at the interface, not as a stand in GitHub server.** A stand in server would
exercise the adapter in every test, but it would have to imitate GitHub's API closely enough to be
trusted, its paging, its errors, its token exchange, which is a second GitHub to keep right, and
every test would depend on it. At the interface the fake is small, a test stages exactly what the
interface would answer, and the cost is that no test through the fake proves the adapter right.
So **the adapter is tested on its own**, against recorded answers from the real GitHub in
`contracts/github/`, and the owner's check after the first batch, merging a real pull request from
his phone, is the first time the two meet for real.

## GitHub

Dev flow acts on GitHub as a GitHub App, as the spec's round 3 decided. It reads as the app. What
the owner does from the page it writes with a token that acts as him, see "Acting as the owner" and
"Acting from the page" below, and what the program does itself it writes as the app, see "The
program's own writes" below. The adapter,
`GitHubCodeHost` in `src/DevFlow/CodeHost/GitHub/`, is written by hand on .NET's `HttpClient`,
since its calls are few, and no automated test touches GitHub: its tests run against GitHub's real
answers, recorded once in `contracts/github/`. Its addresses and settings are in
`devflow/README.md`.

**Signing in as the app.** The app's private key, a file whose path is
`DEVFLOW_GITHUB_PRIVATE_KEY_FILE`, is read once at startup. When the adapter needs an installation
token it signs a JSON Web Token with it, RS256, with `iss` the app's client id, `iat` 60 seconds in
the past against clock drift and `exp` 9 minutes ahead, inside GitHub's limit of 10. That token is
made when needed and never stored; it can do nothing but ask for installation tokens.

**Its reads stay read only.** Every installation token it reads with is asked for with `permissions` of exactly
`contents`, `pull_requests`, `issues`, `statuses`, `checks` and `metadata`, each `read`, and
`repository_ids` holding the one repository being read. GitHub never grants more than the app holds,
and narrows to what the request asks. A token is kept in memory per installation and repository
until five minutes before its hour is up, and written nowhere. A repository is addressed by its
owner and name, as GitHub's addresses are, but the token reaches its id alone, so a name that
points elsewhere since is not found rather than another repository read.

**The program's own writes.** What the program does on GitHub, opening a card's pull request,
commenting on it, moving its labels, opening an issue and merging, is done as the app, so GitHub
marks it as the app's and it is traced back to a card and a run, the spec's anti goal 8, rather
than passed off as the owner's. It goes through a second write path, as typed as the owner's: each
write takes an `AppWriteToken`, a type of its own with an `internal` constructor, made only by
`InstallationTokens.ForWrite(repository, purpose)` and, in the fake, by the fake's own issuer, and
reached by callers only through `ICodeHost.ForWrite`. A read token is a string and the owner's is a
`UserAccessToken`, so neither can be passed where an `AppWriteToken` is wanted, and the compiler
keeps the three apart. Each key is asked for with exactly its purpose's permissions and
`repository_ids` holding the one repository:

| Purpose | Permissions | For |
|---|---|---|
| `Push` | `contents: write`, `metadata: read` | a worker fetching and pushing code |
| `Post` | `issues: write`, `pull_requests: write`, `metadata: read` | opening a pull request or an issue, commenting, labelling |
| `Merge` | `contents: write`, `pull_requests: read`, `metadata: read` | merging only |

No purpose asks for `workflows`, so no key Dev flow makes can change a project's CI. A write key is
never kept: each `ForWrite` asks GitHub for a new one, so a push key handed to one worker is never
another's, and no write key is ever used to read. A write refuses a key for another purpose or
repository before GitHub is asked, so a bug that hands a push key to a merge fails loudly rather
than at GitHub. GitHub answers `422` when the installation has not granted a permission asked for,
which reads as not found, as a repository outside the installation does.

The push key needs a correction to the spec, which said the worker's key "cannot merge": GitHub
files merging a pull request under Contents, and there is no narrower permission, so a push key can
push to any branch of its repository, `main` included, and could merge through the API. It is
accepted as the spec accepted a push to `main`, on the condition that `main` is watched, and
anything landing there that Dev flow did not merge stops the project; that is "Watching the default
branch" below. It was planned to come before anything hands out a push key, and "Handing out work
and the brief" landed while it was still in review, see "Push keys before the watch" in the
security section. A push key leaves Dev flow only inside a job's answer, or a fresh key's, to the
worker holding that job, and is kept in memory only, never stored. Handing out work uses the push
key; the post and merge keys are the later slices'.

The reads cannot reach a write key: `ReadPathTests` reads the compiled code and fails if anything
under `Reading/` or `Runner/`, or anything a read of the adapter runs, through its helpers, lambdas
and async state machines, names an `AppWriteToken` or a `WritePurpose`. `AppWriteTokenTests` holds
each purpose's request body, that none names `workflows`, and that only the two issuers make a key.

**Reading cheaply.** The program will ask GitHub about every pull request it follows once a minute.
Each answer is remembered, in memory, per installation, media type and address, with its `ETag`,
and the next request for it carries `If-None-Match`: GitHub answers `304` when nothing changed,
which does not count against its limit of 5,000 requests an hour per installation, and the adapter
answers what it remembered. What is remembered is bounded at 64 MB and forgotten after an hour
without a request for it. Lists follow the `Link` header's `next`, 100 a page, never to an address
off `api.github.com`, where the token would go, and at most 100 pages.

**Failures.** `404` is not found, which also covers a repository the app cannot see, since GitHub
does not tell the two apart. A `403` or `429` with `retry-after`, or with `x-ratelimit-remaining:
0` and `x-ratelimit-reset`, is unavailable until the time given; one with neither is unavailable
with no time, since a `403` may be a refusal that waiting does not change. Any other failure, a
`5xx`, a timeout of 20 seconds, the network, or a body that does not parse, is unavailable with no
time. Nothing is retried inside the adapter: its caller is a loop that comes back.

**Who may read what is the caller's to decide.** The adapter reads as the app, so any repository
the app is installed on can be read through it, whatever platform account asks. Which account may
ask about which repository is decided by the callers, from the repositories that account
connected, its `projects`, which the database keeps per account under row level security; and an
account connects only what GitHub, asked as its linked person, lists for them, see "Projects"
below.

## Acting as the owner

What the owner does from Dev flow's pages is recorded on GitHub as done by him, round 3 of the
spec. That is also a requirement of this repository's runner, which starts a fix run only on a
comment whose author is the login `Adamniels`. So Dev flow holds a token that acts as him: a GitHub
App's user access token, which GitHub documents as attributing what it does to the user, and which
has only the permissions both the user and the app have. How it is got and kept is "Linking
GitHub" in `devflow/README.md`; this is why it is built so.

**Linking is the OAuth web flow with PKCE**, as GitHub documents it and strongly recommends: Dev
flow keeps a random verifier and sends GitHub only its SHA-256, the S256 challenge, so a code the
browser comes back with can be spent only with the verifier, which never leaves the database. The
state that comes back with it is random, single use, bound to the platform user who started the
link and ten minutes old at most, and only its SHA-256 is stored. Nothing prints a code, a
verifier, a state or a token.

**The tokens expire, and Dev flow renews them.** With "Expire user authorization tokens" on, an
access token lasts eight hours and a refresh token six months, and a refresh token is taken once:
after it, it and the access token before it stop working. `UserTokens.For` is the one way to a
usable token, and renews under a lock on the account's row, so two requests at once never spend
one refresh token and lose the link. A renewal GitHub refuses ends the link, `needs_relink`, and
the page asks the owner to link again; GitHub not answering ends nothing.

**One GitHub account per platform user.** The link is a row per user, under row level security
like every table, and its tokens are sealed in the vault under that user, so one user's token is
never reachable as another's.

**Drop takes a label off as him.** Dropping a card he stopped takes the project's pick-up label off
its issue with his token, `ICodeHost.Unlabel`, never with an app key, though the app could: the
label is his own signal that a card is for Dev flow, put on by him or a trusted account, and the
issue's timeline should show him taking it off, as it shows him putting it on. The app takes off
only what it put on, its attention label. A label GitHub says is not there is as good as taken off,
for the same reason it is when the app takes its own off. Dev flow keeps the card, ignored, rather
than forgetting it as it forgets a card whose label someone took off on GitHub, so its story stays
on its page; and since a read could reach GitHub's list before the label is gone, only a read that
began after the drop may pick it up again. How it is done is "Stopped by you" under "Stops" in
`devflow/README.md`.

## Projects

A project is one connected repository with its settings, as the spec defines it. Part 5 built the
whole settings model, which "Settings" under "Cards" in `devflow/README.md` lists, with the three
presets; schedules and the daily budget are part 8's. Every project is also read the way this
repository's runner writes, through the reader below, and `bot_login` names the login whose pull
requests that reader looks at, this repository's bot by default and a column without a page. How
the owner connects one, the routes and the tables are "Connecting a repository" in
`devflow/README.md`.

**Presets are shortcuts, never stored.** A preset fills in the settings it covers, questions,
"waits for the owner" and the three conditions of `sometimes`, and the settings route answers the
preset the stored settings match, or `custom`. Storing the choice would give two sources of truth
for the same three settings: the owner may change any one of them afterwards, as the spec has it,
and a stored preset would then either lie or have to be cleared by every change. Computing it keeps
one truth, the settings, and the name always follows them. The settings page works the name out
from the form as it is typed with the same rule, so it can say "Custom" before anything is saved; that rule
is written twice, in `Presets.Of` and in `presetOf` in `web/src/settings.tsx`, and is small enough
that the duplication is cheaper than a round trip per keystroke.

**A project's settings have a page of their own**, `/devflow/projects/<id>/settings`, reached by
"Edit settings" on the project's page and in its setup's settings step, in four sections, how much
happens alone, how cards are picked, runs and values, with one Save; "A project's settings page" in
`devflow/README.md` is what it shows. The spec's round 7, and the slice that built the settings
panel, put every setting in one panel on the project's page. Eighteen settings do not fit beside the
cards: open above them, the panel pushed the cards, which the redesign puts first, off the screen,
and a panel that big is a page in all but its address. A page of its own gives each section room,
a Save bar that counts what is unsaved and can undo it, and a place for the project's values, which
were editable only during setup before, and for Disconnect, which leaves the Projects tab's cards so
a destructive action is not one click from the dashboard. The page reads and saves through the same
routes as the panel did, so only the frontend changed, and the project's page reads the settings
each time it opens, so a save shows there once back.

**"Straight to main" was dropped**, which the spec's round 2 had as a value of where a feature's
cards land, so no preset sets it. A feature's cards always land in its branch, and the owner always
merges a feature into the default branch himself; a card on its own already goes to the default
branch through its own pull request, so work that should reach `main` card by card is planned as
single cards linked by blocked by. As a setting it would have been a second life for a feature
beside everything part 4 built, as the feature issue, "Dev flow: reading, questions and settings",
records.

**A new project starts as "Only I merge"**, questions on and `always`, the most cautious of the
three, and the migration gave every project there was the same, so Fodr, on `always` before, became
exactly "Only I merge". Part 6's setup run proposes a preset for each project.

**A project's summary is computed as the list is read, never stored.** The Projects tab draws
every project's card from `GET /devflow/api/projects`, so each project there carries how it is
doing: its cards by group, its workers' health, its setup steps while it sets up and its open
feature furthest on, in the shape "Connecting a repository" in `devflow/README.md` gives. It is a
field on the list rather than a route of its own, since the tab already reads that one, and it is
worked out from the tables each part already keeps, with no migration: a stored count would be a
second truth that every change of a card's state would have to keep. Its parts reuse what already
says them rather than a copy: `held` is `WatchStore.HoldSql` and then `Dispatcher.HeldFor`, as the
workers list has it, `cannot_run` the fragment `ProjectWaits.CannotRunOf` reads through, and
setting up `SetupStore.Of`, asked only for a project not registered, since its values step is
worked out in C# from the newest setup report.

The groups are written once, in `ProjectSummaries.Groups`, over the thirteen card states the
`cards.state` check allows:

| Group | Card states |
| --- | --- |
| needs you | `needs_answers`, `waiting_for_owner`, `stopped`, `refused` |
| in progress | `reading`, `building`, `checking`, `reviewing`, `fixing` |
| queued | `queued`, `held` |
| done | `done` |

`ignored` is in none: an ignored card is one Dev flow will not build, so it is neither work nor the
owner's turn. `STATES` in `web/src/cards.tsx` also names feature states, `bringing_in`,
`resolving`, `ready`, `merged` and `closed`; they are not card states and are not counted. A
feature's `landed` is its cards in `done`, and its `total` its cards in any group, so an ignored
sub-issue does not hold a feature short of its total. An open feature is one neither merged,
closed nor ignored, the last being a parent that failed the rules a card is held to, and the one
named is the one with the most cards landed, the lowest number among equals.

A project's page counts its own cards into the same groups for its summary line, from the cards it
reads, as its slice settled, so the groups have a second copy, `CARD_GROUPS` in
`web/src/cards.tsx`. The two are kept the same by hand; a change to one is a change to both.

**A person connects only what GitHub lists for them.** The repositories to choose from are read as
the person, with their user token, `GET /user/installations` and each installation's
repositories, never as the app: the app may be installed where the person cannot reach, and the
list must be theirs. Connecting asks GitHub again, as them, and stores the repository only when
it is among those of that installation, with the owner and name GitHub gave, so a request cannot
name a repository the person cannot reach, or give one a name of its choosing. Nothing assumes one
installation: the owner's own account and each organisation he installs the app on are one each.

**Every project belongs to one user**, under row level security like every table, and two users
connecting the same repository have a project each. Disconnecting deletes the project and,
through each later table's foreign key with `ON DELETE CASCADE`, everything stored for it; it
changes nothing on GitHub, where the app stays installed until the owner removes it.

**The loop that watches GitHub learns from `poll_targets` which projects exist and whose they
are**, as "Who a worker and the program's own loop act as" in the spec has it: a loop has no
logged in user, so it reads a table without row level security, which holds a project's id
and its user and nothing else, and then does all its work through the door as that user. A
trigger on `projects` keeps it, so no code path can forget. The loop is "Reading and the snapshot"
below; why the table is safe is in the section after it.

**The time zone is the user's, not the project's or the server's.** A day and a schedule mean the
owner's day and his evening, and he lives in one place at a time, so one zone per user,
`user_settings.time_zone`, Europe/Stockholm until he sets another, serves every project of his: a
zone per project would let two of his projects disagree about when his night starts, and the
server's zone is wherever the machine happens to be. It has a table of its own rather than a column
on a project, since it is about him, and is read only through `DevFlowTime`, which replaced the
fixed zone #452 added with a lookup of his, so the usage chart's days, the schedules and part 8's
budget and API cap read the same zone and cannot disagree. Only IANA ids are taken, and only in
their shape, area and place joined by `/`, or `UTC`: .NET converts a Windows id such as
`W. Europe Standard Time` and on Linux reads an id as a path under `/usr/share/zoneinfo`, and
neither is something a stored setting should lean on. A stored zone this machine does not know is
read as the default rather than failing every read of his projects.

**A schedule is wall clock, so it follows summer time.** The owner writes "22:00 to 06:00" to mean
his night, and that is still 22:00 on his clock after the clocks change; a schedule kept in UTC
would move an hour twice a year. A window is therefore days and two times, read in his zone at the
moment it is asked, by `DevFlowTime.At`, never stored as instants. The two times a change of clocks
makes odd are decided once, in that one function: a start the clocks skip starts at the first
instant after the gap, the nearest thing to what he wrote that exists, and a start they repeat is
the first, so a window never starts an hour late. A window that crosses midnight belongs to the day
it starts, since "Friday night" is a Friday window. Inside or outside is worked out each time the
rule is asked, from the windows and the clock, and never stored, so there is no job that flips a
flag at 22:00 to fail or run late.

## Reading this repository's runner

The first part's list of what waits for the owner is read from this repository's runner,
`scripts/agent/`, which records everything on GitHub. The one piece that knows its conventions is
`RunnerReader.Read`, in `src/DevFlow/Runner/`: given one pull request, the repository it is in,
its comments, its head commit's statuses and check runs, the comments on its slice issue, the bot's login and the owner's,
it answers an item when the pull request is the owner's turn, and nothing otherwise. It is pure:
no database, no network, no clock, and it never throws for what it reads, so a field it cannot
understand becomes text or is left out. Fetching what it reads, and keeping the list, belong to
its caller. What an item holds is in `src/DevFlow/Runner/RunnerItem.cs`; every text in it is
plain text as read, which a page shows as text.

It knows these conventions, each defined where the runner keeps it:

- **The verdict**, by `scripts/agent/verdict.sh`'s rule exactly: the latest comment by the bot,
  by creation time and then id, that was never edited and whose first line is the marker and
  nothing else. Its `## For the owner` section, as `.claude/agents/reviewer.md` lays a verdict
  out, is the reviewer's view.
- **Whose turn it is**, from the labels `scripts/agent/run.sh` sets, described in "What waits for
  the owner" in `High-level/docs/agent-workflow.md`: `owner-merge` or `owner-decision` is waiting,
  `owner-blocked` is stopped, and only an open pull request by the bot that is not a draft counts.
- **Whether merging may be offered**, from the merge gate's checks, `ci-ok` made by GitHub Actions
  and the runner's `review-ok` status, both green on the head, and only for a waiting item.
- **The pull request body**, as `.claude/skills/pick-up-slice/templates.md` lays it out: the
  summary, the slice from its `Closes` line, and the decisions, each split into what was chosen,
  the alternative and why. One is the owner's when it carries a bold mark starting
  `**owner-decision`, in each form the agents have written it, `**owner-decision**`,
  `**owner-decision.**` and `**owner-decision: what was decided**`.
- **The screenshots**, as `scripts/agent/screenshots.sh` publishes them: committed to the orphan
  branch `pr-screenshots` under `<number>/<name>.png`, and embedded in the body as
  `![<name>](https://github.com/<owner>/<repository>/blob/pr-screenshots/<number>/<name>.png?raw=true)`,
  with names from `capture()` in `e2e/conftest.py`, ending in `-desktop` or `-phone`, and the
  pick-up skill's before shots, ending in `-desktop-before` or `-phone-before`. A screenshot is
  every Markdown image outside a fenced block whose address is exactly that, for the reader's own
  repository and the pull request's own number, with a name of lowercase letters, digits and
  hyphens only; anything else is left out, silently. The item keeps the names, each once, in the
  body's order. The address itself is never used: those addresses need a GitHub session, so a page
  cannot show them, and Dev flow reads the file as the app instead, see "A code host's bytes handed
  to the browser" under the security decisions. `Screenshots.Group` pairs the names for the page by
  the part before their last `-desktop` or `-phone`, so a before pair is a pair of its own.
- **The reason**, from the runner's mentions in `run.sh`: a handover on the slice issue, with the
  merge gate's reason lines in parentheses, each made a sentence, the review rounds used up, or a
  Blocked notice on the pull request. Its tests hold the reader to the reason lines
  `scripts/agent/merge-gate.sh` prints and to the mentions `run.sh` writes, so a change there fails
  them.

**An owner label alone is never trusted.** The runner corrects its labels once a turn, so for a
moment after a new commit an `owner-merge` can sit on a head nobody approved. A waiting item needs
the latest verdict to approve the head commit as well, and the page must never offer anything
else for merging.

It goes when the runner does, part 10 of the development flow, since the program then records the
same things in its own tables. Its tests run against real pull requests of this repository,
recorded in `contracts/github/runner/`.

## Reading and the snapshot

Dev flow keeps an up to date picture of what waits for the owner in every project, as decided with
him for the first part: a loop reads GitHub into stored rows, and **the pages read what is stored
and never GitHub**. So a page is fast, still answers while GitHub is down, and can give the front
page its number inside the core's 500 ms. The cost is that the picture is up to a minute old, and
it says how old. The routes, the tables and the setting are "What waits" in `devflow/README.md`.

**Reading one project** is `ProjectReader`: the open pull requests in one list, then only for the
few that are the owner's turn by the runner's labels, by the project's bot and carrying an owner
label, their comments, their head's checks and their slice's comments, each through the reader
above; and the result replaces the project's stored items in one transaction, as its user. A pull
request without an owner label costs nothing beyond the list, and the list is a conditional
request, free when nothing changed, see "Reading cheaply" under "GitHub" above, so a minute's
reads stay small.

**One seam asks for a read**, `ProjectRefresher.Request`: the loop for every project on each
tick, the refresh route when the owner wants it now, merging and sending back after they act on a
pull request, and GitHub's webhook once the platform is reachable from the internet, which the owner wants then.
A project is read by one reader at a time, and a request made during a read waits for it and then
for one more read, since what changed may have come after the first began; every request during
the same read shares that one, so a burst of them costs one read.

**The loop** lists `poll_targets`, the one thing it reads outside a user, through the door's
`WithoutUser`, to which every guarded table is empty, and asks for each project as that project's
user, so everything it stores goes through the door as its owner. It reads as the app, never as the
user, so it goes on when the owner's link has expired; it uses his link only to learn his login,
which tells his own comments apart. It runs every `DEVFLOW_POLL_SECONDS`, a minute by default, in
the fake mode as well, and not at all with `0`, which the service's own tests use so no test's app
reads on its own; its time is `TimeProvider`'s, so its tests move it. A tick reads a few projects
at a time rather than all at once: each read holds a database connection while it runs, so reading
every project together grew the pool to one connection per project and, with the database's
connections shared by every service, ran the others out; a few at a time costs a tick of a hundred
projects seconds, well inside its minute.

**Stale and failing.** A project's picture is stale when its last successful read was more than
five minutes ago, or there has been none, and failing when its last read failed. A read that fails
leaves the stored items as they were, so the owner sees the last known list with how old it is
rather than an empty one, and records why as a short fixed phrase, never the host's own text,
which is data.

**One project never stops another**, anti goal 7 of the spec: each read has its own `try`, inside
the reader and again inside the refresher, so a project whose read throws is recorded as failing
and the loop goes on to the next, and the next tick tries it again.

## Cards

A card is one piece of work the owner hands Dev flow: an open issue, never a pull request, that
carries the project's pick-up label, `devflow` unless its settings say otherwise. Picking, as the
spec's round 2 decided, is by a label and only by a label. The routes, the tables and the page are
"Cards" in `devflow/README.md`.

**Picking is part of each project's read.** After the waiting list, `ProjectReader` asks
`CardPicker` for the project's cards, through the same `ICodeHost` and as the app, and stores them
with `CardStore` in the same transaction as the waiting items, so the two always describe one read,
and a read that fails changes neither and is recorded as failing like any other. Cards are read on
each project's minute, through the same seam, as the waiting list is.

**How a card is picked.** `CardPicker` lists the open issues carrying the pick-up label, which
GitHub answers with pull requests among them, and drops the pull requests. Then, for each, in this
order, the first that holds decides:

1. The owner has not linked GitHub: ignored, since Dev flow does not know which login is his.
2. A login that is not a trusted account of the project wrote it: ignored, "<login> is not a
   trusted account of this project".
3. A login that is not a trusted account edited its body or its title: ignored, saying who. Who
   edited an issue is read through GraphQL, since REST does not say; a deleted account shows as
   `ghost`, which is never on the list.
4. For an issue carrying the pick-up label itself, a card on its own or a feature's parent, no
   trusted account put it on: ignored, "The pick-up label <label> was added by <login>, who is not
   a trusted account of this project", naming who put it on last, or "was not added by a trusted
   account of this project" when its timeline does not say. A card of a feature carries none of its
   own; its parent's label switched it on.
5. It carries the hold label, whoever put it on: held.
6. It does not, but a trusted account put the hold label on and only someone untrusted took it off
   since: held, "Held by the label <label>, taken off by <login>, who is not a trusted account of
   this project".
7. Something it is blocked by is still open: queued, and its reason names what it waits for, `#12`,
   or `owner/name#12` in another repository, so the queue says which card it waits for, as the spec
   asks. It is not free to start until that is closed.
8. Otherwise queued and free to start.

**Who changed a label decides it, and the rule fails closed.** The pick-up label switches the
automation on, and anyone who can label issues in the repository can put it on, which GitHub grants
far more widely than writing the card. So a label counts by who changed it, as the spec's round 3
has it: for each label, the newest `LabeledEvent` or `UnlabeledEvent` of it on the issue's timeline
whose actor is trusted decides. The pick-up label counts only when that event put it on and it is
on the issue. The hold label counts while it is on the issue, whoever put it there, and also while
it is off when that event put it on and only someone untrusted took it off since. Both directions
fail closed: someone untrusted can stop a card, by holding it or by making it ignored, but can
never start one, release one or switch anything on. A deleted account shows as `ghost`, which is
never on the list. The rule is one pure function, `LabelActors.Judge`, over the events, the labels
on the issue and the trusted accounts, answering for one label at a time whether it counts, so the
review label of "Sometimes" is read by the same rule, and the picker and the merge check cannot
drift apart.

The timeline is read with one GraphQL query per issue, `timelineItems` of the types `LABELED_EVENT`
and `UNLABELED_EVENT`, each with its label's name and its actor's login, paged to the end. Half a
history is never taken for the whole: a page that fails, or an event the adapter cannot name, makes
the read unavailable, since the missing event could be the one that decides, and the project's read
fails and stores nothing, as any other failure does.

The reads are ordered so a card costs only what decides it: one by someone else costs nothing
beyond the list, only a card nobody off the list edited costs the read of its timeline, which a card
of a feature skips while a hold label on it or its feature decides first, and only a card still
picked and not held costs the read of what blocks it. A picked
card keeps a record of what was read, its body's SHA-256, its title, and the id of its edit
history's newest entry, which later parts compare against before building on it and before
merging, as "Not edited since it was read" in the spec's clarifications asks. Logins and labels are
compared whatever their case, as GitHub's are.

**Only a project's trusted accounts give instructions.** Each project has a list of trusted
accounts, the setting `trusted_logins`, as the spec's round 3 has it: the GitHub logins whose cards,
edits and comments count. It governs who may write a card or a feature's parent, who may edit their
body or title, and whose comment is input wherever one is read: sending a card back, running a
stopped card again, fixing a card, and fixing and sending back a feature. Where this document speaks
of the owner's comment as an input, it is a comment by any trusted account. A card that is anything
else is never queued, and is listed as ignored with the reason, so a card that does not run says
why rather than vanishing. Who changes a label counts by the same list, steps 4 and 6 above.

The owner's linked login is always on the list and is never stored there: it is added whenever the
list is read. A relinked account with a new login is then trusted at once and the old one is not,
which a stored copy would have to be kept in step with; and since it is never stored, it cannot be
removed, which the route refuses as well. Without a link nobody is known to be him and nothing is
trusted, as before. The others are compared whatever their case, as GitHub's logins are, and kept
once each. `TrustedAccounts` is the one place that answers whether a login is trusted, so the
picker, the merge check and the comment readers cannot drift apart.

A trusted account gives instructions and never makes Dev flow act as it. Dev flow acts on GitHub as
the owner only with his linked token, and as the app otherwise, whoever wrote the card. Watching the
default branch is not governed by the list either: a push is an action, not an instruction, and
only the owner's own or Dev flow's are fine there, see "Watching the default branch".

**A card that changes.** A card that has not started is decided afresh on every read, and one that
was read is read again, see "Reading a card". One that has started stays where its run put it: when the owner changed its body or title, its record is
replaced and it stays; when anyone else did, it is ignored with the reason, and a run on it is not
merged, since its state is no longer one a run merges from. An ignored card keeps the record it had,
so a run already under way is still checked against what was picked.

**Taking the label off** removes a card that has not started. One whose run is under way is marked
unlabelled and finishes its run, and the part that ends a run removes it then; one that has
finished, done or stopped, is removed, but a done card with its pull request. That one is kept
whenever it is no longer listed, since its pull request's `Closes` line closed its issue as it
merged, which takes it off the list exactly as taking the label off would, and the two cannot be
told apart from the list; the card is the story of what was built, and its page needs it.

**The order** in which queued cards free to start are handed out is `CardStore.Queue`:
`priority: high`, then no priority label, then `priority: low`, each oldest first by when the issue
was written. The spec left where priority is read from to round 6; it is these two labels, matched
whatever their case.

### Features and their cards

A feature is the spec's "parent issue with its cards as sub-issues": an open issue carrying the
pick-up label that has at least one sub-issue. Its cards are its direct sub-issues in the same
repository, in GitHub's sub-issue order. The routes and the table are "Features" in
`devflow/README.md`. How its cards are handed out and the branch they are built on are "A
feature's cards, one at a time" and "A feature's branch" under "Handing out work"; its page and
the rest of its life are the later slices of part 4.

**Why a sub-issue needs no label of its own.** The spec's round 2 has the label on a parent cover
every sub-issue, so a planned feature, which `/plan-feature` already makes as a parent with its
slices as sub-issues, is switched on in one step. A label on each would be as many steps as cards,
and a sub-issue added later would be left out until labelled. The label still decides what is
picked, only at the feature's level: a sub-issue of an unlabelled parent is never a card through
it.

**Why the parent is checked as a card is.** The parent is the feature's description, which later
slices quote in every brief and review the whole feature against, so it is an instruction as much
as a card's body is. It is held to the same rules, written by a trusted account, edited by nobody
off the list and its pick-up label put on by a trusted account, and keeps the same record of what
was picked. A parent that fails ignores
every card of it, read no further: a feature whose description is not trusted is not his. Each
sub-issue is checked by the card rules all the same, so a stranger's sub-issue under a trusted
parent is not picked either.

**What is a card of the feature, and what is not.** Only the direct sub-issues in this repository:
a card is a number in the project's repository, so a sub-issue in another is not stored, and the
feature's reason names it instead. A sub-issue of a sub-issue would make a feature a tree, which
nothing builds; it is ignored on its own. It is known only when the sub-issue in the middle carries
the label, since sub-issues are read only for labelled issues, as the slice settled; one under an
unlabelled card is not read at all. A labelled sub-issue is one card of its feature, never a
second card or a second feature.

**Two rules hang on what is stored, so the store decides them.** A labelled issue that started as
a card on its own stays one when sub-issues are added, and a closed sub-issue is a card only when
Dev flow built it, meaning it has started. Both need the card's stored state, which `CardStore`
reads inside the read's transaction; `CardPicker` reads only the code host. So a card taken by a
worker while the read ran is seen as started rather than turned into a feature.

**A feature goes with its last card.** Taking the label off the parent does to each of its cards
what taking a card's own label off does, and the feature is kept, marked unlabelled, while any card
still refers to it, so a run under way finishes in a feature that still exists.

**Priority and order.** A feature's priority is its parent's label and its age the parent's, so
features and cards on their own are ordered by one rule, "The order" above; a card's own priority
label inside a feature is not read, and its row carries the feature's.

**The settings table is a deliberate exception** to "A table arrives with the first slice that
needs it, in its documented final shape" in `High-level/docs/how-we-build.md`. `project_settings`
holds the four settings this part reads, the three labels and whether a card's pull request waits
for the owner, and nothing else. The spec's other settings, questions before starting, where a
feature's cards land, "sometimes", trusted accounts, the model per job, subscription or API key and
schedules, arrive with the parts that build them, each with its own migration: the spec's list of
settings is not a designed table, and a column for behaviour not yet built would be a setting the
page shows and the code ignores. A row is made with each project by a trigger, so no way of making
a project can forget it.

## Acting from the page

From the waiting list the owner merges a pull request or sends it back with a comment, and GitHub
records both as done by him, so the runner picks his comment up as it does one written on GitHub,
and he needs neither a terminal nor GitHub's own page for it. This is the only code in the first
part that writes to a code host. The routes, their refusals and the page are "Acting from the
page" and "The pages" in `devflow/README.md`.

**Only as the owner, by construction.** Both writes are made with his linked account's token from
`UserTokens.For`, so they are recorded on GitHub as his, and the runner, which starts a fix run
only on a comment by `Adamniels`, reads his comment as his input. The owner's two writes take a
`UserAccessToken`, a type of its own, and nothing else that could authorise them; an installation's
token is a string and an app's key an `AppWriteToken`, so handing either to one of his writes does
not compile, only `UserTokens` makes the type, which a test of the compiled code holds, and his
writes never ask for an installation's token. What the compiler enforces and what the test does is
the security entry "The owner's actions from the page are made only with his token".
`CommentTests.TheOwnersActionsAreNeverWrittenAsTheApp` and `GitHubWriteTests` keep it.

**The snapshot is never trusted for the act itself.** The list is up to a minute old, so a merge
names the commit the page showed: Dev flow refuses one that is not the stored head, and GitHub,
given it as `sha`, refuses to merge a pull request whose head moved on. Either way the project is
read again and the owner is told it changed. A stale page can cost a click, never a wrong merge.
What can be refused without GitHub, another user's item, a stopped or sent back item, checks that
are not green, a missing or expired link, is refused before GitHub is asked.

**A stopped item cannot be merged from the page.** The runner gave up on it and says "do not merge
it as it is", so neither the page nor the route offers a merge for it; a comment runs it again.

**A merge is one tap on a phone and cannot be cleanly undone**, so it asks first: a dialog names
the pull request, its commit and how many of the agent's decisions merging confirms, as merging on
GitHub does. Nothing else is recorded about a decision in this part. Sending back needs no
confirmation, since a comment can be answered.

**The page never writes what the runner owns.** No label, no status, no board column: after a
comment the runner sees the owner's input on its next turn and moves the label itself, and a merge
is the merge the runner's gate would make, a merge commit on a head with both checks green.

**A whole feature is merged the same way**, as the owner, with his token, naming the commit the page
showed, and asking first; what it adds, and why it is offered without the review's approval but
never without CI, is "Merging a feature" below.

## Workers

A worker is a program in a container on one of the owner's machines that takes a project's cards
and builds them, as the spec's rounds 3 and 9 have it. Every worker belongs to exactly one
project, and a worker calls Dev flow and is never called. What exists of it: a project's key, the
scheme a worker's request authenticates with, the worker API, `hello`, which records the worker,
`jobs/next`, which hands it a job, and a job's routes, see "Handing out work" below, and the worker
program that calls them, with the agent interface it runs a job behind, the base image it runs
in, the commands the page shows to build and start one, and the build job it carries through,
fetching, running the agent, checking and pushing. The routes, the tables and the panel are
"Workers" and "The pages" in `devflow/README.md`, the image and the commands "Starting a worker",
and the program, its settings, loop, exit codes and a build job step by step "The worker" there.

**One key per project, shared by its workers**, as the feature's slice settles. A worker is started
from a command with its project's key in it, so a key per worker would only add a step to starting
one; the price is that a key is ended for every worker of the project at once, which is what
replacing it is for. Removing a single worker, and a key per worker, are left for when a project
has workers it wants to tell apart.

**A key leads to a project and so to a user**, as "Who a worker and the program's own loop act as"
in the spec has it, and everything a worker's request does runs through the door as that user.
That is the second way a request authenticates, beside a user's token, and "How a feature
authenticates" in `High-level/docs/feature-contract.md` describes it. The scheme is Dev flow's own,
`WorkerAuthentication`, not `PlatformAuth`'s, since it is the only feature with workers; it is a
dozen lines that call the store, and the store holds the security, see the next section.

**A worker is the id it made when it started**, a UUID, with the name it sends, its container's
hostname, and the recipe version and base image it reports, which later slices compare with what
the project expects. Its primary key is the project and that id together, so a worker of one user
can never collide with one of another's. One not seen for ten minutes shows as "not seen since"
then; nothing removes a worker yet.

**The worker program is the hands, and knows nothing else**, as the spec's round 4 has it: the
program decides which card is next and judges every result, and a worker asks for work, does one
job and forgets it. It is C#, like the program, so the two share the definitions of what they say
to each other, `DevFlow.Protocol`, and a change that breaks either side fails to compile; and it is
published as one file carrying its own .NET runtime, so a project's image needs no .NET. It is a
console program, not a hosted service, since it serves nothing: it loops, and ends only when it is
stopped or its key is wrong. Dev flow being away is a wait, never an exit, so a worker outlives a
restart of the hosted copy without anyone starting it again; the wait grows from 5 seconds to a
minute while there is nothing, so idle workers cost Dev flow a request a minute each.

**Three layers make a worker**, as the feature settles. The base image holds what every worker
needs, the worker program, Node, git and a pinned Claude Code; the project's recipe, its own
`.devflow/Dockerfile`, adds what that project's check needs on top; and the worker is a container
of that image with the project's key. The base is the platform's and moves with its releases, the
recipe is the project's and moves with its repository, and neither has to know the other's
contents. A project's packages are not in any layer: its check installs them in each card's
working copy, so a change to a lockfile never needs a rebuild.

**The release builds the base image, and never starts it.** It is `devflow-worker` in the hosted
compose file with no replica, so the release's `compose build` builds it at the release's tag
beside every other image, from the same commit, and `compose up` starts nothing; the release
script is unchanged. A worker started from an image the release did not build would run a worker
program that may not speak the release's API; built together, the image's tag is the release, and
`DEVFLOW_WORKER_IMAGE` names it, so a worker on an older one is told apart.

**Dev flow hands out the recipe, and refuses one that copies files.** The terminal that builds a
worker holds no checkout of the project, so the command fetches the recipe from Dev flow with the
project's key, read from the default branch as the app, rather than asking the owner to clone the
repository on the machine; and the image is built with no context. A `COPY` or an `ADD` could then
only fail, or, with `ADD`'s addresses, fetch something no lockfile pins, so Dev flow refuses it
naming the line, before Docker gives a stranger error. The recipe's version is the newest commit of
the default branch that changed anything under `.devflow/`, so it changes when the setup does and
not with every push; see "Setting up a project" below for why it covers `project.json` too.

**A worker on an old recipe or base image gets no work**, and the page says to run the command
again. The version Dev flow compares with is the one it last read, by the recipe route or the
loop, so a recipe pushed to the default branch is noticed within a minute; a project whose recipe
has not been read judges by the base image alone, since holding a worker back for what Dev flow
does not know would stop work on a guess. Rebuilding itself, and a registry for the images, are
part 9 of the feature.

**The agent runs behind one narrow interface**, `IAgent`, as the spec's round 4 has it: given the
working copy, the brief, the report's schema, the limits and the credential, it answers a success
with the report or a failure with its reason. The reason says where the failure lies, in the card
or outside it, since the two are answered differently: a card that ran out of turns has to change,
and a run that hit the usage limit only has to wait. Claude Code run headless is the one real
implementation, and a fake answering a prepared report is the other, so the worker's own tests
spend no usage. The Agent SDK, another agent or a side program in another language would be a
third implementation of the same interface.

**A run succeeds only by a rule of six conditions, never by what Claude Code calls it**, from the
experiment of 2026-11-16 on the Mac mini with Claude Code 2.1.325, which found that its `subtype`
reads `success` on a run that failed, that a run refused a write still exits `0` as `completed`,
and that a run as root with `bypassPermissions` answers nothing at all. So every condition is
checked, exit `0`, a `result` event, `is_error` false, `terminal_reason` `completed`, a report
present and an object, and no refused action, and anything else is a failure; the table of which
failure lies where is "The worker" in `devflow/README.md`. The report's schema is checked by Dev
flow rather than the worker, which needs no JSON Schema library for it.

**The worker reads Claude Code as it runs**, as the feature "Dev flow: running now, usage and
history", #441, decides, so a run is no black box until its result: what the agent writes, each
tool it calls and each answer it gets become log lines while it works, and its usage so far is
counted. Claude Code runs with `--output-format stream-json --verbose`, which prints one event per
line, rather than `--output-format json`, which prints one answer at the end. The run's answer is
the stream's last `result` event, which carries exactly what the JSON answer did, so the rule
above is unchanged and judges the same answer the same way; only a stream with no `result` at all
is new, and it fails outside the card, since like no answer it says nothing about the card. The
stream gives the usage limit's reset exactly, as a `rate_limit_event`, where the answer gave it only
as text for people; the text stays the second way, for a run whose stream has no event that stopped
it. Each line is scrubbed of the token and the job's values before it goes anywhere, as the result
is, since a tool's answer is the likeliest place for a value the project's code printed. The
worker sends them to Dev flow as the run goes, with the check's output and its own steps, and the
posts are the run's heartbeat, see "A run is lost when its worker goes quiet" under "Handing out
work"; the pages that show them are the feature's later slices.

**A build job is one workbench and two users**, as the feature's decisions have it: root, the
worker, holds the push key and does everything that talks to the code host, from a bare repository
of its own in `/run/devflow/<job>/`, which the agent cannot read; the agent works as `agent` in a
copy of its own under `/work/<job>/`. Code crosses between them in two places only. Going in, root
writes a git bundle of the branch into a third folder, `/run/devflow-handover/<job>/`, root's and
mode 755, and the agent clones it, since the agent cannot read root's repository. Root never
writes in the agent's folder at all: a write there resolves a name the agent controls, so a link
it left at the bundle's name, or its job's folder replaced with one, would turn root's write into
one over any file the agent chose; git's lockfile follows such a link. In the handover folder the
agent can create nothing, so the name root writes at is root's. Root never writes a repository
for the agent either, so it never changes the owner of files it checked out, where a link in the
repository would hand a file of root's to the agent. Coming back, root fetches the agent's branch from the copy, and git's side of that fetch,
`git-upload-pack`, which reads the copy's config, runs as `agent`. git refuses to run it as root
in a repository another user owns, for "dubious ownership", and an experiment on Debian trixie's
git 2.47 on 2026 11 18 found that `-c safe.directory` cannot lift that, since git drops `-c`
options when it starts a command in another repository. Lifting it in root's global config would
have root's git read the agent's config; running that side as the agent leaves root only reading
the pack it is sent. The bare repository, rather than a checked out one, means root never checks
out the agent's files either.

**The check runs on a clean copy of the commit, and its exit code is what counts**, as the spec's
"The trusted checks run on a clean copy of the pushed commit" has it. The agent's copy holds
whatever it left, a build's output, an ignored file, a stale cache, which could make a check pass
that the commit alone fails; a fresh clone of the head commit holds only what was committed, and
so only what is pushed. What the agent left uncommitted and not ignored is work, so the worker
commits it first, "Work the agent left uncommitted", rather than lose it or check without it. The
check runs in the same container, so what the agent installed there is still there, which the
spec accepts; a project's CI on GitHub is the independent environment. It runs only when the agent
succeeded, since Dev flow judges a failure before a check, and a check of a run that failed would
spend up to 30 minutes of a worker on an answer nobody reads.

**The branch is pushed whatever the check said, and never forced.** Pushing the work of a failed
check, or of a run past its time limit, means it is never lost: whether it goes further is Dev
flow's decision, and a run handed out again after a lost one continues the branch rather than
starting from the base. A push without force, of the fetched head to the job's own branch, means a
history the agent rewrote is refused at the fetch, and a branch someone else moved meanwhile at
the push, rather than overwritten.

**A worker running a check stays heard.** A run is lost once its worker has sent nothing for 2
minutes, see "A run is lost when its worker goes quiet" under "Handing out work", and the check can
take 30. So the worker keeps posting to the run's log while the check runs, its output as `check`
lines and a post with none every 15 seconds when it is quiet, see "The job's log, sent as it runs"
under "The worker" in `devflow/README.md`; the route marks the worker seen as every post does. Saying `progress`
`checking` again every 5 minutes, as the worker does now, no longer keeps a run, since 5 minutes is
past the 2.

**A run's credentials are the Claude login and the project's values, and Dev flow holds both**, as
the spec's rounds 3 and 4b have it. The owner runs `claude setup-token` once and pastes the token on the Projects tab, so a
new worker needs no login step of its own: the program hands the token to each run inside the job,
as `CLAUDE_CODE_OAUTH_TOKEN`, see "Handing out work" below. It is one per
user, not per project, since the subscription is the user's, and it is sealed in the vault like
the GitHub tokens. Claude Code does not report when a setup token runs out, the experiment of
2026 11 16 found, so Dev flow counts a year from when it was given and warns a month before,
through the Projects tab, the nav's dot and the front page's card, which say it apart from what
waits rather than counting it. It checks only that what is pasted looks like a setup token; whether
it works is the worker's check before every run. The routes, the table and the section are "Claude
login" and "The pages" in `devflow/README.md`.

The project's values, what its tests or its start need, are the other: throwaway values or keys
made for the automation, never production's, which the owner gives on the project's page, so they
are in no repository and never copied to a machine by hand. Every job that runs the project's code
carries them beside the token, and the worker puts them in the environment of the agent's run, the
check and the start, and nowhere else. Why each is safe, and the risk accepted, is under "Security
decisions and accepted risks" below; how they are given and carried is "A project's values" in
`devflow/README.md`.

## Setting up a project

A project hands out no card until its setup has been tried, the feature "Dev flow: setup run and
recipe" has it, so a project never takes cards on a recipe nobody has run. How it is handed out,
recorded and shown is "Setting up a project" in `devflow/README.md`.

**Setting up is a worker's job, not the program's.** The spec's round 3 has the program build the
recipe and run the project's tests in it, but the hosted program can neither build images nor
start containers on the owner's machines until part 9's machine program. So the check is a job, as
a card's build is: the worker that will take the project's cards is the one built from the recipe,
so checking in it is checking the recipe itself, on the machine where it runs, and part 9 only
starts its workers from the page. It runs no agent, so it needs no Claude login and is handed out
before that is looked at; its key only fetches, so its pictures go to Dev flow in the result rather
than to `pr-screenshots`.

**Every version is checked once, and before any other job.** A card's own check ran in the
container the old recipe built, so a version a card changed has never been tried as a whole, and
the first worker built from it would otherwise take cards on it untried. Checking per version, not
per worker, means a second worker on a checked version starts work at once, and one check at a
time, by version, means two workers starting together do not run it twice. A failed check holds
back only that version's workers, so a registered project whose new recipe breaks keeps nothing
merged at risk, and it waits for the owner's "Check again" rather than retrying, since running the
same commit again would only fail again; a new version starts afresh. A failure outside, the
network or the machine, is no verdict on the recipe, so it is put back and makes the project wait,
as "Stops" has it for any run.

**The version covers `project.json` too.** The check, the start and the address are the setup as
much as the Dockerfile is: a changed check command that never passes would otherwise reach cards
unchecked. So the version is the newest commit changing anything under `.devflow/`, and a change to
`project.json` alone asks workers to rebuild, a cheap rebuild of an unchanged image, which is the
price of one version naming the whole setup. The column keeps its name, `recipe_version`.

**Setting up or registered is computed, not stored.** A project is registered once any check has
passed, read from the checks themselves, so the state can never disagree with them; the later
slices of part 6 add their steps to what registers a project the same way.

**The setup run is a worker's job too, on the base image itself.** A project with no valid
`.devflow/` has no recipe to build a worker from, so the page's command starts one from the base
image, which reports no recipe version and is handed only that project's setup jobs. The worker is
told apart by its version, not by a kind of key, so one key still serves every worker of a project.

**Its worker runs no check.** In the base image the agent runs without root and cannot install
what its own recipe installs, so a check there would fail for reasons that say nothing about the
recipe. Dev flow validates what it pushed instead, both files at the commit, judged as a card's
check that failed; and the check that counts is the check at registration, which the first worker
built from the merged recipe runs before any card.

**Its pull request always waits for the owner.** The recipe decides what runs as root in his
containers on his machine, so whatever the project's settings, Dev flow never merges it: the merge
check reads only cards, so no setting can reach it. It is reviewed and fixed like a card's first,
so what he reads is a reviewed recipe with what it could not settle said plainly.

**A setup that stopped before its pull request is run again from its step.** The owner's comment
on its pull request is what runs a stopped setup again, as it is for a stopped card on its issue.
A setup has no issue, and one that stopped before its pull request opened has nothing on GitHub to
comment on. Opening its pull request all the same covers only some stops: a run that failed, timed
out or pushed nothing may leave no commit beyond the default branch, and GitHub opens no pull
request for that. So the page takes his comment instead, the same input a comment on GitHub would
be, and the setup runs again with it, whatever stopped it. A pull request opens only for what is
valid, so he never reads a broken setup on GitHub.

**Cards wait for the settings, beside the check.** The settings decide what merges without the
owner, so a project that took cards on a new project's defaults, or on settings he never looked at,
could merge work he meant to see. The step is done by his first save, not by a separate "accepted"
flag he sets, since saving the settings page is already him confirming them, and "Accept" goes through the
same route, so a preset is never stored, as "Projects" has it. The setup run and the check go out
before it: neither merges anything, and the proposal he accepts comes from the first. Only readings
and builds are held, the start of a card's work, so reviews and fixes of work already under way
finish.

**The settings step waits for the setup's pull request.** It is offered, and a save does it, only
once `.devflow/` is valid on the default branch and no setup sent back is under way. Before then the
setup run's proposal can still change: a fix round after its review, or a setup sent back, proposes
again, and the proposal shown is the newest that succeeded. A step done earlier would leave the
proposal that finally merged unasked. The cost is that a project whose `.devflow/` is not valid
takes no card until its setup merges, even when a check of its recipe passed; that project needs
its setup anyway.

**Existing projects confirm their settings once.** Fodr's settings were set before any setup run
proposed them, and every project connected before the step would otherwise count as accepted
without him having been asked. The migration leaves the step not done for all of them, so each is
held until he saves its settings once, which costs one click and leaves no project that skipped the
step.

**A failed check is sent back to a setup run, not fixed in place.** The check failed on what the
recipe installs or what the check runs, which is the setup agent's work, so the fix is a new setup
run with the failure in hand rather than a card. It starts from the default branch, where the
recipe that failed is, on a new branch, since the earlier setup's branch merged. The files are read
as they were checked and kept with the setup, so its brief quotes what failed even if the default
branch moves; the comment is taken on the page, as the stopped setup's is, since a check has nothing
on GitHub to comment on. The setup is needed only while the version it was sent back from is the
project's, so a recipe fixed by hand ends the need. A setup not done that the project no longer
needs, one whose pull request was closed after `.devflow/` was fixed by hand, still holds the one
open setup a project may have, and so stops a later send back until it is done; that is accepted for
now as rare.

**A project's values are a step of setting up, and hold nothing back.** The setup run is what reads
the project and finds what its tests need, so it lists the variables and the step asks for each,
after the settings and before a worker on the recipe, the order the owner meets them in. The step
does not stop the check or any card: a check that runs without a value its tests need fails like any
other, and is sent back or checked again, which a hold would only make the owner wait for. A name
the setup listed that the values do not take, lowercase or reserved, is shown with why and does not
hold the step back, since the owner could never do it, and the check says whether the project really
needs it.

**A value is sealed in its project's own table.** The Claude login keeps its sealed token in
`secrets`, beside a table that points at it; a value kept that way would outlive its project, since
disconnecting removes the project's rows and not a secret's. So the vault seals a value bound to the
user, the project and the name, and `project_values` keeps it, gone with the project by its foreign
key. A value is at least 8 characters, so that scrubbing it from text never takes out ordinary
words, and the field asks for a longer throwaway value instead.

**A value is scrubbed in two places, with one function.** Dev flow scrubs what every worker sends
and what it posts, since it cannot trust a worker or an agent to have done it, and the worker scrubs
what it sends and logs, so a value never leaves the container in clear even to Dev flow. Both use
`ValueScrub`, in `DevFlow.Protocol`, so the forms found cannot drift apart. Dev flow scrubs with the
values as they are when the result comes, and the worker with the values its job carried, so a value
changed or removed while a job runs is still found by one of them. Posting is scrubbed in front of the
code host, so a poster added later cannot forget it.

**"Waiting for you" lists only a check that failed.** The feature has a project setting up shown
there, but a project connected only so Dev flow reads this repository's runner, the platform
itself until part 10, would sit there for good with nothing for the owner to do. So "Setup" lists a
project whose newest check failed with no other asked for, where the owner is needed; the steps of
one still setting up are on its own page. A setup run that waits for him to merge it, or stopped,
is listed there too, for the same reason: he is needed.

## Handing out work

A worker asks for work, and Dev flow decides which card, writes the brief, and judges what comes
back, as the spec's round 4 has the program as the manager and a worker as the hands. The routes,
the job, the result and the table are "Jobs" in `devflow/README.md`.

**One card, one lock, and no count across projects.** `jobs/next` takes the first card of the
worker's own project that is queued and waits for nothing, in the order "Cards" above gives, with
`SELECT … FOR UPDATE SKIP LOCKED`, moves it to `building` and records a run, in one transaction as
the project's user. `SKIP LOCKED` makes a second worker asking at the same moment pass over the
row the first holds rather than wait for it, so it takes the next card or nothing; a unique index
allowing one run under way per card is a second guard beside it. There is no limit on runs at once
and no turns between projects, at the owner's wish in the feature's decisions: how many run is how
many workers are started, and a worker belongs to one project and so to its user.

**One rule decides what runs.** Whether a project's next job runs, and for every card waiting why
it does not run yet, is `RunDecision`, a pure function over the project's situation read in one
query, and the dispatcher asks it in place of gates of its own, as "Next up" on "Running now" does
for its reasons. Before it, the dispatcher checked its gates one after another in its own code while the
project's page read some of them again by itself, so a page could say a card was next while the
dispatcher held it back, or the other way, and a gate added to one was easily missed in the other.
Now each gate is one SQL fragment where it always lived, `WatchStore.HoldSql`,
`WorkerStore.RebuildSql`, the setup's checks, `JobStore.NextKindSql` and the card predicates the
claims use, and the rule reads those fragments rather than copies of them, so a change to a gate is
a change to what both see; `RunDecisionTests.TheDispatcherDoesWhatTheRuleSays` holds the dispatcher
to the rule in staged worlds. Its answer is a closed set, run, a `WaitReason` with its sentence and
where it is acted on, or nothing to do, so the page shows the rule's own reason rather than
rewording a code. The gates are met in the order the slice settled, which puts the project's wait
after failures outside its cards in two places: other failures before the setup's check, and
Claude's usage limit after it and after the Claude login. A check runs no agent, so a usage limit
no longer holds it, where the dispatcher before held every job; a network failure still does, since
the check needs the network as much as a build. What a job needs from the code host is read after
the rule has answered run, as before, since it is a question for GitHub and not for the database.

**The pauses are the rule's first reasons.** The owner pausing a project, or everything, is the
strongest thing he can say about it, so it comes before every reason that existed before it:
everything paused, then the project's own pause, then a rebuild and the rest. Ahead of a stop, since
a project both paused and stopped by a commit is held by his own word first, and the stop still
waits for him under "Waiting for you"; ahead of the setup's check too, since a check is a job a
worker runs, and a pause that let it through would not be one. Everything paused is its own table
rather than a mark written on each project, so continuing it leaves a project he paused on its own
paused, and a project connected while everything is paused is paused from the start. A pause is
its own SQL fragment, `Pauses.PauseSql`, and not part of the watch's `HoldSql`, which the merge
check and a feature's end read as well: a pause holds the jobs that spend his usage, and leaves
merging what is approved, reading GitHub and opening pull requests going, since none of them
starts a run. The dispatcher reads the pauses again under the project's row lock, as it reads the
watch's hold, so a pause recorded while GitHub was asked still holds the work back.

**A schedule is the rule's next reason, after the pauses.** Outside its schedule a project waits as
a paused one does, for every kind of job and with a run under way finishing, since a schedule is
the owner saying when he wants his usage spent, a pause written in advance. It comes after the
pauses, since a pause is his word now and a schedule his word ahead of time, and before every reason
that existed before part 8: a project outside its hours has no use for being told it needs a
rebuild. It cannot be an SQL fragment as a pause is, since its arithmetic is wall clock in a zone,
which Postgres must never decide, so the rule's one transaction reads the mode, the windows and the
zone and `Schedule.At` works it out in C#; the dispatcher does the same again under the project's
row lock, after the pauses took it, and saving the schedules takes that row `FOR NO KEY UPDATE`, so
schedules saved, or a window that ended, while GitHub was asked still hold the work back. The
reason carries the next start in the zone's own offset, so its sentence on Running now is the
owner's wall clock without the record holding a zone. The workers list's `held` and the projects'
summary, which are SQL, do not carry it; the project's page says it in its own card, where the
windows are.

**The daily budget comes between the pauses and the schedule.** Reached, a project waits as a
paused one does, for every kind of job, with the run under way finishing. It comes after the pauses,
since a pause is the owner's word now and a budget his cap set ahead, and before the schedule, so
"Run now anyway", which narrows only the schedule, never spends past a cap he set: an override says
when, the budget says how much. Like the schedule it cannot be an SQL fragment alone, since its day
is wall clock in the user's zone, so `DailyBudget` reads the zone, works out the day's bounds in C#
through `DevFlowTime`, and sums the runs that finished inside them with `Tokens.Sql`, the one
definition, never a second sum; the dispatcher reads it again under the project's row lock, after
the pauses took it, so a run that finished or a budget lowered while GitHub was asked still holds
the work back. Two workers handed jobs at once under the budget may both finish over it; the
overshoot is bounded by the runs under way, one per worker, which the slice accepted as "one run at
most" for the usual single worker. The workers list carries it apart from `held`, as `budget`, with
the rule's own sentence, so the project's page says it in the rule's order without an SQL fragment
or a second wording.

**"Run now anyway" narrows the schedule, it does not lift it.** An override for a feature or a card
on its own, a row of `run_now`, lets the rule pass the schedule for what it covers and for nothing
else of the project: outside its schedule with one that holds, the rule goes on through its later
gates with the next kind among covered jobs alone, `JobStore.RunNowKindSql`, which is the next kind's
one SQL with each card's and feature's condition followed by `RunNowStore.CoversCard` or
`CoversFeature`, so the order of kinds stays stated once. A setup's job or a check, which no
override covers, still waits for the schedule, and each card the rule lists answers
`OutsideSchedule` unless an override covers it. The dispatcher reads the schedule again under the
project's row lock as before, and outside it takes only covered work, every claim carrying the same
filter, so the page, the rule and the claim never disagree. It sits after the pauses and touches
nothing before them: a pause is the owner's word now and the override his word for one thing ahead
of the clock, so a paused project with an override still hands out nothing.

An override is a row rather than a mark on the feature or the card, since it ends by itself: it
lasts until what it covers has nothing left to run, every card of a feature and the feature itself
waiting for the owner, done, stopped, refused, needing answers, held or ignored, which
`RunNowStore.Left` states once and the coverage reads too, so an override whose thing has finished
covers nothing even before it is deleted. A feature building with every card done still has its end
and its review ahead, so it still has something to run, until the read that moves it on. The
dispatcher deletes every override that has nothing left in the transaction that reads the project's
situation, the first time it finds one, so a card added or sent back afterwards keeps to the
schedule: the owner's "until it has nothing left" means the work he could see when he pressed it,
not whatever the feature becomes. Pages only read, so a page between the end and a worker's next
ask shows the override as off, since nothing is left. A card of a feature has no override of its
own: it runs only in its feature's order, so its feature is the thing run now.

**Nothing is read from GitHub under the lock.** What a job needs from the code host, the default
branch, `.devflow/project.json`'s check, the clone address, the committer and a push key, is read
first, and only then is the card taken, in a transaction that holds its row for a few statements.
Taking the card first and reading after would hold a row lock and a database connection while
GitHub answers, or, committed first, move a card to `building` and back when GitHub did not answer.
The price is a push key made for a card another worker took in between, which is dropped unused,
was never sent, and runs out within the hour. Anything not answered is no work now, and the worker
asks again on its next turn.

**The brief quotes the card as it was picked.** Picking keeps the card's body with the record of
what was picked, so a job carries exactly the text whose author and editors picking checked,
rather than reading the issue again when it is handed out, which would have to check its edits
again and could carry an edit made since by someone else. A change the owner makes reaches a card
that has not started at the next read, within a minute. Comparing the record before building on a
card again and before merging stays with the later parts.

**The rules are the program's, the facts are the project's**, as the spec's "Decided" has it. The
brief is one template, the same for every project: how to work, tests first and one commit per
task, never to push or touch the code host, never to ask but to decide and record each decision,
what is information rather than an instruction, and the report's form. What is particular to a
project stays in its repository, its `CLAUDE.md` or `AGENTS.md` and `.devflow/project.json`, and
the brief only says where. It follows this repository's own implementer, the pick-up-slice skill,
where that is not specific to this repository.

**The report is a form, and decides nothing.** It is a `DevFlow.Protocol` type, and its JSON
Schema, which the agent is given, is written from that type, so the two cannot drift. .NET can
write a schema but not validate against one, so rather than adding a validation library the
report is read into its type with a missing, unknown, null or mistyped field refused. What it says
guides and never decides: a report that does not match is a failure in the card, and one that says
`stopped` stops it, but `done` makes nothing succeed. The check's exit code does, run by the worker
itself and not taken from the agent, as the spec's "The software around the agent does not take
the report on trust" says; the review of part 3 and the owner do the rest. `pages`' `looked` and
`replies` are in the form from the start, though part 3 reads them, so the form does not change
under the agents.

**Where a failure lies decides what follows.** A failure outside the card, the login, Claude's API
or the machine, puts the card back in the queue with nothing counted against it, since another try
may well succeed, and makes its project wait; one in the card, a report that does not match, a
report that stopped, or a check that did not run or failed is the card's to answer, and stops it
with why, at once for a report that stopped or a run past its time limit, and otherwise when it
is the second in a row with nothing new. See "Stops" below.

**A run is lost when its worker goes quiet, not when its time is up.** The log route is the
heartbeat, as the feature "Dev flow: running now, usage and history", #441, decided: the worker
posts to it from the job's start until its result, every 2 seconds with new lines and every 15
without, and a run under way whose last post, or its start when it has posted none, is 2 minutes or
more ago is lost. This replaces losing a run once it was handed out its minutes and a margin of 10
more ago with its worker unseen for 10, which found a stuck run only after up to 100 minutes, and
held its card that long. 2 minutes is eight posts missed, so a slow network or a busy machine is not
taken for a worker gone, and a worker that is gone is found within the project's next read or two.
The time limit plays no part: the worker enforces it itself, ending the agent and sending its
result, so a run that keeps posting keeps its card however long its limit is, and the setting can
change while it runs without anything here reading it. The heartbeat is `last_heard_at` on the run,
set by every post with lines or none in the transaction that stores the lines, and not the worker's
`last_seen_at`, since a worker that is seen asking for work or posting for another run says nothing
about this one. The search runs on each project's read, before the code host is asked anything, so
GitHub being away holds no card.

**A run's log is kept under the run, each line once.** A line is keyed by its run and its `seq`,
which the worker numbers from 1, so a post retried after its answer was lost stores nothing twice,
`ON CONFLICT DO NOTHING`, and a gap, a post that never arrived, is allowed rather than refused,
since refusing it would lose every line after it too. Each post takes the run's row first, so two
posts of one run, and a post and the run's result, store one after the other, and a post for a run
that has finished is `404` like any other route of a finished job. A run keeps at most 20,000
lines, about four times what a long build writes, and then one line saying the rest was not kept,
so a run whose agent loops on a tool cannot fill the database; the count is read in the same
transaction, under the run's row. A line's text is scrubbed of the project's values again, as a
result's is, see "Security decisions and accepted risks", and cut to 4,000 characters, since a
tool's answer is already cut to 2,000 by the worker and a line is for reading. The usage so far is
the run's token figures while it runs, which the result's usage replaces, so a page can show what a
run has used before it ends.

**Lines reach a live page from memory, never by polling.** The lines a post stored are handed to
`RunLogFeed` once their transaction has committed, so a reader never sees a line the table does not
hold, and the live pages read them from it. Dev flow runs as one replica, so every line
passes through it. A reader that falls 2,000 lines behind is ended rather than handed lines with a
hole in them, and takes up again from the table after the last `seq` it had, which the browser's
`EventSource` does by itself with `Last-Event-ID`; publishing never waits for a reader, so a slow
page never slows a worker's post. The feed holds nothing a reader has not asked for, and nothing
once its readers are gone.

**Live pages are server-sent events.** Running now's list and each run's log reach the page as
`TypedResults.ServerSentEvents` of .NET 10, `text/event-stream`, which the browser's `EventSource`
reads: no new dependency, no socket to hold open both ways, since the page only listens, and
Caddy passes the stream through unbuffered. `EventSource` opens a stream again by itself and sends
the `Last-Event-ID` it last had, which for a log is the last line's `seq`, so a page that loses its
connection takes up where it was rather than reading the whole log again. A log stream is fed by
`RunLogFeed`, and its end by the same feed: the result being recorded, or the run being found lost,
ends its readers once committed, and a reader whose lines end reads the table once to tell the
run's end from having fallen behind. Both rely on Dev flow being **one replica**: a line stored or a
run ended by another process would never reach this one's feed. A second replica would need the
feed carried between processes, Postgres's `LISTEN` and `NOTIFY` the likely way, and this is where
to change it. Running now's list is the one stream that reads the database, every 2 seconds for
each open page, since what it shows, a run starting, ending or using tokens, comes from several
places; it sends only when the list changed. A stream is let in by the token it opened with and
ends when that token runs out, so nothing is sent on a token past its time; the page opens it again,
and a stream refused with a `401`, which `EventSource` cannot refresh, has the page's client refresh
the session first. A stream's headers go out with its first event, so each sends one at once, the
list, or a log's `start`, and the page and any proxy between see it open.

**Usage counts each run once, where its own columns put it.** Usage and history sums a run into
its card by `card_number`, its feature by `feature_number`, or, with neither, its project's
"Setting up", and a card into the feature it is a card of now. A card's page lists its feature's
readings too, since they read it, but counting them there as well would count one run twice; the
run's columns are the one attribution, and they never change once it is handed out. A run of a card
whose label was taken off keeps its number, so its usage stays, shown without a page. The period
counts the runs that started in it, so a run is in one period or not and the sums add up, while a
card's elapsed time, fix rounds and sends back are its whole: half a card's span would say nothing.
The token is one definition, `input_tokens + cache_creation_input_tokens + output_tokens`, held in
`Tokens` as C# for what a page reads run by run and as SQL for what a query sums, beside each
other, so Running now, a card's and a feature's page and this tab cannot disagree, and part 8's
daily budget counts the same thing. Cache reads are shown beside it: on a long run they are most of
the figures, and cost far less, so in the sum they would drown what was used. The sums are read per
request from `runs`, grouped by card, feature and project, with no table of their own; one user's
runs are few enough, and a stored sum would be one more thing to keep right.

**Days are counted in one zone, read through `DevFlowTime`.** A day Dev flow counts, the tokens per
day on Usage and history and part 8's daily budget next, is a day in the user's zone,
`DevFlowTime.ZoneOf` in `src/DevFlow/DevFlowTime.cs`, Europe/Stockholm until he sets another, never
a day in UTC or in the server's own zone, which would put a run at 00:30 in the owner's night on the
day before. It is the one place a zone is read, through .NET's own IANA zones with no dependency;
#452 made it a fixed zone so that #455 could make it each user's by changing that one place, see
"The time zone is the user's" under "Projects". `DevFlowTime` turns an instant into its day and a day
into its bounds, so a day across a change of summer time is 23 or 25 hours, and a query is handed
those bounds rather than a zone name: Postgres's time zone rules never decide a day, and C# and SQL
cannot disagree about one. The chart is always the last 30 days, whatever the period, since the
period rolls by 24 hours and a day does not; mixing the two would leave a half day at the chart's
start.

**The model and the limits are the project's, carried by the job.** Which model reads, builds and
reviews, and the minutes and turns a run takes, are settings, as the spec's round 4b has them per
job per project, each starting on the strongest model; the values are Claude Code's aliases, which
follow the newest model of each family, where a pinned id would need a release for each new model.
They are read in the transaction that takes the work and handed to the worker in the job, so a run
keeps what it started with and the worker holds no model of its own: one setting on the worker
would have been a second place to change, and could disagree with the project's. A job without a
model is a protocol error the worker refuses, outside the card, rather than guessing one.


**Every run is measured from the first day**, as the spec's round 3 has it: Claude's usage is kept
as it came, and its token counts, cost and turns in columns of their own, with the job's duration,
for the usage page of part 7.

**The fake worker** takes a job and ends it with a prepared result through the same code a worker's
result goes through, and commits on the branch in the fake's git repository, so an acceptance test
drives handing out, the result and what later parts build on it without a container or a model.

**A feature's cards, one at a time.** Inside a feature one card runs at a time, as the spec's
round 2 has it, since its cards build on each other in one branch: a card is let start only while
no card of its feature has a run under way, nor the feature itself, its reading among them, and
every card before it is done, stopped, refused or held. A
card before it in any other state holds it back, whatever that state is, queued and waiting on
GitHub, ignored, needing the owner's answers, or waiting for the owner, because its work is what the
later card builds on: a card that is not finished might still change the code under it, and one
that is ignored or waits for its own blockers is still meant to come first. A card that needs
answers is the plainest case: it is the owner's turn as a refused card is, but unlike a refused
card it is meant to be built as it is, once he answers, and it comes first; a later card built
meanwhile would be built on code that does not have it yet, and on a guess at answers the earlier
card's questions were asked to settle. So it holds back as a queued card does, the feature's
"Inside a feature" decision, and an earlier card that is ready is built while it waits. A stopped, refused or held card is the exception, since
the spec's "A card stops only when..." has a stop hold back only that card and the cards depending
on it, and a refused card is the same case: it waits for the owner, as a stopped one does, for an
answer that may take days, and holding the whole feature for it would stop work that does not need
it, which the feature's "Inside a feature" decision rules out; and the owner's hold label says the same of one card: the rest of the feature goes on, and
a card that does depend on it says so with a blocked by link on GitHub, which holds it as any
blocker does. The rule is one SQL expression, `CardStore.HeldBackBy`, which handing out and the
page's reason both read, so the page can never say a card waits while a worker takes it. The
reason is computed as the cards are read and never stored, because the card before moves on
through runs and merges, not through a read of the project. Different features run side by side,
as far as there are workers, and between features and cards on their own the project's order
stands.

**A feature's branch** is made by the app, from the default branch, when the feature's first card
is about to be handed out, as the spec's "Clarified after the builder check" has it, with a push
key and outside the lock, as every read for a job is. It is made at the default branch's head as
the code host reads it then, and recorded with that commit, which "Bringing main in" later
compares against. Making it is claimed on the feature before GitHub is asked, since two workers
asking at once would otherwise both ask for the same name and the loser would see a branch that
exists. That is also why a branch of the name that the feature has no record of is never taken:
it is someone else's, and building on it would put work Dev flow did not check under every card;
the feature waits for the owner instead. One case cannot be told apart from it, GitHub making the
branch while its answer is lost, and it is treated the same, the stricter reading. A claim whose
worker never came back is taken over after two minutes, longer than the two requests can take.

**A stopped project**, or one whose default branch has not been read for five minutes, hands out
nothing, ahead of everything above, and the transaction that takes the card reads that again under
the project's lock, see "Watching the default branch" below.

### Reading a card

Before anything is built, a fresh agent reads the cards with the code they touch and answers for
each whether it is ready, has questions, or is too thin, as the spec's round 2 has it and the
feature's decisions keep whatever the project's settings. How it is handed out, its form, its result
and its table are "Reading a card" under "Jobs" in `devflow/README.md`.

**Why every card is read.** A run that builds from a card the owner left unclear spends a whole
build on a guess, and the guess is found at the review or by him, when it is dearest to undo. Asking
before is cheap: a reading reads and writes nothing, so it costs a fraction of a build, and it is
the one moment a choice that is his can reach him while he is still there, as the feature's "What I
want" puts it, rather than after the night's work. A card read as ready costs one short run; one
with questions waits for the owner's answers, or, with the project's questions off, is built on the
reader's recommendations, below; and one too thin is not built at all, `refused` with what it lacks,
rather than built badly.

**Questions on and off.** The setting `questions` decides what a card with questions does, read
when its reading comes back, as every setting is read at the next decision. On, the card is
`needs_answers`, the owner's turn, and nothing about it reaches GitHub until he answers: asking him
is the point of reading before building, the feature's "What I want", and a comment or a label on
the card before he has answered would be noise on an issue he is about to settle from the page. It
waits under "Questions before starting", first on "Waiting for you" as the spec's round 7 has it,
since answering it is what lets a night's work start. Off, the card goes on the reader's
recommendations, which the app posts on the card so the owner sees what was chosen for him, and the
build's brief carries them as the answers; a project that wants the agents to run without him,
"Fully automatic" or "Guided" among the presets, gets that. On is the default, and every project
there was is on, since a new project starts as "Only I merge", the feature's decision, whose
preset has questions on. A card already waiting is not let go when the setting is turned off: the
decision was made when it was read, and the owner can answer it from the page; turning it off
counts from the next reading. A card picked again after its label came off and on keeps its
reading, and is held for answers again on the project's read while nobody has answered its
questions, so it is never built on the recommendations by accident. Turning it on has one
window the other way: a card queued on the recommendations whose comment the app has not posted
yet, about a minute, is held for answers on the project's next read, since nothing records yet that
its questions were answered. That fails safe, asking him rather than building on choices nobody has
seen.

**His answers are his comment, posted as him.** The spec's round 2 answers questions on the page of
what waits for him, and his answers are posted on the card as his comment, with his linked token,
under "Acting as the owner": the issue then carries the decision in his name, where anyone reading
the card later sees it, and the build's brief carries the same answers. One comment holds all of a
card's answers, each question with its answer and "(recommended)" where he accepted the reader's
recommendation, since the questions are one decision about one card and a comment per question
would scatter it. Every answer is required: a card is built on all of its answers or not at all,
and an unanswered question would leave the build guessing, which is what asking was for. The
answers name the reading they answer, and a page showing an older reading's questions is refused,
so answers never land on questions that were not the ones shown. They are kept with the reading
that asked, not on the card, so a later reading's questions never take an earlier reading's
answers. The comment's id is kept with the reading too, so Dev flow's own answers never read the
card again, and the card is queued without another reading: the reader's questions are answered,
and nothing about the card changed. GitHub taking the comment while its answer was lost would
otherwise post it twice; the same answers sent again find his comment by its exact text and keep
it, as the app's comments are found by their last line.

**A refusal is marked where the owner reads the card, as a stop is.** The spec's "A stopped or
refused card is marked on the card itself" settles it: the attention label and one comment from the
app listing what is missing, on the issue he will edit anyway, and the card under "Refused" on
"Waiting for you", after "Stopped" and counted as everything that waits, so a thin card is never
silent. Nothing is offered on the page: the answer to a refusal is a better card, which is written
on GitHub, and a box on the page would only post a comment there. The comment is found again by its
last line, as a stop's is, and its id is kept with the reading that refused, not on the card, since
a card refused again gets a comment of its own for its new reading. Dev flow's own comments never
start a reading; they are known by their ids.

**A card is read again when it changes before it starts.** Nothing is built from a reading of a
card that no longer says the same, as the spec's clarifications have it: "a card is read again when
it changes". While a card has not started, `queued`, `needs_answers` or `refused`, a trusted
account's edit of its body or title, or comment on it newer than its newest reading, reads it again.
An edit is seen without reading anything more from GitHub: picking already replaces a card's record
whenever every editor is trusted, and ignores one anyone else edited, so the record no longer
matching what the newest reading was handed, its body's SHA-256 or its title, is a trusted edit. A
comment counts only from a trusted account and only when newer than the reading, as a stop's does,
since one from before it was written while the card was read and is not about what was read. For a
refused card either is the owner answering the refusal, the spec's round 4 having `Refused` wait
for him, and for one that needs answers it is the same: it was his turn, and he changed the card
instead, so its questions may no longer be the ones to ask. Either is queued, no longer his turn,
and its attention label comes off. A queued card so changed is due a reading rather than a build,
and the rule "a build takes only a card whose newest reading is ready or has questions and is not
to be read again" keeps it from being built meanwhile.

It is read alone, with the note of the code the feature touches, the feature and its done cards'
handover notes, what was missing for a refused card, and the comments, fenced as that account's
input: the rest of the feature was read already, and reading it all again would spend a run on
cards nobody changed and could move cards that are under way since. The run is the card's, not the
feature's, so the feature's note stays what the whole feature was read to write, and is never
rewritten from one card. The cost is one read of each such card's comments on every project read,
as the refused cards' already was: GitHub's issue list says nothing of comments, so a card can be
known to be commented on only by reading its comments, and they are read only while it can still be
read again.

**Dev flow's own comments never read a card again.** They are known by their ids, kept with the
reading each is for: the comment listing the questions and the recommendations chosen, the
refusal's, and the owner's answers posted from the page, which are his comment, posted with his
token, but Dev flow's action and already taken. The app's comments are never a trusted account's
anyway, since a trusted login is a GitHub user's; his answers are, which is why the ids, not the
author, decide.

**Answers belong to the reading that asked.** A card read again with questions asks them again,
and needs his answers again with the setting on, or goes on the new recommendations with it off.
The earlier answers were to an earlier card: the card changed, so a question may mean something
else now, or not be asked at all, and matching old answers to new questions by their text would be
a guess. They stay on the card's page, under "Earlier readings", as the history of what he decided,
and on the issue as his comment, and no brief carries them.

**Why a card that started is never read again.** From `building` on, work exists that was built
from the reading, and reading the card again could not undo it: a reading moves only a card that is
not under way, and a new answer would leave the built work out of step with it. A change to a
started card is "A card that changes" under "Cards": an edit by a trusted account replaces its
record and the card stays where its run put it; and the merge check holds the card to its newest
reading, condition 4, so a card edited since it was read never merges unattended. The owner then
sends it back, part 4's way, which builds it again on a new branch from the card as it is. Started
is read from the runs, not the state: a card with any run but a reading has started, so a card
queued again after a build, run again after a stop or sent back, is built, never read.

**Why a feature's cards are read together.** The spec's round 2 reads all of a feature's cards at
once, not each as its turn comes, and this keeps it: the owner gets every question of the feature in
one batch, a refusal comes before the first card is built rather than halfway through, and only
cards read side by side show that they contradict each other, which the reading asks as a question
on each card involved. The note of the code the feature touches is written once, with the whole
feature in view, and goes into every brief of it, the spec's round 4b: each agent still reads the
code it changes, but no longer has to search for it. A reading is one run for the feature, by its
parent's number, so the index that allows one run under way per feature holds one reading of a
feature at a time, and no card of the feature starts while it runs. A card on its own is read alone,
since it has nothing to be read with. A card added to a feature once a reading of it came back is
read alone when its turn comes, in the sub-issues' order, and so is a held card once it is released:
a held card is left out of its feature's reading, since it would not be handed out, and a reading of
it could move nothing while it is held, so a too thin answer would sit on a card that the owner's
release then queues. Reading it on release keeps the rule simple, every card's reading moves that
card. It is read alone rather than with the feature again for the reasons a changed card is: the
cards already read are not read twice, and the feature's note is not rewritten from one card. The
price is that a contradiction between it and a card read before can be missed: its reading is given
the feature, the note and what the done cards left, not the other cards' text. That is accepted,
since asking it on both cards, as a reading of the whole feature does, would mean moving cards that
may be under way. As a second line, a build takes only a card whose newest reading is ready or has questions,
so a card read as too thin is never built however it comes to be queued again.

**Why reading comes before every build.** The kinds of job are handed out work in progress first, and
a reading goes after conflicts and before builds: it is short and it frees work, since a build can
only take a card that was read, and a project with one worker would otherwise build the cards it
already read while the rest wait unread, and their questions with them. Builds wait for nothing but
the reading of their own card; the rest of the order stands as above. A failure is judged as any
run's is, "Stops": outside the cards, the reading is put back and the project waits; in them, or a
reading out of its form, it is run once more, and the second in a row stops every card it read with
why, counted per card as a build's failures are, since a reader that cannot answer twice will not
answer the third time either.

**Why a reading only fetches.** It reads the code and decides nothing in it, so it is given what a
review is given and no more: a key that only fetches, a copy it can read, search and list and not
write, and no check to run. It cannot push, so nothing a reading does reaches the repository, and
its note and questions, an agent's words, reach the other briefs only fenced as information. Its
answer is a form, `Reading`, whose schema is written from its type, as the report's and the
verdict's are, and read with a field missing, unknown or mistyped refused, and with every card it
was handed answered exactly once, so a reading that skips a card or names another is a failure, not
an answer. The cards a reading was handed are recorded when it is handed out, with each card's body
SHA-256 and title as it was, which reading again and the merge check compare against; and the note
lives on the run's report, as a card's handover note does on its run's.

## How a card reaches its pull request

A card whose run built it, with a report in its form and a check that exited 0, gets a pull request
of its own, from its branch into the branch it was built on, the default branch or, for a card of
a feature, the feature's branch, as the spec's round 2 has every card, and the owner reads it as he
reads the runner's. The routes, the body and the page are
"The pull request Dev flow opens" and "A card's page" in `devflow/README.md`.

**Opened as the app, outside the reads.** Opening is one of the program's own writes, so it is made
with a `Post` key, see "The program's own writes" above, and traced back to the card and the run by
the body's last line, as the spec's round 4 asks of its trail on GitHub. It lives in
`PullRequests/`, a loop of its own on the reads' minute, rather than in the reading code, which can
never reach a write key. And it is not made in the worker's result route: the worker gives up on a
call after 30 seconds and GitHub may take 20 for each of the three requests opening needs, so the
result is recorded and answered, and the pull request opens within the minute.

**Opening twice opens one.** Nothing is held while GitHub answers, as for handing out work: the
opener reads what waits, asks GitHub, and records the pull request only while the card still waits
for one. Before it opens, it looks for the app's own open pull request from that branch into that
base and reuses it, so an opening whose answer was lost, or one at the same moment, never leaves a
second; GitHub refuses a second from the same branch anyway. One by anyone else is never taken for
the card's: a pull request from a fork can carry the same branch name.

**The body is the templates' layout**, `.claude/skills/pick-up-slice/templates.md`'s Summary with
its `Closes` line, which a card of a feature replaces with "Card #<n> of the feature #<parent>.",
since GitHub's closing keywords close an issue only from a pull request into the default branch and
the app closes the card itself when it lands, "A card lands in its feature", Tasks, Decisions the agent made, Verification and Follow ups, as the feature
keeps of this repository's runner, with Verification saying what Dev flow ran itself rather than
what the agent says, since the report decides nothing. Everything in it but the check and Dev
flow's own words is the agent's, and data, see the security entry below.

**The card follows its pull request** on each read of its project, in the read's transaction: a
pull request still in the list of open ones costs nothing, and only one that left it is read on
its own. Merged by anyone, on GitHub or from the page, is done; closed without merging is stopped.
A card whose pull request opened is reviewed first, see "Review", and is followed while it is under
review or being fixed too, so a pull request merged or closed meanwhile is not reviewed or fixed for
nothing. After a review that approves its head a card waits for the owner until the merge check
has read it, see "The merge check".

**The waiting list reads Dev flow's own cards beside the runner's.** The runner's items are read
from its pull requests on GitHub, by its labels and verdicts, see "Reading this repository's
runner"; Dev flow's cards are its own rows, so the list reads them from its tables, as it reads a
stopped project, and never asks GitHub. Both show under "Pull requests waiting" and count the same,
in the tab, the nav's dot and the front page. The owner merges a card as himself, with his linked
token, as he merges an item, naming the commit its run built and Dev flow's check passed on, so a
pull request someone pushed to since is refused; GitHub records it as his, and the watch on the
default branch finds a change by his login fine. A card of a stopped project is not merged, since
nothing a stopped project produced is merged until he resumes it. Sending one back is part 1's send
back, his comment posted as him on its pull request, which the next read takes as the one input Dev
flow reads from GitHub, see "Fixing".

**A card's page is its whole story**, round 7 of the spec, read from what Dev flow stored, so it
answers while GitHub is away. Since the redesign it tells it in order: what the agent did, from the
latest report, then how it got here, its runs, reviews and fix rounds as one timeline, oldest first,
so a review and the fix that answered it sit together rather than in three lists matched up by
commit. It stays read only, and where the card needs the owner its banner links to its item on
Waiting for you, the one place he acts. A failed run carries `stderr_tail` on the card's story
route, the last 50 of the 200 lines of stderr the run keeps: enough to see why Claude Code failed
without reading a whole log, which the page does not show. It is cut from what was stored scrubbed,
so it is never scrubbed again, and a run that succeeded carries none, since its stderr explains
nothing the owner acts on.

## Review

A reviewer with fresh context reads every card's pull request, round 2 of the spec, and is given
the report together with the real diff, round 4, so nothing merges because the developer said so.
How it is handed out, run, judged, kept and posted is "Review" in `devflow/README.md`.

**A review is a job like a build, handed out first.** It needs the same worker, the same Claude
login and the same care with keys and folders, so it is a second kind of job rather than a second
program, and `WorkerJob` holds what the two share. Reviews go before builds, and fixes will go
between, so work already in progress reaches the owner before new work starts and a project with
one worker does not pile up unreviewed pull requests. The kind is read before the key is made, and
only a card of that kind is then taken, so the key matches the job and no lock is held while
GitHub answers, as for a build.

**The reviewer is a fresh session, and the last job to weaken**, round 4b: its worth is that it has
not seen the developer's reasoning, so it starts empty, on the strongest model, and is told only
the card, the report, the diff and the check's output, all as files it reads. Whether the card had
tests written for it is its to judge, by whether the card's acceptance is covered, round 8, since
counting tests rewards the wrong thing. It judges and does not teach, after this repository's
reviewer, so a verdict is a list of findings and never a fix.

**The verdict lives in the database, tied to the commit.** The merge check of round 8 asks that the
reviewer approved exactly the commit being merged, so a verdict is only worth anything with the
commit it is for, and the commit has to be one Dev flow can trust: the one the worker checked out,
recorded on the run when it was handed out, never one the agent names, which is why the form has
no field for it. Keeping it in `reviews`, in the transaction that records its run's result, makes
it Dev flow's own record, read without GitHub, and a card's current verdict is the one for its head
commit alone, so a commit pushed after a review needs a new one. The comment on the pull request is
a copy for people and is never read back, so the record cannot be written by anyone who can
comment; this repository's runner reads its verdict from a comment and needs guards against an
agent approving its own work that Dev flow does not, since developer and reviewer share the app's
identity and only the program writes the record, round 4.

**A verdict out of its form is a failure, not a verdict.** A reviewer that hands back something
unreadable, or `changes` with nothing blocking, has not reviewed, so the run fails and stops the
card with why, rather than Dev flow guessing in favour of either word; the program never guesses in
favour of merging, round 8. A review that failed outside the card is simply handed out again.

## Fixing

The developer gets a limited number of rounds to fix what the reviewer finds, round 2 of the spec,
and the owner sends work back the same way, by commenting where the work is. How a fix is handed
out, run, judged and reviewed again, and how a comment is read, is "Fixing" in
`devflow/README.md`.

**The project's rounds, then a stop.** Each `changes` verdict starts a round, at most the project's
`fix_rounds_card`, 3 by default, round 3 of the spec, read when the verdict is judged so a change
counts at the next one; one more stops the card with "Fix rounds used up", and with 0 the first
does, so a card the developer and the reviewer
cannot agree on reaches the owner instead of costing runs forever. The owner's comment is not a
round: he decides when his own input is worth another run, so it never uses one up.

**A fix is a build that continues.** It needs the same worker, key and care as a build, so it is a
third kind of job that the build's code carries, on the card's existing branch, rather than a new
branch or a fresh start: the pull request, its conversation and the review history stay one story.
Fixes go after reviews and before builds, for the reason reviews go first. A fix starts only from
the commit Dev flow handed it, the newest one its runs left on the branch, which the worker checks
before anything runs, so nothing is built on a commit someone else pushed that no review or report
has seen; and a fix that leaves the branch where it was changed nothing, which is a failure in the
card rather than a round answered by doing nothing.

**The re-review judges the whole change again.** A fresh reviewer gets the earlier findings and the
developer's replies with the new diff from the base, as this repository's fix loop does, so a reply
that argues a finding is weighed and not taken on trust, and a fix that broke something elsewhere
is still seen. Only blocking findings start another round.

**The owner's comment is the one input read from GitHub**, on a card's pull request, and on a
stopped card's issue too, see "Stops". Dev flow learns everything else from its
own records, round 4 of the spec; a comment on the pull request is where the owner already talks to
the work, so it is read, but only his, by his linked login, and only as input to a fix, never as a
verdict or an approval. It is taken by GitHub's comment id, which only grows, so a comment is acted
on once, and only between runs, so one written while a run is under way waits for a read that
falls between runs rather than being lost or racing it; in a quick review and fix loop that is often
only once the card waits for him or stops. "Between runs" is checked after the card's row is locked,
the row every claim of a run locks, so a run claimed in the same moment is always seen. No time is compared, not even before his first comment: runs follow
each other seconds apart, so a comment older than the card's last run is usually one written while
an earlier run was under way, not one already answered. Sending a card back from "Waiting for you" posts his comment as him
and lets the next read take it, so the comment on GitHub is the one record and there is one way in.

## The worker's pictures

The owner should be able to judge a card's pages without starting the project himself, and so
should the reviewer, round 4 of the spec: "takes the screenshots of the pages the report names".
How the worker takes them, where they go and how they are served is "Screenshots" in
`devflow/README.md`.

**The worker takes the evidence, never the developer.** The developer agent could attach pictures
of its own, but they would be its claim, made from its own copy with whatever it left there. The worker
starts the commit it pushed, from a clean copy, as it runs the check, so the pictures are of what
merges and nothing else, and the program uses those, round 4. Only a run whose check passed and
whose branch was pushed gets them: a failed check stops the card anyway, and pictures of it would
spend minutes of a worker on evidence nobody reads.

**They never decide anything.** The program cannot judge a picture, round 8, so a run succeeds by
its check and a card merges by the merge check's conditions, whatever the pictures show or whether
they were taken at all. They are evidence for two judges who can look: the reviewer, whose brief
makes a page that looks broken, empty or wrong a blocking finding, and the owner. That is also why
a failure to take them is said in a note and never fails the job: a project that will not start
for pictures is worth knowing about, and the reviewer decides whether the card needed them.

**The developer looks too.** This goes beyond the spec, which had only the worker take pictures,
afterwards, and was decided with the owner on 2026-11-17: verification is what makes an agent good,
and a developer that never sees its pages hands back layouts that fall apart for the reviewer to
find a round later. So for a project with pages its brief says how to start the project with
`DEVFLOW=1`, take each page it changed at both sizes, look at them against the card and fix what
is wrong before it reports, and its report says in `looked` which it looked at. Its pictures are
its own check and go nowhere: they are made from its copy, so they are still its claim, and the
worker's after the push stay what the reviewer and the owner judge. They are taken in a folder
beside its copy, never in it, so nothing it makes while looking can be committed, and it goes with
the job. A page it did not look at is shown as such to the owner and the reviewer, who weighs it;
it stops nothing by itself, since the worker's pictures and the reviewer's judgement of them are
what would catch a broken page. The time spent looking comes out of the run's own limits rather
than a budget of its own, so a card that needs much looking is a card that needs much work. The
brief tells it to use `npx playwright`, which in a project without a Playwright of its own runs the
image's pinned one, and the image's `playwright` when the project's own cannot find its browser;
the worker itself never goes through `npx`, since its pictures must not depend on what a project
installs.

**A project's login is its own business**, as the feature settles. Dev flow could log in on a
project's behalf, but every project logs in differently, and Dev flow would then hold the
project's credentials. Instead `DEVFLOW=1` is the one thing it sets, and a project's `start` uses
it to come up already signed in, a test mode the project owns and keeps off without it.

**On `pr-screenshots`, beside the runner's.** The branch is where this repository's runner already
keeps its pictures, and where a pull request body's images on GitHub point, so the owner reads Dev
flow's pull requests as he reads the runner's. Dev flow's live under `devflow/`, so the two never
share a name, and under the commit, so a commit pushed later does not replace an older commit's
pictures. Root commits them with plumbing, on top of the branch as cloned, rather than in a
checked out copy, and pushes without force, fetching once more when another job moved it, so
pictures pushed at the same moment are never lost.

**Read as the app, served like part 1's.** The addresses a body embeds need a GitHub session, so
the page reads each picture through Dev flow, with the same checks as the runner's items, see "A
code host's bytes handed to the browser" below.

## The merge check

A card merges unattended only when all of round 8's conditions hold, read fresh at the time, and
the program never guesses in favour of merging. It is the successor of this repository's merge
gate, `scripts/agent/merge-gate.sh`, and like it is deterministic code that fails closed. The
order, the reasons and the tables are "The merge check" in `devflow/README.md`.

**One pure function decides, and its caller reads.** `MergeCheck.Decide` takes a record of what was
read and answers merge or a reason, with no database, network or clock inside it, so every
condition is one case of a test over plain values, and what decides a merge can be read in one
place. The code host's answers reach it as they came, failures included, so "anything that cannot
be read is no merge" is decided there too rather than by each read's caller. No language model
takes part anywhere in it: of the agent's report it reads only whether any build's or fix's marked
a decision for the owner, on `sometimes`, read in SQL from the report as stored, which can make a
card wait and never lets one merge; and a test reads the
compiled code the check runs and fails if it names an agent, a brief, a report, a worker or a job.

**The conditions, and why each fails closed.**

1. *The worker's check passed on the pull request's head.* The check's exit code is the worker's,
   run on the commit it pushed, never the agent's word. The head is read from GitHub at the time,
   so a commit pushed since by anyone is not the one checked, and is not merged.
2. *CI passed on that commit.* Every check run and status completed and succeeded; anything else
   that completed refuses with its name, `skipped` and `neutral` among them, since a check that did
   not pass is not known to be fine. Pending waits. CI that never reports does not count as passing:
   with nothing on the commit 30 minutes after the push, the card is the owner's, "CI did not
   report", since a project's CI that broke silently looks exactly like one with no CI.
3. *The latest review approved exactly that commit*, from Dev flow's own `reviews`, never from a
   comment, so a commit after the review needs a new one, see "Review".
4. *The card is still valid.* Written by a trusted account, open with the pick-up label a trusted
   account put on, not held, and edited by nobody outside the list since it was picked, read again
   from GitHub as the app rather than from the last minute's picture. Its labels are read from its
   timeline by `LabelActors.Judge`, the rule picking reads them by in "How a card is picked": a
   pick-up label someone untrusted put on, or a hold label a trusted account put on and only
   someone untrusted took off, is no merge, and a timeline that cannot be read waits like any read
   that failed. For a card of a feature the parent is held to all of it, and the card to all but
   the pick-up label, which it does not carry. Its body and title are compared with the record
   made when it was picked, and a change the history cannot attribute is a reason too. They are
   compared with what its newest reading was handed as well, since that is what it was built from,
   "not edited since it was read" in the spec's clarifications: a trusted account's edit since the
   pick is allowed, one since the reading is not. Before a card starts such an edit reads it again,
   so the check meets one only once it has started, where it is the owner's to judge, merging it as
   himself or sending it back. A card with no reading, built before reading came, is held to the
   pick's record alone.
5. *The project's setting lets it merge.* `never` does; `sometimes` does unless a condition the
   owner ticked holds, see "Sometimes" below; any other value, `always` and one it does not know
   included, waits.

And the project is not stopped and its watch has read within five minutes, as for handing out
work, since nothing a project produced merges while something unexplained may sit on its default
branch.

**Sometimes.** The spec's round 2 left "what 'sometimes' means" open, and #331 left it to this
part. A card's pull request waits for the owner on `sometimes` when any condition he ticked holds,
the first deciding, and merges as with `never` otherwise. Each condition is a reason a person would
want to look, and each errs towards looking:

- *Marked for his review.* The review label is read by `LabelActors.Judge` as a label that holds
  back, as the hold label is: anyone may put it on, since asking the owner to look can only stop a
  merge, and only a trusted account can take it off, since taking it off lets one through. On a
  feature's parent it marks every card of the feature, whose pull requests into the feature's branch
  then wait, and a card's own label inside a feature counts too.
- *A change touching a listed path.* The files are the pull request's own, as GitHub lists them,
  read whole or not at all, a rename by both its names, so moving a file out of a listed folder is
  seen as touching it. GitHub lists at most 3000; a pull request at that limit may change a file
  past it, so it cannot be judged and waits for the owner, "Too many files to check the paths",
  since the check fails closed. A read that failed is no merge either, and is tried again on the
  next minute like every other read that failed, since it may clear by itself and its reason should
  say what happened. The files are read only when the condition is
  ticked and a path is listed, so a project that lists none costs nothing.
- *A decision the agent marked for him.* Any build's or fix's report of the card counts, as "A
  decision for the owner" below counts them, through the one SQL rule `DecisionStore.MarkedFrom`,
  so what waits and what a merge records are the same decisions, and the waiting card shows them.
  Not ticked, the card merges and its decisions open their attention issues as with `never`.

**The paths are globs, not gitignore's full syntax.** They are read by
`Microsoft.Extensions.FileSystemGlobbing`'s `Matcher`, which is part of the ASP.NET Core shared
framework Dev flow already runs on, so the rule is a first party one with no dependency to keep:
`*` within a folder, `**` across folders, and a pattern ending in `/` everything under it, each from
the repository's top. There is no negation: in gitignore a later `!` line takes a file back out, so
whether a path waits would depend on the order of the lines and a single line could hollow out the
list, and a list whose only job is to make pull requests wait should only ever add to what waits.
`*.sql` is a file at the top, not one anywhere as gitignore reads it, which the Paths field's help says;
`**/*.sql` is one anywhere. Paths are matched whatever their case, the stricter of the two, so a
pattern never misses a file by its case. `PathPatterns` holds the matching and the rules the
settings route checks a pattern against.

**What is tried again, and what is the owner's.** A reason that may clear by itself, CI still
running or not reported yet, a read that failed, the files of a pull request on `sometimes` among
them, the watch not read lately, no key or no answer from the merge, keeps the card due, and it is
read again on the next minute. Every other is the owner's: a check, CI or review that failed, a
card no longer valid, `always`, a condition of `sometimes` that held, a stopped project. It
then waits on "Waiting for you" with the reason in words, as every card that waits does, and he
merges it as himself or sends it back; a stopped project's card stays his after he resumes, since
resuming accepts what landed on the default branch, not the card.

**When it runs.** Right after a review approves, through `MergeRequests`, so a card does not wait a
minute for nothing, but never inside the worker's result route, whose worker gives up after 30
seconds while a merge may take GitHub 20 for each request; and on each project's minute, beside the
program's other writes, outside the reading code, which can never reach a write key.

**The merge is made under the watch's lock and names the approved commit.** `AppMerges.MergeCard`
holds the project's row as the watch judges under it, reads again there what could have changed
since the check read, the hold, the setting, exactly as the check decided on it, `never` or
`sometimes` with the same conditions, review label and paths, so a change made since is decided
again on the next minute, and that the card still waits for this merge, and only
then merges with a `Merge` key and `sha` set to the approved commit, so GitHub refuses one whose
head moved. The merge commit, the card's `done` and its decisions for the owner are recorded in
that one transaction, so the watch finds the merge fine and none of it is lost. A merge commit
GitHub's answer did not name is not recorded, and the watch then stops the project: that path errs
towards asking the owner.

**A decision for the owner, with `never`**, or with `sometimes` and its decision condition not
ticked. The card merges, and each decision the agent marked for
him gets one issue, opened as the app with the attention label, round 8's "a new issue with the
attention label is opened only for a decision the agent flagged on a card that has already
merged". Every build's and fix's report counts, not only the newest, since a fix need not repeat
what the build decided, and a decision for the owner that vanished would be one merged unchecked.
"Waiting for you" lists the open ones until he closes them on GitHub; whether a decision stands is
his to judge, and a fix is a new card. With `always`, or `sometimes` and the condition ticked,
nothing merges, so the decision waits on the card itself, which lists every report's.

**Not on the list, on purpose**, round 8: screenshots, which the reviewer judges; whether tests were
written, which the reviewer judges by the card's acceptance; and the agent's report, which never
decides. Branch protection is not relied on either, as for the watch.

### A card of a feature

A card of a feature lands in the feature's branch, not the default branch, through the same check,
"Where a finished card lands is a setting, and it follows the card" in the spec's round 2. Nothing
of a feature reaches the default branch before the owner merges the feature himself.

**The setting governs a card's merge into its feature's branch only.** With `never`, `sometimes`
or `always` alike, the owner merges a feature into the default branch himself, so the setting
decides only whether each card's pull request into the feature's branch waits for him; on
`sometimes`, the review label on the parent marks every card of the feature.

**The same function, with the feature's branch as the base.** `Decide` is not forked for features:
the facts gain the card's feature, and the base the card's run used stands wherever the default
branch did. The feature's branch not read for five minutes waits beside the default branch's, since
nothing merges into a branch whose changes are not being judged. A pull request is merged only into
the branch its card was built on, for a card on its own too: a base changed on GitHub since would
land the card where nobody built or reviewed it to land, a feature's card in the default branch
around the owner's own merge of the feature, so that is the owner's.

**The card rules, read through the parent.** A feature's cards carry no label of their own, so "open
with the pick-up label" is the parent's: the parent is open with the label, written by a trusted
account, not held and edited by nobody off the list since it was picked, as picking checks it, and
the card is an open sub-issue of it in this repository, written by a trusted account, not held and
edited by nobody off the list.
The parent is held to the rules because its body is in every brief of the feature, so a stranger's
edit there reaches the agent as surely as one on the card.

**Its issue is closed by the app, not by a closing keyword.** GitHub closes an issue from a closing
keyword only when the pull request merges into the default branch, so a card landing in its
feature's branch would stay open, and the parent, which counts its closed sub-issues, would show no
progress. `LandedCards` closes it as the app once the card is `done`, whoever merged it, after one
comment saying where it landed and that it reaches the default branch with its feature. It runs
after the merge and on the minute, never in the reading code, so a merge by the owner from the page
or on GitHub is told too. It reads the pull request first and tells only one merged into the
feature's branch, since the comment would not be true of one merged elsewhere. It looks for its own
comment before posting, as the stop's mark does, so an answer that was lost never posts it twice,
and it records the pull request it told, not a flag, so a card sent back and landing again is told
again without anything clearing it.

**Its decisions open no attention issue.** The attention issues exist so a decision on something
already in the default branch is not merged unseen. A feature's card's decisions are not in the
default branch until the owner merges the feature, and the feature's page shows them with an
answer, so they stay in the card's reports, whatever the setting. A card on its own keeps
its attention issues.

### A feature's page

A feature has a page of its own, as a card has, laid out as the mockup's feature page of the spec's
round 7, and since the redesign as `docs/redesign/feature-page.png`: merging and what the agents
want the owner to know first, then its cards with their state, every card's screenshots, and its
branch to try beside them. "Trying a feature is its branch and its screenshots for now": the page
gives the command that checks the branch out and the pictures the worker took, and runs nothing. A
card's fix rounds and tokens are on its own page; tokens are shown, not cost, as the spec's round 7
says.

**Read from what Dev flow stored, as every page is.** The story is the feature's row, its cards in
order, their runs, reviews and answers, read in one transaction as the user, never from the code
host, so a page that reads often costs GitHub nothing and another user's feature is not there. The
commit the branch is at is the one the watch last judged, `branch_watches.last_commit`, or the one
it was made from before any change: the commit Dev flow has seen, the owner's own pushes among them,
rather than one guessed from Dev flow's own merges. Its pictures are each card's, read through the
card's own route, so they pass the same checks as everywhere, see "A code host's bytes handed to
the browser".

**The decisions are each card's latest report's.** What the page shows is what the card's page shows
as its latest report, the newest build's or fix's that reads, so the two pages never disagree; a fix
whose report decides again is asked about again. Each decision carries the reviewer's view from the
newest review of the commit that report's run left, matched by what was chosen as a merge's decisions
are. The texts are the agent's and the reviewer's, shown as text through `format.tsx`, see "Text
read from a code host is data".

**An answer stays in Dev flow and never reaches the code host.** "Fine as it is" is kept in
`decision_answers`, per card, run and decision, and nothing is written to GitHub: nothing of a
feature is in the default branch before the owner merges it, so there is no one on GitHub to tell,
and an issue or a comment would be noise the owner then has to close, the reason a feature's
decisions open no attention issue. Keeping it in Dev flow also keeps it out of what agents read: a
comment on the card would be text a later brief could quote. An answer is kept as first given, and
only a decision the page can show is answered, marked for the owner in the card's latest report; one
from an older report is refused as `409`, so a page read before a newer run is read again rather than
answering what the card no longer says. "Send back", the other answer, is below, and the
confirmation of "Merging a feature into main" counts the decisions not answered.

#### Sending a card back

A card that has landed in its feature's branch is sent back from the feature's page with a comment,
one action, and the program opens it again and queues it, as the spec's round 2 has it in "Work is
sent back in three ways, which are one mechanism"; reopening it on GitHub with a comment does the
same, round 4's "The page can do everything". The routes, the read and the columns are "Send back"
under "A feature's page" in `devflow/README.md`.

**The comment is the send back, and the only input read.** The page posts the owner's comment on
the card's issue as him, and the next read takes it, as every comment of his that starts work is
taken, see "Fixing": the page never moves the card itself, so a comment written on GitHub and one
posted from the page are one path, and the comment on GitHub is the record of why the card came
back. An issue opened again, by him or by anyone, and anyone else's comment, queue nothing: opening
says nothing about what to change, and a stranger's words are never an input. The page says why
instead, so a reopened card that sits still is not a mystery. A decision sent back is the same
comment with the decision quoted above his words, so the agent reads what he is answering; the
answer `sent_back` is kept only once the comment is posted, so a refused comment leaves the decision
open.

**Only a landed card of an open feature.** Before it lands, a card is sent back on its own pull
request, as "Fixing" has it; once its feature is merged or closed, there is no feature branch to
build on. One SQL expression decides it, so the page's button, the routes' refusals and the read
cannot disagree about which card it is.

**Its issue is opened again by the app, on the minute.** The app closed it when the card landed, so
the app opens it again: the parent then shows the card as not done, and the merge check, which lands
only an open sub-issue of its feature, can land it again. The read cannot do it, since the reading
code never holds a write key, so `LandedCards`, which closes it, opens it too, once per send back,
and the read keeps the card meanwhile even while its issue is still closed.

**A new branch, never the one that merged.** A sent back card is built on
`devflow/<number>-<slug>-<k>`, from the feature's branch at its head, rather than on its old branch. The old
branch merged: building on it again would start from a commit behind the feature's branch, missing
every later card, and the new pull request would carry the old commits again, or need a rebase or a
force push, which a worker never does. A branch from the feature's head starts from everything the
feature holds now, the card's own earlier work among it, so the agent changes what the owner asks
on top of it and the pull request shows only that change. Counting the send backs into the name
keeps every branch name used once, so GitHub never reuses a merged branch's pull request and a
lost push can never land on a branch that already merged.

**The brief says what was built.** The agent is told the card landed in pull request #<n>, gets the
report it landed with, whose handover note is for it, and the owner's comment fenced as his input,
as the fix brief carries them. Its rounds and failures start afresh, since this is new work on the
card, and the feature goes back to building, its pull request open, so its end, its review as a
whole and its readiness run again once the card lands again.

## The end of a feature

When every card of a feature is done, the default branch is brought into the feature's branch and
the feature's pull request to the default branch is opened, as the spec's "Clarified after the
builder check" has it for "A feature's branch", and as the feature issue, "Dev flow: features",
decides for "The end of a feature". The feature is then reviewed as a whole, see "Reviewing a
feature" below, and he merges it on GitHub until "Merging a feature into main" lands. The routes, states and
tables are "The end of a feature" in `devflow/README.md`.

**Decided from what Dev flow stored.** Whether a feature's end is due is one pure function,
`FeatureEnd.Next`, over the feature's state, its cards' states, the default branch's commit last
brought in and the one bringing in last failed at, and the default branch's commit as its watch last
judged it. The read runs it, as the slice has it, and the end's minute runs it again before acting,
so the end follows the last card's merge within the same minute rather than on the next read; both
read the same stored state, so they never disagree. The default branch's commit is the watch's,
`branch_watches.last_commit`, not a read of GitHub of its own: it costs nothing more per read, and
the end never sets out to bring in a commit the watch has not seen and judged.

**A merge commit, never a rebase.** Every card's pull request, its review and the commit the merge
check approved name commits on the feature's branch. A rebase would rewrite them, needing a force
push, which the watch stops on by anyone, and would leave each review naming a commit no longer on
the branch. A merge commit keeps every reviewed commit as it was and adds one, which the watch tells
as Dev flow's by its record. GitHub's `POST /repos/{owner}/{repo}/merges` only ever makes a merge
commit and moves the base forward, so the code here cannot rebase or force a branch even by mistake;
it is asked with a `Merge` key, whose `contents: write` it needs, the same purpose as merging a card.

**Recorded before the watch can see it.** As a merge of a card's pull request is, see "Watching the
default branch": the project's lock is taken, the holds read under it, the code host asked while it
is held, and the merge commit written to `merges_made` with the feature's branch before it is let
go, so the watch, which takes the same lock before it judges, finds it recorded. It merges no pull
request, so its `number` is null and GitHub lists it as a push, not a pull request's merge: on a
feature's branch, a push that left it at a commit in `merges_made` for that branch is fine too.
The default branch is never brought in anywhere else, so the default branch's own rule is
unchanged. A merge GitHub made but whose commit its answer did not name is not recorded, and the
watch stops the project on it, erring towards asking the owner.

**Which commit was brought in.** GitHub is asked with the default branch's name, as the slice has
it, and merges the branch as it is at that moment, which may be newer than the commit the watch
last judged. The answer's second parent says which commit it merged, and that is recorded as
`brought_in_commit`; nothing to bring in records the watch's commit, which the branch then holds. A
default branch that moved in the gap is brought in again on a later read, and answers nothing to
bring in: one request more, never one missed.

**A conflict goes to an agent**, see "A conflict" below. A refusal waits for the owner, with the
default branch's commit it met remembered, so it is not asked again every minute while nothing has
changed, since asking again unchanged would be refused again, and is asked again once the default
branch moves.

**The pull request is opened at the end, not as a draft from the first card.** The spec opens a
draft pull request for the feature from its first card. Draft pull requests on a private repository
need GitHub Team, and Fodr's repository is private, so the feature issue decided otherwise: it is
opened, not a draft, once the default branch is brought in, and the feature's progress before then
shows on the parent issue, which counts its closed sub-issues, and on the feature's page. This is a
difference from the spec.

**Its body is the feature's, and its titles are data.** It names the feature, lists each card with
the pull request it landed in, and closes the parent on a line of its own, which GitHub honours once
the pull request merges into the default branch, so an open parent means a feature not yet there.
The titles are the issues', written as a card's report is in its body, so a title cannot add a
heading, hide what follows or add a second closing line. It is opened once: the app's own open one
from the branch is reused, as a card's is, and a body that no longer matches the feature's cards is
replaced, so a card added later is listed when the end runs again. A pull request recorded and no
longer open is not opened again here, since closing a feature's pull request is the owner's act,
which "Merging a feature into main" and "Sending a card back" answer.

**Why the default branch is kept in a feature that waits.** The owner merges a feature when he has
looked at it, which may be days later. A feature's branch behind the default branch shows him a
diff that is not only the feature's, runs CI on a base that is gone, and lets conflicts grow until
the merge. Bringing each change in while the feature waits keeps his merge one step and what he
reviewed close to what lands. It is not done while the feature is worked on, resolving, reviewing or
fixing, since a job on the branch would then push onto a branch that moved under it; it is brought
in once the feature waits again.

**A card added sends it back to building.** A sub-issue added to a feature at its end is picked on
the next read, as every sub-issue is, and is one more card to build: the feature is `building` again,
its pull request stays open, since the owner's view of it stays useful, and the end runs again once
the card is done.

### A conflict

When the default branch conflicts with a feature's branch, bringing it in is a job for an agent,
checked and reviewed like a card, as the spec's "Clarified after the builder check" has it and the
feature issue decides for "The end of a feature". GitHub only says that it conflicts; an agent on one
of the project's workers makes the merge and resolves it, the worker checks it and pushes it to the
feature's branch, and what the agent chose is kept for the review of the whole feature. The job, its
brief and its routes are "A conflict" under "The end of a feature" in `devflow/README.md`, and the
worker's side "The worker" there.

**A run for a feature, not a card.** A run names a card or a feature, exactly one, by a check
constraint, so the reviews and fixes of a whole feature that follow use the same, and a feature's
runs are counted, measured and shown on its page as a card's are on its page. Every query that reads
a card's runs names its card, so a feature's run is never taken for one; the one thing that listed
runs without a card, the sweep for lost runs, leaves a feature's where it was.

**From the commits Dev flow judged.** The job starts from the feature branch's commit as its watch
last named it, and brings in the default branch's as its watch last named it, the same commit the
end set out to bring in. It is handed out only once the feature's watch has read the branch since
the feature began resolving: the card whose landing ended the feature was merged there moments
before, and a job handed out before the watch saw that merge would start from a commit the branch
has left. The watch can still lag behind GitHub, so a worker that finds the branch elsewhere fails
outside the job and the job is handed out again later; a push by anyone else on the branch stops
the project anyway.

**The files in conflict are where the conflicts are.** Dev flow has no git, and GitHub's merge
answers a conflict without naming a file, so the brief lists the files each side changed since the
two parted, from the code host's comparison of each commit with the other, read as the app before
the feature is taken; the feature is then taken only at those two commits, so the list is never
handed out with others. A file both sides changed may well merge cleanly, and the agent's
`git merge` names exactly which conflict. Reading the worker's own trial merge instead would have
put the brief, which is Dev flow's, together on the worker, and listing nothing would have left the
brief without what the slice asks it to carry.

**Exactly one merge commit, of known parents.** The worker takes the agent's branch only when it is
one new commit on the feature's head, a merge whose first parent is that head and whose second is
the default branch's commit Dev flow handed out. That is what bringing the default branch in is,
and nothing more: a second commit would be work no card asked for, landing on a branch every later
job of the feature builds on and no review of a card saw; a commit that is not a merge would rewrite
the feature's history in one squash, so its reviewed commits would no longer be what the branch
holds; and a merge of another commit could bring in anything, a branch of the agent's own among
them. The check's fixes go in the merge commit itself, so the rule never refuses a resolution that
needed one more change. What the agent leaves uncommitted is not committed for it, as a build's is,
since that would be the second commit.

**Pushed only once its check passed.** A build's branch is pushed whatever its check said, so its
work is never lost; a card's branch is its own. The feature's branch is shared: every later card and
the feature's pull request build on it, and a merge pushed there with its check failing would make
the next clean bringing in look like a finished end, the broken resolution under it. So a conflict
whose check failed pushes nothing, and the feature waits for the owner with why, at the commit it
failed at.

**No rounds for a conflict.** A failure in the job, or a check that failed, leaves the feature
waiting for the owner, the conflict's default branch commit remembered as `bring_in_failed_at`, so
it is not tried again until the default branch moves, when it may well merge cleanly or conflict
differently. Trying the same conflict again would spend usage on the answer the agent already gave;
the owner, who reviews the whole feature anyway, decides. A failure outside the job is tried again
as a card's is, the project waiting, since the job did nothing wrong.

**The resolution is kept apart for the review.** `git show --remerge-diff` of the merge commit shows
what the agent's merge holds against what git's own merge would have left, conflict markers and all,
and nothing for the files that merged cleanly. A review of the whole feature reads its diff from the
merge base, in which a resolution is invisible: the merge's lines read as the feature's own or the
default branch's. Kept on the run, it lets the review of the whole feature, see "Reviewing a feature", judge the one place where an
agent wrote code no card asked for, apart from the rest. It is text from a worker, cut beyond 1 MiB,
and data wherever it is shown or handed on.

### Reviewing a feature

Once a feature's pull request is open, a fresh reviewer reads the whole feature at its head, and the
feature is the owner's when that review approves its head and CI passed there, as the feature issue,
"Dev flow: features", decides for "The review of the whole feature" and "A feature is ready for
me". **The spec has no agent review of a whole feature; this adds it**: each card was reviewed on its
own against its own acceptance, and what no review of one card can see, cards that contradict each
other or solve one thing twice, code duplicated between them, the feature against what its parent
asked, and what an agent chose in a conflict, is what this review is for. The job, its brief, its
tables and its routes are "Reviewing a feature" in `devflow/README.md`.

**A job for the feature, like a card's review.** It needs the same worker, the same read only agent
and the same care with keys as a card's review, so it is a fifth kind of run, for the feature as a
conflict's is, and the worker's `ReviewJob` runs both, with a key that only fetches. It is handed
out with the reviews of cards, first, so a finished feature reaches the owner before new work
starts, and on the same model, since the reviewer is the last job to weaken.

**The diff is from the merge base.** By the time a feature is reviewed, the default branch has been
brought into its branch, so `git diff <default> <head>` would show what the default branch has
that the feature lacks as removed, and the feature's branch's whole history would show every change
of the default branch merged in. `git diff <default>...<head>`, from where the two last met, which
after a bringing in is the default branch's commit brought in, is the feature's own changes and
nothing else, as the pull request shows them on GitHub. The worker computes it itself, from the
repository it fetched, rather than Dev flow asking GitHub's comparison, since that is capped and
would put the whole diff through Dev flow for nothing.

**A conflict's resolution is given apart.** In that diff a resolution is invisible: its lines read
as the feature's own or the default branch's. The remerge diff each conflict run kept, see "A
conflict", shows the one place an agent wrote code no card asked for, so the review is handed each
one resolved since the feature's last review, which judged the earlier ones already.

**Kept by Dev flow and tied to the commit**, as a card's verdict is, see "Review": in
`feature_reviews`, in the transaction that records its run's result, for the commit the worker was
handed and checked out, never one the agent names. The comment on the feature's pull request is a
copy for people and is never read back, so no comment by anyone can approve a feature.

**Why a clean bringing in needs no new review, and a resolved conflict does.** A merge the code
host made bringing the default branch in, without a conflict, adds no line anyone wrote: every
change in it is the default branch's, which was reviewed where it landed, and the feature's own
changes, the diff above, are the same before and after. Reviewing it again would spend a review on
nothing new, every time the default branch moves while the feature waits. So a review carries over
from the commit it approved to a head that differs from it only by such merges, which Dev flow
recorded itself in `merges_made` with the commit each merged into, and nothing else: a conflict an
agent resolved is a commit of its own, an agent's choices in it, so it needs a new review, as does a
push by anyone to the branch. CI runs on the new head either way, since a merge that changed nothing
by hand can still break the build. The walk follows only Dev flow's own records, so a merge it did
not make, or whose first parent the code host did not name, carries nothing over, erring towards a
review.

**Decided from what Dev flow stored, each minute.** Whether a feature needs a review and whether it
is ready is one pure function, `FeatureReady.Decide`, over its head as its branch's watch last named
it, its latest review, the merges Dev flow made on its branch and CI on the head, so what decides a
feature's readiness can be read in one place, as the merge check's can. The head is used only once
the watch has read the branch since Dev flow's own last merge or push there, so a review is never
asked for a commit the watch has not seen and judged, and a bringing in that just happened is not
missed. CI is read as "The merge check" reads it, by the same `MergeCheck.Ci`: pending waits,
nothing for 30 minutes is "CI did not report", any other conclusion is "CI failed". It is read
again each minute while a feature is ready, so a ready feature whose CI later fails, or whose
branch moves, stops being ready on the next minute; and for the head of a feature that waits for the
owner whatever its review, until CI has passed there, since he may merge it, see "Merging a
feature".

**A failure in the job waits for the owner, at that commit.** A review that failed in its own work,
its time limit or a verdict out of its form, is not handed out again for the same commit, as a
conflict is not: the owner reviews a feature anyway, and the same commit would most likely fail the
same way and spend the usage again. Once the branch moves, its new head is reviewed afresh. A
failure outside the job is tried again as a card's is.

**Changes start a fix round**, see "Fixing a feature" below.

### Fixing a feature

A review of the whole feature that asks for changes sends it to a fresh developer agent on the
feature's branch, for at most the project's `fix_rounds_feature`, 3 by default, each followed by a
new review, and the owner sends a
feature back by commenting on its pull request, as the feature issue, "Dev flow: features", decides
for "The review of the whole feature". With the rounds used up the feature waits for him with the
findings. The job, its brief, its tables and its routes are "Fixing a feature" under "Reviewing a
feature" in `devflow/README.md`.

**Its rounds end with the owner, not a stop.** A card whose developer and reviewer cannot agree
stops after its rounds, see "Fixing", because a stop is how a card reaches him. A feature reaches
him anyway: he reviews every feature before he merges it, its pull request is open, and its page
already shows the review's findings beside each round. Stopping it would add nothing he does not
see, and would hold back what does not need him: the default branch is still brought in while it
waits, and his comment still starts a fix. So "Review rounds used up" is a reason a feature waits
for him, as is a fix that failed twice in a row, and never a stop. A fix that failed in the job
runs once more from the same commit, as a card's fix does, since a developer's failure is often a
bad run rather than a bad commit; the second in a row waits for him at that commit, since a third
would most likely fail the same way and spend the usage again. A time limit or a report that
stopped waits for him at once.

**A fix of a card, on the feature's branch.** A feature's fix needs what a card's fix needs, the
same worker, key and brief with what to fix, so it is the same job carried by the same code, a
sixth kind of run for the feature, on the feature's branch from its head instead of a card's
branch: the branch, its pull request and its reviews stay one story, as a card's do. It starts from
the commit the feature's watch last named after Dev flow's own last change there, as a conflict
does, so a worker that finds the branch elsewhere, the watch having lagged, fails outside the job
and runs again later; a push by anyone but the owner stops the project anyway.

**Pushed only once its check passed.** A card's fix is pushed whatever its check said, so its work
is never lost: a card's branch is its own, and the merge check stands between it and anything
shared. A feature's branch is shared, and it is the head of the feature's pull request to the
default branch, which GitHub's own button merges. A fix whose check failed pushed there would be one
click from the default branch, and the next round would build on it. So, as a conflict's merge is,
a feature's fix is pushed only when its agent succeeded, its report reads as `done` and its check
passed, and one that failed leaves the branch where it was handed it, a failure in the job. The
worker reads the report with the same `Report.TryRead` Dev flow judges the run by, so an agent that
committed part of the work and reported it stopped, or wrote a report out of its form, pushes
nothing a passing check would otherwise carry; the check is not run for it. This is a difference
from a card's fix, which the slice describes as carried the same way.

**The re-review judges the whole again**, as a card's does: a fresh reviewer gets the earlier
findings with the developer's replies, and the whole diff from the merge base, not only what the fix
changed, so a reply that argues a finding is weighed rather than taken on trust, and a fix that
broke something elsewhere in the feature is still seen.

**His comment, by id and not by time.** A comment by his linked login on the feature's pull request
is the one comment read there as an input, as on a card's, and for the card's reasons, see
"Fixing": it is taken by GitHub's comment id past the newest of his already taken, only between the
feature's runs, and never by comparing its time with a run's. The slice says a comment newer than
the feature's last run starts a round; read as a time, that would drop a comment written in the
seconds between one read and a run claimed before the next, since the comment is then older than
the run that did not see it. Taken by id, every comment of his is answered exactly once, which is
what "newer than the last run" is for. It does not count against the rounds: he decides when
his own input is worth another run. "Send the whole feature back" posts his comment as him and lets
the next read take it, so the comment on GitHub is the one record and there is one way in.

### Merging a feature

The owner merges a feature into the default branch himself, as the spec's round 2 has it in every
setting, from its page or from its item under "Features ready for your review", and GitHub's own
button works too. It is the one way part 4 reaches the default branch. The routes, the refusals,
the dialog and what follows are "Merging a feature" and "Acting from the page" in
`devflow/README.md`.

**As him, as part 1's merge is.** The merge is `ICodeHost.Merge` with his token from
`UserTokens.For`, a merge commit, so GitHub records it as his and the watch on the default branch
finds it fine as a change by his login, without anything recorded for it; and nothing the program
holds as the app can make it, see the security entry "A feature reaches the default branch only by
the owner's own merge".

**Offered without the review's approval, never without CI.** The feature issue decides it: "It is
offered only when CI has passed on its head, and it warns me when the review has not approved it."
The review of the whole feature is advice to the owner, who reviews the feature himself anyway: a
review that asked for changes three times, or one that could not be made, leaves the feature
waiting for him, and he may judge the findings wrong or not worth another round, so the confirmation
says the review's state, lists its blocking findings, or says it was not reviewed as a whole, and
lets him merge. CI is different: a failing or missing check is not a judgement to weigh but a fact
that the default branch would break, and testing is not something he clicks past. So CI on the head
is a condition, and the review's verdict a warning.

**Decided from what Dev flow stored, so it refuses before GitHub is asked.** Whether a merge is
offered is read from the feature's state, its pull request, the head its branch's watch named, the
head the minute last read CI as passed on, `features.ci_passed_commit`, and whether the project is
stopped. The page, the waiting list and the route read the same rule, `FeatureMergeOffers`, so a
button is never shown that the route refuses, and the route refuses a head CI has not passed on, a
stopped project or a feature not his turn without a request to GitHub. The minute reads CI for a
head that waits for him whatever its review, and only until it has passed there, so a feature
waiting for days costs no reads once it is mergeable; CI that could not be read is no pass. As for
part 1's merge, the snapshot is never trusted for the act itself: the merge names the head the page
showed as `sha`, which GitHub refuses once the branch moved on, and the project is read again either
way. A stale page can cost a click, never a wrong merge. A project that is stopped merges nothing,
since something on one of its branches is not what Dev flow put there.

**Merging confirms what he did not answer.** A decision a feature's card marked for him that he left
unanswered on the feature's page is kept as `merged`, confirmed by merging, as merging a pull request
on GitHub confirms the decisions in it. It is written by the read that finds the feature merged,
whoever merged it, so GitHub's button confirms them too, and the confirmation counts them first.

**The read follows the feature's pull request as it follows a card's.** One that left the open pull
requests is read on its own: merged, the feature is `merged`; closed without merging, `closed`.
Either way its branch is no longer watched, since nothing more of the feature lands there, and the
features table keeps it, as a merged card is kept, as the story of what was built.

**After a merge, the cards are closed by the app.** The parent closes by its pull request's
`Closes` line, which is why the feature issue says an open parent means the feature is not in the
default branch yet. Each card was closed by the app as it landed in the feature's branch, but one
may have been opened again since, so on the next minute Dev flow reads the parent's sub-issues and
closes, as the app with a comment, any still open. It reads them from GitHub rather than from its
own cards, since a read that no longer finds the parent stops keeping cards that are not done.

**A pull request closed without merging gives the cards back.** What was built for them never
reached the default branch, so each card Dev flow kept for the feature is opened again by the app,
with a comment saying why, and the feature is `closed`. It stays closed while its label stays on,
so closing the pull request is never undone by Dev flow. Labelled again later, after the label was
taken off, it starts afresh: a closed feature not found by a read gives up the cards it kept, and is
then removed as any feature no card refers to is, so the next read with the label picks a new
feature, queued, with its cards queued. Its old runs and reviews stay, by the parent's number. Its
old branch is not deleted by Dev flow, so a new branch of the same name answers that it exists and
the feature waits for the owner, as any feature whose branch name is taken does, until he deletes
it.

**Deleting the branch is left to GitHub.** After a merge, the feature's branch is GitHub's to delete,
by the repository's own setting; Dev flow never deletes a branch.

## Stops

A card stops only when the program cannot produce a working result, and a problem outside the
card pauses and retries without reaching the owner, the spec's round 2; a run and a card have
stops on by default, round 3; and anti goal 4, "It burns usage silently". What stops a card, how
it is marked and run again, and how a project waits are "Stops" in `devflow/README.md`.

**What lies in the card, and what outside it.** In the card is what the card's own work caused:
the agent ran out of turns, budget or time, was refused an action, handed back no report or one out
of its form, stopped, or left a check that failed or never ran; a fix that changed nothing; a
reviewer's verdict out of its form. Outside it is everything another try may well not repeat: the
login, Claude's API and its usage limit, the network, git and the code host, the machine, and a run
that never came back. The worker says which, "The worker" in `devflow/README.md` holding the table,
since only it sees the failure; Dev flow judges what follows.

**A failure outside never counts against the card.** Counting it would stop good cards for a bad
machine, and a broken machine would then stop every card in turn, each needing the owner's comment
once it is mended; leaving the cards be means mending the machine is enough. The price is that a
machine that never mends costs a run every hour, which is why the project, not the card, waits: 5
minutes after the first failure, doubling to an hour, so a short outage passes in minutes and a
long one costs a request an hour; and why six in a row say so on the project's page and on "Waiting
for you", this repository's runner's two failures about a slice, raised for a cause that is not the
card's, so a broken machine is not silent. At the usage limit the project waits until it resets,
round 4b's default, which is shorter than the doubling's guess when the limit is near its reset and
spends nothing on tries that cannot succeed. A lost run is outside the card too: whether its worker
or its machine went, the card did nothing.

**One failure in the card is tried again; the second with nothing new stops it.** A first failure
in the card is often a flake, a test that timed out or a run that lost its way, which a fresh run
gets past, as this repository's runner gives a slice two failures before it stops it. A second with
the branch where the first left it is the same answer twice, the spec's "a card that comes back
twice with nothing new", so another run would spend usage on it again. A new commit between them is
progress, and runs the card once more; how long that may go on is bounded by the fix rounds once a
pull request exists, and before that only by the daily budget of part 8. A report that stopped and
a run at its time limit stop at once: the agent said the card cannot be built, or it spent the
longest a run may take, and a second try would spend as much again on the same card.

**Marked on the card itself, not in an issue of its own.** The spec's builder check settled that a
stopped card is marked with the attention label and a comment, where the owner already reads it, so
a stop needs no second thing to find and close; an attention issue is for a decision on a card that
has merged, which has no card left to mark. The comment is posted once, found again by its last
line as the review's copy is, and it decides nothing, so the label and the comment are for people,
and Dev flow's own record is the card's `stopped` row.

**His comment newer than the stop runs it again, on the issue or the pull request.** The issue is
where the stop's comment asks for one, and it is there whatever became of the pull request, so a
card stopped before it had one, or whose pull request was closed, can run again too. Here, unlike a
fix's comments, time is compared: a stop is the moment the owner learns of it, so a comment from
before it was either taken already or written while the run that stopped it was under way, and was
not his answer to the stop. His comment is the input to the next run, and the count starts afresh,
since he has changed what the card is asked. Only his linked login counts, as everywhere a comment
is read.

**A stop by the owner is no failure, and is not marked on the issue.** The owner stopping a run says
nothing about the card: the run was going the wrong way by his judgement, not failing by the
card's own work or by the machine. Counting it in the card would stop a good card at his second
stop, and counting it outside would make the project wait for nothing. So it is a result of its
own, `stopped_by_owner`, beside `stopped`, which is the agent's own word that it stopped, and it
touches no count, no fix round and no wait. A pushing job keeps what it did on its branch, since
the owner may want to run it again from there, and the commit it names is Dev flow's own push as
any result's is, so the watch accepts it. The card is not marked on its issue: the comment and the
attention label are how Dev flow tells the owner something he does not know, and he is the one who
stopped it. A feature's own run is held rather than stopped, since a feature never stops, and
everything of the feature waits with it, its cards' builds included, so nothing builds on a branch
the owner just stopped work on. A reading, a setup or a check has nothing to resume from, so it is
put back as a lost one is; keeping one from running is pausing the project.

**"Stop now" on a project pauses it first, then stops its runs, in one transaction.** Stopping
every run without pausing would hand the next job to the first worker that asks a moment later,
and he asked for the project to stop. The order matters too: writing the pause takes the project's
row, which the dispatcher takes `FOR SHARE` before it takes any work, so a job being handed out at
that moment either waits and then sees the pause, or committed first and is a run under way that
the second statement marks. Each run is marked as stopping one marks it, so each lands as one he
stopped, "Stopped by you", with nothing counted; it stops nothing of another project.

**The usage limit's reset is read from the text Claude Code prints.** Its JSON answer has no field
for it, and the exact `resetsAt` is in a streamed event the worker's format never prints, so the
worker reads the time from the message, as Claude Code 2.1.322's own formatter writes it, and Dev
flow believes it only when it is in the future and within 8 days, a week being Claude's longest
limit. A misreading then costs at most a wait that is too short, which the doubling follows, or a
week's wait the owner sees on the page; and a message the worker cannot read leaves the doubling,
which is never wrong for long. When Claude Code's version in the base image changes, a real run at
the limit is the check that the form still holds.

**The daily budget's hold is worked out, never stored.** Storing a "budget reached" mark would need
something to clear it at midnight, in each user's zone, and again whenever the budget is raised or
switched off, and a clearing that failed would hold a project with nothing to say why. Worked out
from the runs each time it is asked for, it lifts by itself at midnight and at once when the budget
changes, and the page, the rule and "Waiting for you" can never disagree, since they read the same
sum. The cost is one indexed range sum over the day's runs each time a worker asks, which
`runs_finished` keeps small. A run under way counts once it finishes, since only then are its
figures known; it is never stopped for the budget, so a day overshoots by what was already running.

## Watching the default branch

A worker's push key can reach `main` and merge through the API, see "The program's own writes"
above, and the spec decided not to lean on branch protection, since rulesets on a private
repository need a paid plan. What makes the key acceptable is that `main` is watched: anything
landing there that neither Dev flow nor the owner put there pauses the project and reaches him, as
the spec's round 4 put it. The watch detects; it does not prevent. The routes and tables are
"Watching branches" in `devflow/README.md`.

**What is watched** is the repository's default branch, read from GitHub on every read of the
project rather than assumed to be `main`. The watch is part of each project's minute, through the
same seam as every read, `ProjectRefresher`, and runs first, on its own, so it judges what landed
whether or not the rest of the project could be read, and one project's watch never stops
another's. GitHub lists a repository's activity, `GET /repos/{owner}/{repo}/activity`, as each push
or merge to a ref with its actor, its type and the commits before and after; it is read with the
read only token, as a conditional request, so a quiet minute costs nothing against the rate limit.

**Where it begins.** The first read of a project records the branch's newest change and judges
nothing before it, so connecting a repository does not stop it for its own history. A branch with
no change at all begins at nothing, and judges every change after.

**What is fine.** A change by the owner's linked GitHub login, compared whatever its case as GitHub
compares logins; and a merge whose merge commit Dev flow recorded when it merged as the app. Dev
flow's own merges go through `AppMerges`, which merges with `MergeAsApp` and writes the merge
commit GitHub answered to `merges_made` in the same transaction, holding a lock on the project's row
that the watch takes before it judges, so the watch can never see Dev flow's merge before its
record. Should the record fail after GitHub merged, or GitHub's answer not name the commit, the
watch finds a merge by the app it has no record of and stops the project: a failure on that path
errs towards asking the owner, never towards letting something through.

**What stops the project** is anything else: a push or a merge by the app that Dev flow did not
record, which is what a misused push key looks like; a force push by anyone, the owner included,
since it rewrites what was there; a change by any other login; and one whose actor GitHub no longer
names. The project records when it stopped and the newest change that stopped it, which is what
the owner looks at; while it is stopped the watch goes on, and a later change that would stop it
replaces what is shown, so he always looks at the newest.

**A stopped project** hands out no new job, `jobs/next` answers its workers `204`, and merges
nothing: `AppMerges` refuses before GitHub is asked. Handing out reads the hold before it asks
GitHub anything, and again in the transaction that takes the card, after taking the project's row
`FOR SHARE`, which waits for a watch writing a stop under its lock; so a stop recorded while
GitHub was being asked for the job still holds the card back. A card already with a worker finishes its run,
and its result is recorded, so stopping loses no work, but nothing it produced is merged until the
owner resumes the project. Stopping is not undone by anything the watch reads later; only the
owner's resume ends it.

**A watch that cannot read** holds new work rather than guessing: when the branch's changes have
not been read for five minutes, or never have, the project hands out no new work and its page says
why, but it is not marked stopped, since GitHub being away is not something that landed on `main`,
and it starts again once a read succeeds. A word in GitHub's answer the adapter does not know, such
as a new type of change, makes the read fail the same way, so a change the watch cannot judge
holds work rather than being let through.

**Resuming** is the owner's alone, from "Waiting for you", where a stopped project is one of Dev
flow's own items beside the runner's, counted in the same number. The page sends the commit it
showed, and a project stopped since by something newer is refused, so what landed after he looked
is never accepted unseen. Each resume is kept, with who resumed, when and which commit was
accepted, so a later reader sees it.

**When the default branch changes**, the watch begins again on the new one, recording where it
stands, as on a project's first read: only an admin of the repository can change it, and no key Dev
flow makes holds administration. The cost is accepted under "Security decisions and accepted risks"
below.

### A feature's branch

A push key reaches a feature's branch as it reaches `main`, and a feature's branch reaches `main`
with one confirmation from the owner, so it is watched as the default branch is, as the spec's
"Clarified after the builder check" has it: a commit there that Dev flow did not put there pauses
the project and reaches him. The tables and the order of a read are "Watching branches" in
`devflow/README.md`.

**One watch per branch, read on its own.** Each open feature's branch has a watch of its own,
beside the default branch's, read after it in the same minute, each with its own last read and its
own failure, so a branch GitHub does not answer for holds only what it watches. The project keeps
one stop, naming the branch that stopped it, since resuming is one decision of the owner's whatever
branch it was on.

**Where it begins.** At the branch's creation, which Dev flow made itself and recorded with the
commit it was made from, so the watch begins at the oldest creation GitHub lists at that commit and
judges nothing before it. A branch of the same name that existed earlier, someone else's and long
deleted, is history, as a repository's history is for the default branch. When no such creation is
listed, every change listed is judged, which errs towards asking the owner. The watch is begun in
the transaction that records the branch as made, so a feature with a branch is never without one,
and its first card is handed out at once rather than a minute later. It ends when its feature is
`merged` or `closed`: the branch is then the owner's to delete, and nothing more is built on it.

**What is fine** is what is fine on the default branch, with each merge fine only on the branch
Dev flow recorded it into, and two more: a push one of Dev flow's own jobs made there, recorded by
the commit its run's result named, and the default branch brought in by Dev flow at the feature's
end, which GitHub lists as a push and Dev flow records in `merges_made`, see "The end of a
feature". No card's job pushes to a feature's branch, its own branch is a
card's; the later jobs that do are resolving a conflict and fixing a feature.

**What stops the project** is what stops it on the default branch, and the branch deleted while its
feature is open, by anyone, the owner included: the feature's work is on it, and nothing Dev flow
does deletes it, so a deletion is never one of the expected changes. The stop names the branch, and
resuming is the same action, with the commit shown; for a deletion there is no commit, and the page
says the branch was deleted.

**Why a job's own push is judged when its run ends.** The worker pushes before it sends its result,
so the watch can read the push in the minute between, when Dev flow has no record of it yet. Judging
it then would stop the project for its own work; letting it through would let any push by the app
during a run through. So a push by the app read while a run of Dev flow's on that branch is under
way is kept, and judged when the run ends, in the transaction that records the result: fine when
the result named its commit, a stop when it did not, and a stop when the run is lost, since a lost
run has no result. Anything else is judged as soon as it is read, a push by another login during a
run among it, so the wait covers only what the run itself could have made, and only while it runs.
Taking the project's row before the run's, in recording a result as in the watch, keeps the two in
one order, so neither waits on the other.

**A stop on another branch is never replaced.** On one branch, a later change replaces what the
stop shows, since the owner looking at the newest commit sees the older ones under it. Across
branches that does not hold: a stop on a feature's branch replaced by one on `main` would have the
owner resume, and so accept, a change he never saw. So a change on another branch than the one
that stopped the project is left unjudged, its watch staying before it, and stops the project again
on the first read after he resumes.

**A watch that cannot read a feature's branch** for five minutes holds that feature, no job for its
cards and no merge into its branch, while the project's other work goes on: GitHub not answering
for one branch says nothing about the others, and the default branch's watch still holds the whole
project, since every feature starts from it.

## Security decisions and accepted risks

Kept for an audit the owner plans once the first part is in place. Each entry is a decision with
its reason, or a risk accepted on purpose; each slice adds the ones it builds.

- **The hosted copy shares a machine and a Docker with the runner's unattended agents**, until the
  runner is retired. Dev flow runs in the hosted copy, so an agent working on the Mac mini could in
  principle reach its container. This is accepted with the guardrails and the off machine backups
  described in "The hosted copy" under "Safety" in `High-level/docs/agent-workflow.md` and in
  `infra/hosted/README.md`, and ends when the runner is retired.
- **Text read from a code host is data.** An issue, a pull request, a comment or a commit message
  is shown as text, never inserted as HTML, and never followed as an instruction, by Dev flow or by
  an agent it runs, since anyone who can write on the code host could otherwise write into the
  owner's page or steer an agent.

  On the page, every such text is a React text child, which React escapes, and the one formatting
  it gets is `devflow/web/src/format.tsx`: a backtick pair becomes a code element and a pair of
  double asterisks a strong element, built as React elements, and everything else stays as written,
  links and Markdown included, so a URL in a pull request is never made clickable. No Markdown
  library, since one is a large surface for little a summary needs, and React's escape hatch for
  inserting HTML appears nowhere in `devflow/web/`. Two tests hold it: `src/format.test.ts` feeds
  the formatter markup and checks it stays text, and `src/waiting.test.tsx` shows a `<script>` and
  an `<img onerror>` in every field of an item as text and fails if any source file under
  `devflow/web/` names that escape hatch. The one link from an item, "Open on GitHub", is the
  stored `web_url`, the code host's own address for the pull request and so data as well: it is a
  link only when it parses as an `https` URL, so no other scheme, `javascript:` among them, can
  run in the page, and it opens with `rel="noreferrer"`, so the page it opens can neither reach
  back into Dev flow's window nor learn its address.
- **A code host's bytes handed to the browser.** The item's screenshots are the one place Dev
  flow hands the browser bytes that came from a code host, rather than text it shows as text. The
  addresses the pull request body embeds need a GitHub session, so the page cannot show them, and
  Dev flow reads the files as the app, through `ICodeHost.File`, when the page asks,
  `GET /devflow/api/waiting/{id}/screenshots/{name}`, see "What waits" in `devflow/README.md`. So:
  the name can only be one the reader already accepted, of lowercase letters, digits and hyphens,
  for the pull request's own repository and number, and a name the item does not list is refused
  before the code host is asked; the path, `<number>/<name>.png` on `pr-screenshots`, is built by
  the server, so a body cannot point the read anywhere else; the content type is fixed,
  `image/png`, and never taken from the host; the bytes are checked to begin with the PNG
  signature and to be at most 5 MB; and every answer carries `X-Content-Type-Options: nosniff`
  and `Content-Security-Policy: default-src 'none'`, so a file that is something else cannot be
  sniffed as HTML or script, nor run anything if it is opened on its own. The browser keeps a
  screenshot for an hour, `private`, so no shared cache does. The address in the body is never
  fetched, shown or made a link. Accepted: the GitHub adapter reads a file's whole answer into
  memory before the route can refuse one over 5 MB, so a large file on `pr-screenshots` costs a
  read of it; the branch is written only by the runner's `screenshots.sh` and Dev flow's workers,
  and GitHub's contents API answers files up to 100 MB.

  A card's pictures go through the same two functions, `ScreenshotEndpoints.Guard` and `FromHost`,
  from `GET /devflow/api/projects/{id}/cards/{number}/screenshots/{name}`: the name can only be one
  the card's newest build that succeeded listed, which Dev flow checked against the rules the
  worker names pictures by when it took the result, and any other is refused before the code host
  is asked; the path, `devflow/<card>/<commit, seven characters>/<name>.png`, is built by the
  server from the card's own number and stored commit.
- **An agent's run gets an environment built from nothing.** Claude Code is started with exactly
  `HOME`, `PATH`, `LANG`, `CLAUDE_CONFIG_DIR`, `CLAUDE_CODE_OAUTH_TOKEN` and
  `PLAYWRIGHT_BROWSERS_PATH`, a path to the image's Chromium, which holds no secret, and the
  project's values its job carried, beneath these so none of them can be overridden, and the
  process starter clears everything else, rather than passing the worker's environment and taking out what is
  known to be dangerous. The experiment found that an `ANTHROPIC_API_KEY` in the environment
  silently wins over the subscription token and bills the API, and a list of what to take out
  misses the next such variable, where a list of what to put in cannot. `--bare` is never passed,
  since it refuses the token, and the login is checked with `claude auth status` before every run,
  which must say the token is what it is logged in with. Two tests hold it:
  `ClaudeAgentTests.TheEnvironmentIsBuiltFromNothing`, of what is asked for, and
  `ProcessStarterTests.TheProcessGetsOnlyTheEnvironmentGiven`, which sets an API key in the test's
  own process and checks a real child process never sees it.
- **The worker never writes the token.** The Claude token reaches a run in its environment and
  nowhere else: the worker does not write it to a file, log it or send it back, `AgentCredential`
  prints as redacted, and its value is replaced in Claude Code's output before anything reads it,
  so an agent that prints its own environment cannot carry the token out in its report or its
  stderr. `ClaudeAgentTests.TheTokenIsNeverWrittenAnywhere` checks every answer the worker keeps.
  Accepted: the agent runs with the token in its environment and can read it, which a run cannot
  avoid, and could send it out encoded; the token is the owner's subscription alone, and is revoked
  and made again if it is ever exposed.
- **The agent cannot read the worker's files.** In the base image the worker runs as root and keeps
  a job's secrets in `/run/devflow/`, mode 700, owned by root, and starts every process the agent
  runs, Claude Code, the check and the start, as the user `agent`, uid 1000, through `setpriv`
  with its environment reset. So what a job gives the worker, such as the push key, stays out of
  the reach of the code the agent writes and runs. `base-image.test.sh`, which `check.sh` runs on
  the built image, checks that `agent` can neither list nor read the folder. Accepted: the agent
  shares the container's network and kernel with the worker; a container per job is a later
  choice, if the agent turns out to need less trust than that.
- **The key is never in the command that starts a worker.** The page shows the key once, in the
  command that saves it, to `~/.devflow/<project>.env`, readable by the owner alone; the command
  that builds and starts a worker has `sed` read the key from that file and pipe it to `curl` as
  its header on stdin, `-H @-`, and passes it to the container with `--env-file`, so it is never in
  the command's text, the shell's history, any process's arguments in `ps`, or `docker inspect`'s
  command line. A `-H "Authorization: Bearer $(…)"` would not do: the shell expands it before
  `curl` starts, and `curl` hides `-u` from `ps` but not `-H`. `StartCommandTests` checks the start
  command holds no key, and runs its fetch in a shell with a `curl` that records its arguments, to
  check the key reaches it on stdin and not in them. The recipe route is the worker scheme's, so only a holder of the project's key reads the
  project's recipe, and it answers it as `text/plain` with `nosniff` and `no-store`, as the
  screenshot route hands on a code host's bytes. Accepted: the key is in the container's
  environment, which `docker inspect` shows to anyone who can run Docker on the machine, who can
  already read the file.
- **A worker on an old recipe gets no work.** A worker that reports another recipe version than
  Dev flow last read, or another base image than the release's, is answered `204` by `jobs/next`
  whatever work waits, so a worker built before a fix to its recipe or to the worker program does
  not run a card with what the fix replaced. The comparison is in SQL, so the workers' namespace,
  which the constant time test keeps free of string comparisons, stays so. Accepted: a worker
  reports its versions itself, so one built by hand could claim the current ones; it holds the
  project's key, which already lets it do anything a worker may.
- **The program asks GitHub for changes; there is no webhook.** The platform is not reachable from
  the internet, by design, so nothing outside can call it, and opening it to receive webhooks would
  add an exposed endpoint for a convenience polling gives without one. When it is reachable, a
  webhook is to call the same seam the loop does, `ProjectRefresher.Request`, see "Reading and the
  snapshot" above, and verify GitHub's signature before it does.
- **A secret Dev flow holds is sealed in its database, with a key that is not in it.** The owner's
  GitHub tokens and Claude login, and later each project's test secrets, are put in the vault,
  `src/DevFlow/Vault/Vault.cs`: AES-256-GCM from .NET's own `AesGcm`, the standard authenticated
  cipher, with no other library, under a 32 byte key from `DEVFLOW_VAULT_KEY`, a random 12 byte
  nonce per put and the user and the purpose as associated data. So a row is unreadable without the
  key, a changed byte is refused, and a row copied to another user or purpose does not open. The
  rows are also scoped to their user by row level security, like every table.

  It protects against someone holding a copy of the database, the backup role, a backup, or any
  other reader of it, and against a query that selects too much. It does not protect against
  someone who can read the service's environment, where the key is, and on the shared Mac mini
  that includes the runner's agents, the risk the first entry above accepts.

  The key is not in the backups, by design, so a backup alone opens nothing. It is kept in the
  owner's password manager; if it is lost, what is sealed cannot be opened and is entered again, a
  GitHub link by linking again. A key is changed by making a new one current and listing the old
  one in `DEVFLOW_VAULT_OLD_KEYS` until every row has been put again; each row records the id of
  the key it was sealed with. Nothing rewrites every row under a new key yet. Without a key, the
  fake code host, used by tests, CI and the runner's stack, makes one in memory at startup, and
  with `DEVFLOW_CODE_HOST=github` Dev flow refuses to start. How it works is "The vault" in
  `devflow/README.md`.
- **The Claude token is sealed per user, never answered, and leaves Dev flow only inside a job.**
  It is the credential a run holds that can spend the owner's subscription, so: it is put
  in the vault under `claude-login`, bound to its user, and nothing else in the database holds it;
  no route answers it or any part of it, the status route says only whether one is set and when,
  and a refused one is not repeated in the error; the page never reads it back, and its field is a
  password field emptied on every save; and it is opened by one method alone,
  `ClaudeLogins.ForRun`, which takes the run's user, opens as that user, and answers a type that
  never prints its value, so the only way out of Dev flow is a job's answer to that user's own
  worker. A test holds each of these, in `ClaudeLoginTests`. Accepted: inside the container, code a
  run executes can read the token, since Claude Code, which runs as the container's unprivileged
  user, must hold it; the spec's experiment named this, and its round 5 looks at narrowing it. And
  Dev flow does not check the token with Anthropic when it is saved, so a mistyped one is found by
  the worker's check, not the page.
- **The program writes as the app, through a second typed path.** What the program does on GitHub
  is the app's, see "The program's own writes" above, made with an `AppWriteToken`, which only the
  installation token issuer and the fake's make, and which a read token or the owner's token
  cannot stand in for. Each key is narrowed, in the request for it, to one purpose and one
  repository, and is never kept; no key asks for `workflows`, so none can change a project's CI;
  and the program's reads stay read only, which `InstallationTokenTests` keeps, and cannot reach a
  write key, which `ReadPathTests` keeps. Accepted: a push key can push to any branch of its
  repository, `main` included, and merge through the API, since GitHub files merging under
  Contents; the default branch is watched, and anything landing there that Dev flow did not merge
  stops the project, see the next entry. Someone who holds the app's private key could still
  make any key the app's permissions allow; the key is a file on the machine, outside the
  repository, mounted read only into Dev flow's container alone, and on the shared Mac mini it is
  exposed to the runner's agents like the vault's key, the risk the first entry accepts.
- **The default branch is watched, and that is what makes a push key acceptable.** A push key can
  reach `main` and merge through the API; nothing on GitHub stops it, so the watch is what catches
  it, within the minute of the next read, see "Watching the default branch". What is fine is
  narrow: a change by the owner's linked login that is not a force push, or a merge whose commit
  Dev flow recorded in the same transaction as it merged, under the lock the watch judges under.
  Everything else stops the project, the app's own unrecorded push or merge first among it. A
  stopped project starts nothing and merges nothing until the owner resumes it, naming the commit
  he looked at. A watch that cannot read holds new work rather than guessing, and an answer it
  cannot read is a read that failed. `MainWatchTests` holds each of these. Accepted: the watch
  detects and does not prevent, so a change can sit on `main` for up to a minute, and a run already
  under way finishes, before anything stops; nothing is reverted, which is the owner's to decide.
  A change of the default branch, which only an admin can make, begins the watch again on the new
  branch, so a push to it between the change and the next read would not be judged. The project's
  own runner, where it has one, merges as its own login, which is not the owner's and not Dev
  flow's, so each of its merges stops the project; this repository's runner merges as
  `mac-mini-agent`, so while it runs, a connected `Personal-platform` stops at each merge the gate
  makes, and is resumed from the page.
- **A feature's branch is watched as the default branch is, and a push key reaching it is accepted
  on that condition.** A push key can reach every branch, a feature's among them, and a feature's
  branch reaches `main` with one confirmation from the owner. So each open feature's branch is
  watched from its creation, every change judged as on `main`, its deletion stopping the project
  too, and a stop on one branch is never replaced by another's, so nothing is resumed unseen. Fine
  besides is a push a run of Dev flow's own made there, by the commit its result named; a push by
  the app while such a run is under way waits for the result, and stops the project when the result
  names another commit or the run is lost. A feature's branch not read for five minutes holds that
  feature. `FeatureWatchTests` holds each of these. Accepted: the worker reports the commit it
  pushed, so a run that pushes something other than its work is fine to the watch as long as its
  result names it; the worker already holds the push key, and what it pushed is reviewed before it
  is merged, so the watch guards against what no run of Dev flow's made, not against the run. A
  feature's branch is no longer watched once its feature is merged or closed, so a change to it
  afterwards is not seen, and nothing builds on it then.
- **No language model decides a merge.** `MergeCheck.Decide` is deterministic code over what was
  read, and of the agent's report only whether it marked a decision for the owner is among it, on
  `sometimes`, read in SQL as stored, which can only make a card wait; the reviewer's verdict counts only as Dev flow's
  record, tied to its commit. `MergeCheckTests.NoLanguageModelDecides` walks everything the check
  and its caller run in Dev flow's code and fails if any of it names a type of the briefs, the
  worker protocol, the agent's report, the jobs, the workers, the Claude login or the recipes.
- **CI that never reports is not a pass.** No check run and no status on the commit is waited for
  30 minutes from the push and then left to the owner, "CI did not report"; any conclusion but
  `success`, a status `failure` or `error`, refuses with its name; and a read of CI that failed is
  no merge. Accepted: a project with no CI at all never merges by itself; its owner merges each card,
  or gives it CI.
- **A merge names the approved commit.** The merge is asked with `sha` set to the commit the worker
  checked and the reviewer approved, which GitHub refuses when the head moved, and that refusal
  merges nothing and records nothing. The hold, the setting and the card are read again under the
  project's lock before GitHub is asked, so a stop, a change to `always` or a send back made while
  the check read is never overtaken. `MergeCheckTests.AHeadThatMovedIsNotMerged` and
  `EachConditionAloneRefuses` hold it.
- **Only a merge Dev flow recorded is fine on the default branch.** The merge check merges only
  through `AppMerges`, which writes the merge commit to `merges_made` in the transaction that
  merged, under the lock the watch judges under; a merge by the app the watch finds no record of
  stops the project. Accepted: a merge whose answer named no merge commit stops its project although
  Dev flow made it, and the owner resumes it.
- **A decision issue's text cannot close an issue or notify anyone.** Its body is the agent's and the
  reviewer's words, made data as a card's pull request body makes them, see "The report's text
  cannot add a section or close an issue"; its title is one line of what was chosen, where GitHub
  reads no keyword or mention. The issue is opened with a `Post` key, never the merge's.
- **The owner's actions from the page are made only with his token, and the code makes the other
  impossible.** What the owner does from the waiting list is recorded on GitHub as his, so the
  runner reads his comment as his input; what the program does is the app's, so it is traced back
  to a card and a run, the spec's anti goal 8, and never passed off as his.
  `ICodeHost.Merge` and `ICodeHost.Comment` take a `UserAccessToken`, and nothing else that could
  authorise them. An installation's token is a string and an app's key an `AppWriteToken`, so
  passing either where his writes want a `UserAccessToken` does not compile. Turning a string into
  one takes its constructor, which is `internal`, so no code outside Dev flow's assembly can, its
  test assembly apart. Within the assembly C# cannot narrow an `internal` constructor to one class,
  so a test does: `GitHubWriteTests.AUserTokenIsMadeOnlyByUserTokens` reads the compiled code of
  every method in the assembly and fails when anything but `UserTokens` calls that constructor, a
  target typed `new()` included. `UserTokens.For` answers `UserToken.Usable` already holding the
  typed token, from the person's own token in the vault, so the routes pass it on and never make
  one. His writes never reach `InstallationTokens`. `GitHubWriteTests` also checks the two
  signatures and the requests, and `CommentTests.TheOwnersActionsAreNeverWrittenAsTheApp` that
  every write the routes make carries his own token, and that they ask for no app key and make no
  write as the app.
- **A merge names the commit that was shown.** The page sends the head commit it showed, Dev flow
  refuses it unless it is the stored head, and GitHub is given it as `sha`, which it must match, so
  neither a stale page nor a commit pushed since can be merged unseen.
- **A stopped item cannot be merged from the page.** The route refuses a merge for an item of kind
  `stopped`, as it does for one sent back or whose checks are not green, before GitHub is asked.
- **The page never writes what the runner owns.** Neither route sets a label or a status; the
  runner reads the owner's comment and moves its labels itself.
- **A comment goes to the repository named by its stored owner and name.** GitHub's comment address
  names a repository by them, and a comment, unlike a merge, names no commit that would refuse it
  elsewhere. So if a connected repository is renamed and another repository the owner can write to
  takes its old name, a comment could reach that one's pull request of the same number until the
  project is connected again. Accepted: the owner would have to create that repository himself,
  and the reads, narrowed to the repository's id, already show the project as failing once its
  name points elsewhere.
- **The return from the code host is a GET that changes state.** "How a feature authenticates" in
  `High-level/docs/feature-contract.md` says never to change state on a GET, and
  `GET /devflow/github/callback` is the one exception, since GitHub sends the browser back with a
  GET. What makes it safe is not the `Origin` header, which a redirect from another site cannot
  carry, but the state: random, single use, looked up only among the logged in user's own sign
  ins, and ten minutes old at most, with PKCE so a code is useless without the verifier Dev flow
  kept. A forged return names no state of the victim's and stores nothing; an attacker's own code
  sent to the victim's browser fails the same way, so the victim's account cannot be linked to the
  attacker's GitHub. It needs the platform session, under the `Page` policy, and answers only with
  a redirect to Dev flow's own page.
- **A refresh token is single use, and renewed under a lock.** GitHub takes a refresh token once,
  so two requests renewing at once would spend it twice and end the link. `UserTokens.For` renews
  inside one transaction holding `SELECT ... FOR UPDATE` on the account's row: the second request
  waits, then finds the first's new token fresh. `TokenTests.TwoRequestsAtOnceSpendOneRefreshToken`
  keeps it, with the second request seen waiting on the lock in Postgres. The cost is that the row
  stays locked for one renewal, at most the adapter's 20 seconds, which only that user's own
  requests wait on.
- **Unlinking forgets, and revokes nothing at GitHub.** It deletes the account and both tokens
  from Dev flow, which is all Dev flow holds, so nothing of the link can be used through Dev flow
  afterwards. The authorization stays on GitHub until the owner revokes it there, under his
  Settings, Applications, Authorized GitHub Apps, which "Registering the GitHub App" in
  `devflow/README.md` says. Revoking it from Dev flow would need the client secret sent to delete
  the grant, and adds nothing while Dev flow is the only holder of the tokens.
- **The polling targets are the first of two tables without row level security.** `poll_targets` tells a
  background loop, which has no logged in user, which projects exist and whose they are, so it
  can then work through the door as each one's user. It holds a project's id and its user id and
  nothing else: no name, no repository, no token, so all it says about another user is that they
  have a project. `devflow_app` may only read it; the migration revokes `INSERT`, `UPDATE` and
  `DELETE`, and only a trigger on `projects` writes it, through a `SECURITY DEFINER` function owned
  by `devflow_owner` with its `search_path` fixed. The loop reads it through the door's
  `WithoutUser`, a transaction that sets the user empty, so every guarded table is empty to it in
  that transaction and refuses its writes; all it does after is as each project's user. The alternative, decided against with the owner,
  was a second database role for the loop that bypasses row level security: less to build, but a
  bug in the loop could then read or write across users. `IsolationTests.EveryTableIsGuarded`
  passes the shared check `["poll_targets", "worker_key_lookup"]` and nothing else, so a third
  unguarded table fails it, and `PollTargetTests` keeps the table's two columns, the app role's
  read only access and the trigger. The second is the worker key lookup, the next entry.
- **The worker key lookup is the second table without row level security.** A worker's request
  carries a key and no user, and a key must lead to a user before anything else can run through the
  door. `worker_key_lookup` holds a key's SHA-256, its project's id and the user's id and nothing
  else, and is read only through the door's `WithoutUser`, by the hash. It is safe for the reasons
  `poll_targets` is, and kept the same way: `devflow_app` may only read it, and only a trigger on
  `worker_keys`, through a `SECURITY DEFINER` function owned by `devflow_owner` with its
  `search_path` fixed, writes it. What it adds over `poll_targets` is a hash, which gives nothing
  back: a key is 256 random bits, so neither its hash nor the table opens anything to someone who
  reads it. And the lookup alone decides nothing: the request then confirms, as the user it found
  and under row level security, that the user's own `worker_keys` holds that hash for that
  project, so a lookup row that disagreed with the keys, by a bug or by someone who could write the
  table, would open nothing; `WorkerApiTests.AWorkerActsAsItsProjectsUserOnly` points a key's
  lookup row at another user and at another user's project and sees `401` both times. A key and a
  worker reference `projects (id, user_id)`, so neither can be stored for a user other than its
  project's, whatever row level security let through. The alternative, a second database role
  that bypasses row level security for the lookup, was decided against with `poll_targets`, for
  the same reason. `WorkerKeyLookupTests` keeps the table's three columns, the app role's read
  only access, the trigger and the foreign keys.
- **A worker key is stored only as a hash, and is replaced, not listed.** A key is 32 bytes from a
  cryptographic generator, written as `dfw_` and their base64url, so a scanner for leaked secrets
  can recognise one. It is sent once, in the answer that made it, with `Cache-Control: no-store`,
  and only the SHA-256 of it is stored; nothing lists keys or shows one again, and making a new one
  replaces the last in the same statement, so the old one is refused from the next request. A
  plain SHA-256 is enough where a password needs a slow hash, since there is nothing to guess in
  256 random bits. The key is never logged, never put in an address and never in a cookie:
  `WorkerKeyTests.TheKeyIsNeverLogged` reads every log line at every level, and
  `AKeyIsShownOnceAndOnlyItsHashIsStored` searches every table for it. A presented key is never
  compared with a stored value: it is hashed and the hash is looked up, so the time a lookup takes
  says nothing an attacker could use, and `WorkerApiTests.TheKeyIsComparedInConstantTime` reads
  the compiled code of the worker's namespace and fails if anything there compares strings or
  spans for equality. Accepted: the key sits in the command that starts a worker, and so in that
  machine's shell history and the container's environment; on the shared Mac mini that is the risk
  the first entry accepts, and replacing the key ends a leaked one.
- **Worker routes are outside the `Origin` check, and read no cookie.** A worker is not a browser
  and sends no `Origin`, so `/devflow/api/worker/` is the one path the check leaves out, matched
  as written, case and all, so a path that only routes the same way is still checked. That is safe
  because what the check defends against, another site riding the owner's session, cannot reach
  those routes: the worker scheme reads only the `Authorization` header, never a cookie, so a
  browser's request carries nothing they accept, and a user's token in the header is refused there
  as well. A worker's key sent to a user's route is refused the same way, so the two ways to
  authenticate never stand in for each other; `WorkerApiTests.AWorkerKeyCannotCallTheUsersRoutes`
  keeps both.
- **Only a trusted account's own, unedited cards are instructions.** A card is picked only when a
  trusted account of the project wrote it and nobody off the list has edited its body or title, by
  GitHub's own edit history, read through GraphQL; the list is the owner's linked login, always,
  and the logins he added, see "Cards". Anything else carrying the pick-up label, a stranger's
  issue, a trusted issue edited by a stranger, or any issue while the owner has no link, is
  ignored, and the page lists it as ignored with why, so it is seen rather than silently dropped. A
  feature's parent and each of its sub-issues are held to the same rule, see "Features and their
  cards": the label on a trusted parent never makes a stranger's sub-issue an instruction. A card's
  title and body are data, shown as text. A trusted account's words count as the owner's would, so
  adding one is trusting that account and whoever controls it with work on the project; it never
  makes Dev flow act as that account, and Dev flow acts as the owner only with his own token. The
  pick-up label itself counts only when a trusted account put it on, and a hold label a trusted
  account put on stays in force until a trusted account takes it off, by the issue's timeline, see
  "How a card is picked". The accepted risk: anyone who can label issues in a repository can
  still make a card appear as ignored on the owner's page, as text, by labelling a stranger's issue
  or by putting the pick-up label on a trusted account's issue it was not on, and can hold a card
  by the hold label; they can no longer start, release or switch on anything. GitHub's edit history
  does not cover a comment, which no card is read from yet.
- **A project's values are sealed per project, never answered, and carried only by jobs that run its
  code.** A value can reach whatever it is a key for, so: it is sealed by the vault bound to its
  user, its project and its name, in `project_values`, and nothing else in the database holds it;
  no route answers it, the values route names each with when it was set, and a refused one is not
  repeated in the error; the page's field is a password field the browser does not keep, cleared once
  sent; and it is opened only in the transaction that hands out a build, a fix, a conflict, a
  feature's fix, a setup or a check, so a reading or a review, whose agent runs none of the project's
  code, never holds one. The worker puts it in the environment of the agent's run, the check and the
  start and of nothing else, beneath its own variables, and keeps it only for the job; a name the
  worker, Claude Code, git or Playwright reads is refused, so a value cannot steer them. Tests hold
  each, in `ProjectValuesTests` and `WorkerValuesTests`. Accepted: inside the container, the agent and
  the project's code can read every value, which is what they are for, as the Claude token is.
- **Every known value is scrubbed from what Dev flow stores and posts, and from what the worker
  sends.** Each value of the project, as itself, URL-encoded, and in standard and URL-safe base64,
  whole or inside a longer encoding, becomes `[value of NAME]`, by `ValueScrub`: Dev flow scrubs every
  text of every result before it is stored, `JobEndpoints.Complete`, and every title, body, comment
  and commit message it posts, `ScrubbingCodeHost`, the code host every part of it is given; the
  worker scrubs every text it sends and every line it logs while the job runs. `ScrubTests` holds
  each side. Accepted, as the slice "A project's values" records it: scrubbing finds only these forms,
  so an agent that uses a value, or prints it transformed in another way, is not caught; a picture's
  bytes are not text and are not scrubbed, so a page that shows a value shows it in its picture. That
  is why a value must be a throwaway or a key made for the automation with a low limit, never
  production's, as the spec's round 3 has it.
- **A run's log is scrubbed of the token and the project's values only.** The worker replaces the
  Claude token and scrubs the job's values from every line before it sends it, and Dev flow scrubs
  the project's values from each line again before it stores it, `JobEndpoints.Hear`, as it does a
  result's text. Nothing else is looked for. Accepted: a secret the agent reads from a file the
  values do not hold, a key in the repository or one the project's own code prints, can reach the
  log, as it can reach a result's stderr today; the log is shown only to the project's user, through
  the door as them, and a line is never posted on GitHub. A project that keeps such a secret where
  its agent can read it should give it as a value, so it is scrubbed.
- **The Claude token, the project's values and a push key leave Dev flow only inside a job, to the
  worker holding it.**
  A job is answered only to a worker of the key's own project, through the door as its user, so
  the token is that user's, the values that project's, and the push key reaches that project's
  repository alone, narrowed to
  fetching and pushing code; the answer carries `Cache-Control: no-store`. A fresh push key is
  answered only to the worker holding that job while it runs, and every other asker gets the same
  `404`, so it cannot tell another's job from none. The token is opened only after the login's
  status allows a run; the push key is never stored; and `Job`, `JobValue` and `PushKeyAnswer` print
  none of them.
  Accepted: a worker's id is not a secret among the workers of one project, which share one key,
  so a worker of the project that learned another's id could ask a fresh key for its job; it holds
  the same key and could take a job of its own, so it gains nothing.
- **A card's title and body are the only text from GitHub in the brief.** Each is quoted inside a
  fenced block longer than any run of backticks in it, so nothing in a card can end its quote and
  write into the rules around it, and the brief says the card is the only instruction and anything
  else the agent reads is information. The check from `.devflow/project.json` is in it too, fenced
  the same way: it is read from the default branch, which only someone who can push there writes,
  as the project's `CLAUDE.md` is. No comment, pull request or other issue is put in a brief.
- **The report decides nothing.** A run succeeds by the check's exit code, which the worker runs
  itself, and never by what the agent says; a report that does not match its form stops the card.
- **The report's text cannot add a section or close an issue.** A card's pull request body is
  written from the agent's report, which is data, and GitHub acts on a body: a `Closes` line closes
  an issue when the pull request merges, an `@` mention notifies a person, and HTML, a comment
  among it, can hide what follows. So every text from the report is made one line, so it begins no
  heading, fence, quote or table; `&`, `<` and `>` become entities; its backslashes and backticks
  are escaped, so no backslash of the agent's can undo an escape and the only code spans are Dev
  flow's; a leading `#` is escaped; a closing keyword followed by an
  issue has the keyword put in a code span, so it closes nothing; and a mention is put in a code
  span, so the app notifies nobody. The body's five sections and its one `Closes` line are Dev
  flow's. `PullRequestTests.TextFromTheReportCannotBreakTheBody` and `EachTextIsMadeData` hold it.
  Accepted: the check, shown in a code span as it is, is the project's own, from its default branch,
  as the brief's check is; and a link in the agent's text stays a link on GitHub, as one in a
  comment would.
- **The app reuses only its own pull request.** A pull request from the card's branch by anyone
  else, a fork's with the same branch name among them, is never taken for the card's, so nobody can
  have the owner merge their commits as the card's.
- **Push keys before the watch.** The push key can reach `main`, and the watch on the default branch,
  the slice "Watching main", is what catches a push that Dev flow did not make. "Handing out work"
  landed while that one was still in review, so for that time `jobs/next` hands out push keys with
  no watch, and since "The worker builds and pushes" the worker uses them. Accepted because the
  worker program never pushes to the default branch, see the entries below, the agent cannot read
  the key, and the feature's own runbook starts no worker until the whole feature has merged. The
  window closed when "Watching main" merged: its hold stands in front of handing out, and is read
  again under the project's lock as the card is taken, so a project it has stopped, or cannot
  read, hands out nothing.
- **Root never runs git in a copy the agent controlled.** A hook, a config such as
  `core.fsmonitor`, or an attribute in the agent's copy would run as root, beside the push key, if
  root ran git there. So root runs git only in its own bare repository, with
  `core.hooksPath=/dev/null` and no system or global config, and fetches the agent's branch from
  the copy with git's side of the fetch run as `agent`, see "A build job is one workbench and two
  users" above; the agent clones from a bundle root writes, so root never makes or changes a
  repository the agent works in. The git commands in the copy run as `agent`, with hooks,
  `core.fsmonitor` and signing off, so what the agent planted neither runs nor stops the worker's
  commit of what it left. `GitSafetyTests.RootNeverRunsGitInTheAgentsCopy` checks every root git
  command against a recording starter, and `AHookTheAgentPlantedNeverRuns` plants hooks and an
  `fsmonitor` in the copy and sees none run, with real git.
- **Root never writes, or removes, at a path the agent can redirect.** A file root writes in a
  folder the agent can change is written wherever a link the agent left there points, as root, so
  the agent could overwrite the worker's binary, `/etc/passwd` or root's repository beside the push
  key. Root writes the bundles in `/run/devflow-handover/<job>/`, which it makes new for each job in
  a parent only root can write, and the agent only reads; the agent's own folder, `/work/<job>/`,
  is removed by the agent, with `rm -rf`, since a recursive removal by root could follow a folder a
  process of the agent's swapped for a link while it ran. Its only other contact with that folder is
  the fetch from the copy above, whose side there runs as `agent`.
  `GitSafetyTests.ALinkTheAgentLeftWhereRootMightWriteIsNeverWrittenThrough` and
  `AJobFolderTheAgentReplacedWithALinkIsNeverWrittenThrough` plant both links and see the file
  behind them untouched and no root command naming the agent's folder but the fetch, and
  `base-image.test.sh` checks that, on the built image, `agent` can read a bundle there and create
  nothing, not even a link.
- **The agent's copy has no credentials.** The push key reaches git only in the environment of
  root's clone and push, as a header for the clone address through `GIT_CONFIG_COUNT`, never in an
  argument, which every user sees in `ps`, a file, or a remote's address; the copy's remote is the
  path of a bundle that is gone. `/proc/<pid>/environ` is its owner's, so the agent cannot read
  root's environment, which `base-image.test.sh` checks on the built image, and
  `GitSafetyTests.ThePushKeyNeverReachesTheAgent` checks that no file under the agent's folder,
  the copy's config, its remote, Claude Code's environment or arguments, or any process run as the
  agent holds the key. Accepted: the agent's `/work` is its own, so it can rename or replace the
  job's folder there, and a process it detached from the agent's process tree, which ending the run
  does not reach, keeps running as `agent` through the check, the push and the next job in the same
  container. Root writes nothing there and fetches from the copy as `agent`, see the entry above, so
  the most either gains is a check run on files of the agent's choosing, or a later job's copy
  changed, both as `agent`, which code the agent writes already runs as; the check's exit code is
  trusted only that far, and a project's CI on GitHub is the independent check.
- **A push is never forced and never to the default branch.** A job whose branch is the
  repository's default branch, or its base, or not a plain branch name, is refused before anything
  runs, whatever Dev flow sent, and stops the card. The push is
  `git push origin <head>:refs/heads/<branch>`, without force, so the worker can only add to the
  job's own branch; a branch someone else moved meanwhile is refused rather than overwritten.
  `GitSafetyTests.ThePushIsNeverForcedAndNeverToTheDefaultBranch` and
  `BuildPartsTests.ABranchGitCouldReadAsSomethingElseIsRefused` keep both.
- **The reviewer cannot write.** A review's agent runs with Claude Code's default permission mode,
  no prompts and only `Read`, `Grep` and `Glob` allowed, never `bypassPermissions`, so every other
  tool is refused and reported in `permission_denials` rather than waiting, as the experiment of
  2026-11-16 found; those are recorded and fail nothing, since it cannot write by construction. Its
  worker holds a key narrowed to fetching, `contents: read`, never one that pushes, and the push key
  route refuses a review; it commits and pushes nothing, and root writes the review's files only in
  its own handover folder, which the agent copies from, so root never writes where the agent can.
  What it reads is data: an instruction in the card, the report, the diff, the code or the text in
  a picture is a finding.
- **A fix starts from the commit Dev flow handed it.** A fix job names the commit the card's branch
  must be at, the newest one its own runs left, and the worker refuses the fix in the card when the
  remote's branch is anywhere else, before the agent runs. A fix that leaves the branch at that
  commit fails in the card. `FixJobTests.ABranchThatMovedIsNotFixed` and
  `FixTests.AFixThatChangesNothingIsAFailureInTheCard` keep both.
- **Only a trusted account's comment is read as input, and only into a fix.** Comments on a card's
  pull request are read for one purpose: a comment by a trusted account of the project, new by its
  id, starts a fix with its words quoted in the brief as data. A comment by a login off the list,
  the app's review copies among them, is passed over and never reaches a brief; no comment, his included, is read as a
  verdict, so none can approve or merge a card. `FixTests.TheOwnersCommentStartsAFixRun` keeps it.
- **The project runs as `agent`, in a clean copy.** A project's `start` is code from its
  repository, on a commit an agent wrote, so it runs as `agent`, never root, in an environment
  built from nothing and `DEVFLOW=1`, from a fresh clone of the pushed commit the agent made,
  with nothing of the agent's working copy in it. It runs in a process group of its own, through
  `setsid`, and the group is ended afterwards, `SIGTERM` and then `SIGKILL`, so a server it left
  in the background ends too, which ending the tree by parent would miss once it was detached.
  Root reads each picture through a process of the agent's, `head -c` at most one byte past 5 MB,
  never by opening the file itself, so a link the project left at a picture's name reads nothing
  the agent could not, and a file that never ends is cut; and root keeps only a PNG of at most
  5 MB. The page's address is the project's `url` joined with a path from the report, which is
  data, and a join that leaves the project's own origin is refused. Accepted: a process that
  calls `setsid` again leaves the group and keeps running as `agent` in the container until it
  is stopped, the same as a process the agent detaches during its run, under "The agent's copy
  has no credentials"; and Chromium runs as `agent` without its own sandbox, as Playwright starts
  it, since the container is the boundary and the pages are the project's own. The browser is
  root's, under `/ms-playwright`, so one job cannot change it to fake the next job's pictures.
- **The test mode is the project's, and must be off without `DEVFLOW=1`.** Dev flow never holds
  a project's credentials or logs in for it: it sets `DEVFLOW=1` and nothing else, and a project
  that needs a login comes up signed in by its own code. A test mode that turns on without the
  variable would be a way into the project in production, so `devflow/README.md` says plainly
  that it must be off when `DEVFLOW` is not set; whether it is, is the project's to keep, and
  Dev flow cannot check it.
- **Pictures are served through part 1's checks.** A card's pictures are the second place Dev flow
  hands the browser bytes from a code host, and they go through the same code as the first, see
  "A code host's bytes handed to the browser" above, so a check added there holds for both. The
  pictures are pushed with the build's own push key, after the job's branch, never with force and
  never to the default branch; a repository whose default branch is `pr-screenshots` gets none.
- **A verdict is the worker's commit and Dev flow's record, never a comment.** The commit a verdict
  is for is the one the run was handed and its worker checked out; the result route refuses any
  other, and the verdict's form has no place to name one. The verdict is stored in `reviews` in the
  same transaction as its run's result, by the program alone. The comment the app posts is a copy,
  and the only comments Dev flow reads on its account are the app's own, to avoid posting one twice;
  no comment, by the owner, the app or anyone, is ever read as a verdict, so none can approve a card.
- **The app is registered for one account.** "Only on this account" means only the owner's account
  can install it. Installing it on an organisation needs it opened to any account first, and then
  any stranger could install it on their own repositories too; that gains them nothing, since
  installing it gives Dev flow's app access to their repositories, not them access to the owner's,
  and which repositories Dev flow reads is decided by what each platform user connects.
- **The fake code host must never be on where real data is.** It answers whatever an account
  staged, so a page in the fake mode can show a pull request that does not exist, and every
  signed in user can replace their own world through `/devflow/api/fake/`. It is the default, so
  that tests and the runner's stack need nothing set, which makes the hosted copy the one place
  that must say otherwise. It does: `infra/hosted/compose.yaml` passes `DEVFLOW_CODE_HOST`, empty
  when its `.env` leaves it out, which Dev flow refuses rather than reading as the default;
  `infra/hosted/.env.example` lists it as `github`, so the release's preflight refuses an env file
  without it; and only `check.sh`'s throwaway copy sets `fake`. The fake's addresses do not exist in
  the GitHub mode. The visible sign is the banner, "Fake GitHub. Nothing here is real.", on every
  Dev flow page in the fake mode, which cannot be dismissed, so a fake page never passes for the
  real one.
- **A stop's comment carries a run's reason as data.** The reason may be a worker's or an agent's
  words, so `StopText` makes each one data as the pull request body makes the report's: one line,
  no HTML, its backticks escaped, a closing keyword and an `@` mention each in a code span, so the
  app's comment closes nothing and notifies nobody. The label and the comment are posted with a
  `Post` key, which cannot push or merge.
- **A stopped card's issue is read for a trusted account's comment alone.** Running a card again
  is a write on the strength of a comment, so it is taken only from a trusted account of the
  project, as GitHub compares logins, only when written after the stop, and only into the next
  run's brief as input, quoted in a fence no text can end; a comment by a login off the list, the
  app's own among them, is never read as an input. `TrustedCommentsTests.OnlyATrustedCommentIsInput`
  keeps the three places a comment is read.
- **A worker's `resets_at` is believed within limits.** A worker could send a reset far off and
  hold its own project's work back; it can only hold back the project whose key it has, which it
  could as well by asking for no work, and Dev flow believes a reset only in the next 8 days.
- **A feature reaches the default branch only by the owner's own merge.** The program merges a
  feature's cards into its branch as the app, but never the feature into the default branch: the
  only code that asks for that merge is the route the owner's page calls, which merges with his
  token from `UserTokens.For`, so it is typed as his and recorded on GitHub as his, and the app's
  keys cannot make it. It is refused, before GitHub is asked, while CI has not passed on the head,
  while the project is stopped, and for a feature that is not his turn; and it names the head the
  page showed, which GitHub must match. `FeatureMergeTests.MergingIsMine` checks that the merge is
  his, never the app's, and with the head shown.
- **The only job that pushes a merge of the default branch is held to one merge commit with known
  parents.** A conflict job pushes to a feature's branch, which every later job of the feature builds
  on and whose pull request goes to the default branch. The worker takes the agent's work only when
  it is one new commit, a merge whose parents are the feature's head and the default branch's
  commit Dev flow handed out, in that order, and pushes it without force and only once its check
  passed on a clean copy; anything else pushes nothing. So an agent steered by a conflicting file
  cannot add commits of its own, rewrite the feature's history, or merge in a branch of its own
  choosing. The merge's tree is still the agent's, as any build's is, and is judged by the check and
  then by the review of the whole feature, which reads the resolution apart as its remerge diff, the
  text a worker sent and data. The files in conflict in the brief are paths the code host listed,
  fenced as every other text from outside is.
- **A live stream ends with the token that let it in.** A server-sent events stream is checked
  once, as it opens, like any request; one left open would otherwise go on sending after the
  session it was opened with was ended or ran out. Each ends at its token's `exp`, on Dev flow's
  clock, and the page opens it again, which checks the session afresh. Each reads through the door
  as the caller, so another user's run, its log among it, is `404` and never streamed.
- **A worker's log line is shown as text.** A line is what an agent, a tool or a check wrote, which
  can hold anything a repository or a web page put in front of it, so Running now renders it as a
  text node, a tool's name split off by its first space and nothing parsed, never as HTML or
  Markdown, as every other text from a worker is.
