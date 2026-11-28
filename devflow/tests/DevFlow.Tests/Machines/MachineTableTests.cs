using Dapper;

using DevFlow.Machines;
using DevFlow.Tests.Reading;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace DevFlow.Tests.Machines;

/// <summary>
/// The tables of migration 0064: <c>machines</c>, <c>machine_keys</c> and
/// <c>machine_enrollments</c>, guarded like every table, a key tied to its machine's user by a
/// foreign key on both columns; and <c>machine_key_lookup</c> and
/// <c>machine_enrollment_lookup</c>, the third and fourth tables Dev flow leaves without row level
/// security, which lead a key or a code, as its hash, to its user. A trigger keeps each, and the app
/// role reads them and never writes them, as <c>worker_key_lookup</c>. See "Security decisions and
/// accepted risks" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class MachineTableTests
{
    private static CancellationToken Ct => Machines.Ct;

    private static readonly MachineIntro Mini = new("mac-mini", "macos", "arm64");

    [Fact]
    public async Task TheLookupsAreKeptByTheirTriggersAndOnlyRead()
    {
        await using var routes = await Routes.Start();
        var enrollments = routes.App.Services.GetRequiredService<MachineEnrollments>();
        var used = await enrollments.Make(Machines.User, Ct);
        var unused = await enrollments.Make(Machines.User, Ct);
        var (machine, key) = await enrollments.Enroll(used.Code, Mini, Ct) ?? throw new InvalidOperationException("the code did not enrol");

        // One row each: a key's hash, its machine and user; a code's hash and its user.
        await using var admin = await routes.Database.OpenAdmin(Ct);
        Assert.Equal([(Machines.Hash(key), machine, Machines.User)], await admin.QueryAsync<(byte[], Guid, Guid)>(new CommandDefinition(
            "SELECT key_hash, machine_id, user_id FROM machine_key_lookup", cancellationToken: Ct)));
        Assert.Equal(
            new[] { Machines.Hash(used.Code), Machines.Hash(unused.Code) }.OrderBy(Convert.ToHexString),
            (await admin.QueryAsync<byte[]>(new CommandDefinition("SELECT code_hash FROM machine_enrollment_lookup WHERE user_id = @user", new { user = Machines.User }, cancellationToken: Ct))).OrderBy(Convert.ToHexString));
        Assert.Equal(
            [("key_hash", "bytea", "NO"), ("machine_id", "uuid", "NO"), ("user_id", "uuid", "NO")],
            await Columns(admin, "machine_key_lookup"));
        Assert.Equal([("code_hash", "bytea", "NO"), ("user_id", "uuid", "NO")], await Columns(admin, "machine_enrollment_lookup"));
        foreach (var (table, primary) in new[] { ("machine_key_lookup", "key_hash"), ("machine_enrollment_lookup", "code_hash") })
        {
            Assert.Equal([primary], await admin.QueryAsync<string>(new CommandDefinition(
                $"""
                SELECT a.attname FROM pg_index i
                JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
                WHERE i.indrelid = '{table}'::regclass AND i.indisprimary
                """,
                cancellationToken: Ct)));
        }

        // The app role reads them without a user, and never writes them, with a user or without.
        foreach (var sql in new[]
        {
            "INSERT INTO machine_key_lookup (key_hash, machine_id, user_id) VALUES (@hash, gen_random_uuid(), @bob)",
            "UPDATE machine_key_lookup SET user_id = @bob",
            "DELETE FROM machine_key_lookup",
            "TRUNCATE machine_key_lookup",
            "INSERT INTO machine_enrollment_lookup (code_hash, user_id) VALUES (@hash, @bob)",
            "UPDATE machine_enrollment_lookup SET user_id = @bob",
            "DELETE FROM machine_enrollment_lookup",
            "TRUNCATE machine_enrollment_lookup",
        })
        {
            var parameters = new { bob = Machines.Bob, hash = new byte[32] };
            var refused = await Assert.ThrowsAsync<PostgresException>(() => routes.Door.WithoutUser(transaction =>
                transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: Ct)), Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
            refused = await Assert.ThrowsAsync<PostgresException>(() => routes.Door.InUserTransaction(Machines.User, transaction =>
                transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: Ct)), Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }

        Assert.Equal([machine], await routes.Door.WithoutUser(transaction => transaction.Connection!.QueryAsync<Guid>(new CommandDefinition(
            "SELECT machine_id FROM machine_key_lookup WHERE key_hash = @hash", new { hash = Machines.Hash(key) }, transaction, cancellationToken: Ct)), Ct));
        // While the guarded tables themselves stay closed without a user.
        foreach (var table in new[] { "machines", "machine_keys", "machine_enrollments" })
        {
            Assert.Equal(0, await routes.Door.WithoutUser(transaction => transaction.Connection!.QuerySingleAsync<long>(new CommandDefinition(
                $"SELECT count(*) FROM {table}", transaction: transaction, cancellationToken: Ct)), Ct));
        }

        await using (var app = await routes.Database.OpenApp(Ct))
        {
            Assert.Equal(["devflow_owner", "devflow_owner"], await app.QueryAsync<string>(new CommandDefinition(
                "SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE proname IN ('sync_machine_key_lookup', 'sync_machine_enrollment_lookup') AND prosecdef",
                cancellationToken: Ct)));
        }

        // Removed, the machine takes its key and its lookup row with it.
        await routes.Door.InUserTransaction(Machines.User, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM machines WHERE id = @machine", new { machine }, transaction, cancellationToken: Ct)), Ct);
        Assert.Equal(0, await admin.QuerySingleAsync<long>(new CommandDefinition("SELECT count(*) FROM machine_keys", cancellationToken: Ct)));
        Assert.Equal(0, await admin.QuerySingleAsync<long>(new CommandDefinition("SELECT count(*) FROM machine_key_lookup", cancellationToken: Ct)));
    }

    [Fact]
    public async Task ANewCodeDeletesOnlyTheExpired()
    {
        await using var routes = await Routes.Start();
        var enrollments = routes.App.Services.GetRequiredService<MachineEnrollments>();
        var old = await enrollments.Make(Machines.User, Ct);
        var bobs = await enrollments.Make(Machines.Bob, Ct);
        routes.Time.Advance(TimeSpan.FromMinutes(30));
        var recent = await enrollments.Make(Machines.User, Ct);
        routes.Time.Advance(TimeSpan.FromMinutes(30));

        // The first is an hour old: it no longer enrols, and the next code made deletes it, its
        // lookup row with it, while the recent one and Bob's, another user's, stay.
        await enrollments.Make(Machines.User, Ct);

        await using var admin = await routes.Database.OpenAdmin(Ct);
        var left = (await admin.QueryAsync<byte[]>(new CommandDefinition("SELECT code_hash FROM machine_enrollment_lookup", cancellationToken: Ct))).ToList();
        Assert.Equal(3, left.Count);
        Assert.DoesNotContain(Machines.Hash(old.Code), left);
        Assert.Contains(Machines.Hash(recent.Code), left);
        Assert.Contains(Machines.Hash(bobs.Code), left);
        Assert.Null(await enrollments.Enroll(old.Code, Mini, Ct));
        Assert.NotNull(await enrollments.Enroll(recent.Code, Mini, Ct));
    }

    [Fact]
    public async Task AKeyCannotNameAnotherUsersMachine()
    {
        await using var routes = await Routes.Start();
        var enrollments = routes.App.Services.GetRequiredService<MachineEnrollments>();
        var (bobs, _) = await enrollments.Enroll((await enrollments.Make(Machines.Bob, Ct)).Code, Mini, Ct)
            ?? throw new InvalidOperationException("the code did not enrol");

        // Bob's machine without its key, so only the foreign key stands in the way.
        await routes.Door.InUserTransaction(Machines.Bob, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM machine_keys WHERE machine_id = @bobs", new { bobs }, transaction, cancellationToken: Ct)), Ct);

        // As Alice, naming Bob's machine as hers: the foreign key on the machine and its user
        // refuses it, whatever row level security lets through.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => routes.Door.InUserTransaction(Machines.User, transaction =>
            transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "INSERT INTO machine_keys (machine_id, user_id, key_hash, created_at) VALUES (@bobs, @alice, @hash, now())",
                new { bobs, alice = Machines.User, hash = new byte[32] },
                transaction,
                cancellationToken: Ct)), Ct));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    private static async Task<List<(string, string, string)>> Columns(NpgsqlConnection admin, string table) =>
        [.. await admin.QueryAsync<(string, string, string)>(new CommandDefinition(
            """
            SELECT column_name, data_type, is_nullable FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table ORDER BY column_name
            """,
            new { table },
            cancellationToken: Ct))];
}