using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests;

/// <summary><c>GET /devflow/api/health</c>, which says the process serves. See devflow/README.md.</summary>
public sealed class HealthTests : IAsyncLifetime
{
    private Harness? _devflow;

    private Harness DevFlow => _devflow!;

    public async ValueTask InitializeAsync() => _devflow = await Harness.Start();

    public async ValueTask DisposeAsync() => await DevFlow.DisposeAsync();

    [Fact]
    public async Task HealthAnswersWithoutAToken()
    {
        var response = await DevFlow.Get("/devflow/api/health");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.StartsWith("application/json", response.Response.ContentType, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"status": "ok"}"""), JsonNode.Parse(await Harness.Body(response))));
    }
}