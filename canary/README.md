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
`High-level/docs/feature-contract.md`. Canary is the first to carry them, and is the reference
implementation a later feature starts from: these four files, and their tests.

- `src/canary/auth.py` reads the token from `Authorization: Bearer <token>` when that header is
  present, otherwise from the `__Host-access` cookie. A header that is present and invalid is a
  refusal, even beside a valid cookie, and so are two of them. It resolves the key through the key
  cache and verifies the token with the time read once, `int(time.time())`. A route takes one of
  two requirements: `@auth.page` redirects to the core's refresh endpoint with the path and query
  exactly as received, relative and never judged by canary, and `@auth.api` answers `401` with an
  empty body. A missing token, an expired one, one that fails verification, and a core that cannot
  be reached with no key cached are all the same to both. Canary never decides between refreshing
  and logging in; the core does, see "The refresh endpoint is the one place that decides between
  refreshing and logging in" in `Core/docs/core-architecture.md`.
- `src/canary/origin.py` holds `OriginCheck`, ASGI middleware in front of the whole app, routing
  included: any method other than GET, HEAD and OPTIONS needs exactly one `Origin` header equal,
  byte for byte, to `CANARY_PUBLIC_ORIGIN`, or it gets `403`. It mirrors `Core/src/origin.rs`, and
  `tests/test_origin.py` runs the same table as `Core/tests/origin.rs` over a stub route, since
  canary has no route that changes state yet.

- `src/canary/verify.py` holds `verify(token, keys, now)`, a pure function of the token, the
  public keys by key id and the time, which returns the claims or raises `Refused`. It accepts
  only `EdDSA`, chooses the key by the `kid` in the header, and requires every claim the core
  writes, ids as UUIDs. PyJWT's own expiry check is off, along with its other checks that read the
  system clock, and canary compares `exp + 5 < now` itself, since PyJWT would refuse at `exp + 5`,
  where the contract accepts. Why five seconds is in "The clock leeway on verification is chosen,
  not inherited" in `Core/docs/core-architecture.md`. Every refusal looks the same to a caller;
  its reason, invalid or expired, is for logs and tests.
- `src/canary/keys.py` holds `KeyCache`, the key set fetched from the core's
  `/.well-known/jwks.json` and kept in memory. A key id it does not know refetches the set once,
  at most once every thirty seconds, and a failed fetch keeps the keys it had, so canary keeps
  verifying while the core is down.

The tests verify `contracts/token/token.jwt`, the token the core actually produces, against
`contracts/token/jwks.json`, see `contracts/README.md`, and cover the ways verification is known to
break, listed in "A feature's authentication tests are part of the feature template" in
`High-level/docs/how-we-build.md`.

`create_app(settings, keys=None)` in `src/canary/app.py` builds the app. Without `keys` it creates
one HTTP client and one key cache over it when the app starts, and closes the client when it
stops; tests pass a cache of their own, over a key set answered from memory.

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

**PyJWT for verifying tokens**, with its `crypto` extra, which brings `cryptography` for the
Ed25519 signature and the key itself. Chosen because it is the most used JWT library in Python,
supports EdDSA, and lets every check that reads the clock be turned off, which the contract needs:
expiry is compared by canary against a time passed in. It is also what the core's `jsonwebtoken`
is to Rust, a library that checks the algorithm and the signature and leaves policy to the caller.
joserfc was the alternative, newer and covering all of JOSE, encryption included, which canary
does not need.

**httpx2 for HTTP calls**, a runtime dependency, used to fetch the core's key set. It has an async
client, which canary's async handlers need, and its `MockTransport` lets tests answer the core's
requests from memory and count them. Starlette's `TestClient` uses it too, to call the app in
process, so it is the one HTTP client canary has. Plain `httpx` still works but is deprecated for
the test client since Starlette 1.7.

**pytest for tests.** The key cache's tests are async and run with `asyncio.run` inside a plain
test, rather than through a pytest plugin, since a handful of tests did not earn a dependency.

**ruff for linting and formatting**, a dev dependency, with its default rules and no configuration.
Chosen because one tool does both, the job flake8 and black would do as two, and it is fast enough
to run on every push. The defaults stay until a rule earns its place by catching something.
