using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

namespace Review.Tests;

/// <summary><c>GET /review/api/health</c>, which says the process serves. See review/README.md.</summary>
public sealed class HealthTests : IAsyncLifetime
{
    private Harness? _review;

    private Harness Review => _review!;

    public async ValueTask InitializeAsync() => _review = await Harness.Start();

    public async ValueTask DisposeAsync() => await Review.DisposeAsync();

    [Fact]
    public async Task HealthAnswersWithoutAToken()
    {
        var response = await Review.Get("/review/api/health");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.StartsWith("application/json", response.Response.ContentType, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"status": "ok"}"""), JsonNode.Parse(await Harness.Body(response))));
    }
}