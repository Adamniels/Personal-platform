using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using NSec.Cryptography;

using static PlatformAuth.Tests.Tokens;

namespace PlatformAuth.Tests;

/// <summary>
/// A service's edge: where the token is read from, and what a request without a valid one gets.
/// The rules are in "How a feature authenticates" in High-level/docs/feature-contract.md. The C#
/// twin of libs/python/platform-auth/tests/test_auth.py.
///
/// The tests run through a stub service with one page and one API route, with its own key cache
/// over a key set answered from memory, so they mint tokens with a key they hold and never reach
/// a real core. The clock is a fake one at <see cref="Tokens.Now"/>.
/// </summary>
public sealed class EdgeTests : IAsyncLifetime
{
    private const string PagePath = "/feature";
    private const string WhoAmI = "/feature/api/whoami";
    private const string Forward = "/feature/api/forward";
    private const string AccountA = "01900000-0000-7000-8000-0000000000a1";
    private const string AccountB = "01900000-0000-7000-8000-0000000000b2";

    private readonly Key _signingKey = NewKey();
    private readonly StubCore _core;
    private WebApplication? _app;

    public EdgeTests()
    {
        _core = new StubCore(StubCore.Serves(Jwks(_signingKey, Kid)));
    }

    private TestServer Server => _app!.GetTestServer();

    public async ValueTask InitializeAsync() => _app = await Start(_core);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _core.Dispose();
        _signingKey.Dispose();
    }

    private static async Task<WebApplication> Start(StubCore core)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(Now));
        builder.Services.AddSingleton<TimeProvider>(time);
        // Registered first, so AddPlatformAuth keeps it rather than making one over the network.
        builder.Services.AddSingleton(core.Cache(time));
        builder.Services.AddPlatformAuth(options => options.CoreUrl = new Uri(StubCore.Url));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet(PagePath, (HttpContext context) => Results.Content(
            $"<p>You are account {context.Features.GetRequiredFeature<IPlatformToken>().Claims.AccountId}.</p>",
            "text/html"))
            .RequireAuthorization(PlatformAuthPolicies.Page);
        app.MapGet(WhoAmI, (HttpContext context) => Results.Json(new
        {
            account_id = context.Features.GetRequiredFeature<IPlatformToken>().Claims.AccountId,
            sub = context.User.FindFirst("sub")?.Value,
        }))
            .RequireAuthorization(PlatformAuthPolicies.Api);
        app.MapGet(Forward, (HttpContext context) => context.Features.GetRequiredFeature<IPlatformToken>().Token)
            .RequireAuthorization(PlatformAuthPolicies.Api);
        await app.StartAsync();
        return app;
    }

    private string Token(string account = AccountA, long expiresIn = Lifetime, Key? key = null) =>
        Mint(key ?? _signingKey, Wire(changes: [("sub", account), ("exp", Now + expiresIn)]));

    private static Action<HttpContext> Get(string pathAndQuery, params (string Name, string Value)[] headers) =>
        context =>
        {
            context.Request.Method = "GET";
            var query = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
            context.Request.Path = query < 0 ? pathAndQuery : pathAndQuery[..query];
            context.Request.QueryString = new QueryString(query < 0 ? "" : pathAndQuery[query..]);
            foreach (var (name, value) in headers)
            {
                context.Request.Headers.Append(name, value);
            }
        };

    private Task<HttpContext> Send(string pathAndQuery, params (string Name, string Value)[] headers) =>
        Server.SendAsync(Get(pathAndQuery, headers), TestContext.Current.CancellationToken);

    private static (string, string) Cookie(string token) => ("Cookie", $"__Host-access={token}");

    private static (string, string) Bearer(string token) => ("Authorization", $"Bearer {token}");

    private static async Task<string> Body(HttpContext context) =>
        await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);

    private static async Task AssertRefusedApi(HttpContext response)
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.Equal("", await Body(response));
        Assert.False(response.Response.Headers.ContainsKey("Location"));
    }

    // The three ways a request has no valid token, which must look the same.
    public static TheoryData<string> NoValidToken() => ["none", "expired", "bad signature"];

    private (string, string)[] NoValidTokenHeaders(string kind)
    {
        using var other = NewKey();
        return kind switch
        {
            "none" => [],
            "expired" => [Cookie(Token(expiresIn: -3600))],
            "bad signature" => [Cookie(Token(key: other))],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    [Fact]
    public async Task APageShowsTheAccountFromACookieToken()
    {
        var response = await Send(PagePath, Cookie(Token(AccountA)));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.StartsWith("text/html", response.Response.ContentType, StringComparison.Ordinal);
        Assert.Contains(AccountA, await Body(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApiRouteAcceptsTheTokenInEitherPlace()
    {
        foreach (var header in new[] { Bearer(Token(AccountA)), Cookie(Token(AccountA)) })
        {
            var response = await Send(WhoAmI, header);

            Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
            Assert.Equal($"{{\"account_id\":\"{AccountA}\",\"sub\":\"{AccountA}\"}}", await Body(response));
        }
    }

    [Theory]
    [MemberData(nameof(NoValidToken))]
    public async Task APageWithoutAValidTokenRedirectsToRefreshWithTheWayBack(string kind)
    {
        var response = await Send($"{PagePath}?a=1", NoValidTokenHeaders(kind));

        Assert.Equal(StatusCodes.Status303SeeOther, response.Response.StatusCode);
        Assert.Equal("/auth/refresh?return_to=%2Ffeature%3Fa%3D1", response.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task TheWayBackIsThePathAsReceived()
    {
        // Already encoded bytes are encoded once more, so the core decodes back to exactly what
        // the browser sent. The service never judges the path. A real client, so the request
        // goes through the server's parsing of the raw target as it would in Kestrel.
        using var client = Server.CreateClient();
        using var response = await client.GetAsync(
            new Uri($"{PagePath}?q=%2F%3Cx%3E&b=", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(
            "/auth/refresh?return_to=%2Ffeature%3Fq%3D%252F%253Cx%253E%26b%3D",
            response.Headers.Location?.OriginalString);
    }

    [Theory]
    [MemberData(nameof(NoValidToken))]
    public async Task AnApiRouteWithoutAValidTokenAnswers401Empty(string kind)
    {
        await AssertRefusedApi(await Send(WhoAmI, NoValidTokenHeaders(kind)));
    }

    [Fact]
    public async Task AHeaderTokenIsPreferredOverTheCookie()
    {
        var response = await Send(WhoAmI, Bearer(Token(AccountA)), Cookie(Token(AccountB)));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Contains(AccountA, await Body(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidHeaderBesideAValidCookieIsRefused()
    {
        using var other = NewKey();
        foreach (var authorization in new[]
        {
            "Bearer not-a-token",
            $"Bearer {Token(expiresIn: -3600)}",
            $"Bearer {Token(key: other)}",
            "Bearer ",
            "Bearer",
            $"Basic {Token()}",
            Token(),
            $"Bearer {Token()} extra",
        })
        {
            (string, string)[] headers = [Cookie(Token()), ("Authorization", authorization)];

            await AssertRefusedApi(await Send(WhoAmI, headers));
            Assert.Equal(StatusCodes.Status303SeeOther, (await Send(PagePath, headers)).Response.StatusCode);
        }
    }

    [Fact]
    public async Task TwoAuthorizationHeadersAreRefused()
    {
        // Two valid headers, so only the count can refuse them. Sent through the server's context
        // directly, so no client folds them into one value.
        var token = Token();
        (string, string)[] headers = [Bearer(token), Bearer(token)];

        Assert.Equal(StatusCodes.Status200OK, (await Send(WhoAmI, Bearer(token))).Response.StatusCode);
        await AssertRefusedApi(await Send(WhoAmI, headers));
        Assert.Equal(StatusCodes.Status303SeeOther, (await Send(PagePath, headers)).Response.StatusCode);
    }

    [Fact]
    public async Task TheBearerSchemeIsCaseInsensitive()
    {
        var response = await Send(WhoAmI, ("Authorization", $"bearer {Token()}"));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task TheVerifiedTokenIsAvailableForForwarding()
    {
        var fromHeader = Token(AccountA);
        var fromCookie = Token(AccountB);

        Assert.Equal(fromHeader, await Body(await Send(Forward, Bearer(fromHeader))));
        Assert.Equal(fromCookie, await Body(await Send(Forward, Cookie(fromCookie))));
        // The header's, never the cookie's, when both are sent.
        Assert.Equal(fromHeader, await Body(await Send(Forward, Bearer(fromHeader), Cookie(fromCookie))));
    }

    [Fact]
    public async Task AnUnreachableCoreWithNoCachedKeyCountsAsNoValidToken()
    {
        using var unreachable = new StubCore(StubCore.Unreachable);
        await using var app = await Start(unreachable);
        var server = app.GetTestServer();
        var token = Token();

        var page = await server.SendAsync(Get(PagePath, Cookie(token)), TestContext.Current.CancellationToken);
        var api = await server.SendAsync(Get(WhoAmI, Bearer(token)), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status303SeeOther, page.Response.StatusCode);
        Assert.Equal("/auth/refresh?return_to=%2Ffeature", page.Response.Headers.Location.ToString());
        await AssertRefusedApi(api);
    }
}