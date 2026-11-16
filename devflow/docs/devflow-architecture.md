# Dev flow architecture

Status: living. It describes what is built, and changes in the same pull request as the code.

Last updated: 2026 11 16

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
the GitHub link's, see "Acting as the owner" below, and the projects', see "Projects" below. Everything it will ask of a code host goes
through one interface, with an in memory fake behind it and a GitHub adapter that reads as the app,
see "The code host seam" and "GitHub" below. On the Projects tab the owner links his GitHub
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

## The code host seam

Everything Dev flow asks of a code host goes through `ICodeHost`, in `src/DevFlow/CodeHost/`:
today the reads the waiting list needs, open pull requests, one pull request, a conversation's
comments, a commit's statuses and check runs, and a file on a branch, and listing what a person
can connect, read with their own token; and its two writes, merging and commenting, made only with
a person's token. Signing a person in is a second interface beside it,
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

Dev flow acts on GitHub as a GitHub App, as the spec's round 3 decided. It reads as the app, and
signs the owner in so it holds a token that acts as him, see "Acting as the owner" below, and
writes only with that token, see "Acting from the page" below. The adapter,
`GitHubCodeHost` in `src/DevFlow/CodeHost/GitHub/`, is written by hand on .NET's `HttpClient`,
since its calls are few, and no automated test touches GitHub: its tests run against GitHub's real
answers, recorded once in `contracts/github/`. Its addresses and settings are in
`devflow/README.md`.

**Signing in as the app.** The app's private key, a file whose path is
`DEVFLOW_GITHUB_PRIVATE_KEY_FILE`, is read once at startup. When the adapter needs an installation
token it signs a JSON Web Token with it, RS256, with `iss` the app's client id, `iat` 60 seconds in
the past against clock drift and `exp` 9 minutes ahead, inside GitHub's limit of 10. That token is
made when needed and never stored; it can do nothing but ask for installation tokens.

**Its own keys can only read.** Every installation token is asked for with `permissions` of exactly
`contents`, `pull_requests`, `issues`, `statuses`, `checks` and `metadata`, each `read`, and
`repository_ids` holding the one repository being read. GitHub never grants more than the app holds,
and narrows to what the request asks. A token is kept in memory per installation and repository
until five minutes before its hour is up, and written nowhere. A repository is addressed by its
owner and name, as GitHub's addresses are, but the token reaches its id alone, so a name that
points elsewhere since is not found rather than another repository read.

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
logged in user, so it reads the one table without row level security, which holds a project's id
and its user and nothing else, and then does all its work through the door as that user. A
trigger on `projects` keeps it, so no code path can forget. The loop is "Reading and the snapshot"
below; why the table is safe is in the section after it.

## Reading this repository's runner

The first part's list of what waits for the owner is read from this repository's runner,
`scripts/agent/`, which records everything on GitHub. The one piece that knows its conventions is
`RunnerReader.Read`, in `src/DevFlow/Runner/`: given one pull request, its comments, its head
commit's statuses and check runs, the comments on its slice issue, the bot's login and the owner's,
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

## Acting from the page

From the waiting list the owner merges a pull request or sends it back with a comment, and GitHub
records both as done by him, so the runner picks his comment up as it does one written on GitHub,
and he needs neither a terminal nor GitHub's own page for it. This is the only code in the first
part that writes to a code host. The routes, their refusals and the page are "Acting from the
page" and "The pages" in `devflow/README.md`.

**Only as the owner, by construction.** Both writes are made with his linked account's token from
`UserTokens.For`, so they are recorded on GitHub as his, and the runner, which starts a fix run
only on a comment by `Adamniels`, reads his comment as his input. The interface's two writes take a
`UserAccessToken`, a type of its own, and nothing else that could authorise them; an installation's
token is a string, so a write as the app does not compile, and the adapter's writes never ask for
one. `CommentTests.NothingIsEverWrittenAsTheApp` and `GitHubWriteTests` keep it.

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
- **The program asks GitHub for changes; there is no webhook.** The platform is not reachable from
  the internet, by design, so nothing outside can call it, and opening it to receive webhooks would
  add an exposed endpoint for a convenience polling gives without one. When it is reachable, a
  webhook is to call the same seam the loop does, `ProjectRefresher.Request`, see "Reading and the
  snapshot" above, and verify GitHub's signature before it does.
- **A secret Dev flow holds is sealed in its database, with a key that is not in it.** The owner's
  GitHub tokens, and later the Claude login and each project's test secrets, are put in the vault,
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
- **The app holds write access to code, and the program's own keys never use it.** Merging needs
  the app to hold `contents: write`, so the app as GitHub sees it can write. Every key the program
  makes for itself is narrowed, in the request for it, to read access on one repository, see
  "GitHub" above, and only a linked user's own token, see "Acting as the owner" above, is written
  with, so what is written is recorded on GitHub as done by him. "Only the owner writes" is so by
  construction, not by care: the test of that request's body, `InstallationTokenTests`, keeps the
  program's keys read only, and the writes' token type keeps them from being written with, see the
  next entry. Someone
  who holds the app's private key could still make a key that writes; the key is a file on the
  machine, outside the repository, mounted read only into Dev flow's container alone, and on the
  shared Mac mini it is exposed to the runner's agents like the vault's key, the risk the first
  entry accepts.
- **Writes are made only with a linked user's token, and the code makes the other impossible.**
  `ICodeHost.Merge` and `ICodeHost.Comment` take a `UserAccessToken`, made only from what
  `UserTokens.For` answers, and an installation's token is a string, so passing one does not
  compile; the adapter's writes never reach `InstallationTokens`. `GitHubWriteTests` checks the two
  signatures and the requests, and `CommentTests.NothingIsEverWrittenAsTheApp` that every write the
  routes make carries the user's own token.
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
- **The polling targets are the one table without row level security.** `poll_targets` tells a
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
  passes the shared check `["poll_targets"]` and nothing else, so a second unguarded table fails
  it, and `PollTargetTests` keeps the table's two columns, the app role's read only access and the
  trigger.
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
