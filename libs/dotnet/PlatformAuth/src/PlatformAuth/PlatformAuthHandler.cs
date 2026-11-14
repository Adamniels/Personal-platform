using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSec.Cryptography;

namespace PlatformAuth;

/// <summary>How a scheme answers a request without a valid token.</summary>
internal sealed class PlatformAuthSchemeOptions : AuthenticationSchemeOptions
{
    public bool RedirectToRefresh { get; set; }
}

/// <summary>
/// Who is asking, at a service's edge: the token read from the request, verified against the
/// core's keys, and what a request without a valid one gets. The rules are in "How a feature
/// authenticates" in <c>High-level/docs/feature-contract.md</c>; the C# twin of platform-auth's
/// <c>auth.py</c>. Registered twice by
/// <see cref="PlatformAuthServiceCollectionExtensions.AddPlatformAuth"/>, once per policy, the two
/// differing only in their challenge.
/// </summary>
internal sealed partial class PlatformAuthHandler(
    IOptionsMonitor<PlatformAuthSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    KeyCache keys,
    TimeProvider time)
    : AuthenticationHandler<PlatformAuthSchemeOptions>(options, logger, encoder)
{
    /// <summary>Set by the core at login and refresh, see "The session" in <c>Core/docs/core-architecture.md</c>.</summary>
    public const string AccessCookie = "__Host-access";

    /// <summary>The core's deciding endpoint. Relative, since every service sits on the platform's one origin.</summary>
    public const string RefreshPath = "/auth/refresh";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryReadToken(Request, out var token))
        {
            return AuthenticateResult.Fail("unreadable Authorization header");
        }

        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        // Reading the kid here only picks which key to fetch, so a token with no usable one costs
        // no key set request. Verify makes the same check and is the one that decides.
        if (TokenVerifier.ReadKeyId(token) is not { } keyId)
        {
            return AuthenticateResult.Fail("token refused");
        }

        // The time is read once here, at the edge, and passed down.
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var key = await keys.FindAsync(keyId, Context.RequestAborted).ConfigureAwait(false);
        var known = key is null
            ? new Dictionary<string, PublicKey>()
            : new Dictionary<string, PublicKey> { [keyId] = key };

        Claims claims;
        try
        {
            claims = TokenVerifier.Verify(token, known, now);
        }
        catch (Refused refused)
        {
            LogRefused(Logger, refused.Reason);
            return AuthenticateResult.Fail(refused);
        }

        Context.Features.Set<IPlatformToken>(new PlatformToken(token, claims));
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", claims.AccountId.ToString("D")),
                new Claim("sid", claims.FamilyId.ToString("D")),
                new Claim("jti", claims.TokenId.ToString("D")),
                new Claim("iat", claims.IssuedAt.ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
                new Claim("exp", claims.ExpiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
                new Claim("kid", claims.KeyId),
            ],
            Scheme.Name,
            nameType: "sub",
            roleType: null);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Options.RedirectToRefresh)
        {
            Response.StatusCode = StatusCodes.Status303SeeOther;
            Response.Headers.Location = RefreshLocation(Context);
        }
        else
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The token the request carries in <paramref name="token"/>, null when it carries none.
    /// False when it carries one that could not be valid. An <c>Authorization</c> header, when
    /// there is one, is the only place looked: a header that is present but malformed is a
    /// refusal, never a reason to fall back to the cookie, and so are two of them.
    /// </summary>
    internal static bool TryReadToken(HttpRequest request, out string? token)
    {
        token = null;
        var headers = request.Headers.Authorization;
        if (headers.Count > 0)
        {
            if (headers.Count != 1 || headers[0] is not { } header)
            {
                return false;
            }

            var space = header.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0
                || !header.AsSpan(0, space).Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                || header.Length == space + 1
                || header.IndexOf(' ', space + 1) >= 0)
            {
                return false;
            }

            token = header[(space + 1)..];
            return true;
        }

        token = request.Cookies[AccessCookie] is { Length: > 0 } cookie ? cookie : null;
        return true;
    }

    /// <summary>
    /// <c>/auth/refresh</c> with the path and query exactly as the browser sent them, encoded once
    /// more as <c>return_to</c>. The service does not judge the path; the core checks it once,
    /// after decoding.
    /// </summary>
    internal static string RefreshLocation(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw) || raw[0] != '/')
        {
            // A server that keeps no raw target, or one in absolute form: rebuilt from the parts,
            // each in the escaped form it arrived in.
            var request = context.Request;
            raw = request.PathBase.ToUriComponent() + request.Path.ToUriComponent() + request.QueryString.ToUriComponent();
        }

        return $"{RefreshPath}?return_to={Uri.EscapeDataString(raw)}";
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Token refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, RefusalReason reason);
}