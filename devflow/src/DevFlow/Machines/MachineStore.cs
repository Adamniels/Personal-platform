using Dapper;

using DevFlow.Link;

using PlatformData;

namespace DevFlow.Machines;

/// <summary>What a machine reports when it says hello, each already checked by the route.</summary>
public sealed record MachineReport(string Name, string Os, string Arch, string ProgramVersion, string Docker);

/// <summary>A machine as the user's list answers it.</summary>
/// <param name="Id">The machine's id.</param>
/// <param name="Name">Its name, as it last said.</param>
/// <param name="Os"><c>macos</c> or <c>linux</c>.</param>
/// <param name="Arch"><c>arm64</c> or <c>x64</c>.</param>
/// <param name="ProgramVersion">The machine program's version, null before its first hello.</param>
/// <param name="Docker"><c>running</c>, or why Docker is not, null before its first hello.</param>
/// <param name="Online">Whether it was seen less than <see cref="MachineStore.OnlineFor"/>
/// ago.</param>
/// <param name="LastSeenAt">When it enrolled or last said hello.</param>
public sealed record Machine(Guid Id, string Name, string Os, string Arch, string? ProgramVersion, string? Docker, bool Online, DateTimeOffset LastSeenAt);

/// <summary>
/// Each user's machines: a machine saying hello as its user, through the door, from the
/// <see cref="MachineCaller"/> its key led to, so a machine can only ever change itself; and the
/// user's list of them and removing one, through the door as the user, so another user's machine
/// is never seen or removed. See "Machines" in devflow/README.md.
/// </summary>
public sealed class MachineStore(UserDb database, TimeProvider time)
{
    /// <summary>How recently a machine was seen to be online. The program says hello every few
    /// seconds, so a minute is several missed in a row.</summary>
    public static readonly TimeSpan OnlineFor = TimeSpan.FromSeconds(60);

    /// <summary>The user's machines, by when each enrolled, each online when seen less than
    /// <see cref="OnlineFor"/> ago.</summary>
    public async Task<IReadOnlyList<Machine>> List(Guid userId, CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow() - OnlineFor;
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id AS Id, name AS Name, os AS Os, arch AS Arch, program_version AS ProgramVersion, docker AS Docker,
                last_seen_at > @since AS Online, last_seen_at AS LastSeenAt
            FROM machines ORDER BY first_seen_at, id
            """,
            new { since },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(row => new Machine(row.Id, row.Name, row.Os, row.Arch, row.ProgramVersion, row.Docker, row.Online, GitHubLink.Utc(row.LastSeenAt)))];
    }

    /// <summary>Removes the user's machine, and its key with it, so the key is refused from the
    /// next request; false when they have none by that id, another user's included.</summary>
    public async Task<bool> Remove(Guid userId, Guid machineId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM machines WHERE id = @machineId",
            new { machineId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken) > 0;

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

    private sealed record Row(Guid Id, string Name, string Os, string Arch, string? ProgramVersion, string? Docker, bool Online, DateTime LastSeenAt);
}