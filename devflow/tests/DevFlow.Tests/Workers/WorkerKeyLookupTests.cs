using Dapper;

using DevFlow.Settings;

using Npgsql;

using PlatformData;
using PlatformData.Testing;

using ReadingSupport = DevFlow.Tests.Reading.Reading;

namespace DevFlow.Tests.Workers;

/// <summary>
/// The tables of migration 0007: <c>worker_keys</c> and <c>workers</c>, guarded like every table
/// and tied to their project's user by a foreign key on both columns, and
/// <c>worker_key_lookup</c>, the second table Dev flow leaves without row level security, which
/// leads a worker's key, as its hash, to its project and user. A trigger on <c>worker_keys</c>
/// keeps it, and the app role reads it and never writes it, as <c>poll_targets</c>. See "Security
/// decisions and accepted risks" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class WorkerKeyLookupTests
{
    private static readonly Guid Alice = Guid.Parse("0190a8f0-0000-7000-8000-00000000a11c");
    private static readonly Guid Bob = Guid.Parse("0190a8f0-0000-7000-8000-000000000b0b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AKeyIsLookedUpUntilItIsReplacedOrItsProjectGoes()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var project = await ReadingSupport.InsertProject(door, Alice);
        var first = Hash(1);

        await PutKey(door, Alice, project, first);
        Assert.Equal([(Hex(first), project, Alice)], await Lookup(database));

        // Replaced: only the new hash leads anywhere.
        var second = Hash(2);
        await PutKey(door, Alice, project, second);
        Assert.Equal([(Hex(second), project, Alice)], await Lookup(database));

        // Disconnected: the key goes with the project, and its lookup with the key.
        await door.InUserTransaction(Alice, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM projects WHERE id = @project", new { project }, transaction, cancellationToken: Ct)), Ct);
        Assert.Empty(await Lookup(database));
    }

    [Fact]
    public async Task TheLookupHoldsOnlyTheHashTheProjectAndTheUser()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var admin = await database.OpenAdmin(Ct);

        var columns = await admin.QueryAsync<(string, string, string)>(new CommandDefinition(
            """
            SELECT column_name, data_type, is_nullable FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'worker_key_lookup' ORDER BY column_name
            """,
            cancellationToken: Ct));
        Assert.Equal([("key_hash", "bytea", "NO"), ("project_id", "uuid", "NO"), ("user_id", "uuid", "NO")], columns);
    }

    [Fact]
    public async Task TheAppRoleReadsTheLookupAndCannotWriteIt()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var project = await ReadingSupport.InsertProject(door, Alice);
        await PutKey(door, Alice, project, Hash(1));

        foreach (var sql in new[]
        {
            "INSERT INTO worker_key_lookup (key_hash, project_id, user_id) VALUES (@hash, gen_random_uuid(), @bob)",
            "UPDATE worker_key_lookup SET user_id = @bob",
            "DELETE FROM worker_key_lookup",
            "TRUNCATE worker_key_lookup",
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => door.InUserTransaction(Alice, transaction =>
                transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, new { bob = Bob, hash = Hash(9) }, transaction, cancellationToken: Ct)), Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
            refused = await Assert.ThrowsAsync<PostgresException>(() => door.WithoutUser(transaction =>
                transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, new { bob = Bob, hash = Hash(9) }, transaction, cancellationToken: Ct)), Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }

        // Read without a user, as a worker's request does, while the keys themselves stay closed.
        Assert.Equal([project], await door.WithoutUser(transaction => transaction.Connection!.QueryAsync<Guid>(new CommandDefinition(
            "SELECT project_id FROM worker_key_lookup WHERE key_hash = @hash", new { hash = Hash(1) }, transaction, cancellationToken: Ct)), Ct));
        Assert.Equal(0, await door.WithoutUser(transaction => transaction.Connection!.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM worker_keys", transaction: transaction, cancellationToken: Ct)), Ct));
        await using var app = await database.OpenApp(Ct);
        Assert.Equal("devflow_owner", await app.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE proname = 'sync_worker_key_lookup'", cancellationToken: Ct)));
    }

    [Fact]
    public async Task AKeyOrAWorkerCannotNameAnotherUsersProject()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var bobs = await ReadingSupport.InsertProject(door, Bob);

        // As Alice, naming Bob's project as hers: the foreign key on the project and its user
        // refuses it, whatever row level security lets through.
        foreach (var sql in new[]
        {
            "INSERT INTO worker_keys (project_id, user_id, key_hash, created_at) VALUES (@bobs, @alice, @hash, now())",
            """
            INSERT INTO workers (project_id, id, user_id, name, recipe_version, base_image, first_seen_at, last_seen_at)
            VALUES (@bobs, gen_random_uuid(), @alice, 'w', '3', 'b', now(), now())
            """,
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => door.InUserTransaction(Alice, transaction =>
                transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, new { bobs, alice = Alice, hash = Hash(1) }, transaction, cancellationToken: Ct)), Ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
        }

        Assert.Empty(await Lookup(database));
    }

    private static Task<UserDb> Door(TestDatabase database) =>
        UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);

    private static byte[] Hash(byte seed) => [.. Enumerable.Repeat(seed, 32)];

    private static Task<int> PutKey(UserDb door, Guid user, Guid project, byte[] hash) =>
        door.InUserTransaction(user, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO worker_keys (project_id, user_id, key_hash, created_at) VALUES (@project, @user, @hash, now())
            ON CONFLICT (project_id) DO UPDATE SET key_hash = EXCLUDED.key_hash, created_at = EXCLUDED.created_at
            """,
            new { project, user, hash },
            transaction,
            cancellationToken: Ct)), Ct);

    /// <summary>The lookup's rows, each hash in lowercase hex.</summary>
    private static async Task<List<(string, Guid, Guid)>> Lookup(TestDatabase database)
    {
        await using var admin = await database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(string, Guid, Guid)>(new CommandDefinition(
            "SELECT encode(key_hash, 'hex'), project_id, user_id FROM worker_key_lookup ORDER BY project_id", cancellationToken: Ct))];
    }

    private static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);
}