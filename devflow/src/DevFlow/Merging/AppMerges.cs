using Dapper;

using DevFlow.CodeHost;
using DevFlow.Decisions;
using DevFlow.Watching;

using PlatformData;

namespace DevFlow.Merging;

/// <summary>What merging as the app answers. A closed set.</summary>
public abstract record AppMergeOutcome
{
    private AppMergeOutcome()
    {
    }

    /// <summary>The code host was asked, and answered <paramref name="Result"/>.</summary>
    public sealed record Done(MergeResult Result) : AppMergeOutcome;

    /// <summary>The project is stopped, so nothing was asked.</summary>
    public sealed record ProjectStopped : AppMergeOutcome;

    /// <summary>The project's default branch has not been read within five minutes, so nothing was
    /// asked: a card is merged only while its project is watched.</summary>
    public sealed record ProjectUnwatched : AppMergeOutcome;

    /// <summary>The pull request goes into an open feature's branch that has not been read within
    /// five minutes, so nothing was asked: nothing is merged into a feature's branch while it is
    /// not watched, though the project's other work goes on.</summary>
    public sealed record FeatureUnwatched : AppMergeOutcome;

    /// <summary>The card no longer waits for this merge, read under the project's lock: it moved
    /// on, its pull request is another, or the project no longer merges by itself. Nothing was
    /// asked.</summary>
    public sealed record CardMoved : AppMergeOutcome;

    /// <summary>The user has no such project.</summary>
    public sealed record NoSuchProject : AppMergeOutcome;

    /// <summary>No key to merge with: the code host does not reach the repository, or did not
    /// answer, with when to try again if it said.</summary>
    public sealed record NoKey(bool NotFound, DateTimeOffset? RetryAt) : AppMergeOutcome;
}

/// <summary>
/// Dev flow's own merges, as the app, and the record of each that lets the watch tell them from
/// anything else landing on the default branch. A merge is made only while the project is not
/// stopped, and its merge commit is written to <c>merges_made</c> in the same transaction, which
/// holds the lock the watch takes before it judges, so the watch never judges the merge before it is
/// recorded. Should the record fail after the code host merged, or the host not say the commit, the
/// watch finds an unrecorded merge by the app and stops the project: a failure here errs towards
/// asking the owner. The merge check merges a card's pull request through it, and the card is
/// recorded done in the same transaction. See "Watching the default branch" and "The merge check"
/// in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class AppMerges(UserDb database, ICodeHost host, TimeProvider time)
{
    /// <summary>
    /// Merges the pull request with the number in the user's project, while its head is
    /// <paramref name="commit"/>, as the app with a key for merging alone, unless the project is
    /// stopped or its default branch's watch has not begun; and records the merge commit when the
    /// host gives one, as a merge into the default branch, as its watch names it.
    /// </summary>
    public Task<AppMergeOutcome> Merge(Guid userId, Guid projectId, int number, string commit, CancellationToken cancellationToken) =>
        Merge(userId, projectId, number, commit, card: null, cancellationToken);

    /// <summary>
    /// Merges the pull request of the user's card as <see cref="Merge(Guid, Guid, int, string, CancellationToken)"/>
    /// does, into the base its newest build or fix ran on, the default branch or its feature's,
    /// and only while, read under the project's lock, the project is watched, its setting is
    /// still <c>never</c>, and the card still waits for this merge, due for the merge check with this
    /// pull request; so a send back, a change of the setting or a stop made since the check read is
    /// never overtaken. When the code host merges, the card is recorded <c>done</c> in the same
    /// transaction as the merge commit, with the decisions a card on its own marked for the owner,
    /// and when it does not the card is left as it was, for the caller to say why.
    /// </summary>
    public Task<AppMergeOutcome> MergeCard(Guid userId, Guid projectId, int card, int number, string commit, CancellationToken cancellationToken) =>
        Merge(userId, projectId, number, commit, card, cancellationToken);

    private async Task<AppMergeOutcome> Merge(Guid userId, Guid projectId, int number, string commit, int? card, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return await database.InUserTransaction<AppMergeOutcome>(userId, async transaction =>
        {
            if (!await BranchWatch.Lock(transaction, projectId, cancellationToken))
            {
                return new AppMergeOutcome.NoSuchProject();
            }

            // Whether the card is one on its own, whose decisions for the owner open attention
            // issues; a feature's card's are answered on the feature's page, since nothing of a
            // feature reaches the default branch before the owner merges it.
            var onItsOwn = false;

            if (card is { } held)
            {
                switch (WatchStore.Read(await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                    $"SELECT {WatchStore.HoldSql} FROM projects p WHERE p.id = @projectId",
                    new { projectId, watchedSince = time.GetUtcNow() - BranchWatch.UnwatchedAfter },
                    transaction,
                    cancellationToken: cancellationToken))))
                {
                    case ProjectHold.Stopped:
                        return new AppMergeOutcome.ProjectStopped();
                    case ProjectHold.Unwatched:
                        return new AppMergeOutcome.ProjectUnwatched();
                }

                if (await transaction.Connection!.ExecuteScalarAsync<bool?>(new CommandDefinition(
                        """
                        SELECT c.feature_id IS NULL FROM cards c JOIN project_settings s ON s.project_id = c.project_id
                        WHERE c.project_id = @projectId AND c.number = @held AND c.state = 'waiting_for_owner' AND c.merge_check_due
                          AND c.pull_request_number = @number AND s.waits_for_owner = 'never'
                        FOR UPDATE OF c
                        """,
                        new { projectId, held, number },
                        transaction,
                        cancellationToken: cancellationToken)) is not { } own)
                {
                    return new AppMergeOutcome.CardMoved();
                }

                onItsOwn = own;
            }

            // The branch the merge goes into, as Dev flow knows it: a card's, the base its newest
            // build or fix ran on, the default branch or its feature's; any other, the default
            // branch, as its watch names it. Without one, nothing is merged.
            var into = await transaction.Connection!.ExecuteScalarAsync<string?>(new CommandDefinition(
                card is null
                    ? "SELECT branch FROM branch_watches WHERE project_id = @projectId AND feature_id IS NULL"
                    : """
                      SELECT r.base FROM runs r
                      WHERE r.project_id = @projectId AND r.card_number = @card AND r.kind IN ('build', 'fix') AND r.result = 'succeeded'
                      ORDER BY r.started_at DESC, r.id DESC LIMIT 1
                      """,
                new { projectId, card },
                transaction,
                cancellationToken: cancellationToken));
            if (into is null)
            {
                return card is null ? new AppMergeOutcome.ProjectUnwatched() : new AppMergeOutcome.CardMoved();
            }

            if (await transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
                    $"SELECT {WatchStore.FeatureUnwatchedSql}",
                    new { projectId, into, watchedSince = WatchStore.WatchedSince(time.GetUtcNow()) },
                    transaction,
                    cancellationToken: cancellationToken)))
            {
                return new AppMergeOutcome.FeatureUnwatched();
            }

            var project = await transaction.Connection!.QuerySingleAsync<ProjectRow>(new CommandDefinition(
                """
                SELECT p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner, p.name AS Name,
                       EXISTS (SELECT 1 FROM project_stops s WHERE s.project_id = p.id) AS Stopped
                FROM projects p WHERE p.id = @projectId
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            if (project.Stopped)
            {
                return new AppMergeOutcome.ProjectStopped();
            }

            var repository = new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name);
            AppWriteToken key;
            switch (await host.ForWrite(userId, repository, WritePurpose.Merge, cancellationToken))
            {
                case CodeHostResult<AppWriteToken>.Found { Value: var found }:
                    key = found;
                    break;
                case CodeHostResult<AppWriteToken>.NotFound:
                    return new AppMergeOutcome.NoKey(NotFound: true, RetryAt: null);
                case CodeHostResult<AppWriteToken>.Unavailable { RetryAt: var retryAt }:
                    return new AppMergeOutcome.NoKey(NotFound: false, retryAt);
                default:
                    throw new InvalidOperationException("a code host result that is not one of the three");
            }

            var result = await host.MergeAsApp(userId, key, repository, number, commit, cancellationToken);
            if (result is MergeResult.Merged { Commit: { } merged })
            {
                var now = time.GetUtcNow();
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO merges_made (project_id, user_id, branch, commit, number, merged_at)
                    VALUES (@projectId, @userId, @into, @merged, @number, @now)
                    ON CONFLICT (project_id, commit) DO NOTHING
                    """,
                    new { projectId, userId, into, merged, number, now = now.AddTicks(-(now.Ticks % 10)) },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            if (card is { } done && result is MergeResult.Merged)
            {
                // Merged on the code host, so done here, whether or not its answer named the merge
                // commit; one that did not is stopped by the watch, which finds no record of it.
                var now = time.GetUtcNow();
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE cards SET state = 'done', reason = NULL, merge_check_due = false, changed_at = @now
                    WHERE project_id = @projectId AND number = @done
                    """,
                    new { projectId, done, now = now.AddTicks(-(now.Ticks % 10)) },
                    transaction,
                    cancellationToken: cancellationToken));
                // Each decision a card on its own marked for the owner, recorded with the merge so
                // none is lost; its issue opens next. A feature's card's stay in its reports.
                if (onItsOwn)
                {
                    await DecisionStore.Record(transaction, projectId, userId, done, number, commit, now.AddTicks(-(now.Ticks % 10)), cancellationToken);
                }
            }

            return new AppMergeOutcome.Done(result);
        }, cancellationToken);
    }

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name, bool Stopped);
}