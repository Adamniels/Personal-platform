using Dapper;

using Npgsql;

namespace DevFlow.Placements;

/// <summary>
/// A project's slots, the place of one worker each, brought up to date with what it wants: a
/// placement of N on a machine wants worker slots 1 to N there. A slot that is not wanted is kept,
/// wanted false, until its machine reports it gone, so a card under way on it is never cut off, and
/// a slot is the same row when its number is wanted again. Every change that moves what is wanted
/// calls <see cref="Sync"/> in its own transaction: a placement, the recipe found or lost, a
/// machine removed. See "Workers" in devflow/README.md.
/// </summary>
public static class WorkerSlots
{
    /// <summary>A worker slot, one of a placement's workers.</summary>
    public const string Worker = "worker";

    /// <summary>
    /// Takes the project's row for the rest of <paramref name="transaction"/>, so two changes to
    /// what it wants follow one another, and each sees what the last committed. Every caller takes
    /// it before it touches a placement, so two never wait on each other the other way round.
    /// False when the user has no such project.
    /// </summary>
    public static async Task<bool> Lock(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return await transaction.Connection!.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM projects WHERE id = @projectId FOR NO KEY UPDATE",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)) is not null;
    }

    /// <summary>
    /// Brings the project's slots up to date with its placements, inside
    /// <paramref name="transaction"/>, which runs as the project's user and holds its row from
    /// <see cref="Lock"/> or an update of it: each wanted slot made or marked wanted, and every
    /// other marked not wanted.
    /// </summary>
    public static async Task Sync(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO worker_slots (id, project_id, machine_id, user_id, kind, number, wanted)
            SELECT uuidv7(), want.project_id, want.machine_id, want.user_id, want.kind, want.number, true
            FROM ({WantedSql}) want
            ON CONFLICT (project_id, machine_id, kind, number) DO UPDATE SET wanted = true WHERE worker_slots.wanted IS NOT TRUE
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE worker_slots s SET wanted = false
            WHERE s.project_id = @projectId AND s.wanted
              AND NOT EXISTS (SELECT 1 FROM ({WantedSql}) want WHERE want.machine_id = s.machine_id AND want.kind = s.kind AND want.number = s.number)
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>The slots project <c>@projectId</c> wants, as SQL: a row each with its
    /// <c>project_id</c>, <c>machine_id</c>, <c>user_id</c>, <c>kind</c> and <c>number</c>.</summary>
    private const string WantedSql = $"""
        SELECT pl.project_id, pl.machine_id, pl.user_id, '{Worker}' AS kind, n AS number
        FROM worker_placements pl CROSS JOIN LATERAL generate_series(1, pl.workers) n
        WHERE pl.project_id = @projectId
        """;
}