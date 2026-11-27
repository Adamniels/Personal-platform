using Dapper;

using DevFlow.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.Extensions.Logging.Abstractions;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A run's log is kept 90 days after the run finished: <see cref="RunLogPruner"/> deletes the lines
/// of each run finished 90 days ago or more, once a day, every project as its user, and keeps the
/// run, its result and its figures for ever; a run under way is never pruned. Against real Postgres
/// and the fake code host, the clock moved by hand. See "Jobs" in devflow/README.md.
/// </summary>
public sealed class LogPrunerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RunLogPruner Pruner(Jobs jobs) =>
        new(jobs.Routes.Door, jobs.Routes.Time, NullLogger<RunLogPruner>.Instance);

    /// <summary>How many lines the run holds, read past row level security.</summary>
    private static async Task<int> Lines(Jobs jobs, Guid run) =>
        (await RunLogTests.Stored(jobs, run)).Count;

    /// <summary>When the pruner marked the run, read past row level security.</summary>
    private static async Task<DateTime?> PrunedAt(Jobs jobs, Guid run)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<DateTime?>(new CommandDefinition(
            "SELECT log_pruned_at FROM runs WHERE id = @run",
            new { run },
            cancellationToken: Ct));
    }

    /// <summary>The fake worker takes the project's next job and ends it failed in the card, its first
    /// lines posted; the run's id.</summary>
    private static async Task<Guid> Ran(Jobs jobs) =>
        (Guid)(await Built.Run(jobs, Built.PreparedFailure(inCard: true, "The card asks for two pages")))["job_id"]!;

    /// <summary>The run made to have finished <paramref name="earlier"/> before it did, past row
    /// level security, since a route's token runs out long before the clock could be moved so
    /// far.</summary>
    private static async Task Age(Jobs jobs, Guid run, TimeSpan earlier)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE runs SET started_at = started_at - @earlier, finished_at = finished_at - @earlier WHERE id = @run",
            new { run, earlier },
            cancellationToken: Ct));
    }

    /// <summary>What a run's page shows of it besides its log, which pruning keeps.</summary>
    private static (string? Result, string? Reason, DateTime? FinishedAt, long? DurationMs, long? InputTokens, long? OutputTokens, string? Stderr) Figures(Jobs.RunRow run) =>
        (run.Result, run.Reason, run.FinishedAt, run.DurationMs, run.InputTokens, run.OutputTokens, run.Stderr);

    [Fact]
    public async Task OldLogsGo()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2), Jobs.Card(3), Jobs.Card(4)]);

        // A run finished a day ago, and two now, one of them moved to Bob's project.
        var oldest = await Ran(jobs);
        await Age(jobs, oldest, TimeSpan.FromDays(1));
        var newer = await Ran(jobs);
        var bobs = await Ran(jobs);
        var bob = Guid.CreateVersion7();
        var bobsProject = await Reading.Reading.InsertProject(jobs.Routes.Door, bob, name: "bobs-repository");
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO workers (project_id, id, user_id, name, recipe_version, base_image, first_seen_at, last_seen_at)
                VALUES (@bobsProject, @worker, @bob, 'bobs-worker', '1', 'base', now(), now());
                UPDATE runs SET project_id = @bobsProject, user_id = @bob, worker_id = @worker WHERE id = @bobs;
                """,
                new { bobsProject, bob, worker = Guid.CreateVersion7(), bobs },
                cancellationToken: Ct));
        }

        // And one taken by a second worker at the same time, with a line, still under way.
        await jobs.SayHello(Jobs.SecondWorker);
        var underWay = (Guid)(await jobs.Job(Jobs.SecondWorker))["job_id"]!;
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status200OK, (await jobs.Post(Protocol.WorkerApi.LogPath(underWay), RunLogTests.Post(Jobs.SecondWorker, [RunLogTests.Line(1, "Reading the card.")]))).Response.StatusCode);
        foreach (var run in new[] { oldest, newer, bobs, underWay })
        {
            Assert.True(await Lines(jobs, run) > 0);
        }

        var before = (await jobs.Runs()).Concat(await jobs.Runs(bobsProject)).ToDictionary(run => run.Id, Figures);
        var pruner = Pruner(jobs);

        // 89 days and 23 hours after the oldest finished: nothing is old enough.
        jobs.Routes.Time.Advance(TimeSpan.FromDays(89) - TimeSpan.FromHours(1));
        await pruner.PruneAll(Ct);
        Assert.True(await Lines(jobs, oldest) > 0);
        Assert.Null(await PrunedAt(jobs, oldest));

        // 90 days after it finished: its lines go, and it is marked, at the clock's time.
        jobs.Routes.Time.Advance(TimeSpan.FromHours(1));
        await pruner.PruneAll(Ct);
        Assert.Equal(0, await Lines(jobs, oldest));
        Assert.Equal(jobs.Routes.Time.GetUtcNow().UtcDateTime, await PrunedAt(jobs, oldest));
        foreach (var run in new[] { newer, bobs, underWay })
        {
            Assert.True(await Lines(jobs, run) > 0);
            Assert.Null(await PrunedAt(jobs, run));
        }

        // The run itself is kept, its result, reason and figures as they were.
        Assert.Equal(before[oldest], Figures(Assert.Single(await jobs.Runs(), run => run.Id == oldest)));

        // A day on, the two finished a day later go, Bob's in his own project as him; the run
        // under way, taken as long ago, never does.
        jobs.Routes.Time.Advance(TimeSpan.FromDays(1));
        await pruner.PruneAll(Ct);
        Assert.Equal(0, await Lines(jobs, newer));
        Assert.Equal(0, await Lines(jobs, bobs));
        Assert.NotNull(await PrunedAt(jobs, bobs));
        Assert.True(await Lines(jobs, underWay) > 0);
        Assert.Null(await PrunedAt(jobs, underWay));
        Assert.Equal(before[newer], Figures(Assert.Single(await jobs.Runs(), run => run.Id == newer)));
        Assert.Equal(before[bobs], Figures(Assert.Single(await jobs.Runs(bobsProject), run => run.Id == bobs)));

        // Pruning again changes nothing: a run already pruned keeps when it was.
        var pruned = await PrunedAt(jobs, oldest);
        jobs.Routes.Time.Advance(TimeSpan.FromDays(1));
        await pruner.PruneAll(Ct);
        Assert.Equal(pruned, await PrunedAt(jobs, oldest));
    }

    [Fact]
    public async Task ItRunsOnceADay()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2)]);
        // One run finished an hour before the other.
        var first = await Ran(jobs);
        await Age(jobs, first, TimeSpan.FromHours(1));
        var second = await Ran(jobs);

        // Half an hour after the first is 90 days old: as it starts, it prunes the first.
        jobs.Routes.Time.Advance(TimeSpan.FromDays(90) - TimeSpan.FromMinutes(30));
        using var pruner = Pruner(jobs);
        await pruner.StartAsync(Ct);
        await Reading.Reading.Until(async () => await Lines(jobs, first) == 0, "the first run's log being pruned as the loop starts");
        Assert.True(await Lines(jobs, second) > 0);

        // The second is 90 days old within the hour, but the loop does not look again until a day
        // has passed.
        jobs.Routes.Time.Advance(RunLogPruner.Every - TimeSpan.FromSeconds(1));
        await Task.Delay(300, Ct);
        Assert.True(await Lines(jobs, second) > 0);

        // Once it has, it does.
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(1));
        await Reading.Reading.Until(async () => await Lines(jobs, second) == 0, "the second run's log being pruned a day later");
        await pruner.StopAsync(Ct);
        Assert.Equal(TimeSpan.FromDays(1), RunLogPruner.Every);
    }
}