# devflow

Dev flow is the platform's feature for automating development on connected repositories: the owner
plans, agents build, and he follows and answers all of it from its pages. What it is and how it is
meant to grow is in `docs/devflow-architecture.md`, and its scope and slices are in its feature
issue, #268. "Dev flow" is its name in the nav and in headings, and `devflow` everywhere technical.

It is a C# feature on .NET 10, an ASP.NET Core minimal API app, built the way `review/` is. Today it
is the empty service and its page: the database, the code host and everything the tabs will show
arrive with later slices.

## What it serves

- `GET /devflow/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what Dev flow depends on.
- `GET /devflow/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.
- `GET /devflow` and `/devflow/projects`, with any query, the page: `web/dist/index.html`,
  revalidated on every load. Without a valid token each answers `303` to
  `/auth/refresh?return_to=<the path and query as received, URL encoded>`.
- `GET /devflow/assets/*`, the page's built files from `web/dist/assets/`, without a token, as
  review and the brain serve their own: they are the same for everyone and hold no data.

Everything else is `404`, and any request that changes state from another origin is `403`, before
routing, see "How Dev flow authenticates a request" below. An error the service did not plan for is
an RFC 9457 problem document, `application/problem+json` with `status`, `title` and `detail`,
through ASP.NET Core's `AddProblemDetails`, as review's are.

The routes under `/devflow/api/` are pinned by `contracts/devflow/openapi.json`, see "The schema"
below.

## The page

`web/`, a frontend like any other, a `web` folder under its service, so CI and the agents gate and
build it with the rest, see `design/ui/README.md`. It is review's stack at its versions,
`review/web/package.json`, without React Query, which nothing here needs until a section loads
data, and depends on the components as `"@platform/ui": "file:../../design/ui"`. Its `index.html`
includes the nav's lines from "Navigation" in `High-level/docs/platform-architecture.md`, with
`<platform-nav current="devflow">`, and its content sits in `<main class="page">`.

The page is an `h1`, "Dev flow", a line under it, "What your agents built, and what waits for
you.", then the package's `Tabs`, one per section, each with its own address:

| Section         | Address             |
| --------------- | ------------------- |
| Waiting for you | `/devflow`          |
| Projects        | `/devflow/projects` |

Each section shows the package's `EmptyState` until its slice lands. "Running now" and "Usage and
history" join with the parts that build them, never as empty tabs. The address is the state,
through the History API and no router library (`src/address.ts`): a tab pushes its section's
address, Back and Forward come back as `popstate`, and an address that names no section shows a
not found state under the tabs. The list of sections lives in `src/sections.ts`, and the addresses
the service answers the page for in `src/DevFlow/Pages/PageEndpoints.cs`; the two change together.

`src/api/client.ts` is the page's one way to call Dev flow: every call goes to `/devflow/api/` with
the session cookie, a `401` is refreshed once, shared by every call that got one, by posting to
`/auth/refresh` and repeating the call, and a second `401` sends the browser to
`/auth/refresh?return_to=<path>`, as review's client does. Its GETs are typed from
`src/api/schema.d.ts`. Nothing on the page calls it yet.

## The schema

`Microsoft.AspNetCore.OpenApi` generates the OpenAPI document of the API at
`GET /devflow/api/openapi.json`, needing no token, only when `DEVFLOW_EXPOSE_OPENAPI=1`, so the
running service serves it nowhere. `ContractTests` writes it with keys sorted, a two space indent
and a trailing newline and checks that it equals `contracts/devflow/openapi.json` byte for byte,
see `contracts/README.md`. Every route has a summary and named response types, so a change to
either is a diff to review. After changing a route, from this directory:

```zsh
UPDATE_CONTRACTS=1 dotnet test --filter "FullyQualifiedName~ContractTests"
cd web && npm run generate:api
```

The second line writes `web/src/api/schema.d.ts` from the file with `tools/generate-schema.mjs`,
review's tool, and `src/api/schema.test.ts` fails when it is stale. The app is named for its own
assembly, so the document is the same under the tests' host as in the service.

## Running it

From this directory, with the .NET SDK `global.json` pins installed, and the page built first,
since Dev flow refuses to start without `web/dist/index.html`, naming it:

```zsh
(cd web && npm ci && npm run build)
dotnet run --project src/DevFlow -c Release --no-launch-profile
```

`dotnet run` runs it from this directory, where `web/dist` is, which `RunWorkingDirectory` in
`src/DevFlow/DevFlow.csproj` sets; the Web SDK would otherwise run it from the project's folder. Its
entry in `infra/services.json` starts it the same way, with `DEVFLOW_LISTEN_ADDR` set to the host
and port the manifest gives. Port 8084 sits beside review's 8083, and the Caddyfile's upstream for
`/devflow` points at it. In the hosted copy it is the `devflow` container, built by `Dockerfile`
here, see `infra/hosted/README.md`.

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

Dev flow reads its settings from the process environment, once, at startup, into one immutable
`DevFlowSettings`, in `src/DevFlow/Settings/DevFlowSettings.cs`. It loads no `.env` file. Unset
means the default, and a value that is set must parse, or Dev flow refuses to start, naming the
setting, rather than quietly using the default. The rules are review's, written again rather than
shared, since no service imports from another's directory.

- `DEVFLOW_LISTEN_ADDR`, default `127.0.0.1:8084`: where Kestrel listens, a host and a port, an
  IPv6 host in brackets.
- `DEVFLOW_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `DEVFLOW_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.
- `DEVFLOW_EXPOSE_OPENAPI`, default `0`: `1` maps the schema's route, for the contract test and
  regenerating the file, see "The schema". Nothing else is accepted.

The defaults are right on the Mac mini and in CI, so neither sets them, except the listen address,
which the manifest's `start` sets. The hosted copy sets all three it needs in
`infra/hosted/compose.yaml`.

## How Dev flow authenticates a request

The rules every feature follows are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`. Dev flow holds none of the code for them: `PlatformAuth`, in
`libs/dotnet/PlatformAuth/`, does every check of the token and the origin, and its README says what
it holds and how its tests cover the ways verification breaks. Dev flow references it as a project
and does three things with it, in `src/DevFlow/DevFlowApp.cs`:

- `AddPlatformAuth` with `DEVFLOW_CORE_URL`, which registers the key cache, the handler and the two
  policies. The pages take `PlatformAuthPolicies.Page` and `whoami` takes
  `PlatformAuthPolicies.Api`; health and the built files take neither. A route reads its caller's
  user id through `Api/Caller.cs`, as review's do.
- `UsePlatformOriginCheck` with `DEVFLOW_PUBLIC_ORIGIN`, before routing, so every route, one added
  later included, is covered. Dev flow has no route that changes state yet.
- `DevFlowApp.Build(settings, webRoot, configure)` builds the app; `configure` runs before it is
  built, which is where `Program` sets the listen address and where the tests put the app on a
  test server, with a key cache over the committed key set answered from memory and a clock fixed
  at the committed token's `iat`, both of which `AddPlatformAuth` keeps.

The tests send the ways verification is known to break through Dev flow's own routes, as "A
feature's authentication tests are part of the feature template" in
`High-level/docs/how-we-build.md` asks: the committed token is accepted from the cookie and the
header, a missing, expired, wrongly signed or unknown key id token gets `401` from the API, and a
page without a token answers `303` to the refresh endpoint with the way back.

## Libraries Dev flow is built on

**The .NET 10 SDK, pinned in `global.json`**, with `Directory.Build.props`,
`Directory.Packages.props` and `.editorconfig` holding the same values as review's folder, copies
rather than shared files, since each C# folder builds from its own directory alone. Why .NET 10,
the analyzers as the linter and `dotnet format` as the formatter are in "Libraries it is built on"
in `libs/dotnet/PlatformAuth/README.md`.

**One web project, not layered projects.** `src/DevFlow/` is the whole service, organised in
feature folders: `Settings`, `Health` (health and whoami), `Pages` and `Api` today, more with later
slices, as `review/` is.

**ASP.NET Core minimal APIs**, from the shared framework, for routing, static files and problem
details, with no MVC. JSON is snake case on the wire, as review's, the brain's and canary's is.

**Microsoft.AspNetCore.OpenApi for the schema**, at review's version, for review's reasons, see
"Libraries review is built on" in `review/README.md`.

**xUnit v3 for the tests**, with `Microsoft.AspNetCore.Mvc.Testing` for `TestServer` and
`Microsoft.Extensions.TimeProvider.Testing` for the clock, review's test packages at its versions.
Review's database and brain client packages, Npgsql, Dapper and Kiota, are left out until Dev flow
needs them.

**The page** is review's stack, React, Vite, Vitest, Biome and TypeScript, at its versions; see
"Libraries the brain is built on" in `Brain/README.md` for why each was chosen.
