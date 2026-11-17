using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Reading;
using DevFlow.Tests.Workers;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A job's progress and result: the report checked against its form, everything the worker sent
/// stored on the run, the card moved on as the slice settles, a failure outside the card putting it
/// back, a run that never returns lost, and only the job's own worker heard. Against the fake and a
/// real Postgres. See "Jobs" in devflow/README.md.
/// </summary>
public sealed class ResultTests
{
    /// <summary>A report that breaks its form, by name, so each case says which.</summary>
    public static TheoryData<string> Broken => ["missing", "unknown", "mistyped", "null", "outcome", "stopped without why", "nested", "not an object"];

    private static JsonNode? Break(string how)
    {
        var report = Jobs.GoodReport();
        switch (how)
        {
            case "missing":
                report.Remove("handover");
                break;
            case "unknown":
                report["files_changed"] = new JsonArray("a.ts");
                break;
            case "mistyped":
                report["tasks"] = "all of them";
                break;
            case "null":
                report["summary"] = null;
                break;
            case "outcome":
                report["outcome"] = "finished";
                break;
            case "stopped without why":
                report["outcome"] = "stopped";
                break;
            case "nested":
                report["decisions"]![0]!.AsObject().Remove("for_owner");
                break;
            case "not an object":
                return JsonValue.Create("done");
        }

        return report;
    }

    /// <summary>The project's one card handed to the first worker.</summary>
    private static async Task<(Jobs Jobs, Guid Id, string Branch)> Handed()
    {
        var jobs = await Jobs.Start();
        var job = await jobs.Job();
        return (jobs, (Guid)job["job_id"]!, (string)job["branch"]!);
    }

    [Theory]
    [MemberData(nameof(Broken))]
    public async Task AReportIsCheckedAgainstItsSchema(string how)
    {
        var (jobs, id, branch) = await Handed();
        await using var _ = jobs;

        var response = await jobs.Post(WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, branch, Break(how)));

        // The result is taken; the report is a failure in the card, with why.
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        var card = await jobs.Card(1);
        Assert.Equal("stopped", card.State);
        Assert.StartsWith("The report did not match its form: ", card.Reason, StringComparison.Ordinal);
        var run = Assert.Single(await jobs.Runs());
        Assert.Equal("report_invalid", run.Result);
        Assert.Equal(card.Reason, run.Reason);
        Assert.NotNull(run.FinishedAt);
        // The report is kept as it came, for the owner to read.
        Assert.True(JsonNode.DeepEquals(Break(how), JsonNode.Parse(run.Report!)));
    }

    [Fact]
    public async Task AReportsFaultIsNamed()
    {
        var (jobs, id, branch) = await Handed();
        await using var _ = jobs;
        var report = Jobs.GoodReport();
        report.Remove("handover");

        await jobs.Post(WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, branch, report));

        Assert.Contains("handover", (await jobs.Card(1)).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportTypeTakesTheGoodReport()
    {
        using var document = JsonDocument.Parse(Jobs.GoodReport().ToJsonString());
        Assert.True(Report.TryRead(document.RootElement, out var report, out var error), error);
        Assert.Equal(ReportOutcome.Done, report.Outcome);
        Assert.Equal("The empty state is in list.tsx; its copy is in en.json.", report.Handover);
        Assert.True(Assert.Single(report.Pages).Looked);
    }

    [Fact]
    public async Task TheResultIsRecordedAsItCame()
    {
        var (jobs, id, branch) = await Handed();
        await using var _ = jobs;

        // The agent has finished, and the worker checks.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(20));
        var progress = await jobs.Post(WorkerApi.ProgressPath(id), new JsonObject { ["worker_id"] = Jobs.FirstWorker, ["state"] = "checking" });
        Assert.Equal(StatusCodes.Status204NoContent, progress.Response.StatusCode);
        Assert.Equal("checking", (await jobs.Card(1)).State);
        Assert.NotNull(Assert.Single(await jobs.Runs()).CheckingAt);

        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(5));
        var sent = Jobs.Result(Jobs.FirstWorker, branch);
        var response = await jobs.Post(WorkerApi.ResultPath(id), sent);
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);

        var run = Assert.Single(await jobs.Runs());
        Assert.Equal("succeeded", run.Result);
        Assert.Null(run.Reason);
        Assert.Equal(jobs.Routes.Time.GetUtcNow().UtcDateTime, run.FinishedAt);
        Assert.True(JsonNode.DeepEquals(sent["report"], JsonNode.Parse(run.Report!)));
        Assert.Equal(0, run.CheckExitCode);
        Assert.Equal("Tests: 12 passed", run.CheckOutput);
        Assert.Equal(branch, run.Branch);
        Assert.Equal(new string('a', 40), run.HeadCommit);
        Assert.Equal("oauth_token", run.AuthMethod);
        Assert.True(JsonNode.DeepEquals(sent["usage"], JsonNode.Parse(run.Usage!)));
        Assert.Equal(41, run.InputTokens);
        Assert.Equal(6113, run.OutputTokens);
        Assert.Equal(18204, run.CacheCreationInputTokens);
        Assert.Equal(512870, run.CacheReadInputTokens);
        Assert.Equal(1.8342m, run.CostUsd);
        Assert.Equal(23, run.Turns);
        Assert.Equal(200000, run.DurationMs);
        Assert.Equal("a warning", run.Stderr);

        // A run whose check passed waits for its pull request, a later part's.
        var card = await jobs.Card(1);
        Assert.Equal("checking", card.State);
        Assert.Equal("Built, and its check passed; its pull request opens in a later part", card.Reason);

        // A result is taken once.
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Post(WorkerApi.ResultPath(id), sent)).Response.StatusCode);
    }

    public static TheoryData<string, string, string> Stops => new()
    {
        { "stopped", "stopped", "The agent stopped: the card asks for two opposite things" },
        { "check failed", "check_failed", "The check failed with exit code 1" },
        { "check missing", "check_failed", "The check did not run" },
        { "failed in the card", "failed_in_card", "The run failed: it reached the most turns, 200 turns" },
    };

    [Theory]
    [MemberData(nameof(Stops))]
    public async Task ACardStopsWithWhy(string how, string result, string reason)
    {
        var (jobs, id, branch) = await Handed();
        await using var _ = jobs;
        var sent = how switch
        {
            "stopped" => Jobs.Result(Jobs.FirstWorker, branch, Jobs.GoodReport("stopped", "the card asks for two opposite things"), checkExit: null),
            "check failed" => Jobs.Result(Jobs.FirstWorker, branch, checkExit: 1),
            "check missing" => Jobs.Result(Jobs.FirstWorker, branch, checkExit: null),
            _ => Jobs.Failure(Jobs.FirstWorker, branch, inCard: true, "it reached the most turns, 200 turns"),
        };

        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath(id), sent)).Response.StatusCode);

        var card = await jobs.Card(1);
        Assert.Equal("stopped", card.State);
        Assert.Equal(reason, card.Reason);
        var run = Assert.Single(await jobs.Runs());
        Assert.Equal(result, run.Result);
        Assert.Equal(reason, run.Reason);

        // A stopped card is not handed out again.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
    }

    [Fact]
    public async Task AFailureOutsideTheCardPutsItBack()
    {
        var (jobs, id, branch) = await Handed();
        await using var _ = jobs;

        var response = await jobs.Post(WorkerApi.ResultPath(id), Jobs.Failure(Jobs.FirstWorker, branch, inCard: false, "Claude's API answered an error (529)"));

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        var card = await jobs.Card(1);
        Assert.Equal("queued", card.State);
        Assert.Null(card.Reason);
        var run = Assert.Single(await jobs.Runs());
        Assert.Equal("failed_outside_card", run.Result);
        Assert.Equal("Claude's API answered an error (529)", run.Reason);

        // Nothing counts against it: it is handed out again, as a new run.
        var again = await jobs.Job();
        Assert.Equal(1, (int)again["card"]!["number"]!);
        Assert.NotEqual(id, (Guid)again["job_id"]!);
        Assert.Equal(2, (await jobs.Runs()).Count);
    }

    [Fact]
    public async Task ARunThatNeverReturnsIsLost()
    {
        var (jobs, id, _) = await Handed();
        await using var _jobs = jobs;

        // 99 minutes on, with the worker gone: not yet.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(99));
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        Assert.Equal("building", (await jobs.Card(1)).State);

        // 100 minutes, with the worker seen 5 minutes ago: it is still there, so not lost.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        await jobs.Routes.Door.InUserTransaction(Jobs.User, transaction => Dapper.SqlMapper.ExecuteAsync(
            transaction.Connection!, "UPDATE workers SET last_seen_at = @at", new { at = jobs.Routes.Time.GetUtcNow().AddMinutes(-5) }, transaction), Jobs.Ct);
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        Assert.Equal("building", (await jobs.Card(1)).State);

        // Not seen for 10 minutes: lost, and the card is queued again, nothing against it.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        var card = await jobs.Card(1);
        Assert.Equal("queued", card.State);
        var run = Assert.Single(await jobs.Runs());
        Assert.Equal("lost", run.Result);
        Assert.Equal("No result came back, and its worker was not seen for 10 minutes", run.Reason);
        Assert.NotNull(run.FinishedAt);

        // A result that comes back after is for a finished job.
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Post(WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, run.Branch))).Response.StatusCode);
    }

    [Fact]
    public async Task OnlyTheJobsWorkerReports()
    {
        var (jobs, id, branch) = await Handed();
        await using var _ = jobs;
        await jobs.SayHello(Jobs.SecondWorker);

        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Post(WorkerApi.ResultPath(id), Jobs.Result(Jobs.SecondWorker, branch))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Post(WorkerApi.ProgressPath(id), new JsonObject { ["worker_id"] = Jobs.SecondWorker, ["state"] = "checking" })).Response.StatusCode);
        Assert.Equal("building", (await jobs.Card(1)).State);
        Assert.Null(Assert.Single(await jobs.Runs()).Result);

        // Another project's key does not reach it either.
        var other = await jobs.Routes.Project(Jobs.User, repository: 9002, name: "fodr");
        var otherKey = await Workers.Workers.NewKey(jobs.Routes, other);
        var result = await Workers.Workers.AsWorker(jobs.Routes.App, WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, branch).ToJsonString(), null, Workers.Workers.Bearer(otherKey));
        Assert.Equal(StatusCodes.Status404NotFound, result.Response.StatusCode);

        // Without a key, 401.
        var none = await Workers.Workers.AsWorker(jobs.Routes.App, WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, branch).ToJsonString());
        Assert.Equal(StatusCodes.Status401Unauthorized, none.Response.StatusCode);
    }

    [Theory]
    [InlineData("outcome", "\"done\"")]
    [InlineData("worker_id", "null")]
    [InlineData("branch", "\"\"")]
    [InlineData("usage", "[]")]
    [InlineData("duration_ms", "-1")]
    [InlineData("stderr", "null")]
    [InlineData("head_commit", "\"main\"")]
    [InlineData("failure", "{\"reason\": \"x\", \"in_card\": true}")]
    public async Task AResultThatBreaksARuleIsRefused(string field, string value)
    {
        var (jobs, id, branch) = await Handed();
        await using var _ = jobs;
        var sent = Jobs.Result(Jobs.FirstWorker, branch);
        sent[field] = JsonNode.Parse(value);

        var response = await jobs.Post(WorkerApi.ResultPath(id), sent);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Response.StatusCode);
        Assert.Contains(field, (string)(await Workers.Workers.Json(response))["detail"]!, StringComparison.Ordinal);
        Assert.Equal("building", (await jobs.Card(1)).State);
    }

    [Fact]
    public async Task ACardWhoseLabelWasTakenOffIsRemovedWhenItsRunEnds()
    {
        var (jobs, id, branch) = await Handed();
        await using var _ = jobs;

        // The label comes off while it builds: it finishes, marked.
        var world = jobs.Fake.World(Jobs.User);
        var repository = world.Installations[0].Repositories[0] with { Issues = [] };
        jobs.Fake.Replace(Jobs.User, world with { Installations = [world.Installations[0] with { Repositories = [repository] }] });
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        Assert.Equal("building", (await jobs.Card(1)).State);

        // Its run ends, and the card goes; the run stays, for its usage.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, branch))).Response.StatusCode);
        Assert.Empty(await jobs.Cards());
        Assert.Equal("succeeded", Assert.Single(await jobs.Runs()).Result);
    }
}