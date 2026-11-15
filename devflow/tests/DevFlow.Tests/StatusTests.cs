using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Link;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests;

/// <summary>
/// <c>GET /devflow/api/status</c>, what the page asks before it draws: which code host Dev flow talks
/// to, so a page in the fake mode says so, and the caller's GitHub link. See "What it serves" in
/// devflow/README.md.
/// </summary>
public sealed class StatusTests
{
    private const string Status = "/devflow/api/status";

    private static readonly JsonObject Unlinked = new() { ["state"] = "none", ["login"] = null };

    [Fact]
    public async Task TheStatusNamesTheCodeHost()
    {
        await using (var fake = await Linking.Start())
        {
            var response = await fake.App.Get(Status, Contract.Token);

            Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
            Assert.StartsWith("application/json", response.Response.ContentType, StringComparison.Ordinal);
            Assert.True(JsonNode.DeepEquals(new JsonObject { ["code_host"] = "fake", ["github"] = Unlinked.DeepClone() }, JsonNode.Parse(await Harness.Body(response))));
        }

        // The GitHub mode, with its adapter, which reading the status never calls.
        await using var database = await Databases.DevFlow.Migrated(TestContext.Current.CancellationToken);
        var door = await PlatformData.UserDb.Open(Settings.DevFlowSettings.Service, database.App(), Settings.DevFlowSettings.AppDatabaseUrl, TestContext.Current.CancellationToken);
        await using var github = await Harness.Start(GitHub.TestApp.Load([.. GitHub.TestApp.Environment.Select(pair => (pair.Name, (string?)pair.Value))]), database: door);
        var answer = await github.Get(Status, Contract.Token);

        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["code_host"] = "github", ["github"] = Unlinked.DeepClone() }, JsonNode.Parse(await Harness.Body(answer))));
    }

    [Fact]
    public async Task TheStatusSaysWhetherGitHubIsLinked()
    {
        await using var linking = await Linking.Start();

        async Task<JsonNode?> GitHubLink() => JsonNode.Parse(await Harness.Body(await linking.App.Get(Status, Contract.Token)))!["github"];

        Assert.True(JsonNode.DeepEquals(Unlinked, await GitHubLink()));

        await linking.LinkAll();
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["state"] = "linked", ["login"] = "octo-owner" }, await GitHubLink()));

        // GitHub refused a renewal: the login it was linked as is kept, for the page to name.
        await using (var admin = await linking.Database.OpenAdmin(TestContext.Current.CancellationToken))
        {
            await admin.ExecuteAsync(new CommandDefinition("UPDATE github_accounts SET state = 'needs_relink'", cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.True(JsonNode.DeepEquals(new JsonObject { ["state"] = "needs_relink", ["login"] = "octo-owner" }, await GitHubLink()));

        // Another user's link is not the caller's.
        await using (var admin = await linking.Database.OpenAdmin(TestContext.Current.CancellationToken))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE github_accounts SET user_id = @bob", new { bob = Linking.Bob }, cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.True(JsonNode.DeepEquals(Unlinked, await GitHubLink()));
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