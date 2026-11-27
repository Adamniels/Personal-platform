using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>Whether a project is paused, and by what, as <see cref="Pauses.PauseSql"/> words
/// it.</summary>
public enum PauseHold
{
    /// <summary>It is not paused.</summary>
    None,

    /// <summary>The owner paused it.</summary>
    Paused,

    /// <summary>The owner paused every project of theirs, laid over the project's own pause.</summary>
    AllPaused,
}

/// <summary>Whether everything is paused, as <c>GET /devflow/api/paused</c> answers it.</summary>
/// <param name="Paused">Whether every project of the caller is paused.</param>
/// <param name="PausedAt">When it was paused, null while it is not.</param>
public sealed record AllPausedState(bool Paused, DateTimeOffset? PausedAt);

/// <summary>
/// Pausing: a project's own pause, <c>projects.paused_at</c>, and everything paused, one row of
/// <c>all_paused</c> laid over every project of the user. Either holds every kind of job of the
/// project: <see cref="RunDecision"/> reads them as its first reasons, everything paused before the
/// project's own, and the dispatcher reads them again under the project's lock before it takes
/// anything, so a pause once recorded is never followed by a job. A run under way finishes; reading
/// GitHub, opening and marking go on, since none of them reads a pause. Every read and write runs
/// through the door as the user. See "Jobs" in devflow/README.md and "Handing out work" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class Pauses
{
    /// <summary>
    /// Whether a project <c>p</c> is paused, in SQL: <c>all_paused</c> while its user paused
    /// everything, then <c>paused</c> while it is paused on its own, or null. Shared with the
    /// workers' list and the projects' summary, which put it ahead of the watch's hold.
    /// </summary>
    public const string PauseSql = """
        CASE
            WHEN EXISTS (SELECT 1 FROM all_paused a WHERE a.user_id = p.user_id) THEN 'all_paused'
            WHEN p.paused_at IS NOT NULL THEN 'paused'
        END
        """;

    /// <summary>A pause as <see cref="PauseSql"/> words it.</summary>
    public static PauseHold Read(string? word) => word switch
    {
        null => PauseHold.None,
        "paused" => PauseHold.Paused,
        "all_paused" => PauseHold.AllPaused,
        _ => throw new ArgumentOutOfRangeException(nameof(word), word, "not a pause"),
    };

    /// <summary>
    /// Whether the project is paused, read inside <paramref name="transaction"/>, which runs as its
    /// user, after taking its row <c>FOR SHARE</c>: that waits for a pause or "Stop now" writing
    /// <c>paused_at</c>, and the pause is read in a statement of its own after it, so it sees what
    /// they committed. Handing out a job takes it only after this answers none, in the same
    /// transaction.
    /// </summary>
    public static async Task<PauseHold> HeldUnderLock(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM projects WHERE id = @projectId FOR SHARE",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        return Read(await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            $"SELECT {PauseSql} FROM projects p WHERE p.id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)));
    }

    /// <summary>Pauses the project at <paramref name="now"/>, keeping the time it was paused when it
    /// already is, or continues it. False when the user has no such project.</summary>
    public static async Task<bool> SetProject(UserDb database, Guid userId, Guid projectId, bool paused, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET paused_at = CASE WHEN @paused THEN coalesce(paused_at, @now) END WHERE id = @projectId",
            new { projectId, paused, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken) > 0;
    }

    /// <summary>Pauses every project of the user at <paramref name="now"/>, keeping the time it was
    /// paused when it already is, or continues them, which leaves a project paused on its own
    /// paused.</summary>
    public static async Task SetAll(UserDb database, Guid userId, bool paused, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            paused
                ? "INSERT INTO all_paused (user_id, paused_at) VALUES (@userId, @now) ON CONFLICT (user_id) DO NOTHING"
                : "DELETE FROM all_paused",
            new { userId, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>Whether the user paused everything, and when.</summary>
    public static async Task<AllPausedState> All(UserDb database, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var at = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            "SELECT paused_at FROM all_paused",
            transaction: transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return at is { } paused ? new AllPausedState(true, Utc(paused)) : new AllPausedState(false, null);
    }

    /// <summary>
    /// "Stop now" on a project, in one transaction as the user: the project is paused first, its
    /// time kept when it already is, and then every run of it under way is marked to stop as
    /// <see cref="RunStore.RequestStop"/> marks one, a mark set before kept. Pausing first takes the
    /// project's row, so a job being handed out meanwhile either sees the pause and is not, or
    /// committed before it and is among the runs marked. The number of runs marked, or null when
    /// the user has no such project.
    /// </summary>
    public static async Task<int?> StopNow(UserDb database, Guid userId, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction<int?>(userId, async transaction =>
        {
            if (await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    "UPDATE projects SET paused_at = coalesce(paused_at, @now) WHERE id = @projectId",
                    new { projectId, now },
                    transaction,
                    cancellationToken: cancellationToken)) == 0)
            {
                return null;
            }

            return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "UPDATE runs SET stop_requested_at = coalesce(stop_requested_at, @now) WHERE project_id = @projectId AND finished_at IS NULL",
                new { projectId, now },
                transaction,
                cancellationToken: cancellationToken));
        }, cancellationToken);
    }

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));
}