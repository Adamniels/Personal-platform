using Dapper;

using DevFlow.Machines;

using Npgsql;

using PlatformData;

namespace DevFlow.Placements;

/// <summary>A project's workers placed on one of its user's machines, as the Workers card reads
/// them.</summary>
/// <param name="MachineId">The machine's id.</param>
/// <param name="Name">The machine's name, as it last said.</param>
/// <param name="Online">Whether the machine was seen less than <see cref="MachineStore.OnlineFor"/>
/// ago.</param>
/// <param name="Workers">How many of the project's workers it should run, 0 to 8.</param>
public sealed record Placement(Guid MachineId, string Name, bool Online, int Workers);

/// <summary>What placing a project's workers came to.</summary>
public abstract record PlaceOutcome
{
    private PlaceOutcome()
    {
    }

    /// <summary>Placed: the project's placements as they are now.</summary>
    public sealed record Placed(IReadOnlyList<Placement> Placements) : PlaceOutcome;

    /// <summary>The user has no such project.</summary>
    public sealed record NoProject : PlaceOutcome;

    /// <summary>The entries, by their index in the list, whose machine is not one of the user's:
    /// another user's, or none.</summary>
    public sealed record NotMine(IReadOnlyList<int> Indexes) : PlaceOutcome;
}

/// <summary>
/// Where each project's workers run, through the door as the project's user: its placements, read
/// with each machine's name and whether it is online, and replaced as a whole list, which brings
/// its slots up to date in the same transaction, see <see cref="WorkerSlots"/>. Only the user's
/// own machines can be placed on, since another user's is not there for them. See "Workers" in
/// devflow/README.md.
/// </summary>
public sealed class PlacementStore(UserDb database, TimeProvider time)
{
    /// <summary>The most workers of a project one machine runs.</summary>
    public const int MostWorkers = 8;

    /// <summary>The project's placements, by when each was placed and then the machines' order on
    /// the Machines tab; or null when the user has no such project.</summary>
    public async Task<IReadOnlyList<Placement>?> List(Guid userId, Guid projectId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, async transaction =>
            await Exists(transaction, projectId, cancellationToken) ? await Read(transaction, projectId, cancellationToken) : null, cancellationToken);

    /// <summary>
    /// Replaces the project's placements with <paramref name="placements"/>, each machine once and
    /// each count already checked: a machine left out is no longer placed, one kept keeps when it
    /// was placed unless it rises from 0, and a new one is placed now. Its slots are brought up to
    /// date in the same transaction.
    /// </summary>
    public async Task<PlaceOutcome> Place(Guid userId, Guid projectId, IReadOnlyList<(Guid Machine, int Workers)> placements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placements);
        var now = Now();
        return await database.InUserTransaction<PlaceOutcome>(userId, async transaction =>
        {
            if (!await WorkerSlots.Lock(transaction, projectId, cancellationToken))
            {
                return new PlaceOutcome.NoProject();
            }
            var machines = placements.Select(placement => placement.Machine).ToArray();
            var mine = (await transaction.Connection!.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM machines WHERE id = ANY(@machines)",
                new { machines },
                transaction,
                cancellationToken: cancellationToken))).ToHashSet();
            var notMine = machines.Select((machine, index) => (machine, index)).Where(entry => !mine.Contains(entry.machine)).Select(entry => entry.index).ToList();
            if (notMine.Count > 0)
            {
                return new PlaceOutcome.NotMine(notMine);
            }

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "DELETE FROM worker_placements WHERE project_id = @projectId AND NOT (machine_id = ANY(@machines))",
                new { projectId, machines },
                transaction,
                cancellationToken: cancellationToken));
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO worker_placements (project_id, machine_id, user_id, workers, placed_at)
                SELECT @projectId, placed.machine_id, @userId, placed.workers, @now
                FROM unnest(@machines, @workers) AS placed (machine_id, workers)
                ON CONFLICT (project_id, machine_id) DO UPDATE SET
                    workers = EXCLUDED.workers,
                    placed_at = CASE WHEN worker_placements.workers = 0 AND EXCLUDED.workers > 0 THEN EXCLUDED.placed_at ELSE worker_placements.placed_at END
                """,
                new { projectId, userId, machines, workers = placements.Select(placement => placement.Workers).ToArray(), now },
                transaction,
                cancellationToken: cancellationToken));
            await WorkerSlots.Sync(transaction, projectId, cancellationToken);
            return new PlaceOutcome.Placed(await Read(transaction, projectId, cancellationToken));
        }, cancellationToken);
    }

    private static async Task<bool> Exists(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken) =>
        await transaction.Connection!.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM projects WHERE id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)) is not null;

    private async Task<IReadOnlyList<Placement>> Read(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken) =>
        [.. await transaction.Connection!.QueryAsync<Placement>(new CommandDefinition(
            """
            SELECT m.id AS MachineId, m.name AS Name, m.last_seen_at > @since AS Online, pl.workers AS Workers
            FROM worker_placements pl JOIN machines m ON m.id = pl.machine_id
            WHERE pl.project_id = @projectId
            ORDER BY pl.placed_at, m.first_seen_at, m.id
            """,
            new { projectId, since = time.GetUtcNow() - MachineStore.OnlineFor },
            transaction,
            cancellationToken: cancellationToken))];

    /// <summary>Now, to the microsecond Postgres keeps.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }
}