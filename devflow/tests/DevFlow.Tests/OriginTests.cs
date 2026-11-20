using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests;

/// <summary>
/// The <c>Origin</c> check in front of Dev flow's routing, PlatformAuth's middleware, sent through
/// the service rather than a stub, so a route added later is covered too. The full table of
/// origins is PlatformAuth's OriginTests; this checks Dev flow put the middleware in front.
/// </summary>
public sealed class OriginTests : IAsyncLifetime
{
    private Harness? _devflow;

    private Harness DevFlow => _devflow!;

    public async ValueTask InitializeAsync() => _devflow = await Harness.Start();

    public async ValueTask DisposeAsync() => await DevFlow.DisposeAsync();

    [Theory]
    [InlineData("POST", "/devflow/api/whoami")]
    [InlineData("DELETE", "/devflow/projects")]
    [InlineData("PUT", "/devflow/api/nope")]
    [InlineData("PATCH", "/elsewhere")]
    [InlineData("POST", "/devflow/api/waiting/0190a8f0-0000-7000-8000-000000000042/merge")]
    [InlineData("POST", "/devflow/api/waiting/0190a8f0-0000-7000-8000-000000000042/send-back")]
    [InlineData("POST", "/devflow/api/projects/0190a8f0-0000-7000-8000-000000000042/features/10/decisions/0190a8f0-0000-7000-8000-000000000043/1/fine")]
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
            var response = await DevFlow.Send(method, path, Contract.Token, origin);

            Assert.Equal(StatusCodes.Status403Forbidden, response.Response.StatusCode);
        }
    }

    [Fact]
    public async Task TheOwnOriginPassesTheCheck()
    {
        // Past the check, nothing answers a POST there yet.
        var response = await DevFlow.Send("POST", "/devflow/api/whoami", Contract.Token, ("Origin", "https://platform.test"));

        Assert.NotEqual(StatusCodes.Status403Forbidden, response.Response.StatusCode);
    }
}