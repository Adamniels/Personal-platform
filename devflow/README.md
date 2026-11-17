# devflow

Dev flow is the platform's feature for automating development on connected repositories: the owner
plans, agents build, and he follows and answers all of it from its pages. What it is and how it is
meant to grow is in `docs/devflow-architecture.md`, and its scope and slices are in its feature
issue, #268. "Dev flow" is its name in the nav and in headings, and `devflow` everywhere technical.

It is a C# feature on .NET 10, an ASP.NET Core minimal API app, built the way `review/` is. Today it
is the service, its page, its database, which holds the vault, the code host's interface with its
fake and its GitHub adapter, which reads as the app, linking a user's GitHub account, so Dev
flow holds a token that acts as them, connecting a repository, which makes it a project, and
reading every project about once a minute into a stored list of what waits for the owner, which
the API answers and the platform's front page counts, and each project's worker key, with the
worker API a worker calls with it, and the worker program that calls it: everything else the tabs
will show arrives with later slices.

## What it serves

- `GET /devflow/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what Dev flow depends on.
- `GET /devflow/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.
- `GET /devflow/api/status`, `{"code_host": "fake", "github": {"state": "none", "login": null}}`:
  which code host Dev flow talks to, `fake` or `github`, see "The code host", and the caller's
  GitHub link, its `state` `none`, `linked` or `needs_relink`, with the `login` it was linked as,
  null with `none`, see "Linking GitHub". The page reads it to show its banner and the link.
  Without a valid token, `401` with an empty body.
- `POST /devflow/api/github/link`, `{"authorize_url": "..."}`, starts linking the caller's GitHub
  account, and `DELETE /devflow/api/github/link`, `204`, unlinks it. Without a valid token, `401`
  with an empty body.
- `GET /devflow/api/github/repositories`, what the caller can connect, and
  `GET /devflow/api/projects`, `POST /devflow/api/projects` and
  `DELETE /devflow/api/projects/{id}`, their projects, see "Connecting a repository". Without a
  valid token, `401` with an empty body.
- `GET /devflow/api/waiting`, what waits for the caller as last read, and
  `POST /devflow/api/projects/{id}/refresh`, which reads one of their projects now, see "What
  waits". Without a valid token, `401` with an empty body.
- `GET /devflow/api/waiting/{id}/screenshots/{name}`, one of the screenshots a waiting item's pull
  request shows, as a PNG read from GitHub as the app, see "What waits". Without a valid token,
  `401` with an empty body.
- `POST /devflow/api/waiting/{id}/merge` and `POST /devflow/api/waiting/{id}/send-back`, which
  merge a waiting pull request and send one back with a comment, as the caller on GitHub, see
  "Acting from the page". Without a valid token, `401` with an empty body.
- `POST /devflow/api/projects/{id}/worker-key`, which makes a project's worker key and answers it
  once, and `GET /devflow/api/projects/{id}/workers`, the workers that have said hello with it,
  see "Workers". Without a valid token, `401` with an empty body.
- `GET /devflow/api/claude-login`, `PUT /devflow/api/claude-login` and
  `DELETE /devflow/api/claude-login`, whether the caller's Claude login is set and when it is due,
  giving or replacing it, and removing it, see "Claude login". None ever answers the token. Without
  a valid token, `401` with an empty body.
- `POST /devflow/api/worker/hello` and `POST /devflow/api/worker/jobs/next`, the worker API, which
  takes a project's worker key in `Authorization: Bearer dfw_…` and never a user's token, see
  "Workers". Without a key the project has now, `401` with an empty body.
- `GET /devflow/api/display`, what the platform's front page shows of Dev flow,
  `{"waiting": 2, "stopped": 1, "projects": 1, "stale": false, "needs_relink": false, "claude_login": "ok"}`:
  the items of kind `waiting` and of kind `stopped` that are not sent back, how many projects the
  caller has connected, whether any of them is stale or failing, whether their GitHub link has
  expired, and where their Claude login stands, `none`, `ok`, `due_soon` or `expired`, as
  `GET /devflow/api/claude-login` says it, which the core says apart as it does the link.
  It reads the same stored picture as the waiting list, never GitHub, so it answers well inside the
  core's deadline, and it sends no words or order meant for display. The core calls it with the
  user's token in the `Authorization` header; `contracts/display/devflow.json` pins its shape, see
  "What a feature exposes for the front page" in `Core/docs/core-architecture.md`. Without a valid
  token, `401` with an empty body.
- `GET /devflow/api/attention`, `{"attention": true}` or `{"attention": false}`, Dev flow's answer
  to the nav bar, which draws the dot on Dev flow's link while it is true: true when the caller has
  an item, waiting or stopped, that is not sent back, or a GitHub link that has expired, which is
  what the waiting list's `count` counts, or a Claude login `due_soon` or `expired`, which it does
  not, since the login is not on that tab. It reads the same stored picture and the login, never
  GitHub. Both
  registries name it as Dev flow's `attention`; the contract every feature's answers to is "The
  attention endpoint" in `High-level/docs/feature-contract.md`. Without a valid token, `401` with an
  empty body.
- `GET /devflow/github/callback`, where the code host sends the browser back, answering `303` to
  `/devflow/projects`, or to `/devflow/projects?link=failed` when it did not link. Without a valid
  token, `303` to the refresh endpoint as a page answers, see "Linking GitHub".
- In the fake mode only, the fake's addresses under `/devflow/api/fake/`, see "Staging a world",
  its sign in page, `/devflow/fake-github/authorize`, and its install page,
  `/devflow/fake-github/install`, see "The fake".
- `GET /devflow`, `/devflow/projects` and `/devflow/projects/{id}`, a project's page, with any
  query, the page: `web/dist/index.html`, revalidated on every load; `{id}` must be a UUID. Without a valid token each answers `303` to
  `/auth/refresh?return_to=<the path and query as received, URL encoded>`.
- `GET /devflow/assets/*`, the page's built files from `web/dist/assets/`, without a token, as
  review and the brain serve their own: they are the same for everyone and hold no data.

Everything else is `404`, and any request that changes state from another origin is `403`, before
routing, but a worker route's, see "How Dev flow authenticates a request" below. An error the service did not plan for is
an RFC 9457 problem document, `application/problem+json` with `status`, `title` and `detail`,
through ASP.NET Core's `AddProblemDetails`, as review's are.

The routes under `/devflow/api/` are pinned by `contracts/devflow/openapi.json`, see "The schema"
below.

## The pages

`web/`, a frontend like any other, a `web` folder under its service, so CI and the agents gate and
build it with the rest, see `design/ui/README.md`. It is review's stack at its versions,
`review/web/package.json`, React Query among them, and depends on the components as
`"@platform/ui": "file:../../design/ui"`. Its `index.html`
includes the nav's lines from "Navigation" in `High-level/docs/platform-architecture.md`, with
`<platform-nav current="devflow">`, and its content sits in `<main class="page">`.

The page is an `h1`, "Dev flow", a line under it, "What your agents built, and what waits for
you.", then the package's `Tabs`, one per section, each with its own address:

| Section         | Address             |
| --------------- | ------------------- |
| Waiting for you | `/devflow`          |
| Projects        | `/devflow/projects` |

The page names its tab in the top bar's crumb, "Dev flow / " and the tab's name, by setting the
`crumb` attribute of its `<platform-nav>` whenever the tab changes.

Above the heading, in the fake mode, a strip says "Fake GitHub. Nothing here is real.", in the
attention style of "Needs you banner" in `design/DESIGN.md`, tokens only, and is not dismissible. It
is `src/banner.tsx`, the page's own component, since no other feature needs it, and it reads
`GET /devflow/api/status` once as the page loads; before the answer, with `github`, or when the
status cannot be read, it shows nothing.

The Projects tab shows the GitHub link above its list, `src/projects-link.tsx`, as the package's
`Card` titled "GitHub": with nothing linked, "Link your GitHub account to connect repositories and
to merge and comment as yourself." and a primary "Link GitHub", which posts to
`/devflow/api/github/link` and sends the browser to the address it answers; linked, "Linked as
@<login>" and a ghost "Unlink" behind the package's `Dialog`, "Unlink GitHub?"; and with
`needs_relink`, the card's attention style, "Your GitHub link has expired." and "Link again". At
`?link=failed`, or when starting fails, it says "GitHub was not linked. Try again." as an alert.
Before the status answers it shows nothing.

Under the link is the Claude login, `src/claude-login.tsx`, the package's `Card` titled "Claude
login", read from `GET /devflow/api/claude-login` as the tab loads: "Agents run on your
subscription with this. Make it with `claude setup-token`.", then where it stands, "Not set yet.",
or "Set on <date>, renew by <date>.", the dates in the reader's own zone. From a month before it is
due the card takes the attention style and says "Set on <date>. Renew it by <date>: agents cannot
run once it runs out.", and once past it "Set on <date>. It ran out on <date>: agents cannot run
until you replace it.", in `--accent-text` at weight 600. Under that, a password field labelled
"Claude token" and a secondary "Save", or "Replace" once one is set, enabled only while the field
holds something, and, once set, a ghost "Remove" behind a destructive `Dialog`, "Remove the Claude
login?". Saving empties the field whatever the answer; a token the service refuses says "That is
not a setup token. Make one with claude setup-token and paste all of it." on the field, and any
other failure says so as an alert. The page never reads the token back, not even a part of it.
The buttons are secondary, since the tab's one primary is "Link GitHub" or "Connect a repository".

Under it are the projects, `src/projects.tsx`. A primary "Connect a repository", enabled only
while GitHub is linked, opens "Choose a repository": the repositories the app reaches for the
owner, read from `GET /devflow/api/github/repositories`, as one package `Table` per account, named
and headed by it, each repository a row with "Connect", or a `Badge` "Connected" when it is a
project already; with the app installed nowhere he can reach, "The GitHub App is not installed on
any repository you can reach." with a link "Install it on GitHub" to the answer's `install_url`.
Connecting closes it and reads the projects again. The projects are a `Table` named "Projects",
each row "<owner>/<name>" over "Read the way this repository's runner writes." with a ghost
"Disconnect" behind a destructive `Dialog`, "Disconnect <owner>/<name>?", which says Dev flow
forgets the project and everything it read for it and that nothing changes on GitHub; with none,
and the choice closed, the `EmptyState` "No project connected yet". A read, a connect or a
disconnect that fails says so as an alert. Each row's "<owner>/<name>" is a link to the project's
page.

A project's page, `src/project.tsx`, is at `/devflow/projects/<id>`, under the Projects tab, which
stays selected there. Its header is a link "Back to Projects" over the repository's
"<owner>/<name>" as a section heading; a project the caller does not have is the `EmptyState`
"There is no such project". Under it is the Workers panel, the package's `Card` titled "Workers",
read from `GET /devflow/api/projects/{id}/workers` as the page loads: a line saying a worker holds
the project's key and asks Dev flow for work with it, then the key, then the workers. With no key,
"It has no key yet." and a primary "Make a key". Making one shows it once, in the region "The new
key", on an inset in the mono font, with "Shown once. Copy it now: Dev flow keeps only a hash of it
and cannot show it again." and a secondary "Copy the key", which copies it and then reads
"Copied"; a copy that fails says so as an alert. The page never reads the key back: after a
reload it says "Its key was made <date and time>.", and the button is a secondary "Make a new
key", which opens a destructive `Dialog`, "Make a new key?", saying the old key stops working at
once and every worker holding it is refused until it is given the new one, and makes nothing until
it is confirmed. The workers are the list "Workers", each an inset row, by when it first said
hello: its name, "Recipe <version> on <base image>" in the mono font, and "Last seen <time> ago",
or, after ten minutes without a word, "Not seen since <date and time>", in `--text` at weight 600;
with none, "No worker has said hello yet." The commands to start a worker arrive with a later
slice.

Waiting for you, `src/waiting.tsx`, is one list of what waits for the owner across his projects,
read from `GET /devflow/api/waiting`, see "What waits", through React Query: as the page loads,
every 60 seconds while the page is visible, and when the window regains focus. Its tab carries the
answer's `count` as a badge once it is more than none. Its parts, top to bottom:

- With `needs_relink`, "Your GitHub link has expired. You can read, but not merge or comment." in
  the attention style, with a link "Go to Projects".
- The age of the picture: for each project that is stale, "<owner>/<name> has not been updated for
  <time>", or "has not been read yet" when no read has succeeded, and for each that is failing,
  "<owner>/<name>: GitHub could not be read, showing what was last seen", in the attention style;
  when every project is fine, one muted "Updated <time> ago" from the oldest `last_success_at`. A
  secondary "Refresh" beside it posts to every project's refresh route and then reads the list
  again.
- Three groups, in this order, each a section heading in the heading font's capitals with its
  count, and shown only with items: "Pull requests waiting", items of kind `waiting` not sent
  back; "Stopped", kind `stopped` not sent back; and "Sent back, the runner has it", every item
  sent back, muted, and not in the tab's count. Within a group, by `<owner>/<name>` and then
  number.
- An item is the package's `Card`, titled by the pull request, its status label on the right:
  "Waiting for you" in the needs you tone, with the card's attention outline, "Stopped" or "Sent
  back". Under the title, `<owner>/<name> #<number>` in the mono font, then the summary. A waiting
  item with decisions for the owner shows "The agent decided", each decision on an inset with what
  was chosen, "Instead of" and the alternative, and "Because" and the reason, each when there is
  one, and "and <n> more it did not need you for" when the body records others; without
  decisions, "Why it waits" and the runner's reason, and a stopped item "Why it stopped". A
  waiting item's reviewer points follow as "The reviewer". Then, with screenshots,
  `src/screenshots.tsx`, "Screenshots": each pair under its name in the mono font, the desktop
  one and the phone one as thumbnails at most 160px high, loaded lazily and read from the
  screenshot route, never from the body's addresses, each image with its name as its alternative
  text; a name with only one of the two shows that one, and an image that fails to load shows
  "<name> could not be loaded". A thumbnail is a button that opens the image in the package's
  `Dialog`, titled by its name, at its natural size in a frame that scrolls, with one "Close".
  Last, its actions, `src/actions.tsx`,
  and a ghost styled link "Open on GitHub" to the pull request, in a new tab with
  `rel="noreferrer"`, offered only when its address is `https`. On a phone it all stacks and the
  actions wrap.
- The actions, before "Open on GitHub": a waiting item has "Merge" and a secondary "Send back"; a
  stopped item a secondary "Comment to run again" and no Merge; an item sent back nothing but the
  link. Only the first waiting item's Merge is primary, the rest secondary, by the design's one
  primary button per view. Merge is disabled, with a line under it saying why, when `can_merge` is
  false, "The checks are not green on this commit yet", or when GitHub is not linked or the link
  has expired, which `GET /devflow/api/status` says; the comment buttons are disabled for the
  link, with the same line. Merge opens the package's `Dialog`, "Merge #<number> into main?", with
  the title, the head commit's first seven characters in the mono font, and, with decisions for
  the owner, "This confirms <n> decisions the agent made.", and sends nothing until its "Merge" is
  pressed. Send back opens a `TextArea` in the item, labelled "Comment", or "A comment runs it
  again." for a stopped item, with "Send", disabled while the text is empty, and "Cancel". A merge
  ends in a toast, "Merged #<number>", and the item leaves the list; sending back in "Sent back
  #<number>", and the item moves to "Sent back, the runner has it". Either way the list is read
  again, and a refusal is shown inside the item, in words, as an alert. The page is wrapped in the
  package's `ToastProvider`.
- With no project, the `EmptyState` "Nothing waits for you", "Connect a repository under Projects
  to see what waits for you."; with projects and no item, "Nothing waits for you", "Your agents
  have nothing for you to decide."; and a list that cannot be read says so as an alert.

Every text in an item came from the code host and is shown as text. The summary, the decisions,
the reviewer's points and the reason go through `src/format.tsx`, which turns `` `code` `` into a
code element and `**bold**` into a strong element, as React elements, and leaves the rest as
written; no Markdown library, and no HTML is ever inserted, see "Text read from a code host is
data" in `docs/devflow-architecture.md`.

"Running now" and "Usage and history" join with the parts that build them, never as empty tabs. The address is the state,
through the History API and no router library (`src/address.ts`): a tab pushes its section's
address, Back and Forward come back as `popstate`, and an address that names no section shows a
not found state under the tabs; a project's name pushes its page's address, and "Back to Projects"
the tab's. The list of sections and the project page's address live in `src/sections.ts`, and the
addresses the service answers the page for in `src/DevFlow/Pages/PageEndpoints.cs`; the two change
together.

`src/api/client.ts` is the page's one way to call Dev flow: every call goes to `/devflow/api/` with
the session cookie, a `401` is refreshed once, shared by every call that got one, by posting to
`/auth/refresh` and repeating the call, and a second `401` sends the browser to
`/auth/refresh?return_to=<path>`, as review's client does. Its GETs are typed from
`src/api/schema.d.ts`. The page reads the status once as it loads, in `src/status.ts`, for the
banner and the link, and again after unlinking; its other calls are starting a link, unlinking,
the projects' four, reading what waits and refreshing a project, merging and sending back, a
project's workers and making its key, and reading, giving and removing the Claude login, which
`src/app.tsx` gives the tabs, so a test gives its own,
with its own clock.

## The schema

`Microsoft.AspNetCore.OpenApi` generates the OpenAPI document of the API at
`GET /devflow/api/openapi.json`, needing no token, only when `DEVFLOW_EXPOSE_OPENAPI=1`, so the
running service serves it nowhere. `ContractTests` writes it with keys sorted, a two space indent
and a trailing newline and checks that it equals `contracts/devflow/openapi.json` byte for byte,
see `contracts/README.md`. Every route has a summary and named response types, so a change to
either is a diff to review. After changing a route, from this directory:

```zsh
UPDATE_CONTRACTS=1 dotnet test --filter "FullyQualifiedName~ContractTests"
cd web && npm run generate:api
```

The second line writes `web/src/api/schema.d.ts` from the file with `tools/generate-schema.mjs`,
review's tool, and `src/api/schema.test.ts` fails when it is stale. The app is named for its own
assembly, so the document is the same under the tests' host as in the service.

## Running it

From this directory, with the .NET SDK `global.json` pins installed, the database migrated (see
"Migrating"), and the page built first, since Dev flow refuses to start without
`web/dist/index.html`, naming it:

```zsh
(cd web && npm ci && npm run build)
DEVFLOW_APP_DATABASE_URL="postgres://devflow_app:<password>@127.0.0.1:5432/devflow" \
  dotnet run --project src/DevFlow -c Release --no-launch-profile
```

`dotnet run` runs it from this directory, where `web/dist` is, which `RunWorkingDirectory` in
`src/DevFlow/DevFlow.csproj` sets; the Web SDK would otherwise run it from the project's folder. Its
entry in `infra/services.json` starts it the same way, with `DEVFLOW_LISTEN_ADDR` set to the host
and port the manifest gives, and `DEVFLOW_APP_DATABASE_URL` built from `infra/.env` by
`scripts/agent/services.sh`. Port 8084 sits beside review's 8083, and the Caddyfile's upstream for
`/devflow` points at it. In the hosted copy it is the `devflow` container, built by `Dockerfile`
here, see `infra/hosted/README.md`.

Its tests, the steps `.github/workflows/dotnet.yml` runs:

```zsh
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore -c Release
DEVFLOW_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" \
DEVFLOW_APP_DATABASE_URL="postgres://devflow_app:$(grep '^DEVFLOW_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/devflow" \
  dotnet test --no-build --no-restore -c Release
```

The tests that need a database each get one of their own, from `Databases.DevFlow` in
`tests/DevFlow.Tests/Harness/TestDatabase.cs`, the harness `PlatformData.Testing` holds, see
`libs/dotnet/PlatformData/README.md`, through the Postgres superuser's string,
`DEVFLOW_TEST_ADMIN_DATABASE_URL`, which only the test step holds, and as `devflow_app` through
`DEVFLOW_APP_DATABASE_URL` pointed at their copy. Both are required, and unset is a failure, never
a skip. Tests that need no database take none.

and the page's, from `web/`, the steps `.github/workflows/web.yml` runs for every frontend:

```zsh
npm ci
npx biome ci .
npx tsc --noEmit
npx vitest run
npm run build
```

`packages.lock.json` is committed beside each project; after changing a package, run
`dotnet restore` without `--locked-mode` once to update it, and commit the change. The default
`.editorconfig` wants no final newline in a C# file, and `dotnet format --no-restore` fixes one.

## Settings

Dev flow reads its settings from the process environment, once, at startup, into one immutable
`DevFlowSettings`, in `src/DevFlow/Settings/DevFlowSettings.cs`. It loads no `.env` file. Unset
means the default, and a value that is set must parse, or Dev flow refuses to start, naming the
setting, rather than quietly using the default. The rules are review's, written again rather than
shared, since no service imports from another's directory.

- `DEVFLOW_LISTEN_ADDR`, default `127.0.0.1:8084`: where Kestrel listens, a host and a port, an
  IPv6 host in brackets.
- `DEVFLOW_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `DEVFLOW_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.
- `DEVFLOW_EXPOSE_OPENAPI`, default `0`: `1` maps the schema's route, for the contract test and
  regenerating the file, see "The schema". Nothing else is accepted.
- `DEVFLOW_APP_DATABASE_URL`, required, with no default: the database as `devflow_app`,
  `postgres://devflow_app:<password>@<host>:<port>/devflow`, parsed by `DatabaseUrl.Parse` from
  `libs/dotnet/PlatformData/`. Unset, or anything else, stops Dev flow at startup, naming the setting
  and never repeating the value, since it holds a password, which `DevFlowSettings` also leaves out
  of what it prints.
- `DEVFLOW_OWNER_DATABASE_URL`, the same form as `devflow_owner`: read by `migrate` alone, never
  by Dev flow while it serves, see "Migrating".
- `DEVFLOW_CODE_HOST`, default `fake`: `fake` or `github`, nothing else, and set but empty is refused
  like any value that does not parse. It decides which code host Dev flow is built with, whether
  the fake's addresses exist and the page shows its banner, whether the vault needs a key, and
  whether the four GitHub settings below are read; see "The code host".
- `DEVFLOW_VAULT_KEY`, the vault's current key, `<id>:<base64 of 32 bytes>`, see "The vault". Unset
  or empty, Dev flow makes one in memory with the fake code host and refuses to start with
  `github`. A key that does not parse stops it, naming the setting and never repeating the value.
- `DEVFLOW_VAULT_OLD_KEYS`, unset or empty for none: keys rows may still be sealed with, comma
  separated in the same form, each with an id of its own that is not the current key's.
- The GitHub App's four, in `src/DevFlow/Settings/GitHubSettings.cs`, each required with
  `DEVFLOW_CODE_HOST=github` and ignored with `fake`, whatever their values; empty is unset. A
  missing or malformed one stops Dev flow at startup, naming the setting and never repeating its
  value, and `DevFlowSettings` prints the client id and the slug alone.
  - `DEVFLOW_GITHUB_CLIENT_ID`: the app's client id, ASCII letters, digits and dots, such as
    `Iv23li` followed by the rest. The app's tokens carry it as `iss`.
  - `DEVFLOW_GITHUB_CLIENT_SECRET`: a client secret of the app, printable ASCII without spaces.
    Sent when a person's code or refresh token is exchanged for their tokens, see "Linking
    GitHub".
  - `DEVFLOW_GITHUB_PRIVATE_KEY_FILE`: the path of the app's private key, read once at startup with
    `RSA.ImportFromPem`. It must be an RSA private key of at least 2048 bits in PEM, unencrypted,
    either `BEGIN RSA PRIVATE KEY`, as GitHub gives it, or `BEGIN PRIVATE KEY`.
  - `DEVFLOW_GITHUB_APP_SLUG`: the app's name in its address, `github.com/apps/<slug>`, lowercase
    letters, digits and inner hyphens. It makes `install_url`, where the owner installs the app,
    see "Connecting a repository".
- `DEVFLOW_POLL_SECONDS`, default `60`: how often the loop reads every project, in whole seconds,
  ASCII digits alone, from `10` to `86400`, a day; or `0`, which turns the loop off, as the
  service's own tests do, see "What waits". Empty is unset, so the hosted copy passes it empty when
  its `.env` leaves it out and the default stands.

The defaults are right on the Mac mini and in CI, so neither sets them, the code host's fake
included, except the listen address,
which the manifest's `start` sets, and the database URLs, which `scripts/agent/services.sh` and
`.github/workflows/dotnet.yml` build. The hosted copy sets the four it needs to serve and passes the
vault's two, the code host's mode and the GitHub App's settings in `infra/hosted/compose.yaml`, from
its `.env`, and the owner's URL only on `devflow-migrate`. The app's key file is mounted read only
at `/run/secrets/github-app.pem`, which is the path Dev flow is given; the `.env` names the file on
the machine. Its `.env.example` names the mode `github` and lists the app's four, so a release
refuses an env file without them, and `check.sh`'s throwaway copy sets `fake` with placeholders
and an empty key file, which the fake never reads.

## The database

Dev flow has a database of its own, `devflow`, in the one Postgres in `infra/`, created with its two
roles by `infra/postgres/initdb/devflow.sh`, see "The databases" in `infra/README.md`. Why a
database per service, and why the roles are split, is in "Databases, migrations, and running it"
and "Multi user, and how isolation is enforced" in `High-level/docs/platform-architecture.md`.
Everything it does with it is the shared library's, `libs/dotnet/PlatformData/`, whose README says
what it holds; what is Dev flow's own is below.

- **`devflow_owner`** owns the tables and runs the migrations. Nothing serving connects as it.
- **`devflow_app`** is what Dev flow serves as. It owns nothing, so row level security applies to
  it, and `migrations/0001_default_privileges.sql` grants it `SELECT`, `INSERT`, `UPDATE` and
  `DELETE` on every table the owner creates and `USAGE` on every sequence, never `TRUNCATE`, and
  takes `CREATE` on `public` from everyone but its owner, as review's first migration does.

Its tables are the vault's, `secrets`, from `migrations/0002_secrets.sql`, see "The vault", the
GitHub link's two, `github_sign_ins` and `github_accounts`, from
`migrations/0003_github_link.sql`, see "Linking GitHub", and `projects` with `poll_targets`, from
`migrations/0004_projects.sql`, see "Connecting a repository", and `waiting_items` with
`project_reads`, from `migrations/0005_waiting_items.sql`, see "What waits", and `worker_keys`,
`worker_key_lookup` and `workers`, from `migrations/0007_workers.sql`, see "Workers".
`poll_targets` and `worker_key_lookup` are the two tables without row level security, which
`devflow_app` may only read.

### Migrating

Migrations are plain SQL files in `migrations/`, named `NNNN_snake_description.sql`, forward only,
embedded in the assembly by `src/DevFlow/DevFlow.csproj`. They run only as a command, never at
startup:

```zsh
DEVFLOW_OWNER_DATABASE_URL="postgres://devflow_owner:<password>@127.0.0.1:5432/devflow" \
  dotnet run --project src/DevFlow -c Release --no-launch-profile -- migrate
```

`src/DevFlow/Commands.cs` runs the shared `MigrateCommand` with the name `devflow`, so its lock's
key is the first 8 bytes of SHA-256 of `devflow.migrate`, reading `DEVFLOW_OWNER_DATABASE_URL` and
nothing else. With no argument Dev flow serves, and any argument but `migrate` is refused with the
usage. On the Mac mini and in CI the manifest's `migrate` runs it; in the hosted copy,
`devflow-migrate`.

### The door

Every query goes through `UserDb.InUserTransaction(userId, work, cancellationToken)`, the shared
library's door, which sets `devflow.user_id` for that transaction alone, but two, each through
the door's `WithoutUser`, a transaction on behalf of nobody, to which every guarded table is
empty: the loop lists `poll_targets` and then reads each project as its user, see "What waits",
and a worker's request looks its key's hash up in `worker_key_lookup` and then does everything as
that project's user, see "Workers". Dev flow registers one
`UserDb`, opened at startup in `src/DevFlow/Commands.cs` with the name `devflow`,
`DevFlowSettings.Service`, as `devflow_app`, so a database it cannot reach, or a wrong password,
stops Dev flow there, naming `DEVFLOW_APP_DATABASE_URL` and never its password. It opens no
connection of its own, which `IsolationTests.OnlyTheDoorOpensConnections` checks by reflection.

Every scoped table has `user_id uuid NOT NULL`, row level security enabled
and forced, and one policy using `UserDb.GuardFor("devflow")` for `USING` and `WITH CHECK`.
`tests/DevFlow.Tests/IsolationTests.cs` proves the policy on a probe table, and fails for any table
a later migration adds to `public` without exactly that one policy, `schema_migrations` aside and
the two tables left unguarded on purpose, `poll_targets` and `worker_key_lookup`, which the test
names; Dev flow has no other path around the policies.

## The vault

The vault keeps a secret for a user, such as their GitHub token, in the database, sealed so that a
copy of the database does not reveal it. It is `Vault`, in `src/DevFlow/Vault/`, one instance the
app registers beside the door, with three operations that each take the door's transaction, so a
secret is only ever touched as its user:

- `Put(transaction, userId, purpose, value, cancellationToken)` seals the value, bytes, and stores
  it, replacing any before it;
- `Get(transaction, userId, purpose, cancellationToken)` gives the value, or null when there is
  none;
- `Delete(transaction, userId, purpose, cancellationToken)` removes it, saying whether there was
  one.

A purpose is a short fixed name the caller chooses, such as `github.access` or `claude-login`,
matching `^[a-z][a-z0-9.-]*$`; anything else is an `ArgumentException`. The hyphen arrived with
`claude-login`, in `migrations/0008_claude_logins.sql`, which changes the table's check with it.

Each row of `secrets` is one user's secret for one purpose: `key_id`, `nonce` and `ciphertext`,
with `updated_at`. A put seals with AES-256-GCM, from .NET's own `AesGcm`, under the current key, a
random 12 byte nonce every time and the 16 byte tag appended to the ciphertext. The associated data
is the UTF-8 of `devflow:v1:<user id>:<purpose>`, so a row moved to another user or purpose does
not open, and neither does one with any byte changed. The table is guarded like every scoped one,
so a user's transaction never sees another's rows. A get that cannot open its row throws
`VaultException`, naming the purpose and, for a key that is not configured, the key's id.

Nothing prints a value or a key: not a log line, not an exception's message, not a problem
document. `DevFlowSettings`, `VaultSettings` and `VaultKey` print key ids alone.

**The key** is `DEVFLOW_VAULT_KEY`, `<id>:<base64 of 32 random bytes>`, the id 1 to 16 lowercase
letters and digits. Make one with:

```zsh
printf 'k1:%s\n' "$(openssl rand -base64 32)"
```

A put records the id of the key it sealed with, and a get opens with the key its row names, the
current one or one listed in `DEVFLOW_VAULT_OLD_KEYS`; a row naming a key that is not configured is
refused with its id. So a key is changed by making a new one current and listing the old one until
every row has been put again, which happens as each secret is next written. Nothing rewrites every
row at once yet.

Without a key, with the fake code host, Dev flow makes a random key in memory at startup and says so
in one line on stderr, without the key: the tests, CI and the runner's stack need nothing set, and
what is sealed there is lost at a restart, which is right for a throwaway stack. With
`DEVFLOW_CODE_HOST=github` a missing key stops startup. The hosted copy's key is in its `.env`, and
the owner's password manager, never in the backups; see `infra/hosted/README.md`.

What it protects against, and what it does not, is in "Security decisions and accepted risks" in
`docs/devflow-architecture.md`.

## The code host

Everything Dev flow asks of a code host goes through one interface, `ICodeHost`, in
`src/DevFlow/CodeHost/`, with plain records for what it answers. Why it sits there, and why the
fake does, is "The code host seam" in `docs/devflow-architecture.md`. It names things as the
development flow's design does, a repository, a pull request, a comment, never as GitHub's API
does. Its read side:

- `OpenPullRequests(user, repository)`: each open pull request, with its number, title, author's
  login, labels, head commit, head and base branch, whether it is a draft, body, web address, when
  it was last updated, and its state;
- `PullRequest(user, repository, number)`: one, whatever its state, open, merged or closed;
- `Comments(user, repository, number)`: an issue's or pull request's conversation comments, oldest
  first, each with id, author's login, body, when it was created and when it was last edited;
- `Statuses(user, repository, commit)`: each context with its state and description;
- `CheckRuns(user, repository, commit)`: each name with its status, conclusion and the slug of the
  app that made it;
- `File(user, repository, path, branch)`: the bytes;
- `Installations(user, userToken)`: the installations of the app the person whose token it is can
  reach, each with its id and the login of the account it is on;
- `Repositories(user, userToken, installation)`: the repositories of one of them the person can
  reach, each a `Repository`; not found for an installation they cannot reach;
- `InstallAddress(origin)`: where a person installs the app, GitHub's page for it, or the fake's
  own under the platform's origin.

The two that take a user's token read as that person, never as the app, so what they list is what
the person may connect; the token comes from `UserTokens.For`, see "Linking GitHub".

Its write side, both as the person and never as the app:

- `Merge(user, token, repository, number, commit)`: merges the pull request with a merge commit
  while its head is `commit`, answering a `MergeResult`: `Merged`, `HeadMoved` when the head is
  another commit, `NotMergeable` with the host's words, `Refused` with the host's status and words,
  or `Unavailable` when it did not answer;
- `Comment(user, token, repository, number, body)`: writes `body`, exactly, in the pull request's
  conversation, answering a `CommentResult`: `Posted`, `Refused` with the status and words, or
  `Unavailable`.

Each takes a `UserAccessToken`, a type of its own, and nothing else that could authorise it. An
installation's token is a string, so passing one to a write does not compile. The type's
constructor is `internal`, and the one code in the service that calls it is `UserTokens`, which
answers `UserToken.Usable` holding it; `GitHubWriteTests.AUserTokenIsMadeOnlyByUserTokens` reads
the compiled code and fails if anything else does. Why, and where the compiler's guarantee ends,
is "Writes are made only with a linked user's token" in `devflow/docs/devflow-architecture.md`.

A `Repository` is the installation it was reached through and its id, which identify it, with its
owner and name carried for display and for GitHub's addresses, which name a repository by them; the
adapter's token is narrowed to the id, so a name that no longer matches it is not found rather than
another repository read. Every operation takes the platform account it is asked for:
the fake keeps a world per account, and the GitHub adapter reads as the app and writes as the
account's linked user. Each read answers a `CodeHostResult<T>`: `Found` with the value, `NotFound`,
or `Unavailable` with when to try again if the host said, and the caller handles all three; nothing
is thrown for an expected condition.

Signing a person in is a second interface beside it in the same folder, `ICodeHostSignIn`, which
both implementations implement, since its reads are as the app and these are as a person:

- `AuthorizeAddress(user, state, challenge, redirect)`: the code host's page to send the browser
  to, carrying the state, the S256 challenge and where to send it back;
- `Exchange(user, code, verifier, redirect)`: the two tokens for the code the browser came back
  with, an access token and a refresh token, with when each expires;
- `Renew(user, refreshToken)`: a new pair, the refresh token taken once;
- `WhoIs(user, accessToken)`: the account the token acts as, its login and numeric id.

Each answers a `SignInResult<T>`: `Granted`, `Refused` when the host does not take what it was
given, or `Unavailable` when it did not answer. The two failures are kept apart because a refused
renewal ends a link and an unavailable one must not. The PKCE challenge is `Pkce.Challenge`, the
base64url of the verifier's SHA-256, RFC 7636's S256.

`DEVFLOW_CODE_HOST` chooses the implementation when the app is built, in `DevFlowApp.Build`. `fake`
is `FakeCodeHost`; `github` is `GitHubCodeHost`, in `src/DevFlow/CodeHost/GitHub/`, registered by
`AddGitHubCodeHost` with its settings. A test can still hand the app a code host of its own, which
`Build` keeps, as the test harness's `codeHost` does.

### The GitHub adapter

`GitHubCodeHost` reads GitHub's REST API as Dev flow's GitHub App. How it signs in, why its keys
can only read and what it does with GitHub's failures are "GitHub" in
`docs/devflow-architecture.md`. Each operation is one address, relative to
`https://api.github.com`, a constant:

| Operation | Address |
|---|---|
| `OpenPullRequests` | `GET /repos/{owner}/{repo}/pulls?state=open&per_page=100` |
| `PullRequest` | `GET /repos/{owner}/{repo}/pulls/{number}` |
| `Comments` | `GET /repos/{owner}/{repo}/issues/{number}/comments?per_page=100` |
| `Statuses` | `GET /repos/{owner}/{repo}/commits/{sha}/statuses?per_page=100`, the first of each context, which is its latest |
| `CheckRuns` | `GET /repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100` |
| `File` | `GET /repos/{owner}/{repo}/contents/{path}?ref={branch}`, with `Accept: application/vnd.github.raw+json` |
| `Installations` | `GET /user/installations?per_page=100`, with the user's token, each installation's `id` and its account's `login`, or an enterprise's `slug` |
| `Repositories` | `GET /user/installations/{id}/repositories?per_page=100`, with the user's token |
| `InstallAddress` | `https://github.com/apps/<DEVFLOW_GITHUB_APP_SLUG>/installations/new`, on GitHub's site |
| `Merge` | `PUT /repos/{owner}/{repo}/pulls/{number}/merge` with `{"sha": "<commit>", "merge_method": "merge"}`, with the user's token: `200` merged, `409` head moved, `405` not mergeable, any other status refused with it |
| `Comment` | `POST /repos/{owner}/{repo}/issues/{number}/comments` with `{"body": "<text>"}`, with the user's token: `201` posted, any other status refused with it |

The two read with the user's token follow every page as the others do, and nothing of them is
remembered for a conditional request, since an answer read with one person's token is never one to
give another. The two writes never ask for an installation token, are never retried, and keep at
most 500 characters of GitHub's `message` from a refusal.

Signing a person in, in `GitHubCodeHost.SignIn.cs`, is GitHub's web flow exactly as GitHub
documents it:

| Operation | Address |
|---|---|
| `AuthorizeAddress` | `https://github.com/login/oauth/authorize` with `client_id`, `redirect_uri`, `state`, `code_challenge` and `code_challenge_method=S256` |
| `Exchange` | `POST https://github.com/login/oauth/access_token`, a form of `client_id`, `client_secret`, `code`, `redirect_uri` and `code_verifier`, with `Accept: application/json` |
| `Renew` | the same `POST`, a form of `client_id`, `client_secret`, `grant_type=refresh_token` and `refresh_token` |
| `WhoIs` | `GET /user` on the API, with the user's token |

GitHub answers a token request it refuses with `200` and an `error` field. Each error is refused,
but `incorrect_client_credentials` and `redirect_uri_mismatch`, which say the app's own settings are
wrong, are unavailable, as is any answer that is not a success and one without expiring tokens,
which an app without "Expire user authorization tokens" gives: ending a person's link would fix
none of them. `WhoIs` is refused on `401`.

Every request is sent with one `HttpClient` named `github` from `IHttpClientFactory`, with a 20
second timeout, `X-GitHub-Api-Version: 2022-11-28`, `User-Agent: platform-devflow`, and `Accept:
application/vnd.github+json` unless it reads a file, and the client's logs leave every header's
value out. GitHub's answers are read into records private to the adapter, in
`GitHubCodeHost.Answers.cs`; a field GitHub always sends must be there, and a word the adapter does
not know, such as a new state, makes the answer one that does not parse. An author whose account
was deleted is `ghost`, as GitHub shows it, and a body GitHub sends as null is empty. Waiting,
requested and pending check runs are queued.

Its tests are in `tests/DevFlow.Tests/GitHub/`, against GitHub's real answers recorded in
`contracts/github/`, and for signing in against its documented answers written there by hand, see
`contracts/README.md`, with a stub `HttpMessageHandler` in place of the
network and a `FakeTimeProvider`: no test reaches GitHub. They build it as Dev flow does, through
`AddGitHubCodeHost`, and replace the client's handler, never its address.

### The fake

`FakeCodeHost` keeps a world per platform account, in memory, lost at a restart: installations,
their repositories, and each repository's pull requests, conversations, commits with their statuses
and check runs, and files. Per account, since every acceptance test registers its own account and
they all run against one Dev flow. A world is one JSON document whose shape is the interface's
records, so staging something is writing what the interface would answer:

```json
{
  "installations": [
    {
      "id": 1,
      "account": "Adamniels",
      "repositories": [
        {
          "id": 1001, "owner": "Adamniels", "name": "Personal-platform",
          "pull_requests": [ { "number": 304, "title": "The vault", "author": "mac-mini-agent",
            "labels": ["owner-merge"], "head_commit": "<40 hex>", "head_branch": "272-the-vault",
            "base_branch": "main", "draft": false, "body": "", "url": "https://github.com/...",
            "updated_at": "2026-11-15T09:30:00+00:00", "state": "open" } ],
          "conversations": [ { "number": 304, "comments": [ { "id": 1, "author": "Adamniels",
            "body": "...", "created_at": "...", "updated_at": "..." } ] } ],
          "commits": [ { "id": "<40 hex>",
            "statuses": [ { "context": "review-ok", "state": "success", "description": null } ],
            "check_runs": [ { "name": "ci-ok", "status": "completed", "conclusion": "success",
              "app": "github-actions" } ] } ],
          "files": [ { "path": "AGENTS.md", "branch": "main", "content": "<base64>" } ]
        }
      ]
    }
  ]
}
```

A world may also name who the account is at the fake, `"user": {"login": "octo-owner", "id":
70000001}`, the one person its sign in page shows and its tokens act as; it is the one field a
world may leave out, `octo-owner` with that id when it does, and it is left out when it is read
back.

It is read strictly, by `FakeJson` in `src/DevFlow/CodeHost/FakeWorld.cs`: every field present but
`user`, null only where the record allows it, no field the records do not have, numbers as numbers, and states
as their snake case words, `open`, `in_progress`, `timed_out`. What identifies a thing is listed
once: an installation's id, a repository's id within it, a pull request's or a conversation's
number, a commit's id, and a file's path and branch. An installation's `account` is the login of
the account it is on, which the list to choose from groups by. A pull request's number reaches its
conversation, empty unless one is listed; a commit that is not listed is not found.

The fake signs a person in too, in `FakeCodeHost.SignIn.cs`, per account and apart from its world,
so staging a world again signs nobody out. Its sign in page, `GET /devflow/fake-github/authorize`,
mapped only in the fake mode and under the `Page` policy, so it knows whose world to read, is a
plain page that names itself "Fake GitHub", shows the login of the world's user, and has
"Authorize" and "Deny", a form posted back to the same address, which sends the browser to the
redirect address with a code and the state, or with `error=access_denied` and the state. It sends
the browser only to Dev flow's own redirect address, and answers `400` to anything else, as GitHub
sends it only to a registered one. Its codes last ten minutes and are taken once, bound to the
challenge, and a verifier the challenge was not made from is refused. Its tokens are random and
last as long as GitHub's, eight hours and six months, and a refresh token is taken once, so a test
of renewal means something. With the account set unavailable, signing in is unavailable too.

Listing what a person can connect reads the world's installations and their repositories, and
only with the account's current access token from its sign in, as GitHub takes only the person's
own: another token, or one that has expired, is unavailable, as the adapter answers GitHub's `401`.
Its install page, `GET /devflow/fake-github/install`, mapped only in the fake mode and under the
`Page` policy, is a plain "Fake GitHub" page that installs nothing and says a test stages
installations in its world.

Its writes, in `FakeCodeHost.Writes.cs`, take only the account's current access token too, and
answer another as GitHub answers `401`, refused with "Bad credentials". A merge is refused with
`404` for a pull request the world does not hold, is not mergeable unless the pull request is open,
answers head moved unless its head is the commit named, and otherwise marks it merged in the world.
A comment is refused with `404` for a number with neither a pull request nor a conversation, and is
otherwise added to the end of the conversation, by the login the token acts as, at the fake's now.
Each write taken is recorded, with whose token made it, and the record survives staging a world
again, so a test asserts who wrote and that nothing was written as the app.

### Staging a world

In the fake mode only, under the `Api` policy and the origin check like every route, each acting on
the caller's own world, and left out of `contracts/devflow/openapi.json`, since they are not part
of what Dev flow is:

- `PUT /devflow/api/fake/world` replaces the caller's world with the document, `204`. One that does
  not parse is `400`, a problem document titled "The world is malformed" whose detail starts with
  the field, such as `installations[0].repositories[0].name: is missing`, and the world before it is
  kept.
- `GET /devflow/api/fake/world` reads it back, the same document; `{"installations": []}` before
  anything is staged.
- `POST /devflow/api/fake/unavailable` with `{"unavailable": true}` makes every operation for the
  caller answer unavailable, without a time to try again, and `false` makes it answer again. The
  world is kept.
- `POST /devflow/api/fake/expire-token` ends the caller's access token now, at the fake and in Dev
  flow's record of when it expires, so the next use of it renews, `204`. The refresh token still
  works.
- `POST /devflow/api/fake/revoke-token` ends both of the caller's tokens at the fake, as revoking
  the app on GitHub does, and the access token in Dev flow's record, so the next use renews, the
  renewal is refused and the link becomes `needs_relink`, `204`.
- `POST /devflow/api/fake/claude-login-age` with `{"days": 340}` moves when the caller's Claude
  login was given to that many days before now, so a test sees it due without waiting a year,
  `204`; `404` without a login, and `400` for a negative or malformed number.
- `GET /devflow/api/fake/writes` lists the merges and comments the fake took for the caller,
  oldest first, each `{"kind": "merge", "repository": 1001, "number": 304, "commit": "<40 hex>",
  "method": "merge", "body": null, "by": "octo-owner"}`, or with `kind` `comment`, the `body`, and
  `commit` and `method` null.

In the GitHub mode none of them exists, `404`. An acceptance test stages a world through the
`devflow_world` fixture, which puts it through the page's own session, with `e2e/devflow_worlds.py`
to build it; see "Fixtures" in `e2e/README.md`. A C# test calls `FakeCodeHost.Replace` and
`SetUnavailable`, or sends the same requests through the harness.

## Linking GitHub

The owner links his GitHub account once, on the Projects tab, and from then on Dev flow holds a
token that acts as him on GitHub, renewed by itself, which he can remove again. Why it acts as him,
and why it is safe, is "Acting as the owner" in `docs/devflow-architecture.md`. It is
`GitHubLink` and `UserTokens`, in `src/DevFlow/Link/`, over the two tables of
`migrations/0003_github_link.sql`, both guarded like every scoped table:

- `github_sign_ins`: a link started and not yet returned from, `user_id`, `state_hash`, the
  SHA-256 of the state and never the state, `verifier`, the PKCE verifier, and `created_at`, with
  the primary key `(user_id, state_hash)`;
- `github_accounts`: the one GitHub account a user has linked, `user_id` its primary key, `login`,
  `github_user_id`, `state`, `linked` or `needs_relink`, `access_expires_at`, `refresh_expires_at`
  and `linked_at`. The two tokens are in the vault, under the purposes `github.access` and
  `github.refresh`, and nowhere else.

**Starting.** `POST /devflow/api/github/link` makes a random state and a random PKCE verifier, 32
bytes each in base64url, stores the state's SHA-256 with the verifier and the time, removing the
user's own sign ins older than ten minutes on the way, and answers the code host's address with the
state and the verifier's S256 challenge. The redirect address is
`<DEVFLOW_PUBLIC_ORIGIN>/devflow/github/callback`, so it follows the origin a copy is served at.

**The return.** `GET /devflow/github/callback?code=&state=`, under the `Page` policy, so the person
must be logged in to the platform, and one who is not goes to refresh and comes back with the same
address, nothing spent. It deletes the state's sign in from this user's own, so it is spent whatever
follows, and refuses one that is missing, already spent, another user's or older than ten minutes,
a return carrying the code host's own `error`, and one without a code. Otherwise it exchanges the
code with the stored verifier, asks who the token belongs to, stores the account as `linked` with
both tokens in the vault, and answers `303` to `/devflow/projects`. Any refusal answers `303` to
`/devflow/projects?link=failed` and stores nothing.

**A usable token.** `UserTokens.For(userId)` is the one way to a token that acts as the user, which
every later use goes through. In one door transaction it locks the account's row with
`SELECT ... FOR UPDATE`, returns the access token while it has more than five minutes left, and
otherwise renews, stores both new tokens and their lifetimes, and returns the new one. A renewal
the host refuses, or a refresh token already past its end, sets the state to `needs_relink` and
deletes both tokens; a host that is unavailable changes nothing and is answered as unavailable;
with no account, or `needs_relink`, it answers that for the caller to show.

**Unlinking.** `DELETE /devflow/api/github/link` removes the account, its tokens and any sign ins.
Nothing is revoked at GitHub; "Registering the GitHub App" below says where the owner does that.

Nothing prints a code, a verifier, a state or a token. The request log, which carries each
address's query and so the return's code and state, is raised to `Warning` in `DevFlowApp.Build`,
and `LinkTests.NothingPrintsACodeAVerifierAStateOrAToken` reads every log line of a whole link and
a renewal for each of them.

## Connecting a repository

A project is one connected repository with its settings, and in this part it has almost none:
every project is read the way this repository's runner writes, and one without the runner's
labels shows nothing waiting. What it is for is "Projects" in `docs/devflow-architecture.md`.

**As the owner does it.** On the Projects tab, with GitHub linked, press "Connect a repository",
find the repository under the account it is on, and press "Connect": it is listed under the
tab's projects from then on. A repository that is not listed is one the app is not installed on,
or one the GitHub account linked cannot reach: install the app on it, through "Install it on
GitHub" or the app's page, see "Registering the GitHub App", and press "Connect a repository"
again. "Disconnect" removes the project and everything Dev flow read for it, after asking, and
changes nothing on GitHub.

It is `ProjectStore` and `ProjectEndpoints`, in `src/DevFlow/Projects/`, under the `Api` policy
and the origin check:

- `GET /devflow/api/github/repositories` answers
  `{"installations": [{"id", "account", "repositories": [{"id", "owner", "name", "connected"}]}], "install_url": "..."}`:
  every installation the caller's GitHub account can reach, read as them, sorted by account, its
  repositories by owner and name, each saying whether it is already one of their projects, and
  where to install the app. An installation gone between the two reads is left out.
- `POST /devflow/api/projects` with `{"installation_id", "repository_id"}` asks the code host, as
  the caller, for that installation's repositories, and only when the id is among them stores the
  project, with the owner and name the code host gave and never the request's, answering `201` with
  `{"id", "installation_id", "repository_id", "owner", "name", "created_at"}`. A repository it does
  not list there, or an installation the caller cannot reach, is `404`; one already a project of
  theirs is `409`; a field missing or not a positive number is `422` naming it. Nothing is stored
  for any of them.
- `GET /devflow/api/projects` answers `{"projects": [...]}`, each as above, by owner and name.
- `DELETE /devflow/api/projects/{id}` answers `204`, or `404` for a project the caller does not
  have, another user's among them.

The first two need a linked account: without one each is `409`, a problem document titled "GitHub
is not linked", and with a link GitHub stopped taking, "Your GitHub link has expired". When GitHub
does not answer, either is `503`, "GitHub did not answer", with `Retry-After` when GitHub said when
to try again. Listing projects and disconnecting need no link.

The two tables, from `migrations/0004_projects.sql`:

- `projects`, guarded like every scoped table: `id` its primary key, `user_id`, `code_host`, always
  `github`, `installation_id`, `repository_id`, `owner`, `name`, `bot_login`, the login whose pull
  requests the runner's reader looks at, `mac-mini-agent` by default and changed from no page yet,
  and `created_at`, unique on `(user_id, repository_id)`. Two users may connect the same
  repository, each as their own project. Everything later slices store for a project references
  its id with `ON DELETE CASCADE`, so disconnecting removes it.
- `poll_targets`: `project_id`, its primary key, and `user_id`, nothing else, and **no** row level
  security, so a loop with no logged in user can learn which projects exist and whose they are, and
  then works through the door as each one's user. A trigger on `projects`, with a
  `SECURITY DEFINER` function, `sync_poll_target`, owned by `devflow_owner`, adds a row after an
  insert, removes it after a delete and follows a changed id, so no code path can forget;
  `devflow_app` may only `SELECT` it. Why it is safe is "Security decisions and accepted risks" in
  `docs/devflow-architecture.md`, and `IsolationTests.EveryTableIsGuarded` names it as the one
  exception.

## What waits

Dev flow keeps a stored picture of what waits for the owner in every project, and the pages read
it and never the code host, so they are fast and still answer while GitHub is down. Why it is
built so is "Reading and the snapshot" in `docs/devflow-architecture.md`. It is `ProjectReader`,
`ProjectRefresher`, `Poller`, `WaitingStore`, `WaitingEndpoints` and `ScreenshotEndpoints`, in
`src/DevFlow/Reading/`.

**Reading a project.** `ProjectReader.Read(projectId, userId)` reads one project as the app,
through `ICodeHost`: its open pull requests in one list; then, only for those by the project's
`bot_login` that carry `owner-merge`, `owner-decision` or `owner-blocked`, their comments, their
head's statuses and check runs, and the comments of the slice issue their body closes; and passes
each to `RunnerReader.Read`, with the project's repository and the owner's login from his link in `github_accounts`, whatever
its state, or none without a link. A conversation, a commit or a slice that is not found is read as
empty. It then replaces the project's items in one transaction through the door as the user: an
item still there is updated and keeps its id, one gone is deleted, and the read is recorded as a
success. A read that fails, because GitHub did not answer, does not show the repository, or
anything throws, leaves the items as they were and records the time and a fixed phrase, "GitHub
did not answer", "GitHub does not show this repository" or "Reading failed", never the host's own
text.

**Asking for a read.** `ProjectRefresher.Request(projectId, userId)` is the one way to ask, and
answers a task that completes once a read that began after the request is done, whether it
succeeded or not. A project is read by one reader at a time; requests made while it is read share
one more read after it. The loop, the refresh route, merging and sending back, see "Acting from the
page", and a webhook, when the platform is reachable, all call it.

**The loop.** `Poller`, a hosted service, lists `poll_targets` as it starts and then every
`DEVFLOW_POLL_SECONDS`, through the door's `WithoutUser`, the one read outside a user, and requests
each project as its user. It reads as the app, so it goes on when the owner's link has expired,
and runs in the fake mode as well. With `DEVFLOW_POLL_SECONDS=0` it does not run.

**The routes**, under the `Api` policy and the origin check:

- `GET /devflow/api/waiting` answers
  `{"count", "needs_relink", "items": [...], "projects": [{"id", "owner", "name", "last_success_at", "stale", "failing"}]}`.
  Each item is a stored row with its project's id and repository name: `id`, `project_id`,
  `project_name`, `number`, `kind`, `waiting` or `stopped`, `sent_back`, `title`, `web_url`,
  `slice_number`, null when the body closes none, `head_commit`, `can_merge`, `summary`, `reason`,
  `decisions`, each `{"chosen", "alternative", "why"}`, `other_decisions`, `reviewer_view`,
  `screenshots` and `read_at`, by project and number. `screenshots` are the stored names paired for
  the page by `Screenshots.Group`, each `{"name", "images"}`: the part of the names before
  `-desktop` and `-phone`, and the names, the desktop one first; what each means is `RunnerItem` in
  `src/DevFlow/Runner/RunnerItem.cs`, and every text is plain text to show as text. `count` is the
  items not sent back, plus one when `needs_relink`, the caller's link needing renewal. A project is
  `stale` when its last success was more than five minutes ago or it was never read, and `failing`
  when its last read failed.
- `POST /devflow/api/projects/{id}/refresh` requests the caller's project and answers `204` once it
  is read, or `202` after ten seconds if it is not, while the read goes on. Whether it failed is in
  the list. Another user's project, or none, is `404`.
- `GET /devflow/api/waiting/{id}/screenshots/{name}` answers one of the item's screenshots, read
  when it is asked for and never stored. In order: another user's item, or none, is `404`; a
  `name` that is not one of the item's stored `screenshots` is `404`, without asking GitHub; then
  `ICodeHost.File` reads `<number>/<name>.png` on the branch `pr-screenshots` of the item's own
  repository, as the app, the path built here and never taken from the body. What it finds is
  served only when it begins with the PNG signature and is at most 5 MB, always as `image/png`,
  with `Cache-Control: private, max-age=3600`; anything else it finds is `502`, a file not there
  `404`, and GitHub not answering `503`, with `Retry-After` when GitHub said when. Every answer
  carries `X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none'`, and
  a refusal `Cache-Control: no-store`. Why it is built so is "A code host's bytes handed to the
  browser" in `docs/devflow-architecture.md`.

The two tables, from `migrations/0005_waiting_items.sql`, both guarded like every scoped table:

- `waiting_items`: `id`, `user_id`, `project_id` referencing `projects` with `ON DELETE CASCADE`,
  and the item's fields above, `decisions` and `reviewer_view` as `jsonb` arrays, unique on
  `(project_id, number)`; and `screenshots`, from `migrations/0006_waiting_screenshots.sql`, the
  names `RunnerReader` accepted from the body, a `jsonb` array of strings, empty by default;
- `project_reads`: `project_id`, its primary key with the same reference, `user_id`,
  `last_attempt_at`, `last_success_at`, null before a read succeeds, and `last_error`, the phrase,
  null after a success.

## Acting from the page

From the waiting list the owner merges a pull request or sends it back with a comment, and GitHub
records both as done by him, since both are made with his own linked account's token, from
`UserTokens.For`, and never as the app. The runner then picks his comment up as it picks up one
written on GitHub. Why it is built so, and what it never does, is "Acting from the page" in
`docs/devflow-architecture.md`. It is `ActionStore` and `ActionEndpoints`, in `src/DevFlow/Acting/`,
under the `Api` policy and the origin check. Every refusal is a problem document, its `title` the
line the page shows and its `detail` what follows it.

**`POST /devflow/api/waiting/{id}/merge`**, with `{"head_commit": "<the commit the page showed>"}`,
checks, in this order, and answers the first that fails:

| Answer | Title | When |
|---|---|---|
| `404` | No such item | The caller has no item with that id, another user's included. |
| `422` | The request breaks a rule | `head_commit` is missing or empty. |
| `409` | This pull request is stopped | Its kind is `stopped`: the runner says do not merge it as it is. A comment runs it again. |
| `409` | This pull request was sent back | The owner has commented since the latest verdict, so the runner has it. |
| `409` | The checks are not green on this commit yet | `can_merge` is false. |
| `409` | The pull request has changed since this was shown | `head_commit` is not the stored head. The project is read again first. |
| `409` | GitHub is not linked | The caller has no link. |
| `409` | Your GitHub link has expired | The link is `needs_relink`, or renewing it was refused. |
| `503` | GitHub did not answer | The token needed renewing and GitHub did not answer; nothing was sent. |

Only then is GitHub asked to merge that commit, with a merge commit. Merged, the project is read
again and the answer is `204`. GitHub saying the head moved is the same `409` "The pull request has
changed since this was shown", after the project is read again. Not mergeable is `409` "GitHub
would not merge it", with GitHub's words in the detail; any other refusal `409` "GitHub refused the
merge", with its status and words; no answer `503` "GitHub did not answer", after the project is
read again, since it may have merged. Merging an item with decisions confirms them, as merging on
GitHub does, and nothing else is recorded.

**`POST /devflow/api/waiting/{id}/send-back`**, with `{"comment": "<text>"}`, for a waiting or a
stopped item: `404` "No such item" as above; `422` when `comment` is missing, empty once trimmed,
or longer than 10,000 characters once trimmed; the same two `409`s and the `503` for the link; then
the comment is posted, trimmed and with nothing added. Posted, the item's `sent_back` is set at
once, the project is read again and the answer is `204`. GitHub refusing is `409` "GitHub refused
the comment", with its status and words; no answer `503`, after the project is read again.

Reading again goes through `ProjectRefresher` and waits for the read as the refresh route does, at
most ten seconds, so the list the page reads next is current. After either, whatever it answered,
the page reads the list again and dispatches `platform-nav-attention` on `window`, so the nav bar
asks `GET /devflow/api/attention` again and its dot follows; a refusal can change the answer too,
an expired link among them. It is `askNavAgain` in `web/src/nav-attention.ts`. Neither route
writes a label, a status or anything else the runner owns: after a comment, the runner sees the
owner's input on its next turn and moves the label itself.

## Workers

A worker is a program in a container on one of the owner's machines that builds a project's cards.
Every worker belongs to one project, and so to that project's user, and calls Dev flow; Dev flow
never calls it. This part gives a project its key and a worker its first two calls, which the
worker program, "The worker" below, makes; its image and the commands to start it come with later
slices. Why it is built so is "Workers" in `docs/devflow-architecture.md`.

**As the owner does it.** On the Projects tab, open a project by its name, and in its Workers panel
press "Make a key". Copy the key then: it is shown once and never again. A worker started with it
says hello and is listed under the panel. "Make a new key" replaces it, and every worker holding
the old one is refused from then on.

**The key.** One per project, shared by every worker of that project: 32 random bytes from .NET's
`RandomNumberGenerator`, written as `dfw_` and their base64url without padding, 47 characters. Only
the SHA-256 of that text is stored. There is no way to list a key or show one again, and a key is
never logged or put in an address. It is `WorkerKeys`, in `src/DevFlow/Workers/`.

**The worker scheme.** A worker sends `Authorization: Bearer dfw_…` and nothing else: no cookie and
no `Origin`. `WorkerAuthentication` reads the one header, refuses a second one, another scheme or a
key of the wrong shape, hashes the key, looks the hash up in `worker_key_lookup` through the door's
`WithoutUser`, and then, as the project's user through `InUserTransaction`, confirms that
`worker_keys` holds that hash for that project. Everything the request does after that runs as that
user. Anything else is `401` with an empty body, the same whatever the reason. A worker route takes
only this scheme, and a user's route only the user's token: a user's token is not the shape of a
key, and a key is not a token the core signed.

The routes, in `WorkerEndpoints`:

- `POST /devflow/api/projects/{id}/worker-key`, the user's, origin checked, answers
  `{"key": "dfw_…"}` with `Cache-Control: no-store`, the one time the key is sent, and replaces the
  project's last key at once. `404` for a project the caller does not have, another user's among
  them, and nothing is stored.
- `GET /devflow/api/projects/{id}/workers`, the user's, answers
  `{"key_made_at": "<time>" or null, "workers": [{"id", "name", "recipe_version", "base_image", "first_seen_at", "last_seen_at"}]}`,
  by when each worker first said hello. Never the key. `404` as above.
- `POST /devflow/api/worker/hello`, the worker's, with
  `{"worker_id": "<uuid>", "name": "<the container's hostname>", "recipe_version", "base_image"}`,
  records the worker in its key's project, or brings it up to date, keeping when it was first
  seen, and answers `204`. Each field is required; `worker_id` is a UUID that is not all zeros, and
  each text has 1 to 255 characters, not only spaces; otherwise `422` naming the field.
- `POST /devflow/api/worker/jobs/next`, the worker's, with `{"worker_id"}`, marks the worker seen
  and answers its next job; in this part there is none, so `204`. A worker that has not said hello
  in the key's project is `409` "Say hello first", and says hello and asks again; no `worker_id`
  is `422`.

The tables, from `migrations/0007_workers.sql`:

- `worker_keys`, guarded like every scoped table: `project_id`, its primary key, `user_id`,
  `key_hash`, the 32 byte SHA-256, unique, and `created_at`.
- `worker_key_lookup`: `key_hash`, its primary key, `project_id` and `user_id`, nothing else, and
  **no** row level security, so a worker's request, which carries no user, can be led to one. A
  trigger on `worker_keys`, with a `SECURITY DEFINER` function, `sync_worker_key_lookup`, owned by
  `devflow_owner`, keeps it, so a replaced key and a disconnected project leave nothing behind;
  `devflow_app` may only `SELECT` it, as with `poll_targets`.
- `workers`, guarded: `project_id` and `id`, the worker's own, together its primary key, so the
  same id in two projects is two workers and one user's worker never meets another's, `user_id`,
  `name`, `recipe_version`, `base_image`, each 1 to 255 characters, `first_seen_at` and
  `last_seen_at`.

`worker_keys` and `workers` reference `projects (id, user_id)`, not `projects (id)` alone, with
`ON DELETE CASCADE`, so a key or a worker can only belong to its project's own user, whatever a
query tries, and disconnecting removes them; the migration adds the unique constraint on
`projects (id, user_id)` they need.

## The worker

The worker is the program in a worker's container, `src/DevFlow.Worker/`: started with Dev flow's
address and a project's key, it introduces itself, asks for work in a loop, and runs an agent
behind one narrow interface. What it says to Dev flow is `src/DevFlow.Protocol/`, the worker API's
paths, its requests and their JSON, which Dev flow's worker routes take too, so a change that breaks
either side fails to compile. Why it is built so is "Workers" in `docs/devflow-architecture.md`.
In this part it asks for work and is told there is none; fetching the code, the check and pushing
come with later slices of the feature, and its image and the command that starts it with "The base
image and the start commands".

**Its settings**, from the environment:

| Variable | What it is |
|---|---|
| `DEVFLOW_URL` | Required. Dev flow's address, an `http` or `https` origin with no path, such as `http://host.docker.internal:9080` |
| `DEVFLOW_PROJECT_KEY` | Required. The project's worker key, `dfw_…`, made on the project's page; sent as `Authorization: Bearer` and nowhere else, and never printed |
| `DEVFLOW_RECIPE_VERSION` | The version of the project's recipe the image was built from, which the image sets; sent in `hello` |
| `DEVFLOW_BASE_IMAGE` | The base image the image was built on, which the image sets; sent in `hello` |

A value of only spaces counts as unset. Unset, each version is sent as `unset`: `hello` requires
both, each 1 to 255 characters, so an empty one would be refused, and `unset` says plainly that the
worker runs outside a project's image and never matches a version Dev flow expects.

**The loop.** The worker makes its id, a UUIDv7, when it starts, and takes its container's
hostname as its name. It says `hello`, then asks `jobs/next`:

- `204`: no work. It waits and asks again, 5 seconds at first, doubling each time to at most 60.
- A job: it runs it and asks again at once, and the wait starts again from 5 seconds. Dev flow
  hands out no job yet, and one arriving is logged and left.
- `409`: it says `hello` and asks again, at once, or after a wait when it has just said it.
- Dev flow away, which is no connection, no answer in 30 seconds or a `5xx`: a wait of the same
  length and another try, never an exit, and the next answer from Dev flow begins with `hello`
  again. So does an answer this worker does not know.
- `401`, to either call: it ends, see below.

Time is a `TimeProvider`, so the tests move it. The log is a line per event, its time in UTC
first, on standard output; nothing is logged while there is no work.

**Exit codes:**

| Code | When |
|---|---|
| `0` | Stopped by `SIGTERM` or `SIGINT`, as `docker stop` sends |
| `2` | A setting is missing or wrong, named on standard error before anything is sent; or Dev flow refused the key, `401`, with a line naming `DEVFLOW_PROJECT_KEY` and never the key; or refused what `hello` reports, `422`, with Dev flow's reason |

Anything else is a crash, with .NET's own code and message.

**The agent interface.** `IAgent`, in `src/DevFlow.Worker/Agents/`, has one method: given the
working copy, the brief as text, the report's JSON Schema, the limits and the credential, it
answers a result, a success with the report, an object, or a failure with its reason and which
side it lies on, in the card or outside it; and either way the usage as the agent gave it, its
standard error, and how it was logged in. Two implementations:

- `FakeAgent` answers a prepared report and writes or removes the files it is told to, refusing a
  path outside the working copy. It spends no usage, so the worker's own tests use it.
- `ClaudeCodeAgent` runs Claude Code headless. First `claude auth status --json`, which must answer
  `"loggedIn": true` and `"authMethod": "oauth_token"`, or the run never starts and fails outside
  the card; the method is recorded either way. Then, in the working copy:

  ```zsh
  claude -p <brief> --model claude-opus-5-5 --permission-mode bypassPermissions --output-format json \
    --json-schema <schema> --max-turns 200 --max-budget-usd 25
  ```

  never with `--bare`, which refuses the subscription token. Both get an environment built from
  nothing: `HOME`, the agent's home, `/home/agent`; `PATH`; `LANG`; `CLAUDE_CONFIG_DIR`, `.claude`
  in that home; and `CLAUDE_CODE_OAUTH_TOKEN`. The arguments are a list, never through a shell.

**When a run succeeds**: only when every one of these holds: exit `0`; standard output is valid
JSON; `is_error` is false; `terminal_reason` is `completed`; `structured_output` is present and an
object; `permission_denials` is empty. `subtype` is never read, since it says `success` on some
failures. The report's schema is checked by Dev flow, which hands it out with the brief; the
worker checks only that it is an object. Anything else is a failure, judged in this order:

| What Claude Code answered | Where it lies |
|---|---|
| Past the time limit, ended by the worker | In the card |
| Nothing on standard output, as root with `bypassPermissions` | Outside |
| Something that is not JSON | Outside |
| `terminal_reason` `api_error`: not logged in, a refused key (`api_error_status` 401), a usage limit | Outside |
| `terminal_reason` `max_turns` | In the card |
| `terminal_reason` `budget_exhausted` | In the card |
| `permission_denials` not empty, a refused action | In the card |
| Any other exit code | Outside |
| `is_error` true, whatever `subtype` says | Outside |
| Any other `terminal_reason` | Outside |
| No `structured_output`, or one that is not an object | In the card |

Outside the card means another try may well succeed and the card is not to blame: the login, the
API or the machine. The usage is taken as Claude Code gives it: `usage`, `modelUsage`,
`total_cost_usd`, `duration_ms` and `num_turns`.

**The limits for now**, fixed in code as `AgentLimits.Default` until Dev flow's settings hold them:
200 turns and 25 USD, which Claude Code enforces, overshooting by about a turn, and 90 minutes,
which the worker enforces by ending the process and everything it started.

**The token** is handed to the run in its environment only. The worker never writes it to a file,
logs it or sends it back: `AgentCredential` prints as redacted, and the token's value is replaced
by `[redacted]` in Claude Code's standard output and standard error before anything reads them, so
neither the report, the stderr kept nor a reason can carry it out.

**Its own user.** The process starter, `SystemProcessStarter`, takes the user to run as, `agent`
in the image, and starts Claude Code through `setpriv --reuid agent --regid agent --init-groups`,
which keeps the environment as built; Claude Code refuses `bypassPermissions` as root. The worker
itself runs as root, so what a job needs kept from the agent stays out of the agent's reach.
Without a user, as in the tests, it starts the program directly.

**Publishing.** One self-contained file carrying its own .NET runtime, so a project's image needs
no .NET, for each Linux a worker runs on:

```zsh
dotnet publish src/DevFlow.Worker -c Release -r linux-arm64 -o <folder>
dotnet publish src/DevFlow.Worker -c Release -r linux-x64 -o <folder>
```

Naming a runtime makes it self-contained and one file, with its symbols inside it, and
`InvariantGlobalization` means the image needs no ICU either. `RuntimeIdentifiers` in the worker's
and `DevFlow.Protocol`'s project files, and the single-file analyzer turned off, keep one
`packages.lock.json` that holds for the build and the publish alike, on any machine: the analyzer
comes in an implicit package, `Microsoft.NET.ILLink.Tasks`, that a source-built SDK, such as
Homebrew's on the Mac mini, serves with another hash than nuget.org's, which fails a locked
restore on one side or the other.

**Running it** by hand, against Dev flow from "Running it" above, with a key made on a project's
page:

```zsh
DEVFLOW_URL=http://127.0.0.1:8084 DEVFLOW_PROJECT_KEY=<the key> dotnet run --project src/DevFlow.Worker
```

**Its tests** are `tests/DevFlow.Worker.Tests/`, run by the same `dotnet test` as Dev flow's own,
with the same two database strings. `WorkerLoopTests` runs the worker against Dev flow in process,
on a database of its own, through `DevFlow.Tests`' harness, which that project references, and
against a Dev flow played from a script, on a clock the test moves. `ClaudeAgentTests` runs
`ClaudeCodeAgent` on a fake process starter answering with Claude Code's answers recorded in
`Claude/`, whose README says where they come from; `ProcessStarterTests` checks that a real process
gets only the environment given. `ProtocolTests` checks that Dev flow's routes take the shared
types and that both sides write them as `contracts/devflow/openapi.json` says, and `PublishTests`
publishes for both runtimes, in locked mode, in a folder of its own.

## Claude login

The Claude login is the token the owner makes with `claude setup-token`, which every run of his
is handed so it runs on his subscription. Why it is held by Dev flow, and how it reaches a run, is
"Workers" in `docs/devflow-architecture.md`.

**As the owner does it.** In a normal terminal, run `claude setup-token`, and paste what it prints
into "Claude login" on the Projects tab, then "Save". The field empties and the section says when
it was given and when to renew it, a year later. A month before then the section, the nav's dot
and the front page's card say it is due: make a new token the same way and "Replace" the old one.
"Remove" forgets it.

**One per user**, not per project, since the subscription is the user's. The token is sealed in
the vault, see "The vault", under the purpose `claude-login`, and nowhere else.
`ClaudeLogins`, in `src/DevFlow/ClaudeLogin/`, does everything with it, through the door as the
user.

**Its year is counted from when it was given.** Claude Code does not say when a setup token runs
out: `claude auth status --json` reports `loginExpiresAt` as `null` for one. So Dev flow records
`given_at` and counts: `renew_by` is `given_at` plus 365 days, the state is `due_soon` from 31
days before `renew_by`, and `expired` from `renew_by` on. Replacing it starts the year again. The
clock is the app's `TimeProvider`, so a test moves it.

**The check on save** is only that it looks like a setup token: `sk-ant-oat` followed by at least
one more visible ASCII character, no space or line break, at most 512 characters, once the space
around a paste is dropped. Dev flow does not ask Anthropic whether it works; a worker checks the
login before every run.

The routes, in `ClaudeLoginEndpoints`, the user's, origin checked:

- `GET /devflow/api/claude-login` answers
  `{"set": true, "given_at": "<time>", "renew_by": "<time>", "state": "ok"}`, with `state` one of
  `none`, `ok`, `due_soon` and `expired`, and both times null with `none`. Never the token.
- `PUT /devflow/api/claude-login` with `{"token": "sk-ant-oat…"}` seals it, replacing any before
  it, given now, and answers the status it leaves, `200`. Anything that does not look like a setup
  token is `422` naming `token`, never repeating what was sent, and nothing is stored.
- `DELETE /devflow/api/claude-login` forgets the login and its sealed token, `204`, whether or not
  there was one.

**Opening it for a run** is one method, `ClaudeLogins.ForRun(runUserId)`, and nothing else opens
it. It opens through the door as the run's user, so no one else's is visible, and the vault's
binding to the user it was sealed for refuses a token moved to another, with `VaultException`. It
answers a `ClaudeToken`, which prints as `ClaudeToken { sealed }`, so a log line or an exception
naming it does not reveal it. It is called only when a job is handed out, so the token leaves Dev
flow only inside a job's answer to its own user's worker; nothing calls it until "Handing out work
and the brief". An expired login is still handed out, since its expiry is only counted.

The table, from `migrations/0008_claude_logins.sql`, guarded like every scoped one:
`claude_logins`, `user_id`, its primary key, `secret_purpose`, always `claude-login`, and
`given_at`. `(user_id, secret_purpose)` references `secrets (user_id, purpose)` with
`ON DELETE CASCADE`, so a login cannot be recorded without its sealed token, and removing the
token removes the login.

## Registering the GitHub App

The owner's runbook, done once on GitHub, under Settings, Developer settings, GitHub Apps, New
GitHub App, as the account that owns the repositories. Every field on the form, and what to enter:

| Field | What to enter |
|---|---|
| GitHub App name | A name of your own, such as `platform-devflow`; its slug, from the app's address `github.com/apps/<slug>`, is `DEVFLOW_GITHUB_APP_SLUG` |
| Description | Optional |
| Homepage URL | The hosted copy's origin |
| Callback URL | `https://platform.test/devflow/github/callback`, then "Add Callback URL" and `<the hosted origin>/devflow/github/callback`; each must match Dev flow's redirect address exactly |
| Expire user authorization tokens | On: Dev flow renews the eight hour token itself, and refuses an answer without expiring tokens |
| Request user authorization (OAuth) during installation | Off: linking happens from the Projects tab |
| Enable Device Flow | Off: Dev flow uses the web flow alone |
| Setup URL | Empty |
| Redirect on update | Off |
| Webhook, Active | Off: Dev flow polls, see "The program asks GitHub for changes; there is no webhook" |
| Repository permissions | Contents read and write, Pull requests read and write, Issues read, Commit statuses read, Checks read, Metadata read, and nothing else |
| Organization permissions | None |
| Account permissions | None |
| Subscribe to events | None |
| Where can this GitHub App be installed? | Only on this account |

Then, on the app's page:

1. **Generate a private key.** GitHub downloads a `.pem` file. Keep it on the Mac mini outside the
   repository and name its path as `DEVFLOW_GITHUB_PRIVATE_KEY_FILE` in `infra/hosted/.env`, which
   mounts it read only into Dev flow's container; keep a copy in the password manager.
2. **Generate a new client secret.** GitHub shows it once. Put it in `infra/hosted/.env` as
   `DEVFLOW_GITHUB_CLIENT_SECRET`, with the client id the page shows as `DEVFLOW_GITHUB_CLIENT_ID`,
   and keep it in the password manager.
3. **Install App**, on the repositories Dev flow is to read, only those.
4. **Try the link on the laptop first.** Start Dev flow there with `DEVFLOW_CODE_HOST=github`, a
   `DEVFLOW_VAULT_KEY` made as "The vault" says, and the app's four settings, open the Projects
   tab, press "Link GitHub", authorize on GitHub, and see "Linked as @<your login>"; then press
   "Connect a repository" and see the repositories you installed it on. Then release
   the hosted copy, see `infra/hosted/README.md`.

**Revoking.** Unlinking forgets the tokens in Dev flow and revokes nothing. To end what GitHub
granted, revoke the app's authorization under your GitHub Settings, Applications, Authorized
GitHub Apps; to stop it reading the repositories, uninstall it under Installed GitHub Apps; and to
make every client secret or private key useless, delete it on the app's page.

## How Dev flow authenticates a request

The rules every feature follows are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`. Dev flow holds none of the code for them: `PlatformAuth`, in
`libs/dotnet/PlatformAuth/`, does every check of the token and the origin, and its README says what
it holds and how its tests cover the ways verification breaks. Dev flow references it as a project
and does three things with it, in `src/DevFlow/DevFlowApp.cs`:

- `AddPlatformAuth` with `DEVFLOW_CORE_URL`, which registers the key cache, the handler and the two
  policies. The pages take `PlatformAuthPolicies.Page`, as do the return from the code host and the
  fake's sign in page, and `whoami`, `status`, starting a link, unlinking, the projects' routes,
  the waiting list, refreshing a project, making a project's worker key, listing its workers and
  the fake's addresses take `PlatformAuthPolicies.Api`; health and the built files take neither. A
  route reads its caller's user id through `Api/Caller.cs`, as review's do.
- `AddWorkerAuthentication`, Dev flow's own, in `src/DevFlow/Workers/WorkerAuthentication.cs`: the
  second way a request authenticates, a project's worker key, and the policy
  `WorkerAuthentication.Policy`, which the worker routes, `hello` and `jobs/next`, take and nothing
  else does. A worker route reads its project and user through `context.Worker()`. See "Workers".
- `UsePlatformOriginCheck` with `DEVFLOW_PUBLIC_ORIGIN`, before routing, so every route, one added
  later included, is covered: starting a link, unlinking, making a worker key, and the fake's
  addresses and its sign in form. It runs for every path but those under `/devflow/api/worker/`,
  matched case and all, which the worker scheme alone guards and which read no cookie, see
  "Workers". The return from the code host, `GET /devflow/github/callback`, is the one route that
  changes state on a GET, which "How a feature authenticates" otherwise forbids; why it is safe is
  in "Security decisions and accepted risks" in `docs/devflow-architecture.md`.
- `DevFlowApp.Build(settings, database, webRoot, configure)` builds the app, with the door; `configure` runs before it is
  built, which is where `Program` sets the listen address and where the tests put the app on a
  test server, with a key cache over the committed key set answered from memory and a clock fixed
  at the committed token's `iat`, both of which `AddPlatformAuth` keeps.

The tests send the ways verification is known to break through Dev flow's own routes, as "A
feature's authentication tests are part of the feature template" in
`High-level/docs/how-we-build.md` asks: the committed token is accepted from the cookie and the
header, a missing, expired, wrongly signed or unknown key id token gets `401` from the API, and a
page without a token answers `303` to the refresh endpoint with the way back.

## Libraries Dev flow is built on

**The .NET 10 SDK, pinned in `global.json`**, with `Directory.Build.props`,
`Directory.Packages.props` and `.editorconfig` holding the same values as review's folder, copies
rather than shared files, since each C# folder builds from its own directory alone. Why .NET 10,
the analyzers as the linter and `dotnet format` as the formatter are in "Libraries it is built on"
in `libs/dotnet/PlatformAuth/README.md`.

**One web project, not layered projects**, beside the worker. `src/DevFlow/` is the whole
service, organised in
feature folders: `Settings`, `Health` (health and whoami), `Status`, `CodeHost` (the interfaces, their
records, the fake and, in `GitHub`, the adapter), `Link` (linking GitHub and a usable token), `Vault`,
`Projects` (connecting a repository), `Reading` (the snapshot, the loop and the waiting list),
`Workers` (the worker key, the worker scheme and the worker API), `Pages` and `Api` today, more with
later slices, as `review/` is. Two projects stand beside it because a second program needs them:
`src/DevFlow.Worker/`, the worker, a console program, and `src/DevFlow.Protocol/`, the types it and
Dev flow share, which hold nothing else.

**The worker is built on .NET alone**: `HttpClient` and `System.Text.Json` for Dev flow,
`System.Diagnostics.Process` for Claude Code, and `TimeProvider` for its waits, so it adds no
package. It is published self-contained, see "The worker" above.

**The GitHub adapter is written by hand on .NET's `HttpClient`**, with `System.Text.Json`, and
signs the app's tokens with .NET's own `RSA`, and signs a person in with the same `HttpClient` and
.NET's own SHA-256 for PKCE, so no GitHub, JWT or OAuth library is added: its calls are
few, and a library would be a dependency for what a dozen typed requests do. What it remembers for
conditional requests is in a `MemoryCache`, from the shared framework.

**The vault's cipher is .NET's own `AesGcm`**, AES-256-GCM from `System.Security.Cryptography`, the
standard authenticated cipher, which the framework has, so no library is added for it.

**ASP.NET Core minimal APIs**, from the shared framework, for routing, static files and problem
details, with no MVC. JSON is snake case on the wire, as review's, the brain's and canary's is.

**Microsoft.AspNetCore.OpenApi for the schema**, at review's version, for review's reasons, see
"Libraries review is built on" in `review/README.md`.

**xUnit v3 for the tests**, with `Microsoft.AspNetCore.Mvc.Testing` for `TestServer` and
`Microsoft.Extensions.TimeProvider.Testing` for the clock, review's test packages at its versions.
Npgsql and Dapper come through `PlatformData`, at its versions, so `Directory.Packages.props` does
not list them; review's brain client package, Kiota, is left out until Dev flow needs it.

**The page** is review's stack, React, Vite, Vitest, Biome and TypeScript, at its versions; see
"Libraries the brain is built on" in `Brain/README.md` for why each was chosen.
