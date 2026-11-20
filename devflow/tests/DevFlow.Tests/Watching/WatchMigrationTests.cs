using Dapper;

using DevFlow.Settings;

using PlatformData;

namespace DevFlow.Tests.Watching;

/// <summary>
/// The migration that moves <c>branch_watches</c> from one row per project to one per project and
/// branch, against a real Postgres: every project's watch of its default branch is kept as it was,
/// and Dev flow's merges so far are recorded as into the default branch. See "Watching branches" in
/// devflow/README.md.
/// </summary>
public sealed class WatchMigrationTests
{
    /// <summary>The last migration before a watch per branch.</summary>
    private const int Before = 24;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid User = Guid.Parse("0190a8f0-0000-7000-8000-0000000a11ce");

    private static readonly Guid Watched = Guid.Parse("0190a8f0-0000-7000-8000-00000000b001");

    private static readonly Guid Failing = Guid.Parse("0190a8f0-0000-7000-8000-00000000b002");

    private static readonly Guid Unread = Guid.Parse("0190a8f0-0000-7000-8000-00000000b003");

    private static string Commit(int n) => n.ToString("x40", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheDefaultBranchsWatchIsKept()
    {
        await using var database = await Databases.DevFlow.Empty(Ct);
        var all = Migrator.Embedded(typeof(Commands).Assembly);
        await using (var owner = await database.OpenOwner(Ct))
        {
            await Migrator.Run(owner, DevFlowSettings.Service, [.. all.Where(migration => migration.Version <= Before)], Ct);
        }

        var began = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        var read = began.AddDays(3);
        var tried = read.AddMinutes(1);
        await using (var admin = await database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO projects (id, user_id, code_host, installation_id, repository_id, owner, name, created_at)
                VALUES (@watched, @user, 'github', 1, 101, 'Adamniels', 'Personal-platform', @began),
                       (@failing, @user, 'github', 1, 102, 'Adamniels', 'fodr', @began),
                       (@unread, @user, 'github', 1, 103, 'Adamniels', 'new', @began);
                INSERT INTO branch_watches (project_id, user_id, branch, last_activity_id, began_at, last_read_at, last_attempt_at, last_error)
                VALUES (@watched, @user, 'main', 4711, @began, @read, @read, NULL),
                       (@failing, @user, 'trunk', NULL, @began, @read, @tried, 'GitHub did not answer');
                INSERT INTO merges_made (project_id, user_id, commit, number, merged_at)
                VALUES (@watched, @user, @merged, 7, @read),
                       (@failing, @user, @other, 8, @read);
                """,
                new { watched = Watched, failing = Failing, unread = Unread, user = User, began, read, tried, merged = Commit(1), other = Commit(2) },
                cancellationToken: Ct));
        }

        await using (var owner = await database.OpenOwner(Ct))
        {
            Assert.NotEmpty(await Migrator.Run(owner, DevFlowSettings.Service, all, Ct));
        }

        await using var check = await database.OpenAdmin(Ct);
        var watches = (await check.QueryAsync<WatchRow>(new CommandDefinition(
            """
            SELECT project_id AS ProjectId, user_id AS UserId, feature_id AS FeatureId, branch AS Branch, last_activity_id AS LastActivityId,
                   began_at AS BeganAt, last_read_at AS LastReadAt, last_attempt_at AS LastAttemptAt, last_error AS LastError
            FROM branch_watches ORDER BY project_id
            """,
            cancellationToken: Ct))).ToList();
        Assert.Equal(
            [
                new WatchRow(Watched, User, null, "main", 4711, began, read, read, null),
                new WatchRow(Failing, User, null, "trunk", null, began, read, tried, "GitHub did not answer"),
            ],
            watches);

        // Every merge so far went into the default branch, as its project's watch names it.
        Assert.Equal(
            [(Watched, "main", Commit(1)), (Failing, "trunk", Commit(2))],
            await check.QueryAsync<(Guid, string, string)>(new CommandDefinition(
                "SELECT project_id, branch, commit FROM merges_made ORDER BY project_id", cancellationToken: Ct)));

        // One default branch's watch per project, and one per feature.
        var second = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => check.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO branch_watches (project_id, user_id, branch, last_activity_id, began_at, last_read_at, last_attempt_at)
            VALUES (@watched, @user, 'other', NULL, @began, @began, @began)
            """,
            new { watched = Watched, user = User, began },
            cancellationToken: Ct)));
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, second.SqlState);
    }

    private sealed record WatchRow(Guid ProjectId, Guid UserId, Guid? FeatureId, string Branch, long? LastActivityId, DateTime BeganAt, DateTime LastReadAt, DateTime LastAttemptAt, string? LastError);
}