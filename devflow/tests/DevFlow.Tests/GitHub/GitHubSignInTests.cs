using System.Net;

using DevFlow.CodeHost;
using DevFlow.CodeHost.GitHub;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The GitHub adapter's sign in half, against GitHub's documented form: the authorize address, the
/// two exchanges at the token address, and who a token belongs to, each answered from
/// contracts/github/, written by hand from GitHub's documented shapes since a real one cannot be
/// recorded without a real authorization. See "Acting as the owner" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class GitHubSignInTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Code = "a1b2c3d4e5f6a7b8c9d0";
    private static readonly Uri Redirect = new("https://platform.test/devflow/github/callback");

    private static ICodeHostSignIn SignIn(GitHubHost github) => (ICodeHostSignIn)github.CodeHost;

    private static string AccessToken(string fixture) => (string)Fixture.Node(fixture)["access_token"]!;

    private static string RefreshToken(string fixture) => (string)Fixture.Node(fixture)["refresh_token"]!;

    [Fact]
    public async Task TheAuthorizeAddressAndTheTwoExchangesMatchGitHubsDocumentedForm()
    {
        using var github = new GitHubHost();
        var signIn = SignIn(github);
        var now = github.Time.GetUtcNow();

        // The authorize address: GitHub's page, with exactly the five parameters.
        var authorize = signIn.AuthorizeAddress(GitHubHost.User, "the-state", Pkce.Challenge(Verifier), Redirect);
        Assert.Equal("https://github.com/login/oauth/authorize", authorize.GetLeftPart(UriPartial.Path));
        var query = new Received(HttpMethod.Get, authorize, new Dictionary<string, string>(), null).Query;
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["client_id"] = TestApp.ClientId,
                ["redirect_uri"] = Redirect.AbsoluteUri,
                ["state"] = "the-state",
                ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                ["code_challenge_method"] = "S256",
            },
            query);

        // Exchanging the code: a POST of the form, asking for JSON.
        github.Stub.Answer = _ => StubGitHub.Json(Fixture.Text("oauth-token.json"));
        var exchanged = Assert.IsType<SignInResult<UserTokenPair>.Granted>(await signIn.Exchange(GitHubHost.User, Code, Verifier, Redirect, GitHubHost.Ct));
        var exchange = Assert.Single(github.Stub.Requests);
        AssertTokenRequest(exchange, new Dictionary<string, string>
        {
            ["client_id"] = TestApp.ClientId,
            ["client_secret"] = TestApp.ClientSecret,
            ["code"] = Code,
            ["redirect_uri"] = Redirect.AbsoluteUri,
            ["code_verifier"] = Verifier,
        });
        Assert.Equal(
            new UserTokenPair(AccessToken("oauth-token.json"), now.AddSeconds(28_800), RefreshToken("oauth-token.json"), now.AddSeconds(15_897_600)),
            exchanged.Value);

        // Renewing: the same address, with the refresh token.
        github.Time.Advance(TimeSpan.FromHours(8));
        github.Stub.Answer = _ => StubGitHub.Json(Fixture.Text("oauth-refresh.json"));
        var renewed = Assert.IsType<SignInResult<UserTokenPair>.Granted>(await signIn.Renew(GitHubHost.User, exchanged.Value.RefreshToken, GitHubHost.Ct));
        AssertTokenRequest(github.Stub.Requests[^1], new Dictionary<string, string>
        {
            ["client_id"] = TestApp.ClientId,
            ["client_secret"] = TestApp.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = RefreshToken("oauth-token.json"),
        });
        var later = github.Time.GetUtcNow();
        Assert.Equal(
            new UserTokenPair(AccessToken("oauth-refresh.json"), later.AddSeconds(28_800), RefreshToken("oauth-refresh.json"), later.AddSeconds(15_897_600)),
            renewed.Value);

        // Who the token is: GET /user on the API, with the token.
        github.Stub.Answer = _ => StubGitHub.Json(Fixture.Text("user.json"));
        var who = Assert.IsType<SignInResult<CodeHostAccount>.Granted>(await signIn.WhoIs(GitHubHost.User, renewed.Value.AccessToken, GitHubHost.Ct));
        Assert.Equal(new CodeHostAccount("octo-owner", 5830001), who.Value);
        var user = github.Stub.Requests[^1];
        Assert.Equal(HttpMethod.Get, user.Method);
        Assert.Equal("https://api.github.com/user", user.Uri.AbsoluteUri);
        Assert.Equal($"Bearer {AccessToken("oauth-refresh.json")}", user.Header("Authorization"));
        Assert.Equal("application/vnd.github+json", user.Header("Accept"));
        Assert.Equal("2022-11-28", user.Header("X-GitHub-Api-Version"));
    }

    private static void AssertTokenRequest(Received request, Dictionary<string, string> fields)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://github.com/login/oauth/access_token", request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal("application/x-www-form-urlencoded", request.Header("Content-Type"));
        Assert.Equal("platform-devflow", request.Header("User-Agent"));
        // Nothing of the form is in the address, where a log or a proxy would keep it.
        Assert.Equal("", request.Uri.Query);
        Assert.Null(request.Header("Authorization"));
        var sent = new Received(HttpMethod.Get, new Uri("https://example.test/?" + request.Body), new Dictionary<string, string>(), null).Query;
        Assert.Equal(fields, sent);
    }

    public static TheoryData<string, string> TokenAnswers() => new()
    {
        // Errors about what was sent: refused.
        { """{"error": "bad_verification_code", "error_description": "The code passed is incorrect or expired."}""", "refused" },
        { """{"error": "bad_refresh_token", "error_description": "The refresh token passed is incorrect or expired."}""", "refused" },
        { """{"error": "access_denied"}""", "refused" },
        { """{"error": "something_new"}""", "refused" },
        // Errors that say the app is set up wrongly: unavailable, so a link is never ended for them.
        { """{"error": "incorrect_client_credentials"}""", "unavailable" },
        { """{"error": "redirect_uri_mismatch"}""", "unavailable" },
        // A token that does not expire, from an app without expiring tokens, or no JSON at all.
        { """{"access_token": "ghu_x", "scope": "", "token_type": "bearer"}""", "unavailable" },
        { """{"access_token": "ghu_x", "expires_in": 28800, "refresh_token": "ghr_x", "refresh_token_expires_in": 15897600, "token_type": "mac"}""", "unavailable" },
        { "<html>", "unavailable" },
    };

    [Theory]
    [MemberData(nameof(TokenAnswers))]
    public async Task ARefusalIsToldApartFromAHostThatCannotAnswer(string body, string expected)
    {
        using var github = new GitHubHost();
        github.Stub.Answer = _ => StubGitHub.Json(body);

        var exchanged = await SignIn(github).Exchange(GitHubHost.User, Code, Verifier, Redirect, GitHubHost.Ct);
        var renewed = await SignIn(github).Renew(GitHubHost.User, "ghr_old", GitHubHost.Ct);

        foreach (var result in new[] { exchanged, renewed })
        {
            Assert.Equal(expected, result switch
            {
                SignInResult<UserTokenPair>.Refused => "refused",
                SignInResult<UserTokenPair>.Unavailable => "unavailable",
                _ => "granted",
            });
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AnAnswerThatIsNotASuccessNeverEndsALink(HttpStatusCode status)
    {
        using var github = new GitHubHost();
        github.Stub.Answer = _ => new HttpResponseMessage(status);

        Assert.IsType<SignInResult<UserTokenPair>.Unavailable>(await SignIn(github).Renew(GitHubHost.User, "ghr_old", GitHubHost.Ct));
    }

    [Fact]
    public async Task WhoATokenIsAnswersRefusedForATokenGitHubDoesNotTake()
    {
        using var github = new GitHubHost();

        github.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        Assert.IsType<SignInResult<CodeHostAccount>.Refused>(await SignIn(github).WhoIs(GitHubHost.User, "ghu_old", GitHubHost.Ct));

        github.Stub.Answer = _ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
            limited.Headers.Add("retry-after", "60");
            return limited;
        };
        var unavailable = Assert.IsType<SignInResult<CodeHostAccount>.Unavailable>(await SignIn(github).WhoIs(GitHubHost.User, "ghu_old", GitHubHost.Ct));
        Assert.Equal(github.Time.GetUtcNow().AddSeconds(60), unavailable.RetryAt);

        github.Stub.Answer = _ => StubGitHub.Json("""{"id": 1}""");
        Assert.IsType<SignInResult<CodeHostAccount>.Unavailable>(await SignIn(github).WhoIs(GitHubHost.User, "ghu_old", GitHubHost.Ct));
    }

    [Fact]
    public async Task NoCodeVerifierSecretOrTokenReachesALog()
    {
        using var github = new GitHubHost();
        github.Stub.Answer = request => request.Uri.Host == "api.github.com"
            ? StubGitHub.Json(Fixture.Text("user.json"))
            : StubGitHub.Json(Fixture.Text("oauth-token.json"));

        var tokens = Assert.IsType<SignInResult<UserTokenPair>.Granted>(await SignIn(github).Exchange(GitHubHost.User, Code, Verifier, Redirect, GitHubHost.Ct)).Value;
        await SignIn(github).Renew(GitHubHost.User, tokens.RefreshToken, GitHubHost.Ct);
        await SignIn(github).WhoIs(GitHubHost.User, tokens.AccessToken, GitHubHost.Ct);

        var printed = string.Join('\n', github.Logs.Lines) + tokens;
        Assert.NotEmpty(github.Logs.Lines);
        foreach (var secret in new[] { Code, Verifier, TestApp.ClientSecret, tokens.AccessToken, tokens.RefreshToken })
        {
            Assert.DoesNotContain(secret, printed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheChallengeIsRfc7636s()
    {
        // The example in RFC 7636, appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge(Verifier));
        Assert.True(Pkce.Matches("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Verifier));
        Assert.False(Pkce.Matches("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cN", Verifier));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", Pkce.Random());
    }
}