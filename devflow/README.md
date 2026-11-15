# devflow

Dev flow is the platform's feature for automating development on connected repositories: the owner
plans, agents build, and he follows and answers all of it from its pages. What it is and how it is
meant to grow is in `docs/devflow-architecture.md`, and its scope and slices are in its feature
issue, #268. "Dev flow" is its name in the nav and in headings, and `devflow` everywhere technical.

It is a C# feature on .NET 10, an ASP.NET Core minimal API app, built the way `review/` is. Today it
is the service, its page and its database, which holds the vault: the other tables, the code host
and everything the tabs will show arrive with later slices.

## What it serves

- `GET /devflow/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what Dev flow depends on.
- `GET /devflow/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.
- `GET /devflow` and `/devflow/projects`, with any query, the page: `web/dist/index.html`,
  revalidated on every load. Without a valid token each answers `303` to
  `/auth/refresh?return_to=<the path and query as received, URL encoded>`.
- `GET /devflow/assets/*`, the page's built files from `web/dist/assets/`, without a token, as
  review and the brain serve their own: they are the same for everyone and hold no data.

Everything else is `404`, and any request that changes state from another origin is `403`, before
routing, see "How Dev flow authenticates a request" below. An error the service did not plan for is
an RFC 9457 problem document, `application/problem+json` with `status`, `title` and `detail`,
through ASP.NET Core's `AddProblemDetails`, as review's are.

The routes under `/devflow/api/` are pinned by `contracts/devflow/openapi.json`, see "The schema"
below.

## The page

`web/`, a frontend like any other, a `web` folder under its service, so CI and the agents gate and
build it with the rest, see `design/ui/README.md`. It is review's stack at its versions,
`review/web/package.json`, without React Query, which nothing here needs until a section loads
data, and depends on the components as `"@platform/ui": "file:../../design/ui"`. Its `index.html`
includes the nav's lines from "Navigation" in `High-level/docs/platform-architecture.md`, with
`<platform-nav current="devflow">`, and its content sits in `<main class="page">`.

The page is an `h1`, "Dev flow", a line under it, "What your agents built, and what waits for
you.", then the package's `Tabs`, one per section, each with its own address:

| Section         | Address             |
| --------------- | ------------------- |
| Waiting for you | `/devflow`          |
| Projects        | `/devflow/projects` |

Each section shows the package's `EmptyState` until its slice lands. "Running now" and "Usage and
history" join with the parts that build them, never as empty tabs. The address is the state,
through the History API and no router library (`src/address.ts`): a tab pushes its section's
address, Back and Forward come back as `popstate`, and an address that names no section shows a
not found state under the tabs. The list of sections lives in `src/sections.ts`, and the addresses
the service answers the page for in `src/DevFlow/Pages/PageEndpoints.cs`; the two change together.

`src/api/client.ts` is the page's one way to call Dev flow: every call goes to `/devflow/api/` with
the session cookie, a `401` is refreshed once, shared by every call that got one, by posting to
`/auth/refresh` and repeating the call, and a second `401` sends the browser to
`/auth/refresh?return_to=<path>`, as review's client does. Its GETs are typed from
`src/api/schema.d.ts`. Nothing on the page calls it yet.

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
- `DEVFLOW_CODE_HOST`, default `fake`: `fake` or `github`, nothing else. Today it decides one thing,
  whether the vault needs a key; the code host's slice, #273, gives it the rest of its meaning.
- `DEVFLOW_VAULT_KEY`, the vault's current key, `<id>:<base64 of 32 bytes>`, see "The vault". Unset
  or empty, Dev flow makes one in memory with the fake code host and refuses to start with
  `github`. A key that does not parse stops it, naming the setting and never repeating the value.
- `DEVFLOW_VAULT_OLD_KEYS`, unset or empty for none: keys rows may still be sealed with, comma
  separated in the same form, each with an id of its own that is not the current key's.

The defaults are right on the Mac mini and in CI, so neither sets them, except the listen address,
which the manifest's `start` sets, and the database URLs, which `scripts/agent/services.sh` and
`.github/workflows/dotnet.yml` build. The hosted copy sets the four it needs to serve and passes the
vault's two in `infra/hosted/compose.yaml`, from its `.env`, and the owner's URL only on
`devflow-migrate`.

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

Its one table is the vault's, `secrets`, from `migrations/0002_secrets.sql`, see "The vault".

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
library's door, which sets `devflow.user_id` for that transaction alone. Dev flow registers one
`UserDb`, opened at startup in `src/DevFlow/Commands.cs` with the name `devflow`,
`DevFlowSettings.Service`, as `devflow_app`, so a database it cannot reach, or a wrong password,
stops Dev flow there, naming `DEVFLOW_APP_DATABASE_URL` and never its password. It opens no
connection of its own, which `IsolationTests.OnlyTheDoorOpensConnections` checks by reflection.

Every scoped table has `user_id uuid NOT NULL`, row level security enabled
and forced, and one policy using `UserDb.GuardFor("devflow")` for `USING` and `WITH CHECK`.
`tests/DevFlow.Tests/IsolationTests.cs` proves the policy on a probe table, and fails for any table
a later migration adds to `public` without exactly that one policy, `schema_migrations` aside; Dev
flow leaves no table unguarded on purpose, and has no path around the policies.

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

A purpose is a short fixed name the caller chooses, such as `github.access`, matching
`^[a-z][a-z0-9.]*$`; anything else is an `ArgumentException`.

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

## How Dev flow authenticates a request

The rules every feature follows are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`. Dev flow holds none of the code for them: `PlatformAuth`, in
`libs/dotnet/PlatformAuth/`, does every check of the token and the origin, and its README says what
it holds and how its tests cover the ways verification breaks. Dev flow references it as a project
and does three things with it, in `src/DevFlow/DevFlowApp.cs`:

- `AddPlatformAuth` with `DEVFLOW_CORE_URL`, which registers the key cache, the handler and the two
  policies. The pages take `PlatformAuthPolicies.Page` and `whoami` takes
  `PlatformAuthPolicies.Api`; health and the built files take neither. A route reads its caller's
  user id through `Api/Caller.cs`, as review's do.
- `UsePlatformOriginCheck` with `DEVFLOW_PUBLIC_ORIGIN`, before routing, so every route, one added
  later included, is covered. Dev flow has no route that changes state yet.
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

**One web project, not layered projects.** `src/DevFlow/` is the whole service, organised in
feature folders: `Settings`, `Health` (health and whoami), `Pages` and `Api` today, more with later
slices, as `review/` is.

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
