using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

using NSec.Cryptography;

namespace Review.Tests;

/// <summary>
/// <c>GET /review/api/whoami</c>, the account the request's token names. The ways verification is
/// known to break are sent through review's own route, as "A feature's authentication tests are
/// part of the feature template" in High-level/docs/how-we-build.md asks, since PlatformAuth
/// cannot tell whether a route is behind it. The C# twin of the brain's tests/test_whoami.py.
/// </summary>
public sealed class WhoamiTests : IAsyncLifetime
{
    private const string Whoami = "/review/api/whoami";

    private Harness? _review;

    private Harness Review => _review!;

    public async ValueTask InitializeAsync() => _review = await Harness.Start();

    public async ValueTask DisposeAsync() => await Review.DisposeAsync();

    [Fact]
    public async Task WhoamiAnswersTheAccountOfTheToken()
    {
        // The harness's clock is the fixture's iat, so the token the core produced is valid.
        var response = await Review.Get(Whoami, Contract.Token);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.StartsWith("application/json", response.Response.ContentType, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(
            new JsonObject { ["account_id"] = Contract.Sub },
            JsonNode.Parse(await Harness.Body(response))));
    }

    [Fact]
    public async Task WhoamiAcceptsTheTokenInTheAuthorizationHeader()
    {
        var response = await Review.Send("GET", Whoami, token: null, ("Authorization", $"Bearer {Contract.Token}"));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Contains(Contract.Sub, await Harness.Body(response), StringComparison.Ordinal);
    }

    public static TheoryData<string> NoValidToken() => ["missing", "expired", "bad signature", "unknown key id"];

    [Theory]
    [MemberData(nameof(NoValidToken))]
    public async Task WhoamiWithoutAValidTokenIs401(string kind)
    {
        // Signed by the core, with the clock past exp and the five second leeway.
        await using var review = await Harness.Start(clockAt: kind == "expired" ? Contract.ExpiresAt + 6 : null);
        using var otherKey = Key.Create(SignatureAlgorithm.Ed25519);
        var token = kind switch
        {
            "missing" => null,
            "expired" => Contract.Token,
            // The fixture's key id, but another key signed it.
            "bad signature" => Contract.SignedWith(otherKey, Contract.Kid),
            // A key id the core's set does not hold, even after the refetch it causes.
            "unknown key id" => Contract.SignedWith(otherKey, "unknown-key"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var response = await review.Get(Whoami, token);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.Equal("", await Harness.Body(response));
        Assert.False(response.Response.Headers.ContainsKey("Location"));
    }
}