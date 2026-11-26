using Dapper;

using DevFlow.Features;
using DevFlow.Fixing;
using DevFlow.Jobs;
using DevFlow.Projects;
using DevFlow.Protocol;
using DevFlow.PullRequests;

using PlatformData;

namespace DevFlow.Stops;

/// <summary>What running again, or dropping, a card or a feature the owner stopped comes to.</summary>
public enum OwnerStopResult
{
    /// <summary>Done.</summary>
    Done,

    /// <summary>It is not stopped by the owner now: stopped by Dev flow, run again already, or
    /// never stopped.</summary>
    NotStoppedByYou,

    /// <summary>The caller has no such card or feature in the project.</summary>
    NoSuch,
}

/// <summary>
/// What the owner does with a card or a feature he stopped, through the door as him, so another
/// user's is none: "Run again" puts a card back where its stopped run started from, counting
/// nothing against it, or clears a feature's hold; "Drop" leaves a card on its own ignored once its
/// pick-up label is off. Each takes the project's lock first, as recording a run's result does, so
/// neither races a result or a sweep for the same card. See "Stops" in devflow/README.md.
/// </summary>
public static class OwnerStops
{
    /// <summary>Why a card the owner dropped is ignored.</summary>
    public const string DroppedByYou = "Dropped by you";

    /// <summary>
    /// Runs the card the owner stopped again where its stopped run started from: a build's in
    /// <c>queued</c>, a review's in <c>reviewing</c>, a fix's in <c>fixing</c> in the same round,
    /// with the reason that state has when a lost run puts it back there. Its failures in a row,
    /// the commit they remember and its fix rounds are left as they were, since a stop is never a
    /// failure and never a round.
    /// </summary>
    public static async Task<OwnerStopResult> RunCardAgain(UserDb database, Guid userId, Guid projectId, int number, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            if (!await Watching.BranchWatch.Lock(transaction, projectId, cancellationToken)
                || await RunSettings.Read(transaction, projectId, cancellationToken) is not { } settings)
            {
                return OwnerStopResult.NoSuch;
            }

            var card = await transaction.Connection!.QuerySingleOrDefaultAsync<StoppedCard>(new CommandDefinition(
                """
                SELECT state AS State, stopped_by_owner AS ByOwner, stopped_kind AS Kind, stopped_round AS Round
                FROM cards WHERE project_id = @projectId AND number = @number
                FOR UPDATE
                """,
                new { projectId, number },
                transaction,
                cancellationToken: cancellationToken));
            if (card is null)
            {
                return OwnerStopResult.NoSuch;
            }

            if (card is not { State: "stopped", ByOwner: true, Kind: { } kind })
            {
                return OwnerStopResult.NotStoppedByYou;
            }

            var (state, reason) = kind switch
            {
                Job.Review => ("reviewing", PullRequestOpener.ToReview),
                Job.Fix => ("fixing", FixRounds.Waiting(card.Round, settings.FixRoundsCard)),
                _ => ("queued", (string?)null),
            };
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                UPDATE cards SET state = @state, reason = @reason, changed_at = @now,
                    fix_round = CASE WHEN stopped_kind = 'fix' THEN stopped_round ELSE fix_round END,
                    stopped_by_owner = false, stopped_kind = NULL, stopped_round = NULL
                WHERE project_id = @projectId AND number = @number
                """,
                new { projectId, number, state, reason, now },
                transaction,
                cancellationToken: cancellationToken));
            return OwnerStopResult.Done;
        }, cancellationToken);
    }

    /// <summary>
    /// Clears the hold the owner's stop put on the feature, so its next job, the review, fix or
    /// conflict it was at, is handed out, and it says what that state says when a lost run leaves
    /// it there; in any other state, which a read moved it to while it was held, it keeps its
    /// reason unless that is the stop's.
    /// </summary>
    public static async Task<OwnerStopResult> RunFeatureAgain(UserDb database, Guid userId, Guid projectId, int number, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            if (!await Watching.BranchWatch.Lock(transaction, projectId, cancellationToken)
                || await RunSettings.Read(transaction, projectId, cancellationToken) is not { } settings)
            {
                return OwnerStopResult.NoSuch;
            }

            var feature = await transaction.Connection!.QuerySingleOrDefaultAsync<HeldFeature>(new CommandDefinition(
                """
                SELECT f.state AS State, f.reason AS Reason, f.stopped_by_owner_at IS NOT NULL AS Held, f.next_fix_round AS Round,
                       (SELECT d.branch FROM branch_watches d WHERE d.project_id = f.project_id AND d.feature_id IS NULL) AS DefaultBranch
                FROM features f WHERE f.project_id = @projectId AND f.number = @number
                FOR UPDATE OF f
                """,
                new { projectId, number },
                transaction,
                cancellationToken: cancellationToken));
            if (feature is null)
            {
                return OwnerStopResult.NoSuch;
            }

            if (!feature.Held)
            {
                return OwnerStopResult.NotStoppedByYou;
            }

            var reason = feature.State switch
            {
                "reviewing" => FeatureReview.ToReview,
                "fixing" => FeatureFix.Waiting(feature.Round, settings.FixRoundsFeature),
                "resolving" when feature.DefaultBranch is { } branch => FeatureEnd.Conflict(branch),
                _ => feature.Reason == RunStore.StoppedByYou ? null : feature.Reason,
            };
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "UPDATE features SET stopped_by_owner_at = NULL, reason = @reason, changed_at = @now WHERE project_id = @projectId AND number = @number",
                new { projectId, number, reason, now },
                transaction,
                cancellationToken: cancellationToken));
            return OwnerStopResult.Done;
        }, cancellationToken);
    }

    /// <summary>The caller's card with the number in their project, with what dropping it needs, or
    /// null when they have no such project or card.</summary>
    public static Task<DropTarget?> FindDroppable(UserDb database, Guid userId, Guid projectId, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<DropTarget>(new CommandDefinition(
            """
            SELECT p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner, p.name AS Name,
                   c.number AS Number, c.state = 'stopped' AND c.stopped_by_owner AS StoppedByOwner,
                   c.feature_id IS NOT NULL AS OfAFeature, s.pickup_label AS PickupLabel
            FROM cards c
            JOIN projects p ON p.id = c.project_id
            JOIN project_settings s ON s.project_id = c.project_id
            WHERE c.project_id = @projectId AND c.number = @number
            """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>
    /// Leaves the card the owner stopped <c>ignored</c>, <see cref="DroppedByYou"/>, dropped at
    /// <paramref name="now"/>, once its pick-up label is off, and only while it is still stopped by
    /// him; its branch, its pull request and everything its runs left are kept. Whether it was.
    /// </summary>
    public static async Task<bool> Dropped(UserDb database, Guid userId, Guid projectId, int number, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
            await Watching.BranchWatch.Lock(transaction, projectId, cancellationToken)
            && await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                UPDATE cards SET state = 'ignored', reason = @reason, dropped_at = @now, changed_at = @now,
                    stopped_by_owner = false, stopped_kind = NULL, stopped_round = NULL
                WHERE project_id = @projectId AND number = @number AND state = 'stopped' AND stopped_by_owner AND feature_id IS NULL
                """,
                new { projectId, number, reason = DroppedByYou, now },
                transaction,
                cancellationToken: cancellationToken)) > 0, cancellationToken);
    }

    /// <summary>A card as running it again reads it.</summary>
    private sealed record StoppedCard(string State, bool ByOwner, string? Kind, int? Round);

    /// <summary>A feature as running it again reads it.</summary>
    private sealed record HeldFeature(string State, string? Reason, bool Held, int? Round, string? DefaultBranch);
}

/// <summary>A card the owner asked to drop, as stored, with what dropping it needs.</summary>
/// <param name="StoppedByOwner">Whether it is stopped, by him.</param>
/// <param name="OfAFeature">Whether it is a card of a feature, whose pick-up label is its
/// feature's, not its own.</param>
/// <param name="PickupLabel">The project's pick-up label, which dropping takes off.</param>
public sealed record DropTarget(long Installation, long RepositoryId, string Owner, string Name, int Number, bool StoppedByOwner, bool OfAFeature, string PickupLabel)
{
    /// <summary>The repository, as the code host is asked for it.</summary>
    public CodeHost.Repository Repository => new(Installation, RepositoryId, Owner, Name);
}