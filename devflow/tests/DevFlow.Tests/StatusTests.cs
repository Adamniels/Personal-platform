using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Settings;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests;

/// <summary>
/// <c>GET /devflow/api/status</c>, what the page asks before it draws: today, which code host Dev
/// flow talks to, so a page in the fake mode says so. See "What it serves" in devflow/README.md.
/// </summary>
public sealed class StatusTests
{
    private const string Status = "/devflow/api/status";

    [Fact]
    public async Task TheStatusNamesTheCodeHost()
    {
        await using (var fake = await Harness.Start())
        {
            var response = await fake.Get(Status, Contract.Token);

            Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
            Assert.StartsWith("application/json", response.Response.ContentType, StringComparison.Ordinal);
            Assert.True(JsonNode.DeepEquals(new JsonObject { ["code_host"] = "fake" }, JsonNode.Parse(await Harness.Body(response))));
        }

        // The GitHub adapter is not built yet, so a stand in takes its place.
        await using var github = await Harness.Start(Harness.Defaults with { CodeHost = CodeHostMode.GitHub }, codeHost: new FakeCodeHost());
        var answer = await github.Get(Status, Contract.Token);

        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["code_host"] = "github" }, JsonNode.Parse(await Harness.Body(answer))));
    }

    [Fact]
    public async Task TheStatusNeedsAToken()
    {
        await using var devflow = await Harness.Start();

        var response = await devflow.Get(Status);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.Equal("", await Harness.Body(response));
    }
}