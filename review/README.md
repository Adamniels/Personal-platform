# review

Review is a queue of cards that comes back to the owner, so what he marks as worth remembering
sticks. Cards are added by hand first, and later sent by other features. It is its own feature
rather than part of desk: three features will send it cards, it has its own logic for when a card
returns, and a card needs only a link back to where it came from, see "Being allowed to call is
not a reason to split" in `High-level/docs/feature-contract.md`. Until it was planned, on
2026-11-13, it was described as a part of desk, in `desk/docs/desk-architecture.md`, whose Review
section now points here. Its scope and slices are in its feature issue, #229.

It is the platform's first C# feature, on .NET 10, an ASP.NET Core minimal API app.

It has nothing to do with the brain's review queue, which is where the brain's consolidation
proposals wait for the owner, see "The review queue" in `Brain/docs/trust-model.md`.

## What it serves

- `GET /review/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what review depends on.
- `GET /review/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.
- `GET /review`, `/review/cards`, `/review/decks` and `/review/study`, with any query, the page:
  `web/dist/index.html`, revalidated on every load. Without a valid token each answers `303` to
  `/auth/refresh?return_to=<the path and query as received, URL encoded>`.
- `GET /review/assets/*`, the page's built files from `web/dist/assets/`, without a token, as the
  brain serves its own: they are the same for everyone and hold no data.

Everything else is `404`, and any request that changes state from another origin is `403`, before
routing, see "How review authenticates a request" below. An error the service did not plan for is
an RFC 9457 problem document, `application/problem+json` with `status`, `title` and `detail`,
through ASP.NET Core's `AddProblemDetails`, which the page's client reads as the memory centre's
reads the brain's.

The routes under `/review/api/` are pinned by `contracts/review/openapi.json`, see "The schema"
below.

## The page

`web/`, a frontend like any other, a `web` folder under its service, so CI and the agents gate and
build it with the rest, see `design/ui/README.md`. It is the memory centre's stack at its versions,
`Brain/web/package.json`, and depends on the components as
`"@platform/ui": "file:../../design/ui"`. Its `index.html` includes the nav's lines from
"Navigation" in `High-level/docs/platform-architecture.md`, with `<platform-nav current="review">`,
and its content sits in `<main class="page">`.

The page is an `h1`, "Review", a line under it, then the package's `Tabs`, one per section, each
with its own address:

| Section | Address         |
| ------- | --------------- |
| Study   | `/review`       |
| Cards   | `/review/cards` |
| Decks   | `/review/decks` |

`/review/study` is the address of a study session, which a later slice builds, and shows the Study
tab until then. Each section shows the package's `EmptyState` until its slice lands. The address is
the state, through the History API and no router library (`src/address.ts`): a tab pushes its
section's address, Back and Forward come back as `popstate`, and an address that names no section
shows a not found state under the tabs. The list of sections lives in `src/sections.ts`, and the
addresses the service answers the page for in `src/Review/Pages/PageEndpoints.cs`; the two change
together.

`src/api/client.ts` is the page's one way to call review: every call goes to `/review/api/` with the
session cookie, a `401` is refreshed once, shared by every call that got one, by posting to
`/auth/refresh` and repeating the call, and a second `401` sends the browser to
`/auth/refresh?return_to=<path>`, as the memory centre's client does. Its GETs are typed from
`src/api/schema.d.ts`.

## The schema

`Microsoft.AspNetCore.OpenApi` generates the OpenAPI document of the API at
`GET /review/api/openapi.json`, needing no token, only when `REVIEW_EXPOSE_OPENAPI=1`, so the
running service serves it nowhere. `ContractTests` writes it with keys sorted, a two space indent
and a trailing newline and checks that it equals `contracts/review/openapi.json` byte for byte,
see `contracts/README.md`. Every route has a summary and named response types, so a change to
either is a diff to review. After changing a route, from this directory:

```zsh
UPDATE_CONTRACTS=1 dotnet test --filter "FullyQualifiedName~ContractTests"
cd web && npm run generate:api
```

The second line writes `web/src/api/schema.d.ts` from the file with `tools/generate-schema.mjs`,
the memory centre's tool, and `src/api/schema.test.ts` fails when it is stale. The app is named
for its own assembly, so the document is the same under the tests' host as in the service.

## Running it

From this directory, with the .NET SDK `global.json` pins installed, and the page built first,
since review refuses to start without `web/dist/index.html`, naming it:

```zsh
(cd web && npm ci && npm run build)
dotnet run --project src/Review -c Release --no-launch-profile
```

`dotnet run` runs it from this directory, where `web/dist` is, which `RunWorkingDirectory` in
`src/Review/Review.csproj` sets; the Web SDK would otherwise run it from the project's folder. Its
entry in `infra/services.json` starts it the same way, with `REVIEW_LISTEN_ADDR` set to the host
and port the manifest gives. Port 8083 sits beside the brain's 8082, and the Caddyfile's upstream
for `/review` points at it.

Its tests, the steps `.github/workflows/dotnet.yml` runs:

```zsh
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore -c Release
dotnet test --no-build --no-restore -c Release
```

and the page's, from `web/`, the steps `.github/workflows/web.yml` runs for every frontend:

```zsh
npm ci
npx biome ci .
npx tsc --noEmit
npx vitest run
npm run build
```

`packages.lock.json` is committed beside each project; after changing a package, run
`dotnet restore` without `--locked-mode` once to update it, and commit the change. The default
`.editorconfig` wants no final newline in a C# file, and `dotnet format --no-restore` fixes one.

## Settings

Review reads its settings from the process environment, once, at startup, into one immutable
`ReviewSettings`, in `src/Review/Settings/ReviewSettings.cs`. It loads no `.env` file. Unset means
the default, and a value that is set must parse, or review refuses to start, naming the setting,
rather than quietly using the default. The rules are canary's, written again rather than shared,
since no service imports from another's directory.

- `REVIEW_LISTEN_ADDR`, default `127.0.0.1:8083`: where Kestrel listens, a host and a port, an
  IPv6 host in brackets.
- `REVIEW_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `REVIEW_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.
- `REVIEW_EXPOSE_OPENAPI`, default `0`: `1` maps the schema's route, for the contract test and
  regenerating the file, see "The schema". Nothing else is accepted.

The defaults are right on the Mac mini and in CI, so neither sets them, except the listen address,
which the manifest's `start` sets. Later slices add `REVIEW_APP_DATABASE_URL`,
`REVIEW_OWNER_DATABASE_URL`, `REVIEW_DAY_ZONE` and `REVIEW_BRAIN_URL`.

## How review authenticates a request

The rules every feature follows are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`. Review holds none of the code for them: `PlatformAuth`, in
`libs/dotnet/PlatformAuth/`, does every check of the token and the origin, and its README says what
it holds and how its tests cover the ways verification breaks. Review references it as a project
and does three things with it, in `src/Review/ReviewApp.cs`:

- `AddPlatformAuth` with `REVIEW_CORE_URL`, which registers the key cache, the handler and the two
  policies. The pages take `PlatformAuthPolicies.Page` and `whoami` takes
  `PlatformAuthPolicies.Api`; health and the built files take neither.
- `UsePlatformOriginCheck` with `REVIEW_PUBLIC_ORIGIN`, before routing, so every route, one added
  later included, is covered. Review has no route that changes state yet.
- `ReviewApp.Build(settings, webRoot, configure)` builds the app; `configure` runs before it is
  built, which is where `Program` sets the listen address and where the tests put the app on a
  test server, with a key cache over the committed key set answered from memory and a clock fixed
  at the committed token's `iat`, both of which `AddPlatformAuth` keeps.

The tests send the ways verification is known to break through review's own routes, as "A
feature's authentication tests are part of the feature template" in
`High-level/docs/how-we-build.md` asks: the committed token is accepted from the cookie and the
header, a missing, expired, wrongly signed or unknown key id token gets `401` from the API, and a
page without a token answers `303` to the refresh endpoint with the way back. That every kind of
invalid token is the same to a page is `PlatformAuth`'s own test.

## Libraries review is built on

**The .NET 10 SDK, pinned in `global.json`**, with `Directory.Build.props`,
`Directory.Packages.props` and `.editorconfig` holding the same values as `PlatformAuth`'s folder,
copies rather than shared files, since each C# folder builds from its own directory alone. Why .NET
10, the analyzers as the linter and `dotnet format` as the formatter are in "Libraries it is built
on" in `libs/dotnet/PlatformAuth/README.md`.

**One web project, not layered projects.** `src/Review/` is the whole service, organised in
feature folders: `Settings`, `Health` (health and whoami) and `Pages` today, more with later
slices. The service is small, and folders inside one project keep it readable without Clean
Architecture's ceremony.

**ASP.NET Core minimal APIs**, from the shared framework, for routing, static files and problem
details, with no MVC. JSON is snake case on the wire, as the brain's and canary's is.

**Microsoft.AspNetCore.OpenApi 10.0.15 for the schema**, the generator that ships with ASP.NET
Core, so the document follows the routes and their types without annotations of a second library.
Swashbuckle was the alternative, which ASP.NET Core no longer includes by default.

**xUnit v3 for the tests**, with `Microsoft.AspNetCore.Mvc.Testing` for `TestServer` and
`Microsoft.Extensions.TimeProvider.Testing` for the clock, the five test packages `PlatformAuth`
uses, at its versions.

**The page** is the memory centre's stack, React, Vite, Vitest, Biome and TypeScript, at its
versions, without React Query, which nothing here needs until a section loads data; see
"Libraries the brain is built on" in `Brain/README.md` for why each was chosen.
