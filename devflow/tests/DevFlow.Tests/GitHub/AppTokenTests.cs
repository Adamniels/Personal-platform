using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// Signing in as the app: the token the adapter sends when it asks for an installation token is
/// signed with RS256 by the app's private key, and carries the client id as <c>iss</c>, <c>iat</c>
/// 60 seconds before now and <c>exp</c> 9 minutes after. See "GitHub" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class AppTokenTests
{
    [Fact]
    public async Task TheTokenIsSignedWithTheAppsKeyAndCarriesItsClaims()
    {
        using var host = new GitHubHost();
        var now = host.Time.GetUtcNow().ToUnixTimeSeconds();

        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, GitHubHost.Ct);

        var request = Assert.Single(host.Stub.TokenRequests);
        Assert.Equal("/app/installations/41/access_tokens", request.Path);
        var authorization = request.Header("Authorization")!;
        Assert.StartsWith("Bearer ", authorization, StringComparison.Ordinal);
        var parts = authorization["Bearer ".Length..].Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonNode.Parse(Base64Url.DecodeFromChars(parts[0]))!.AsObject();
        Assert.Equal("RS256", (string)header["alg"]!);
        Assert.Equal("JWT", (string)header["typ"]!);

        // Verified with the public half alone, as GitHub verifies it.
        using var publicHalf = RSA.Create();
        publicHalf.ImportRSAPublicKey(TestApp.Key.ExportRSAPublicKey(), out _);
        Assert.True(publicHalf.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        // Signed by any other key, it does not verify.
        using var other = RSA.Create(2048);
        Assert.False(other.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        var claims = JsonNode.Parse(Base64Url.DecodeFromChars(parts[1]))!.AsObject();
        Assert.Equal(TestApp.ClientId, (string)claims["iss"]!);
        Assert.Equal(now - 60, (long)claims["iat"]!);
        Assert.Equal(now + (9 * 60), (long)claims["exp"]!);
        Assert.Equal(["exp", "iat", "iss"], claims.Select(claim => claim.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ATokenIsMadeWhenNeededAndNeverKept()
    {
        using var host = new GitHubHost();
        // Every installation token expires at once, so each read asks for a new one.
        host.Stub.TokenAnswer = _ => host.Stub.Token(TimeSpan.FromMinutes(5));

        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, GitHubHost.Ct);
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, GitHubHost.Ct);

        var claims = host.Stub.TokenRequests
            .Select(request => JsonNode.Parse(Base64Url.DecodeFromChars(request.Header("Authorization")!["Bearer ".Length..].Split('.')[1]))!)
            .Select(token => (long)token["iat"]!)
            .ToList();
        // A second app token, made a minute later, not the first one kept.
        Assert.Equal(2, claims.Count);
        Assert.Equal(60, claims[1] - claims[0]);
    }
}