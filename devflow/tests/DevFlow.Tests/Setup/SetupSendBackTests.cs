using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Setup;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// Sending a failed check back: the owner's comment on a check of the newest version of
/// <c>.devflow/</c> that failed starts a new setup run from the default branch, on the next free
/// <c>devflow/setup-&lt;k&gt;</c>, whose brief holds the files as they were checked, the check's
/// output and his comment; its pull request follows the setup run's. See "Setting up a project" in
/// devflow/README.md.
/// </summary>
public sealed class SetupSendBackTests
{
    private const string Output = "> fodr@1.0.0 test\n> vitest run\n × src/cache.test.ts > connects to Redis\nTests  1 failed | 24 passed";

    private const string Comment = "The cache tests need Redis: install it in the recipe and start it in the check.";

    private static CancellationToken Ct => Jobs.Ct;

    private static string SendBackPath(Jobs jobs) => $"/devflow/api/projects/{jobs.Project}/setup/send-back";

    private static Task<HttpContext> SendBack(Jobs jobs, string? comment) =>
        jobs.Routes.App.SendJson("POST", SendBackPath(jobs), Contract.Token, new JsonObject { ["comment"] = comment }.ToJsonString());

    /// <summary>A check's result as its worker sends one: it ran and exited with
    /// <paramref name="exit"/>.</summary>
    private static JsonObject Checked(int exit, string output = Output) => new()
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
    };

    /// <summary>
    /// A registered project whose first setup ran and merged, the setup's row standing for it, and
    /// whose .devflow/ is on the default branch, valid; a worker built from it says hello, and is
    /// handed its check. The check job.
    /// </summary>
    private static async Task<(Jobs Jobs, JsonObject Check)> Checking()
    {
        var jobs = await Jobs.Start([Jobs.Card(1)], checkedRecipe: false);
        SetupRunTests.Stage(jobs, (".devflow/Dockerfile", "main", SetupRunTests.GoodRecipe), (".devflow/project.json", "main", Jobs.ProjectFile()));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        await DevFlow.Tests.Merging.Merged.Sql(
            jobs,
            """
            INSERT INTO setups (project_id, user_id, number, branch, state, reason, pull_request_number, pull_request_url, created_at, changed_at)
            SELECT id, user_id, 1, 'devflow/setup', 'done', 'Merged; .devflow/ is on the default branch', 7, 'https://github.com/Adamniels/Personal-platform/pull/7', now(), now()
            FROM projects WHERE id = @project
            """);
        var version = FakeCodeHost.FolderVersion([(".devflow/Dockerfile", Encoding.UTF8.GetBytes(SetupRunTests.GoodRecipe)), (".devflow/project.json", Encoding.UTF8.GetBytes(Jobs.ProjectFile()))]);
        var hello = new JsonObject { ["worker_id"] = Jobs.FirstWorker, ["name"] = "fodr-worker", ["recipe_version"] = version, ["base_image"] = Jobs.Image };
        await Workers.Workers.AsWorker(jobs.Routes.App, Workers.Workers.HelloPath, hello.ToJsonString(), null, Workers.Workers.Bearer(jobs.Key));
        var check = await jobs.Job();
        Assert.Equal(Job.RecipeCheck, (string)check["kind"]!);
        return (jobs, check);
    }

    private static async Task Ends(Jobs jobs, JsonObject check, int exit) =>
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)check["job_id"]!), Checked(exit))).Response.StatusCode);

    [Fact]
    public async Task AFailedCheckIsSentBack()
    {
        var (jobs, check) = await Checking();
        await using var _ = jobs;
        await Ends(jobs, check, 1);

        // The failed check offers sending it back; the setup worker started from the base image has
        // nothing to do until then.
        var setup = await SetupRunTests.Steps(jobs);
        Assert.True((bool)setup["can_send_back"]!);
        await SetupRunTests.Hello(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(SetupRunTests.SetupWorker)).Response.StatusCode);

        // My comment sends it back: a new setup, the second, queued on devflow/setup-2.
        Assert.Equal(StatusCodes.Status204NoContent, (await SendBack(jobs, $"  {Comment}  ")).Response.StatusCode);
        setup = await SetupRunTests.Steps(jobs);
        Assert.False((bool)setup["can_send_back"]!);
        Assert.Equal(2, (int)setup["setup"]!["number"]!);
        Assert.Equal("devflow/setup-2", (string)setup["setup"]!["branch"]!);
        Assert.Equal("queued", (string)setup["setup"]!["state"]!);
        Assert.True((bool)setup["setup"]!["sent_back"]!);

        // The setup worker is handed its run, from the default branch on the new branch, with a
        // first run's brief and a section "Sent back": the files as they were checked, the check's
        // output and my comment, trimmed, each between fences, my comment as my input.
        var job = await SetupRunTests.Next(jobs);
        Assert.Equal(Job.Setup, (string)job["kind"]!);
        Assert.Equal(("devflow/setup-2", "main"), ((string)job["branch"]!, (string)job["base"]!));
        Assert.Null(job["to_fix"]);
        var brief = (string)job["brief"]!;
        Assert.Contains("# Set this project up for Dev flow", brief, StringComparison.Ordinal);
        Assert.Contains("## Sent back", brief, StringComparison.Ordinal);
        Assert.Contains($"```dockerfile\n{SetupRunTests.GoodRecipe}```", brief, StringComparison.Ordinal);
        Assert.Contains($"```json\n{Jobs.ProjectFile()}\n```", brief, StringComparison.Ordinal);
        Assert.Contains("It exited with 1", brief, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Output}\n```", brief, StringComparison.Ordinal);
        Assert.Contains("The owner's words", brief, StringComparison.Ordinal);
        Assert.Contains($"```text\n{Comment}\n```", brief, StringComparison.Ordinal);
        Assert.DoesNotContain($"  {Comment}", brief, StringComparison.Ordinal);

        // The recipe's worker, its version failed, still gets nothing.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // What it pushed is valid: its pull request opens as the setup's does, from its branch, and
        // its review is handed to the setup worker next.
        SetupRunTests.Stage(jobs, (".devflow/Dockerfile", "devflow/setup-2", SetupRunTests.GoodRecipe + "RUN apt-get install -y redis-server\n"), (".devflow/project.json", "devflow/setup-2", Jobs.ProjectFile()));
        var head = await SetupRunTests.Push(jobs, branch: "devflow/setup-2");
        Assert.Equal(StatusCodes.Status204NoContent, (await SetupRunTests.Send(jobs, job, SetupRunTests.Result(head, branch: "devflow/setup-2"))).Response.StatusCode);
        await SetupRunTests.Tend(jobs);
        var opened = Assert.Single(jobs.Fake.Writes(Jobs.User), write => write.Kind == "pull_request");
        Assert.Equal(("devflow/setup-2", "main", "Set up Dev flow"), (opened.Head, opened.Base, opened.Title));
        Assert.Equal("reviewing", (await SetupRunTests.Setup(jobs, 2)).State);
        var review = await SetupRunTests.Next(jobs);
        Assert.Equal(Job.Review, (string)review["kind"]!);
        Assert.Equal(head, (string)review["to_review"]!["commit"]!);

        // A fix of it keeps the section, so its fixer knows why it was sent back.
        var changes = Jobs.Result(SetupRunTests.SetupWorker, "devflow/setup-2", DevFlow.Tests.PullRequests.Built.Verdict("changes", DevFlow.Tests.PullRequests.Built.Finding(file: ".devflow/project.json", line: 1, what: "The check never starts Redis", why: "The tests connect to it")), checkExit: null);
        changes["head_commit"] = head;
        Assert.Equal(StatusCodes.Status204NoContent, (await SetupRunTests.Send(jobs, review, changes)).Response.StatusCode);
        var fix = await SetupRunTests.Next(jobs);
        Assert.Equal(Job.Setup, (string)fix["kind"]!);
        Assert.Contains("## Sent back", (string)fix["brief"]!, StringComparison.Ordinal);
        Assert.Contains("The check never starts Redis", (string)fix["brief"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyForAFailedCheck()
    {
        var (jobs, check) = await Checking();
        await using var _ = jobs;

        // While the check runs, there is nothing to send back.
        var refused = await SendBack(jobs, Comment);
        Assert.Equal(StatusCodes.Status409Conflict, refused.Response.StatusCode);
        Assert.False((bool)(await SetupRunTests.Steps(jobs))["can_send_back"]!);

        // Failed: a comment missing, empty or over 4,000 characters is refused first.
        await Ends(jobs, check, 1);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await SendBack(jobs, null)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await SendBack(jobs, "   ")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await SendBack(jobs, new string('a', 4001))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{Guid.NewGuid()}/setup/send-back", Contract.Token, new JsonObject { ["comment"] = Comment }.ToJsonString())).Response.StatusCode);

        // Another check asked for and running: refused while it runs.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Routes.App.Send("POST", $"/devflow/api/projects/{jobs.Project}/setup/check-again", Contract.Token, ("Origin", Link.Linking.Origin))).Response.StatusCode);
        var again = await jobs.Job();
        Assert.Equal(Job.RecipeCheck, (string)again["kind"]!);
        Assert.Equal(StatusCodes.Status409Conflict, (await SendBack(jobs, Comment)).Response.StatusCode);

        // It fails again; 4,000 characters are taken, and a setup run is then under way: a second is
        // refused.
        await Ends(jobs, again, 1);
        Assert.Equal(StatusCodes.Status204NoContent, (await SendBack(jobs, new string('a', 4000))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, (await SendBack(jobs, Comment)).Response.StatusCode);
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM setups WHERE project_id = @project AND state <> 'done'", new { project = jobs.Project }, cancellationToken: Ct)));
        }

        // A check that passed is not sent back.
        var (passing, passingCheck) = await Checking();
        await using var __ = passing;
        await Ends(passing, passingCheck, 0);
        Assert.Equal(StatusCodes.Status409Conflict, (await SendBack(passing, Comment)).Response.StatusCode);
        Assert.False((bool)(await SetupRunTests.Steps(passing))["can_send_back"]!);
    }
}