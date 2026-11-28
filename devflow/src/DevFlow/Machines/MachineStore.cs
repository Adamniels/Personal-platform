using Dapper;

using PlatformData;

namespace DevFlow.Machines;

/// <summary>What a machine reports when it says hello, each already checked by the route.</summary>
public sealed record MachineReport(string Name, string Os, string Arch, string ProgramVersion, string Docker);

/// <summary>
/// Each user's machines: a machine saying hello as its user, through the door, from the
/// <see cref="MachineCaller"/> its key led to, so a machine can only ever change itself. See
/// "Machines" in devflow/README.md.
/// </summary>
public sealed class MachineStore(UserDb database, TimeProvider time)
{
    /// <summary>
    /// Records what the machine reports and now as when it was last seen. False when the machine
    /// is gone, which a key that was just found makes rare.
    /// </summary>
    public async Task<bool> Hello(MachineCaller caller, MachineReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(report);
        var now = Now();
        return await database.InUserTransaction(caller.UserId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE machines SET name = @name, os = @os, arch = @arch, program_version = @programVersion, docker = @docker, last_seen_at = @now
            WHERE id = @machineId
            """,
            new { caller.MachineId, report.Name, report.Os, report.Arch, report.ProgramVersion, report.Docker, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken) > 0;
    }

    /// <summary>Now, to the microsecond Postgres keeps, so a time is stored as the page reads
    /// it.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }
}