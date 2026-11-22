using System.Text;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Setup;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// A project's setup steps, as <c>GET /devflow/api/projects/{id}/setup</c> answers them: the recipe
/// on the default branch, a worker on it, and its check passed, in that order, each done as the
/// project gets there. See "Setting up a project" in devflow/README.md.
/// </summary>
public sealed class SetupStepsTests
{
    private static async Task<JsonNode> Setup(Jobs jobs)
    {
        var response = await jobs.Routes.App.Get($"/devflow/api/projects/{jobs.Project}/setup", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!;
    }

    private static IEnumerable<(string, bool)> Steps(JsonNode setup) =>
        setup["steps"]!.AsArray().Select(step => ((string)step!["step"]!, (bool)step!["done"]!));

    [Fact]
    public async Task TheStepsFollowTheProject()
    {
        // Connected, with project.json but no recipe on its default branch, and a worker that said
        // hello on a version of its own.
        await using var jobs = await Jobs.Start([Jobs.Card(1)], checkedRecipe: false);
        var setup = await Setup(jobs);
        Assert.Equal("setting_up", (string)setup["state"]!);
        Assert.Equal([("recipe", false), ("worker", false), ("check", false)], Steps(setup));
        Assert.Null(setup["check"]);
        Assert.False((bool)setup["checking"]!);

        // The recipe lands on the default branch, and Dev flow reads its version.
        var recipe = Encoding.UTF8.GetBytes("ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\n");
        var projectFile = Encoding.UTF8.GetBytes(Jobs.ProjectFile());
        var world = Jobs.World([Jobs.Card(1)], null);
        jobs.Fake.Replace(Jobs.User, world with
        {
            Installations = [world.Installations[0] with
            {
                Repositories = [world.Installations[0].Repositories[0] with
                {
                    Files = [new FakeFile(".devflow/Dockerfile", "main", recipe), new FakeFile(".devflow/project.json", "main", projectFile)],
                }],
            }],
        });
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        Assert.Equal([("recipe", true), ("worker", false), ("check", false)], Steps(await Setup(jobs)));

        // A worker built from that version says hello.
        var version = FakeCodeHost.FolderVersion([(".devflow/Dockerfile", recipe), (".devflow/project.json", projectFile)]);
        var hello = new JsonObject { ["worker_id"] = Jobs.FirstWorker, ["name"] = "fodr-worker", ["recipe_version"] = version, ["base_image"] = Jobs.Image };
        await Workers.Workers.AsWorker(jobs.Routes.App, Workers.Workers.HelloPath, hello.ToJsonString(), null, Workers.Workers.Bearer(jobs.Key));
        Assert.Equal([("recipe", true), ("worker", true), ("check", false)], Steps(await Setup(jobs)));

        // Its check runs, and passes: every step is done, and the project is registered.
        var job = await jobs.Job();
        Assert.Equal(Job.RecipeCheck, (string)job["kind"]!);
        Assert.True((bool)(await Setup(jobs))["checking"]!);
        var passed = new JsonObject
        {
            ["worker_id"] = Jobs.FirstWorker,
            ["outcome"] = "succeeded",
            ["report"] = null,
            ["failure"] = null,
            ["check"] = new JsonObject { ["exit_code"] = 0, ["output"] = "ok" },
            ["branch"] = "main",
            ["head_commit"] = null,
            ["auth_method"] = null,
            ["usage"] = new JsonObject(),
            ["duration_ms"] = 1000,
            ["stderr"] = "",
        };
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), passed)).Response.StatusCode);
        setup = await Setup(jobs);
        Assert.Equal("registered", (string)setup["state"]!);
        Assert.Equal([("recipe", true), ("worker", true), ("check", true)], Steps(setup));
        Assert.Equal(version, (string)setup["check"]!["version"]!);
        Assert.Empty(setup["check"]!["pictures"]!.AsArray());
    }
}