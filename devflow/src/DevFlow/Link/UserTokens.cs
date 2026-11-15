using System.Text;

using Dapper;

using DevFlow.CodeHost;

using Npgsql;

using PlatformData;

namespace DevFlow.Link;

/// <summary>
/// What <see cref="UserTokens.For"/> answers: a token to act as the user with, or why there is
/// none, which the caller shows. A closed set.
/// </summary>
public abstract record UserToken
{
    private UserToken()
    {
    }

    /// <summary>An access token with more than <see cref="UserTokens.Margin"/> left.</summary>
    public sealed record Usable(string AccessToken) : UserToken
    {
        /// <summary>Never the token.</summary>
        public override string ToString() => "Usable { }";
    }

    /// <summary>The user has not linked GitHub.</summary>
    public sealed record NotLinked : UserToken;

    /// <summary>GitHub refused to renew the link, and the user links again.</summary>
    public sealed record NeedsRelink : UserToken;

    /// <summary>The token needed renewing and the code host did not answer; nothing changed, and a
    /// later call tries again. <paramref name="RetryAt"/> is when, when the host said.</summary>
    public sealed record Unavailable(DateTimeOffset? RetryAt) : UserToken;
}

/// <summary>
/// The one way to a token that acts as a user on the code host, which every later use, merging and
/// commenting among them, goes through. Inside one door transaction it locks the user's account
/// row with <c>SELECT ... FOR UPDATE</c>; with more than <see cref="Margin"/> left the access token
/// is returned, and otherwise both tokens are renewed and stored before the new one is returned.
/// The lock is what keeps two requests from spending one single use refresh token: the second
/// waits, then finds the first's new token fresh. A renewal the host refuses marks the link
/// <c>needs_relink</c> and deletes both tokens; a host that is unavailable changes nothing. See
/// "Acting as the owner" in devflow/docs/devflow-architecture.md.
/// </summary>
/// <remarks>The renewal is a request to the code host inside the transaction, so the row stays
/// locked for at most the adapter's timeout, which only that user's own requests wait on.</remarks>
public sealed class UserTokens(UserDb database, Vault vault, ICodeHostSignIn host, TimeProvider time)
{
    /// <summary>How much of an access token's life must be left to use it, so a request that starts
    /// with it ends before GitHub stops taking it.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    public Task<UserToken> For(Guid userId, CancellationToken cancellationToken) =>
        database.InUserTransaction(userId, async transaction =>
        {
            var account = await transaction.Connection!.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
                "SELECT state AS State, access_expires_at AS AccessExpiresAt, refresh_expires_at AS RefreshExpiresAt FROM github_accounts WHERE user_id = @userId FOR UPDATE",
                new { userId },
                transaction,
                cancellationToken: cancellationToken));
            if (account is null)
            {
                return new UserToken.NotLinked();
            }

            if (account.State != "linked")
            {
                return new UserToken.NeedsRelink();
            }

            var now = time.GetUtcNow();
            if (GitHubLink.Utc(account.AccessExpiresAt) - now > Margin
                && await Read(transaction, userId, GitHubLink.AccessPurpose, cancellationToken) is { } access)
            {
                return new UserToken.Usable(access);
            }

            // A refresh token already past its end, or missing, is one GitHub would refuse.
            if (now >= GitHubLink.Utc(account.RefreshExpiresAt)
                || await Read(transaction, userId, GitHubLink.RefreshPurpose, cancellationToken) is not { } refresh)
            {
                return await NeedsRelink(transaction, userId, cancellationToken);
            }

            switch (await host.Renew(userId, refresh, cancellationToken))
            {
                case SignInResult<UserTokenPair>.Granted { Value: var tokens }:
                    await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                        "UPDATE github_accounts SET access_expires_at = @accessExpiresAt, refresh_expires_at = @refreshExpiresAt WHERE user_id = @userId",
                        new { userId, accessExpiresAt = tokens.AccessExpiresAt, refreshExpiresAt = tokens.RefreshExpiresAt },
                        transaction,
                        cancellationToken: cancellationToken));
                    await GitHubLink.PutTokens(vault, transaction, userId, tokens, cancellationToken);
                    return new UserToken.Usable(tokens.AccessToken);
                case SignInResult<UserTokenPair>.Refused:
                    return await NeedsRelink(transaction, userId, cancellationToken);
                case SignInResult<UserTokenPair>.Unavailable { RetryAt: var retryAt }:
                    return new UserToken.Unavailable(retryAt);
                default:
                    throw new InvalidOperationException("a sign in result that is not one of the three");
            }
        }, cancellationToken);

    /// <summary>Marks the link <c>needs_relink</c> and deletes both tokens, which no longer work.</summary>
    private async Task<UserToken> NeedsRelink(NpgsqlTransaction transaction, Guid userId, CancellationToken cancellationToken)
    {
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE github_accounts SET state = 'needs_relink' WHERE user_id = @userId",
            new { userId },
            transaction,
            cancellationToken: cancellationToken));
        await GitHubLink.DeleteTokens(vault, transaction, userId, cancellationToken);
        return new UserToken.NeedsRelink();
    }

    private async Task<string?> Read(NpgsqlTransaction transaction, Guid userId, string purpose, CancellationToken cancellationToken) =>
        await vault.Get(transaction, userId, purpose, cancellationToken) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    private sealed record AccountRow(string State, DateTime AccessExpiresAt, DateTime RefreshExpiresAt);
}