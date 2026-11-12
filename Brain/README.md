# brain

The platform's memory, a service of its own that every feature depends on and that is not itself a
feature. Why it is a separate service is in "The brain is its own service" in
`High-level/docs/platform-architecture.md`; what it will hold is in `docs/`, starting at
`docs/brain-architecture.md`, with the tables in `docs/data-model.md`.

Today it serves a health endpoint, and has a database with no table in it yet: the roles, the
migrations and the one door every query goes through, proven to keep two users apart.
Authentication and the tables arrive with the slices that need them.

## What it serves

- `GET /brain/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what the brain depends on.

Every route sits under `/brain/api/`, which the Caddyfile sends here with the prefix kept, so the
brain's routes are written with it. Everything else is `404`, FastAPI's own `/docs`, `/redoc` and
`/openapi.json` included: they would sit outside the prefix, and through the proxy would never
reach the brain anyway. The brain has no entry in `infra/features.toml`, since it is not a feature:
it is not in the nav and has nothing for the front page.

The schema FastAPI generates from the routes and their models is committed as
`contracts/brain/openapi.json`, and `tests/test_contract.py` fails when the app no longer generates
it byte for byte. Changing what the brain serves means regenerating it, with
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

## Settings

The brain reads three settings from the process environment, once, in `src/brain/settings.py`. It
loads no `.env` file; `.env.example` lists them for whoever exports them. Unset means the default,
and a value that is set must parse, or the brain refuses to start rather than quietly using the
default. The rules are canary's, written out again rather than imported, since no service imports
from another's directory.

- `BRAIN_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's. A
  scheme, a host and an optional port, with nothing after it.
- `BRAIN_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.

Neither is used by a route yet. Both are what verifying a token and checking the `Origin` of a
request that changes state need, and they arrive with authentication. The defaults are right on
the Mac mini and in CI, so neither sets them.

- `BRAIN_APP_DATABASE_URL`, required, with no default: the brain's database as `brain_app`, such as
  `postgres://brain_app:<password>@127.0.0.1:5432/brain`. It holds a password, so it is never
  shown, in an error or in the settings' `repr`, and is set on the command that starts the brain
  rather than written to a file.

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
fails for any table a later migration adds to `public` without that guard, except the global ones
the data model names.

`create_app(settings)` in `src/brain/app.py` builds the app, so tests build one from settings of
their own.

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

**httpx2 for the test client**, a dev dependency, since FastAPI's `TestClient` is Starlette's and
uses it.

**psycopg 3, with its pool, for Postgres**, `psycopg[binary,pool]`. The standard Postgres driver,
with plain SQL and no ORM, so every transaction and the user setting the door sets stay visible in
the code rather than inside a library. It is async, as FastAPI is, and its pool is the one the door
takes connections from. The binary extra ships libpq, so no Postgres client has to be installed.

**pytest-asyncio**, a dev dependency, so tests can be async and use the pool the way the app does.
