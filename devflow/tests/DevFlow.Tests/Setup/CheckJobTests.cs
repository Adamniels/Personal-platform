using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Tests.Reading;
using DevFlow.Tests.Workers;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Setup;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// The check at registration: a worker on a version of <c>.devflow/</c> with no check that passed
/// gets that check before any other job, one at a time; a check that passed registers the version
/// and its project, one that failed holds back that version's workers until the owner asks for
/// another, and one that failed outside the project is put back and makes the project wait. See
/// "Setting up a project" in devflow/README.md.
/// </summary>
public sealed class CheckJobTests
{
    private const string Start = "npm ci && npm run dev";
    private const string Url = "http://localhost:5173";

    private static CancellationToken Ct => Jobs.Ct;

    /// <summary>The bytes of a PNG, as far as Dev flow reads one.</summary>
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    /// <summary>A check's result as its worker sends one: the check ran with <paramref name="exit"/>
    /// and <paramref name="output"/>, and, when it passed, the address's two pictures.</summary>
    private static JsonObject Checked(int exit, string output = "Tests: 12 passed", bool pictures = true) => new()
    {
        ["worker_id"] = Jobs.FirstWorker,
        ["outcome"] = "succeeded",
        ["report"] = null,
        ["failure"] = null,
        ["check"] = new JsonObject { ["exit_code"] = exit, ["output"] = output },
        ["branch"] = "main",
        ["head_commit"] = null,
        ["auth_method"] = null,
        ["usage"] = new JsonObject(),
        ["duration_ms"] = 60000,
        ["stderr"] = "",
        ["screenshots"] = pictures ? new JsonObject { ["names"] = new JsonArray("home-desktop", "home-phone"), ["note"] = null } : null,
        ["pictures"] = pictures
            ? new JsonArray(
                new JsonObject { ["name"] = "home-desktop", ["png"] = Convert.ToBase64String(Png) },
                new JsonObject { ["name"] = "home-phone", ["png"] = Convert.ToBase64String(Png) })
            : null,
    };

    private static Task<HttpContext> Send(Jobs jobs, JsonObject job, JsonObject result) =>
        jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result);

    private static async Task<JsonNode> Setup(Jobs jobs)
    {
        var response = await jobs.Routes.App.Get($"/devflow/api/projects/{jobs.Project}/setup", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!;
    }

    private static Task<HttpContext> CheckAgain(Jobs jobs) =>
        jobs.Routes.App.Send("POST", $"/devflow/api/projects/{jobs.Project}/setup/check-again", Contract.Token, ("Origin", Link.Linking.Origin));

    private static async Task<List<(string Version, bool Passed)>> Checks(Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(string, bool)>(new CommandDefinition(
            "SELECT version, passed FROM recipe_checks WHERE project_id = @project ORDER BY checked_at, run_id",
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    private static async Task<List<(string Kind, string? Version, string? Result)>> Runs(Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(string, string?, string?)>(new CommandDefinition(
            "SELECT kind, recipe_version, result FROM runs WHERE project_id = @project ORDER BY started_at, id",
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    [Fact]
    public async Task AnUncheckedVersionIsCheckedFirst()
    {
        // Its card is waiting to be read, and there is no Claude login: a check runs no agent, so
        // it is handed out all the same, before any reading or build.
        await using var jobs = await Jobs.Start(
            [Jobs.Card(1)], login: false, read: false, checkedRecipe: false,
            projectFile: Jobs.ProjectFile(start: Start, url: Url));
        var repository = new DevFlow.CodeHost.Repository(Reading.Reading.Installation, Reading.Reading.RepositoryId, "Adamniels", "Personal-platform");

        var job = await jobs.Job();

        Assert.Equal(Job.RecipeCheck, (string)job["kind"]!);
        Assert.Null(job["card"]);
        Assert.Equal("main", (string)job["branch"]!);
        Assert.Equal("main", (string)job["base"]!);
        // The default branch's head as it was read, the worker's version, and that commit's
        // project.json's check, start and address, with the check's 30 minutes.
        var head = Assert.IsType<CodeHostResult<string>.Found>(await jobs.Fake.BranchHead(Jobs.User, repository, "main", Ct)).Value;
        Assert.Equal(head, (string)job["to_check"]!["commit"]!);
        Assert.Equal("1", (string)job["to_check"]!["version"]!);
        Assert.Equal(Jobs.Check, (string)job["check"]!);
        Assert.Equal(Start, (string)job["start"]!);
        Assert.Equal(Url, (string)job["url"]!);
        Assert.Equal(30, (int)job["limits"]!["minutes"]!);
        // No agent: no brief, model, token or report's schema; and a key that only fetches.
        Assert.Equal("", (string)job["brief"]!);
        Assert.Equal("", (string)job["model"]!);
        Assert.Equal("", (string)job["claude_token"]!);
        Assert.Null(job["report_schema"]);
        Assert.Equal(WritePurpose.Fetch, jobs.Fake.Issued(Jobs.User, (string)job["push_key"]!)!.Value.Purpose);

        // One check of a version at a time: a second worker on it gets nothing, nor does the first
        // asking again, and the card is neither read nor built meanwhile.
        await jobs.SayHello(Jobs.SecondWorker);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.SecondWorker)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(1)).State);
        Assert.Equal([("check", "1", null)], await Runs(jobs));
        var setup = await Setup(jobs);
        Assert.Equal("setting_up", (string)setup["state"]!);
        Assert.True((bool)setup["checking"]!);
    }

    [Fact]
    public async Task APassedCheckRegistersTheProject()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1)], read: false, checkedRecipe: false, projectFile: Jobs.ProjectFile(start: Start, url: Url));
        var job = await jobs.Job();
        Assert.Equal("setting_up", (string)(await Setup(jobs))["state"]!);

        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, job, Checked(0))).Response.StatusCode);

        // The version is checked, the project registered, and its newest check shows its output
        // and its pictures.
        Assert.Equal([("1", true)], await Checks(jobs));
        var setup = await Setup(jobs);
        Assert.Equal("registered", (string)setup["state"]!);
        Assert.False((bool)setup["checking"]!);
        var check = setup["check"]!;
        Assert.True((bool)check["passed"]!);
        Assert.Equal("Tests: 12 passed", (string)check["output"]!);
        Assert.Equal(["home-desktop", "home-phone"], check["pictures"]!.AsArray().Select(name => (string)name!));
        var picture = await jobs.Routes.App.Get($"/devflow/api/projects/{jobs.Project}/setup/checks/{(Guid)check["id"]!}/pictures/home-phone", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, picture.Response.StatusCode);
        Assert.Equal("image/png", picture.Response.ContentType);
        Assert.Equal("nosniff", (string?)picture.Response.Headers.XContentTypeOptions);

        // The next job is its first reading.
        var next = await jobs.Job();
        Assert.Equal(Job.Read, (string)next["kind"]!);
        Assert.Equal(1, (int)next["card"]!["number"]!);
    }

    [Fact]
    public async Task AFailedCheckHandsOutNothing()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1)], checkedRecipe: false);
        await jobs.SayHello(Jobs.SecondWorker);
        var job = await jobs.Job();
        var output = string.Join('\n', Enumerable.Range(1, 250).Select(line => $"line {line}"));

        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, job, Checked(1, output, pictures: false))).Response.StatusCode);

        // No work to the version's workers, whichever asks, and the card waits.
        Assert.Equal([("1", false)], await Checks(jobs));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.SecondWorker)).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(1)).State);

        // The project's page shows the output's last 200 lines.
        var setup = await Setup(jobs);
        Assert.Equal("setting_up", (string)setup["state"]!);
        Assert.False((bool)setup["check"]!["passed"]!);
        Assert.Equal(1, (int)setup["check"]!["exit_code"]!);
        var shown = ((string)setup["check"]!["output"]!).Split('\n');
        Assert.Equal(200, shown.Length);
        Assert.Equal("line 51", shown[0]);
        Assert.Equal("line 250", shown[^1]);

        // "Check again" hands out another check, once.
        Assert.Equal(StatusCodes.Status204NoContent, (await CheckAgain(jobs)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, (await CheckAgain(jobs)).Response.StatusCode);
        Assert.True((bool)(await Setup(jobs))["check"]!["again_asked"]!);
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        var again = await jobs.Job();
        Assert.Equal(Job.RecipeCheck, (string)again["kind"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, again, Checked(0))).Response.StatusCode);
        Assert.Equal([("1", false), ("1", true)], await Checks(jobs));
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);

        // Nothing to check again once it passed, and another user's project is not found.
        Assert.Equal(StatusCodes.Status409Conflict, (await CheckAgain(jobs)).Response.StatusCode);
        var bobs = await jobs.Routes.Project(Workers.Workers.Bob, repository: 9002, name: "bobs");
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Routes.App.Get($"/devflow/api/projects/{bobs}/setup", Contract.Token)).Response.StatusCode);
    }

    [Fact]
    public async Task ANewVersionIsCheckedOnceAgain()
    {
        var recipe = Encoding.UTF8.GetBytes("ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\n");
        var first = Encoding.UTF8.GetBytes(Jobs.ProjectFile());
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2)], checkedRecipe: false);
        Stage(jobs, recipe, first);
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        var v1 = FakeCodeHost.FolderVersion([(".devflow/Dockerfile", recipe), (".devflow/project.json", first)]);
        await Hello(jobs, Jobs.FirstWorker, v1);

        var check = await jobs.Job();
        Assert.Equal(v1, (string)check["to_check"]!["version"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, check, Checked(0))).Response.StatusCode);
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);

        // The project's .devflow/ changes on its default branch: its workers on the old version are
        // told to rebuild, as today.
        var second = Encoding.UTF8.GetBytes(Jobs.ProjectFile(check: "npm ci && npm run test:all"));
        Stage(jobs, recipe, second);
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        var v2 = FakeCodeHost.FolderVersion([(".devflow/Dockerfile", recipe), (".devflow/project.json", second)]);
        await Hello(jobs, Jobs.SecondWorker, v1);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.SecondWorker)).Response.StatusCode);
        var listed = await Workers.Workers.List(jobs.Routes, jobs.Project);
        Assert.Contains(listed["workers"]!.AsArray(), worker => (string?)worker!["rebuild"] == "recipe");

        // Rebuilt on the new version, a worker checks it once before it takes anything else; the
        // project stays registered meanwhile.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        await Hello(jobs, Jobs.SecondWorker, v2);
        var recheck = await jobs.Job(Jobs.SecondWorker);
        Assert.Equal(Job.RecipeCheck, (string)recheck["kind"]!);
        Assert.Equal(v2, (string)recheck["to_check"]!["version"]!);
        Assert.Equal("npm ci && npm run test:all", (string)recheck["check"]!);
        var setup = await Setup(jobs);
        Assert.Equal("registered", (string)setup["state"]!);
        Assert.Equal([false], setup["steps"]!.AsArray().Where(step => (string)step!["step"]! == "check").Select(step => (bool)step!["done"]!));
        var passed = Checked(0);
        passed["worker_id"] = Jobs.SecondWorker;
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, recheck, passed)).Response.StatusCode);
        Assert.Equal([(v1, true), (v2, true)], await Checks(jobs));
        Assert.NotEqual(Job.RecipeCheck, (string)(await jobs.Job(Jobs.SecondWorker))["kind"]!);
    }

    [Fact]
    public async Task AFailureOutsideIsTriedAgain()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1)], checkedRecipe: false);
        var job = await jobs.Job();

        var failed = Jobs.Failure(Jobs.FirstWorker, "main", inCard: false, "The repository could not be fetched: the network is down");
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, job, failed)).Response.StatusCode);

        // No verdict on the setup: nothing recorded for the version, the run ended outside, and the
        // project waits as after any run that failed outside.
        Assert.Empty(await Checks(jobs));
        Assert.Equal([("check", "1", "failed_outside_card")], await Runs(jobs));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("setting_up", (string)(await Setup(jobs))["state"]!);

        // Once the wait is over, the check is handed out again.
        jobs.Routes.Time.Advance(DevFlow.Stops.ProjectWaits.First);
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        var again = await jobs.Job();
        Assert.Equal(Job.RecipeCheck, (string)again["kind"]!);

        // A check lost with its worker is put back the same way.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(45));
        Assert.Equal(1, await DevFlow.Jobs.RunStore.SweepLost(jobs.Routes.Door, Jobs.User, jobs.Project, jobs.Routes.Time.GetUtcNow(), Ct));
        Assert.Empty(await Checks(jobs));
        Assert.Equal("lost", (await Runs(jobs))[^1].Result);
    }

    [Fact]
    public async Task ACheckResultFollowsItsRules()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1)], checkedRecipe: false);
        var job = await jobs.Job();

        // A check runs no agent, so it sends no report; and its success carries the check's result.
        var withReport = Checked(0);
        withReport["report"] = Jobs.GoodReport();
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await Send(jobs, job, withReport)).Response.StatusCode);
        var noCheck = Checked(0);
        noCheck["check"] = null;
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await Send(jobs, job, noCheck)).Response.StatusCode);
        // A picture the screenshots do not name, or that is not a PNG, is refused.
        var unnamed = Checked(0);
        unnamed["screenshots"]!["names"] = new JsonArray("home-desktop");
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await Send(jobs, job, unnamed)).Response.StatusCode);
        var notPng = Checked(0);
        notPng["pictures"]![0]!["png"] = Convert.ToBase64String("not a png"u8.ToArray());
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await Send(jobs, job, notPng)).Response.StatusCode);
        Assert.Empty(await Checks(jobs));

        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, job, Checked(0))).Response.StatusCode);

        // Pictures on any other job are refused.
        var build = await jobs.Job();
        Assert.Equal(Job.Build, (string)build["kind"]!);
        var result = Jobs.Result(Jobs.FirstWorker, (string)build["branch"]!);
        result["pictures"] = Checked(0)["pictures"]!.DeepClone();
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await jobs.Post(WorkerApi.ResultPath((Guid)build["job_id"]!), result)).Response.StatusCode);
    }

    /// <summary>The world as the harness stages it, with the recipe and project.json given on main.</summary>
    private static void Stage(Jobs jobs, byte[] recipe, byte[] projectFile)
    {
        var world = Jobs.World([Jobs.Card(1), Jobs.Card(2)], null);
        var repository = world.Installations[0].Repositories[0];
        jobs.Fake.Replace(Jobs.User, world with
        {
            Installations = [world.Installations[0] with
            {
                Repositories = [repository with
                {
                    Files = [new FakeFile(".devflow/Dockerfile", "main", recipe), new FakeFile(".devflow/project.json", "main", projectFile)],
                }],
            }],
        });
    }

    private static async Task Hello(Jobs jobs, Guid worker, string version)
    {
        var hello = new JsonObject { ["worker_id"] = worker, ["name"] = "fodr-worker", ["recipe_version"] = version, ["base_image"] = Jobs.Image };
        var response = await Workers.Workers.AsWorker(jobs.Routes.App, Workers.Workers.HelloPath, hello.ToJsonString(), null, Workers.Workers.Bearer(jobs.Key));
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
    }
}