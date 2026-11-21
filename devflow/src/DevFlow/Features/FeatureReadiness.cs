using Dapper;

using DevFlow.CodeHost;
using DevFlow.Merging;

using PlatformData;

namespace DevFlow.Features;

/// <summary>
/// On the project's minute, after the end of its features: for each feature whose pull request is
/// open, every card done, nothing of its own under way and its branch read since Dev flow last
/// changed it, whether its head needs a review of the whole feature, handed out as a job, and
/// whether it is ready for the owner, by <see cref="FeatureReady.Decide"/>. What it decides from is
/// what Dev flow stored, the head the feature branch's watch named among it, so a review is never
/// asked for a commit the watch has not seen and judged; only CI on a head an approve covers is read
/// from the code host, as the app, as the merge check reads it. It writes nothing to the code host.
/// See "Reviewing a feature" in devflow/README.md and devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class FeatureReadiness(UserDb database, ICodeHost host, TimeProvider time, ILogger<FeatureReadiness> log)
{
    /// <summary>
    /// Decides every feature of the project that is due, as the project's user,
    /// <paramref name="userId"/>. Never throws for one feature's failure, which is logged and tried
    /// again on the next call; only cancellation and a database that cannot be reached escape.
    /// </summary>
    public async Task DecidePending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var (repository, due) = await Read(projectId, userId, cancellationToken);
        foreach (var feature in due)
        {
            try
            {
                var facts = new ReadyFacts(feature.Head, feature.ReviewFailedAt, feature.Latest, feature.CleanMerges);
                // CI is read for a head an approve covers, which decides whether it is ready, with
                // the time it was read at.
                var ci = FeatureReady.Covering(facts) is { Verdict: "approve" } && repository is not null
                    ? await Ci(userId, repository, feature, cancellationToken)
                    : null;
                var decision = FeatureReady.Decide(facts, ci) ?? new ReadyDecision(feature.State, feature.Reason, feature.ReviewCommit);

                // And for a head that waits for the owner whatever its review, since the owner may
                // merge it once CI passed there; read until it has, and not again for that head.
                if (ci is null && decision.State == "waiting_for_owner" && feature.CiPassed != feature.Head && repository is not null)
                {
                    ci = await Ci(userId, repository, feature, cancellationToken);
                }

                await Move(userId, feature, decision, CiPassed(feature, ci), cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, feature.Number, error);
            }
        }
    }

    /// <summary>CI on the feature's head, read as the app, with the time it was read at.</summary>
    private async Task<ReadyCi> Ci(Guid userId, Repository repository, Due feature, CancellationToken cancellationToken) =>
        new(
            await host.CheckRuns(userId, repository, feature.Head, cancellationToken),
            await host.Statuses(userId, repository, feature.Head, cancellationToken),
            time.GetUtcNow(),
            feature.PushedAt);

    /// <summary>
    /// The head CI passed on, as the merge check reads CI by <see cref="MergeCheck.Ci"/>, or null:
    /// from <paramref name="ci"/> when it was read now, a read that failed being no pass; otherwise
    /// the one recorded, while the head is still the one it passed on.
    /// </summary>
    private static string? CiPassed(Due feature, ReadyCi? ci) =>
        ci is not null
            ? MergeCheck.Ci(ci.CheckRuns, ci.Statuses, ci.Now, ci.PushedAt) is null ? feature.Head : null
            : feature.CiPassed == feature.Head ? feature.Head : null;

    /// <summary>
    /// The project's features whose review or readiness is to be decided, read in one transaction
    /// as the user: <c>reviewing</c>, <c>waiting_for_owner</c> or <c>ready</c>, with their pull
    /// request, the default branch brought in, every card done, no run of their own under way, and
    /// their branch's watch read in the last five minutes and since Dev flow's last merge or push
    /// there, so the commit it names holds what Dev flow put there; each with its latest review, the
    /// clean merges Dev flow made on its branch, and when its head was pushed: the time Dev flow
    /// recorded its merge or its push, or the feature's last change for a head pushed by the owner.
    /// The project's repository with them, null for a project the user does not have.
    /// </summary>
    private async Task<(Repository? Repository, IReadOnlyList<Due> Due)> Read(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction<(Repository?, IReadOnlyList<Due>)>(userId, async transaction =>
        {
            var project = await transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
                "SELECT installation_id AS Installation, repository_id AS RepositoryId, owner AS Owner, name AS Name FROM projects WHERE id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            if (project is null)
            {
                return (null, []);
            }

            var rows = (await transaction.Connection!.QueryAsync<DueRow>(new CommandDefinition(
                """
                SELECT f.id AS Id, f.number AS Number, f.branch AS Branch, f.state AS State, f.reason AS Reason, f.review_commit AS ReviewCommit,
                       f.review_failed_at AS ReviewFailedAt, f.ci_passed_commit AS CiPassed, wb.last_commit AS Head,
                       coalesce((SELECT m.merged_at FROM merges_made m WHERE m.project_id = f.project_id AND m.commit = wb.last_commit),
                                (SELECT p.recorded_at FROM pushes_made p WHERE p.project_id = f.project_id AND p.branch = f.branch AND p.commit = wb.last_commit),
                                f.changed_at) AS PushedAt,
                       v.reviewed_commit AS ReviewedCommit, v.verdict AS Verdict
                FROM features f
                JOIN branch_watches wb ON wb.project_id = f.project_id AND wb.feature_id = f.id
                LEFT JOIN LATERAL (SELECT reviewed_commit, verdict FROM feature_reviews
                                   WHERE feature_reviews.project_id = f.project_id AND feature_reviews.feature_number = f.number
                                   ORDER BY reviewed_at DESC, run_id DESC LIMIT 1) v ON true
                WHERE f.project_id = @projectId AND f.state IN ('reviewing', 'waiting_for_owner', 'ready')
                  AND f.pull_request_number IS NOT NULL AND f.branch IS NOT NULL AND f.bring_in_failed_at IS NULL
                  AND wb.last_commit IS NOT NULL AND wb.last_read_at >= @watchedSince
                  AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = f.project_id AND r.feature_number = f.number AND r.finished_at IS NULL)
                  AND EXISTS (SELECT 1 FROM cards c WHERE c.project_id = f.project_id AND c.feature_id = f.id)
                  AND NOT EXISTS (SELECT 1 FROM cards c WHERE c.project_id = f.project_id AND c.feature_id = f.id AND c.state <> 'done')
                  AND NOT EXISTS (SELECT 1 FROM merges_made m WHERE m.project_id = f.project_id AND m.branch = f.branch AND m.merged_at >= wb.last_read_at)
                  AND NOT EXISTS (SELECT 1 FROM pushes_made p WHERE p.project_id = f.project_id AND p.branch = f.branch AND p.recorded_at >= wb.last_read_at)
                ORDER BY f.number
                """,
                new { projectId, watchedSince = Watching.WatchStore.WatchedSince(time.GetUtcNow()) },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            var merges = rows.Count == 0
                ? []
                : (await transaction.Connection!.QueryAsync<(string Branch, string Commit, string Parent)>(new CommandDefinition(
                    """
                    SELECT branch, commit, parent_commit FROM merges_made
                    WHERE project_id = @projectId AND branch = ANY (@branches) AND number IS NULL AND parent_commit IS NOT NULL
                    """,
                    new { projectId, branches = rows.Select(row => row.Branch).ToArray() },
                    transaction,
                    cancellationToken: cancellationToken))).ToList();
            return (new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name), [.. rows.Select(row => new Due(
                row.Id, row.Number, row.State, row.Reason, row.ReviewCommit, row.ReviewFailedAt, row.CiPassed, row.Head,
                new DateTimeOffset(DateTime.SpecifyKind(row.PushedAt, DateTimeKind.Utc)),
                row.ReviewedCommit is { } commit && row.Verdict is { } verdict ? new ReviewOfFeature(commit, verdict) : null,
                merges.Where(merge => merge.Branch == row.Branch).ToDictionary(merge => merge.Commit, merge => merge.Parent, StringComparer.Ordinal)))]);
        }, cancellationToken);

    /// <summary>Moves the feature as <paramref name="decision"/> says, with the head CI passed on,
    /// only while it is still in the state it was read in and nothing of its own runs, and only when
    /// something changes; its <c>changed_at</c> moves only with its state or its reason.</summary>
    private async Task Move(Guid userId, Due feature, ReadyDecision decision, string? ciPassed, CancellationToken cancellationToken)
    {
        if (decision.State == feature.State && decision.Reason == feature.Reason && decision.ReviewCommit == feature.ReviewCommit
            && ciPassed == feature.CiPassed)
        {
            return;
        }

        var now = Now();
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = @state, reason = @reason, review_commit = @reviewCommit, ci_passed_commit = @ciPassed,
                                changed_at = CASE WHEN state IS DISTINCT FROM @state OR reason IS DISTINCT FROM @reason THEN @now ELSE changed_at END
            WHERE id = @id AND state = @before
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = features.project_id AND r.feature_number = features.number AND r.finished_at IS NULL)
            """,
            new { id = feature.Id, before = feature.State, state = decision.State, reason = decision.Reason, reviewCommit = decision.ReviewCommit, ciPassed, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        if (decision.State != feature.State || decision.Reason != feature.Reason)
        {
            LogMoved(log, feature.Number, decision.State, decision.Reason ?? "");
        }
    }

    /// <summary>The time, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Feature #{Feature} is {State}: {Reason}")]
    private static partial void LogMoved(ILogger logger, int feature, string state, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deciding the review of feature #{Feature} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int feature, Exception failed);

    /// <summary>A feature whose review or readiness is due, as read.</summary>
    private sealed record Due(
        Guid Id, int Number, string State, string? Reason, string? ReviewCommit, string? ReviewFailedAt, string? CiPassed, string Head, DateTimeOffset PushedAt,
        ReviewOfFeature? Latest, IReadOnlyDictionary<string, string> CleanMerges);

    private sealed record DueRow(
        Guid Id, int Number, string Branch, string State, string? Reason, string? ReviewCommit, string? ReviewFailedAt, string? CiPassed, string Head, DateTime PushedAt,
        string? ReviewedCommit, string? Verdict);

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name);
}