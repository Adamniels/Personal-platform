using System.Text;
using System.Text.RegularExpressions;

using Dapper;

using DevFlow.Link;

using PlatformData;

namespace DevFlow.ClaudeLogin;

/// <summary>What <c>GET /devflow/api/api-key</c> answers: whether a key is set and when it was
/// given, and never the key or any part of it.</summary>
/// <param name="Set">Whether the user has given one.</param>
/// <param name="GivenAt">When, null without one.</param>
public sealed record ApiKeyStatus(bool Set, DateTimeOffset? GivenAt)
{
    public static readonly ApiKeyStatus None = new(false, null);
}

/// <summary>
/// A user's Anthropic API key, opened for one of their runs by <see cref="ApiKeys.ForRun"/>, the
/// only way to one. It prints as a placeholder, never as its value, so a log line or an exception
/// that names it does not reveal it.
/// </summary>
public sealed class ApiKey
{
    internal ApiKey(string value) => Value = value;

    /// <summary>The key, for the run's <c>ANTHROPIC_API_KEY</c> and nowhere else.</summary>
    public string Value { get; }

    public override string ToString() => "ApiKey { sealed }";
}

/// <summary>
/// Each user's Anthropic API key, which a run of a project that runs on it is handed in place of the
/// Claude login, so it is billed to the user's Anthropic account. One per user, as the Claude login
/// is, since one owner pays for all of his projects. The key is sealed in the vault under
/// <see cref="Purpose"/>, and <c>api_keys</c> holds only that there is one and when it was given.
/// Every operation runs through the door as the user. See "The API key" in devflow/README.md.
/// </summary>
public sealed partial class ApiKeys(UserDb database, Vault vault, TimeProvider time)
{
    /// <summary>The vault's purpose the key is sealed under.</summary>
    public const string Purpose = "anthropic-api-key";

    /// <summary>What an API key begins with.</summary>
    public const string Prefix = "sk-ant-api";

    /// <summary>The longest text taken as a key, well past a real one's hundred or so characters,
    /// so a paste of something else is not stored.</summary>
    public const int MaxLength = 512;

    /// <summary>
    /// <paramref name="text"/> without the space around it, when it looks like an API key:
    /// <see cref="Prefix"/> followed by more, all visible ASCII, at most <see cref="MaxLength"/>
    /// characters. Null otherwise, a setup token among them. Dev flow does not ask Anthropic
    /// whether it works; a run it is refused on fails outside its card.
    /// </summary>
    public static string? AsApiKey(string? text)
    {
        var key = text?.Trim();
        return key is { Length: <= MaxLength } && ApiKeyShape().IsMatch(key) ? key : null;
    }

    /// <summary>
    /// Seals <paramref name="key"/> as the user's API key, replacing any before it, given now, and
    /// answers the status it leaves.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> does not look like an API key,
    /// see <see cref="AsApiKey"/>.</exception>
    public async Task<ApiKeyStatus> Set(Guid userId, string key, CancellationToken cancellationToken)
    {
        var accepted = AsApiKey(key)
            ?? throw new ArgumentException("does not look like an API key", nameof(key));
        var now = Now();
        await database.InUserTransaction(userId, async transaction =>
        {
            await vault.Put(transaction, userId, Purpose, Encoding.UTF8.GetBytes(accepted), cancellationToken);
            return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO api_keys (user_id, secret_purpose, given_at) VALUES (@userId, @purpose, @now)
                ON CONFLICT (user_id) DO UPDATE SET given_at = EXCLUDED.given_at
                """,
                new { userId, purpose = Purpose, now },
                transaction,
                cancellationToken: cancellationToken));
        }, cancellationToken);
        return new ApiKeyStatus(true, now);
    }

    /// <summary>The user's key as it stands now, without opening it.</summary>
    public async Task<ApiKeyStatus> Status(Guid userId, CancellationToken cancellationToken)
    {
        var given = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            "SELECT given_at FROM api_keys WHERE user_id = @userId",
            new { userId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return given is { } at ? new ApiKeyStatus(true, GitHubLink.Utc(at)) : ApiKeyStatus.None;
    }

    /// <summary>
    /// Removes the user's key and its sealed secret. Empty when it was removed, or when there was
    /// none.
    /// </summary>
    public async Task<IReadOnlyList<string>> Remove(Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction<IReadOnlyList<string>>(userId, async transaction =>
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "DELETE FROM api_keys WHERE user_id = @userId",
                new { userId },
                transaction,
                cancellationToken: cancellationToken));
            await vault.Delete(transaction, userId, Purpose, cancellationToken);
            return [];
        }, cancellationToken);

    /// <summary>
    /// <paramref name="runUserId"/>'s key, opened for one of their runs, or null when they have
    /// given none. The one method that opens it, called only when a job is handed out, so the key
    /// leaves Dev flow only inside a job's answer to its own user's worker. It opens through the door
    /// as the run's user, so row level security shows no one else's, and the vault's binding to the
    /// user it was sealed for refuses a key moved to another.
    /// </summary>
    /// <exception cref="VaultException">The key does not open as this user's: it was moved from
    /// another user, or changed.</exception>
    public async Task<ApiKey?> ForRun(Guid runUserId, CancellationToken cancellationToken)
    {
        var sealedKey = await database.InUserTransaction(runUserId, async transaction =>
        {
            var given = await transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM api_keys WHERE user_id = @runUserId)",
                new { runUserId },
                transaction,
                cancellationToken: cancellationToken));
            return given ? await vault.Get(transaction, runUserId, Purpose, cancellationToken) : null;
        }, cancellationToken);
        return sealedKey is null ? null : new ApiKey(Encoding.UTF8.GetString(sealedKey));
    }

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    /// <summary><see cref="Prefix"/> and at least one more visible ASCII character, nothing else:
    /// no space or line break, which would end up in the run's environment. <c>\z</c>, since
    /// <c>$</c> would let a trailing newline through.</summary>
    [GeneratedRegex(@"^sk-ant-api[\x21-\x7E]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyShape();
}