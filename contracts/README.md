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
`exp + 6`. A feature's tests verify `token.jwt` against `jwks.json` with its own verifier.

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

The file is written by hand and changed together with the feature that serves it.

`scripts/ci/changed-areas.sh` maps a change here to every area that tests against it.
