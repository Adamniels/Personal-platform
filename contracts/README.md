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
- `devflow.json`: Dev flow's data, an example response, pretty printed, with a trailing newline.
  Dev flow's `DisplayTests.TheResponseMatchesTheCommittedFixture` checks that its real response
  has the same keys, each with the same JSON type. The core reads it into `DevFlowCard` in
  `the_committed_devflow_fixture_reads_into_the_card` in `Core/src/cards.rs`.

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

## devflow/

Dev flow's HTTP schema, what its page, and any later caller of Dev flow, is written against. Dev
flow is C#, so this is the same pin as `review/`.

- `openapi.json`: the OpenAPI document Dev flow generates with `Microsoft.AspNetCore.OpenApi`, at
  `GET /devflow/api/openapi.json`, with keys sorted, a two space indent and a trailing newline. The
  route is mapped only when `DEVFLOW_EXPOSE_OPENAPI=1`, for the test and for regenerating this
  file, so the running service serves it nowhere.
- `project.schema.json`: the JSON Schema of a project's `.devflow/project.json`, written by hand,
  for people and editors. .NET does not validate against a schema, so `DevFlow.Protocol`'s
  `ProjectFile` reads the file by the same rules, and `ProjectFileTests` checks the two hold the
  same fields, the same required ones, and the same pattern for `url`. See "The format of
  .devflow/" in `devflow/README.md`.
- `report.schema.json`: the JSON Schema of a developer agent's report, which every job hands the
  agent, written by `DevFlow.Protocol`'s `Report.Schema()` from the type that reads a report, with a
  two space indent and a trailing newline, its fields in the type's order. Since the type writes it,
  the two hold the same fields. See "Jobs" in `devflow/README.md`.

Dev flow's `ContractTests` check that the app still generates exactly these bytes, as review's do,
and that `Report.Schema()` still writes exactly `report.schema.json`.
From `devflow/`:

```zsh
UPDATE_CONTRACTS=1 dotnet test --filter "FullyQualifiedName~ContractTests"
```

Without the variable the test only compares. The page's types in
`devflow/web/src/api/schema.d.ts` are generated from this file, and its `schema.test.ts` fails when
they are stale, see `devflow/README.md`.

## github/

GitHub's own answers, which Dev flow's GitHub adapter is tested against, since no automated test
may reach GitHub. Each is read by `devflow/tests/DevFlow.Tests/GitHub/ReadTests.cs`, which checks
every field of the code host's records equals what the file holds, see "The GitHub adapter" in
`devflow/README.md`. They hold what is already in this repository's history on GitHub, and no
token.

Recorded once, on 2026 11 15, from this repository with `gh api` as the bot, from pull request 305,
"The code host seam and its fake", merged, by the bot, with a review verdict, and its merge commit
`1154137dceb065f226823ec720f1f5e55d7c205d`; each pretty printed with `jq .`:

- `pull.json`: `gh api repos/Adamniels/Personal-platform/pulls/305`.
- `pulls-open.json`: `gh api 'repos/Adamniels/Personal-platform/pulls?state=closed&per_page=2'`,
  the list the adapter reads with `state=open`, in the same shape. No pull request was open when it
  was recorded, so it holds 305 and 304, both merged.
- `issue-comments.json`: `gh api repos/Adamniels/Personal-platform/issues/305/comments`, its one
  comment, the reviewer's, with the body's first line dropped, piped through
  `jq '[.[0] | .body |= (split("\n")[1:] | join("\n"))]'`. That line is the marker the merge gate
  reads, which the agents' tooling refuses to write anywhere.
- `statuses.json`: the `statuses` array of
  `gh api repos/Adamniels/Personal-platform/commits/<sha>/status`, the combined status, whose
  entries have the shape `GET .../commits/<sha>/statuses` lists, since the runner's gh guard
  refuses any address with `statuses` in it.
- `check-runs.json`: `gh api repos/Adamniels/Personal-platform/commits/<sha>/check-runs`, trimmed
  with `jq '.check_runs |= .[0:2] | .total_count = 2'` to two runs, a success and a skipped one.

`commits-path.json` was recorded on 2026 11 17, the same way: `gh api
'repos/Adamniels/Personal-platform/commits?path=devflow/Dockerfile&per_page=1'`, the newest commit of
the default branch that changed `devflow/Dockerfile`, a commit by the bot, of which the adapter reads
`sha` to read a project's recipe at, see "Starting a worker" in `devflow/README.md`.

What handing out a job reads, read by `devflow/tests/DevFlow.Tests/GitHub/GitHubJobReadTests.cs`;
see "Jobs" in `devflow/README.md`:

- `repository.json`: recorded on 2026 11 17 for the slice "Watching main", and taken byte for byte
  from its branch, so the two slices add the same file: `gh api repos/Adamniels/Personal-platform`,
  piped through `jq 'del(.temp_clone_token)'`, since GitHub documents that field as a short lived
  credential; the repository is public and the answer held none. The adapter reads
  `default_branch`.
- `bot-user.json`, recorded on 2026 11 18 as the bot: `gh api 'users/dependabot[bot]'`, an app's bot as GitHub answers
  `GET /users/<app-slug>[bot]`, since Dev flow's own app's slug is a setting and not in this
  repository; the test renames its `login` to the test app's. The adapter reads `login` and `id`.

The app's own writes are answered with the same shapes as these reads, so four more are recorded
for them, read by `devflow/tests/DevFlow.Tests/GitHub/GitHubAppWriteTests.cs`, and checked by
`ContractTests.TheRecordedWriteAnswersHoldWhatTheAdapterReads` for every field the adapter reads.
Recorded on 2026 11 17, as the bot, with the reads below, since recording the writes themselves
would write to this repository:

- `pull-opened.json`: `gh api repos/Adamniels/Personal-platform/pulls/352`, cut with
  `jq 'del(.head.repo, .base.repo, ._links)'` as the runner's are, a pull request open, with two
  labels, when it was recorded, the shape `POST /repos/{owner}/{repo}/pulls` answers with `201`.
- `labels.json`: `gh api repos/Adamniels/Personal-platform/issues/267/labels`, the list of labels
  adding one or removing one answers with `200`.
- `issue.json`: `gh api repos/Adamniels/Personal-platform/issues/336`, an open issue, the shape
  `POST /repos/{owner}/{repo}/issues` answers with `201`. The adapter reads it as it reads an entry
  of `issues-labelled.json` below, the same fields.
- `merge.json`, written by hand from GitHub's documented `200` answer to merging a pull request,
  `sha`, `merged` and `message`.

The answer to commenting as the app is the first entry of `issue-comments.json`, the same shape.

What picking cards reads was recorded on 2026 11 17 the same way, read by
`devflow/tests/DevFlow.Tests/GitHub/GitHubIssueTests.cs`; see "Cards" in `devflow/README.md`. The
two REST answers have each `body` cut to its first line with
`jq 'map(.body |= (if . == null then . else (split("\n")[0]) end))'`, since the adapter keeps a
body as it is and the full ones are long:

- `issues-labelled.json`: `gh api 'repos/Adamniels/Personal-platform/issues?state=open&per_page=2'`,
  the list the adapter reads with `labels=<the pick-up label>`, in the same shape. No issue carried
  a label when it was recorded, so it holds the two newest open ones: 353, a pull request, which
  GitHub marks with a `pull_request` field, and 347, an issue. The adapter reads `number`, `title`,
  `user.login`, `labels[].name`, `body`, `html_url`, `created_at` and whether `pull_request` is
  there.
- `issue-blocked-by.json`: `gh api repos/Adamniels/Personal-platform/issues/339/dependencies/blocked_by`,
  the four issues 339 is blocked by, two open and two closed, of which the adapter reads `number`,
  `state`, `html_url`, `repository.name` and `repository.owner.login`.
- `issue-edits.json`: `gh api graphql` with the query `GitHubCodeHost.EditsQuery` in
  `devflow/src/DevFlow/CodeHost/GitHub/GitHubCodeHost.Issues.cs`, exactly, and `owner`
  `Adamniels`, `name` `Personal-platform`, `number` 4 and both `with` variables true: issue 4's two
  body edits, the first version among them, and its one change of title.
- `issue-edits-not-found.json`: the same query for issue 99999, which does not exist, GraphQL's
  `200` with `issue` null and an error of type `NOT_FOUND`.

`installation-token.json` is written by hand from GitHub's documented answer to
`POST /app/installations/{installation_id}/access_tokens`, with a made up token. The tests set its
`expires_at` from their clock.

Signing a person in cannot be recorded without a real authorization, so its three are written by
hand from GitHub's documented shapes, with made up values, and read by
`devflow/tests/DevFlow.Tests/GitHub/GitHubSignInTests.cs`:

- `oauth-token.json`: the answer to `POST https://github.com/login/oauth/access_token` exchanging a
  code, with expiring tokens: `access_token`, `expires_in` of eight hours, `refresh_token`,
  `refresh_token_expires_in` of six months, `scope` and `token_type`.
- `oauth-refresh.json`: the answer to the same address renewing with a refresh token, the same
  shape with new tokens.
- `user.json`: the answer to `GET /user`, a made up account, `octo-owner`, of which the adapter
  reads `login` and `id`.

Listing what a person can connect is read with their token for the app, which the bot's `gh` does
not hold, so these two are written by hand the same way, from the shapes GitHub documents, and read
by `devflow/tests/DevFlow.Tests/GitHub/GitHubConnectTests.cs`:

- `user-installations.json`: the answer to `GET /user/installations`, `total_count` and
  `installations`, two installations of a made up app, 41 on the person `octo-owner` and 52 on the
  organisation `octo-lab`, of which the adapter reads `id` and `account.login`.
- `user-installation-repositories.json`: the answer to
  `GET /user/installations/41/repositories`, `total_count`, `repository_selection` and
  `repositories`, two repositories of `octo-owner`, of which the adapter reads `id`, `name` and
  `owner.login`.

To record one again, run its command from the repository root into the file, with the same `jq`,
and run Dev flow's tests: a field GitHub renamed or dropped fails `ReadTests`.

### runner/

Four real pull requests by the bot, each in a folder named by its number, which
`devflow/tests/DevFlow.Tests/Runner/RecordedTests.cs` reads through the GitHub adapter and gives to
the reader of the runner's conventions, see "Reading this repository's runner" in
`devflow/docs/devflow-architecture.md`. Each shows one case: 267 `owner-decision` with
`has-decision`, a decision marked for the owner and a verdict with "For the owner"; 266
`owner-merge`; 210 `owner-blocked`; and 265 no owner label. They are merged, so their state is
closed and their labels are those they carried when they merged; the tests read them as open.

Recorded once, on 2026 11 15, from this repository with `gh api` as the bot, `<n>` the pull
request, `<sha>` its head commit and `<slice>` the issue its body closes:

- `pull.json`: `gh api repos/Adamniels/Personal-platform/pulls/<n>`, cut with
  `jq 'del(.head.repo, .base.repo, ._links)'`, the long parts the adapter does not read.
- `comments.json`: `gh api repos/Adamniels/Personal-platform/issues/<n>/comments --paginate`,
  joined with `jq -s add`, with each verdict's first line dropped from its body, as in
  `issue-comments.json` above.
- `markers.json`: what each dropped line said, `[{"comment": <id>, "kind": "approve" or "changes",
  "sha": <40 hex>}]`, read from it before it was dropped. The tests put the line back in memory,
  built from these three, so the reader sees the comment as GitHub has it.
- `statuses.json`: the `statuses` array of
  `gh api repos/Adamniels/Personal-platform/commits/<sha>/status`, as `statuses.json` above.
- `check-runs.json`: `gh api repos/Adamniels/Personal-platform/commits/<sha>/check-runs`, cut with
  `jq '.check_runs |= map(select(.name == "ci-ok")) | .total_count = (.check_runs | length)'` to
  the runs the reader reads.
- `slice-comments.json`: `gh api repos/Adamniels/Personal-platform/issues/<slice>/comments
  --paginate`, joined with `jq -s add`.

Recording one again can change what its test sees, a label or a comment added since; if one no
longer shows its case, record another pull request by the bot that does, and name it here.

`scripts/ci/changed-areas.sh` maps a change here to every area that tests against it.
