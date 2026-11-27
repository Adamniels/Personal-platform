using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Schedules;

/// <summary>A project's schedules, as <c>GET .../schedules</c> answers them.</summary>
/// <param name="RunsBy">How it runs, <see cref="Schedules.RunsBy"/>'s.</param>
/// <param name="TimeZone">The user's zone, which the windows are wall clock in.</param>
/// <param name="Schedules">Its windows, in their order.</param>
public sealed record ProjectSchedules(string RunsBy, string TimeZone, IReadOnlyList<ScheduleEntry> Schedules);

/// <summary>
/// A project's schedules: its mode, <c>project_settings.runs_by</c>, and its windows,
/// <c>project_schedules</c>, read and replaced whole through the door as the user, never through the
/// settings page's Save. See "Schedules" in devflow/README.md.
/// </summary>
public static class ScheduleStore
{
    /// <summary>The project's schedules, null when the user has no such project.</summary>
    public static async Task<ProjectSchedules?> Read(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => Read(transaction, projectId, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Replaces the project's mode and windows with <paramref name="runsBy"/> and
    /// <paramref name="windows"/>, already checked by <see cref="ScheduleRules.Check"/>, in one
    /// transaction as the user, and answers what it left; null when the user has no such project.
    /// The project's row is taken first, <c>FOR NO KEY UPDATE</c>, which a job being handed out
    /// waits for under its <c>FOR SHARE</c>, so once this answers no job is handed out by the
    /// schedules it replaced.
    /// </summary>
    public static async Task<ProjectSchedules?> Replace(UserDb database, Guid userId, Guid projectId, string runsBy, IReadOnlyList<ScheduleEntry> windows, CancellationToken cancellationToken)
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
            return await Read(transaction, projectId, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>The project's schedules, read inside <paramref name="transaction"/>, which runs as
    /// its user; null when there is no such project.</summary>
    public static async Task<ProjectSchedules?> Read(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
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
        return new ProjectSchedules(runsBy, zone.Id, [.. rows.Select(row => new ScheduleEntry(row.Id, row.Name, row.Days, row.From, row.To, row.Active))]);
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