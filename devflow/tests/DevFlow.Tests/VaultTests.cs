using System.Security.Cryptography;
using System.Text;

using Dapper;

using DevFlow.Settings;

using Npgsql;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests;

/// <summary>
/// The vault: a secret sealed with AES-256-GCM, bound to its user and purpose, stored in
/// <c>secrets</c> and reached only through the door. Each test takes its own copy of the migrated
/// database, as devflow_app through the door, and reads or changes the stored row from outside it,
/// as someone holding a copy of the database would. See "The vault" in devflow/README.md.
/// </summary>
public sealed class VaultTests
{
    private static readonly Guid Alice = Guid.Parse("0190a8f0-0000-7000-8000-00000000a11c");
    private static readonly Guid Bob = Guid.Parse("0190a8f0-0000-7000-8000-000000000b0b");

    private const string Purpose = "github.access";

    /// <summary>A value shaped like a GitHub token, so a test can look for it in text.</summary>
    private static readonly string Token = "ghu_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
    private static byte[] Value => Encoding.UTF8.GetBytes(Token);

    private static readonly string K1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string K2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A vault whose current key is <paramref name="current"/>, <c>&lt;id&gt;:&lt;base64&gt;</c>,
    /// with <paramref name="old"/> listed as old keys, loaded as the service loads them.</summary>
    private static Vault VaultWith(string current, string? old = null)
    {
        var environment = new Dictionary<string, string>(Harness.MinimalEnvironment) { ["DEVFLOW_VAULT_KEY"] = current };
        if (old is not null)
        {
            environment["DEVFLOW_VAULT_OLD_KEYS"] = old;
        }

        return new Vault(DevFlowSettings.Load(name => environment.GetValueOrDefault(name)).Vault);
    }

    private static Vault TheVault => VaultWith($"k1:{K1}");

    [Fact]
    public async Task AValuePutIsTheValueGot()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var vault = TheVault;
        var bytes = RandomNumberGenerator.GetBytes(300);

        await Put(door, vault, Alice, Purpose, Value);
        await Put(door, vault, Alice, "github.refresh", bytes);
        await Put(door, vault, Alice, "empty", []);

        Assert.Equal(Value, await Get(door, vault, Alice, Purpose));
        Assert.Equal(bytes, await Get(door, vault, Alice, "github.refresh"));
        Assert.Equal(Array.Empty<byte>(), await Get(door, vault, Alice, "empty"));
        // Nothing put is nothing got.
        Assert.Null(await Get(door, vault, Alice, "claude.login"));
        // And a vault loaded again from the same key opens what the first sealed.
        Assert.Equal(Value, await Get(door, TheVault, Alice, Purpose));
    }

    [Fact]
    public async Task APurposeIsAShortFixedName()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var vault = TheVault;

        foreach (var purpose in new[] { "", "GitHub.access", "1github", ".github", "github access", "github-access", "github.access\n", "gïthub" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Put(door, vault, Alice, purpose, Value));
            await Assert.ThrowsAsync<ArgumentException>(() => Get(door, vault, Alice, purpose));
            await Assert.ThrowsAsync<ArgumentException>(() => Delete(door, vault, Alice, purpose));
        }

        // And the table holds the same rule, for a row written around the vault.
        await using var owner = await OwnerAs(database, Alice);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(new CommandDefinition(
            "INSERT INTO secrets (user_id, purpose, key_id, nonce, ciphertext, updated_at) VALUES (@Alice, 'Bad purpose', 'k1', @nonce, @ciphertext, now())",
            new { Alice, nonce = new byte[12], ciphertext = new byte[16] }, cancellationToken: Ct)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    [Fact]
    public async Task TheStoredBytesDoNotContainTheValue()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        await Put(door, TheVault, Alice, Purpose, Value);

        await using var owner = await OwnerAs(database, Alice);
        var row = await owner.QuerySingleAsync<Row>(new CommandDefinition(
            "SELECT key_id AS KeyId, nonce AS Nonce, ciphertext AS Ciphertext FROM secrets", cancellationToken: Ct));

        Assert.Equal("k1", row.KeyId);
        Assert.Equal(12, row.Nonce.Length);
        // The value's length and the 16 byte tag appended to it.
        Assert.Equal(Value.Length + 16, row.Ciphertext.Length);
        Assert.False(Contains(row.Ciphertext, Value));
        // Nor any 6 bytes of it, so no run of the token is stored as it is.
        for (var start = 0; start + 6 <= Value.Length; start++)
        {
            Assert.False(Contains(row.Ciphertext, Value.AsSpan(start, 6)));
        }

        // And no column of any table holds it as text.
        var dump = await owner.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT string_agg(s::text, ' ') FROM secrets s", cancellationToken: Ct));
        Assert.DoesNotContain(Token, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(Value), dump, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARowMovedToAnotherUserDoesNotOpen()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var vault = TheVault;
        await Put(door, vault, Alice, Purpose, Value);

        // The same nonce and ciphertext, under Bob, as someone writing to the database could.
        await using (var admin = await database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "INSERT INTO secrets (user_id, purpose, key_id, nonce, ciphertext, updated_at) SELECT @Bob, purpose, key_id, nonce, ciphertext, updated_at FROM secrets WHERE user_id = @Alice",
                new { Alice, Bob }, cancellationToken: Ct));
        }

        var refused = await Assert.ThrowsAsync<VaultException>(() => Get(door, vault, Bob, Purpose));
        Assert.Contains(Purpose, refused.Message, StringComparison.Ordinal);
        // Alice's own still opens.
        Assert.Equal(Value, await Get(door, vault, Alice, Purpose));
    }

    [Fact]
    public async Task ARowMovedToAnotherPurposeDoesNotOpen()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var vault = TheVault;
        await Put(door, vault, Alice, Purpose, Value);

        await using (var admin = await database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "INSERT INTO secrets (user_id, purpose, key_id, nonce, ciphertext, updated_at) SELECT user_id, 'github.refresh', key_id, nonce, ciphertext, updated_at FROM secrets",
                cancellationToken: Ct));
        }

        await Assert.ThrowsAsync<VaultException>(() => Get(door, vault, Alice, "github.refresh"));
        Assert.Equal(Value, await Get(door, vault, Alice, Purpose));
    }

    [Fact]
    public async Task AChangedByteIsRefused()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var vault = TheVault;

        // The first and a middle byte of the ciphertext, the first and last of the nonce, and the
        // first and last of the tag, which ends the ciphertext column.
        var tagStart = Value.Length;
        var changes = new (string Column, int Index)[]
        {
            ("ciphertext", 0), ("ciphertext", Value.Length / 2),
            ("nonce", 0), ("nonce", 11),
            ("ciphertext", tagStart), ("ciphertext", tagStart + 15),
        };
        foreach (var (column, index) in changes)
        {
            await Put(door, vault, Alice, Purpose, Value);
            await using (var admin = await database.OpenAdmin(Ct))
            {
                Assert.Equal(1, await admin.ExecuteAsync(new CommandDefinition(
                    $"UPDATE secrets SET {column} = set_byte({column}, @index, get_byte({column}, @index) # 1)",
                    new { index }, cancellationToken: Ct)));
            }

            await Assert.ThrowsAsync<VaultException>(() => Get(door, vault, Alice, Purpose));
        }
    }

    [Fact]
    public async Task TwoPutsOfOneValueStoreDifferentBytes()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var vault = TheVault;

        await Put(door, vault, Alice, Purpose, Value);
        var first = await Stored(database, Alice);
        await Put(door, vault, Alice, Purpose, Value);
        var second = await Stored(database, Alice);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.Equal(Value, await Get(door, vault, Alice, Purpose));
    }

    [Fact]
    public async Task PutReplacesAndDeleteRemoves()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var vault = TheVault;
        var replacement = Encoding.UTF8.GetBytes("ghu_replacement");

        await Put(door, vault, Alice, Purpose, Value);
        await Put(door, vault, Alice, "github.refresh", Value);
        await Put(door, vault, Alice, Purpose, replacement);

        Assert.Equal(replacement, await Get(door, vault, Alice, Purpose));
        Assert.Equal(2, await Count(database));

        Assert.True(await Delete(door, vault, Alice, Purpose));
        Assert.Null(await Get(door, vault, Alice, Purpose));
        Assert.False(await Delete(door, vault, Alice, Purpose));
        // The other purpose stays.
        Assert.Equal(Value, await Get(door, vault, Alice, "github.refresh"));
        Assert.Equal(1, await Count(database));
    }

    [Fact]
    public async Task AValueSealedWithAnOlderKeyOpensAndIsResealedOnTheNextPut()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        await Put(door, VaultWith($"k1:{K1}"), Alice, Purpose, Value);

        // k2 made current, k1 listed as old: the row opens, and still names k1 until it is put again.
        var rotated = VaultWith($"k2:{K2}", $"k1:{K1}");
        Assert.Equal(Value, await Get(door, rotated, Alice, Purpose));
        Assert.Equal("k1", (await Stored(database, Alice)).KeyId);

        await Put(door, rotated, Alice, Purpose, Value);
        Assert.Equal("k2", (await Stored(database, Alice)).KeyId);

        // Now k1 can go: the row opens with k2 alone.
        var onlyK2 = VaultWith($"k2:{K2}");
        Assert.Equal(Value, await Get(door, onlyK2, Alice, Purpose));

        // A row naming a key that is not listed is refused, naming the key's id and never the key.
        await Put(door, VaultWith($"k1:{K1}"), Alice, "github.refresh", Value);
        var refused = await Assert.ThrowsAsync<VaultException>(() => Get(door, onlyK2, Alice, "github.refresh"));
        Assert.Contains("k1", refused.Message, StringComparison.Ordinal);
        Assert.Contains("DEVFLOW_VAULT_OLD_KEYS", refused.Message, StringComparison.Ordinal);
        NoSecretIn(refused.ToString());
    }

    [Fact]
    public async Task OneUserCannotReadAnothersSecret()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var vault = TheVault;
        await Put(door, vault, Alice, Purpose, Value);

        // As Bob, by his own id and by Alice's: row level security leaves him nothing.
        Assert.Null(await Get(door, vault, Bob, Purpose));
        Assert.Null(await door.InUserTransaction(Bob, transaction => vault.Get(transaction, Alice, Purpose, Ct), Ct));
        Assert.False(await door.InUserTransaction(Bob, transaction => vault.Delete(transaction, Alice, Purpose, Ct), Ct));

        // Nor can he write one in her name.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => door.InUserTransaction(Bob, async transaction =>
        {
            await vault.Put(transaction, Alice, Purpose, [1, 2, 3], Ct);
            return 0;
        }, Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);

        Assert.Equal(Value, await Get(door, vault, Alice, Purpose));
    }

    [Fact]
    public async Task NoSecretReachesALogOrAnException()
    {
        await using var database = await Databases.DevFlow.Migrated(Ct);
        await using var door = await Door(database);
        var vault = TheVault;
        var texts = new List<string>();

        async Task Refused<TException>(Func<Task> act)
            where TException : Exception => texts.Add((await Assert.ThrowsAsync<TException>(act)).ToString());

        // A key that is not listed, a changed byte, a row moved to another user and to another
        // purpose, and a purpose that is not a name.
        await Put(door, vault, Alice, Purpose, Value);
        await Refused<VaultException>(() => Get(door, VaultWith($"k2:{K2}"), Alice, Purpose));
        await using (var admin = await database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO secrets (user_id, purpose, key_id, nonce, ciphertext, updated_at)
                SELECT @Bob, purpose, key_id, nonce, ciphertext, updated_at FROM secrets WHERE user_id = @Alice;
                INSERT INTO secrets (user_id, purpose, key_id, nonce, ciphertext, updated_at)
                SELECT user_id, 'github.refresh', key_id, nonce, ciphertext, updated_at FROM secrets WHERE user_id = @Alice;
                """,
                new { Alice, Bob }, cancellationToken: Ct));
        }

        await Refused<VaultException>(() => Get(door, vault, Bob, Purpose));
        await Refused<VaultException>(() => Get(door, vault, Alice, "github.refresh"));
        await using (var admin = await database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE secrets SET ciphertext = set_byte(ciphertext, 0, get_byte(ciphertext, 0) # 1) WHERE user_id = @Alice AND purpose = @Purpose",
                new { Alice, Purpose }, cancellationToken: Ct));
        }

        await Refused<VaultException>(() => Get(door, vault, Alice, Purpose));
        await Refused<ArgumentException>(() => Put(door, vault, Alice, "Not a name", Value));
        await Refused<PostgresException>(() => door.InUserTransaction(Bob, async transaction =>
        {
            await vault.Put(transaction, Alice, Purpose, Value, Ct);
            return 0;
        }, Ct));

        // What the service prints of its settings and its keys.
        var settings = DevFlowSettings.Load(name => name switch
        {
            "DEVFLOW_APP_DATABASE_URL" => Harness.MinimalEnvironment["DEVFLOW_APP_DATABASE_URL"],
            "DEVFLOW_VAULT_KEY" => $"k2:{K2}",
            "DEVFLOW_VAULT_OLD_KEYS" => $"k1:{K1}",
            _ => null,
        });
        texts.Add(settings.ToString());
        texts.Add(settings.Vault.ToString());
        texts.Add(settings.Vault.Current.ToString());
        texts.Add(vault.ToString()!);

        Assert.Equal(10, texts.Count);
        foreach (var text in texts)
        {
            NoSecretIn(text);
        }
    }

    /// <summary>Asserts that <paramref name="text"/> holds neither the value nor a key, in any of
    /// the ways they could be written out.</summary>
    private static void NoSecretIn(string text)
    {
        foreach (var secret in new[]
        {
            Token, Convert.ToHexString(Value), Convert.ToBase64String(Value),
            K1, K2, Convert.ToHexString(Convert.FromBase64String(K1)), Convert.ToHexString(Convert.FromBase64String(K2)),
        })
        {
            Assert.DoesNotContain(secret[..16], text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Task<UserDb> Door(TestDatabase database) =>
        UserDb.Open(DevFlowSettings.Service, database.App(), DevFlowSettings.AppDatabaseUrl, Ct);

    private static async Task Put(UserDb door, Vault vault, Guid user, string purpose, byte[] value) =>
        await door.InUserTransaction(user, async transaction =>
        {
            await vault.Put(transaction, user, purpose, value, Ct);
            return 0;
        }, Ct);

    private static Task<byte[]?> Get(UserDb door, Vault vault, Guid user, string purpose) =>
        door.InUserTransaction(user, transaction => vault.Get(transaction, user, purpose, Ct), Ct);

    private static Task<bool> Delete(UserDb door, Vault vault, Guid user, string purpose) =>
        door.InUserTransaction(user, transaction => vault.Delete(transaction, user, purpose, Ct), Ct);

    /// <summary>A connection as devflow_owner reading as <paramref name="user"/>: the table's row
    /// level security is forced, so even its owner reads only the user it names.</summary>
    private static async Task<NpgsqlConnection> OwnerAs(TestDatabase database, Guid user)
    {
        var owner = await database.OpenOwner(Ct);
        await owner.ExecuteAsync(new CommandDefinition(
            "SELECT set_config('devflow.user_id', @user, false)", new { user = user.ToString() }, cancellationToken: Ct));
        return owner;
    }

    /// <summary><paramref name="user"/>'s one stored row, read as the owner.</summary>
    private static async Task<Row> Stored(TestDatabase database, Guid user)
    {
        await using var owner = await OwnerAs(database, user);
        return await owner.QuerySingleAsync<Row>(new CommandDefinition(
            "SELECT key_id AS KeyId, nonce AS Nonce, ciphertext AS Ciphertext FROM secrets", cancellationToken: Ct));
    }

    private static async Task<long> Count(TestDatabase database)
    {
        await using var admin = await database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<long>(new CommandDefinition("SELECT count(*) FROM secrets", cancellationToken: Ct));
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) => haystack.IndexOf(needle) >= 0;

    private sealed record Row(string KeyId, byte[] Nonce, byte[] Ciphertext);
}