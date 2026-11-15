using System.Collections.Concurrent;

namespace DevFlow.CodeHost;

/// <summary>
/// The fake's half of signing in, <see cref="ICodeHostSignIn"/>, kept per account beside its world
/// and apart from it, so staging a world again does not sign anyone out. Its page,
/// <c>/devflow/fake-github/authorize</c>, grants a code through <see cref="Grant"/>; its tokens are
/// random and last as long as GitHub's, and a refresh token is taken once, as GitHub takes it, so
/// a test of renewal means something. When the account is set unavailable, every operation here
/// is too. See "The fake" in devflow/README.md.
/// </summary>
public sealed partial class FakeCodeHost
{
    /// <summary>The fake's client id, which its authorize address carries as GitHub's does.</summary>
    public const string ClientId = "fake-devflow";

    /// <summary>The fake's sign in page, under Dev flow, mapped only in the fake mode.</summary>
    public const string AuthorizePath = "/devflow/fake-github/authorize";

    /// <summary>How long a code may wait to be exchanged, GitHub's ten minutes.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long an access token works, GitHub's eight hours.</summary>
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromSeconds(28_800);

    /// <summary>How long a refresh token works, GitHub's six months.</summary>
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromSeconds(15_897_600);

    private readonly ConcurrentDictionary<Guid, SignIns> _signIns = new();

    /// <summary>Who the account is at the fake, from its world.</summary>
    public FakeUser SignedInUser(Guid userId) => World(userId).SignedInUser;

    /// <summary>How many renewals the fake has taken for the account, refused ones included.</summary>
    public int Renewals(Guid userId) => Of(userId, signIns => signIns.Renewals);

    public Uri AuthorizeAddress(Guid userId, string state, string challenge, Uri redirect)
    {
        ArgumentNullException.ThrowIfNull(redirect);
        var query = QueryString.Create(new KeyValuePair<string, string?>[]
        {
            new("client_id", ClientId),
            new("redirect_uri", redirect.AbsoluteUri),
            new("state", state),
            new("code_challenge", challenge),
            new("code_challenge_method", Pkce.Method),
        });
        return new Uri(redirect, AuthorizePath + query.ToUriComponent());
    }

    /// <summary>
    /// A code for the account, as the person pressing Authorize on the fake's page gives one: bound
    /// to the <paramref name="challenge"/> and the <paramref name="redirect"/>, taken once, within
    /// <see cref="CodeLifetime"/>.
    /// </summary>
    public string Grant(Guid userId, string challenge, Uri redirect)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(redirect);
        var code = Pkce.Random(20);
        Of(userId, signIns => signIns.Codes[code] = new FakeCode(challenge, redirect, _time.GetUtcNow() + CodeLifetime));
        return code;
    }

    public Task<SignInResult<UserTokenPair>> Exchange(Guid userId, string code, string verifier, Uri redirect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(redirect);
        return Answer<UserTokenPair>(userId, signIns =>
        {
            // Taken once, whether it is then accepted or not, as GitHub takes a code.
            if (!signIns.Codes.Remove(code, out var granted)
                || _time.GetUtcNow() >= granted.ExpiresAt
                || granted.Redirect != redirect
                || !Pkce.Matches(granted.Challenge, verifier))
            {
                return new SignInResult<UserTokenPair>.Refused();
            }

            return new SignInResult<UserTokenPair>.Granted(Issue(userId, signIns));
        });
    }

    public Task<SignInResult<UserTokenPair>> Renew(Guid userId, string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        return Answer<UserTokenPair>(userId, signIns =>
        {
            signIns.Renewals++;
            if (signIns.Current is not { } current
                || current.RefreshToken != refreshToken
                || _time.GetUtcNow() >= current.RefreshExpiresAt)
            {
                return new SignInResult<UserTokenPair>.Refused();
            }

            // The old pair stops working as the new one is made.
            return new SignInResult<UserTokenPair>.Granted(Issue(userId, signIns));
        });
    }

    public Task<SignInResult<CodeHostAccount>> WhoIs(Guid userId, string accessToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        return Answer<CodeHostAccount>(userId, signIns =>
            signIns.Current is { } current && current.AccessToken == accessToken && _time.GetUtcNow() < current.AccessExpiresAt
                ? new SignInResult<CodeHostAccount>.Granted(new CodeHostAccount(signIns.User!.Login, signIns.User.Id))
                : new SignInResult<CodeHostAccount>.Refused());
    }

    /// <summary>Ends the account's current access token now, for a test of what follows; its
    /// refresh token still works.</summary>
    public void ExpireAccessToken(Guid userId) => Of(userId, signIns =>
    {
        if (signIns.Current is { } current)
        {
            signIns.Current = current with { AccessExpiresAt = _time.GetUtcNow() };
        }

        return 0;
    });

    /// <summary>A new pair for the account's user as the world names them now, replacing any
    /// before it. Called under the account's lock.</summary>
    private UserTokenPair Issue(Guid userId, SignIns signIns)
    {
        var now = _time.GetUtcNow();
        var pair = new UserTokenPair("fake_ghu_" + Pkce.Random(27), now + AccessLifetime, "fake_ghr_" + Pkce.Random(54), now + RefreshLifetime);
        signIns.Current = pair;
        signIns.User = SignedInUser(userId);
        return pair;
    }

    /// <summary>Unavailable when the account is set so; otherwise <paramref name="act"/> under the
    /// account's lock, so a code or a refresh token is taken once however many ask at once.</summary>
    private Task<SignInResult<T>> Answer<T>(Guid userId, Func<SignIns, SignInResult<T>> act) =>
        Task.FromResult(Of(userId).Unavailable
            ? new SignInResult<T>.Unavailable(RetryAt: null)
            : Of(userId, act));

    private T Of<T>(Guid userId, Func<SignIns, T> act)
    {
        var signIns = _signIns.GetOrAdd(userId, _ => new SignIns());
        lock (signIns)
        {
            return act(signIns);
        }
    }

    /// <summary>A code granted and not yet exchanged.</summary>
    private sealed record FakeCode(string Challenge, Uri Redirect, DateTimeOffset ExpiresAt);

    /// <summary>One account's sign ins: the codes waiting, the pair that works now and whom it acts
    /// as, and how many renewals were asked for. Changed only under its own lock.</summary>
    private sealed class SignIns
    {
        public Dictionary<string, FakeCode> Codes { get; } = new(StringComparer.Ordinal);

        public UserTokenPair? Current { get; set; }

        public FakeUser? User { get; set; }

        public int Renewals { get; set; }
    }
}