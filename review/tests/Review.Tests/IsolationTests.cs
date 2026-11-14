using System.Reflection;

using Dapper;

using Npgsql;

using Review.Database;

namespace Review.Tests;

/// <summary>
/// Isolation between users, enforced by Postgres and reached only through the door,
/// <see cref="ReviewDb.InUserTransaction"/>. Each test that needs a database runs on its own copy
/// of the migrated database, as review_app, the role review serves as. See "The door" in
/// review/README.md.
///
/// The tests of the door itself make a probe table, as review_owner, the way every scoped table is
/// made: <c>user_id</c>, row level security enabled and forced, and the one policy, so they hold
/// whatever review's own tables become. <see cref="EveryTableIsGuarded"/> checks review's own.
/// </summary>
public sealed class IsolationTests
{
    private static readonly Guid Alice = Guid.Parse("0190a8f0-0000-7000-8000-00000000a11c");
    private static readonly Guid Bob = Guid.Parse("0190a8f0-0000-7000-8000-000000000b0b");

    private const string Probe = $"""
        CREATE TABLE probe (id uuid PRIMARY KEY, user_id uuid NOT NULL, note text NOT NULL);
        ALTER TABLE probe ENABLE ROW LEVEL SECURITY;
        ALTER TABLE probe FORCE ROW LEVEL SECURITY;
        CREATE POLICY probe_user ON probe USING ({ReviewDb.Guard}) WITH CHECK ({ReviewDb.Guard});
        """;

    // The tables that hold nothing about a person: the ledger alone.
    private static readonly string[] GlobalTables = ["schema_migrations"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARowIsVisibleOnlyToItsUser()
    {
        await using var database = await WithProbe();
        await using var door = await ReviewDb.Open(database.App(), Ct);

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
        await using var door = await ReviewDb.Open(database.App(), Ct);
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
        await using var door = await ReviewDb.Open(database.App(), Ct);

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
        await using var door = await ReviewDb.Open(database.App(), Ct);
        await Insert(door, Alice, "alice's");

        // Allowed to set, but then any query a policy would filter is an error rather than an
        // unfiltered read.
        await RefusedAs(door, Bob, "row-level security", "SET row_security = off; SELECT * FROM probe");
        await RefusedAs(door, Bob, "must be owner", "ALTER TABLE probe DISABLE ROW LEVEL SECURITY");
        await RefusedAs(door, Bob, "must be owner", "ALTER TABLE probe NO FORCE ROW LEVEL SECURITY");
        await RefusedAs(door, Bob, "must be owner", "DROP POLICY probe_user ON probe");
        await RefusedAs(door, Bob, "permission denied", "SET ROLE review_owner");
        await RefusedAs(door, Bob, "permission denied", "TRUNCATE probe");

        Assert.Equal(["alice's"], await Notes(door, Alice));
    }

    [Fact]
    public async Task TheDoorTakesOnlyAUser()
    {
        await using var door = ReviewDb.Unopened(Harness.Defaults.AppDatabase);

        // The empty id names nobody, and a transaction for nobody is a bug upstream.
        await Assert.ThrowsAsync<ArgumentException>(() => door.InUserTransaction(Guid.Empty, _ => Task.FromResult(0), Ct));
    }

    [Fact]
    public async Task EveryTableIsGuarded()
    {
        // Every migration in review/migrations/ has run on this database, so a table a later one
        // adds without its guard fails here.
        await using var database = await TestDatabase.Migrated(Ct);
        Assert.Empty(await Unguarded(database));

        // And a table made the way the slice settles passes, so the check is not refusing all.
        await using (var owner = await database.OpenOwner(Ct))
        {
            await TestDatabase.Execute(owner, Probe, Ct);
        }

        Assert.Empty(await Unguarded(database));
    }

    [Fact]
    public async Task ARowCannotPointAtAnotherUsersRow()
    {
        // A foreign key is checked without row level security, so each reference carries user_id:
        // even a query that skipped review's own checks cannot file a card in another user's deck,
        // or give another user's card a topic.
        await using var database = await TestDatabase.Migrated(Ct);
        await using var door = await ReviewDb.Open(database.App(), Ct);
        var (deck, card) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await door.InUserTransaction(Alice, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO decks (id, user_id, name, created_at) VALUES (@deck, @alice, 'Alice''s', now());
            INSERT INTO cards (id, user_id, deck_id, front, back, due, created_at, updated_at)
                VALUES (@card, @alice, @deck, 'front', 'back', now(), now(), now());
            """,
            new { deck, card, alice = Alice }, transaction, cancellationToken: Ct)), Ct);

        foreach (var sql in new[]
        {
            """
            INSERT INTO cards (id, user_id, deck_id, front, back, due, created_at, updated_at)
                VALUES (gen_random_uuid(), @bob, @deck, 'front', 'back', now(), now(), now())
            """,
            "INSERT INTO card_topics (card_id, user_id, position, slug, name) VALUES (@card, @bob, 0, 'planted', 'Planted')",
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => door.InUserTransaction(Bob, transaction =>
                transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, new { deck, card, bob = Bob }, transaction, cancellationToken: Ct)), Ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
        }

        await using var admin = await database.OpenAdmin(Ct);
        Assert.Equal(0, await admin.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT (SELECT count(*) FROM cards WHERE user_id = @bob) + (SELECT count(*) FROM card_topics WHERE user_id = @bob)",
            new { bob = Bob }, cancellationToken: Ct)));
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
                CREATE POLICY nullable_user_user ON nullable_user USING ({ReviewDb.Guard}) WITH CHECK ({ReviewDb.Guard});
                CREATE TABLE bare_cast (id uuid PRIMARY KEY, user_id uuid NOT NULL);
                ALTER TABLE bare_cast ENABLE ROW LEVEL SECURITY;
                ALTER TABLE bare_cast FORCE ROW LEVEL SECURITY;
                CREATE POLICY bare_cast_user ON bare_cast USING (user_id = current_setting('review.user_id')::uuid)
                    WITH CHECK (user_id = current_setting('review.user_id')::uuid);
                {Like("extra_select")}
                CREATE POLICY extra_select_open ON extra_select FOR SELECT USING (true);
                {Like("narrowed")}
                CREATE POLICY narrowed_notes ON narrowed AS RESTRICTIVE USING (note <> '');
                {Like("loose_check").Replace($"WITH CHECK ({ReviewDb.Guard})", "WITH CHECK (true)", StringComparison.Ordinal)}
                {Like("no_check").Replace($" WITH CHECK ({ReviewDb.Guard})", "", StringComparison.Ordinal)}
                {Like("select_only").Replace("ON select_only USING", "ON select_only FOR SELECT USING", StringComparison.Ordinal).Replace($" WITH CHECK ({ReviewDb.Guard})", "", StringComparison.Ordinal)}
                {Like("owner_only").Replace("ON owner_only USING", "ON owner_only TO review_owner USING", StringComparison.Ordinal)}
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
        ], await Unguarded(database));
    }

    [Fact]
    public void OnlyTheDoorOpensConnections()
    {
        Assert.Empty(Openers(typeof(ReviewDb).Assembly.GetTypes(), allowed: typeof(ReviewDb)));
    }

    [Fact]
    public void TheConnectionCheckFindsAConnectionHeldElsewhere()
    {
        // So the test above cannot pass by checking nothing: each of these holds one where the
        // check looks, and none is the door.
        var found = Openers(Nested(typeof(Leaks)), allowed: typeof(ReviewDb)).ToList();

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
        Assert.Empty(Openers(Nested(typeof(Leaks)), allowed: typeof(Leaks)));
    }

    [Fact]
    public async Task TheAppRoleOwnsNothing()
    {
        await using var database = await TestDatabase.Migrated(Ct);
        await using (var admin = await database.OpenAdmin(Ct))
        {
            Assert.Equal(0, await admin.QuerySingleAsync<long>(new CommandDefinition(
                """
                SELECT (SELECT count(*) FROM pg_class WHERE relowner = 'review_app'::regrole)
                     + (SELECT count(*) FROM pg_namespace WHERE nspowner = 'review_app'::regrole)
                """,
                cancellationToken: Ct)));
        }

        await using var door = await ReviewDb.Open(database.App(), Ct);
        await RefusedAs(door, Alice, "permission denied", "CREATE TABLE public.mine (id uuid)");
        await RefusedAs(door, Alice, "permission denied", "CREATE TEMP TABLE mine (id uuid)");
    }

    /// <summary>A copy of the migrated database with the probe table.</summary>
    private static async Task<TestDatabase> WithProbe()
    {
        var database = await TestDatabase.Migrated(Ct);
        await using var owner = await database.OpenOwner(Ct);
        await TestDatabase.Execute(owner, Probe, Ct);
        return database;
    }

    private static async Task<Guid> Insert(ReviewDb door, Guid user, string note)
    {
        var id = Guid.NewGuid();
        await door.InUserTransaction(user, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "INSERT INTO probe (id, user_id, note) VALUES (@id, @user, @note)",
            new { id, user, note }, transaction, cancellationToken: Ct)), Ct);
        return id;
    }

    private static async Task<List<string>> Notes(ReviewDb door, Guid user) =>
        [.. await door.InUserTransaction(user, transaction => transaction.Connection!.QueryAsync<string>(new CommandDefinition(
            "SELECT note FROM probe ORDER BY note", transaction: transaction, cancellationToken: Ct)), Ct)];

    private static Task<int> Change(ReviewDb door, Guid user, string sql, Guid id) =>
        door.InUserTransaction(user, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            sql, new { id }, transaction, cancellationToken: Ct)), Ct);

    /// <summary>Asserts that <paramref name="sql"/>, run through the door as
    /// <paramref name="user"/>, is refused by Postgres for lack of privilege.</summary>
    private static async Task RefusedAs(ReviewDb door, Guid user, string reason, string sql)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() => door.InUserTransaction(user, transaction =>
            transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, new { bob = Bob }, transaction, cancellationToken: Ct)), Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.Contains(reason, refused.MessageText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each table in public but the global ones, and why it is not guarded: row level security
    /// not enabled or not forced, no <c>user_id uuid NOT NULL</c>, or anything but exactly one
    /// policy, the door's: permissive, for every command and every role, reading and checking rows
    /// by <see cref="ReviewDb.Guard"/>. A near copy, such as the bare cast that errors outside the
    /// door instead of returning nothing, is not the door's.
    /// </summary>
    private static async Task<List<string>> Unguarded(TestDatabase database)
    {
        await using var admin = await database.OpenAdmin(Ct);
        var door = await DoorPolicy(admin);
        var tables = await admin.QueryAsync<(string Name, bool Enabled, bool Forced, bool HasUserId)>(new CommandDefinition(
            """
            SELECT c.relname::text, c.relrowsecurity, c.relforcerowsecurity,
                   EXISTS (
                       SELECT FROM pg_attribute a
                       WHERE a.attrelid = c.oid AND a.attname = 'user_id'
                         AND a.atttypid = 'uuid'::regtype AND a.attnotnull
                   )
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
            ORDER BY c.relname COLLATE "C"
            """,
            cancellationToken: Ct));
        var policies = (await admin.QueryAsync<Policy>(new CommandDefinition(
            """
            SELECT tablename::text AS "Table", policyname::text AS "Name", permissive AS "Permissive",
                   cmd AS "Command", roles::text AS "Roles", qual AS "Using", with_check AS "Check"
            FROM pg_policies WHERE schemaname = 'public' ORDER BY tablename COLLATE "C", policyname COLLATE "C"
            """,
            cancellationToken: Ct))).ToList();

        var found = new List<string>();
        foreach (var (name, enabled, forced, hasUserId) in tables.Where(t => !GlobalTables.Contains(t.Name)))
        {
            if (!enabled)
            {
                found.Add($"{name}: row level security is not enabled");
            }

            if (!forced)
            {
                found.Add($"{name}: row level security is not forced");
            }

            if (!hasUserId)
            {
                found.Add($"{name}: no user_id uuid NOT NULL");
            }

            var own = policies.Where(p => p.Table == name).ToList();
            if (own.Count != 1)
            {
                found.Add($"{name}: {own.Count} policies, not exactly the door's one");
            }

            found.AddRange(own.Where(p => p with { Table = door.Table, Name = door.Name } != door).Select(p => $"{name}: policy {p.Name} is not the door's"));
        }

        return found;
    }

    /// <summary>A row of pg_policies.</summary>
    private sealed record Policy(string Table, string Name, string Permissive, string Command, string Roles, string? Using, string? Check);

    /// <summary>The door's policy, as Postgres deparses it into pg_policies, so a table's is
    /// compared with it exactly. Read from a temporary table in a transaction rolled back.</summary>
    private static async Task<Policy> DoorPolicy(NpgsqlConnection admin)
    {
        await using var transaction = await admin.BeginTransactionAsync(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            $"CREATE TEMP TABLE door_shape (user_id uuid); CREATE POLICY door_shape ON door_shape USING ({ReviewDb.Guard}) WITH CHECK ({ReviewDb.Guard})",
            transaction: transaction, cancellationToken: Ct));
        var policy = await admin.QuerySingleAsync<Policy>(new CommandDefinition(
            """
            SELECT tablename::text AS "Table", policyname::text AS "Name", permissive AS "Permissive",
                   cmd AS "Command", roles::text AS "Roles", qual AS "Using", with_check AS "Check"
            FROM pg_policies WHERE tablename = 'door_shape' AND schemaname LIKE 'pg_temp%'
            """,
            transaction: transaction, cancellationToken: Ct));
        await transaction.RollbackAsync(Ct);
        Assert.Equal(("PERMISSIVE", "ALL", "{public}"), (policy.Permissive, policy.Command, policy.Roles));
        return policy;
    }

    /// <summary>
    /// Every place in <paramref name="types"/> outside <paramref name="allowed"/>, and the types
    /// nested in it, compiler generated ones included, where a member mentions a connection or a
    /// data source: a field, a property, an event, a method's or constructor's parameter, return
    /// or local, or a base type or interface, generic arguments and arrays included. An async
    /// method's locals are fields of its state machine, a nested type, and a lambda's captures
    /// fields of its closure.
    /// </summary>
    private static IEnumerable<string> Openers(IEnumerable<Type> types, Type allowed)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in types.Where(t => !IsWithin(t, allowed)))
        {
            var uses = new List<(string Where, Type Used)>();
            if (type.BaseType is { } baseType)
            {
                uses.Add(("base type", baseType));
            }

            uses.AddRange(type.GetInterfaces().Select(i => ("interface", i)));
            uses.AddRange(type.GetFields(Declared).Select(f => ($"field {f.Name}", f.FieldType)));
            uses.AddRange(type.GetProperties(Declared).Select(p => ($"property {p.Name}", p.PropertyType)));
            uses.AddRange(type.GetEvents(Declared).Select(e => ($"event {e.Name}", e.EventHandlerType!)));
            foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            {
                if (method is MethodInfo info)
                {
                    uses.Add(($"return of {method.Name}", info.ReturnType));
                }

                uses.AddRange(method.GetParameters().Select(p => ($"parameter of {method.Name}", p.ParameterType)));
                uses.AddRange((method.GetMethodBody()?.LocalVariables ?? []).Select(l => ($"local in {method.Name}", l.LocalType)));
            }

            foreach (var where in uses.Where(use => Mentions(use.Used)).Select(use => use.Where).Distinct())
            {
                yield return $"{type.FullName![(type.Namespace!.Length + 1)..]}: {where}";
            }
        }
    }

    private static bool Mentions(Type type) =>
        type.IsAssignableTo(typeof(NpgsqlConnection))
        || type.IsAssignableTo(typeof(NpgsqlDataSource))
        || (type.HasElementType && Mentions(type.GetElementType()!))
        || (type.IsConstructedGenericType && type.GetGenericArguments().Any(Mentions));

    private static bool IsWithin(Type type, Type outer)
    {
        for (Type? at = type; at is not null; at = at.DeclaringType)
        {
            if (at == outer)
            {
                return true;
            }
        }

        return false;
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