using Dapper;

using Npgsql;

namespace DevFlow.Tests.Cards;

/// <summary>
/// <c>features</c> is under row level security like every table: another user, through the door,
/// neither sees a feature nor changes it, nor plants one for its owner; and a card cannot belong to
/// another project's feature. Against a real Postgres as devflow_app. See "The door" and "Features"
/// in devflow/README.md.
/// </summary>
public sealed class FeatureIsolationTests
{
    private const string Insert = """
        INSERT INTO features (id, project_id, user_id, number, title, web_url, state, priority, issue_created_at, changed_at, read_at)
        VALUES (@id, @project, @user, @number, 'A feature', 'https://github.com/Adamniels/Personal-platform/issues/120', 'ignored', 'none', now(), now(), now())
        """;

    private static CancellationToken Ct => Reading.Reading.Ct;

    [Fact]
    public async Task AnotherUsersFeatureIsNotSeen()
    {
        await using var reading = await Reading.Reading.Start();
        var alice = Reading.Reading.Alice;
        var bob = Reading.Reading.Bob;
        var project = await reading.Project(alice);
        var feature = Guid.CreateVersion7();
        await reading.Door.InUserTransaction(alice, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            Insert, new { id = feature, project, user = alice, number = 120 }, transaction, cancellationToken: Ct)), Ct);

        Assert.Equal(1, await Count(reading, alice));
        Assert.Equal(0, await Count(reading, bob));

        // Aimed at Alice's feature by its id, as Bob: it is not there for him.
        foreach (var change in new[] { "UPDATE features SET state = 'held' WHERE id = @feature", "DELETE FROM features WHERE id = @feature" })
        {
            Assert.Equal(0, await reading.Door.InUserTransaction(bob, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
                change, new { feature }, transaction, cancellationToken: Ct)), Ct));
        }

        // Nor can he plant one as her, in her project.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => reading.Door.InUserTransaction(bob, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            Insert, new { id = Guid.CreateVersion7(), project, user = alice, number = 121 }, transaction, cancellationToken: Ct)), Ct));
        Assert.Contains("row-level security", refused.MessageText, StringComparison.Ordinal);

        // His own card, in his own project, cannot be put in her feature.
        var bobs = await reading.Project(bob, repository: 9002, name: "bobs");
        var planted = await Assert.ThrowsAsync<PostgresException>(() => reading.Door.InUserTransaction(bob, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO cards (id, project_id, user_id, number, title, web_url, state, priority, issue_created_at, changed_at, read_at, feature_id, position)
            VALUES (gen_random_uuid(), @bobs, @bob, 1, 'A card', 'https://example.com', 'ignored', 'none', now(), now(), now(), @feature, 1)
            """,
            new { bobs, bob, feature },
            transaction,
            cancellationToken: Ct)), Ct));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, planted.SqlState);

        await using var admin = await reading.Database.OpenAdmin(Ct);
        Assert.Equal("ignored", await admin.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT state FROM features WHERE id = @feature", new { feature }, cancellationToken: Ct)));
    }

    private static async Task<int> Count(Reading.Reading reading, Guid user) =>
        await reading.Door.InUserTransaction(user, transaction => transaction.Connection!.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM features", transaction: transaction, cancellationToken: Ct)), Ct);
}