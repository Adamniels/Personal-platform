using System.Globalization;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Watching;

/// <summary>
/// A stopped project on "Waiting for you": Dev flow's own items, read from its own tables beside the
/// runner's, counted in the same number, lighting the nav's dot and counted as stopped on the front
/// page; and gone once resumed. See "Watching the default branch" in devflow/README.md.
/// </summary>
public sealed class StoppedListTests
{
    private const string Owner = "octo-owner";

    private static readonly string Landed = "ab12cd34" + new string('0', 32);

    private static async Task<JsonObject> Get(Routes routes, string path)
    {
        var response = await routes.App.Get(path, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    [Fact]
    public async Task AStoppedProjectIsListedCountedAndGoneOnceResumed()
    {
        await using var routes = await Routes.Start();
        await Reading.Reading.Link(routes.Door, Routes.User, Owner);
        routes.Fake.Replace(Routes.User, Reading.Reading.Staging([]));
        var project = await routes.Project(Routes.User);
        var bobs = await routes.Project(Routes.Bob, repository: 9005, name: "bobs");
        await routes.Read(project, Routes.User);
        var landedAt = routes.Time.GetUtcNow();
        var world = routes.Fake.World(Routes.User);
        var repository = world.Installations[0].Repositories[0] with
        {
            Activity = [new FakeActivity(1, "main", new string('0', 40), Landed, landedAt, ActivityKind.Push, FakeCodeHost.AppLogin)],
        };
        routes.Fake.Replace(Routes.User, new FakeWorld([world.Installations[0] with { Repositories = [repository] }]));
        routes.Time.Advance(TimeSpan.FromMinutes(1));
        var stoppedAt = routes.Time.GetUtcNow();
        await routes.Read(project, Routes.User);

        var waiting = await Get(routes, Routes.WaitingPath);

        Assert.Equal(1, (int)waiting["count"]!);
        Assert.Empty(waiting["items"]!.AsArray());
        var item = Assert.Single(waiting["own_items"]!.AsArray())!;
        Assert.True(JsonNode.DeepEquals(
            new JsonObject
            {
                ["kind"] = "project_stopped",
                ["project_id"] = project.ToString(),
                ["project_name"] = "Personal-platform",
                ["owner"] = "Adamniels",
                ["branch"] = "main",
                ["activity_type"] = "push",
                ["actor"] = FakeCodeHost.AppLogin,
                ["commit"] = Landed,
                ["commit_url"] = $"https://github.com/Adamniels/Personal-platform/commit/{Landed}",
                ["landed_at"] = At(landedAt),
                ["stopped_at"] = At(stoppedAt),
            },
            item), item.ToJsonString());
        Assert.True((bool)(await Get(routes, "/devflow/api/attention"))["attention"]!);
        var display = await Get(routes, "/devflow/api/display");
        Assert.Equal(1, (int)display["stopped"]!);
        Assert.Equal(0, (int)display["waiting"]!);

        // Another user's stopped project is not the caller's.
        Assert.DoesNotContain(waiting["own_items"]!.AsArray(), each => (string?)each!["project_id"] == bobs.ToString());

        var resumed = await routes.App.SendJson("POST", $"/devflow/api/projects/{project}/resume", Contract.Token, $$"""{"commit": "{{Landed}}"}""");
        Assert.Equal(StatusCodes.Status204NoContent, resumed.Response.StatusCode);

        waiting = await Get(routes, Routes.WaitingPath);
        Assert.Equal(0, (int)waiting["count"]!);
        Assert.Empty(waiting["own_items"]!.AsArray());
        Assert.False((bool)(await Get(routes, "/devflow/api/attention"))["attention"]!);
        Assert.Equal(0, (int)(await Get(routes, "/devflow/api/display"))["stopped"]!);
    }

    private static string At(DateTimeOffset at) => at.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.') + "+00:00";
}