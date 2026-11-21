using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Features;

/// <summary>Where a feature's pull request went, as a read of the project found it.</summary>
/// <param name="Feature">The parent's number.</param>
/// <param name="PullRequest">Its pull request's number.</param>
/// <param name="Merged">Whether it merged, rather than closed without merging.</param>
public sealed record FollowedFeature(int Feature, int PullRequest, bool Merged);

/// <summary>
/// A feature's pull request to the default branch, followed by each read of its project as a card's
/// is: one that left the open pull requests is read on its own, and merged, by the owner from the
/// page or by anyone on GitHub, the feature is <c>merged</c>; closed without merging, it is
/// <c>closed</c>. It writes nothing to the code host.
/// See "Merging a feature" in devflow/README.md.
/// </summary>
public static class FeaturePulls
{
    /// <summary>The project's features with a pull request recorded that are not merged or closed
    /// yet, each with its number, read as the project's user.</summary>
    public static async Task<IReadOnlyList<(int Feature, int PullRequest)>> Followed(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<(int, int)>(new CommandDefinition(
            """
            SELECT number, pull_request_number FROM features
            WHERE project_id = @projectId AND pull_request_number IS NOT NULL AND state NOT IN ('merged', 'closed')
            ORDER BY number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)];
    }

    /// <summary>
    /// Moves the feature on as its pull request went, inside the read's transaction, only while it
    /// still has that pull request and is neither merged nor closed: merged, it is <c>merged</c>,
    /// with no reason, and each decision its cards marked for the owner that has no answer is
    /// answered <c>merged</c>, confirmed by merging, as merging a pull request on GitHub confirms
    /// the decisions in it, whoever merged it; closed without merging, it is <c>closed</c>, with
    /// no reason. Either way its branch is no longer watched, since the watch reads only the
    /// branches of features that are neither, and <see cref="FeatureOutcomes"/> tells its cards on
    /// the next minute.
    /// </summary>
    public static async Task Follow(NpgsqlTransaction transaction, Guid projectId, FollowedFeature pull, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(pull);
        if (!pull.Merged)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                UPDATE features SET state = 'closed', reason = NULL, changed_at = @now
                WHERE project_id = @projectId AND number = @feature AND pull_request_number = @pullRequest AND state NOT IN ('merged', 'closed')
                """,
                new { projectId, feature = pull.Feature, pullRequest = pull.PullRequest, now },
                transaction,
                cancellationToken: cancellationToken));
            return;
        }

        var user = await transaction.Connection!.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            UPDATE features SET state = 'merged', reason = NULL, changed_at = @now
            WHERE project_id = @projectId AND number = @feature AND pull_request_number = @pullRequest AND state NOT IN ('merged', 'closed')
            RETURNING user_id
            """,
            new { projectId, feature = pull.Feature, pullRequest = pull.PullRequest, now },
            transaction,
            cancellationToken: cancellationToken));
        if (user is not { } userId || await FeatureStory.Read(transaction, projectId, pull.Feature, cancellationToken) is not { } story)
        {
            return;
        }

        foreach (var card in story.Cards)
        {
            foreach (var decision in card.Decisions.Where(decision => decision.Answer is null))
            {
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO decision_answers (project_id, user_id, card_number, run_id, position, answer, answered_at)
                    VALUES (@projectId, @userId, @card, @run, @position, 'merged', @now)
                    ON CONFLICT (project_id, run_id, position) DO NOTHING
                    """,
                    new { projectId, userId, card = card.Number, run = decision.RunId, position = decision.Position, now },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }
    }
}