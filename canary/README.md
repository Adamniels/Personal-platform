# canary

The platform's first feature, written in Python on purpose. Why it exists, and why not in Rust, is
in "The first milestone" in `High-level/docs/platform-architecture.md`.

## What it serves

- `GET /canary`, its page: a heading and a line saying the token was verified in Python, with the
  account id from it, under the platform's nav. Without a valid token it answers `303` to
  `/auth/refresh?return_to=<the path and query as received, URL encoded>`.
- `GET /canary/api/whoami`, `{"account_id": "<sub>"}`. Without a valid token it answers `401` with
  an empty body.
- `GET /canary/api/display`, canary's display data for the front page, see "What a feature exposes
  for the front page" in `Core/docs/core-architecture.md`. Without a valid token it answers `401`
  with an empty body, like any API route, and in a display mode other than `normal` it hangs or
  fails before looking at the token, see Settings.

The display data is what canary knows, that it verified this person's token, and nothing meant to
be shown as it is: no label, colour, ordering or sentence, since the core decides how it looks.

```json
{
  "account_id": "01900000-0000-7000-8000-000000000001",
  "token_expires_at": 1800001800,
  "key_id": "kPrK_qmxVWaYVA9wwBF6Iuo3vVzz7TxHCTwXBygrS4k",
  "python_version": "3.14.0"
}
```

`account_id` is the token's `sub`, `token_expires_at` its `exp` in whole seconds since the epoch,
`key_id` the `kid` it was verified with, and `python_version` is `platform.python_version()`. The
shape is a contract with the core, pinned by `contracts/display/canary.json`:
`tests/test_display.py` checks that the real response has its keys, each with the same JSON type,
see `contracts/README.md`.

Everything else is `404`. The page includes the nav's two lines from "Navigation" in
`High-level/docs/platform-architecture.md` and a few lines of inline CSS of its own, the nav's
reserved height among them, and takes nothing else from the core: it does not link the core's
stylesheet.

## Running it

From this directory, with uv installed:

```zsh
uv run uvicorn canary.app:app --host 127.0.0.1 --port 8081 --reload
uv run pytest
uv run ruff check
uv run ruff format --check
```

`uv run` builds `.venv` from `uv.lock` on first use. Port 8081 sits beside the core's 8080, and the
Caddyfile's upstream for `/canary` points at it. The port stays a uvicorn flag rather than a
setting, since uvicorn binds it before canary's code runs.

## Settings

Canary reads three settings from the process environment, once, in `src/canary/settings.py`. It
loads no `.env` file; `.env.example` lists them for whoever exports them. Unset means the default,
and a value that is set must parse, or canary refuses to start rather than quietly using the
default.

- `CANARY_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `CANARY_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.
- `CANARY_DISPLAY_MODE`, default `normal`: what `GET /canary/api/display` does, for every request
  and before authentication, the way a broken feature would. `normal` serves the data, `error`
  answers `500`, and `hang` sleeps for an hour, far past the core's deadline, so the client gives
  up first. Any other value, a different case included, stops canary at startup.

The defaults are right on the Mac mini and in CI, so neither sets them.

**Watching the front page without canary.** The mode changes only with a restart, since a runtime
toggle would be a state changing endpoint of its own. Stop canary and start it again with the mode
set, from this directory:

```zsh
CANARY_DISPLAY_MODE=hang uv run uvicorn canary.app:app --host 127.0.0.1 --port 8081
```

and load the front page: the core's display fetch gives up at its deadline, and canary's card
shows the placeholder saying it could not be reached. `error` shows the same placeholder at once.
Restart without the variable to go back to `normal`. What the card shows is the core's, in
`Core/src/cards.rs` and `Core/templates/front.html`.

## How canary authenticates a request

The rules every feature follows are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`. Canary was the first to carry them, and its code for them is
now `platform-auth`, the library in `libs/python/platform-auth/` that every Python service
installs; its README says what the library holds, how its tests cover the ways verification breaks,
and what it must never grow into. Canary installs it as an editable path dependency and does three
things with it:

- `canary_page` takes `@auth.page`, so without a valid token it answers `303` to the core's refresh
  endpoint with the way back, and `whoami` and `display_data` take `@auth.api`, so without one they
  answer `401` with an empty body.
- `create_app` wraps the whole app, routing included, in `OriginCheck` with
  `CANARY_PUBLIC_ORIGIN`. Canary has no route that changes state yet.
- `create_app(settings, keys=None)` in `src/canary/app.py` builds the app. Without `keys` it
  creates one HTTP client and one `KeyCache` over it when the app starts, sets it as
  `app.state.keys`, where the wrappers find it, and closes the client when it stops; tests pass a
  cache of their own, over a key set answered from memory.

`tests/test_app.py` checks that canary gets these through its own routes: each way of having no
valid token still gets `303` on the page and `401` on the API, the `Origin` check sits in front of
paths canary does not serve, and the app owns its key cache.

## Libraries canary is built on

**uv for the toolchain.** Chosen because it keeps a real lockfile, `uv.lock`, with hashes for every
platform, which "One repository" in `High-level/docs/platform-architecture.md` asks of every
service. It also installs the Python version pinned in `.python-version`. pip with a venv was the
alternative, more universal, but a pinned `requirements.txt` is a weaker lock and closing the gap
means adding pip-tools.

**A packaged `src/` layout.** The code lives in `src/canary/` and is installed into `.venv` in
editable mode, so the tests and uvicorn both import `canary` from any directory. A flat layout with
a `main.py` would work only from this directory, which is the kind of assumption that holds locally
and fails in CI.

**Starlette for HTTP.** Chosen for the same reason as axum in the core: routing, requests and
middleware with little machinery hiding the request pipeline, and middleware as plain ASGI apps
wrapping each other, the way tower layers do. FastAPI was the close alternative. It is Starlette
with pydantic validation, dependency injection and OpenAPI added, none of which canary needs, since
it validates no request bodies. A feature that does can choose FastAPI without changing any
middleware written against Starlette.

**uvicorn as the server.** Python separates the server from the application through ASGI, so the
app needs one. Plain `uvicorn` rather than `uvicorn[standard]`, whose faster event loop and file
watching do not matter here.

**platform-auth for who is asking**, a path dependency on `../libs/python/platform-auth`. It
brings PyJWT, and why PyJWT was chosen is in its README.

**PyJWT in the tests**, a dev dependency, since canary's own code no longer imports it but its
tests mint tokens with it.

**httpx2 for HTTP calls**, a runtime dependency, since canary creates the client the key cache
fetches the core's key set with. Its `MockTransport` lets tests answer the core's requests from
memory. Starlette's `TestClient` uses it too, to call the app in process, so it is the one HTTP
client canary has. Plain `httpx` still works but is deprecated for the test client since Starlette
1.7.

**pytest for tests.** An async test runs with `asyncio.run` inside a plain test, rather than
through a pytest plugin, since a handful of tests did not earn a dependency.

**ruff for linting and formatting**, a dev dependency, with its default rules and no configuration.
Chosen because one tool does both, the job flake8 and black would do as two, and it is fast enough
to run on every push. The defaults stay until a rule earns its place by catching something.
