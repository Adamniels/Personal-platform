using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Machines;

/// <summary>Who a machine's request is made by: its key's machine, and that machine's user, whom
/// everything it does runs as.</summary>
/// <param name="MachineId">The machine the key was made for.</param>
/// <param name="UserId">The machine's user, who enrolled it.</param>
public sealed record MachineCaller(Guid MachineId, Guid UserId);

/// <summary>
/// A machine's key: 32 random bytes from a cryptographic generator, written as <c>dfm_</c> and
/// their base64url, and stored only as the SHA-256 of that text. It is made when the machine
/// enrols, by <see cref="MachineEnrollments.Enroll"/>, answered that once, and ends when the
/// machine is removed; nothing lists one or shows it again. A presented key is found by its hash,
/// never compared with a stored value: the hash is looked up outside a user in
/// <c>machine_key_lookup</c>, and then confirmed against <c>machine_keys</c> through the door as
/// the machine's user, so the lookup alone opens nothing, as <see cref="Workers.WorkerKeys"/> does
/// for a worker's. See "Machines" in devflow/README.md and "Security decisions and accepted risks"
/// in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class MachineKeys(UserDb database)
{
    /// <summary>What every key begins with, so one is recognised for what it is wherever it ends
    /// up, a scanner for leaked secrets among them.</summary>
    public const string Prefix = "dfm_";

    /// <summary>
    /// A new key for <paramref name="machineId"/>, stored as its hash in
    /// <paramref name="transaction"/>, the one that creates the machine as its user. The key is in
    /// the answer and nowhere else: it is never logged, and only its hash is stored.
    /// </summary>
    internal static async Task<string> Make(NpgsqlTransaction transaction, Guid machineId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var key = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "INSERT INTO machine_keys (machine_id, user_id, key_hash, created_at) VALUES (@machineId, @userId, @hash, @now)",
            new { machineId, userId, hash = Hash(key), now },
            transaction,
            cancellationToken: cancellationToken));
        return key;
    }

    /// <summary>
    /// The machine and user <paramref name="presented"/> is the key of, or null when it is not a
    /// key, or not one any machine has now. Found by its hash, outside a user, and confirmed as that
    /// user, so a lookup row that does not agree with the user's own keys opens nothing.
    /// </summary>
    public async Task<MachineCaller?> Find(string presented, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presented);
        if (!Shape().IsMatch(presented))
        {
            return null;
        }

        var hash = Hash(presented);
        var target = await database.WithoutUser(transaction => transaction.Connection!.QuerySingleOrDefaultAsync<Target>(new CommandDefinition(
            "SELECT machine_id AS MachineId, user_id AS UserId FROM machine_key_lookup WHERE key_hash = @hash",
            new { hash },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        if (target is null)
        {
            return null;
        }

        var confirmed = await database.InUserTransaction(target.UserId, transaction => transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM machine_keys WHERE machine_id = @machineId AND key_hash = @hash)",
            new { machineId = target.MachineId, hash },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return confirmed ? new MachineCaller(target.MachineId, target.UserId) : null;
    }

    /// <summary>The SHA-256 of the key as written. The key is 256 random bits, so a plain hash is
    /// as strong as a slow one would be: there is nothing to guess.</summary>
    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary><c>dfm_</c> and the base64url of 32 bytes, without padding, and nothing after it:
    /// <c>\z</c>, since <c>$</c> would let a trailing newline through.</summary>
    [GeneratedRegex(@"^dfm_[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    private sealed record Target(Guid MachineId, Guid UserId);
}