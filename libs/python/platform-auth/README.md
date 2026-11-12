# platform-auth

Checking the core's access token and the `Origin` header, for every Python service on the platform.
Canary and the brain install it; a Python feature after them installs it too, rather than copying
the code. Why this is a shared library at all, an exception to the rule that no service imports
from another's directory, is in "One repository" in `High-level/docs/platform-architecture.md`:
it is the platform's security code, and a flaw fixed in one copy and missed in another is a hole
nobody sees. The rules it implements are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`.

## What it holds

Four modules in `src/platform_auth/`, and nothing else:

- `auth.py` reads the token from `Authorization: Bearer <token>` when that header is present,
  otherwise from the `__Host-access` cookie. A header that is present and invalid is a refusal,
  even beside a valid cookie, and so are two of them. `authenticate(request, keys, now)` resolves
  the key through the key cache and verifies the token at the time it is given. A route takes one
  of two requirements: `@auth.page` redirects to the core's refresh endpoint with the path and
  query exactly as received, relative and never judged by the service, and `@auth.api` answers
  `401` with an empty body. A missing token, an expired one, one that fails verification, and a
  core that cannot be reached with no key cached are all the same to both. The service never
  decides between refreshing and logging in; the core does, see "The refresh endpoint is the one
  place that decides between refreshing and logging in" in `Core/docs/core-architecture.md`. Both
  read the clock once per request, `int(time.time())`, and pass it down, and both find the key
  cache at `request.app.state.keys`, which the service sets when it starts.
- `verify.py` holds `verify(token, keys, now)`, a pure function of the token, the public keys by
  key id and the time, which returns the `Claims` or raises `Refused`. It accepts only `EdDSA`,
  chooses the key by the `kid` in the header, and requires every claim the core writes, ids as
  UUIDs. PyJWT's own expiry check is off, along with its other checks that read the system clock,
  and `verify` compares `exp + 5 < now` itself, since PyJWT would refuse at `exp + 5`, where the
  contract accepts. Why five seconds is in "The clock leeway on verification is chosen, not
  inherited" in `Core/docs/core-architecture.md`. Every refusal looks the same to a caller; its
  reason, invalid or expired, is for logs and tests. The user id is always `Claims.account_id`.
- `keys.py` holds `KeyCache`, the key set fetched from the core's `/.well-known/jwks.json` and kept
  in memory. A key id it does not know refetches the set once, at most once every thirty seconds,
  and a failed fetch keeps the keys it had, so a service keeps verifying while the core is down.
  `parse_key_set` reads the Ed25519 keys out of a JWK set and ignores every other entry.
- `origin.py` holds `OriginCheck`, ASGI middleware in front of the whole app, routing included:
  any method other than GET, HEAD and OPTIONS needs exactly one `Origin` header equal, byte for
  byte, to the platform's origin, or it gets `403`. It mirrors `Core/src/origin.rs`.

A service using it creates one HTTP client and one `KeyCache` over it when it starts, sets the
cache as `app.state.keys`, and wraps the app in `OriginCheck` with its public origin setting.
`canary/src/canary/app.py` is the smallest example, `Brain/src/brain/app.py` the same over FastAPI.

## What it must never grow into

A place for shared code. It holds token verification and the origin check, the two things a
service must get exactly right and identically, and nothing else: no settings parsing, no
HTTP helpers, no models, no logging setup, however many services would like them. Each of those
is fine written again in each service, which is what keeps a service movable out of this
repository; a library that grows is how the drift "One repository" warns about comes back. A
change here changes every Python service at once, so it changes only when the contract does.

Issuing, refreshing and rotating tokens, the cookies and the signing key are the core's, described
in `Core/docs/core-architecture.md`, and never come here. A C# version arrives with the first C#
feature, as a library of its own beside this one.

## Running its tests

From this directory, with uv installed:

```zsh
uv run pytest
uv run ruff check
uv run ruff format --check
uv run mypy
```

These are the four steps `.github/workflows/libs.yml` runs for every library under `libs/python/`.
The tests verify `contracts/token/token.jwt`, the token the core actually produces, against
`contracts/token/jwks.json`, read from the repository root, see `contracts/README.md`. They cover
the ways verification is known to break, listed in "A feature's authentication tests are part of
the feature template" in `High-level/docs/how-we-build.md`, and run the same `Origin` table as
`Core/tests/origin.rs` over a stub route. The key cache's tests answer the core's requests from
memory with httpx2's `MockTransport`, and the edge's run through a stub service with one page and
one API route.

## How a service installs it

As a path dependency, editable, so a change here reaches every service on its next `uv run`:

```toml
[project]
dependencies = ["platform-auth"]

[tool.uv.sources]
platform-auth = { path = "../libs/python/platform-auth", editable = true }
```

## Libraries it is built on

**PyJWT for verifying tokens**, with its `crypto` extra, which brings `cryptography` for the
Ed25519 signature and the key itself. Chosen because it is the most used JWT library in Python,
supports EdDSA, and lets every check that reads the clock be turned off, which the contract needs:
expiry is compared against a time passed in. It is also what the core's `jsonwebtoken` is to Rust,
a library that checks the algorithm and the signature and leaves policy to the caller. joserfc was
the alternative, newer and covering all of JOSE, encryption included, which nothing here needs.

**httpx2 for fetching the key set.** It has an async client, which the services' async handlers
need, and its `MockTransport` lets tests answer the core's requests from memory and count them.
Starlette's `TestClient` uses it too.

**Starlette for the request types and the middleware interface**, which FastAPI shares, so the
same wrappers and middleware serve canary on Starlette and the brain on FastAPI.

**uv, a packaged `src/` layout, pytest, ruff, and mypy in strict mode**, as in `Brain/`, for the
reasons its README gives. The key cache's tests are async and run with `asyncio.run` inside a plain
test, rather than through a pytest plugin.
