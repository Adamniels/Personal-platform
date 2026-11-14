# PlatformAuth

Checking the core's access token and the `Origin` header, for every C# service on the platform. A
C# feature references it rather than copying the code. It is the C# twin of
`libs/python/platform-auth/`, which every Python service installs, and holds the same rules
tested against the same fixtures, so the two agree with the core and with each other. Why this is
a shared library at all, an exception to the rule that no service imports from another's
directory, is in "One repository" in `High-level/docs/platform-architecture.md`: it is the
platform's security code, and a flaw fixed in one copy and missed in another is a hole nobody
sees. The rules it implements are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`.

## What it holds

One project, `src/PlatformAuth/`, and nothing else:

- `TokenVerifier.Verify(token, keys, now)`, a pure function of the token, the public keys by key
  id and the time, whole seconds since the epoch, which returns the `Claims` or throws `Refused`.
  It splits exactly three base64url segments without padding, requires `alg` to be exactly
  `EdDSA` and `kid` a string, looks the key up by `kid`, and verifies the Ed25519 signature over
  the first two segments before reading any claim. Then it requires `sub`, `sid` and `jti` as
  lowercase hyphenated UUIDs and `iat` and `exp` as integers, and refuses when `exp + 5 < now`.
  Nothing in it reads the clock. Why five seconds is in "The clock leeway on verification is
  chosen, not inherited" in `Core/docs/core-architecture.md`. Every refusal looks the same to a
  caller; its `Reason`, `Invalid` or `Expired`, is for logs and tests. The user id is always
  `Claims.AccountId`.
- `KeyCache`, the key set fetched from the core's `/.well-known/jwks.json` over an `HttpClient`
  and kept in memory, its time from a `TimeProvider`. A key id it does not know refetches the set
  once, at most once every thirty seconds, and a failed fetch keeps the keys it had, so a service
  keeps verifying while the core is down. `ParseKeySet` reads the Ed25519 `OKP` keys out of a JWK
  set and ignores every other entry.
- `services.AddPlatformAuth(options => options.CoreUrl = ...)`, which registers the key cache, an
  ASP.NET Core authentication handler, and two authorization policies a route takes with
  `RequireAuthorization`. The handler reads the token from `Authorization: Bearer <token>` when
  that header is present, otherwise from the `__Host-access` cookie. A header that is present and
  invalid is a refusal, even beside a valid cookie, and so are two of them. It reads the time once
  per request from the `TimeProvider`. `PlatformAuthPolicies.Page` answers a request without a
  valid token with a `303` to `/auth/refresh?return_to=<path and query as received, URL encoded>`,
  and `PlatformAuthPolicies.Api` with `401` and an empty body. A missing token, an expired one, one
  that fails verification, and a core that cannot be reached with no key cached are all the same
  to both. The service never decides between refreshing and logging in; the core does, see "The
  refresh endpoint is the one place that decides between refreshing and logging in" in
  `Core/docs/core-architecture.md`. On success `HttpContext.User` carries the claims, `sub` among
  them, and `IPlatformToken`, a request feature, holds the `Claims` and the verified token string,
  so a service can forward it to another service on the caller's behalf.
- `OriginCheckMiddleware`, added with `app.UsePlatformOriginCheck(publicOrigin)` before routing:
  any method other than GET, HEAD and OPTIONS needs exactly one `Origin` header equal, byte for
  byte, to the platform's origin, or it gets `403`. It mirrors `Core/src/origin.rs`.

A service using it calls, when it starts:

```csharp
builder.Services.AddPlatformAuth(options => options.CoreUrl = new Uri(coreUrl));

var app = builder.Build();
app.UsePlatformOriginCheck(publicOrigin);
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/feature", (HttpContext context) => ...).RequireAuthorization(PlatformAuthPolicies.Page);
app.MapGet("/feature/api/thing", (HttpContext context) => ...).RequireAuthorization(PlatformAuthPolicies.Api);
```

and reads who is asking with `context.Features.GetRequiredFeature<IPlatformToken>().Claims.AccountId`.
A `KeyCache` or a `TimeProvider` registered before `AddPlatformAuth` is kept, which is how its own
tests answer the core from memory and fix the clock.

## What it must never grow into

A place for shared code. It holds token verification and the origin check, the two things a
service must get exactly right and identically, and nothing else: no settings parsing, no HTTP
helpers, no models, no logging setup, however many services would like them. Each of those is fine
written again in each service, which is what keeps a service movable out of this repository; a
library that grows is how the drift "One repository" warns about comes back. A change here changes
every C# service at once, so it changes only when the contract does, and then in the Python
library too.

Issuing, refreshing and rotating tokens, the cookies and the signing key are the core's, described
in `Core/docs/core-architecture.md`, and never come here. A client for the brain is not this
library either.

## Running its tests

From this directory, with the .NET SDK `global.json` pins installed:

```zsh
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore -c Release
dotnet test --no-build --no-restore -c Release
```

These are the steps `.github/workflows/libs.yml` runs for every library under `libs/dotnet/`, and
`.github/workflows/dotnet.yml` for every C# service. The tests verify `contracts/token/token.jwt`,
the token the core actually produces, against `contracts/token/jwks.json`, found by walking up from
the test binary to the repository root, see `contracts/README.md`. They cover the ways
verification is known to break, listed in "A feature's authentication tests are part of the
feature template" in `High-level/docs/how-we-build.md`, and run the same `Origin` table as
`Core/tests/origin.rs` through the middleware in front of a stub route. The key cache's tests
answer the core's requests from memory with a stub `HttpMessageHandler` that counts them, on a
`FakeTimeProvider`, and the edge's run through `TestServer` with one stub page and one stub API
route. Each test class names its Python twin.

`packages.lock.json` is committed beside each project; after changing a package, run
`dotnet restore` without `--locked-mode` once to update it, and commit the change.

## How a service installs it

As a project reference, the C# form of the Python library's editable path dependency, so a change
here reaches every service on its next build. No NuGet package is made. From a service at
`<Service>/src/<Service>/`:

```xml
<ItemGroup>
  <ProjectReference Include="../../../libs/dotnet/PlatformAuth/src/PlatformAuth/PlatformAuth.csproj" />
</ItemGroup>
```

adjusted to where the project sits. The service's folder has its own `global.json`,
`Directory.Build.props`, `Directory.Packages.props` and `.editorconfig`, copies of this folder's,
since each C# folder builds from its own directory alone; `scripts/ci/dotnet-sdk.test.sh` checks
that every `global.json` pins the same SDK. Its manifest entry in `infra/services.json` has
`"language": "dotnet"` and `"area": "dotnet"`, and `"ci": {"init_script": ...}` when it has a
database, see "The service manifest" in `infra/README.md`.

## Libraries it is built on

**The .NET 10 SDK, pinned in `global.json`** to 10.0.404 with `rollForward` `latestPatch`, so any
10.0.4xx patch builds and never another feature band or major. .NET 10 is the LTS release; the move
to .NET 12 in November 2027 is a `global.json` and target framework change. `Directory.Build.props`
sets `net10.0`, nullable references, warnings as errors, the SDK's analyzers at
`latest-recommended` with code style enforced in the build, and lock files. Those analyzers are the
linter and `dotnet format` the formatter, both built into the SDK, with the `.editorconfig`
`dotnet new editorconfig` writes, the .NET defaults, as ruff is used with its defaults in Python.
`Directory.Packages.props` holds every package version.

**NSec.Cryptography for the Ed25519 signature**, a thin wrapper over libsodium. .NET 10's own
cryptography has no Ed25519, and libsodium's verification is strict: it rejects non canonical
signatures and small order keys. It ships native libsodium for macOS arm64 and Linux x64.
`Microsoft.IdentityModel.JsonWebTokens` with an EdDSA provider was the alternative, but its
validation comes with issuer, audience, lifetime and leeway defaults that would all have to be
switched off, the inherited leeway the core's doc warns against; BouncyCastle is fully managed but
large for one function and less strict by default. The JWS parsing is written here, about a
hundred lines, because it is policy, not cryptography, and every rule it applies is the contract's.

**ASP.NET Core's shared framework**, `Microsoft.AspNetCore.App`, for the authentication handler,
the policies and the middleware. It is part of the SDK, not a package.

**xUnit v3 for the tests**, with its own assertions, through `xunit.runner.visualstudio` and
`Microsoft.NET.Test.Sdk`, so `dotnet test` runs them through VSTest. xUnit v3 defaults to
Microsoft.Testing.Platform, which the .NET 10 SDK runs through `dotnet test` only when
`global.json` opts in, so the test project turns that off and `global.json` holds the SDK pin
alone. `Microsoft.AspNetCore.Mvc.Testing` brings `TestServer`, and
`Microsoft.Extensions.TimeProvider.Testing` the `FakeTimeProvider`.
