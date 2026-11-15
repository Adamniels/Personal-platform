using System.Net;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// Signing a person in to GitHub as Dev flow's app, <see cref="ICodeHostSignIn"/>: GitHub's web
/// flow with PKCE, exactly as it documents it, with expiring user tokens. The authorize address
/// and the token address are on github.com, and who a token belongs to is <c>GET /user</c> on the
/// API. A user token only has the permissions both the person and the app have, and what it does
/// is recorded on GitHub as done by the person. See "Acting as the owner" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
/// <remarks>
/// GitHub answers a token request it refuses with <c>200</c> and an <c>error</c> field. Most such
/// errors are about what was sent, a code, a verifier or a refresh token, and are refused. The
/// two that say the app's own settings are wrong, a client secret or a callback address, are
/// unavailable instead, since ending a person's link would not fix them; so is any answer that is
/// not a success, since an answer GitHub never documented should not end one either.
/// </remarks>
public sealed partial class GitHubCodeHost
{
    /// <summary>GitHub's page that asks the person, relative to <see cref="GitHubHttp.WebAddress"/>.</summary>
    public static readonly Uri AuthorizeAddressBase = new(GitHubHttp.WebAddress, "login/oauth/authorize");

    /// <summary>Where a code or a refresh token is exchanged for tokens.</summary>
    public static readonly Uri TokenAddress = new(GitHubHttp.WebAddress, "login/oauth/access_token");

    /// <summary>The errors that say the app is set up wrongly rather than that what was sent is
    /// wrong, which are unavailable rather than refused.</summary>
    private static readonly HashSet<string> SettingsErrors = new(StringComparer.Ordinal)
    {
        "incorrect_client_credentials",
        "redirect_uri_mismatch",
    };

    public Uri AuthorizeAddress(Guid userId, string state, string challenge, Uri redirect)
    {
        ArgumentNullException.ThrowIfNull(redirect);
        var query = QueryString.Create(new KeyValuePair<string, string?>[]
        {
            new("client_id", _settings.ClientId),
            new("redirect_uri", redirect.AbsoluteUri),
            new("state", state),
            new("code_challenge", challenge),
            new("code_challenge_method", Pkce.Method),
        });
        return new Uri(AuthorizeAddressBase + query.ToUriComponent());
    }

    public Task<SignInResult<UserTokenPair>> Exchange(Guid userId, string code, string verifier, Uri redirect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redirect);
        return Tokens(
        [
            new("client_id", _settings.ClientId),
            new("client_secret", _settings.ClientSecret),
            new("code", code),
            new("redirect_uri", redirect.AbsoluteUri),
            new("code_verifier", verifier),
        ], cancellationToken);
    }

    public Task<SignInResult<UserTokenPair>> Renew(Guid userId, string refreshToken, CancellationToken cancellationToken) =>
        Tokens(
        [
            new("client_id", _settings.ClientId),
            new("client_secret", _settings.ClientSecret),
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
        ], cancellationToken);

    public async Task<SignInResult<CodeHostAccount>> WhoIs(Guid userId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = GitHubHttp.Request(HttpMethod.Get, new Uri(GitHubHttp.BaseAddress, "user"), GitHubHttp.JsonMediaType);
        request.Headers.Authorization = new("Bearer", accessToken);
        var answer = await GitHubHttp.Send(_factory, request, cancellationToken);
        if (answer is null)
        {
            return new SignInResult<CodeHostAccount>.Unavailable(RetryAt: null);
        }

        switch (answer.Status)
        {
            case HttpStatusCode.OK:
                return Parse<UserIdAnswer>(answer.Body) is { Login.Length: > 0 } user
                    ? new SignInResult<CodeHostAccount>.Granted(new CodeHostAccount(user.Login, user.Id))
                    : new SignInResult<CodeHostAccount>.Unavailable(RetryAt: null);
            case HttpStatusCode.Unauthorized:
                // GitHub does not take the token: expired or revoked.
                return new SignInResult<CodeHostAccount>.Refused();
            case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                return new SignInResult<CodeHostAccount>.Unavailable(GitHubHttp.RetryAt(answer.Headers, _time.GetUtcNow()));
            default:
                return new SignInResult<CodeHostAccount>.Unavailable(RetryAt: null);
        }
    }

    /// <summary>
    /// A <c>POST</c> to <see cref="TokenAddress"/> with <paramref name="fields"/> as a form, asking
    /// for JSON, and its answer: the two tokens with when each expires, counted from the clock's
    /// now; refused for an error about what was sent; unavailable otherwise, an answer without
    /// expiring tokens among them, which an app without "Expire user authorization tokens" gives.
    /// </summary>
    private async Task<SignInResult<UserTokenPair>> Tokens(KeyValuePair<string, string>[] fields, CancellationToken cancellationToken)
    {
        using var request = GitHubHttp.Request(HttpMethod.Post, TokenAddress, GitHubHttp.PlainJsonMediaType);
        request.Content = new FormUrlEncodedContent(fields);
        var answer = await GitHubHttp.Send(_factory, request, cancellationToken);
        if (answer is null)
        {
            return new SignInResult<UserTokenPair>.Unavailable(RetryAt: null);
        }

        if (answer.Status != HttpStatusCode.OK)
        {
            return new SignInResult<UserTokenPair>.Unavailable(
                answer.Status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ? GitHubHttp.RetryAt(answer.Headers, _time.GetUtcNow()) : null);
        }

        var tokens = Parse<TokenAnswer>(answer.Body);
        if (tokens?.Error is { } error)
        {
            return SettingsErrors.Contains(error)
                ? new SignInResult<UserTokenPair>.Unavailable(RetryAt: null)
                : new SignInResult<UserTokenPair>.Refused();
        }

        if (tokens is not
            {
                AccessToken.Length: > 0,
                ExpiresIn: > 0,
                RefreshToken.Length: > 0,
                RefreshTokenExpiresIn: > 0,
                TokenType: { } type,
            }
            || !string.Equals(type, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            return new SignInResult<UserTokenPair>.Unavailable(RetryAt: null);
        }

        var now = _time.GetUtcNow();
        return new SignInResult<UserTokenPair>.Granted(new UserTokenPair(
            tokens.AccessToken,
            now.AddSeconds(tokens.ExpiresIn.Value),
            tokens.RefreshToken,
            now.AddSeconds(tokens.RefreshTokenExpiresIn.Value)));
    }

    /// <summary>What the token address answers, a success's fields or an error's, so each is
    /// optional here, with a default the strict options leave alone, and checked by
    /// <see cref="Tokens"/>. Prints no token.</summary>
    private sealed record TokenAnswer(
        string? AccessToken = null,
        long? ExpiresIn = null,
        string? RefreshToken = null,
        long? RefreshTokenExpiresIn = null,
        string? TokenType = null,
        string? Error = null)
    {
        public override string ToString() => $"TokenAnswer {{ ExpiresIn = {ExpiresIn}, RefreshTokenExpiresIn = {RefreshTokenExpiresIn}, Error = {Error} }}";
    }

    /// <summary>Who <c>GET /user</c> says the token is, the fields read.</summary>
    private sealed record UserIdAnswer(string Login, long Id);
}