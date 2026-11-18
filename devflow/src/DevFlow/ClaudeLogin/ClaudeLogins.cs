using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Dapper;

using DevFlow.Link;

using PlatformData;

namespace DevFlow.ClaudeLogin;

/// <summary>Where a user's Claude login stands, as the status route answers it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClaudeLoginState>))]
public enum ClaudeLoginState
{
    /// <summary>None has been given.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Given, and more than <see cref="ClaudeLogins.WarnBefore"/> from its renewal.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>Within <see cref="ClaudeLogins.WarnBefore"/> of its renewal: it needs the owner.</summary>
    [JsonStringEnumMemberName("due_soon")]
    DueSoon,

    /// <summary>Past its renewal: runs on it are likely to be refused.</summary>
    [JsonStringEnumMemberName("expired")]
    Expired,
}

/// <summary>What <c>GET /devflow/api/claude-login</c> answers: whether a login is set, when it was
/// given and when it is due, and never the token or any part of it.</summary>
/// <param name="Set">Whether the user has given one.</param>
/// <param name="GivenAt">When, null without one.</param>
/// <param name="RenewBy"><paramref name="GivenAt"/> plus <see cref="ClaudeLogins.Lasts"/>, null
/// without one.</param>
/// <param name="State">Where it stands now.</param>
public sealed record ClaudeLoginStatus(bool Set, DateTimeOffset? GivenAt, DateTimeOffset? RenewBy, ClaudeLoginState State)
{
    public static readonly ClaudeLoginStatus None = new(false, null, null, ClaudeLoginState.None);

    /// <summary>The status of a login given at <paramref name="givenAt"/>, at
    /// <paramref name="now"/>: due soon from <see cref="ClaudeLogins.WarnBefore"/> before its
    /// renewal, and expired from the renewal on.</summary>
    public static ClaudeLoginStatus At(DateTimeOffset givenAt, DateTimeOffset now)
    {
        var renewBy = givenAt + ClaudeLogins.Lasts;
        var state = now >= renewBy ? ClaudeLoginState.Expired
            : now >= renewBy - ClaudeLogins.WarnBefore ? ClaudeLoginState.DueSoon
            : ClaudeLoginState.Ok;
        return new(true, givenAt, renewBy, state);
    }

    /// <summary>Whether it needs the owner: from due soon on. It counts towards Dev flow's
    /// attention, the nav's dot and the front page's card.</summary>
    [JsonIgnore]
    public bool NeedsYou => State is ClaudeLoginState.DueSoon or ClaudeLoginState.Expired;
}

/// <summary>
/// A user's Claude token, opened for one of their runs by <see cref="ClaudeLogins.ForRun"/>, the
/// only way to one. It prints as a placeholder, never as its value, so a log line or an exception
/// that names it does not reveal it.
/// </summary>
public sealed class ClaudeToken
{
    internal ClaudeToken(string value) => Value = value;

    /// <summary>The token, for the run's <c>CLAUDE_CODE_OAUTH_TOKEN</c> and nowhere else.</summary>
    public string Value { get; }

    public override string ToString() => "ClaudeToken { sealed }";
}

/// <summary>
/// Each user's Claude login: the token <c>claude setup-token</c> makes, which a run of theirs is
/// handed so it runs on their subscription. One per user, since the subscription is theirs. The
/// token is sealed in the vault under <see cref="Purpose"/>, and <c>claude_logins</c> holds only
/// that there is one and when it was given, since a setup token does not say when it runs out:
/// <c>claude auth status</c> reports no expiry for one. It lasts a year, so it is due
/// <see cref="Lasts"/> after it was given, with a warning from <see cref="WarnBefore"/> before.
/// Every operation runs through the door as the user. See "Claude login" in devflow/README.md.
/// </summary>
public sealed partial class ClaudeLogins(UserDb database, Vault vault, TimeProvider time)
{
    /// <summary>The vault's purpose the token is sealed under.</summary>
    public const string Purpose = "claude-login";

    /// <summary>What a setup token begins with.</summary>
    public const string Prefix = "sk-ant-oat";

    /// <summary>The longest text taken as a token, well past a real one's hundred or so
    /// characters, so a paste of something else is not stored.</summary>
    public const int MaxLength = 512;

    /// <summary>How long a setup token lasts, as Claude Code's documentation says.</summary>
    public static readonly TimeSpan Lasts = TimeSpan.FromDays(365);

    /// <summary>How long before its renewal a login needs the owner.</summary>
    public static readonly TimeSpan WarnBefore = TimeSpan.FromDays(31);

    /// <summary>
    /// <paramref name="text"/> without the space around it, when it looks like a setup token:
    /// <see cref="Prefix"/> followed by more, all visible ASCII, at most <see cref="MaxLength"/>
    /// characters. Null otherwise. Dev flow does not ask Anthropic whether it works; a worker
    /// checks the login before every run.
    /// </summary>
    public static string? AsSetupToken(string? text)
    {
        var token = text?.Trim();
        return token is { Length: <= MaxLength } && SetupToken().IsMatch(token) ? token : null;
    }

    /// <summary>
    /// Seals <paramref name="token"/> as the user's login, replacing any before it, given now, and
    /// answers the status it leaves.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="token"/> does not look like a setup
    /// token, see <see cref="AsSetupToken"/>.</exception>
    public async Task<ClaudeLoginStatus> Set(Guid userId, string token, CancellationToken cancellationToken)
    {
        var accepted = AsSetupToken(token)
            ?? throw new ArgumentException("does not look like a setup token", nameof(token));
        var now = Now();
        await database.InUserTransaction(userId, async transaction =>
        {
            await vault.Put(transaction, userId, Purpose, Encoding.UTF8.GetBytes(accepted), cancellationToken);
            return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO claude_logins (user_id, secret_purpose, given_at) VALUES (@userId, @purpose, @now)
                ON CONFLICT (user_id) DO UPDATE SET given_at = EXCLUDED.given_at
                """,
                new { userId, purpose = Purpose, now },
                transaction,
                cancellationToken: cancellationToken));
        }, cancellationToken);
        return ClaudeLoginStatus.At(now, now);
    }

    /// <summary>The user's login as it stands now, without opening the token.</summary>
    public async Task<ClaudeLoginStatus> Status(Guid userId, CancellationToken cancellationToken)
    {
        var given = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            "SELECT given_at FROM claude_logins WHERE user_id = @userId",
            new { userId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return given is { } at ? ClaudeLoginStatus.At(GitHubLink.Utc(at), time.GetUtcNow()) : ClaudeLoginStatus.None;
    }

    /// <summary>Removes the user's login and its sealed token. Whether there was one.</summary>
    public async Task<bool> Remove(Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, async transaction =>
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "DELETE FROM claude_logins WHERE user_id = @userId",
                new { userId },
                transaction,
                cancellationToken: cancellationToken));
            return await vault.Delete(transaction, userId, Purpose, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// <paramref name="runUserId"/>'s token, opened for one of their runs, or null when they have
    /// given none. The one method that opens it, called only when a job is handed out, so the
    /// token leaves Dev flow only inside a job's answer to its own user's worker. It opens through
    /// the door as the run's user, so row level security shows no one else's, and the vault's
    /// binding to the user it was sealed for refuses a token moved to another. It opens an expired
    /// login too; handing out work checks <see cref="Status"/> first and hands out nothing past the
    /// renewal, see <c>Jobs.Dispatcher.HeldFor</c>, and the worker checks the login before every run.
    /// </summary>
    /// <exception cref="VaultException">The token does not open as this user's: it was moved
    /// from another user, or changed.</exception>
    public async Task<ClaudeToken?> ForRun(Guid runUserId, CancellationToken cancellationToken)
    {
        var sealedToken = await database.InUserTransaction(runUserId, async transaction =>
        {
            var given = await transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM claude_logins WHERE user_id = @runUserId)",
                new { runUserId },
                transaction,
                cancellationToken: cancellationToken));
            return given ? await vault.Get(transaction, runUserId, Purpose, cancellationToken) : null;
        }, cancellationToken);
        return sealedToken is null ? null : new ClaudeToken(Encoding.UTF8.GetString(sealedToken));
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
    [GeneratedRegex(@"^sk-ant-oat[\x21-\x7E]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SetupToken();
}