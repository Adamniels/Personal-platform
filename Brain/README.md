# brain

The platform's memory, a service of its own that every feature depends on and that is not itself a
feature. Why it is a separate service is in "The brain is its own service" in
`High-level/docs/platform-architecture.md`; what it will hold is in `docs/`, starting at
`docs/brain-architecture.md`, with the tables in `docs/data-model.md`.

Today it serves a health endpoint, says who is asking, and serves the memory centre, its own
application, whose sections are still empty. It has a database with no table in it yet: the roles,
the migrations and the one door every query goes through, proven to keep two users apart. The
tables arrive with the slices that need them.

## What it serves

- `GET /brain/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what the brain depends on.
- `GET /brain/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.
- `GET /brain` and `GET /brain/<section>`, the memory centre's `index.html` from the build, see
  "The memory centre" below. Without a valid token, a `303` to the core's
  `/auth/refresh?return_to=<the path>`.
- `GET /brain/assets/<file>`, the build's files, with or without a session, since they hold code
  and never data.

**Who is asking.** Every route under `/brain/api/` except `health` needs a valid access token, and
the brain checks it with `platform-auth`, the library in `libs/python/platform-auth/`, as every
Python service does: the token from the `Authorization` header or the `__Host-access` cookie,
verified against the core's key set, and a request without a valid one answered `401` with an
empty body, never a redirect, since a script cannot follow one. The memory centre's pages are the
exception: they go through the library's `page` wrapper instead, which sends a browser without a
valid token to the core's refresh endpoint with the way back. The user id in the
brain is always the verified token's `Claims.account_id`, never a header, a parameter or a body
field. `authenticated` and `page` in `src/brain/app.py` put the library's `api` and `page`
wrappers on a FastAPI route, so FastAPI sees a handler that takes only the request. In front of the whole app, routing
included, the library's `OriginCheck` refuses with `403` any request other than GET, HEAD and
OPTIONS whose `Origin` is not exactly `BRAIN_PUBLIC_ORIGIN`.

Every route sits under `/brain`, which the Caddyfile sends here with the prefix kept, so the
brain's routes are written with it: the API under `/brain/api/`, the memory centre beside it.
Everything else is `404`, FastAPI's own `/docs`, `/redoc` and `/openapi.json` included: they would
sit outside the prefix, and through the proxy would never reach the brain anyway. The brain has no
entry in `infra/features.toml`, since it is not a feature and has nothing for the front page; the
nav bar's link to the memory centre is fixed in `design/nav.js`, see "Navigation" in
`High-level/docs/platform-architecture.md`.

The schema FastAPI generates from the routes and their models is committed as
`contracts/brain/openapi.json`, and `tests/test_contract.py` fails when the app no longer generates
it byte for byte. The memory centre's page routes are left out of it, since they are not API. Changing what the brain serves means regenerating it, with
`UPDATE_CONTRACTS=1 uv run pytest tests/test_contract.py`, and reviewing the diff, see
`contracts/README.md`.

## Running it

From this directory, with uv installed and the stack in `infra/` up. Each command that touches the
database takes its connection string from the environment, built from `infra/.env` on the command
line so no password is written to a file:

```zsh
BRAIN_OWNER_DATABASE_URL="postgres://brain_owner:$(grep '^BRAIN_OWNER_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/brain" \
  uv run brain migrate
BRAIN_APP_DATABASE_URL="postgres://brain_app:$(grep '^BRAIN_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/brain" \
  uv run uvicorn brain.app:app --host 127.0.0.1 --port 8082 --reload
BRAIN_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" \
BRAIN_APP_DATABASE_URL="postgres://brain_app:$(grep '^BRAIN_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/brain" \
  uv run pytest
uv run ruff check
uv run ruff format --check
uv run mypy
```

The tests need both strings, and fail rather than skip without them. Each test that touches the
database gets one of its own, copied from a template migrated once per run, and connects as
`brain_app`; `tests/harness.py` says how.

`uv run` builds `.venv` from `uv.lock` on first use. Port 8082 is the brain's everywhere, beside
the core's 8080 and canary's 8081. It stays a uvicorn flag rather than a setting, since uvicorn
binds it before the brain's code runs.

## The memory centre

The brain's own application, `web/`, over its own data, described in "The brain owns its own
surface" in `docs/brain-architecture.md`. It is a frontend like any other, a `web` folder under its
service, so CI and the agents gate and build it with the rest, see `design/ui/README.md`; it
depends on the components there as `"@platform/ui": "file:../../design/ui"`.

The page is an `h1`, "Memory", a line under it, then the package's `Tabs`, one per section, each
with its own address:

| Section    | Address             |
| ---------- | ------------------- |
| Profile    | `/brain`            |
| Rules      | `/brain/rules`      |
| Knowledge  | `/brain/knowledge`  |
| Now        | `/brain/now`        |
| History    | `/brain/history`    |
| Everything | `/brain/everything` |
| Timeline   | `/brain/timeline`   |
| Ask        | `/brain/ask`        |

The address is the state, through the History API and no router library (`src/address.ts`): a tab
pushes its section's address, Back and Forward come back as `popstate`, and an address that names
no section shows a not found state under the tabs. The brain answers `index.html` for any single
segment under `/brain/` other than `api` and `assets`, so the list of sections lives in
`src/sections.ts` alone. Until its slice lands, a section shows the package's `EmptyState`.

`index.html` is the same shell as every page with the bar: the design files and `nav.js` from the
proxy, not bundled, and `<platform-nav current="brain">`. Vite builds it with base `/brain/` into
`web/dist`, git ignored, its files under `dist/assets/`.

**Calling the brain.** `src/api/client.ts` is the one way the application calls the API: every call
to `/brain/api/` with the session cookie, a state changing one with a JSON body. On a `401` it posts
once to `/auth/refresh` with a form encoded `return_to`, the current path, and repeats the call;
calls that get a `401` together share that refresh. A second `401` sends the browser to
`/auth/refresh?return_to=<the path>`, and the core decides whether a login is needed. Any other
failure, the network's included, is an `ApiError` carrying a typed `Problem`: the status, a title
and a detail read from a problem document or from FastAPI's `detail`. React Query holds what the
server answered, and every failed query or change raises an error toast, in one place,
`src/providers.tsx`.

`src/toast/` is a stand in for the package's toast until #192 adds `ToastProvider` and `useToast` to
`@platform/ui` with the same shape. It holds errors alone; replace it with the package's then.

**The API's types.** `src/api/schema.d.ts` is generated from `contracts/brain/openapi.json` by
`tools/generate-schema.mjs` and committed, and `src/api/schema.test.ts` fails when it is stale. After
the schema changes, regenerate it with `npm run generate:api` and review the diff. `client.get`
takes its paths and answer types from it.

From `web/`, with Node 26:

```zsh
npm ci
npx biome ci .
npx tsc --noEmit
npx vitest run
npm run build
```

The brain serves `web/dist` once it is built; it has no dev server of its own.

## Settings

The brain reads three settings from the process environment, once, in `src/brain/settings.py`. It
loads no `.env` file; `.env.example` lists them for whoever exports them. Unset means the default,
and a value that is set must parse, or the brain refuses to start rather than quietly using the
default. The rules are canary's, written out again rather than imported, since no service imports
from another's directory.

- `BRAIN_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `BRAIN_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.
- `BRAIN_WEB_DIR`, default `web/dist`: where the memory centre's build is, absolute or relative to
  the directory the brain runs from. It must not be empty. Whether it holds a build, an
  `index.html` and an `assets/` folder, is checked when the brain starts, which stops with an error
  naming the setting when it does not; the settings themselves load without one, so the brain's
  tests and its CI job need no build.
- `BRAIN_APP_DATABASE_URL`, required, with no default: the brain's database as `brain_app`, such as
  `postgres://brain_app:<password>@127.0.0.1:5432/brain`. It holds a password, so it is never
  shown, in an error or in the settings' `repr`, and is set on the command that starts the brain
  rather than written to a file.

The defaults are right on the Mac mini and in CI, so neither sets them. The database address
has no default, and every place that starts the brain sets it.

## The database

The brain has a database of its own, `brain`, beside the core's, in the one Postgres in `infra/`,
created with its two roles by `infra/postgres/initdb/brain.sh`, see "The databases" in
`infra/README.md`. Why a database per service, and why the roles are split, is in "Databases,
migrations, and running it" and "Multi user, and how isolation is enforced" in
`High-level/docs/platform-architecture.md`.

- **`brain_owner`** owns the tables and runs the migrations. Nothing running connects as it.
- **`brain_app`** is what the running brain connects as. It owns nothing, so row level security
  applies to it, and `migrations/0001_default_privileges.sql` grants it `SELECT`, `INSERT` and
  `UPDATE` on every table the owner creates, never `DELETE` or `TRUNCATE`. A table that needs less
  revokes it in its own migration.

### Migrating

Migrations are plain SQL files in `migrations/`, named `NNNN_snake_description.sql`, forward only:
an applied file is never edited, renamed or removed, and a change is a new file. They run only as a
command, `uv run brain migrate`, never at startup, since the app role is not allowed to.

The command reads `BRAIN_OWNER_DATABASE_URL` from the environment and nothing else, takes a session
advisory lock so two runs cannot overlap, and records each file in `schema_migrations (version,
name, applied_at)`. Each new file runs in its own transaction with its ledger row, so a failing file
leaves nothing behind and stops the run before any later file. It prints `applied <version>
<name>` per file, or `nothing to do, every migration is applied`. It refuses, applying nothing,
when the files and the ledger disagree: a file name it cannot order, two files with one version, an
applied file renamed or gone, or a new file numbered below one already applied.

### The door

Every query goes through `brain.db.transaction(pool, user_id)`, an async context manager that takes
a connection from the pool, opens a transaction and sets `brain.user_id` for that transaction
alone, with `set_config(..., true)`, the `SET LOCAL` the isolation section requires. Nothing else
in the package takes a connection from the pool, and a test checks it. The pool, a
`psycopg_pool.AsyncConnectionPool` as `brain_app`, is opened in the app's lifespan and waits for its
first connections, so a database it cannot reach stops the brain at startup.

Every scoped table follows "What holds for every table" in `docs/data-model.md`: `user_id uuid NOT
NULL`, row level security enabled and forced, and one policy,
`USING (user_id = nullif(current_setting('brain.user_id', true), '')::uuid)`, the expression
`brain.db.CURRENT_USER_SQL` holds. With no user set it is null, so a query outside the door
returns nothing rather than everything. `tests/test_isolation.py` proves it on a probe table, and
fails for any table a later migration adds to `public` without that guard, or with any other
permissive policy beside it, except the global ones the data model names.

## Building the app

`create_app(settings, keys=None)` in `src/brain/app.py` builds the app, so tests build one from
settings of their own. When the app starts, it checks the memory centre's build, opens the database
pool and, without `keys`, creates one HTTP client and one key cache over it; when it stops, it
closes what it opened, in reverse. Tests pass a cache of their own, over the committed key set
answered from memory, and verify the committed token, see `contracts/README.md`. That token
expires at a fixed time, so the tests pin the clock the library reads, with the `at` fixture in
`tests/conftest.py`, beside the contract token, a folder shaped like the build and the database
fixtures. A test that builds the app without starting it loads `MINIMAL_ENV` from
`tests/harness.py`, a well formed database address nothing connects to; one that starts it is
given a database of its own.

## Libraries the brain is built on

**uv, a packaged `src/` layout, uvicorn, pytest and ruff**, as in `canary/`, for the reasons its
README gives.

**FastAPI for HTTP.** The brain is the platform's widest API: every feature calls it, with request
bodies to validate, so typed request and response models and a schema generated from them pay for
themselves here, which they did not in canary. FastAPI is Starlette underneath, so middleware
written for canary, such as its `Origin` check, carries over unchanged. Its schema is what the
brain's callers are written against.

**mypy, in strict mode, over `src` and `tests`**, configured in `pyproject.toml`. The brain's
models are its contract, so the types are checked rather than decorative. pyright was the
alternative, faster, but its Python package downloads Node when it runs, which does not suit an
unattended machine.

**platform-auth for who is asking**, a path dependency on `../libs/python/platform-auth`, the
one copy of token verification and the `Origin` check every Python service installs. What it holds
and why it is shared is in its README.

**The memory centre's libraries**, in `web/package.json`, pinned exactly: the same React,
TypeScript, Vite, Vitest, Testing Library, jsdom and Biome as `design/ui`, with the same settings;
`@tanstack/react-query` 5.107.1 for what the server holds, so a change refreshes every view that
shows it; `openapi-typescript` 7.13.0 to generate the API's types; and `@types/node` 26.6.6 for the
one test that runs the generator. `openapi-typescript` builds its output with the TypeScript
compiler's JavaScript API, which TypeScript 7 no longer exports, and declares a peer of
TypeScript 5. So `package.json` installs TypeScript 5.9.3 beside 7 under the name `typescript-5`,
an override tells npm that openapi-typescript's peer is the project's TypeScript, and the
generator, alone, resolves its `typescript` import to the alias. `npx tsc` stays TypeScript 7.
Remove the three once openapi-typescript works with TypeScript 7.

**httpx2 for HTTP calls**, a runtime dependency, since the brain creates the client the key cache
fetches the core's key set with. FastAPI's `TestClient` is Starlette's and uses it too.

**psycopg 3, with its pool, for Postgres**, `psycopg[binary,pool]`. The standard Postgres driver,
with plain SQL and no ORM, so every transaction and the user setting the door sets stay visible in
the code rather than inside a library. It is async, as FastAPI is, and its pool is the one the door
takes connections from. The binary extra ships libpq, so no Postgres client has to be installed.

**pytest-asyncio**, a dev dependency, so tests can be async and use the pool the way the app does.
