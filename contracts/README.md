# contracts

Contract fixtures: bytes that two services written in different languages are both tested
against, so drift between them fails a test instead of a request. They belong to neither service,
which is why they live here rather than in either one's directory. This is the one shared place
"One repository" in `High-level/docs/platform-architecture.md` names, and the rule that no service
imports from another service's directory still holds: a fixture is data that tests read, never
code.

**Everything here is public material only.** A fixture holds what any verifier may see, tokens and
public keys, never a private key. The token fixture is signed with the RFC 8037 Appendix A test
seed, a published test vector that signs nothing real, and that seed is a constant in the core's
test, never a file, see "The signing key" in `Core/docs/core-architecture.md`.

## token/

The access token as the core produces it for fixed inputs:

- `token.jwt`: the token, with no trailing newline.
- `jwks.json`: the key set exactly as `GET /.well-known/jwks.json` serves it for the fixture's
  key, pretty printed, with a trailing newline.
- `claims.json`: the token's `sub`, `sid`, `jti`, `iat` and `exp`, and the `kid` in its header,
  so a verifier's test knows what to expect and which time to verify at.

The core's test, `Core/tests/token_contract.rs`, checks that the core still produces these bytes
exactly, and that the token verifies at its `iat` and at `exp + 5` and is refused as expired at
`exp + 6`. On the other side, `platform-auth`, the one verifier every Python service installs,
verifies `token.jwt` against `jwks.json` at the same times in its `tests/test_verify.py`, and its
key cache reads `jwks.json` as the core serves it in `tests/test_keys.py`, see
`libs/python/platform-auth/README.md`. The brain's `tests/test_whoami.py` sends the token, and
tokens made from it, through its own route. `PlatformAuth`, the C# verifier, does the same in its
`VerifyTests` and `KeyCacheTests`, see `libs/dotnet/PlatformAuth/README.md`. A feature in another
language verifies them with that language's library.

Ed25519 signatures are deterministic, so the same inputs give the same bytes every time. A change
that alters them, a dependency reordering the header included, fails the core's test. That is the
point: the fixture is regenerated on purpose and its diff reviewed. From `Core/`:

```zsh
UPDATE_CONTRACTS=1 cargo test --test token_contract
```

Without the variable the test only compares, and a missing or different file fails it.

## display/

A feature's display data for the front page, one file per feature that has any, named after it.
Why each feature's shape is its own, and why it is still a contract, is in "What a feature
exposes for the front page" in `Core/docs/core-architecture.md`.

- `canary.json`: canary's data, an example response, pretty printed, with a trailing newline.
  Canary's `tests/test_display.py` checks that its real response has the same keys, each with the
  same JSON type; the values differ per request, so they are not compared. The core reads it into
  the type canary's card renders, `CanaryCard`, in `the_committed_canary_fixture_reads_into_the_card`
  in `Core/src/cards.rs`.
- `review.json`: review's data, an example response, pretty printed, with a trailing newline.
  Review's `DisplayTests.TheResponseMatchesTheCommittedFixture` checks that its real response has
  the same keys, each with the same JSON type, those of a deck in `decks` included. The core reads
  it into `ReviewCard` in `the_committed_review_fixture_reads_into_the_card` in
  `Core/src/cards.rs`.

Each file is written by hand and changed together with the feature that serves it.

## brain/

The brain's HTTP schema, what every caller of the brain is written against.

- `openapi.json`: the OpenAPI document the brain's app generates, `app.openapi()`, with keys
  sorted, a two space indent and a trailing newline.

The brain's `tests/test_contract.py` checks that the app still generates exactly these bytes, so a
change to a route, a model or a route's docstring, which becomes its `description`, fails the test
until the file is regenerated on purpose and its diff reviewed. From `Brain/`:

```zsh
UPDATE_CONTRACTS=1 uv run pytest tests/test_contract.py
```

Without the variable the test only compares, and a missing or different file fails it. The pages
that call the brain generate their types from this file: the memory centre's, and review's for the
brain's topics, `review/web/src/api/brain-schema.d.ts`, whose `schema.test.ts` fails when it is
stale. After regenerating this file, run `npm run generate:api` in `review/web/` as well, see "The
data, and the brain's topics" in `review/README.md`.

Review's backend calls the brain too, through a client Kiota generates from this file into
`review/src/Review/Brain/Generated/`. `BrainClientTests.TheGeneratedClientIsCurrent` in review's
tests regenerates it and fails when the committed client is stale, so after regenerating this file,
regenerate that client as well, with the command in "What Review tells the brain" in
`review/README.md`.

## review/

Review's HTTP schema, what its page, and any later caller of review, is written against. Review is
C#, so this is the same pin as `brain/` in another language.

- `openapi.json`: the OpenAPI document review generates with `Microsoft.AspNetCore.OpenApi`, at
  `GET /review/api/openapi.json`, with keys sorted, a two space indent and a trailing newline. The
  route is mapped only when `REVIEW_EXPOSE_OPENAPI=1`, for the test and for regenerating this
  file, so the running service serves it nowhere.

Review's `ContractTests` check that the app still generates exactly these bytes, so a change to a
route, its summary, its description or a response type fails the test until the file is
regenerated on purpose and its diff reviewed. From `review/`:

```zsh
UPDATE_CONTRACTS=1 dotnet test --filter "FullyQualifiedName~ContractTests"
```

Without the variable the test only compares. The page's types in `review/web/src/api/schema.d.ts`
are generated from this file, and its `schema.test.ts` fails when they are stale, see
`review/README.md`.

`scripts/ci/changed-areas.sh` maps a change here to every area that tests against it.
