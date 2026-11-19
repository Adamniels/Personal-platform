using Dapper;

using DevFlow.CodeHost;

using Npgsql;

using PlatformData;

namespace DevFlow.Watching;

/// <summary>
/// Watches one project's default branch, as part of each of its reads: the branch is read from the
/// code host, never assumed to be <c>main</c>; the first read records where it stands, and every
/// change after that is judged by <see cref="IsFine"/>. Anything not fine stops the project, which
/// stays stopped until its user resumes it, see <see cref="WatchStore.Resume"/>. Reads are as the
/// app, with the read only token, through <see cref="ICodeHost"/>; everything stored goes through
/// the door as the project's user. A read that fails records why and changes nothing else, so a
/// watch that has not read for <see cref="UnwatchedAfter"/> holds new work rather than guessing. See
/// "Watching the default branch" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class BranchWatch(UserDb database, ICodeHost host, TimeProvider time, ILogger log)
{
    /// <summary>How long ago the branch's changes may have last been read before the project hands
    /// out no new work.</summary>
    public static readonly TimeSpan UnwatchedAfter = TimeSpan.FromMinutes(5);

    /// <summary>Why a watch's read failed, as stored: the code host did not answer.</summary>
    public const string HostUnavailable = "GitHub did not answer";

    /// <summary>The code host does not show the repository.</summary>
    public const string RepositoryNotFound = "GitHub does not show this repository";

    /// <summary>Anything else, never the exception's own text.</summary>
    public const string ReadFailed = "Reading failed";

    /// <summary>
    /// Whether a change is one the project may take without its user looking: never a force push;
    /// otherwise one made by <paramref name="owner"/>, the user's linked login, compared as GitHub
    /// compares logins, whatever their case; or a merge of a pull request whose merge commit is in
    /// <paramref name="mergesMade"/>, those Dev flow made itself. Anything else is not, an actor
    /// the code host does not name included.
    /// </summary>
    public static bool IsFine(BranchActivity activity, string? owner, IReadOnlySet<string> mergesMade)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(mergesMade);
        if (activity.Kind == ActivityKind.ForcePush)
        {
            return false;
        }

        if (owner is not null && string.Equals(activity.Actor, owner, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return activity.Kind == ActivityKind.PullRequestMerge && mergesMade.Contains(activity.After);
    }

    /// <summary>
    /// Reads the project's default branch and judges what changed since the last read, as
    /// <paramref name="userId"/>, whose project it is. <paramref name="owner"/> is their linked
    /// login, or null without a link. Never throws for what the code host answers or a bug: either
    /// is recorded as the watch's failure. Only a database that cannot be reached, or cancellation,
    /// escapes.
    /// </summary>
    public async Task Watch(Guid projectId, Guid userId, Repository repository, string? owner, CancellationToken cancellationToken)
    {
        string? failure;
        try
        {
            failure = await Read(projectId, userId, repository, owner, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogWatchFailed(log, projectId, error);
            failure = ReadFailed;
        }

        if (failure is not null)
        {
            await RecordFailure(projectId, userId, failure, cancellationToken);
        }
    }

    /// <summary>One read, and why it failed, or null when it did not.</summary>
    private async Task<string?> Read(Guid projectId, Guid userId, Repository repository, string? owner, CancellationToken cancellationToken)
    {
        string branch;
        switch (await host.DefaultBranch(userId, repository, cancellationToken))
        {
            case CodeHostResult<string>.Found { Value: var found }:
                branch = found;
                break;
            case var failed:
                return Failure(failed);
        }

        var current = await Current(projectId, userId, cancellationToken);

        // A watch that has not begun, or began on a branch that is no longer the default, begins at
        // the branch's newest change and judges nothing before it.
        var since = current is not null && current.Branch == branch ? current.LastActivityId ?? 0 : (long?)null;
        IReadOnlyList<BranchActivity> listed;
        switch (await host.Activity(userId, repository, branch, since, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<BranchActivity>>.Found { Value: var found }:
                listed = found;
                break;
            case var failed:
                return Failure(failed);
        }

        if (since is null)
        {
            await Begin(projectId, userId, branch, listed.Count > 0 ? listed[0].Id : null, cancellationToken);
        }
        else
        {
            await Judge(projectId, userId, branch, listed, owner, cancellationToken);
        }

        return null;
    }

    private static string Failure<T>(CodeHostResult<T> failed) => failed switch
    {
        CodeHostResult<T>.NotFound => RepositoryNotFound,
        CodeHostResult<T>.Unavailable => HostUnavailable,
        _ => throw new InvalidOperationException("a code host result that is not one of the three"),
    };

    /// <summary>The project's watch, or null before it has begun.</summary>
    private async Task<WatchRow?> Current(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<WatchRow>(new CommandDefinition(
            "SELECT branch AS Branch, last_activity_id AS LastActivityId FROM branch_watches WHERE project_id = @projectId AND feature_id IS NULL",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    /// <summary>The watch begun on <paramref name="branch"/> at <paramref name="newest"/>, read now;
    /// nothing when the project is gone. A stop already recorded is kept.</summary>
    private async Task Begin(Guid projectId, Guid userId, string branch, long? newest, CancellationToken cancellationToken)
    {
        var now = Now();
        await database.InUserTransaction(userId, async transaction =>
        {
            if (!await Lock(transaction, projectId, cancellationToken))
            {
                return 0;
            }

            return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO branch_watches (project_id, user_id, branch, last_activity_id, began_at, last_read_at, last_attempt_at, last_error)
                VALUES (@projectId, @userId, @branch, @newest, @now, @now, @now, NULL)
                ON CONFLICT ON CONSTRAINT branch_watches_one_per_branch DO UPDATE
                SET branch = EXCLUDED.branch, last_activity_id = EXCLUDED.last_activity_id, began_at = EXCLUDED.began_at,
                    last_read_at = EXCLUDED.last_read_at, last_attempt_at = EXCLUDED.last_attempt_at, last_error = NULL
                """,
                new { projectId, userId, branch, newest, now },
                transaction,
                cancellationToken: cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// <paramref name="listed"/>, newest first, judged oldest first under the lock Dev flow's own
    /// merges take, so a merge it has just made and recorded is always found: the newest that is
    /// not fine stops the project, or is what a stopped project shows from then on; and the watch
    /// moves on to the newest listed, read now.
    /// </summary>
    private async Task Judge(Guid projectId, Guid userId, string branch, IReadOnlyList<BranchActivity> listed, string? owner, CancellationToken cancellationToken)
    {
        var now = Now();
        await database.InUserTransaction(userId, async transaction =>
        {
            if (!await Lock(transaction, projectId, cancellationToken))
            {
                return 0;
            }

            var made = (await transaction.Connection!.QueryAsync<string>(new CommandDefinition(
                "SELECT commit FROM merges_made WHERE project_id = @projectId AND commit = ANY (@afters)",
                new { projectId, afters = listed.Select(activity => activity.After).ToArray() },
                transaction,
                cancellationToken: cancellationToken))).ToHashSet(StringComparer.Ordinal);
            if (listed.FirstOrDefault(activity => !IsFine(activity, owner, made)) is { } stops)
            {
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO project_stops (project_id, user_id, stopped_at, branch, activity_id, activity_type, actor, commit, landed_at)
                    VALUES (@projectId, @userId, @now, @branch, @id, @type, @actor, @commit, @landedAt)
                    ON CONFLICT (project_id) DO UPDATE
                    SET branch = EXCLUDED.branch, activity_id = EXCLUDED.activity_id, activity_type = EXCLUDED.activity_type,
                        actor = EXCLUDED.actor, commit = EXCLUDED.commit, landed_at = EXCLUDED.landed_at
                    """,
                    new
                    {
                        projectId,
                        userId,
                        now,
                        branch,
                        id = stops.Id,
                        type = Word(stops.Kind),
                        actor = string.IsNullOrEmpty(stops.Actor) ? null : stops.Actor,
                        commit = stops.After,
                        landedAt = stops.At,
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                UPDATE branch_watches
                SET last_activity_id = coalesce(@newest, last_activity_id), last_read_at = @now, last_attempt_at = @now, last_error = NULL
                WHERE project_id = @projectId AND feature_id IS NULL
                """,
                new { projectId, newest = listed.Count > 0 ? listed[0].Id : (long?)null, now },
                transaction,
                cancellationToken: cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// Locks the project's row against Dev flow's own merges, which take the same lock, see
    /// <see cref="Merging.AppMerges"/>; false when the project is gone.
    /// </summary>
    internal static async Task<bool> Lock(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken) =>
        await transaction.Connection!.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT 1 FROM projects WHERE id = @projectId FOR NO KEY UPDATE",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)) is not null;

    /// <summary>The failure recorded on the watch, its last read left as it was, so it goes on
    /// aging; nothing before the watch has begun, since it holds work already.</summary>
    private Task<int> RecordFailure(Guid projectId, Guid userId, string failure, CancellationToken cancellationToken)
    {
        var now = Now();
        return database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE branch_watches SET last_attempt_at = @now, last_error = @failure WHERE project_id = @projectId AND feature_id IS NULL",
            new { projectId, failure, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>A kind as the tables store it.</summary>
    public static string Word(ActivityKind kind) => kind switch
    {
        ActivityKind.Push => "push",
        ActivityKind.ForcePush => "force_push",
        ActivityKind.BranchCreation => "branch_creation",
        ActivityKind.BranchDeletion => "branch_deletion",
        ActivityKind.PullRequestMerge => "pull_request_merge",
        ActivityKind.MergeQueueMerge => "merge_queue_merge",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a kind of change"),
    };

    /// <summary>The time, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watching the default branch of project {Project} failed; its watch stays as it was")]
    private static partial void LogWatchFailed(ILogger logger, Guid project, Exception failed);

    private sealed record WatchRow(string Branch, long? LastActivityId);
}