using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Dapper;

using PlatformData;

namespace DevFlow.Machines;

/// <summary>A code made for "Add a machine", the one time it is answered.</summary>
/// <param name="Code">The code, <c>dfe_</c> and the base64url of 16 random bytes.</param>
/// <param name="ExpiresAt">When it stops enrolling, an hour after it was made.</param>
public sealed record NewCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>What a machine says of itself when it enrols, each already checked by the
/// route.</summary>
/// <param name="Name">Its name, such as its hostname.</param>
/// <param name="Os"><c>macos</c> or <c>linux</c>.</param>
/// <param name="Arch"><c>arm64</c> or <c>x64</c>.</param>
public sealed record MachineIntro(string Name, string Os, string Arch);

/// <summary>A machine just enrolled: its id, and its key, answered this once.</summary>
public sealed record EnrolledMachine(Guid MachineId, string Key);

/// <summary>
/// The one-time codes a machine is enrolled with. "Add a machine" makes a code for its user, valid
/// for <see cref="Lifetime"/> and once, stored only as its SHA-256; a newer code ends no older one.
/// A machine that presents a code is created as the code's user, with its key, in the transaction
/// that marks the code used, so a code enrols one machine at most however many present it at once.
/// The code is found by its hash outside a user in <c>machine_enrollment_lookup</c>, and its age
/// and use are read as that user, as <see cref="MachineKeys.Find"/> confirms a key. See "Machines"
/// in devflow/README.md.
/// </summary>
public sealed partial class MachineEnrollments(UserDb database, TimeProvider time)
{
    /// <summary>What every code begins with.</summary>
    public const string Prefix = "dfe_";

    /// <summary>How long a code enrols after it was made.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// A new code for <paramref name="userId"/>, answered this once, and only its hash kept. The
    /// user's codes made <see cref="Lifetime"/> ago or more, used or not, are deleted with it, since
    /// none of them enrols any more.
    /// </summary>
    public async Task<NewCode> Make(Guid userId, CancellationToken cancellationToken)
    {
        var code = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var now = Now();
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM machine_enrollments WHERE created_at <= @expired;
            INSERT INTO machine_enrollments (code_hash, user_id, created_at) VALUES (@hash, @userId, @now);
            """,
            new { userId, hash = Hash(code), now, expired = now - Lifetime },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return new NewCode(code, now + Lifetime);
    }

    /// <summary>
    /// The machine <paramref name="presented"/> enrolled, created as the code's user with
    /// <paramref name="intro"/> and seen now, and its new key; or null when it is not a code, not
    /// one made, used already, or made <see cref="Lifetime"/> ago or more, which the route answers
    /// alike. The code is marked used, the machine created and its key stored in one transaction.
    /// </summary>
    public async Task<EnrolledMachine?> Enroll(string? presented, MachineIntro intro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intro);
        if (presented is null || !Shape().IsMatch(presented))
        {
            return null;
        }

        var hash = Hash(presented);
        var userId = await database.WithoutUser(transaction => transaction.Connection!.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT user_id FROM machine_enrollment_lookup WHERE code_hash = @hash",
            new { hash },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        if (userId is not { } user)
        {
            return null;
        }

        var now = Now();
        return await database.InUserTransaction<EnrolledMachine?>(user, async transaction =>
        {
            // The row lock this takes makes a second enrolment with the code wait, and then find it
            // used.
            var used = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "UPDATE machine_enrollments SET used_at = @now WHERE code_hash = @hash AND used_at IS NULL AND created_at > @expired",
                new { hash, now, expired = now - Lifetime },
                transaction,
                cancellationToken: cancellationToken));
            if (used == 0)
            {
                return null;
            }

            var machineId = Guid.CreateVersion7(now);
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO machines (id, user_id, name, os, arch, first_seen_at, last_seen_at)
                VALUES (@machineId, @user, @name, @os, @arch, @now, @now)
                """,
                new { machineId, user, name = intro.Name, os = intro.Os, arch = intro.Arch, now },
                transaction,
                cancellationToken: cancellationToken));
            var key = await MachineKeys.Make(transaction, machineId, user, now, cancellationToken);
            return new EnrolledMachine(machineId, key);
        }, cancellationToken);
    }

    /// <summary>Now, to the microsecond Postgres keeps, so a time is stored as the page reads
    /// it.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    /// <summary>The SHA-256 of the code as written. 128 random bits leave nothing to guess in an
    /// hour, so a plain hash is enough.</summary>
    private static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    /// <summary><c>dfe_</c> and the base64url of 16 bytes, without padding, and nothing after
    /// it.</summary>
    [GeneratedRegex(@"^dfe_[A-Za-z0-9_-]{22}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}