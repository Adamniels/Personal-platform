# PlatformData

The database code every C# service with a database needs: the migrator, the door every query goes
through as a user, and the harness its tests make databases with. A C# service references it rather
than copying the code. Review wrote it first and it moved here before a second C# service, Dev
flow, built a database, since the door is the code that enforces isolation between users, and two
copies of it would drift. Why this is a shared library at all, an exception to the rule that no
service imports from another's directory, is in "One repository" in
`High-level/docs/platform-architecture.md`. The rules the door and the migrator implement are in
"Multi user, and how isolation is enforced" and "Databases, migrations, and running it" there.

## What it holds

Two projects. `src/PlatformData/`, which a service's app references:

- **`UserDb`, the door.** `UserDb.Open(service, app, variable, cancellationToken)` holds one
  `NpgsqlDataSource` as the service's app role, opened at startup with one connection, so a
  database it cannot reach, or a wrong password, stops the service there with a
  `StartupException` naming `variable` and never the password. `InUserTransaction(userId, work,
  cancellationToken)` takes a connection from the pool, opens a transaction, sets
  `<service>.user_id` for that transaction alone with `set_config(..., true)`, the `SET LOCAL` the
  isolation section requires, runs the work with the transaction, and commits, or rolls back if
  the work throws. It refuses the empty user id, which names nobody. `WithoutUser(work,
  cancellationToken)` does the same on behalf of nobody, with `<service>.user_id` set empty for the
  transaction, so every guarded table is empty to it and refuses its writes: it is for a loop with
  no logged in user to read the one table its service leaves unguarded on purpose, such as Dev
  flow's `poll_targets`, and everything after that runs as a user. `UserDb.ConnectAsOwner` opens
  the owner's one connection, for `migrate` alone. A service opens no connection of its own.
- **The guard.** Every scoped table has `user_id uuid NOT NULL`, row level security enabled and
  forced, and one policy, `USING (guard) WITH CHECK (guard)`, where the guard is
  `user_id = nullif(current_setting('<service>.user_id', true), '')::uuid`, what
  `UserDb.GuardFor(service)` returns; `UserDb.CurrentUserSqlFor(service)` is its right hand side.
  With no user set it is null, so a query outside the door returns nothing rather than everything,
  and a row written for another user is refused.
- **`Migrator`, the brain's `brain migrate` written again in C#**, with the same rules. Migrations
  are plain SQL files, `NNNN_snake_description.sql`, forward only, embedded in the service's
  assembly under `migrations/`, which `Migrator.Embedded(assembly)` reads. `Migrator.Run(owner,
  service, migrations, cancellationToken)` takes a session advisory lock whose key,
  `Migrator.LockKey(service)`, is the first 8 bytes of SHA-256 of `<service>.migrate` as a big
  endian signed 64 bit integer, so two runs for one service cannot overlap and runs for two never
  wait on each other. It records each file in `schema_migrations (version, name, applied_at)`, and
  runs each new file in its own transaction with its ledger row, so a failing file leaves nothing
  behind and stops the run before any later file. It refuses, applying nothing, when the files and
  the ledger disagree: a name it cannot order, two files with one version, an applied file renamed
  or gone, or a new file numbered below one already applied.
- **`MigrateCommand.Run(service, ownerVariable, assembly, read, stdout, stderr, migrations,
  cancellationToken)`**, the `migrate` command a service's entry point dispatches to. It reads
  `ownerVariable` and nothing else, prints `applied <version> <name>` per file, or `nothing to do,
  every migration is applied`, and returns 1, with a line on `stderr` starting `<service>: ` and
  never holding the password, when it cannot go on.
- **`DatabaseUrl.Parse`**, which turns `postgres://user:password@host:port/database`, the form
  `scripts/agent/services.sh` builds, into Npgsql's settings, refusing anything else with a message
  that never repeats the value, since it holds a password.
- **`StartupException`**, why a service refuses to start, which the door throws and the service
  throws for its own reasons too, so that it has one such exception.

**The service's name** is the one thing each of them takes from the service: `review` for review.
It must match `^[a-z][a-z0-9_]*$`, and is checked wherever it is taken, the door's construction
included, since it goes into SQL as an identifier. It is also the prefix of the service's roles,
`<service>_owner` and `<service>_app`, as `infra/services.json` names them. It is never changed
once a service has migrated, since every policy applied names its setting.

`src/PlatformData.Testing/`, which a service's test project references, and never its app:

- **`TestPostgres`**, the real Postgres a service's tests make their databases on, the C#
  counterpart of the brain's `tests/harness.py`. A service's tests hold one for the run,
  `new TestPostgres(service, appVariable, assembly)`. `Migrated(cancellationToken)` gives a test
  its own copy of a template migrated once per run, as the owner, from the files the assembly
  embeds; `Empty(cancellationToken)` gives a database with no migration applied, for testing
  `migrate` itself. Each is owned by `<service>_owner`, gives `CONNECT` to `<service>_app` alone,
  and is dropped when the test disposes it; a run that dies leaves its databases for a later run,
  which sweeps what is older than ten minutes. `DropTemplate()` drops the template, from an
  assembly fixture once every test has run. It reads two variables, both required, unset being a
  failure rather than a skip: `<SERVICE>_TEST_ADMIN_DATABASE_URL`, the superuser, which only the
  test step holds, and `appVariable`, the app role's own.
- **`TestDatabase`**, one test's database: `App()` for opening the door to it, and `OpenAdmin`,
  `OpenOwner` and `OpenApp` for connections outside the door, as only a test may open.
- **`IsolationChecks`**, the checks every service with a database runs on itself. Each returns what
  it found, and the service's test asserts it is empty, or zero:
  - `Unguarded(database, unguarded, cancellationToken)`: each table in `public` that is not
    guarded exactly as above, but `schema_migrations` and the tables in `unguarded`, which the
    service names in its own test when it deliberately leaves one unguarded. Nothing else is
    allowed by default.
  - `ConnectionHolders(types, allowed)`: each place in `types` outside `allowed` where a member
    mentions an `NpgsqlConnection` or an `NpgsqlDataSource`, by reflection, an async method's
    locals and a lambda's captures included. A service passes its assembly's types and allows
    none, since the door is this library's.
  - `OwnedByTheAppRole(database, cancellationToken)`: how many relations and schemas the app role
    owns, which must be none.

  Each service also keeps a test that a check finds what it should, on tables and types made to
  fail it, so a check cannot pass by checking nothing; review's are
  `IsolationTests.TheGuardFindsEveryWayATableIsLeftOpen` and
  `IsolationTests.TheConnectionCheckFindsAConnectionHeldElsewhere`.

A service using it, when it starts, with `REVIEW_APP_DATABASE_URL` parsed by `DatabaseUrl.Parse`:

```csharp
await using var database = await UserDb.Open("review", appDatabase, "REVIEW_APP_DATABASE_URL", cancellationToken);
builder.Services.AddSingleton(database);
```

and in every query, with the caller's verified `sub`:

```csharp
await db.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Deck>(
    new CommandDefinition("SELECT ... FROM decks ...", parameters, transaction, cancellationToken: cancellationToken)), cancellationToken);
```

and for `migrate`:

```csharp
MigrateCommand.Run("review", "REVIEW_OWNER_DATABASE_URL", typeof(Commands).Assembly, read, stdout, stderr, migrations: null, cancellationToken);
```

with its migrations embedded by its project file:

```xml
<EmbeddedResource Include="../../migrations/*.sql" LogicalName="migrations/%(Filename)%(Extension)" />
```

## What it must never grow into

A place for shared code. It holds the door, the migrator and the harness, the code that keeps
users apart and must do it identically in every C# service, and nothing else: no queries of any
service, no models, no repositories, no connection of its own to another service's database, and
no settings parsing beyond the database URL the door needs. Each service writes its own SQL
through the door, so the query a policy guards is the one in that service's code, and owns its own
tables and migrations. A library that grows is how the drift "One repository" warns about comes
back. A change here changes every C# service at once, and a change to what the door sets or the
guard reads changes every policy already applied, so it changes only with a migration in every
service that uses it.

## Running its tests

From this directory, with the .NET SDK `global.json` pins installed:

```zsh
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore -c Release
dotnet test --no-build --no-restore -c Release
```

These are the steps `.github/workflows/libs.yml` runs for every library under `libs/dotnet/`.
They need no database: how the migration files are parsed and ordered, the lock key, review's
included, the URL parsing and that it never repeats the value, the guard string, review's
included, and that a name that is not an identifier is refused.

**The tests that need a database live in the services**, not here. `libs.yml` has no Postgres, and
a guarantee that lives in SQL is tested against a real one, never a mock, by "What gets tests" in
`High-level/docs/how-we-build.md`. So the migrator's and the door's behaviour on a real database is
tested by each service that uses them, through this library's harness: review's `MigrateTests` and
`IsolationTests`, which `.github/workflows/dotnet.yml` runs with a Postgres for every C# service
whenever anything under `libs/dotnet/` changes, as `scripts/ci/changed-areas.sh` decides. A change
here is therefore tested by every service's tests on the same pull request.

`packages.lock.json` is committed beside each project; after changing a package, run
`dotnet restore` without `--locked-mode` once to update it, and commit the change.

## How a service installs it

As project references, as `PlatformAuth` is installed, so a change here reaches every service on
its next build. No NuGet package is made. From a service's app at `<service>/src/<Service>/`:

```xml
<ItemGroup>
  <ProjectReference Include="../../../libs/dotnet/PlatformData/src/PlatformData/PlatformData.csproj" />
</ItemGroup>
```

and from its test project at `<service>/tests/<Service>.Tests/`:

```xml
<ItemGroup>
  <ProjectReference Include="../../../libs/dotnet/PlatformData/src/PlatformData.Testing/PlatformData.Testing.csproj" />
</ItemGroup>
```

adjusted to where the projects sit. Npgsql and Dapper come through the reference, at this folder's
versions, so the service's `Directory.Packages.props` does not list them. Its `Dockerfile` copies
`libs/dotnet/PlatformData/` beside `libs/dotnet/PlatformAuth/`. Its manifest entry in
`infra/services.json` has a `database` with `<service>_owner` and `<service>_app` as its roles, and
its tests read `<SERVICE>_TEST_ADMIN_DATABASE_URL`, which `dotnet.yml` sets from the entry's name.

## Libraries it is built on

**The .NET 10 SDK, pinned in `global.json`**, with `Directory.Build.props`, `.editorconfig` and
the test packages holding the same values as `PlatformAuth`'s folder, copies rather than shared
files, since each C# folder builds from its own directory alone. Why .NET 10, the analyzers as the
linter and `dotnet format` as the formatter are in "Libraries it is built on" in
`libs/dotnet/PlatformAuth/README.md`.

**Npgsql 10.0.3**, the PostgreSQL driver for .NET, **with Dapper 2.1.89** for mapping rows to
records with the SQL written out where it runs, the versions review had when the code moved here.
Why these and not EF Core is in "Libraries review is built on" in `review/README.md`.

**A migrator of its own, not DbUp.** DbUp's journal does not refuse an applied file that was
renamed or removed, which forward only depends on, and the brain's rules are short enough to write
again.

**xUnit v3 for the tests**, through `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk`, at
`PlatformAuth`'s versions. `PlatformData.Testing` uses no test framework, so a service's tests are
free to choose theirs, and every check returns what it found rather than asserting.