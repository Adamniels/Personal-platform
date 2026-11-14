using System.Buffers.Binary;
using System.Security.Cryptography;

using Dapper;

using Npgsql;

using Review.Database.Migrations;

namespace Review.Tests;

/// <summary>
/// <c>migrate</c>, against a real Postgres, each test on a database of its own with no migration
/// applied. See "Migrating" in review/README.md.
/// </summary>
public sealed class MigrateTests
{
    private const string NothingToDo = "nothing to do, every migration is applied\n";

    // Each file appends its own number, so the log shows the order they ran in. Written out of
    // order, and with a gap, so neither the order given nor a count decides it.
    private static readonly (string, string)[] Ordered =
    [
        ("0010_third.sql", "INSERT INTO log (n) VALUES (3);"),
        ("0001_create_log.sql", """
            CREATE TABLE log (seq int GENERATED ALWAYS AS IDENTITY, n int NOT NULL);
            INSERT INTO log (n) VALUES (1);
            """),
        ("0002_second.sql", "INSERT INTO log (n) VALUES (2);"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryFileIsAppliedInOrderAndRecorded()
    {
        await using var database = await TestDatabase.Empty(Ct);
        var before = await Now(database);

        IReadOnlyList<Migration> first;
        await using (var owner = await database.OpenOwner(Ct))
        {
            first = await Migrator.Run(owner, Migrator.Parse(Ordered), Ct);
        }

        var after = await Now(database);
        Assert.Equal([(1, "create_log"), (2, "second"), (10, "third")], first.Select(m => (m.Version, m.Name)));
        var ledger = await Ledger(database);
        Assert.Equal([(1, "create_log"), (2, "second"), (10, "third")], ledger.Select(row => (row.Version, row.Name)));
        Assert.All(ledger, row => Assert.InRange(row.AppliedAt, before, after));

        await using (var admin = await database.OpenAdmin(Ct))
        {
            Assert.Equal([1, 2, 3], await admin.QueryAsync<int>(new CommandDefinition("SELECT n FROM log ORDER BY seq", cancellationToken: Ct)));
            // Applied by the owner, so the owner's default privileges reach what the files create.
            Assert.Equal("review_owner", await admin.QuerySingleAsync<string>(new CommandDefinition(
                "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'log'::regclass", cancellationToken: Ct)));
            // The ledger's shape, as the slice settles it.
            Assert.Equal(
                [("version", "integer", "NO"), ("name", "text", "NO"), ("applied_at", "timestamp with time zone", "NO")],
                await admin.QueryAsync<(string, string, string)>(new CommandDefinition(
                    """
                    SELECT column_name::text, data_type::text, is_nullable::text FROM information_schema.columns
                    WHERE table_schema = 'public' AND table_name = 'schema_migrations' ORDER BY ordinal_position
                    """,
                    cancellationToken: Ct)));
        }

        // A second run, through the command, finds every file applied.
        var second = await Command(database, Migrator.Parse(Ordered));
        Assert.Equal((0, NothingToDo, ""), second);
        Assert.Equal(ledger, await Ledger(database));
    }

    [Fact]
    public async Task TheCommandAppliesReviewsOwnMigrations()
    {
        await using var database = await TestDatabase.Empty(Ct);

        var first = await Command(database, migrations: null);
        var second = await Command(database, migrations: null);

        Assert.Equal(0, first.Code);
        Assert.StartsWith("applied 1 default_privileges\n", first.Out, StringComparison.Ordinal);
        Assert.Equal(Migrator.Embedded().Count, first.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal((0, NothingToDo, ""), second);
    }

    [Fact]
    public async Task AFailingFileLeavesNothingAndStopsTheRun()
    {
        await using var database = await TestDatabase.Empty(Ct);
        var migrations = Migrator.Parse(
        [
            ("0001_first.sql", "CREATE TABLE first (x int);"),
            // Fails after creating a table, which must not survive it.
            ("0002_fails.sql", "CREATE TABLE half (x int); SELECT 1 / 0;"),
            ("0003_later.sql", "CREATE TABLE later (x int);"),
        ]);

        await using (var owner = await database.OpenOwner(Ct))
        {
            var refused = await Assert.ThrowsAsync<MigrationException>(() => Migrator.Run(owner, migrations, Ct));
            Assert.Contains("0002_fails.sql", refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal([(1, "first")], (await Ledger(database)).Select(row => (row.Version, row.Name)));
        Assert.True(await Exists(database, "first"));
        Assert.False(await Exists(database, "half"));
        Assert.False(await Exists(database, "later"));
    }

    [Fact]
    public async Task TheRunRefusesWhenFilesAndLedgerDisagree()
    {
        await using var database = await TestDatabase.Empty(Ct);
        (string, string) first = ("0001_first.sql", "SELECT 1;");
        (string, string) second = ("0002_second.sql", "SELECT 1;");
        (string, string) third = ("0003_third.sql", "SELECT 1;");
        await Apply(database, first, second);

        // Every case also brings a new file that would create a table, so a refusal is seen to
        // come before anything is applied.
        static (string, string) Pending(string table) => ($"0009_{table}.sql", $"CREATE TABLE {table} (x int);");

        // An applied file renamed: the history no longer says what ran.
        await Refused(database, "0002", first, ("0002_renamed.sql", "SELECT 1;"), Pending("after_rename"));
        // An applied file removed.
        await Refused(database, "0002", first, Pending("after_removal"));
        // Two files with one version.
        await Refused(database, "version 3", first, second, third, ("0003_twin.sql", "SELECT 1;"), Pending("after_twin"));
        // Names it cannot order.
        foreach (var name in new[] { "1_short.sql", "00001_long.sql", "0004_Capital.sql", "0004-hyphen.sql", "0004_trailing_.sql", "0004_x.SQL", "0004_.sql" })
        {
            await Refused(database, name, first, second, (name, "SELECT 1;"), Pending("after_bad_name"));
        }

        // A new file numbered below one already applied would run out of order.
        await Apply(database, first, second, third);
        await Refused(database, "0000_too_late.sql", ("0000_too_late.sql", "CREATE TABLE too_late (x int);"), first, second, third);

        Assert.Equal([(1, "first"), (2, "second"), (3, "third")], (await Ledger(database)).Select(row => (row.Version, row.Name)));
        foreach (var table in new[] { "after_rename", "after_removal", "after_twin", "after_bad_name", "too_late" })
        {
            Assert.False(await Exists(database, table), table);
        }
    }

    [Fact]
    public async Task TwoRunsCannotOverlap()
    {
        // The key the slice settles: the first 8 bytes of SHA-256 of review.migrate, big endian.
        Assert.Equal(BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData("review.migrate"u8)), Migrator.LockKey);

        await using var database = await TestDatabase.Empty(Ct);
        await using (var owner = await database.OpenOwner(Ct))
        {
            await TestDatabase.Execute(owner, "CREATE TABLE gate (x int)", Ct);
        }

        // The run's only file waits on a table lock this test holds, so the run is known to be
        // under way, holding its advisory lock, until the test lets it finish.
        await using var holder = await database.OpenAdmin(Ct);
        await using var gate = await holder.BeginTransactionAsync(Ct);
        await TestDatabase.Execute(holder, "LOCK TABLE gate IN ACCESS EXCLUSIVE MODE", Ct);

        var run = Task.Run(async () =>
        {
            await using var owner = await database.OpenOwner(Ct);
            return await Migrator.Run(owner, Migrator.Parse([("0001_wait.sql", "LOCK TABLE gate IN ACCESS SHARE MODE; CREATE TABLE done (x int);")]), Ct);
        }, Ct);
        await UntilOneWaitsOnALock(database);

        await using var other = await database.OpenAdmin(Ct);
        Assert.False(await TryLock(other), "a second session took the migration lock while a run held it");

        await gate.CommitAsync(Ct);
        var applied = await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal([(1, "wait")], applied.Select(m => (m.Version, m.Name)));

        // Released when the run ends.
        Assert.True(await TryLock(other));
        await other.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_unlock(@key)", new { key = Migrator.LockKey }, cancellationToken: Ct));
    }

    [Fact]
    public async Task MigrateReadsOnlyTheOwnersUrl()
    {
        var read = new List<string>();
        var unset = await Command(name => { read.Add(name); return null; }, migrations: null);

        Assert.NotEqual(0, unset.Code);
        Assert.Contains("REVIEW_OWNER_DATABASE_URL", unset.Err, StringComparison.Ordinal);
        Assert.Equal(["REVIEW_OWNER_DATABASE_URL"], read);

        // With every one of review's variables set, the app's included, it still reads the
        // owner's alone.
        await using var database = await TestDatabase.Empty(Ct);
        var environment = new Dictionary<string, string>
        {
            ["REVIEW_OWNER_DATABASE_URL"] = TestDatabase.Url(TestDatabase.Admin(database.Name)),
            ["REVIEW_APP_DATABASE_URL"] = "postgres://review_app:app-secret-a1b2@127.0.0.1:5432/review",
            ["REVIEW_LISTEN_ADDR"] = "127.0.0.1:8083",
        };
        read.Clear();
        var set = await Command(name => { read.Add(name); return environment.GetValueOrDefault(name); }, migrations: null);

        Assert.Equal(0, set.Code);
        Assert.Equal(["REVIEW_OWNER_DATABASE_URL"], read);

        // A password it cannot log in with is refused, and never printed.
        var wrong = TestDatabase.Admin(database.Name);
        wrong.Password = "not-the-password-a1b2c3";
        var refused = await Command(name => name == "REVIEW_OWNER_DATABASE_URL" ? TestDatabase.Url(wrong) : null, migrations: null);

        Assert.NotEqual(0, refused.Code);
        Assert.Contains("could not connect", refused.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-password-a1b2c3", refused.Out + refused.Err, StringComparison.Ordinal);

        // A value that is not a URL is refused naming the variable, never repeating the value.
        var malformed = await Command(_ => "postgres://review_owner:secret-a1b2c3@host/db?sslmode=off", migrations: null);

        Assert.NotEqual(0, malformed.Code);
        Assert.Contains("REVIEW_OWNER_DATABASE_URL", malformed.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-a1b2c3", malformed.Err, StringComparison.Ordinal);
    }

    /// <summary>The command, as the superuser on <paramref name="database"/>, since tests do not
    /// hold the owner's password; what it prints, and its exit code.</summary>
    private static Task<(int Code, string Out, string Err)> Command(TestDatabase database, IReadOnlyList<Migration>? migrations)
    {
        var url = TestDatabase.Url(TestDatabase.Admin(database.Name));
        return Command(name => name == "REVIEW_OWNER_DATABASE_URL" ? url : null, migrations);
    }

    private static async Task<(int Code, string Out, string Err)> Command(Func<string, string?> read, IReadOnlyList<Migration>? migrations)
    {
        await using var stdout = new StringWriter { NewLine = "\n" };
        await using var stderr = new StringWriter { NewLine = "\n" };
        var code = await MigrateCommand.Run(read, stdout, stderr, migrations, Ct);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static async Task Apply(TestDatabase database, params (string, string)[] files)
    {
        await using var owner = await database.OpenOwner(Ct);
        await Migrator.Run(owner, Migrator.Parse(files), Ct);
    }

    /// <summary>Asserts that parsing <paramref name="files"/>, or running them, is refused with a
    /// message holding <paramref name="naming"/>.</summary>
    private static async Task Refused(TestDatabase database, string naming, params (string, string)[] files)
    {
        var refused = await Assert.ThrowsAsync<MigrationException>(async () =>
        {
            var migrations = Migrator.Parse(files);
            await using var owner = await database.OpenOwner(Ct);
            await Migrator.Run(owner, migrations, Ct);
        });
        Assert.Contains(naming, refused.Message, StringComparison.Ordinal);
    }

    private static async Task<List<(int Version, string Name, DateTime AppliedAt)>> Ledger(TestDatabase database)
    {
        await using var admin = await database.OpenAdmin(Ct);
        if (!await Exists(admin, "schema_migrations"))
        {
            return [];
        }

        return [.. await admin.QueryAsync<(int, string, DateTime)>(new CommandDefinition(
            "SELECT version, name, applied_at FROM schema_migrations ORDER BY version", cancellationToken: Ct))];
    }

    private static async Task<bool> Exists(TestDatabase database, string table)
    {
        await using var admin = await database.OpenAdmin(Ct);
        return await Exists(admin, table);
    }

    private static Task<bool> Exists(NpgsqlConnection connection, string table) =>
        connection.QuerySingleAsync<bool>(new CommandDefinition(
            "SELECT to_regclass(@table) IS NOT NULL", new { table = $"public.{table}" }, cancellationToken: Ct));

    private static async Task<DateTime> Now(TestDatabase database)
    {
        await using var admin = await database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<DateTime>(new CommandDefinition("SELECT clock_timestamp()", cancellationToken: Ct));
    }

    private static Task<bool> TryLock(NpgsqlConnection connection) =>
        connection.QuerySingleAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_lock(@key)", new { key = Migrator.LockKey }, cancellationToken: Ct));

    /// <summary>Returns once a connection to <paramref name="database"/> waits on a lock, so the
    /// test knows the run it started is inside its file. Fails after about five seconds.</summary>
    private static async Task UntilOneWaitsOnALock(TestDatabase database)
    {
        await using var admin = await database.OpenAdmin(Ct);
        for (var i = 0; i < 250; i++)
        {
            var waiting = await admin.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'",
                cancellationToken: Ct));
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(20, Ct);
        }

        Assert.Fail("the run never reached the file waiting on the gate");
    }
}