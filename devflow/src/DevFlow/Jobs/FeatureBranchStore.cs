using System.Globalization;

using Dapper;

using DevFlow.Cards;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>A feature whose branch is to be made before its first card is handed out.</summary>
/// <param name="Id">Its row's id.</param>
/// <param name="Number">The parent issue's number.</param>
/// <param name="Title">The parent's title as it was picked, which its branch is named from.</param>
public sealed record DueBranch(Guid Id, int Number, string Title);

/// <summary>
/// A feature's branch in the database, through the door as the project's user: which feature's
/// branch is due, and the four steps around making it on the code host, each one short
/// transaction, since nothing is held while GitHub answers. Making it is claimed first, by setting
/// the branch's name on the feature and moving it to <c>building</c>, so a second worker asking at
/// the same moment finds it claimed and never asks GitHub for the same name, which would answer it
/// exists and stop the feature for nothing. A claim whose maker never came back, a worker that
/// died between the claim and GitHub's answer, is taken over after
/// <see cref="ClaimLasts"/>, longer than the two requests can take. See "Jobs" in
/// devflow/README.md and "A feature's branch" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class FeatureBranchStore
{
    /// <summary>How long a claim to make a branch holds before another worker may take it over.</summary>
    public static readonly TimeSpan ClaimLasts = TimeSpan.FromMinutes(2);

    /// <summary>The feature's branch, <c>feature-&lt;number&gt;-&lt;slug&gt;</c>, the slug by
    /// <see cref="JobStore.Branch"/>'s rules from the parent's title; <c>feature-&lt;number&gt;</c>
    /// when nothing is left.</summary>
    public static string Name(int number, string title)
    {
        var slug = JobStore.Slug(title);
        return slug.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"feature-{number}")
            : string.Create(CultureInfo.InvariantCulture, $"feature-{number}-{slug}");
    }

    /// <summary>Why a feature waits for the owner when a branch of its name is there already.</summary>
    public static string Exists(string branch) => $"A branch named {branch} already exists";

    /// <summary>
    /// The feature of the project's next card to build, when its branch is not made yet: the first
    /// card free to start, by <see cref="CardStore.QueueOrder"/>, the one taking a card would take
    /// once its branch is there. Null when that card is on its own, or its feature's branch is
    /// made. Read without a lock.
    /// </summary>
    public static async Task<DueBranch?> Due(UserDb database, Guid userId, Guid projectId, DateTimeOffset now, bool runNowOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            var feature = await transaction.Connection!.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                $"""
                SELECT feature_id FROM cards
                WHERE {JobStore.Buildable} AND {RunNowStore.OnlyCards}
                ORDER BY {CardStore.QueueOrder}
                LIMIT 1
                """,
                new { projectId, watchedSince = Watching.WatchStore.WatchedSince(now), runNowOnly },
                transaction,
                cancellationToken: cancellationToken));
            return feature is not { } id
                ? null
                : await transaction.Connection!.QuerySingleOrDefaultAsync<DueBranch>(new CommandDefinition(
                    """
                    SELECT id AS Id, number AS Number, coalesce(picked_title, title) AS Title FROM features
                    WHERE id = @id AND branch_base_commit IS NULL
                    """,
                    new { id },
                    transaction,
                    cancellationToken: cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// Claims making the feature's branch, named <paramref name="branch"/>: true only for the one
    /// caller that set it on a queued feature with none, or took over a claim of the same name
    /// older than <see cref="ClaimLasts"/> with no commit recorded. The feature moves to
    /// <c>building</c>.
    /// </summary>
    public static async Task<bool> Claim(UserDb database, Guid userId, Guid featureId, string branch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction => await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = 'building', reason = NULL, branch = @branch, changed_at = @now
            WHERE id = @featureId AND branch_base_commit IS NULL
              AND ((state = 'queued' AND branch IS NULL) OR (state = 'building' AND branch = @branch AND changed_at < @stale))
            """,
            new { featureId, branch, now, stale = now - ClaimLasts },
            transaction,
            cancellationToken: cancellationToken)) == 1, cancellationToken);
    }

    /// <summary>
    /// Records the commit the feature's branch was made at, while the claim is still this branch's
    /// with none recorded, and begins the branch's watch in the same transaction, read as of now,
    /// with no change judged yet and the branch at the commit it was made at: its first read begins
    /// at the branch's creation, see <see cref="Watching.BranchWatch"/>. So a feature whose branch is made is never without a
    /// watch, and its first card is handed out at once.
    /// </summary>
    public static async Task Made(UserDb database, Guid userId, Guid featureId, string branch, string commit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            WITH made AS (
                UPDATE features SET branch_base_commit = @commit, reason = NULL, changed_at = @now
                WHERE id = @featureId AND state = 'building' AND branch = @branch AND branch_base_commit IS NULL
                RETURNING project_id, user_id, id, branch
            )
            INSERT INTO branch_watches (project_id, user_id, feature_id, branch, last_activity_id, last_commit, began_at, last_read_at, last_attempt_at, last_error)
            SELECT project_id, user_id, id, branch, NULL, @commit, @now, @now, @now, NULL FROM made
            ON CONFLICT ON CONSTRAINT branch_watches_one_per_branch DO UPDATE
            SET branch = EXCLUDED.branch, last_activity_id = NULL, last_commit = EXCLUDED.last_commit, began_at = EXCLUDED.began_at, last_read_at = EXCLUDED.last_read_at,
                last_attempt_at = EXCLUDED.last_attempt_at, last_error = NULL
            """,
            new { featureId, branch, commit, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>A branch of the name is there already, which Dev flow did not record making: the
    /// claim is dropped, so the feature has no record of the branch and never takes it, and the
    /// feature waits for the owner, <see cref="Exists"/>.</summary>
    public static Task Taken(UserDb database, Guid userId, Guid featureId, string branch, DateTimeOffset now, CancellationToken cancellationToken) =>
        Update(database, userId, featureId, branch, "state = 'waiting_for_owner', reason = @reason, branch = NULL, changed_at = @now", new { featureId, branch, reason = Exists(branch), now }, cancellationToken);

    /// <summary>The code host did not make it now: the claim is dropped and the feature is queued
    /// again, so the next worker asking tries again.</summary>
    public static Task Released(UserDb database, Guid userId, Guid featureId, string branch, DateTimeOffset now, CancellationToken cancellationToken) =>
        Update(database, userId, featureId, branch, "state = 'queued', branch = NULL, changed_at = @now", new { featureId, branch, now }, cancellationToken);

    /// <summary>One change to a claimed feature, only while the claim is still
    /// <paramref name="branch"/>'s with no commit recorded, so a claim taken over meanwhile is
    /// left to its new maker.</summary>
    private static async Task Update(UserDb database, Guid userId, Guid featureId, string branch, string set, object parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE features SET {set}
            WHERE id = @featureId AND state = 'building' AND branch = @branch AND branch_base_commit IS NULL
            """,
            parameters,
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }
}