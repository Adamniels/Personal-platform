using System.Security.Cryptography;
using System.Text;

using Dapper;

using DevFlow.CodeHost;

using Npgsql;

using PlatformData;

namespace DevFlow.Link;

/// <summary>
/// Linking a user's GitHub account: starting it, which remembers a single use state and a PKCE
/// verifier and gives the address to send the browser to; the return from the code host, which
/// spends the state, exchanges the code and stores the account with its two tokens sealed in the
/// vault; and unlinking. Every query goes through the door as the user, so a
/// state, an account or a token is only ever touched as its own user. Getting a usable token is
/// <see cref="UserTokens"/>. See "Linking GitHub" in devflow/README.md.
/// </summary>
/// <remarks>
/// Nothing here prints a code, a verifier, a state or a token: they are not logged, not put in an
/// exception's message, and the state is stored only as its SHA-256.
/// </remarks>
public sealed class GitHubLink(UserDb database, Vault vault, ICodeHostSignIn host, TimeProvider time, Uri redirect)
{
    /// <summary>The vault's purpose for the access token.</summary>
    public const string AccessPurpose = "github.access";

    /// <summary>The vault's purpose for the refresh token.</summary>
    public const string RefreshPurpose = "github.refresh";

    /// <summary>The return from the code host, under Dev flow, which the redirect address names.</summary>
    public const string CallbackPath = "/devflow/github/callback";

    /// <summary>How long a started link may wait for its return.</summary>
    public static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Where the code host sends the browser back:
    /// <c>&lt;DEVFLOW_PUBLIC_ORIGIN&gt;/devflow/github/callback</c>.</summary>
    public Uri Redirect { get; } = redirect ?? throw new ArgumentNullException(nameof(redirect));

    /// <summary>The redirect address for the platform at <paramref name="publicOrigin"/>.</summary>
    public static Uri RedirectFor(string publicOrigin) => new(publicOrigin + CallbackPath);

    /// <summary>
    /// Starts a link: a random state and a random PKCE verifier, each 32 bytes in base64url, the
    /// state's SHA-256 stored with the verifier and the time, and the code host's address carrying
    /// the state and the verifier's S256 challenge. The user's own sign ins older than
    /// <see cref="SignInLifetime"/> are removed on the way, since nothing else would.
    /// </summary>
    public async Task<Uri> Start(Guid userId, CancellationToken cancellationToken)
    {
        var state = Pkce.Random();
        var verifier = Pkce.Random();
        var now = time.GetUtcNow();
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM github_sign_ins WHERE user_id = @userId AND created_at < @stale;
            INSERT INTO github_sign_ins (user_id, state_hash, verifier, created_at) VALUES (@userId, @stateHash, @verifier, @now);
            """,
            new { userId, stale = now - SignInLifetime, stateHash = Hash(state), verifier, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return host.AuthorizeAddress(userId, state, Pkce.Challenge(verifier), Redirect);
    }

    /// <summary>
    /// The return from the code host. The state is looked up among this user's sign ins and
    /// deleted, so it is spent whatever follows; one that is missing, already spent or older than
    /// <see cref="SignInLifetime"/>, a return carrying the code host's own <paramref name="error"/>,
    /// or one without a code, is refused. Otherwise the code is exchanged with the stored verifier,
    /// the code host is asked who the token belongs to, and the account is stored as linked, with
    /// both tokens in the vault. Any refusal stores nothing.
    /// </summary>
    /// <returns>Whether the account is now linked.</returns>
    public async Task<bool> Finish(Guid userId, string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(state))
        {
            return false;
        }

        // Spent in a transaction of its own, so a state is taken once even when the exchange
        // after it fails, and no transaction is held while the code host answers.
        var signIn = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<SignIn>(new CommandDefinition(
            "DELETE FROM github_sign_ins WHERE user_id = @userId AND state_hash = @stateHash RETURNING verifier AS Verifier, created_at AS CreatedAt",
            new { userId, stateHash = Hash(state) },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        if (signIn is null
            || time.GetUtcNow() > Utc(signIn.CreatedAt) + SignInLifetime
            || !string.IsNullOrEmpty(error)
            || string.IsNullOrEmpty(code))
        {
            return false;
        }

        if (await host.Exchange(userId, code, signIn.Verifier, Redirect, cancellationToken) is not SignInResult<UserTokenPair>.Granted { Value: var tokens })
        {
            return false;
        }

        if (await host.WhoIs(userId, tokens.AccessToken, cancellationToken) is not SignInResult<CodeHostAccount>.Granted { Value: var account })
        {
            return false;
        }

        var now = time.GetUtcNow();
        await database.InUserTransaction(userId, async transaction =>
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO github_accounts (user_id, login, github_user_id, state, access_expires_at, refresh_expires_at, linked_at)
                VALUES (@userId, @login, @githubUserId, 'linked', @accessExpiresAt, @refreshExpiresAt, @now)
                ON CONFLICT (user_id) DO UPDATE
                SET login = excluded.login, github_user_id = excluded.github_user_id, state = excluded.state,
                    access_expires_at = excluded.access_expires_at, refresh_expires_at = excluded.refresh_expires_at,
                    linked_at = excluded.linked_at
                """,
                new
                {
                    userId,
                    login = account.Login,
                    githubUserId = account.Id,
                    accessExpiresAt = tokens.AccessExpiresAt,
                    refreshExpiresAt = tokens.RefreshExpiresAt,
                    now,
                },
                transaction,
                cancellationToken: cancellationToken));
            await PutTokens(vault, transaction, userId, tokens, cancellationToken);
            return 0;
        }, cancellationToken);
        return true;
    }

    /// <summary>Removes the user's account, its two tokens and any sign ins. Nothing is revoked at
    /// the code host: the owner does that on GitHub, see "Registering the GitHub App" in
    /// devflow/README.md.</summary>
    public Task Unlink(Guid userId, CancellationToken cancellationToken) =>
        database.InUserTransaction(userId, async transaction =>
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM github_accounts WHERE user_id = @userId;
                DELETE FROM github_sign_ins WHERE user_id = @userId;
                """,
                new { userId },
                transaction,
                cancellationToken: cancellationToken));
            await DeleteTokens(vault, transaction, userId, cancellationToken);
            return 0;
        }, cancellationToken);

    /// <summary>Both tokens into the vault, replacing those before them.</summary>
    internal static async Task PutTokens(Vault vault, NpgsqlTransaction transaction, Guid userId, UserTokenPair tokens, CancellationToken cancellationToken)
    {
        await vault.Put(transaction, userId, AccessPurpose, Encoding.UTF8.GetBytes(tokens.AccessToken), cancellationToken);
        await vault.Put(transaction, userId, RefreshPurpose, Encoding.UTF8.GetBytes(tokens.RefreshToken), cancellationToken);
    }

    /// <summary>Both tokens out of the vault.</summary>
    internal static async Task DeleteTokens(Vault vault, NpgsqlTransaction transaction, Guid userId, CancellationToken cancellationToken)
    {
        await vault.Delete(transaction, userId, AccessPurpose, cancellationToken);
        await vault.Delete(transaction, userId, RefreshPurpose, cancellationToken);
    }

    /// <summary>A <c>timestamptz</c> as Npgsql reads it, a UTC <see cref="DateTime"/>, as a time
    /// to compare with the clock's.</summary>
    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>What is stored of a state: its SHA-256, so the table never holds one that could be
    /// sent back.</summary>
    private static byte[] Hash(string state) => SHA256.HashData(Encoding.UTF8.GetBytes(state));

    /// <summary>A sign in as stored. The verifier is never printed.</summary>
    private sealed record SignIn(string Verifier, DateTime CreatedAt)
    {
        public override string ToString() => $"SignIn {{ CreatedAt = {CreatedAt:O} }}";
    }
}