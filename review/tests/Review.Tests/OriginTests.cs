using Microsoft.AspNetCore.Http;

namespace Review.Tests;

/// <summary>
/// The <c>Origin</c> check in front of review's routing, PlatformAuth's middleware, sent through
/// the service rather than a stub, so a route added later is covered too. The full table of
/// origins is PlatformAuth's OriginTests; this checks review put the middleware in front.
/// </summary>
public sealed class OriginTests : IAsyncLifetime
{
    private Harness? _review;

    private Harness Review => _review!;

    public async ValueTask InitializeAsync() => _review = await Harness.Start();

    public async ValueTask DisposeAsync() => await Review.DisposeAsync();

    [Theory]
    [InlineData("POST", "/review/api/whoami")]
    [InlineData("DELETE", "/review/cards")]
    [InlineData("PUT", "/review/api/nope")]
    [InlineData("PATCH", "/elsewhere")]
    public async Task AStateChangingRequestFromAnotherOriginIs403(string method, string path)
    {
        foreach (var origin in new (string, string)[][]
        {
            [],
            [("Origin", "https://evil.test")],
            [("Origin", "https://platform.test.evil.test")],
            [("Origin", "https://platform.test"), ("Origin", "https://platform.test")],
        })
        {
            var response = await Review.Send(method, path, Contract.Token, origin);

            Assert.Equal(StatusCodes.Status403Forbidden, response.Response.StatusCode);
        }
    }

    [Fact]
    public async Task TheOwnOriginPassesTheCheck()
    {
        // Past the check, nothing answers a POST there yet.
        var response = await Review.Send("POST", "/review/api/whoami", Contract.Token, ("Origin", "https://platform.test"));

        Assert.NotEqual(StatusCodes.Status403Forbidden, response.Response.StatusCode);
    }
}