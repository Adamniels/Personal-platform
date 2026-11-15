using Dapper;

using DevFlow.Settings;

using Npgsql;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests.Projects;

/// <summary>
/// <c>poll_targets</c>, the one table Dev flow leaves without row level security: how a loop with
/// no logged in user learns which projects exist and whose they are. It holds the two ids and
/// nothing else, a trigger on <c>projects</c> keeps it, and the app role can read it and never
/// write it. See "Security decisions and accepted risks" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class PollTargetTests
{
    private static readonly Guid Alice = Guid.Parse("0190a8f0-0000-7000-8000-00000000a11c");
    private static readonly Guid Bob = Guid.Parse("0190a8f0-0000-7000-8000-000000000b0b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConnectingAddsATargetAndDisconnectingRemovesIt()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);

        // Straight into the table through the door, as nothing but the database stands between.
        var project = await InsertProject(door, Alice, 9001);
        Assert.Equal([(project, Alice)], await Targets(database));

        await door.InUserTransaction(Alice, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM projects WHERE id = @project", new { project }, transaction, cancellationToken: Ct)), Ct);
        Assert.Empty(await Targets(database));
    }

    [Fact]
    public async Task ATargetFollowsItsProjectsId()
    {
        // Nothing changes a project's id today, and a target must not be left behind if one does.
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var project = await InsertProject(door, Alice, 9001);
        var renumbered = Guid.NewGuid();

        await door.InUserTransaction(Alice, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET id = @renumbered WHERE id = @project", new { project, renumbered }, transaction, cancellationToken: Ct)), Ct);

        Assert.Equal([(renumbered, Alice)], await Targets(database));
    }

    [Fact]
    public async Task TheTableHoldsOnlyTheTwoIds()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var admin = await database.OpenAdmin(Ct);

        var columns = await admin.QueryAsync<(string, string, string)>(new CommandDefinition(
            """
            SELECT column_name, data_type, is_nullable FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'poll_targets' ORDER BY column_name
            """,
            cancellationToken: Ct));
        Assert.Equal([("project_id", "uuid", "NO"), ("user_id", "uuid", "NO")], columns);

        var key = await admin.QueryAsync<string>(new CommandDefinition(
            """
            SELECT a.attname FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
            WHERE i.indrelid = 'poll_targets'::regclass AND i.indisprimary
            """,
            cancellationToken: Ct));
        Assert.Equal(["project_id"], key);
    }

    [Fact]
    public async Task TheAppRoleReadsItAndCannotWriteIt()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var project = await InsertProject(door, Alice, 9001);

        foreach (var sql in new[]
        {
            "INSERT INTO poll_targets (project_id, user_id) VALUES (gen_random_uuid(), @bob)",
            "UPDATE poll_targets SET user_id = @bob",
            "DELETE FROM poll_targets",
            "TRUNCATE poll_targets",
        })
        {
            // Through the door as Alice, and outside it with no user at all.
            var refused = await Assert.ThrowsAsync<PostgresException>(() => door.InUserTransaction(Alice, transaction =>
                transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, new { bob = Bob }, transaction, cancellationToken: Ct)), Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
            await using var outside = await database.OpenApp(Ct);
            refused = await Assert.ThrowsAsync<PostgresException>(() => outside.ExecuteAsync(new CommandDefinition(sql, new { bob = Bob }, cancellationToken: Ct)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }

        Assert.Equal([(project, Alice)], await Targets(database));
        // And the function that writes it is not the app role's to call or change.
        await using var app = await database.OpenApp(Ct);
        var owner = await app.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE proname = 'sync_poll_target'", cancellationToken: Ct));
        Assert.Equal("devflow_owner", owner);
    }

    [Fact]
    public async Task ItIsReadableWithoutAUser()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var alices = await InsertProject(door, Alice, 9001);
        var bobs = await InsertProject(door, Bob, 9001);

        // Outside the door, a connection that has never had a user set.
        await using var outside = await database.OpenApp(Ct);
        var targets = await outside.QueryAsync<(Guid, Guid)>(new CommandDefinition(
            "SELECT project_id, user_id FROM poll_targets", cancellationToken: Ct));

        Assert.Equal(new HashSet<(Guid, Guid)> { (alices, Alice), (bobs, Bob) }, targets.ToHashSet());
        // While the projects themselves stay closed to it.
        Assert.Equal(0, await outside.QuerySingleAsync<long>(new CommandDefinition("SELECT count(*) FROM projects", cancellationToken: Ct)));
    }

    private static Task<UserDb> Door(TestDatabase database) =>
        UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);

    private static async Task<Guid> InsertProject(UserDb door, Guid user, long repository)
    {
        var id = Guid.NewGuid();
        await door.InUserTransaction(user, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO projects (id, user_id, code_host, installation_id, repository_id, owner, name, created_at)
            VALUES (@id, @user, 'github', 41, @repository, 'Adamniels', 'Personal-platform', now())
            """,
            new { id, user, repository },
            transaction,
            cancellationToken: Ct)), Ct);
        return id;
    }

    private static async Task<List<(Guid, Guid)>> Targets(TestDatabase database)
    {
        await using var admin = await database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(Guid, Guid)>(new CommandDefinition(
            "SELECT project_id, user_id FROM poll_targets ORDER BY user_id", cancellationToken: Ct))];
    }
}