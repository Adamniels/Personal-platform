using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Protocol;
using DevFlow.Tests.Features;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.PullRequests;
using DevFlow.Tests.Stops;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// Stopping a run from the page: the owner's route marks it, the worker's next log post is answered
/// stop, and the run the worker then ends <c>stopped</c> is recorded <c>stopped_by_owner</c>, which is
/// never a failure: a card waits for the owner, "Stopped by you", unmarked on its issue; a feature's
/// own run holds its feature; and a reading, a setup run or a check is put back as a lost one is.
/// Against real Postgres and the fake code host. See "Jobs" and "Stops" in devflow/README.md.
/// </summary>
public sealed class StopRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A result as a worker sends one for a run the owner stopped: no report, no check and
    /// no failure, the head commit it pushed, or none.</summary>
    internal static JsonObject Stopped(Guid worker, string branch, string? head = null) => new()
    {
        ["worker_id"] = worker,
        ["outcome"] = "stopped",
        ["report"] = null,
        ["failure"] = null,
        ["check"] = null,
        ["branch"] = branch,
        ["head_commit"] = head,
        ["auth_method"] = "oauth_token",
        ["usage"] = new JsonObject { ["usage"] = new JsonObject { ["input_tokens"] = 12, ["cache_creation_input_tokens"] = 0, ["cache_read_input_tokens"] = 0, ["output_tokens"] = 34 } },
        ["duration_ms"] = 5000,
        ["stderr"] = "",
    };

    /// <summary>A prepared stopped result for the fake worker, which fills the worker, the branch
    /// and the commit it pushed.</summary>
    internal static JsonObject PreparedStopped()
    {
        var result = Stopped(Guid.Empty, "");
        result.Remove("worker_id");
        result.Remove("branch");
        result.Remove("head_commit");
        return result;
    }

    /// <summary>The owner asks to stop the run, as the page's "Stop now" will.</summary>
    internal static Task<HttpContext> StopRun(Jobs jobs, Guid run) =>
        jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{jobs.Project}/runs/{run}/stop", Contract.Token, "{}");

    [Fact]
    public async Task StopMarksTheRunAndTheLogAnswersStop()
    {
        await using var jobs = await Jobs.Start();
        var job = await jobs.Job();
        var id = (Guid)job["job_id"]!;

        // Until the owner stops it, the log answers no stop.
        Assert.False(await LogSaysStop(jobs, id));

        var stop = await StopRun(jobs, id);
        Assert.Equal(StatusCodes.Status202Accepted, stop.Response.StatusCode);
        var marked = await StopRequestedAt(jobs, id);
        Assert.Equal(jobs.Routes.Time.GetUtcNow().UtcDateTime, marked);

        // A second stop is accepted and changes nothing.
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(StatusCodes.Status202Accepted, (await StopRun(jobs, id)).Response.StatusCode);
        Assert.Equal(marked, await StopRequestedAt(jobs, id));

        // The worker's next post, lines or none, is answered stop.
        Assert.True(await LogSaysStop(jobs, id));

        // Once its result is in, whatever it is, the run has ended.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, (string)job["branch"]!))).Response.StatusCode);
        var ended = await StopRun(jobs, id);
        Assert.Equal(StatusCodes.Status409Conflict, ended.Response.StatusCode);
        Assert.Equal("The run has already ended", (string)JsonNode.Parse(await Harness.Body(ended))!["title"]!);

        // A run that is not one of the caller's is no run: one never made, and another user's, the
        // same run once it is Bob's, in a project of his, which is 404 rather than 409.
        Assert.Equal(StatusCodes.Status404NotFound, (await StopRun(jobs, Guid.CreateVersion7())).Response.StatusCode);
        var bob = Guid.CreateVersion7();
        var bobs = await Reading.Reading.InsertProject(jobs.Routes.Door, bob, name: "bobs-repository");
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO workers (project_id, id, user_id, name, recipe_version, base_image, first_seen_at, last_seen_at)
                VALUES (@bobs, @worker, @bob, 'bobs-worker', '1', 'base', now(), now());
                UPDATE runs SET project_id = @bobs, user_id = @bob, worker_id = @worker WHERE id = @id;
                """,
                new { bobs, bob, worker = Guid.CreateVersion7(), id },
                cancellationToken: Ct));
        }

        Assert.Equal(StatusCodes.Status404NotFound, (await jobs.Routes.App.SendJson("POST", $"/devflow/api/projects/{bobs}/runs/{id}/stop", Contract.Token, "{}")).Response.StatusCode);
        var notHis = await StopRun(jobs, id);
        Assert.Equal(StatusCodes.Status404NotFound, notHis.Response.StatusCode);
        Assert.Equal("No such run", (string)JsonNode.Parse(await Harness.Body(notHis))!["title"]!);
    }

    [Fact]
    public async Task AStoppedCardIsStoppedByYou()
    {
        // A build stopped: the commit it pushed is the run's, and the card waits, stopped by me.
        await using (var jobs = await Jobs.Start())
        {
            await Merging.Merged.Sql(jobs, "UPDATE cards SET in_card_failures = 1 WHERE project_id = @project");
            var built = await Built.Run(jobs, PreparedStopped());
            var card = (int)built["card"]!;
            var run = Assert.Single(await jobs.Runs());
            Assert.Equal(("stopped_by_owner", DevFlow.Jobs.RunStore.StoppedByYou), (run.Result, run.Reason));
            Assert.Equal((string)built["head_commit"]!, run.HeadCommit);
            Assert.Equal(("stopped", DevFlow.Jobs.RunStore.StoppedByYou, true, "build", (int?)null, 0, 1), await Held(jobs, card));
            // Nothing outside the card failed, so the project does not wait, and the watch, read
            // again, finds nothing it does not accept.
            Assert.Null(await Stop.Wait(jobs));
            await jobs.Routes.Read(jobs.Project, Jobs.User);
            Assert.False(await Ending.Stopped(jobs));
        }

        // A review stopped.
        await using (var jobs = await Jobs.Start())
        {
            var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
            var card = (int)built["card"]!;
            Fixed.Later(jobs);
            var reviewed = await Built.Run(jobs, PreparedStopped());
            Assert.Equal("review", (string)reviewed["kind"]!);
            Assert.Equal("stopped_by_owner", (await jobs.Runs())[^1].Result);
            Assert.Equal(("stopped", DevFlow.Jobs.RunStore.StoppedByYou, true, "review", (int?)null, 0, 0), await Held(jobs, card));
        }

        // A fix stopped in its first round: the round is the card's still, and not used up.
        await using (var jobs = await Jobs.Start())
        {
            var built = await Fixed.BuiltAndReviewed(jobs);
            var card = (int)built["card"]!;
            var fixing = await Fixed.Fix(jobs, PreparedStopped());
            Assert.Equal("stopped_by_owner", (await jobs.Runs())[^1].Result);
            Assert.Equal((string)fixing["head_commit"]!, (await jobs.Runs())[^1].HeadCommit);
            Assert.Equal(("stopped", DevFlow.Jobs.RunStore.StoppedByYou, true, "fix", (int?)1, 1, 0), await Held(jobs, card));
            Assert.Equal(1, await Fixed.RoundsUsed(jobs, card));
        }
    }

    [Fact]
    public async Task AStoppedCardIsNotMarkedOnItsIssue()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2)]);

        // One card stopped by me, the other stopped by a failure in it, twice: only the second is
        // marked on its issue.
        var mine = (int)(await Built.Run(jobs, PreparedStopped()))["card"]!;
        await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
        await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
        var failed = mine == 1 ? 2 : 1;
        Assert.Equal("stopped", (await Stop.Card(jobs, failed)).State);
        await Stop.Mark(jobs);

        Assert.Empty(Stop.ByApp(jobs, "comment", mine));
        Assert.Empty(Stop.ByApp(jobs, "label", mine));
        Assert.False((await Stop.Card(jobs, mine)).AttentionLabelled);
        Assert.NotEmpty(Stop.ByApp(jobs, "comment", failed));
    }

    [Fact]
    public async Task AStoppedFeatureRunHoldsTheFeature()
    {
        // A whole feature's review stopped holds the feature where it was, reviewing, and nothing
        // of it is handed out while it is held, the minute that decides its review included.
        await using (var jobs = await Ending.Feature())
        {
            await Reviewing.Published(jobs);
            jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
            var ran = await Built.Run(jobs, PreparedStopped());
            Assert.Equal(Job.FeatureReview, (string)ran["kind"]!);
            Assert.Equal("stopped_by_owner", Assert.Single(await Reviewing.Runs(jobs, Job.FeatureReview)).Result);
            Assert.Equal(("reviewing", DevFlow.Jobs.RunStore.StoppedByYou, true), await Hold(jobs));

            await Reviewing.Minute(jobs);
            Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
            Assert.Equal(("reviewing", DevFlow.Jobs.RunStore.StoppedByYou, true), await Hold(jobs));
            Assert.Null(await Stop.Wait(jobs));
        }

        // A whole feature's fix stopped pushes what it did, which the feature's watch accepts as
        // Dev flow's own, and holds the feature fixing, in its round.
        await using (var jobs = await Ending.Feature())
        {
            await Reviewing.Published(jobs);
            await Reviewing.Review(jobs, Built.Verdict("changes"));
            jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
            var ran = await Built.Run(jobs, PreparedStopped());
            Assert.Equal(Job.FeatureFix, (string)ran["kind"]!);
            var pushed = (string)ran["head_commit"]!;
            var run = Assert.Single(await Reviewing.Runs(jobs, Job.FeatureFix));
            Assert.Equal("stopped_by_owner", run.Result);
            Assert.Contains((Ending.Branch, pushed, run.Id), await FeatureFixing.Pushes(jobs));
            Assert.Equal(("fixing", DevFlow.Jobs.RunStore.StoppedByYou, true), await Hold(jobs));
            Assert.Equal(1, (await FeatureFixing.Row(jobs)).FixRound);

            await Reviewing.Minute(jobs);
            Assert.False(await Ending.Stopped(jobs));
            Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        }

        // A conflict stopped holds the feature resolving.
        await using (var jobs = await Ending.Feature())
        {
            await FeaturePage.Landed(jobs);
            await Ending.MoveMain(jobs);
            Merging.Merged.Change(jobs, repository => repository with { Conflicts = [Ending.Branch] });
            await Ending.Done(jobs);
            await Ending.ReadAndEnd(jobs);
            await Reviewing.ReadAgain(jobs);
            var ran = await Built.Run(jobs, PreparedStopped());
            Assert.Equal(Job.Conflict, (string)ran["kind"]!);
            Assert.Equal(("resolving", DevFlow.Jobs.RunStore.StoppedByYou, true), await Hold(jobs));

            await Reviewing.ReadAgain(jobs);
            Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        }
    }

    [Fact]
    public async Task AStoppedReadingSetupOrCheckGoesBack()
    {
        // A reading stopped: its card is read again, and the project does not wait.
        await using (var jobs = await Jobs.Start(read: false))
        {
            var job = await jobs.Job();
            Assert.Equal(Job.Read, (string)job["kind"]!);
            Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), Stopped(Jobs.FirstWorker, (string)job["branch"]!))).Response.StatusCode);

            Assert.Equal(("stopped_by_owner", DevFlow.Jobs.RunStore.StoppedByYou), ((await jobs.Runs())[^1].Result, (await jobs.Runs())[^1].Reason));
            Assert.Equal("queued", (await jobs.Card(1)).State);
            Assert.Null(await Stop.Wait(jobs));
            Assert.Equal(Job.Read, (string)(await jobs.Job())["kind"]!);
        }

        // A check stopped: no verdict on the version, which is checked again.
        await using (var jobs = await Jobs.Start(checkedRecipe: false))
        {
            var job = await jobs.Job();
            Assert.Equal(Job.RecipeCheck, (string)job["kind"]!);
            Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), Stopped(Jobs.FirstWorker, (string)job["branch"]!))).Response.StatusCode);

            Assert.Equal("stopped_by_owner", (await jobs.Runs())[^1].Result);
            Assert.Null(await Stop.Wait(jobs));
            Assert.Equal(Job.RecipeCheck, (string)(await jobs.Job())["kind"]!);
        }

        // A setup run stopped, after it pushed: the setup is queued still, and set up again.
        await using (var jobs = await Setup.SetupRunTests.Unset())
        {
            var job = await SetupJob(jobs);
            Assert.Equal(Job.Setup, (string)job["kind"]!);
            var result = Stopped(Setup.SetupRunTests.SetupWorker, (string)job["branch"]!);
            Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result)).Response.StatusCode);

            Assert.Equal("stopped_by_owner", (await jobs.Runs())[^1].Result);
            Assert.Equal("queued", (await Setup.SetupRunTests.Setup(jobs)).State);
            Assert.Null(await Stop.Wait(jobs));
            Assert.Equal(Job.Setup, (string)(await SetupJob(jobs))["kind"]!);
        }
    }

    /// <summary>The job the setup worker is handed.</summary>
    private static async Task<JsonObject> SetupJob(Jobs jobs)
    {
        var response = await Workers.Workers.AsWorker(jobs.Routes.App, Workers.Workers.NextPath, Workers.Workers.Next(Setup.SetupRunTests.SetupWorker), null, Workers.Workers.Bearer(jobs.Key));
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (await Workers.Workers.Json(response)).AsObject();
    }

    /// <summary>The feature's state, its reason, and whether I stopped one of its runs.</summary>
    private static async Task<(string State, string? Reason, bool Held)> Hold(Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<(string, string?, bool)>(new CommandDefinition(
            "SELECT state, reason, stopped_by_owner_at IS NOT NULL FROM features WHERE project_id = @project AND number = @parent",
            new { project = jobs.Project, parent = Ending.Parent },
            cancellationToken: Ct));
    }

    /// <summary>Whether the log route answers the worker's post, with no lines, stop.</summary>
    private static async Task<bool> LogSaysStop(Jobs jobs, Guid run)
    {
        var posted = await jobs.Post(WorkerApi.LogPath(run), RunLogTests.Post(Jobs.FirstWorker, []));
        Assert.Equal(StatusCodes.Status200OK, posted.Response.StatusCode);
        return (bool)JsonNode.Parse(await Harness.Body(posted))!["stop"]!;
    }

    private static async Task<DateTime?> StopRequestedAt(Jobs jobs, Guid run)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        var at = await admin.QuerySingleAsync<DateTime?>(new CommandDefinition(
            "SELECT stop_requested_at FROM runs WHERE id = @run",
            new { run },
            cancellationToken: Ct));
        return at?.ToUniversalTime();
    }

    /// <summary>The card's state, reason, whether I stopped it, the kind and round of the run I
    /// stopped, its fix rounds used and its failures in a row.</summary>
    private static async Task<(string State, string? Reason, bool ByOwner, string? Kind, int? Round, int FixRounds, int Failures)> Held(Jobs jobs, int card)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<(string, string?, bool, string?, int?, int, int)>(new CommandDefinition(
            """
            SELECT state, reason, stopped_by_owner, stopped_kind, stopped_round, fix_rounds, in_card_failures
            FROM cards WHERE project_id = @project AND number = @card
            """,
            new { project = jobs.Project, card },
            cancellationToken: Ct));
    }
}