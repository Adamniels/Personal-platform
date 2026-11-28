using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Schedules;

/// <summary>A project's schedules, as <c>GET .../schedules</c> answers them.</summary>
/// <param name="RunsBy">How it runs, <see cref="Schedules.RunsBy"/>'s.</param>
/// <param name="TimeZone">The user's zone, which the windows are wall clock in.</param>
/// <param name="Schedules">Its windows, in their order.</param>
/// <param name="NextStart">When a window that is on next starts, after the one it runs in ends,
/// <see cref="ScheduleState.NextStart"/>; null with none, and for a project that runs always.</param>
/// <param name="RunningUntil">When it runs by schedule and is inside a window that is on, when that
/// ends, <see cref="ScheduleState.Until"/>; null otherwise.</param>
public sealed record ProjectSchedules(string RunsBy, string TimeZone, IReadOnlyList<ScheduleEntry> Schedules, DateTimeOffset? NextStart, DateTimeOffset? RunningUntil);

/// <summary>
/// A project's schedules: its mode, <c>project_settings.runs_by</c>, and its windows,
/// <c>project_schedules</c>, read and replaced whole through the door as the user, never through the
/// settings page's Save. See "Schedules" in devflow/README.md.
/// </summary>
public static class ScheduleStore
{
    /// <summary>The project's schedules at <paramref name="now"/>, null when the user has no such
    /// project.</summary>
    public static async Task<ProjectSchedules?> Read(UserDb database, Guid userId, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => Read(transaction, projectId, now, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Replaces the project's mode and windows with <paramref name="runsBy"/> and
    /// <paramref name="windows"/>, already checked by <see cref="ScheduleRules.Check"/>, in one
    /// transaction as the user, and answers what it left; null when the user has no such project.
    /// The project's row is taken first, <c>FOR NO KEY UPDATE</c>, which a job being handed out
    /// waits for under its <c>FOR SHARE</c>, so once this answers no job is handed out by the
    /// schedules it replaced.
    /// </summary>
    public static async Task<ProjectSchedules?> Replace(UserDb database, Guid userId, Guid projectId, string runsBy, IReadOnlyList<ScheduleEntry> windows, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(windows);
        return await database.InUserTransaction(userId, async transaction =>
        {
            if (await transaction.Connection!.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                    "SELECT 1 FROM projects WHERE id = @projectId FOR NO KEY UPDATE",
                    new { projectId },
                    transaction,
                    cancellationToken: cancellationToken)) is null)
            {
                return null;
            }

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                UPDATE project_settings SET runs_by = @runsBy WHERE project_id = @projectId;
                DELETE FROM project_schedules WHERE project_id = @projectId;
                """,
                new { projectId, runsBy },
                transaction,
                cancellationToken: cancellationToken));
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project_schedules (project_id, user_id, id, name, days, from_time, to_time, active, position)
                VALUES (@projectId, @userId, @Id, @Name, @Days, @From::time, @To::time, @Active, @Position)
                """,
                windows.Select((window, position) => new
                {
                    projectId,
                    userId,
                    window.Id,
                    window.Name,
                    Days = window.Days.ToArray(),
                    window.From,
                    window.To,
                    window.Active,
                    Position = position,
                }),
                transaction,
                cancellationToken: cancellationToken));
            return await Read(transaction, projectId, now, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Where the project stands by its schedules at <paramref name="now"/>, by
    /// <see cref="Schedule.At"/>, read inside <paramref name="transaction"/>, which runs as its user;
    /// null when there is no such project. What <see cref="Jobs.RunDecision"/> reads.
    /// </summary>
    public static async Task<ScheduleState?> StateIn(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await ReadWithState(transaction, projectId, now, cancellationToken))?.State;

    /// <summary>
    /// Whether the project is outside its schedules at <paramref name="now"/>, read inside
    /// <paramref name="transaction"/> once <see cref="Jobs.Pauses.HeldUnderLock"/> has taken its row
    /// <c>FOR SHARE</c>, so a replacement of its schedules committed meanwhile is seen. Handing out a
    /// job takes it only after this answers false, in the same transaction.
    /// </summary>
    public static async Task<bool> OutsideUnderLock(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await StateIn(transaction, projectId, now, cancellationToken) is { Inside: false };

    /// <summary>The project's schedules at <paramref name="now"/>, read inside
    /// <paramref name="transaction"/>, which runs as its user; null when there is no such
    /// project.</summary>
    public static async Task<ProjectSchedules?> Read(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await ReadWithState(transaction, projectId, now, cancellationToken))?.Schedules;

    private static async Task<(ProjectSchedules Schedules, ScheduleState State)?> ReadWithState(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var runsBy = await transaction.Connection!.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT runs_by FROM project_settings WHERE project_id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        if (runsBy is null)
        {
            return null;
        }

        var rows = await transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id AS Id, name AS Name, days AS Days, to_char(from_time, 'HH24:MI') AS "From", to_char(to_time, 'HH24:MI') AS "To", active AS Active
            FROM project_schedules WHERE project_id = @projectId ORDER BY position
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        var zone = await DevFlowTime.ZoneOf(transaction, cancellationToken);
        List<ScheduleEntry> windows = [.. rows.Select(row => new ScheduleEntry(row.Id, row.Name, row.Days, row.From, row.To, row.Active))];
        var state = Schedule.At(runsBy, [.. windows.Select(window => window.Window())], zone, now);
        return (new ProjectSchedules(runsBy, zone.Id, windows, state.NextStart, state.Until), state);
    }

    private sealed class Row
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = "";

        public string[] Days { get; init; } = [];

        public string From { get; init; } = "";

        public string To { get; init; } = "";

        public bool Active { get; init; }
    }
}