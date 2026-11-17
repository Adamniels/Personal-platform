using System.Security.Cryptography;

namespace DevFlow.CodeHost;

/// <summary>
/// The fake's keys for the app's writes: random, an hour long, and recorded per account, so a
/// write can be taken only with one the fake issued for that account and repository. See "The
/// fake" in devflow/README.md.
/// </summary>
public sealed partial class FakeCodeHost
{
    /// <summary>How long a key the fake issues lasts, as GitHub's installation tokens do.</summary>
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(1);

    /// <summary>A key the fake issued: the repository and purpose it is for, and when it ends.</summary>
    private sealed record IssuedKey(long Installation, long Repository, WritePurpose Purpose, DateTimeOffset ExpiresAt);

    /// <summary>Unavailable when the account says so, not found for a repository the world does
    /// not hold, and otherwise a new key, recorded for the account and never given again.</summary>
    public Task<CodeHostResult<AppWriteToken>> ForWrite(Guid userId, Repository repository, WritePurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        lock (_changing)
        {
            var account = Of(userId);
            CodeHostResult<AppWriteToken> result;
            if (account.Unavailable)
            {
                result = new CodeHostResult<AppWriteToken>.Unavailable(RetryAt: null);
            }
            else if (Find(account.World, repository) is null)
            {
                result = new CodeHostResult<AppWriteToken>.NotFound();
            }
            else
            {
                var value = $"fake_app_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20))}";
                var expiresAt = _time.GetUtcNow() + KeyLifetime;
                _accounts[userId] = account with
                {
                    Keys = account.Keys.SetItem(value, new IssuedKey(repository.Installation, repository.Id, purpose, expiresAt)),
                };
                result = new CodeHostResult<AppWriteToken>.Found(new AppWriteToken(value, repository, purpose, expiresAt));
            }

            return Task.FromResult(result);
        }
    }
}