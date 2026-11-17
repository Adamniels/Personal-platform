using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Dapper;

using PlatformData;

namespace DevFlow.Workers;

/// <summary>Who a worker's request is made by: its key's project, and that project's user, whom
/// everything it does runs as.</summary>
/// <param name="ProjectId">The project the key was made for.</param>
/// <param name="UserId">The project's user.</param>
public sealed record WorkerCaller(Guid ProjectId, Guid UserId);

/// <summary>
/// A project's worker key: 32 random bytes from a cryptographic generator, written as <c>dfw_</c>
/// and their base64url, and stored only as the SHA-256 of that text. Making one answers it once and
/// replaces the project's last at once; nothing lists one or shows it again. A presented key is
/// found by its hash, never compared with a stored value: the hash is looked up outside a user in
/// <c>worker_key_lookup</c>, the one thing that may be, and then confirmed against
/// <c>worker_keys</c> through the door as the project's user, so the lookup alone opens nothing.
/// See "Workers" in devflow/README.md and "Security decisions and accepted risks" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class WorkerKeys(UserDb database, TimeProvider time)
{
    /// <summary>What every key begins with, so one is recognised for what it is wherever it ends
    /// up, a scanner for leaked secrets among them.</summary>
    public const string Prefix = "dfw_";

    /// <summary>
    /// A new key for the project, replacing its last one, when the user has the project; null when
    /// they have none by that id, another user's included. The key is in the answer and nowhere
    /// else: it is never logged, and only its hash is stored.
    /// </summary>
    public async Task<string?> Make(Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        var key = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        // Postgres keeps microseconds, so the time is stored as the page will read it.
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % 10));
        // From the user's own project, which row level security hides when it is not theirs, so a
        // key is only ever made for a project of the user's.
        var made = await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO worker_keys (project_id, user_id, key_hash, created_at)
            SELECT id, user_id, @hash, @now FROM projects WHERE id = @projectId
            ON CONFLICT (project_id) DO UPDATE SET key_hash = EXCLUDED.key_hash, created_at = EXCLUDED.created_at
            """,
            new { projectId, hash = Hash(key), now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return made == 0 ? null : key;
    }

    /// <summary>
    /// The project and user <paramref name="presented"/> is the key of, or null when it is not a
    /// key, or not one any project has now. Found by its hash, outside a user, and confirmed as that
    /// user, so a lookup row that does not agree with the user's own keys opens nothing.
    /// </summary>
    public async Task<WorkerCaller?> Find(string presented, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presented);
        if (!Shape().IsMatch(presented))
        {
            return null;
        }

        var hash = Hash(presented);
        var target = await database.WithoutUser(transaction => transaction.Connection!.QuerySingleOrDefaultAsync<Target>(new CommandDefinition(
            "SELECT project_id AS ProjectId, user_id AS UserId FROM worker_key_lookup WHERE key_hash = @hash",
            new { hash },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        if (target is null)
        {
            return null;
        }

        var confirmed = await database.InUserTransaction(target.UserId, transaction => transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM worker_keys WHERE project_id = @projectId AND key_hash = @hash)",
            new { projectId = target.ProjectId, hash },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return confirmed ? new WorkerCaller(target.ProjectId, target.UserId) : null;
    }

    /// <summary>The SHA-256 of the key as written. The key is 256 random bits, so a plain hash is
    /// as strong as a slow one would be: there is nothing to guess.</summary>
    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary><c>dfw_</c> and the base64url of 32 bytes, without padding, and nothing after it:
    /// <c>\z</c>, since <c>$</c> would let a trailing newline through.</summary>
    [GeneratedRegex(@"^dfw_[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    private sealed record Target(Guid ProjectId, Guid UserId);
}