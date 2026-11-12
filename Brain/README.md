# brain

The platform's memory, a service of its own that every feature depends on and that is not itself a
feature. Why it is a separate service is in "The brain is its own service" in
`High-level/docs/platform-architecture.md`; what it will hold is in `docs/`, starting at
`docs/brain-architecture.md`, with the tables in `docs/data-model.md`.

Today it serves a health endpoint and says who is asking, and nothing else. The database arrives
with the slice that needs it.

## What it serves

- `GET /brain/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what the brain depends on.
- `GET /brain/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.

**Who is asking.** Every route under `/brain/api/` except `health` needs a valid access token, and
the brain checks it with `platform-auth`, the library in `libs/python/platform-auth/`, as every
Python service does: the token from the `Authorization` header or the `__Host-access` cookie,
verified against the core's key set, and a request without a valid one answered `401` with an
empty body, never a redirect, since nothing loads a brain page in a browser. The user id in the
brain is always the verified token's `Claims.account_id`, never a header, a parameter or a body
field. `authenticated` in `src/brain/app.py` puts the library's `api` wrapper on a FastAPI route,
so FastAPI sees a handler that takes only the request. In front of the whole app, routing
included, the library's `OriginCheck` refuses with `403` any request other than GET, HEAD and
OPTIONS whose `Origin` is not exactly `BRAIN_PUBLIC_ORIGIN`.

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

From this directory, with uv installed:

```zsh
uv run uvicorn brain.app:app --host 127.0.0.1 --port 8082 --reload
uv run pytest
uv run ruff check
uv run ruff format --check
uv run mypy
```

`uv run` builds `.venv` from `uv.lock` on first use. Port 8082 is the brain's everywhere, beside
the core's 8080 and canary's 8081. It stays a uvicorn flag rather than a setting, since uvicorn
binds it before the brain's code runs.

## Settings

The brain reads two settings from the process environment, once, in `src/brain/settings.py`. It
loads no `.env` file; `.env.example` lists them for whoever exports them. Unset means the default,
and a value that is set must parse, or the brain refuses to start rather than quietly using the
default. The rules are canary's, written out again rather than imported, since no service imports
from another's directory.

- `BRAIN_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `BRAIN_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.

The defaults are right on the Mac mini and in CI, so neither sets them.

`create_app(settings, keys=None)` in `src/brain/app.py` builds the app, so tests build one from
settings of their own. Without `keys` it creates one HTTP client and one key cache over it when the
app starts, and closes the client when it stops; tests pass a cache of their own, over the
committed key set answered from memory, and verify the committed token, see `contracts/README.md`.
That token expires at a fixed time, so `tests/test_whoami.py` pins the clock the library reads.

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

**httpx2 for HTTP calls**, a runtime dependency, since the brain creates the client the key cache
fetches the core's key set with. FastAPI's `TestClient` is Starlette's and uses it too.
