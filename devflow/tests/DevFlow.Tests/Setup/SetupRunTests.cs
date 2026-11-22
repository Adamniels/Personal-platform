using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Setup;

using Jobs = DevFlow.Tests.Jobs.Jobs;
using PullRequestSupport = DevFlow.Tests.PullRequests.Built;

/// <summary>
/// The setup run: a project whose default branch has no valid <c>.devflow/</c> is set up by an
/// agent, on a worker started from the base image itself, which reports no recipe version; what it
/// pushed is validated, and its pull request opened as the app, reviewed and fixed like a card's.
/// See "Setting up a project" in devflow/README.md.
/// </summary>
public sealed class SetupRunTests
{
    /// <summary>The worker started from the base image, which reports no recipe version.</summary>
    private static readonly Guid SetupWorker = Guid.Parse("0190a8f0-0000-7000-8000-00000000f5e7");

    private const string GoodRecipe = "ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\nRUN apt-get update && apt-get install -y postgresql\n";

    private static CancellationToken Ct => Jobs.Ct;

    private static readonly DevFlow.CodeHost.Repository Repo = new(Reading.Reading.Installation, Reading.Reading.RepositoryId, "Adamniels", "Personal-platform");

    /// <summary>A project with one labelled card and no <c>.devflow/</c> on its default branch,
    /// its setup worker said hello.</summary>
    private static async Task<Jobs> Unset()
    {
        var jobs = await Jobs.Start([Jobs.Card(1)], projectFile: null, checkedRecipe: false);
        await Hello(jobs);
        return jobs;
    }

    /// <summary>The setup worker says hello, built from no recipe on the base image.</summary>
    private static async Task Hello(Jobs jobs)
    {
        var hello = new JsonObject { ["worker_id"] = SetupWorker, ["name"] = "devflow-fodr-setup", ["recipe_version"] = WorkerApi.NoVersion, ["base_image"] = Jobs.Image };
        var response = await Workers.Workers.AsWorker(jobs.Routes.App, Workers.Workers.HelloPath, hello.ToJsonString(), null, Workers.Workers.Bearer(jobs.Key));
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
    }

    /// <summary>The world with the given files staged, each on its branch, besides the card.</summary>
    private static void Stage(Jobs jobs, params (string Path, string Branch, string Text)[] files)
    {
        foreach (var (path, branch, text) in files)
        {
            Assert.True(jobs.Fake.PutFile(Jobs.User, Repo, path, branch, Encoding.UTF8.GetBytes(text)));
        }
    }

    /// <summary>A setup report in its form, with two things it could not settle.</summary>
    internal static JsonObject Report(string summary = "Wrote a recipe with Postgres, and a check that starts it.") => new()
    {
        ["summary"] = summary,
        ["proposed"] = new JsonObject { ["preset"] = "guided", ["why"] = "Its migrations are worth a look.", ["paths"] = new JsonArray("migrations/**") },
        ["variables"] = new JsonArray(new JsonObject { ["name"] = "STRIPE_TEST_KEY", ["why"] = "The payment tests call Stripe's test mode." }),
        ["unsettled"] = new JsonArray(
            new JsonObject { ["kind"] = "real_secret", ["detail"] = "The export tests upload to the real bucket." },
            new JsonObject { ["kind"] = "containers_in_tests", ["detail"] = "The integration tests start Redis with Testcontainers." }),
        ["decisions"] = new JsonArray(),
        ["handover"] = "The check starts Postgres before the tests.",
    };

    /// <summary>A setup's result as its worker sends one: the report, no check, and the commit it
    /// pushed.</summary>
    private static JsonObject Result(string head, JsonNode? report = null, string branch = "devflow/setup")
    {
        var result = Jobs.Result(SetupWorker, branch, report ?? Report(), checkExit: null);
        result["head_commit"] = head;
        return result;
    }

    /// <summary>The worker pushes a commit to the setup's branch, as a worker pushes what its agent
    /// left; the commit.</summary>
    private static async Task<string> Push(Jobs jobs, string branch = "devflow/setup", string from = "main")
    {
        if (await jobs.Fake.BranchHead(Jobs.User, Repo, "main", Ct) is not CodeHostResult<string>.Found)
        {
            Assert.Fail("the fake's default branch has no commit");
        }

        return (await jobs.Fake.CommitOnBranch(Jobs.User, Repo, branch, from, "Set up Dev flow", new Committer(FakeCodeHost.AppLogin, FakeCodeHost.AppEmail), Ct))!;
    }

    private static Task<HttpContext> Send(Jobs jobs, JsonObject job, JsonObject result) =>
        jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result);

    /// <summary>The setup worker's next job, a minute on, as runs are ordered by when they began.</summary>
    private static async Task<JsonObject> Next(Jobs jobs)
    {
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        return await jobs.Job(SetupWorker);
    }

    private static async Task<SetupRow> Setup(Jobs jobs, int number = 1)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<SetupRow>(new CommandDefinition(
            """
            SELECT state AS State, reason AS Reason, pull_request_number AS PullRequest, fix_rounds AS FixRounds, failures AS Failures
            FROM setups WHERE project_id = @project AND number = @number
            """,
            new { project = jobs.Project, number },
            cancellationToken: Ct));
    }

    private static async Task<List<(string Kind, string? Result, string? Reason, int? Exit, string? Command)>> Runs(Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(string, string?, string?, int?, string?)>(new CommandDefinition(
            "SELECT kind, result, reason, check_exit_code, check_command FROM runs WHERE project_id = @project AND setup_number IS NOT NULL ORDER BY started_at, id",
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    private static Task Tend(Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<DevFlow.Setup.SetupLoop>().TendPending(jobs.Project, Jobs.User, Ct);

    private static async Task<JsonNode> Steps(Jobs jobs)
    {
        var response = await jobs.Routes.App.Get($"/devflow/api/projects/{jobs.Project}/setup", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!;
    }

    [Fact]
    public async Task AProjectWithoutDevflowGetsASetupRun()
    {
        await using var jobs = await Unset();

        // A worker with no recipe version, of a project whose default branch has no .devflow/,
        // gets a setup job: no card and no check, the setup's branch from the default branch, the
        // setup report's form, the building model and a key that pushes.
        var job = await Next(jobs);
        Assert.Equal(Job.Setup, (string)job["kind"]!);
        Assert.Null(job["card"]);
        Assert.Equal("devflow/setup", (string)job["branch"]!);
        Assert.Equal("main", (string)job["base"]!);
        Assert.Equal("", (string)job["check"]!);
        Assert.Null(job["to_fix"]);
        Assert.Equal(SetupReport.Schema().ToJsonString(), job["report_schema"]!.ToJsonString());
        Assert.Equal("opus", (string)job["model"]!);
        Assert.Equal(Jobs.ClaudeToken, (string)job["claude_token"]!);
        Assert.Equal(WritePurpose.Push, jobs.Fake.Issued(Jobs.User, (string)job["push_key"]!)!.Value.Purpose);
        var brief = (string)job["brief"]!;
        Assert.Contains("# Set this project up for Dev flow", brief, StringComparison.Ordinal);
        Assert.Contains("You are on the branch `devflow/setup`, made from `main`", brief, StringComparison.Ordinal);
        Assert.Contains("Every value a test needs is a throwaway", brief, StringComparison.Ordinal);
        Assert.Contains("Say what you could not settle rather than guess", brief, StringComparison.Ordinal);
        Assert.Contains("You cannot run the check", brief, StringComparison.Ordinal);
        Assert.Equal("queued", (await Setup(jobs)).State);
        Assert.Equal([("setup", (string?)null, (string?)null, (int?)null, (string?)DevFlow.Setup.SetupFiles.Validation)], await Runs(jobs));

        // One setup run at a time: asked again while it runs, it gets nothing; the setup worker is
        // never handed the card, nor a check of its version.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(SetupWorker)).Response.StatusCode);

        // A worker with no recipe of a project that has a valid one gets nothing and is told to
        // rebuild, as a worker on an old recipe is.
        Stage(jobs, (".devflow/Dockerfile", "main", GoodRecipe), (".devflow/project.json", "main", Jobs.ProjectFile()));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition("UPDATE runs SET finished_at = now(), result = 'lost' WHERE project_id = @project AND setup_number IS NOT NULL", new { project = jobs.Project }, cancellationToken: Ct));
        }

        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(SetupWorker)).Response.StatusCode);
        var listed = await jobs.Routes.App.Get($"/devflow/api/projects/{jobs.Project}/workers", Contract.Token);
        var worker = JsonNode.Parse(await Harness.Body(listed))!["workers"]!.AsArray().Single(each => (string)each!["id"]! == SetupWorker.ToString());
        Assert.Equal("recipe", (string)worker!["rebuild"]!);
    }

    [Fact]
    public async Task ValidDevflowNeedsNoSetupRun()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1)], projectFile: null, checkedRecipe: false);
        Stage(jobs, (".devflow/Dockerfile", "main", GoodRecipe), (".devflow/project.json", "main", Jobs.ProjectFile()));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));

        // Both files valid on the default branch: the setup step and the recipe's are done, and the
        // next is a worker on the recipe; no setup is made, and a worker with no recipe is told to
        // rebuild.
        var steps = (await Steps(jobs))["steps"]!.AsArray();
        Assert.Equal(["setup", "recipe", "worker", "check"], steps.Select(step => (string)step!["step"]!));
        Assert.Equal([true, true, false, false], steps.Select(step => (bool)step!["done"]!));
        await Hello(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(SetupWorker)).Response.StatusCode);
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        Assert.Equal(0, await admin.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM setups WHERE project_id = @project", new { project = jobs.Project }, cancellationToken: Ct)));
    }

    [Fact]
    public async Task ItsPullRequestIsReviewedAndFixed()
    {
        await using var jobs = await Unset();
        var job = await Next(jobs);
        Stage(jobs, (".devflow/Dockerfile", "devflow/setup", GoodRecipe), (".devflow/project.json", "devflow/setup", Jobs.ProjectFile()));
        var first = await Push(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, job, Result(first))).Response.StatusCode);

        // Valid: its pull request opens next, as the app, from the setup's branch into the default
        // branch, titled "Set up Dev flow", saying what it wrote and could not settle.
        Assert.Equal("checking", (await Setup(jobs)).State);
        Assert.Equal(("setup", (string?)"succeeded", (string?)null, (int?)0, (string?)DevFlow.Setup.SetupFiles.Validation), Assert.Single(await Runs(jobs)));
        await Tend(jobs);
        var opened = Assert.Single(jobs.Fake.Writes(Jobs.User), write => write.Kind == "pull_request");
        Assert.Equal(("devflow/setup", "main", "Set up Dev flow"), (opened.Head, opened.Base, opened.Title));
        Assert.Equal(FakeCodeHost.AppLogin, opened.By);
        Assert.Contains("Wrote a recipe with Postgres", opened.Body, StringComparison.Ordinal);
        var setup = await Setup(jobs);
        Assert.Equal(("reviewing", (int?)opened.Number), (setup.State, setup.PullRequest));

        // Its review, with the reviewing model and the setup reviewer's brief, of the commit pushed.
        var review = await Next(jobs);
        Assert.Equal(Job.Review, (string)review["kind"]!);
        Assert.Equal(opened.Number, (int)review["card"]!["number"]!);
        Assert.Equal("devflow/setup", (string)review["branch"]!);
        Assert.Equal(first, (string)review["to_review"]!["commit"]!);
        Assert.Equal(ReviewVerdict.Schema().ToJsonString(), review["report_schema"]!.ToJsonString());
        Assert.Equal(WritePurpose.Fetch, jobs.Fake.Issued(Jobs.User, (string)review["push_key"]!)!.Value.Purpose);
        Assert.Contains("# Review this project's setup", (string)review["brief"]!, StringComparison.Ordinal);
        Assert.Contains("The recipe installs only what the check and the start need", (string)review["brief"]!, StringComparison.Ordinal);
        Assert.Equal(0, (int)review["to_review"]!["check"]!["exit_code"]!);

        // Changes: a fix with the setup's brief, from the commit, in round 1.
        var changes = Jobs.Result(SetupWorker, "devflow/setup", PullRequestSupport.Verdict("changes", PullRequestSupport.Finding(file: ".devflow/Dockerfile", line: 3, what: "The recipe installs Postgres's server and its docs", why: "Only the client is needed")), checkExit: null);
        changes["head_commit"] = first;
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, review, changes)).Response.StatusCode);
        Assert.Equal(("fixing", 1), ((await Setup(jobs)).State, (await Setup(jobs)).FixRounds));
        var fix = await Next(jobs);
        Assert.Equal(Job.Setup, (string)fix["kind"]!);
        Assert.Equal(first, (string)fix["to_fix"]!["commit"]!);
        Assert.Equal(1, (int)fix["to_fix"]!["round"]!);
        Assert.Contains("This is fix round 1 of 3", (string)fix["brief"]!, StringComparison.Ordinal);
        Assert.Contains("The recipe installs Postgres's server and its docs", (string)fix["brief"]!, StringComparison.Ordinal);
        Assert.Contains("# Set this project up for Dev flow", (string)fix["brief"]!, StringComparison.Ordinal);

        // The fix pushes a new commit, valid: reviewed again, of the new head, and the body is
        // written again from the fix's report.
        var second = await Push(jobs, from: "devflow/setup");
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, fix, Result(second, Report("Installs Postgres's client only.")))).Response.StatusCode);
        Assert.Equal("reviewing", (await Setup(jobs)).State);
        await Tend(jobs);
        var edited = Assert.Single(jobs.Fake.Writes(Jobs.User), write => write.Kind == "pull_request_edited");
        Assert.Contains("Installs Postgres's client only.", edited.Body, StringComparison.Ordinal);
        var again = await Next(jobs);
        Assert.Equal(Job.Review, (string)again["kind"]!);
        Assert.Equal(second, (string)again["to_review"]!["commit"]!);
        Assert.Contains("This is not the setup's first review", (string)again["brief"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatItWroteIsValidated()
    {
        await using var jobs = await Unset();

        // A pushed recipe that copies files is a failure in the setup, judged as a check that
        // failed: it runs again once.
        var job = await Next(jobs);
        Stage(jobs, (".devflow/Dockerfile", "devflow/setup", "ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\nCOPY . /app\n"), (".devflow/project.json", "devflow/setup", Jobs.ProjectFile()));
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, job, Result(await Push(jobs)))).Response.StatusCode);
        var run = Assert.Single(await Runs(jobs));
        Assert.Equal(("check_failed", 1), (run.Result, run.Exit));
        Assert.Equal("What it wrote is not valid: Line 3 of .devflow/Dockerfile is COPY, and a recipe cannot copy files into the image.", run.Reason);
        var setup = await Setup(jobs);
        Assert.Equal(("queued", 1), (setup.State, setup.Failures));
        Assert.StartsWith("Failed once, so it runs again: What it wrote is not valid", setup.Reason, StringComparison.Ordinal);

        // Run again on its branch, a project.json out of its schema: the second in a row stops it.
        var second = await Next(jobs);
        Assert.Equal(Job.Setup, (string)second["kind"]!);
        Assert.Equal("devflow/setup", (string)second["branch"]!);
        Stage(jobs, (".devflow/Dockerfile", "devflow/setup", GoodRecipe), (".devflow/project.json", "devflow/setup", """{"version": 1, "check": "npm test"}"""));
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, second, Result(await Push(jobs, from: "devflow/setup")))).Response.StatusCode);
        setup = await Setup(jobs);
        Assert.Equal("stopped", setup.State);
        Assert.StartsWith("Failed twice in a row: What it wrote is not valid: .devflow/project.json does not follow its schema", setup.Reason, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(SetupWorker)).Response.StatusCode);

        // Stopped before its pull request opened, it gets none: what it wrote is not valid, and his
        // comment from its step runs it again, see EveryStopRunsAgainOnMyComment.
        await Tend(jobs);
        Assert.DoesNotContain(jobs.Fake.Writes(Jobs.User), write => write.Kind == "pull_request");
        Assert.Equal(("stopped", (int?)null), ((await Setup(jobs)).State, (await Setup(jobs)).PullRequest));

        // A report out of its form is a failure in the setup too.
        Assert.Equal("report_invalid", DevFlow.Setup.SetupRunStore.Judge(DevFlow.Setup.SetupRunStore.SetupKind, Read(Result(new string('0', 40), new JsonObject { ["summary"] = "no form" })), null, 0, 3).Result);
    }

    /// <summary>Every way a setup stops before its pull request opened, each with whether its branch
    /// was pushed: a failure in it, a time limit, a report out of its form, nothing pushed, and what
    /// it wrote not valid.</summary>
    public static TheoryData<string, bool> Stops => new()
    {
        { "failed", true },
        { "timed_out", false },
        { "out_of_form", true },
        { "nothing_pushed", false },
        { "not_valid", true },
    };

    /// <summary>The result a setup's run sends for <paramref name="stop"/>, pushing to its branch
    /// first when that stop pushes.</summary>
    private static async Task<JsonObject> Stopping(Jobs jobs, string stop)
    {
        async Task<string> Pushed() =>
            await Push(jobs, from: await jobs.Fake.BranchHead(Jobs.User, Repo, "devflow/setup", Ct) is CodeHostResult<string>.Found ? "devflow/setup" : "main");

        switch (stop)
        {
            case "failed":
                var failed = Jobs.Failure(SetupWorker, "devflow/setup", inCard: true, "The run failed: it reached the most turns, 200 turns.");
                failed["head_commit"] = await Pushed();
                return failed;
            case "timed_out":
                var timedOut = Jobs.Failure(SetupWorker, "devflow/setup", inCard: true, "The run reached its time limit.");
                timedOut["failure"]!["timed_out"] = true;
                return timedOut;
            case "out_of_form":
                return Result(await Pushed(), new JsonObject { ["summary"] = "no form" });
            case "nothing_pushed":
                var nothing = Result(new string('0', 40));
                nothing["head_commit"] = null;
                return nothing;
            default:
                Stage(jobs, (".devflow/Dockerfile", "devflow/setup", "ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\nCOPY . /app\n"), (".devflow/project.json", "devflow/setup", Jobs.ProjectFile()));
                return Result(await Pushed());
        }
    }

    /// <summary>The setup stopped by <paramref name="stop"/>: once for a time limit, which stops it
    /// at once, and twice in a row for every other.</summary>
    private static async Task Stop(Jobs jobs, string stop)
    {
        for (var run = 0; run < (stop == "timed_out" ? 1 : 2); run++)
        {
            var job = await Next(jobs);
            Assert.Equal(Job.Setup, (string)job["kind"]!);
            Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, job, await Stopping(jobs, stop))).Response.StatusCode);
        }

        Assert.Equal(("stopped", (int?)null), ((await Setup(jobs)).State, (await Setup(jobs)).PullRequest));
    }

    /// <summary>The owner's comment from the setup's step.</summary>
    private static Task<HttpContext> RunAgain(Jobs jobs, string? comment) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/setup/run-again", Contract.Token,
            new JsonObject { ["comment"] = comment }.ToJsonString());

    [Theory]
    [MemberData(nameof(Stops))]
    public async Task EveryStopRunsAgainOnMyComment(string stop, bool pushed)
    {
        await using var jobs = await Unset();
        await Stop(jobs, stop);

        // Stopped before its pull request opened, by any of them: nothing is handed out, no pull
        // request is opened, and it shows on its step and on "Waiting for you" with why.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(SetupWorker)).Response.StatusCode);
        await Tend(jobs);
        Assert.DoesNotContain(jobs.Fake.Writes(Jobs.User), write => write.Kind == "pull_request");
        var step = (await Steps(jobs))["setup"]!;
        Assert.Equal("stopped", (string)step["state"]!);
        Assert.Null(step["pull_request_url"]);
        Assert.False(string.IsNullOrEmpty((string?)step["reason"]));
        var waiting = JsonNode.Parse(await Harness.Body(await jobs.Routes.App.Get("/devflow/api/waiting", Contract.Token)))!;
        Assert.Equal("stopped", (string)Assert.Single(waiting["setup_runs"]!.AsArray())!["state"]!);

        // His comment from the step runs it again: a fix of its branch when it was pushed, a first
        // run again otherwise, with his comment in its brief; still the same setup.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await RunAgain(jobs, "  ")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await RunAgain(jobs, " Use Postgres 16, as the README says. ")).Response.StatusCode);
        Assert.Equal((pushed ? "fixing" : "queued", DevFlow.Setup.SetupRunStore.RunAgainReason, 0), ((await Setup(jobs)).State, (await Setup(jobs)).Reason, (await Setup(jobs)).Failures));
        Assert.Equal(StatusCodes.Status409Conflict, (await RunAgain(jobs, "Again.")).Response.StatusCode);
        var again = await Next(jobs);
        Assert.Equal(Job.Setup, (string)again["kind"]!);
        Assert.Equal("devflow/setup", (string)again["branch"]!);
        Assert.Equal(pushed, again["to_fix"] is not null);
        var brief = (string)again["brief"]!;
        Assert.Contains("The setup ran before, and the owner commented on it", brief, StringComparison.Ordinal);
        Assert.Contains("Use Postgres 16, as the README says.", brief, StringComparison.Ordinal);
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        Assert.Equal(1, await admin.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM setups WHERE project_id = @project", new { project = jobs.Project }, cancellationToken: Ct)));
    }

    [Fact]
    public async Task AStoppedSetupIsForgottenOnceDevflowIsValid()
    {
        await using var jobs = await Unset();
        await Stop(jobs, "timed_out");

        // .devflow/ written on the default branch some other way: nothing of the setup is asked of
        // him any more, and it is not run again.
        Stage(jobs, (".devflow/Dockerfile", "main", GoodRecipe), (".devflow/project.json", "main", Jobs.ProjectFile()));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        var waiting = JsonNode.Parse(await Harness.Body(await jobs.Routes.App.Get("/devflow/api/waiting", Contract.Token)))!;
        Assert.Empty(waiting["setup_runs"]!.AsArray());
        Assert.Equal(StatusCodes.Status409Conflict, (await RunAgain(jobs, "Run it again.")).Response.StatusCode);
    }

    /// <summary>The setup run through to its approved pull request, valid and reviewed; the commit
    /// it waits at.</summary>
    private static async Task<string> Approved(Jobs jobs)
    {
        var job = await Next(jobs);
        Stage(jobs, (".devflow/Dockerfile", "devflow/setup", GoodRecipe), (".devflow/project.json", "devflow/setup", Jobs.ProjectFile()));
        var head = await Push(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, job, Result(head))).Response.StatusCode);
        await Tend(jobs);
        var review = await Next(jobs);
        var approve = Jobs.Result(SetupWorker, "devflow/setup", PullRequestSupport.Verdict(), checkExit: null);
        approve["head_commit"] = head;
        Assert.Equal(StatusCodes.Status204NoContent, (await Send(jobs, review, approve)).Response.StatusCode);
        return head;
    }

    /// <summary>The owner merges the setup's pull request from its step, naming the commit the page
    /// showed.</summary>
    private static Task<HttpContext> MergeFromStep(Jobs jobs, string head) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/setup/merge", Contract.Token,
            new JsonObject { ["head_commit"] = head }.ToJsonString());

    [Theory]
    [InlineData("never", false)]
    [InlineData("sometimes", false)]
    [InlineData("always", false)]
    [InlineData("never", true)]
    public async Task ItAlwaysWaitsForMe(string waits, bool onGitHub)
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1)], projectFile: null, checkedRecipe: false, signIn: true);
        await Merging.Merged.Waits(jobs, waits);
        await Hello(jobs);
        var head = await Approved(jobs);

        // Approved, it waits for the owner whatever the project's settings: the merge check, run as
        // on the minute, never takes it.
        Assert.Equal(("waiting_for_owner", DevFlow.Setup.SetupRunStore.Approved), ((await Setup(jobs)).State, (await Setup(jobs)).Reason));
        await jobs.Routes.App.Services.GetRequiredService<DevFlow.Merging.MergeChecker>().CheckPending(jobs.Project, Jobs.User, Ct);
        await Tend(jobs);
        Assert.DoesNotContain(jobs.Fake.Writes(Jobs.User), write => write.Kind == "merge");
        Assert.Equal("waiting_for_owner", (await Setup(jobs)).State);

        // On "Waiting for you", under Setup, counted.
        var waiting = JsonNode.Parse(await Harness.Body(await jobs.Routes.App.Get("/devflow/api/waiting", Contract.Token)))!;
        var listed = Assert.Single(waiting["setup_runs"]!.AsArray())!;
        Assert.Equal(jobs.Project.ToString(), (string)listed["project_id"]!);
        Assert.Equal("waiting_for_owner", (string)listed["state"]!);
        Assert.Equal(head, (string)listed["head_commit"]!);
        Assert.Equal(1, (int)waiting["count"]!);

        // Merged by the owner, from the step or on GitHub: the step is done once the default branch
        // has the files.
        if (onGitHub)
        {
            var token = Assert.IsType<DevFlow.Link.UserToken.Usable>(await jobs.Routes.App.Services.GetRequiredService<DevFlow.Link.UserTokens>().For(Jobs.User, Ct)).Token;
            Assert.IsType<MergeResult.Merged>(await jobs.Fake.Merge(Jobs.User, token, Repo, (await Setup(jobs)).PullRequest!.Value, head, Ct));
        }
        else
        {
            Assert.Equal(StatusCodes.Status409Conflict, (await MergeFromStep(jobs, new string('e', 40))).Response.StatusCode);
            Assert.Equal(StatusCodes.Status204NoContent, (await MergeFromStep(jobs, head)).Response.StatusCode);
            var merged = Assert.Single(jobs.Fake.Writes(Jobs.User), write => write.Kind == "merge");
            Assert.Equal(Jobs.Owner, merged.By);
        }

        Stage(jobs, (".devflow/Dockerfile", "main", GoodRecipe), (".devflow/project.json", "main", Jobs.ProjectFile()));
        await Tend(jobs);
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        Assert.Equal("done", (await Setup(jobs)).State);
        var steps = (await Steps(jobs))["steps"]!.AsArray();
        Assert.Equal([true, true, false, false], steps.Select(step => (bool)step!["done"]!));
        waiting = JsonNode.Parse(await Harness.Body(await jobs.Routes.App.Get("/devflow/api/waiting", Contract.Token)))!;
        Assert.Empty(waiting["setup_runs"]!.AsArray());
    }

    [Fact]
    public async Task WhatItCouldNotSettleIsShown()
    {
        await using var jobs = await Unset();

        // Before any run, the step says why it is due and shows the command that starts its worker.
        var before = await Steps(jobs);
        Assert.Equal(".devflow/Dockerfile is missing.", (string)before["problem"]!);
        Assert.Contains("--name devflow-personal-platform-setup", (string)before["setup_command"]!, StringComparison.Ordinal);
        Assert.Null(before["setup"]);

        await Approved(jobs);

        // The report's unsettled, each with its kind, on the step, with the review and the pull
        // request to merge.
        var setup = (await Steps(jobs))["setup"]!;
        Assert.Equal("waiting_for_owner", (string)setup["state"]!);
        Assert.Equal("devflow/setup", (string)setup["branch"]!);
        Assert.NotNull(setup["pull_request_url"]);
        Assert.Equal("approve", (string)setup["review"]!["verdict"]!);
        var unsettled = setup["report"]!["unsettled"]!.AsArray();
        Assert.Equal(["real_secret", "containers_in_tests"], unsettled.Select(each => (string)each!["kind"]!));
        Assert.Equal("The export tests upload to the real bucket.", (string)unsettled[0]!["detail"]!);
        Assert.Equal("guided", (string)setup["report"]!["proposed"]!["preset"]!);
        Assert.Equal(["STRIPE_TEST_KEY"], setup["report"]!["variables"]!.AsArray().Select(each => (string)each!["name"]!));

        // And in the pull request's body, named by kind.
        var opened = Assert.Single(jobs.Fake.Writes(Jobs.User), write => write.Kind == "pull_request");
        Assert.Contains("## What it could not settle", opened.Body, StringComparison.Ordinal);
        Assert.Contains("- **Needs a real secret.** The export tests upload to the real bucket.", opened.Body, StringComparison.Ordinal);
        Assert.Contains("- **Its tests start containers.** The integration tests start Redis with Testcontainers.", opened.Body, StringComparison.Ordinal);
        Assert.Contains("`STRIPE_TEST_KEY`: The payment tests call Stripe's test mode.", opened.Body, StringComparison.Ordinal);
    }

    private static DevFlow.Jobs.RunResult Read(JsonObject result) =>
        DevFlow.Jobs.JobEndpoints.Read(System.Text.Json.JsonSerializer.Deserialize<ResultRequest>(result.ToJsonString(), WorkerApi.Json)!)!;

    private sealed record SetupRow(string State, string? Reason, int? PullRequest, int FixRounds, int Failures);
}