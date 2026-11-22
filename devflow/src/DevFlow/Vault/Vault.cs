using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Dapper;

using DevFlow.Settings;

using Npgsql;

namespace DevFlow;

/// <summary>
/// Keeps a secret for a user, sealed in <c>secrets</c> so that a copy of the database does not reveal
/// it. Each value is sealed with AES-256-GCM from .NET's own <see cref="AesGcm"/>, under the current
/// key, whose id the row records so it still opens once a newer key is current, with a random 12
/// byte nonce per put, a 16 byte tag appended to the ciphertext, and
/// <c>devflow:v1:&lt;user id&gt;:&lt;purpose&gt;</c> as the associated data, so a row moved to another
/// user or purpose does not open. Every operation takes the door's transaction, so a secret is only
/// ever touched as its user, under row level security. See "The vault" in devflow/README.md.
///
/// Nothing here puts a value or a key in a message: a refusal names the purpose and the key's id.
/// </summary>
public sealed partial class Vault(VaultSettings keys)
{
    public const int NonceLength = 12;
    public const int TagLength = 16;

    private readonly VaultSettings _keys = keys ?? throw new ArgumentNullException(nameof(keys));

    /// <summary>
    /// Seals <paramref name="value"/> under the current key and stores it as
    /// <paramref name="userId"/>'s secret for <paramref name="purpose"/>, replacing any before it.
    /// </summary>
    /// <param name="transaction">The door's, from <c>UserDb.InUserTransaction</c> for
    /// <paramref name="userId"/>. Under any other user row level security refuses the write.</param>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is not a purpose's name.</exception>
    public async Task Put(NpgsqlTransaction transaction, Guid userId, string purpose, byte[] value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(value);
        CheckPurpose(purpose);
        var (keyId, nonce, sealedValue) = Seal(userId, purpose, value);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO secrets (user_id, purpose, key_id, nonce, ciphertext, updated_at)
            VALUES (@userId, @purpose, @keyId, @nonce, @sealedValue, now())
            ON CONFLICT (user_id, purpose) DO UPDATE
            SET key_id = excluded.key_id, nonce = excluded.nonce, ciphertext = excluded.ciphertext, updated_at = excluded.updated_at
            """,
            new { userId, purpose, keyId, nonce, sealedValue }, transaction, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// <paramref name="userId"/>'s secret for <paramref name="purpose"/>, opened, or null when there
    /// is none, or when the transaction is another user's, which row level security leaves nothing.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is not a purpose's name.</exception>
    /// <exception cref="VaultException">The row names a key that is not configured, or does not
    /// open: it was changed, or moved from another user or purpose.</exception>
    public async Task<byte[]?> Get(NpgsqlTransaction transaction, Guid userId, string purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        CheckPurpose(purpose);
        var row = await transaction.Connection!.QuerySingleOrDefaultAsync<Sealed>(new CommandDefinition(
            "SELECT key_id AS KeyId, nonce AS Nonce, ciphertext AS Ciphertext FROM secrets WHERE user_id = @userId AND purpose = @purpose",
            new { userId, purpose }, transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        return Open(userId, purpose, new SealedValue(row.KeyId, row.Nonce, row.Ciphertext), purpose);
    }

    /// <summary>Removes <paramref name="userId"/>'s secret for <paramref name="purpose"/>.</summary>
    /// <returns>Whether there was one to remove.</returns>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is not a purpose's name.</exception>
    public async Task<bool> Delete(NpgsqlTransaction transaction, Guid userId, string purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        CheckPurpose(purpose);
        return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM secrets WHERE user_id = @userId AND purpose = @purpose",
            new { userId, purpose }, transaction, cancellationToken: cancellationToken)) > 0;
    }

    /// <summary>
    /// Seals <paramref name="value"/>, the value named <paramref name="name"/> of
    /// <paramref name="userId"/>'s project <paramref name="projectId"/>, under the current key, for
    /// the caller to store in its own row: a project's values live in <c>project_values</c>, gone
    /// with their project, rather than in <c>secrets</c>. The associated data binds it to the user,
    /// the project and the name, so a value moved to another of any of them does not open.
    /// </summary>
    public SealedValue SealProjectValue(Guid userId, Guid projectId, string name, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Seal(userId, ProjectValueBinding(projectId, name), value);
    }

    /// <summary>A project's value sealed by <see cref="SealProjectValue"/>, opened.</summary>
    /// <exception cref="VaultException">It names a key that is not configured, or does not open: it
    /// was changed, or moved from another user, project or name.</exception>
    public byte[] OpenProjectValue(Guid userId, Guid projectId, string name, SealedValue sealedValue)
    {
        ArgumentNullException.ThrowIfNull(sealedValue);
        return Open(userId, ProjectValueBinding(projectId, name), sealedValue, $"the value {name} of a project");
    }

    /// <summary>What a project's value is bound to after the user, as a purpose is. A purpose
    /// never holds a colon, so no secret's binding is ever a project value's.</summary>
    private static string ProjectValueBinding(Guid projectId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return $"project-value:{projectId.ToString("D", CultureInfo.InvariantCulture)}:{name}";
    }

    /// <summary>Seals <paramref name="value"/> under the current key with a random nonce, bound to
    /// the user and <paramref name="binding"/>.</summary>
    private SealedValue Seal(Guid userId, string binding, byte[] value)
    {
        var key = _keys.Current;
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var sealedValue = new byte[value.Length + TagLength];
        using (var cipher = new AesGcm(key.Bytes, TagLength))
        {
            cipher.Encrypt(nonce, value, sealedValue.AsSpan(0, value.Length), sealedValue.AsSpan(value.Length), AssociatedData(userId, binding));
        }

        return new SealedValue(key.Id, nonce, sealedValue);
    }

    /// <summary>Opens a sealed value bound to the user and <paramref name="binding"/>; a refusal
    /// names <paramref name="what"/> and the key's id, never the value.</summary>
    private byte[] Open(Guid userId, string binding, SealedValue sealedValue, string what)
    {
        var key = KeyFor(sealedValue.KeyId)
            ?? throw new VaultException(
                $"the secret {what} is sealed with the key {sealedValue.KeyId}, which is not configured: list it in {VaultSettings.OldKeysSetting} to open it");
        if (sealedValue.Nonce.Length != NonceLength || sealedValue.Ciphertext.Length < TagLength)
        {
            throw DidNotOpen(what);
        }

        var length = sealedValue.Ciphertext.Length - TagLength;
        var value = new byte[length];
        try
        {
            using var cipher = new AesGcm(key.Bytes, TagLength);
            cipher.Decrypt(sealedValue.Nonce, sealedValue.Ciphertext.AsSpan(0, length), sealedValue.Ciphertext.AsSpan(length), value, AssociatedData(userId, binding));
        }
        catch (AuthenticationTagMismatchException)
        {
            // Decrypt clears what it wrote, so no part of a forged value is left behind.
            throw DidNotOpen(what);
        }

        return value;
    }

    /// <summary>The key ids alone, never a key.</summary>
    public override string ToString() => $"Vault {{ {_keys} }}";

    /// <summary>The key a row names, by its id: the current one, or one listed as old, which a
    /// get opens with and a put never seals with. So a key is changed by making a new one current
    /// and listing the old one until every row has been put again.</summary>
    private VaultKey? KeyFor(string id) =>
        id == _keys.Current.Id ? _keys.Current : _keys.Old.FirstOrDefault(key => key.Id == id);

    /// <summary>What binds a sealed value to its user and its purpose, or a project's value's
    /// binding. The version lets a later format change what is bound without opening an older row
    /// as the newer.</summary>
    private static byte[] AssociatedData(Guid userId, string binding) =>
        Encoding.UTF8.GetBytes($"devflow:v1:{userId.ToString("D", CultureInfo.InvariantCulture)}:{binding}");

    private static VaultException DidNotOpen(string what) =>
        new($"the secret {what} did not open: it was changed, or moved from another user or purpose");

    /// <summary>A purpose is a short fixed name the caller chooses, such as <c>github.access</c> or
    /// <c>claude-login</c>.</summary>
    private static void CheckPurpose(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        if (!PurposeName().IsMatch(purpose))
        {
            throw new ArgumentException("is not a purpose's name, lowercase ASCII letters, digits, dots and hyphens, starting with a letter", nameof(purpose));
        }
    }

    /// <summary>The same rule as the table's check, ending at <c>\z</c>, since <c>$</c> would let a
    /// trailing newline through.</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PurposeName();

    /// <summary>A row as stored.</summary>
    private sealed record Sealed(string KeyId, byte[] Nonce, byte[] Ciphertext);
}

/// <summary>A value as the vault sealed it: the id of the key, the nonce and the ciphertext with
/// its tag. Prints the key's id alone.</summary>
public sealed record SealedValue(string KeyId, byte[] Nonce, byte[] Ciphertext)
{
    public override string ToString() => $"SealedValue {{ KeyId = {KeyId} }}";
}