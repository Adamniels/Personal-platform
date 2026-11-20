using Dapper;

using DevFlow.CodeHost;

using Npgsql;

using PlatformData;

namespace DevFlow.Watching;

/// <summary>
/// Watches one project's branches, as part of each of its reads: its default branch, read from the
/// code host, never assumed to be <c>main</c>, and then each open feature's branch, each on its own,
/// so one that cannot be read holds only what it watches. The default branch's first read records
/// where it stands; a feature's branch is watched from its creation, which Dev flow made and
/// recorded. Every change after is judged, by <see cref="IsFine"/> on the default branch and
/// <see cref="IsFineOnFeature"/> on a feature's. Anything not fine stops the project, which stays
/// stopped until its user resumes it, see <see cref="WatchStore.Resume"/>. Reads are as the app,
/// with the read only token, through <see cref="ICodeHost"/>; everything stored goes through the
/// door as the project's user. A read that fails records why on its branch's watch and changes
/// nothing else, so a watch that has not read for <see cref="UnwatchedAfter"/> holds work rather
/// than guessing: the default branch's the project's, a feature's that feature's. See "Watching
/// the default branch", with "A feature's branch" under it, in
/// devflow/docs/devflow-architecture.md, and "Watching branches" in devflow/README.md.
/// </summary>
public sealed partial class BranchWatch(UserDb database, ICodeHost host, TimeProvider time, ILogger log)
{
    /// <summary>How long ago a branch's changes may have last been read before what it watches
    /// hands out no new work.</summary>
    public static readonly TimeSpan UnwatchedAfter = TimeSpan.FromMinutes(5);

    /// <summary>Why a watch's read failed, as stored: the code host did not answer.</summary>
    public const string HostUnavailable = "GitHub did not answer";

    /// <summary>The code host does not show the repository.</summary>
    public const string RepositoryNotFound = "GitHub does not show this repository";

    /// <summary>Anything else, never the exception's own text.</summary>
    public const string ReadFailed = "Reading failed";

    /// <summary>
    /// Whether a change to the default branch is one the project may take without its user
    /// looking: never a force push; otherwise one made by <paramref name="owner"/>, the user's
    /// linked login, compared as GitHub compares logins, whatever their case; or a merge of a pull
    /// request whose merge commit is in <paramref name="mergesMade"/>, those Dev flow made into the
    /// branch itself. Anything else is not, an actor the code host does not name included.
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
    /// Whether a change to an open feature's branch is one the project may take without its user
    /// looking: what <see cref="IsFine"/> takes on the default branch, and a push that left the
    /// branch at a commit in <paramref name="pushesMade"/>, those Dev flow's own jobs pushed there
    /// and their runs' results recorded; but never the branch's deletion, by anyone, since the
    /// feature's work is on it.
    /// </summary>
    public static bool IsFineOnFeature(BranchActivity activity, string? owner, IReadOnlySet<string> mergesMade, IReadOnlySet<string> pushesMade)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(pushesMade);
        if (activity.Kind == ActivityKind.BranchDeletion)
        {
            return false;
        }

        return (activity.Kind == ActivityKind.Push && pushesMade.Contains(activity.After)) || IsFine(activity, owner, mergesMade);
    }

    /// <summary>
    /// Reads the project's default branch and then each open feature's branch, and judges what
    /// changed on each since its last read, as <paramref name="userId"/>, whose project it is.
    /// <paramref name="owner"/> is their linked login, or null without a link. Never throws for
    /// what the code host answers or a bug: either is recorded as that branch's watch's failure,
    /// and the next branch is read all the same. Only a database that cannot be reached, or
    /// cancellation, escapes.
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
            await RecordFailure(projectId, userId, null, failure, cancellationToken);
        }

        foreach (var feature in await Features(projectId, userId, cancellationToken))
        {
            try
            {
                failure = await ReadFeature(projectId, userId, repository, feature, owner, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFeatureWatchFailed(log, projectId, feature.Number, error);
                failure = ReadFailed;
            }

            if (failure is not null)
            {
                await RecordFailure(projectId, userId, feature.Id, failure, cancellationToken);
            }
        }
    }

    /// <summary>One read of the default branch, and why it failed, or null when it did not.</summary>
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
            await Judge(projectId, userId, new Watched(null, branch), listed, null, owner, null, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// One read of an open feature's branch, and why it failed, or null when it did not. A watch
    /// that has judged nothing yet lists every change the branch has, and begins at the oldest
    /// that made it at the commit Dev flow recorded making it from: nothing before it is judged,
    /// such as an earlier branch of the same name. When no such creation is listed, nothing is
    /// passed over, and every change listed is judged, the stricter reading.
    /// </summary>
    private async Task<string?> ReadFeature(Guid projectId, Guid userId, Repository repository, FeatureWatch feature, string? owner, CancellationToken cancellationToken)
    {
        IReadOnlyList<BranchActivity> listed;
        switch (await host.Activity(userId, repository, feature.Branch, feature.LastActivityId ?? 0, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<BranchActivity>>.Found { Value: var found }:
                listed = found;
                break;
            case var failed:
                return Failure(failed);
        }

        long? from = null;
        if (feature.LastActivityId is null
            && listed.LastOrDefault(activity => activity.Kind == ActivityKind.BranchCreation && activity.After == feature.BaseCommit) is { } creation)
        {
            from = creation.Id;
            listed = [.. listed.TakeWhile(activity => activity.Id != creation.Id)];
        }

        // Who the app is, needed only to tell its push while a run is under way on the branch, which
        // waits for the run's result, from anyone else's, which is judged now.
        string? app = null;
        if (listed.Any(activity => activity.Kind == ActivityKind.Push && !string.Equals(activity.Actor, owner, StringComparison.OrdinalIgnoreCase))
            && await RunUnderWay(projectId, userId, feature.Branch, cancellationToken))
        {
            switch (await host.AppCommitter(userId, repository, cancellationToken))
            {
                case CodeHostResult<Committer>.Found { Value: var committer }:
                    app = committer.Name;
                    break;
                case var failed:
                    return Failure(failed);
            }
        }

        await Judge(projectId, userId, new Watched(feature.Id, feature.Branch), listed, from, owner, app, cancellationToken);
        return null;
    }

    /// <summary>Whether a run of Dev flow's on the branch is under way, read without a lock: only
    /// whether asking who the app is may be needed.</summary>
    private async Task<bool> RunUnderWay(Guid projectId, Guid userId, string branch, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM runs WHERE project_id = @projectId AND branch = @branch AND finished_at IS NULL)",
            new { projectId, branch },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    private static string Failure<T>(CodeHostResult<T> failed) => failed switch
    {
        CodeHostResult<T>.NotFound => RepositoryNotFound,
        CodeHostResult<T>.Unavailable => HostUnavailable,
        _ => throw new InvalidOperationException("a code host result that is not one of the three"),
    };

    /// <summary>The project's open features whose branch is made, each with where its watch
    /// stands, by number: a feature that is merged or closed is no longer watched.</summary>
    private async Task<IReadOnlyList<FeatureWatch>> Features(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<FeatureWatch>(new CommandDefinition(
            """
            SELECT f.id AS Id, f.number AS Number, f.branch AS Branch, f.branch_base_commit AS BaseCommit, b.last_activity_id AS LastActivityId
            FROM features f
            LEFT JOIN branch_watches b ON b.project_id = f.project_id AND b.feature_id = f.id
            WHERE f.project_id = @projectId AND f.branch IS NOT NULL AND f.branch_base_commit IS NOT NULL
              AND f.state NOT IN ('merged', 'closed')
            ORDER BY f.number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)];

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
    /// <paramref name="listed"/>, newest first, the changes to <paramref name="watched"/> since its
    /// last read, judged oldest first under the lock Dev flow's own merges take, so a merge it has
    /// just made and recorded is always found. One not fine stops the project by
    /// <see cref="RecordStop"/>; one that cannot be recorded, since the project is stopped by
    /// another branch already, is left unjudged with every change after it, to be read again once
    /// that stop is resumed. The watch moves on to the newest judged, keeping the commit the branch
    /// was at after it, which a feature's page shows, or stays at
    /// <paramref name="from"/> or where it was when none was, read now; nothing when the project is
    /// gone. On a feature's branch, a push by <paramref name="app"/>, the app's login, while a run of
    /// Dev flow's on the branch is under way, read under the lock, is awaited rather than judged,
    /// since the worker pushes before it sends its result, see <see cref="JudgeAwaited"/>; and the
    /// project's awaited pushes whose run has ended are judged.
    /// </summary>
    private async Task Judge(Guid projectId, Guid userId, Watched watched, IReadOnlyList<BranchActivity> listed, long? from, string? owner, string? app, CancellationToken cancellationToken)
    {
        var now = Now();
        await database.InUserTransaction(userId, async transaction =>
        {
            if (!await Lock(transaction, projectId, cancellationToken))
            {
                return 0;
            }

            var made = (await transaction.Connection!.QueryAsync<string>(new CommandDefinition(
                "SELECT commit FROM merges_made WHERE project_id = @projectId AND branch = @branch AND commit = ANY (@afters)",
                new { projectId, branch = watched.Branch, afters = listed.Select(activity => activity.After).ToArray() },
                transaction,
                cancellationToken: cancellationToken))).ToHashSet(StringComparer.Ordinal);
            HashSet<string> pushed = [];
            Guid? running = null;
            if (watched.FeatureId is not null)
            {
                pushed = (await transaction.Connection!.QueryAsync<string>(new CommandDefinition(
                    "SELECT commit FROM pushes_made WHERE project_id = @projectId AND branch = @branch AND commit = ANY (@afters)",
                    new { projectId, branch = watched.Branch, afters = listed.Select(activity => activity.After).ToArray() },
                    transaction,
                    cancellationToken: cancellationToken))).ToHashSet(StringComparer.Ordinal);
                running = await transaction.Connection!.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    """
                    SELECT id FROM runs WHERE project_id = @projectId AND branch = @branch AND finished_at IS NULL
                    ORDER BY started_at DESC, id DESC LIMIT 1
                    """,
                    new { projectId, branch = watched.Branch },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            var newest = from;
            string? commit = null;
            foreach (var activity in listed.Reverse())
            {
                var fine = watched.FeatureId is null ? IsFine(activity, owner, made) : IsFineOnFeature(activity, owner, made, pushed);
                if (!fine && running is { } run && app is not null && activity.Kind == ActivityKind.Push
                    && string.Equals(activity.Actor, app, StringComparison.OrdinalIgnoreCase))
                {
                    await Await(transaction, projectId, userId, watched.Branch, run, activity, now, cancellationToken);
                    fine = true;
                }

                if (!fine && !await RecordStop(transaction, projectId, userId, watched.Branch, activity, now, cancellationToken))
                {
                    break;
                }

                newest = activity.Id;
                commit = activity.After;
            }

            if (watched.FeatureId is not null)
            {
                await JudgeAwaited(transaction, projectId, userId, now, cancellationToken);
            }

            return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO branch_watches (project_id, user_id, feature_id, branch, last_activity_id, last_commit, began_at, last_read_at, last_attempt_at, last_error)
                VALUES (@projectId, @userId, @featureId, @branch, @newest, @commit, @now, @now, @now, NULL)
                ON CONFLICT ON CONSTRAINT branch_watches_one_per_branch DO UPDATE
                SET last_activity_id = coalesce(EXCLUDED.last_activity_id, branch_watches.last_activity_id),
                    last_commit = coalesce(EXCLUDED.last_commit, branch_watches.last_commit),
                    last_read_at = EXCLUDED.last_read_at, last_attempt_at = EXCLUDED.last_attempt_at, last_error = NULL
                """,
                new { projectId, userId, featureId = watched.FeatureId, branch = watched.Branch, newest, commit, now },
                transaction,
                cancellationToken: cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// Records that <paramref name="activity"/> on <paramref name="branch"/> stops the project,
    /// inside <paramref name="transaction"/>, which holds <see cref="Lock"/>: a project not stopped
    /// is stopped by it; one stopped by an older change on the same branch shows this one from
    /// then on, the newest, since it stopped first; and one stopped by a newer change on the same
    /// branch keeps showing that one. True for each of those. False, and nothing written, when the
    /// project is stopped by another branch: one row says what stopped it, so replacing it would
    /// let the owner resume what he never saw, and the caller keeps this change to judge again
    /// once he has resumed that one.
    /// </summary>
    internal static async Task<bool> RecordStop(NpgsqlTransaction transaction, Guid projectId, Guid userId, string branch, BranchActivity activity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(activity);
        var current = await transaction.Connection!.QuerySingleOrDefaultAsync<StoppedBy>(new CommandDefinition(
            "SELECT branch AS Branch, activity_id AS ActivityId FROM project_stops WHERE project_id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        if (current is not null && current.Branch != branch)
        {
            return false;
        }

        if (current is not null && current.ActivityId >= activity.Id)
        {
            return true;
        }

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
                id = activity.Id,
                type = Word(activity.Kind),
                actor = string.IsNullOrEmpty(activity.Actor) ? null : activity.Actor,
                commit = activity.After,
                landedAt = activity.At,
            },
            transaction,
            cancellationToken: cancellationToken));
        return true;
    }

    /// <summary>A push by the app, read while <paramref name="run"/> was under way on the
    /// branch, kept to judge when the run ends; once only, by its id.</summary>
    private static Task<int> Await(NpgsqlTransaction transaction, Guid projectId, Guid userId, string branch, Guid run, BranchActivity activity, DateTimeOffset now, CancellationToken cancellationToken) =>
        transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO pushes_awaited (project_id, user_id, activity_id, branch, run_id, actor, commit, landed_at, read_at)
            VALUES (@projectId, @userId, @id, @branch, @run, @actor, @commit, @landedAt, @now)
            ON CONFLICT (project_id, activity_id) DO NOTHING
            """,
            new { projectId, userId, id = activity.Id, branch, run, actor = activity.Actor, commit = activity.After, landedAt = activity.At, now },
            transaction,
            cancellationToken: cancellationToken));

    /// <summary>
    /// Judges the project's awaited pushes whose run has ended, inside
    /// <paramref name="transaction"/>, after taking <see cref="Lock"/>: one whose commit a result
    /// recorded in <c>pushes_made</c> on its branch is fine, and any other stops the project, as a
    /// push by the app that Dev flow has no record of, by <see cref="RecordStop"/>; each judged is
    /// removed, and one that cannot be recorded, since the project is stopped by another branch, is
    /// kept for later. Called where a run ends, in the transaction that records its result or finds
    /// it lost, and on each read of a feature's branch. False when the project is gone.
    /// </summary>
    internal static async Task<bool> JudgeAwaited(NpgsqlTransaction transaction, Guid projectId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!await Lock(transaction, projectId, cancellationToken))
        {
            return false;
        }

        var ended = await transaction.Connection!.QueryAsync<AwaitedPush>(new CommandDefinition(
            """
            SELECT a.activity_id AS ActivityId, a.branch AS Branch, a.actor AS Actor, a.commit AS Commit, a.landed_at AS LandedAt,
                   EXISTS (SELECT 1 FROM pushes_made p WHERE p.project_id = a.project_id AND p.branch = a.branch AND p.commit = a.commit) AS Recorded
            FROM pushes_awaited a
            WHERE a.project_id = @projectId
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.id = a.run_id AND r.finished_at IS NULL)
            ORDER BY a.activity_id
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        foreach (var push in ended)
        {
            var activity = new BranchActivity(push.ActivityId, push.Commit, push.Commit, new DateTimeOffset(DateTime.SpecifyKind(push.LandedAt, DateTimeKind.Utc)), ActivityKind.Push, push.Actor);
            if (push.Recorded || await RecordStop(transaction, projectId, userId, push.Branch, activity, now, cancellationToken))
            {
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM pushes_awaited WHERE project_id = @projectId AND activity_id = @id",
                    new { projectId, id = push.ActivityId },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }

        return true;
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

    /// <summary>The failure recorded on the watch of the default branch, or of the feature's branch
    /// with <paramref name="featureId"/>, its last read left as it was, so it goes on aging;
    /// nothing before the watch has begun, since it holds work already.</summary>
    private Task<int> RecordFailure(Guid projectId, Guid userId, Guid? featureId, string failure, CancellationToken cancellationToken)
    {
        var now = Now();
        return database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE branch_watches SET last_attempt_at = @now, last_error = @failure
            WHERE project_id = @projectId AND feature_id IS NOT DISTINCT FROM @featureId
            """,
            new { projectId, featureId, failure, now },
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watching the branch of feature #{Feature} of project {Project} failed; its watch stays as it was")]
    private static partial void LogFeatureWatchFailed(ILogger logger, Guid project, int feature, Exception failed);

    private sealed record WatchRow(string Branch, long? LastActivityId);

    /// <summary>A branch watched: the default branch, with no feature, or a feature's.</summary>
    private sealed record Watched(Guid? FeatureId, string Branch);

    /// <summary>An open feature whose branch is made, and where its watch stands.</summary>
    private sealed record FeatureWatch(Guid Id, int Number, string Branch, string BaseCommit, long? LastActivityId);

    /// <summary>What a stopped project is stopped by.</summary>
    private sealed record StoppedBy(string Branch, long ActivityId);

    /// <summary>An awaited push whose run has ended, and whether a result recorded its commit.</summary>
    private sealed record AwaitedPush(long ActivityId, string Branch, string Actor, string Commit, DateTime LandedAt, bool Recorded);
}