using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Watching;

/// <summary>Why a project hands out no new work, if it does not.</summary>
public enum ProjectHold
{
    /// <summary>It hands out work.</summary>
    None,

    /// <summary>Something landed on its default branch that Dev flow did not put there, and its user
    /// has not resumed it.</summary>
    Stopped,

    /// <summary>Its default branch's changes have not been read for
    /// <see cref="BranchWatch.UnwatchedAfter"/>, or ever.</summary>
    Unwatched,
}

/// <summary>What resuming a project answers.</summary>
public enum ResumeOutcome
{
    /// <summary>Resumed, and recorded.</summary>
    Resumed,

    /// <summary>The user has no such project.</summary>
    NoSuchProject,

    /// <summary>It is not stopped.</summary>
    NotStopped,

    /// <summary>It is stopped by another commit than the one named, which landed since the page
    /// was shown.</summary>
    Changed,
}

/// <summary>A stopped project, as its user's waiting list reads it.</summary>
/// <param name="Owner">Its repository's owner, as connected.</param>
/// <param name="Name">Its repository's name, as connected.</param>
/// <param name="Branch">The branch something landed on.</param>
/// <param name="ActivityType">What it was, as the tables store it.</param>
/// <param name="Actor">Who did it, data, or null when the code host names nobody.</param>
/// <param name="Commit">The commit the branch was at after it.</param>
/// <param name="LandedAt">When it landed.</param>
/// <param name="StoppedAt">When the project stopped.</param>
public sealed record ProjectStop(Guid ProjectId, string Owner, string Name, string Branch, string ActivityType, string? Actor, string Commit, DateTimeOffset LandedAt, DateTimeOffset StoppedAt);

/// <summary>
/// A project's watch as the rest of Dev flow reads it: whether it hands out work, its user's stopped
/// projects, and resuming one. Every query goes through the door as the user, so only a project's
/// own user sees it or resumes it. See "Watching branches" in devflow/README.md.
/// </summary>
public sealed class WatchStore(UserDb database, TimeProvider time)
{
    /// <summary>Why the user's project hands out no new work, stopped first; none when it does, or
    /// when the user has no such project, which has no work either.</summary>
    public async Task<ProjectHold> Hold(Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        var word = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            $"SELECT {HoldSql} FROM projects p WHERE p.id = @projectId",
            new { projectId, watchedSince = WatchedSince() },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return Read(word);
    }

    /// <summary>
    /// Why the project holds new work, read inside <paramref name="transaction"/>, which runs as
    /// its user, after taking its row <c>FOR SHARE</c>: that waits for a watch or a resume writing a
    /// stop under <see cref="BranchWatch.Lock"/>, and the hold is read in a statement of its own
    /// after it, so it sees what they committed. Handing out a job takes a card only after this
    /// answers none, in the same transaction, so a stop once recorded is never followed by a job.
    /// </summary>
    public async Task<ProjectHold> HoldUnderLock(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (await transaction.Connection!.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM projects WHERE id = @projectId FOR SHARE",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken)) is null)
        {
            // No such project has no work, and nothing to hold.
            return ProjectHold.None;
        }

        return Read(await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            $"SELECT {HoldSql} FROM projects p WHERE p.id = @projectId",
            new { projectId, watchedSince = WatchedSince() },
            transaction,
            cancellationToken: cancellationToken)));
    }

    /// <summary>
    /// Why a project <c>p</c> holds new work, in SQL, with <c>@watchedSince</c> the oldest last read
    /// that still counts: <c>stopped</c>, then <c>unwatched</c>, or null. Shared with the workers'
    /// list, which says it on the project's page.
    /// </summary>
    public const string HoldSql = """
        CASE
            WHEN EXISTS (SELECT 1 FROM project_stops s WHERE s.project_id = p.id) THEN 'stopped'
            WHEN NOT EXISTS (SELECT 1 FROM branch_watches b WHERE b.project_id = p.id AND b.feature_id IS NULL AND b.last_read_at >= @watchedSince) THEN 'unwatched'
        END
        """;

    /// <summary>
    /// Whether a card <c>cards</c> is free of its feature's watch, in SQL, with
    /// <c>@watchedSince</c> the oldest last read that still counts: a card on its own, a card of a
    /// feature whose branch is not made yet, or one whose feature's branch was read since then. A
    /// feature's branch not read lately holds that feature's work alone, never the project's,
    /// which only the default branch's watch holds, by <see cref="HoldSql"/>. See "A feature's
    /// branch" under "Watching the default branch" in devflow/docs/devflow-architecture.md.
    /// </summary>
    public const string FeatureWatchedSql = """
        (cards.feature_id IS NULL
         OR NOT EXISTS (SELECT 1 FROM features wf WHERE wf.id = cards.feature_id AND wf.branch_base_commit IS NOT NULL)
         OR EXISTS (SELECT 1 FROM branch_watches wb
                    WHERE wb.project_id = cards.project_id AND wb.feature_id = cards.feature_id AND wb.last_read_at >= @watchedSince))
        """;

    /// <summary>
    /// Whether <c>@into</c>, a branch of project <c>@projectId</c>, is an open feature's branch that
    /// has not been read since <c>@watchedSince</c>, in SQL: nothing is merged into it then. The
    /// default branch, or a branch that is no open feature's, is not held by it.
    /// </summary>
    public const string FeatureUnwatchedSql = """
        EXISTS (SELECT 1 FROM features f
                WHERE f.project_id = @projectId AND f.branch = @into AND f.branch_base_commit IS NOT NULL
                  AND f.state NOT IN ('merged', 'closed')
                  AND NOT EXISTS (SELECT 1 FROM branch_watches b
                                  WHERE b.project_id = f.project_id AND b.feature_id = f.id AND b.last_read_at >= @watchedSince))
        """;

    /// <summary>The oldest last read of a branch's changes that still lets its project hand out
    /// work.</summary>
    public DateTimeOffset WatchedSince() => WatchedSince(time.GetUtcNow());

    /// <summary>The oldest last read of a branch's changes that still counts at
    /// <paramref name="now"/>.</summary>
    public static DateTimeOffset WatchedSince(DateTimeOffset now) => now - BranchWatch.UnwatchedAfter;

    /// <summary>A hold as <see cref="HoldSql"/> words it.</summary>
    public static ProjectHold Read(string? word) => word switch
    {
        null => ProjectHold.None,
        "stopped" => ProjectHold.Stopped,
        "unwatched" => ProjectHold.Unwatched,
        _ => throw new ArgumentOutOfRangeException(nameof(word), word, "not a hold"),
    };

    /// <summary>The user's stopped projects, by owner and name.</summary>
    public async Task<IReadOnlyList<ProjectStop>> Stops(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<StopRow>(new CommandDefinition(
            """
            SELECT s.project_id AS ProjectId, p.owner AS Owner, p.name AS Name, s.branch AS Branch, s.activity_type AS ActivityType,
                   s.actor AS Actor, s.commit AS Commit, s.landed_at AS LandedAt, s.stopped_at AS StoppedAt
            FROM project_stops s JOIN projects p ON p.id = s.project_id
            ORDER BY lower(p.owner), lower(p.name), p.id
            """,
            transaction: transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(row => new ProjectStop(row.ProjectId, row.Owner, row.Name, row.Branch, row.ActivityType, row.Actor, row.Commit,
            Utc(row.LandedAt), Utc(row.StoppedAt)))];
    }

    /// <summary>
    /// Resumes the user's stopped project, accepting <paramref name="commit"/>, which must be the
    /// commit it is stopped by now, the one the page showed: the stop is kept as a resume, with who
    /// resumed it, when and the commit accepted, and removed, in one transaction under the lock the
    /// watch takes, so a change judged at the same moment is never lost between the two.
    /// </summary>
    public async Task<ResumeOutcome> Resume(Guid userId, Guid projectId, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % 10));
        return await database.InUserTransaction(userId, async transaction =>
        {
            if (!await BranchWatch.Lock(transaction, projectId, cancellationToken))
            {
                return ResumeOutcome.NoSuchProject;
            }

            var stopped = await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT commit FROM project_stops WHERE project_id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            if (stopped is null)
            {
                return ResumeOutcome.NotStopped;
            }

            if (!string.Equals(stopped, commit, StringComparison.Ordinal))
            {
                return ResumeOutcome.Changed;
            }

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project_resumes (id, project_id, user_id, resumed_by, resumed_at, accepted_commit, branch, activity_type, actor, stopped_at, landed_at)
                SELECT @id, project_id, user_id, @userId, @now, commit, branch, activity_type, actor, stopped_at, landed_at
                FROM project_stops WHERE project_id = @projectId;
                DELETE FROM project_stops WHERE project_id = @projectId;
                """,
                new { id = Guid.CreateVersion7(now), projectId, userId, now },
                transaction,
                cancellationToken: cancellationToken));
            return ResumeOutcome.Resumed;
        }, cancellationToken);
    }

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private sealed record StopRow(Guid ProjectId, string Owner, string Name, string Branch, string ActivityType, string? Actor, string Commit, DateTime LandedAt, DateTime StoppedAt);
}