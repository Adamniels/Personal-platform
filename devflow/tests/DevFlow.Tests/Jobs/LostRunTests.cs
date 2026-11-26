using Dapper;

using DevFlow.Protocol;
using DevFlow.Tests.Merging;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A run is lost once its worker has sent nothing for 2 minutes: its last post to the run's log,
/// or its start when it has posted none, is 2 minutes or more ago, whatever its time limit, which
/// the worker enforces itself. Against real Postgres and the fake code host, the clock moved by
/// hand. See "Jobs" in devflow/README.md and "Handing out work" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class LostRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARunQuietForTwoMinutesIsLost()
    {
        // Two cards, each taken by its own worker at the same moment, with a time limit far shorter
        // than the old margin would have needed.
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2)]);
        await Merged.Sql(jobs, "UPDATE project_settings SET run_minutes = 10 WHERE project_id = @project");
        await jobs.SayHello(Jobs.SecondWorker);
        var posting = (Guid)(await jobs.Job(Jobs.FirstWorker))["job_id"]!;
        var quiet = (Guid)(await jobs.Job(Jobs.SecondWorker))["job_id"]!;

        // A minute on, the first posts; the second never does.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        await Heartbeat(jobs, posting);

        // 1 minute 59 seconds after its start, the quiet one is still under way.
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(59));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        Assert.Equal(["building", "building"], (await jobs.Cards()).Select(card => card.State));

        // 2 minutes after its start, with nothing sent: lost, and its card queued again.
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(1));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        var lost = Assert.Single(await jobs.Runs(), run => run.Id == quiet);
        Assert.Equal(("lost", "Its worker sent nothing for 2 minutes"), (lost.Result, lost.Reason));
        Assert.Equal(["building", "queued"], (await jobs.Cards()).Select(card => card.State));

        // The first posted 1 minute 59 seconds ago: still under way.
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(59));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        Assert.Null(Assert.Single(await jobs.Runs(), run => run.Id == posting).Result);

        // Posting each minute keeps it under way well past its 10 minutes and any margin: the
        // time limit is the worker's to enforce, and finds no run lost.
        for (var minute = 0; minute < 30; minute++)
        {
            jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
            await Heartbeat(jobs, posting);
            await jobs.Routes.Read(jobs.Project, Jobs.User);
        }

        Assert.Null(Assert.Single(await jobs.Runs(), run => run.Id == posting).Result);

        // Then 2 minutes without a post: lost.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(2));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        var ended = Assert.Single(await jobs.Runs(), run => run.Id == posting);
        Assert.Equal(("lost", "Its worker sent nothing for 2 minutes"), (ended.Result, ended.Reason));
        Assert.Equal("queued", (await jobs.Card(1)).State);
    }

    [Fact]
    public async Task APostKeepsTheWorkerSeen()
    {
        await using var jobs = await Jobs.Start();
        var id = (Guid)(await jobs.Job())["job_id"]!;
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        var now = jobs.Routes.Time.GetUtcNow();
        Assert.NotEqual(now.UtcDateTime, await LastSeen(jobs));

        await Heartbeat(jobs, id);

        Assert.Equal(now.UtcDateTime, await LastSeen(jobs));
    }

    /// <summary>A post with no lines, the heartbeat a worker sends when it has none.</summary>
    private static async Task Heartbeat(Jobs jobs, Guid id) =>
        Assert.Equal(StatusCodes.Status200OK, (await jobs.Post(WorkerApi.LogPath(id), RunLogTests.Post(Jobs.FirstWorker, []))).Response.StatusCode);

    private static async Task<DateTime> LastSeen(Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return (await admin.QuerySingleAsync<DateTime>(new CommandDefinition(
            "SELECT last_seen_at FROM workers WHERE project_id = @project AND id = @worker",
            new { project = jobs.Project, worker = Jobs.FirstWorker },
            cancellationToken: Ct))).ToUniversalTime();
    }
}