# canary

The platform's first feature, written in Python on purpose. Why it exists, and why not in Rust, is
in "The first milestone" in `High-level/docs/platform-architecture.md`.

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

Canary reads two settings from the process environment, once, in `src/canary/settings.py`. It
loads no `.env` file; `.env.example` lists both for whoever exports them. Unset means the default,
and a value that is set must parse, or canary refuses to start rather than quietly using the
default.

- `CANARY_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `CANARY_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.

The defaults are right on the Mac mini and in CI, so neither sets them.

## How canary verifies a token

The rules every feature follows are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`; canary is the first to carry them, and the one later
features copy.

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
