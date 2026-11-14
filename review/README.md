# review

Review is a queue of cards that comes back to the owner, so what he marks as worth remembering
sticks. Cards are added by hand first, and later sent by other features. It is its own feature
rather than part of desk: three features will send it cards, it has its own logic for when a card
returns, and a card needs only a link back to where it came from, see "Being allowed to call is
not a reason to split" in `High-level/docs/feature-contract.md`. Until it was planned, on
2026-11-13, it was described as a part of desk, in `desk/docs/desk-architecture.md`, whose Review
section now points here. Its scope and slices are in its feature issue, #229.

It is the platform's first C# feature, on .NET 10, an ASP.NET Core minimal API app.

It has nothing to do with the brain's review queue, which is where the brain's consolidation
proposals wait for the owner, see "The review queue" in `Brain/docs/trust-model.md`.

## What it serves

- `GET /review/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what review depends on.
- `GET /review/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.
- `GET /review`, `/review/cards`, `/review/decks` and `/review/study`, with any query, the page:
  `web/dist/index.html`, revalidated on every load. Without a valid token each answers `303` to
  `/auth/refresh?return_to=<the path and query as received, URL encoded>`.
- `GET /review/assets/*`, the page's built files from `web/dist/assets/`, without a token, as the
  brain serves its own: they are the same for everyone and hold no data.

Everything else is `404`, and any request that changes state from another origin is `403`, before
routing, see "How review authenticates a request" below. An error the service did not plan for is
an RFC 9457 problem document, `application/problem+json` with `status`, `title` and `detail`,
through ASP.NET Core's `AddProblemDetails`, which the page's client reads as the memory centre's
reads the brain's.

The routes under `/review/api/` are pinned by `contracts/review/openapi.json`, see "The schema"
below.

## The page

`web/`, a frontend like any other, a `web` folder under its service, so CI and the agents gate and
build it with the rest, see `design/ui/README.md`. It is the memory centre's stack at its versions,
`Brain/web/package.json`, and depends on the components as
`"@platform/ui": "file:../../design/ui"`. Its `index.html` includes the nav's lines from
"Navigation" in `High-level/docs/platform-architecture.md`, with `<platform-nav current="review">`,
and its content sits in `<main class="page">`.

The page is an `h1`, "Review", a line under it, then the package's `Tabs`, one per section, each
with its own address:

| Section | Address         |
| ------- | --------------- |
| Study   | `/review`       |
| Cards   | `/review/cards` |
| Decks   | `/review/decks` |

`/review/study` is the address of a study session, which a later slice builds, and shows the Study
tab until then. Each section shows the package's `EmptyState` until its slice lands. The address is
the state, through the History API and no router library (`src/address.ts`): a tab pushes its
section's address, Back and Forward come back as `popstate`, and an address that names no section
shows a not found state under the tabs. The list of sections lives in `src/sections.ts`, and the
addresses the service answers the page for in `src/Review/Pages/PageEndpoints.cs`; the two change
together.

`src/api/client.ts` is the page's one way to call review: every call goes to `/review/api/` with the
session cookie, a `401` is refreshed once, shared by every call that got one, by posting to
`/auth/refresh` and repeating the call, and a second `401` sends the browser to
`/auth/refresh?return_to=<path>`, as the memory centre's client does. Its GETs are typed from
`src/api/schema.d.ts`.

## The schema

`Microsoft.AspNetCore.OpenApi` generates the OpenAPI document of the API at
`GET /review/api/openapi.json`, needing no token, only when `REVIEW_EXPOSE_OPENAPI=1`, so the
running service serves it nowhere. `ContractTests` writes it with keys sorted, a two space indent
and a trailing newline and checks that it equals `contracts/review/openapi.json` byte for byte,
see `contracts/README.md`. Every route has a summary and named response types, so a change to
either is a diff to review. After changing a route, from this directory:

```zsh
UPDATE_CONTRACTS=1 dotnet test --filter "FullyQualifiedName~ContractTests"
cd web && npm run generate:api
```

The second line writes `web/src/api/schema.d.ts` from the file with `tools/generate-schema.mjs`,
the memory centre's tool, and `src/api/schema.test.ts` fails when it is stale. The app is named
for its own assembly, so the document is the same under the tests' host as in the service.

## Running it

From this directory, with the .NET SDK `global.json` pins installed, and the page built first,
since review refuses to start without `web/dist/index.html`, naming it:

```zsh
(cd web && npm ci && npm run build)
REVIEW_APP_DATABASE_URL="postgres://review_app:<password>@127.0.0.1:5432/review" \
  dotnet run --project src/Review -c Release --no-launch-profile
```

The database is migrated first, as the owner, by `dotnet run --project src/Review -c Release
--no-launch-profile -- migrate`, see "Migrating" below. Locally, from the repository root,
`bash scripts/agent/services.sh run review migrate` and `bash scripts/agent/services.sh run review
start` do both, with the strings built from `infra/.env`.

`dotnet run` runs it from this directory, where `web/dist` is, which `RunWorkingDirectory` in
`src/Review/Review.csproj` sets; the Web SDK would otherwise run it from the project's folder. Its
entry in `infra/services.json` starts it the same way, with `REVIEW_LISTEN_ADDR` set to the host
and port the manifest gives. Port 8083 sits beside the brain's 8082, and the Caddyfile's upstream
for `/review` points at it.

Its tests, the steps `.github/workflows/dotnet.yml` runs, against the Postgres in `infra/`:

```zsh
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore -c Release
REVIEW_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" \
REVIEW_APP_DATABASE_URL="postgres://review_app:$(grep '^REVIEW_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/review" \
  dotnet test --no-build --no-restore -c Release
```

The tests that need a database each get one of their own, from
`tests/Review.Tests/Harness/TestDatabase.cs`: once per run it creates a template database,
migrated as `review_owner`, and each test a copy of it, through the Postgres superuser's string,
`REVIEW_TEST_ADMIN_DATABASE_URL`, which only the test step holds, the pattern of the brain's
`tests/harness.py`. Tests that act as review does connect as `review_app`, through
`REVIEW_APP_DATABASE_URL` pointed at their copy. Both are required, and unset is a failure, never a
skip. Tests that need no database take none, and run without either.

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

Review reads its settings from the process environment, once, at startup, into one immutable
`ReviewSettings`, in `src/Review/Settings/ReviewSettings.cs`. It loads no `.env` file. Unset means
the default, and a value that is set must parse, or review refuses to start, naming the setting,
rather than quietly using the default. The rules are canary's, written again rather than shared,
since no service imports from another's directory.

- `REVIEW_LISTEN_ADDR`, default `127.0.0.1:8083`: where Kestrel listens, a host and a port, an
  IPv6 host in brackets.
- `REVIEW_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `REVIEW_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.
- `REVIEW_EXPOSE_OPENAPI`, default `0`: `1` maps the schema's route, for the contract test and
  regenerating the file, see "The schema". Nothing else is accepted.
- `REVIEW_APP_DATABASE_URL`, required to serve, with no default: review's database as
  `review_app`, `postgres://review_app:<password>@127.0.0.1:5432/review`, the password percent
  encoded. It is parsed once, by `src/Review/Settings/DatabaseUrl.cs`, into Npgsql's settings: a
  `postgres` or `postgresql` scheme, a user and a password, a host, a port, 5432 when left out, and
  the database, with no query. It holds a password, so it is never shown, in an error or in the
  settings' `ToString`, and is set on the command that starts review rather than written to a file.
- `REVIEW_OWNER_DATABASE_URL`, the same form as `review_owner`, read by `migrate` alone, see
  "Migrating". Serving never reads it, and `services.sh` unsets it for `start`.

The defaults are right on the Mac mini and in CI, so neither sets them, except the listen address,
which the manifest's `start` sets, and the two database strings, which `services.sh` builds from
`infra/.env`. Later slices add `REVIEW_DAY_ZONE` and `REVIEW_BRAIN_URL`.

## The database

Review has a database of its own, `review`, beside the core's and the brain's, in the one Postgres
in `infra/`, created with its two roles by `infra/postgres/initdb/review.sh`, see "The databases"
in `infra/README.md`. Why a database per service, and why the roles are split, is in "Databases,
migrations, and running it" and "Multi user, and how isolation is enforced" in
`High-level/docs/platform-architecture.md`.

- **`review_owner`** owns the tables and runs the migrations. Nothing serving connects as it.
- **`review_app`** is what review serves as. It owns nothing, so row level security applies to it,
  and `migrations/0001_default_privileges.sql` grants it `SELECT`, `INSERT`, `UPDATE` and `DELETE`
  on every table the owner creates and `USAGE` on every sequence, never `TRUNCATE`, and takes
  `CREATE` on `public` from everyone but its owner. A table that needs less revokes it in its own
  migration. Unlike the brain, which deletes nothing, review deletes cards.

### Migrating

Migrations are plain SQL files in `migrations/`, named `NNNN_snake_description.sql`, forward only:
an applied file is never edited, renamed or removed, and a change is a new file. They are embedded
in the assembly, so the command reads them wherever it runs. They run only as a command, never at
startup, since the app role is not allowed to:

```zsh
REVIEW_OWNER_DATABASE_URL="postgres://review_owner:<password>@127.0.0.1:5432/review" \
  dotnet run --project src/Review -c Release --no-launch-profile -- migrate
```

The command, `src/Review/Database/Migrations/`, is the brain's `brain migrate` written again in C#,
with the same rules. It reads `REVIEW_OWNER_DATABASE_URL` and nothing else, takes a session
advisory lock, whose key is the first 8 bytes of SHA-256 of `review.migrate` as a big endian
signed 64 bit integer, so two runs cannot overlap, and records each file in `schema_migrations
(version, name, applied_at)`. Each new file runs in its own transaction with its ledger row, so a
failing file leaves nothing behind and stops the run before any later file. It prints `applied
<version> <name>` per file, or `nothing to do, every migration is applied`. It refuses, applying
nothing, when the files and the ledger disagree: a file name it cannot order, two files with one
version, an applied file renamed or gone, or a new file numbered below one already applied. With
no argument review serves, and any argument but `migrate` is refused with the usage.

### The door

Every query goes through `ReviewDb.InUserTransaction(userId, work, cancellationToken)`, in
`src/Review/Database/ReviewDb.cs`, which takes a connection from the pool, opens a transaction,
sets `review.user_id` for that transaction alone with `set_config('review.user_id', @id, true)`,
the `SET LOCAL` the isolation section requires, runs the work with the transaction, and commits,
or rolls back if the work throws. It refuses the empty user id, which names nobody. The work writes
its SQL out, mapped to records with Dapper, so the query a policy guards is the one in the code.

`ReviewDb` holds one `NpgsqlDataSource` as `review_app`, opened at startup with one connection,
so a database it cannot reach, or a wrong password, stops review there, naming
`REVIEW_APP_DATABASE_URL` and never its password. It opens every connection review makes, the
owner's one for `migrate` included, and `IsolationTests.OnlyTheDoorOpensConnections` checks by
reflection that no other type in the assembly holds a connection or a data source in a member.

Every scoped table has `user_id uuid NOT NULL`, row level security enabled and forced, and one
policy, `USING (user_id = nullif(current_setting('review.user_id', true), '')::uuid)` with the same
`WITH CHECK`, the expression `ReviewDb.Guard` holds. With no user set it is null, so a query
outside the door returns nothing rather than everything, and a row written for another user is
refused. `tests/Review.Tests/IsolationTests.cs` proves it on a probe table, and fails for any table a
later migration adds to `public` without exactly that one policy, `schema_migrations` aside. There
is no path around the policies yet: purging and exporting, if review needs them, are planned later.

## How review authenticates a request

The rules every feature follows are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`. Review holds none of the code for them: `PlatformAuth`, in
`libs/dotnet/PlatformAuth/`, does every check of the token and the origin, and its README says what
it holds and how its tests cover the ways verification breaks. Review references it as a project
and does three things with it, in `src/Review/ReviewApp.cs`:

- `AddPlatformAuth` with `REVIEW_CORE_URL`, which registers the key cache, the handler and the two
  policies. The pages take `PlatformAuthPolicies.Page` and `whoami` takes
  `PlatformAuthPolicies.Api`; health and the built files take neither.
- `UsePlatformOriginCheck` with `REVIEW_PUBLIC_ORIGIN`, before routing, so every route, one added
  later included, is covered. Review has no route that changes state yet.
- `ReviewApp.Build(settings, webRoot, configure)` builds the app; `configure` runs before it is
  built, which is where `Program` sets the listen address and where the tests put the app on a
  test server, with a key cache over the committed key set answered from memory and a clock fixed
  at the committed token's `iat`, both of which `AddPlatformAuth` keeps.

The tests send the ways verification is known to break through review's own routes, as "A
feature's authentication tests are part of the feature template" in
`High-level/docs/how-we-build.md` asks: the committed token is accepted from the cookie and the
header, a missing, expired, wrongly signed or unknown key id token gets `401` from the API, and a
page without a token answers `303` to the refresh endpoint with the way back. That every kind of
invalid token is the same to a page is `PlatformAuth`'s own test.

## Libraries review is built on

**The .NET 10 SDK, pinned in `global.json`**, with `Directory.Build.props`,
`Directory.Packages.props` and `.editorconfig` holding the same values as `PlatformAuth`'s folder,
copies rather than shared files, since each C# folder builds from its own directory alone. Why .NET
10, the analyzers as the linter and `dotnet format` as the formatter are in "Libraries it is built
on" in `libs/dotnet/PlatformAuth/README.md`.

**One web project, not layered projects.** `src/Review/` is the whole service, organised in
feature folders: `Settings`, `Database` (the door and the migrations), `Health` (health and whoami)
and `Pages` today, more with later slices. The service is small, and folders inside one project keep it readable without Clean
Architecture's ceremony.

**ASP.NET Core minimal APIs**, from the shared framework, for routing, static files and problem
details, with no MVC. JSON is snake case on the wire, as the brain's and canary's is.

**Microsoft.AspNetCore.OpenApi 10.0.15 for the schema**, the generator that ships with ASP.NET
Core, so the document follows the routes and their types without annotations of a second library.
Swashbuckle was the alternative, which ASP.NET Core no longer includes by default.

**Npgsql 10.0.3**, the PostgreSQL driver for .NET, **with Dapper 2.1.89** for mapping rows to
records, with the SQL written out where it runs, the same shape as sqlx in the core and psycopg in
the brain, so the query a policy guards is visible. EF Core was the alternative and was turned
down: migrations here are plain SQL, so its migrations would go unused, its row level security
setting would sit in an interceptor rather than in one visible door, and its generated SQL makes
"which query does the policy guard" harder to read.

**A migrator of its own, not DbUp.** DbUp's journal does not refuse an applied file that was
renamed or removed, which forward only depends on, and the brain's rules are short enough to write
again.

**xUnit v3 for the tests**, with `Microsoft.AspNetCore.Mvc.Testing` for `TestServer` and
`Microsoft.Extensions.TimeProvider.Testing` for the clock, the five test packages `PlatformAuth`
uses, at its versions.

**The page** is the memory centre's stack, React, Vite, Vitest, Biome and TypeScript, at its
versions, without React Query, which nothing here needs until a section loads data; see
"Libraries the brain is built on" in `Brain/README.md` for why each was chosen.
