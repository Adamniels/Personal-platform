using System.Reflection;

using Dapper;

using DevFlow.Settings;

using Npgsql;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests;

/// <summary>
/// Isolation between users, enforced by Postgres and reached only through the door,
/// <see cref="UserDb.InUserTransaction"/>, built with Dev flow's name. Each test that needs a
/// database runs on its own copy of the migrated database, as devflow_app, the role Dev flow serves
/// as. See "The door" in devflow/README.md.
///
/// The tests of the door itself make a probe table, as devflow_owner, the way every scoped table is
/// made: <c>user_id</c>, row level security enabled and forced, and the one policy, so they hold
/// whatever Dev flow's own tables become. <see cref="EveryTableIsGuarded"/> checks Dev flow's own.
/// </summary>
public sealed class IsolationTests
{
    private static readonly Guid Alice = Guid.Parse("0190a8f0-0000-7000-8000-00000000a11c");
    private static readonly Guid Bob = Guid.Parse("0190a8f0-0000-7000-8000-000000000b0b");

    // Dev flow's one policy, which every scoped table has.
    private static readonly string Guard = UserDb.GuardFor(DevFlowSettings.Service);

    private static readonly string Probe = $"""
        CREATE TABLE probe (id uuid PRIMARY KEY, user_id uuid NOT NULL, note text NOT NULL);
        ALTER TABLE probe ENABLE ROW LEVEL SECURITY;
        ALTER TABLE probe FORCE ROW LEVEL SECURITY;
        CREATE POLICY probe_user ON probe USING ({Guard}) WITH CHECK ({Guard});
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARowIsVisibleOnlyToItsUser()
    {
        await using var database = await WithProbe();
        await using var door = await UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);

        var alices = await Insert(door, Alice, "alice's");

        Assert.Equal(["alice's"], await Notes(door, Alice));
        Assert.Empty(await Notes(door, Bob));

        // Aimed at Alice's row by its id, as Bob: the row is not there for him.
        Assert.Equal(0, await Change(door, Bob, "UPDATE probe SET note = 'changed' WHERE id = @id", alices));
        Assert.Equal(0, await Change(door, Bob, "DELETE FROM probe WHERE id = @id", alices));
        Assert.Equal(["alice's"], await Notes(door, Alice));

        // A connection that has never had the setting, outside the door.
        await using var outside = await database.OpenApp(Ct);
        Assert.Equal(0, await outside.QuerySingleAsync<long>(new CommandDefinition("SELECT count(*) FROM probe", cancellationToken: Ct)));
    }

    [Fact]
    public async Task AWriteNamingAnotherUserIsRefused()
    {
        await using var database = await WithProbe();
        await using var door = await UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);
        var alices = await Insert(door, Alice, "alice's");

        // Planting a row for Bob, and handing Alice's own row to him, both fail the policy's check.
        await RefusedAs(door, Alice, "row-level security",
            "INSERT INTO probe (id, user_id, note) VALUES (gen_random_uuid(), @bob, 'planted')");
        await RefusedAs(door, Alice, "row-level security", $"UPDATE probe SET user_id = @bob WHERE id = '{alices}'");

        Assert.Empty(await Notes(door, Bob));
        Assert.Equal(["alice's"], await Notes(door, Alice));
    }

    [Fact]
    public async Task TheDoorCommitsOnlyWorkThatFinishes()
    {
        await using var database = await WithProbe();
        await using var door = await UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => door.InUserTransaction<int>(Alice, async transaction =>
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "INSERT INTO probe (id, user_id, note) VALUES (gen_random_uuid(), @user, 'half done')",
                new { user = Alice }, transaction, cancellationToken: Ct));
            throw new InvalidOperationException("the work failed after writing");
        }, Ct));

        Assert.Empty(await Notes(door, Alice));
    }

    [Fact]
    public async Task TheAppRoleCannotBypassRowLevelSecurity()
    {
        await using var database = await WithProbe();
        await using var door = await UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);
        await Insert(door, Alice, "alice's");

        // Allowed to set, but then any query a policy would filter is an error rather than an
        // unfiltered read.
        await RefusedAs(door, Bob, "row-level security", "SET row_security = off; SELECT * FROM probe");
        await RefusedAs(door, Bob, "must be owner", "ALTER TABLE probe DISABLE ROW LEVEL SECURITY");
        await RefusedAs(door, Bob, "must be owner", "ALTER TABLE probe NO FORCE ROW LEVEL SECURITY");
        await RefusedAs(door, Bob, "must be owner", "DROP POLICY probe_user ON probe");
        await RefusedAs(door, Bob, "permission denied", "SET ROLE devflow_owner");
        await RefusedAs(door, Bob, "permission denied", "TRUNCATE probe");

        Assert.Equal(["alice's"], await Notes(door, Alice));
    }

    [Fact]
    public async Task WithoutAUserEveryGuardedTableIsEmptyAndClosed()
    {
        // How the loop reads poll_targets, the one table it reads outside a user: nothing guarded
        // is there for it, and nothing guarded can be written.
        await using var database = await WithProbe();
        await using var door = await UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);
        await Insert(door, Alice, "alice's");

        Assert.Equal(0, await door.WithoutUser(transaction => transaction.Connection!.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM probe", transaction: transaction, cancellationToken: Ct)), Ct));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => door.WithoutUser(transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "INSERT INTO probe (id, user_id, note) VALUES (gen_random_uuid(), @alice, 'planted')", new { alice = Alice }, transaction, cancellationToken: Ct)), Ct));
        Assert.Contains("row-level security", refused.MessageText, StringComparison.Ordinal);

        // Even on a pooled connection a user's transaction has just used.
        Assert.Equal(["alice's"], await Notes(door, Alice));
        Assert.Equal(0, await door.WithoutUser(transaction => transaction.Connection!.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM probe", transaction: transaction, cancellationToken: Ct)), Ct));
        Assert.Equal(["alice's"], await Notes(door, Alice));
    }

    [Fact]
    public async Task TheDoorTakesOnlyAUser()
    {
        await using var door = UserDb.Unopened(DevFlowSettings.Service, Harness.Defaults.AppDatabase);

        // The empty id names nobody, and a transaction for nobody is a bug upstream.
        await Assert.ThrowsAsync<ArgumentException>(() => door.InUserTransaction(Guid.Empty, _ => Task.FromResult(0), Ct));
    }

    /// <summary>The two tables Dev flow leaves unguarded, each decided with the owner: the ids a loop
    /// with no logged in user reads, and the hash a worker's key is looked up by, with its project
    /// and user. See "Security decisions and accepted risks" in
    /// devflow/docs/devflow-architecture.md. Nothing else belongs here.</summary>
    private static readonly string[] Unguarded = ["poll_targets", "worker_key_lookup"];

    [Fact]
    public async Task EveryTableIsGuarded()
    {
        // Every migration in devflow/migrations/ has run on this database, so a table a later one
        // adds without its guard fails here.
        await using var database = await Databases.DevFlow.Migrated(Ct);
        Assert.Empty(await IsolationChecks.Unguarded(database, Unguarded, Ct));

        // The exceptions are named, not overlooked: without them, each table fails the check.
        var unnamed = await IsolationChecks.Unguarded(database, unguarded: [], Ct);
        Assert.Contains("poll_targets: row level security is not enabled", unnamed);
        Assert.Contains("worker_key_lookup: row level security is not enabled", unnamed);

        // And a table made the way the slice settles passes, so the check is not refusing all.
        await using (var owner = await database.OpenOwner(Ct))
        {
            await TestDatabase.Execute(owner, Probe, Ct);
        }

        Assert.Empty(await IsolationChecks.Unguarded(database, Unguarded, Ct));

        // While another unguarded table fails it, the named exceptions notwithstanding.
        await using (var owner = await database.OpenOwner(Ct))
        {
            await TestDatabase.Execute(owner, "CREATE TABLE second_targets (project_id uuid PRIMARY KEY, user_id uuid NOT NULL)", Ct);
        }

        Assert.Equal(
        [
            "second_targets: row level security is not enabled",
            "second_targets: row level security is not forced",
            "second_targets: 0 policies, not exactly the door's one",
        ], await IsolationChecks.Unguarded(database, Unguarded, Ct));
    }

    [Fact]
    public async Task TheGuardFindsEveryWayATableIsLeftOpen()
    {
        // So the test above cannot pass by checking nothing.
        await using var database = await WithProbe();
        await using (var owner = await database.OpenOwner(Ct))
        {
            static string Like(string table) => Probe.Replace("probe", table, StringComparison.Ordinal);
            await TestDatabase.Execute(owner, $"""
                CREATE TABLE open_table (id uuid PRIMARY KEY, note text);
                CREATE TABLE half_guarded (id uuid PRIMARY KEY, user_id uuid NOT NULL);
                ALTER TABLE half_guarded ENABLE ROW LEVEL SECURITY;
                CREATE TABLE nullable_user (id uuid PRIMARY KEY, user_id uuid);
                ALTER TABLE nullable_user ENABLE ROW LEVEL SECURITY;
                ALTER TABLE nullable_user FORCE ROW LEVEL SECURITY;
                CREATE POLICY nullable_user_user ON nullable_user USING ({Guard}) WITH CHECK ({Guard});
                CREATE TABLE bare_cast (id uuid PRIMARY KEY, user_id uuid NOT NULL);
                ALTER TABLE bare_cast ENABLE ROW LEVEL SECURITY;
                ALTER TABLE bare_cast FORCE ROW LEVEL SECURITY;
                CREATE POLICY bare_cast_user ON bare_cast USING (user_id = current_setting('devflow.user_id')::uuid)
                    WITH CHECK (user_id = current_setting('devflow.user_id')::uuid);
                {Like("extra_select")}
                CREATE POLICY extra_select_open ON extra_select FOR SELECT USING (true);
                {Like("narrowed")}
                CREATE POLICY narrowed_notes ON narrowed AS RESTRICTIVE USING (note <> '');
                {Like("loose_check").Replace($"WITH CHECK ({Guard})", "WITH CHECK (true)", StringComparison.Ordinal)}
                {Like("no_check").Replace($" WITH CHECK ({Guard})", "", StringComparison.Ordinal)}
                {Like("select_only").Replace("ON select_only USING", "ON select_only FOR SELECT USING", StringComparison.Ordinal).Replace($" WITH CHECK ({Guard})", "", StringComparison.Ordinal)}
                {Like("owner_only").Replace("ON owner_only USING", "ON owner_only TO devflow_owner USING", StringComparison.Ordinal)}
                """, Ct);
        }

        Assert.Equal(
        [
            "bare_cast: policy bare_cast_user is not the door's",
            "extra_select: 2 policies, not exactly the door's one",
            "extra_select: policy extra_select_open is not the door's",
            "half_guarded: row level security is not forced",
            "half_guarded: 0 policies, not exactly the door's one",
            "loose_check: policy loose_check_user is not the door's",
            "narrowed: 2 policies, not exactly the door's one",
            "narrowed: policy narrowed_notes is not the door's",
            "no_check: policy no_check_user is not the door's",
            "nullable_user: no user_id uuid NOT NULL",
            "open_table: row level security is not enabled",
            "open_table: row level security is not forced",
            "open_table: no user_id uuid NOT NULL",
            "open_table: 0 policies, not exactly the door's one",
            "owner_only: policy owner_only_user is not the door's",
            "select_only: policy select_only_user is not the door's",
        ], await IsolationChecks.Unguarded(database, Unguarded, Ct));
    }

    [Fact]
    public void OnlyTheDoorOpensConnections()
    {
        Assert.Empty(IsolationChecks.ConnectionHolders(typeof(Commands).Assembly.GetTypes()));
    }

    [Fact]
    public void TheConnectionCheckFindsAConnectionHeldElsewhere()
    {
        // So the test above cannot pass by checking nothing: each of these holds one where the
        // check looks, and none is the door.
        var found = IsolationChecks.ConnectionHolders(Nested(typeof(Leaks))).ToList();

        // The type each finding is in, an async method's state machine named for its method.
        static string In(string finding) =>
            finding[..finding.IndexOf(": ", StringComparison.Ordinal)] is var type && type.StartsWith("Leaks+AsyncHolder+<Open>", StringComparison.Ordinal)
                ? "Leaks+AsyncHolder+<Open>"
                : type;
        Assert.Equal(
            ["Leaks+AsyncHolder+<Open>", "Leaks+FieldHolder", "Leaks+LocalHolder", "Leaks+ParameterTaker", "Leaks+PropertyHolder", "Leaks+ReturnGiver"],
            found.Select(In).Distinct().Order(StringComparer.Ordinal));
        // A local, and an async method's local, which is a field of its state machine.
        Assert.Contains("Leaks+LocalHolder: local in Open", found);
        Assert.Contains(found, f => f.StartsWith("Leaks+AsyncHolder+<Open>", StringComparison.Ordinal) && f.Contains(": field ", StringComparison.Ordinal));
        // And nothing at all once they are the allowed type.
        Assert.Empty(IsolationChecks.ConnectionHolders(Nested(typeof(Leaks)), allowed: typeof(Leaks)));
    }

    [Fact]
    public async Task TheAppRoleOwnsNothing()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        Assert.Equal(0, await IsolationChecks.OwnedByTheAppRole(database, Ct));

        await using var door = await UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);
        await RefusedAs(door, Alice, "permission denied", "CREATE TABLE public.mine (id uuid)");
        await RefusedAs(door, Alice, "permission denied", "CREATE TEMP TABLE mine (id uuid)");
    }

    /// <summary>A copy of the migrated database with the probe table.</summary>
    private static async Task<TestDatabase> WithProbe()
    {
        var database = await Databases.DevFlow.Migrated(Ct);
        await using var owner = await database.OpenOwner(Ct);
        await TestDatabase.Execute(owner, Probe, Ct);
        return database;
    }

    private static async Task<Guid> Insert(UserDb door, Guid user, string note)
    {
        var id = Guid.NewGuid();
        await door.InUserTransaction(user, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "INSERT INTO probe (id, user_id, note) VALUES (@id, @user, @note)",
            new { id, user, note }, transaction, cancellationToken: Ct)), Ct);
        return id;
    }

    private static async Task<List<string>> Notes(UserDb door, Guid user) =>
        [.. await door.InUserTransaction(user, transaction => transaction.Connection!.QueryAsync<string>(new CommandDefinition(
            "SELECT note FROM probe ORDER BY note", transaction: transaction, cancellationToken: Ct)), Ct)];

    private static Task<int> Change(UserDb door, Guid user, string sql, Guid id) =>
        door.InUserTransaction(user, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            sql, new { id }, transaction, cancellationToken: Ct)), Ct);

    /// <summary>Asserts that <paramref name="sql"/>, run through the door as
    /// <paramref name="user"/>, is refused by Postgres for lack of privilege.</summary>
    private static async Task RefusedAs(UserDb door, Guid user, string reason, string sql)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() => door.InUserTransaction(user, transaction =>
            transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, new { bob = Bob }, transaction, cancellationToken: Ct)), Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.Contains(reason, refused.MessageText, StringComparison.Ordinal);
    }

    private static IEnumerable<Type> Nested(Type type) =>
        type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(Nested).Prepend(type);
}

/// <summary>Types that each hold a connection or a data source where the check looks, for
/// <see cref="IsolationTests.TheConnectionCheckFindsAConnectionHeldElsewhere"/>.</summary>
#pragma warning disable CA1812, CA1822, IDE0051, IDE0052, CS0169, CS0649 // Read only by reflection.
internal static class Leaks
{
    private sealed class FieldHolder
    {
        private readonly NpgsqlConnection? Held;
    }

    private sealed class PropertyHolder
    {
        public Lazy<NpgsqlDataSource>? Source { get; set; }
    }

    private sealed class ParameterTaker
    {
        public void Use(NpgsqlConnection connection) => _ = connection;
    }

    private sealed class ReturnGiver
    {
        public Task<NpgsqlConnection>? Give() => null;
    }

    private sealed class LocalHolder
    {
        public void Open(string connectionString)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
        }
    }

    private sealed class AsyncHolder
    {
        public async Task Open(string connectionString)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await Task.Yield();
        }
    }
}
#pragma warning restore CA1812, CA1822, IDE0051, IDE0052, CS0169, CS0649