using DevFlow.Tests.Merging;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A run that never returns is lost once it was handed out its job's minutes and 10 more ago, and
/// its worker has not been seen for 10, so the margin of the old 100 minutes stays whatever the
/// project's time limit is. Against real Postgres and the fake code host. See "Jobs" in
/// devflow/README.md and "Handing out work" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class LostRunTests
{
    [Fact]
    public async Task LostAfterItsTimeLimitAndTheMargin()
    {
        await using var jobs = await Jobs.Start();
        await Merged.Sql(jobs, "UPDATE project_settings SET run_minutes = 30 WHERE project_id = @project");
        var job = await jobs.Job();
        Assert.Equal(30, (int)job["limits"]!["minutes"]!);

        // 39 minutes on, with the worker gone: not yet, since the margin is not over.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(39));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        Assert.Equal("building", (await jobs.Card(1)).State);
        Assert.Null(Assert.Single(await jobs.Runs()).Result);

        // 40 minutes, its 30 and the 10 of the margin: lost, and the card is queued again.
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        Assert.Equal("queued", (await jobs.Card(1)).State);
        Assert.Equal("lost", Assert.Single(await jobs.Runs()).Result);
    }
}