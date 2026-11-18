# Dev flow architecture

Status: living. It describes what is built, and changes in the same pull request as the code.

Last updated: 2026 11 18

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

A project's page also holds its settings, the labels that pick and hold its cards and whether a
card's pull request waits for the owner, and lists its cards: each read of the project picks the
open issues he wrote that carry the pick-up label, and each card shows as queued, held, waiting for
what blocks it, or ignored with why, see "Cards" below. A worker asking for work is handed the
next queued card with a brief written for it, and what it sends back is recorded on the run and
moves the card on, see "Handing out work" below; nothing yet opens a pull request from it.

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
anything landing there that Dev flow did not merge stops the project; that is the slice "Watching
main". It was planned to come before anything hands out a push key, and "Handing out work and the
brief" landed while it was still in review, see "Push keys before the watch" in the security
section. A push key leaves Dev flow only inside a job's answer, or a fresh key's, to the worker
holding that job, and is kept in memory only, never stored. Handing out work uses the push key;
the post and merge keys are the later slices'.

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

## Projects

A project is one connected repository with its settings, as the spec defines it. In this part it
has almost none: every project is read the way this repository's runner writes, through the
reader below, and `bot_login` names the login whose pull requests that reader looks at, this
repository's bot by default and a column without a page. The settings and presets of the spec
are part 5. How the owner connects one, the routes and the tables are "Connecting a repository"
in `devflow/README.md`.

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
reads on its own; its time is `TimeProvider`'s, so its tests move it.

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
2. Another login wrote it: ignored, saying who.
3. Anyone but the owner edited its body or its title: ignored, saying who. Who edited an issue is
   read through GraphQL, since REST does not say; a deleted account shows as `ghost`, which is not
   the owner either.
4. It carries the hold label: held.
5. Something it is blocked by is still open: queued, and its reason names what it waits for, `#12`,
   or `owner/name#12` in another repository, so the queue says which card it waits for, as the spec
   asks. It is not free to start until that is closed.
6. Otherwise queued and free to start.

The reads are ordered so a card costs only what decides it: one by someone else costs nothing
beyond the list, and only a card nobody else touched costs the read of what blocks it. A picked
card keeps a record of what was read, its body's SHA-256, its title, and the id of its edit
history's newest entry, which later parts compare against before building on it and before
merging, as "Not edited since it was read" in the spec's clarifications asks. Logins and labels are
compared whatever their case, as GitHub's are.

**Only the owner's cards count, fixed in code until part 5.** The spec gives each project a list of
trusted accounts; until part 5 builds that setting, the list is the project's user's linked login
alone. A card that is anything else is never queued, and is listed as ignored with the reason, so a
card that does not run says why rather than vanishing.

**A card that changes.** A card that has not started is decided afresh on every read. One that has
started stays where its run put it: when the owner changed its body or title, its record is
replaced and it stays; when anyone else did, it is ignored with the reason, and a run on it is not
merged, since its state is no longer one a run merges from. An ignored card keeps the record it had,
so a run already under way is still checked against what was picked.

**Taking the label off** removes a card that has not started. One whose run is under way is marked
unlabelled and finishes its run, and the part that ends a run removes it then; one that has
finished, done or stopped, is removed.

**The order** in which queued cards free to start are handed out is `CardStore.Queue`:
`priority: high`, then no priority label, then `priority: low`, each oldest first by when the issue
was written. The spec left where priority is read from to round 6; it is these two labels, matched
whatever their case.

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
the default branch that changed it, so it changes when the recipe does and not with every push.

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
checked, exit `0`, valid JSON, `is_error` false, `terminal_reason` `completed`, a report present
and an object, and no refused action, and anything else is a failure; the table of which failure
lies where is "The worker" in `devflow/README.md`. The report's schema is checked by Dev flow
rather than the worker, which needs no JSON Schema library for it.

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

**A worker running a check stays seen.** A run is lost once it was handed out 100 minutes ago and
its worker has not been seen for 10, see "Handing out work"; the agent's 90 minutes and the check's
30 can pass the 100. So while the check runs the worker says `progress` `checking` again every 5
minutes, which the route takes as often as it comes and which marks the worker seen, so a worker
that is still working is never taken for gone.

**A run's one credential is the Claude login, and Dev flow holds it**, as the spec's rounds 3 and
4b have it. The owner runs `claude setup-token` once and pastes the token on the Projects tab, so a
new worker needs no login step of its own: the program hands the token to each run inside the job,
as `CLAUDE_CODE_OAUTH_TOKEN`, see "Handing out work" below. It is one per
user, not per project, since the subscription is the user's, and it is sealed in the vault like
the GitHub tokens. Claude Code does not report when a setup token runs out, the experiment of
2026 11 16 found, so Dev flow counts a year from when it was given and warns a month before,
through the Projects tab, the nav's dot and the front page's card, which say it apart from what
waits rather than counting it. It checks only that what is pasted looks like a setup token; whether
it works is the worker's check before every run. The routes, the table and the section are "Claude
login" and "The pages" in `devflow/README.md`.

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
may well succeed; one in the card, a report that does not match, a report that stopped, or a check
that did not run or failed stops it with why. The attention label on a stopped card, retries and
the limit on them are part 3's "Stops".

**A run that never returns is lost after 100 minutes, and only once its worker is gone.** 100
minutes is the 90 minute limit on a run and a margin for the check and the push. A worker not seen
for 10 minutes is the second condition, so a slow worker that is still there is not robbed of its
card. The search runs on each project's read, before the code host is asked anything, so GitHub
being away holds no card. A worker that is still seen keeps its card, and one running a check says
so every 5 minutes, see "A worker running a check stays seen" under "Workers", since the agent's 90
minutes and the check's 30 can pass the 100.

**Every run is measured from the first day**, as the spec's round 3 has it: Claude's usage is kept
as it came, and its token counts, cost and turns in columns of their own, with the job's duration,
for the usage page of part 7.

**The fake worker** takes a job and ends it with a prepared result through the same code a worker's
result goes through, and commits on the branch in the fake's git repository, so an acceptance test
drives handing out, the result and what later parts build on it without a container or a model.

**A stopped project** is the slice "Watching main": when it has landed, a project it has stopped,
or whose default branch has not been read for five minutes, hands out nothing, ahead of everything
above.

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
  read of it; the branch is written only by the runner's `screenshots.sh`, and GitHub's contents
  API answers files up to 100 MB.
- **An agent's run gets an environment built from nothing.** Claude Code is started with exactly
  `HOME`, `PATH`, `LANG`, `CLAUDE_CONFIG_DIR` and `CLAUDE_CODE_OAUTH_TOKEN`, and the process starter
  clears everything else, rather than passing the worker's environment and taking out what is
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
  It is the one credential a run holds, and it can spend the owner's subscription, so: it is put
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
  Contents; `main` is to be watched, and anything landing there that Dev flow did not merge stops
  the project, see "The program's own writes". Someone who holds the app's private key could still
  make any key the app's permissions allow; the key is a file on the machine, outside the
  repository, mounted read only into Dev flow's container alone, and on the shared Mac mini it is
  exposed to the runner's agents like the vault's key, the risk the first entry accepts.
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
- **Only the owner's own, unedited cards are instructions.** A card is picked only when the owner's
  linked login wrote it and nobody else has edited its body or title, by GitHub's own edit history,
  read through GraphQL; until part 5 adds a project's trusted accounts, the owner is the only
  account that counts. Anything else carrying the pick-up label, a stranger's issue, the owner's
  issue edited by someone else, or any issue while the owner has no link, is ignored, and the page
  lists it as ignored with why, so it is seen rather than silently dropped. A card's title and body
  are data, shown as text. The accepted risk: anyone who can label issues in a repository can make
  a card appear as ignored on the owner's page, which shows a stranger's title there as text and
  runs nothing. GitHub's edit history does not cover a comment, which no card is read from yet.
- **The Claude token and a push key leave Dev flow only inside a job, to the worker holding it.**
  A job is answered only to a worker of the key's own project, through the door as its user, so
  the token is that user's and the push key reaches that project's repository alone, narrowed to
  fetching and pushing code; the answer carries `Cache-Control: no-store`. A fresh push key is
  answered only to the worker holding that job while it runs, and every other asker gets the same
  `404`, so it cannot tell another's job from none. The token is opened only after the login's
  status allows a run; the push key is never stored; and `Job` and `PushKeyAnswer` print neither.
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
- **Push keys before the watch.** The push key can reach `main`, and the watch on the default branch,
  the slice "Watching main", is what catches a push that Dev flow did not make. "Handing out work"
  landed while that one was still in review, so for that time `jobs/next` hands out push keys with
  no watch, and since "The worker builds and pushes" the worker uses them. Accepted because the
  worker program never pushes to the default branch, see the entries below, the agent cannot read
  the key, and the feature's own runbook starts no worker until the whole feature has merged;
  "Watching main" puts its hold in front of handing out, so a project it has stopped, or cannot
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
