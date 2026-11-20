using Dapper;

using DevFlow.CodeHost;
using DevFlow.Watching;

using PlatformData;

namespace DevFlow.Features;

/// <summary>
/// The end of each feature whose cards are all done, as the app, on the project's minute: first
/// <see cref="FeatureEnd.Advance"/>, so an end due since the last read starts now; then, for each
/// feature <c>bringing_in</c>, the default branch brought into its branch with a merge commit, as
/// the app with a <see cref="WritePurpose.Merge"/> key, never a rebase and never a force push.
/// Bringing in is asked only while the project is not stopped, its default branch's watch and the
/// feature's branch's are current, and the feature still waits for it, all read under the
/// project's lock, which is held while the code host answers: the merge commit it made is written
/// to <c>merges_made</c> with the feature's branch before the lock is let go, so the watch, which
/// takes the same lock before it judges, never sees the merge unrecorded. Should the record fail
/// after the code host merged, or the host not say the commit, the watch finds an unrecorded push by
/// the app and stops the project, erring towards asking the owner, as <see cref="Merging.AppMerges"/>
/// does. A conflict, or a refusal, leaves the feature waiting for the owner. It writes, so it lives
/// outside <c>Reading/</c>, which can never reach a write key. See "The end of a feature" in
/// devflow/README.md and devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class FeatureEnds(UserDb database, ICodeHost host, TimeProvider time, ILogger<FeatureEnds> log)
{
    /// <summary>What the merge commit that brings the default branch in says.</summary>
    public static string Message(string defaultBranch, string branch) => $"Bring {defaultBranch} into {branch}";

    /// <summary>
    /// Ends every feature of the project that is due, as the project's user,
    /// <paramref name="userId"/>. Never throws for what the code host answers or for one feature's
    /// failure, which is logged and tried again on the next call; only cancellation and a database
    /// that cannot be reached escape.
    /// </summary>
    public async Task EndPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var (repository, pending) = await Pending(projectId, userId, cancellationToken);
        if (repository is null)
        {
            return;
        }

        foreach (var feature in pending)
        {
            try
            {
                if (await BringIn(projectId, userId, repository, feature, cancellationToken))
                {
                    await Finish(userId, feature, cancellationToken);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, feature.Number, error);
            }
        }
    }

    /// <summary>
    /// Brings the default branch into the feature's branch, under the project's lock, and answers
    /// whether the feature holds it now: brought in, or nothing to bring in. Already brought in at
    /// the default branch's commit, as a minute whose next step failed left it, nothing is asked.
    /// </summary>
    private async Task<bool> BringIn(Guid projectId, Guid userId, Repository repository, Due feature, CancellationToken cancellationToken)
    {
        if (feature.BroughtIn is not null && feature.BroughtIn == feature.DefaultHead)
        {
            return true;
        }

        return await database.InUserTransaction(userId, async transaction =>
        {
            if (!await BranchWatch.Lock(transaction, projectId, cancellationToken))
            {
                return false;
            }

            var now = Now();
            var hold = WatchStore.Read(await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                $"SELECT {WatchStore.HoldSql} FROM projects p WHERE p.id = @projectId",
                new { projectId, watchedSince = WatchStore.WatchedSince(now) },
                transaction,
                cancellationToken: cancellationToken)));
            if (hold != ProjectHold.None)
            {
                LogHeld(log, projectId, feature.Number, hold);
                return false;
            }

            // Read again under the lock: the feature still waits for this, and where the default
            // branch stands now, as its watch named it.
            var current = await transaction.Connection!.QuerySingleOrDefaultAsync<Current>(new CommandDefinition(
                """
                SELECT f.branch AS Branch, b.branch AS DefaultBranch, b.last_commit AS DefaultHead
                FROM features f JOIN branch_watches b ON b.project_id = f.project_id AND b.feature_id IS NULL
                WHERE f.id = @id AND f.state = 'bringing_in' AND f.branch IS NOT NULL
                FOR UPDATE OF f
                """,
                new { id = feature.Id },
                transaction,
                cancellationToken: cancellationToken));
            if (current is null)
            {
                return false;
            }

            if (await transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
                    $"SELECT {WatchStore.FeatureUnwatchedSql}",
                    new { projectId, into = current.Branch, watchedSince = WatchStore.WatchedSince(now) },
                    transaction,
                    cancellationToken: cancellationToken)))
            {
                LogHeld(log, projectId, feature.Number, ProjectHold.Unwatched);
                return false;
            }

            if (await host.ForWrite(userId, repository, WritePurpose.Merge, cancellationToken) is not CodeHostResult<AppWriteToken>.Found { Value: var key })
            {
                LogNotNow(log, projectId, "a key to merge with");
                return false;
            }

            var result = await host.BringIn(userId, key, repository, current.Branch, current.DefaultBranch, Message(current.DefaultBranch, current.Branch), cancellationToken);
            switch (result)
            {
                case BringInResult.Merged { Commit: var commit, BroughtIn: var brought }:
                    if (commit is not null)
                    {
                        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                            """
                            INSERT INTO merges_made (project_id, user_id, branch, commit, number, merged_at)
                            VALUES (@projectId, @userId, @branch, @commit, NULL, @now)
                            ON CONFLICT (project_id, commit) DO NOTHING
                            """,
                            new { projectId, userId, branch = current.Branch, commit, now },
                            transaction,
                            cancellationToken: cancellationToken));
                    }

                    // The commit it brought in, as the answer named it, or the default branch's as
                    // its watch named it, which the merge holds.
                    await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                        "UPDATE features SET brought_in_commit = coalesce(@brought, @head, brought_in_commit), bring_in_failed_at = NULL WHERE id = @id",
                        new { id = feature.Id, brought, head = current.DefaultHead },
                        transaction,
                        cancellationToken: cancellationToken));
                    LogBrought(log, projectId, feature.Number, current.DefaultBranch);
                    return true;
                case BringInResult.NothingToBringIn:
                    await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                        "UPDATE features SET brought_in_commit = coalesce(@head, brought_in_commit), bring_in_failed_at = NULL WHERE id = @id",
                        new { id = feature.Id, head = current.DefaultHead },
                        transaction,
                        cancellationToken: cancellationToken));
                    return true;
                case BringInResult.Conflict:
                    await Waits(transaction, feature.Id, current.DefaultHead, now, FeatureEnd.Conflict(current.DefaultBranch), cancellationToken);
                    LogConflict(log, projectId, feature.Number, current.DefaultBranch);
                    return false;
                case BringInResult.Refused { Status: var status }:
                    await Waits(transaction, feature.Id, current.DefaultHead, now, FeatureEnd.Refused(current.DefaultBranch, status), cancellationToken);
                    LogRefused(log, projectId, feature.Number, status);
                    return false;
                case BringInResult.Unavailable:
                    // Whether it was made is not known: tried again on the next minute, which
                    // answers nothing to bring in if it was, and the watch judges what it finds.
                    LogNotNow(log, projectId, "bringing the default branch in");
                    return false;
                default:
                    throw new InvalidOperationException("a bring in result that is not one of the five");
            }
        }, cancellationToken);
    }

    /// <summary>The feature waits for the owner with <paramref name="reason"/>, and bringing in is
    /// not tried again until the default branch moves past the commit it failed at.</summary>
    private static Task<int> Waits(Npgsql.NpgsqlTransaction transaction, Guid id, string? head, DateTimeOffset now, string reason, CancellationToken cancellationToken) =>
        transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = 'waiting_for_owner', reason = @reason, bring_in_failed_at = @head, changed_at = @now
            WHERE id = @id AND state = 'bringing_in'
            """,
            new { id, head, now, reason },
            transaction,
            cancellationToken: cancellationToken));

    /// <summary>The feature, brought in, waits for the owner, while it still waits for its end.</summary>
    private async Task Finish(Guid userId, Due feature, CancellationToken cancellationToken)
    {
        var now = Now();
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE features SET state = 'waiting_for_owner', reason = @reason, changed_at = @now WHERE id = @id AND state = 'bringing_in'",
            new { id = feature.Id, reason = FeatureEnd.NotReviewed, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>
    /// The project's repository and its features whose end is due, after moving each as
    /// <see cref="FeatureEnd.Advance"/> says, in one transaction as the user. No repository for a
    /// project the user does not have; nothing is due before the default branch's watch has named
    /// it.
    /// </summary>
    private async Task<(Repository? Repository, IReadOnlyList<Due> Pending)> Pending(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
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

            await FeatureEnd.Advance(transaction, projectId, Now(), cancellationToken);
            var pending = (await transaction.Connection!.QueryAsync<Due>(new CommandDefinition(
                """
                SELECT f.id AS Id, f.number AS Number, f.brought_in_commit AS BroughtIn, b.last_commit AS DefaultHead
                FROM features f JOIN branch_watches b ON b.project_id = f.project_id AND b.feature_id IS NULL
                WHERE f.project_id = @projectId AND f.state = 'bringing_in' AND f.branch IS NOT NULL
                ORDER BY f.number
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            return (new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name), pending);
        }, cancellationToken);

    /// <summary>The time, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Brought {Default} into the branch of feature #{Feature} of project {Project}")]
    private static partial void LogBrought(ILogger logger, Guid project, int feature, string @default);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bringing the default branch into feature #{Feature} of project {Project} waits: {Hold}")]
    private static partial void LogHeld(ILogger logger, Guid project, int feature, ProjectHold hold);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Default} conflicts with the branch of feature #{Feature} of project {Project}; it waits for its owner")]
    private static partial void LogConflict(ILogger logger, Guid project, int feature, string @default);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host refused to bring the default branch into feature #{Feature} of project {Project}, with {Status}; it waits for its owner")]
    private static partial void LogRefused(ILogger logger, Guid project, int feature, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host did not answer {What} for project {Project}; its features' ends are tried again on a later minute")]
    private static partial void LogNotNow(ILogger logger, Guid project, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ending feature #{Feature} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int feature, Exception failed);

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name);

    /// <summary>A feature whose end is due, as read before its lock.</summary>
    private sealed record Due(Guid Id, int Number, string? BroughtIn, string? DefaultHead);

    /// <summary>A feature bringing in, read under the lock, with the default branch.</summary>
    private sealed record Current(string Branch, string DefaultBranch, string? DefaultHead);
}