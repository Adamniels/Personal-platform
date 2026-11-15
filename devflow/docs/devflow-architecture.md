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
two tabs, "Waiting for you" at `/devflow` and "Projects" at `/devflow/projects`, each still empty.
It answers through the proxy under `/devflow` in the dev stack and in the hosted copy, verifies the
core's token itself through `PlatformAuth`, and has a database of its own, `devflow`, migrated by
its `migrate` command. Its tables are the vault's, `secrets`, which seals a secret for a user, and
the GitHub link's, see "Acting as the owner" below. Everything it will ask of a code host goes
through one interface, with an in memory fake behind it and a GitHub adapter that reads as the app,
see "The code host seam" and "GitHub" below. On the Projects tab the owner links his GitHub
account, and Dev flow then holds a token that acts as him, sealed in the vault and renewed by
itself. What it serves, its settings, its database, the vault, the code host and how it
authenticates are in `devflow/README.md`. Whether a pull request waits for the owner, and why, is
read by one pure function that knows this repository's runner, see "Reading this repository's
runner" below.

Nothing uses the owner's token yet, and nothing calls the adapter's reads or that reader yet:
connecting a repository, the poll that reads GitHub, merging and commenting as the owner, and
everything the tabs will show arrive with the later slices of the first part.

## The code host seam

Everything Dev flow asks of a code host goes through `ICodeHost`, in `src/DevFlow/CodeHost/`:
today the reads the waiting list needs, open pull requests, one pull request, a conversation's
comments, a commit's statuses and check runs, and a file on a branch; later, listing what a user
can connect, merging and commenting. Signing a person in is a second interface beside it,
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
signs the owner in so it holds a token that acts as him, see "Acting as the owner" below; writing
as him comes with a later slice. The adapter,
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
ask about which repository is decided by the callers the next slices add, from the repositories
that account connected, which the database keeps per account under row level security.

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
- **The program asks GitHub for changes; there is no webhook.** The platform is not reachable from
  the internet, by design, so nothing outside can call it, and opening it to receive webhooks would
  add an exposed endpoint for a convenience polling gives without one.
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
  "GitHub" above, and only a linked user's own token, see "Acting as the owner" above, which a later
  slice writes with, so what is written is recorded on GitHub as done by him. "Only the owner writes" is so by construction, not
  by care, and the test of that request's body, `InstallationTokenTests`, is what keeps it. Someone
  who holds the app's private key could still make a key that writes; the key is a file on the
  machine, outside the repository, mounted read only into Dev flow's container alone, and on the
  shared Mac mini it is exposed to the runner's agents like the vault's key, the risk the first
  entry accepts.
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
