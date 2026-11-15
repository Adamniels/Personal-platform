namespace DevFlow.CodeHost;

/// <summary>
/// Signing a person in at the code host, so Dev flow holds a token that acts as them there: the
/// address to send the browser to, exchanging the code it comes back with for tokens, renewing
/// them, and who a token belongs to. The OAuth web flow with PKCE, as GitHub documents it, named
/// as the development flow's design does. Beside <see cref="ICodeHost"/> rather than on it, since
/// its reads are as the app and these are as a person; both implementations of one implement the
/// other. Nothing here is stored: the caller keeps the tokens, in the vault. See "Acting as the
/// owner" in devflow/docs/devflow-architecture.md.
/// </summary>
/// <remarks>
/// Every operation takes the platform account it is asked for, as <see cref="ICodeHost"/>'s do:
/// the fake keeps its sign ins per account, and GitHub ignores it. None of them prints a code, a
/// verifier, a state or a token.
/// </remarks>
public interface ICodeHostSignIn
{
    /// <summary>Where to send the browser to ask the person: the host's own page, carrying
    /// <paramref name="state"/>, the S256 <paramref name="challenge"/> and
    /// <paramref name="redirect"/>, where the host sends the browser back.</summary>
    Uri AuthorizeAddress(Guid userId, string state, string challenge, Uri redirect);

    /// <summary>The tokens for the <paramref name="code"/> the host sent the browser back with,
    /// proved by the <paramref name="verifier"/> the challenge was made from. Refused when the
    /// host does not take the code, the verifier or the redirect.</summary>
    Task<SignInResult<UserTokenPair>> Exchange(Guid userId, string code, string verifier, Uri redirect, CancellationToken cancellationToken);

    /// <summary>New tokens for a <paramref name="refreshToken"/>, which the host takes once: after
    /// it, that refresh token and the access token before it no longer work. Refused when the host
    /// does not take it.</summary>
    Task<SignInResult<UserTokenPair>> Renew(Guid userId, string refreshToken, CancellationToken cancellationToken);

    /// <summary>The account <paramref name="accessToken"/> acts as. Refused when the host does not
    /// take the token.</summary>
    Task<SignInResult<CodeHostAccount>> WhoIs(Guid userId, string accessToken, CancellationToken cancellationToken);
}

/// <summary>A person's two tokens at the code host, with when each stops working.</summary>
/// <param name="AccessToken">What acts as them, for a few hours.</param>
/// <param name="AccessExpiresAt">When the access token stops working.</param>
/// <param name="RefreshToken">What renews both, once.</param>
/// <param name="RefreshExpiresAt">When the refresh token stops working, and the person must link
/// again.</param>
public sealed record UserTokenPair(string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt)
{
    /// <summary>When each expires, and never a token.</summary>
    public override string ToString() => $"UserTokenPair {{ AccessExpiresAt = {AccessExpiresAt:O}, RefreshExpiresAt = {RefreshExpiresAt:O} }}";
}

/// <summary>An account on the code host: its login, which can change, and its id, which does
/// not.</summary>
public sealed record CodeHostAccount(string Login, long Id);

/// <summary>
/// What a sign in operation answers: <see cref="Granted"/> with the value, <see cref="Refused"/>
/// when the host does not take what it was given, or <see cref="Unavailable"/> when it did not
/// answer. The difference matters: a refused renewal ends a link, and an unavailable one changes
/// nothing. A closed set, as <see cref="CodeHostResult{T}"/> is.
/// </summary>
public abstract record SignInResult<T>
{
    private SignInResult()
    {
    }

    /// <summary>The value asked for.</summary>
    public sealed record Granted(T Value) : SignInResult<T>;

    /// <summary>The host answered, and does not take the code, the verifier, the refresh token or
    /// the access token: it is wrong, used, expired or revoked. Trying again does not change
    /// it.</summary>
    public sealed record Refused : SignInResult<T>;

    /// <summary>The host did not answer, or answered that it cannot now. <paramref name="RetryAt"/>
    /// is when to try again, when it said.</summary>
    public sealed record Unavailable(DateTimeOffset? RetryAt) : SignInResult<T>;
}