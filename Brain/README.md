# brain

The platform's memory, a service of its own that every feature depends on and that is not itself a
feature. Why it is a separate service is in "The brain is its own service" in
`High-level/docs/platform-architecture.md`; what it will hold is in `docs/`, starting at
`docs/brain-architecture.md`, with the tables in `docs/data-model.md`.

Today it serves a health endpoint, says who is asking, holds each user's subjects, the topics and
named things their memory will be about, records the events features send it and shows them as a
timeline, holds the memory items the owner enters by hand, profile entries and rules, each with the
event that proves it was said, and serves the memory centre, its own application, whose sections are
still empty. Its database has the roles, the migrations, the one door every query goes through,
proven to keep two users apart, the subjects' three tables, the verbs and the events, and the
memory store's four tables, every table in `docs/data-model.md`.

## What it serves

- `GET /brain/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what the brain depends on.
- `GET /brain/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.
- **Subjects**, what a user's memory is about, see "Scope is topic and entity, never feature" in
  `docs/trust-model.md`. Each route needs a valid token, acts for its account alone, and answers
  `{"detail": ...}` when it refuses: `404` for an id the user does not have, another user's
  included, `409` for a second of something unique, and `422` for a body that does not parse or a
  request that breaks a rule. The rules live in `src/brain/topics.py` and `src/brain/entities.py`,
  the routes in `src/brain/subjects.py`.
  - `GET /brain/api/topics`, every topic the user has, aliases included, by slug: `id`, `slug`,
    `name`, `state` (`canonical` or `provisional`), `alias_of`, `resolves_to`, the topic it is read
    as, and `created_at`.
  - `POST /brain/api/topics`, `{"name": ..., "slug": ...}`, a new canonical topic, `201`. A slug is
    lowercase letters, digits and hyphens, starting with a letter or digit, 1 to 80 characters;
    without one it is made from the name, lowercased with every other run of characters one hyphen.
    One the user already has is `409`. A provisional topic is made only by an event.
  - `POST /brain/api/topics/{id}/promote` makes a provisional topic canonical.
  - `POST /brain/api/topics/{id}/same-as`, `{"target_id": ...}`, makes the topic an alias of a
    canonical one. Only `alias_of` changes: whatever names the alias keeps naming it and is read
    as the target, so `separate` loses nothing. Refused when the target is itself, an alias or
    provisional, when the topic is an alias or has aliases, or when one is under the other.
  - `POST /brain/api/topics/{id}/separate` makes an alias its own topic again.
  - `POST /brain/api/topics/{id}/broader`, `{"broader_id": ...}`, places the topic under a broader
    one, `201`, a `broader_than` relation from the broader topic to this one. Refused when it would
    put a topic under itself, directly, through others or through an alias.
  - `POST /brain/api/topic-relations/{id}/end` ends a relation. The row stays, with `ended_at`.
  - `GET /brain/api/entities` and `POST /brain/api/entities`, `{"name": ..., "label": ...}`, the
    user's named things, such as a project: a name compared without letter case, so a second one
    in any case is `409`, and an optional free text label. Only a name, never a link into a
    feature's data.

  Every change to a user's topics or their relations takes that user's lock for its transaction,
  `topics.lock_vocabulary`, since the checks above read rows another change could be writing.
  `topics.resolve` and `topics.narrower`, every topic under one through relations that hold,
  aliases resolved, are what retrieval reads them with.
- **Events**, what happened, in the shape of "The shape" in `docs/event-model.md`: the endpoint
  every feature sends what it observed to, "record event" in "The two contracts" in
  `High-level/docs/feature-contract.md`. The rules live in `src/brain/events.py`, the routes in
  `src/brain/event_routes.py`, and they answer and refuse as the subjects' routes do.
  - `POST /brain/api/events`, `201` with the event. The body carries `verb`, one of the verbs
    seeded from "The verb list" in `docs/data-model.md`; at most one of `topic`, a slug, and
    `entity`, a name; optionally `item`, the id of one of the user's memory items, for
    what the owner did to it, `422` when they have none by it; `origin`, the sending feature's name, by the slug's rule, never `brain`, which
    is the brain's own; optional `source` and `run`; `occurred_at`, with a zone, and
    `occurred_precision`, `instant` (the default), `day`, `month` or `year`, both standing for
    the time it was recorded when left out; `evidence`, readable text, not blank, at most 4000
    characters; `payload`, a JSON object of at most 16 KiB as compact JSON, opaque to the brain;
    `payload_version`, 1 or more; and `idempotency_key`. A field it does not know is `422`, so a
    misspelt one is not lost. `recorded_at` is the brain's own time.
  - Sent again with the same `origin` and `idempotency_key`, it is `200` with the event already
    recorded, unchanged, whatever else the second one carries. The key is the user's: another
    origin's, or another user's, is another event.
  - A slug the user has no topic for makes a `provisional` topic, named by its slug, and a name
    they have no named thing for makes one, with no label, both silently. An event names a topic
    as it was given, an alias included, and is read through it: its `subject_topic` is the one it
    named, and `topic` is the one that resolves to now, so marking topics the same or separating
    them rewrites no event.
  - `GET /brain/api/events`, the timeline: the user's events, newest recorded first, `limit` of
    them (50 by default, 1 to 200), and with `before`, a time with a zone, only those recorded
    before it. The next page is `before` the last one's `recorded_at`.

  An event is never changed or removed: `brain_app` may insert and read `events`, never update it,
  and may only read `verbs`, which is global. A new verb is a migration that adds its row, with its
  kind, strength and polarity.
- **Memory items**, what the brain holds about the user, in the stores of "Which kind goes in which
  store" in `docs/data-model.md`. The rules live in `src/brain/items.py`, the routes in
  `src/brain/item_routes.py`, and they answer and refuse as the subjects' routes do.
  - `POST /brain/api/items`, an entry made by hand, `201` with the item as a single read answers
    it. The body is told apart by `kind`: `profile`, with a `category`, one of `identity`, `goals`,
    `values`, `interests`, `preferences` and `working_style`, and optional `topics`, slugs, and
    `entities`, names, at most 20 of each; or `rule`, which is global, so it takes no subject and
    no category. Both carry `content`, 1 to 4000 characters once its surrounding whitespace is
    dropped, and an optional `occurred_at`, with a zone, and `occurred_precision`, for when it
    became true. A topic the user does not have is `422`, since a hand made entry names one that
    exists; a named thing they do not have is created, with no label. A field it does not know is
    `422`.
  - The entry applies at once, as "Standing rules are created deliberately" in
    `docs/trust-model.md` says: one transaction records an `asserted` event through the same path
    as the events endpoint, with origin `brain`, source `manual-entry`, evidence
    `Entered by hand as <kind>: "<content>"` cut to 500 characters and a payload naming the item,
    then the item, `active`, `asserted` and the first of its lineage, with `created_by_event`
    naming the event, then its subjects, then a `supports` row in `item_evidence`. A failure at any
    step leaves none of it.
  - `GET /brain/api/items`, the user's items, newest first, each with its `topics`, read through
    their aliases, and its `entities`, narrowed by any of `kind`, `status` (`active` or
    `inactive`), `topic`, a slug, and `entity`, a name. A topic matches through aliases on either
    side, and only itself, not the topics under it; a name matches without letter case.
  - `GET /brain/api/items/{id}`, one item with its subjects, its `evidence`, oldest first, each with
    its kind and the event's id, verb, origin, source, evidence and times, and its `relations`, on
    either end. What confidence and currency would be computed from, and neither is stored.
  - `POST /brain/api/items/{id}/edit`, a correction, `201` with the new version as a single read
    answers it. The body names the item's own `kind` and the fields that kind takes, each left out
    to keep what the item has: for `profile`, `content`, `category`, and `topics` and `entities`,
    which replace that kind of subject whole when given; for `rule`, `content` alone. One
    transaction, as "Versions" in `docs/data-model.md` says, inserts the new version with the same
    `lineage_id`, `active` and `asserted` whatever the old one was, holding the old version's
    everything else, its subjects included; sets the old one `inactive` with `superseded`; records
    an `edited` event against it, evidence `Corrected by hand from "<old>" to "<new>"` with each
    text cut to 200 characters, linked to it as `contradicts` evidence; records an `asserted` event
    that makes the new version and supports it; and links the new to the old with `supersedes`,
    made by the `edited` event. `422` for another kind, a field the kind does not take, a topic the
    user does not have, or an edit that changes nothing; `409` for a version that is not its
    entry's newest, or a retired item, which is restored before it is edited.
  - `POST /brain/api/items/{id}/retire` and `POST /brain/api/items/{id}/restore`, putting an item
    away and bringing it back, `200` with it as a single read answers it. Neither takes a body.
    Retiring sets it `inactive` with `retired`, and records a `retired` event against it, evidence
    `Retired by hand: "<content>"`, linked to it as `contradicts` evidence; restoring sets it
    `active` again and records a `restored` event, `Restored by hand: "<content>"`, linked as
    `supports`. Each evidence is cut to 500 characters, and each change is one transaction. `409`
    to retire an item already retired or a superseded version, whose newest version is the one to
    retire, and to restore anything not retired: only a retired item comes back. Retiring is the
    owner's action alone, as "Nothing is destroyed, and only the owner retires" in
    `docs/trust-model.md` says: nothing in the brain retires an item for its age or for a score.

  An item is never changed in place: `brain_app` may insert and read the four tables, and update
  only an item's `status` and `inactive_reason`. Which kind lives in which store, and its nature,
  is checked by the migration as well as the code, since the store decides who may change a claim.
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
field. `authenticated` and `page` in `src/brain/edge.py` put the library's `api` and `page`
wrappers on a FastAPI route, so FastAPI sees a handler that takes only the request. Such a handler
reads its own body and address, with `body_as` and `path_id` beside them, after the token is
checked, so a request without a valid one is `401` whatever its body; `documented` puts what it
reads in the schema. In front of the whole app, routing
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
`contracts/README.md`. It needs `BRAIN_APP_DATABASE_URL` set, as every test run does, since
`brain.app` loads the settings when it is imported; nothing connects to it.

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

A test that calls the running brain as more than one user, as the subjects' tests do, cannot use
the contract token, which names one account and whose private key nobody holds. `calling(user)` in
`tests/conftest.py` starts the brain on the test's database over a key set of the tests' own, and
mints each user a token signed with its private half, by the real clock. The signing is `pyjwt`
and `cryptography`, which `platform-auth` already installs.

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
