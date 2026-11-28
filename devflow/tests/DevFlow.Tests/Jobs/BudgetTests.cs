using System.Globalization;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Schedules;
using DevFlow.Tests.Schedules;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A project's daily budget in tokens, off by default: once the runs that finished today in the
/// user's zone used it, by the one definition of a token, <see cref="RunDecision"/> answers
/// <see cref="WaitReason.BudgetReached"/>, after the pauses and before the schedules, and no job of
/// any kind is handed out; the run under way finishes. Worked out each time and never stored, it
/// lifts at midnight in the zone, or at once when the budget is raised. Against real Postgres and
/// the fake code host. See "Settings" under "Cards" and "Stops" in devflow/README.md.
/// </summary>
public sealed class BudgetTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OffByDefault()
    {
        await using var jobs = await Jobs.Start();

        // A new project's budget is null, stored and answered.
        Assert.Null(await StoredBudget(jobs));
        var settings = await Settings(jobs);
        Assert.True(settings.ContainsKey("daily_token_budget"));
        Assert.Null(settings["daily_token_budget"]);

        // However much its runs used today, nothing holds it: the build is handed out.
        await Spent(jobs, jobs.Routes.Time.GetUtcNow(), input: 9_000_000_000, output: 9_000_000_000);
        Assert.Equal(new Decision.Run(Job.Build), await Rule(jobs));
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }

    [Fact]
    public async Task ReachedHoldsEveryJob()
    {
        await using var jobs = await Jobs.Start();
        await SetBudget(jobs, 1_000_000);
        var now = jobs.Routes.Time.GetUtcNow();

        // Runs that finished today used a million, by the one definition: the input, the cache
        // writes and the output.
        await Spent(jobs, now - TimeSpan.FromHours(2), input: 300_000, cacheCreation: 200_000, output: 100_000);
        await Spent(jobs, now - TimeSpan.FromMinutes(5), input: 250_000, output: 150_000);

        // Nothing is handed out, and the rule says how much was used of what, and when it lifts:
        // the next midnight in Stockholm, written in its own offset.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Empty(await Handed(jobs));
        var midnight = NextMidnight(now);
        var reached = new WaitReason.BudgetReached(1_000_000, 1_000_000, midnight);
        Assert.Equal(new Decision.Wait(reached), await Rule(jobs));
        var said = Assert.IsType<WaitReason.BudgetReached>(((Decision.Wait)(await Rule(jobs))).Reason);
        Assert.Equal(Stockholm.GetUtcOffset(midnight), said.LiftsAt.Offset);
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"Daily budget reached, runs again at {TimeZoneInfo.ConvertTime(midnight, Stockholm):ddd HH:mm}"),
            said.Sentence);
        Assert.Equal(ActedOn.Budget, said.Where);

        // Running now's queue says it, linking to the budget.
        Assert.Equal((said.Sentence, "budget"), await NextReason(jobs));

        // A check of the worker's setup, which runs no agent, is held as well.
        await using var checking = await Jobs.Start(checkedRecipe: false);
        await SetBudget(checking, 1_000_000);
        Assert.Equal(new Decision.Run(Job.RecipeCheck), await Rule(checking));
        await Spent(checking, checking.Routes.Time.GetUtcNow(), input: 1_000_000);
        Assert.Equal(StatusCodes.Status204NoContent, (await checking.Next()).Response.StatusCode);
        Assert.Empty(await Handed(checking));
        Assert.IsType<WaitReason.BudgetReached>(((Decision.Wait)(await Rule(checking))).Reason);
    }

    [Fact]
    public async Task CacheReadsDoNotCount()
    {
        await using var jobs = await Jobs.Start();
        await SetBudget(jobs, 1_000_000);
        var now = jobs.Routes.Time.GetUtcNow();

        // Two million cache reads and a hundred thousand others count a hundred thousand: the
        // project runs.
        await Spent(jobs, now, input: 40_000, cacheCreation: 30_000, cacheRead: 2_000_000, output: 30_000);
        Assert.Equal(new Decision.Run(Job.Build), await Rule(jobs));

        // Nine hundred thousand more is exactly the million.
        await Spent(jobs, now, input: 900_000, cacheRead: 5_000_000);
        Assert.Equal(new Decision.Wait(new WaitReason.BudgetReached(1_000_000, 1_000_000, NextMidnight(now))), await Rule(jobs));
    }

    [Fact]
    public async Task ARunUnderWayFinishes()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2)]);
        await SetBudget(jobs, 1_000_000);
        var now = jobs.Routes.Time.GetUtcNow();
        await Spent(jobs, now, input: 900_000);

        // Under the budget, the build is handed out.
        var job = await jobs.Job();
        var card = (int)job["card"]!["number"]!;

        // It ends over the budget; its result is recorded as always, and its card moves on.
        var result = Jobs.Result(Jobs.FirstWorker, (string)job["branch"]!);
        result["usage"]!["usage"]!["input_tokens"] = 150_000;
        result["usage"]!["usage"]!["cache_creation_input_tokens"] = 0;
        result["usage"]!["usage"]!["output_tokens"] = 50_000;
        var finished = await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result);
        Assert.Equal(StatusCodes.Status204NoContent, finished.Response.StatusCode);
        Assert.Equal("succeeded", Assert.Single(await Handed(jobs)).Result);
        Assert.Equal("checking", (await jobs.Card(card)).State);

        // The day went over by that run, and the next job waits.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Single(await Handed(jobs));
        Assert.Equal(new Decision.Wait(new WaitReason.BudgetReached(1_100_000, 1_000_000, NextMidnight(now))), await Rule(jobs));
    }

    [Fact]
    public async Task ItLiftsAtMidnight()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2)]);
        await SetBudget(jobs, 1_000_000);

        // 28 March 2027, a Sunday, Stockholm's clocks skip from 02:00 to 03:00, so the day is 23
        // hours long: from 23:00 UTC the day before to 22:00 UTC.
        var dayStarts = Utc("2027-03-27T23:00:00");
        var dayEnds = Utc("2027-03-28T22:00:00");

        // The Saturday's run, before that midnight, does not count on the Sunday; a run in its first
        // hour, a day before in UTC, does, and so does one in its last.
        await Spent(jobs, dayStarts - TimeSpan.FromMinutes(30), input: 5_000_000);
        await Spent(jobs, dayStarts + TimeSpan.FromMinutes(30), input: 600_000);
        await Spent(jobs, dayEnds - TimeSpan.FromHours(1), input: 400_000);

        // Late on the Sunday the project is held until midnight, 00:00 on Monday in summer time.
        await MoveTo(jobs, dayEnds - TimeSpan.FromMinutes(30));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        var reached = Assert.IsType<WaitReason.BudgetReached>(((Decision.Wait)(await Rule(jobs))).Reason);
        Assert.Equal(new WaitReason.BudgetReached(1_000_000, 1_000_000, dayEnds), reached);
        Assert.Equal(TimeSpan.FromHours(2), reached.LiftsAt.Offset);
        Assert.Equal("Daily budget reached, runs again at Mon 00:00", reached.Sentence);

        // A second before midnight it still holds.
        await MoveTo(jobs, dayEnds - TimeSpan.FromSeconds(1));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // At midnight the hold is gone with nothing stored to clear: the build is handed out.
        await MoveTo(jobs, dayEnds);
        Assert.Equal(new Decision.Run(Job.Build), await Rule(jobs));
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }

    [Fact]
    public async Task RaisingItLiftsItAtOnce()
    {
        await using var jobs = await Jobs.Start();
        await SetBudget(jobs, 1_000_000);
        await Spent(jobs, jobs.Routes.Time.GetUtcNow(), input: 1_200_000);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // Raised above what was used, the next job is handed out at once.
        await SetBudget(jobs, 2_000_000);
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }

    [Fact]
    public async Task SwitchingItOffLiftsItAtOnce()
    {
        await using var jobs = await Jobs.Start();
        await SetBudget(jobs, 1_000_000);
        await Spent(jobs, jobs.Routes.Time.GetUtcNow(), input: 1_000_000);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        await SetBudget(jobs, null);
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }

    [Fact]
    public async Task ComesAfterThePauses()
    {
        await using var jobs = await Jobs.Start();
        await SetBudget(jobs, 1_000_000);
        var now = jobs.Routes.Time.GetUtcNow();
        await Spent(jobs, now, input: 1_000_000);
        var reached = new Decision.Wait(new WaitReason.BudgetReached(1_000_000, 1_000_000, NextMidnight(now)));

        // Paused and over the budget answers paused, and everything paused before that.
        await PauseTests.Pause(jobs, true);
        Assert.Equal(new Decision.Wait(new WaitReason.Paused()), await Rule(jobs));
        await PauseTests.PauseAll(jobs, true);
        Assert.Equal(new Decision.Wait(new WaitReason.AllPaused()), await Rule(jobs));

        // Continued, it is over the budget, ahead of being outside its schedule, which no override
        // gets past for it.
        await PauseTests.PauseAll(jobs, false);
        await PauseTests.Pause(jobs, false);
        var schedules = await jobs.Routes.App.SendJson("PUT", ScheduleRouteTests.Path(jobs.Project), Contract.Token,
            new JsonObject { ["runs_by"] = RunsBy.Schedule, ["schedules"] = new JsonArray() }.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, schedules.Response.StatusCode);
        Assert.Equal(reached, await Rule(jobs));

        // And ahead of a stop by a commit that landed on main.
        await Merging.Merged.Stop(jobs);
        Assert.Equal(reached, await Rule(jobs));
    }

    [Fact]
    public async Task TheProjectsWorkersSayIt()
    {
        await using var jobs = await Jobs.Start();
        await SetBudget(jobs, 1_000_000);
        var now = jobs.Routes.Time.GetUtcNow();

        // Under it, the workers list has no budget.
        Assert.Null((await WorkerList(jobs))["budget"]);

        // Reached, it says what was used of what, until when, and the rule's own sentence.
        await Spent(jobs, now, input: 1_500_000);
        var midnight = NextMidnight(now);
        var budget = (await WorkerList(jobs))["budget"]!.AsObject();
        Assert.Equal(1_500_000, (long)budget["used"]!);
        Assert.Equal(1_000_000, (long)budget["budget"]!);
        Assert.Equal(midnight, (DateTimeOffset)budget["lifts_at"]!);
        Assert.Equal(new WaitReason.BudgetReached(1_500_000, 1_000_000, midnight).Sentence, (string)budget["sentence"]!);
    }

    private static async Task<JsonObject> WorkerList(Jobs jobs)
    {
        var answer = await jobs.Routes.App.Get(Workers.Workers.ListPath(jobs.Project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(answer))!.AsObject();
    }

    /// <summary>The owner saves the project's settings with <paramref name="budget"/>, as the
    /// settings page does, every other setting as it is.</summary>
    internal static async Task SetBudget(Jobs jobs, long? budget)
    {
        var settings = await Settings(jobs);
        settings.Remove("preset");
        settings["trusted_logins"] = new JsonArray([.. settings["trusted_logins"]!.AsArray().Select(trusted => (JsonNode?)JsonValue.Create((string)trusted!["login"]!))]);
        settings["daily_token_budget"] = budget;
        var saved = await jobs.Routes.App.SendJson("PUT", SettingsPath(jobs.Project), Contract.Token, settings.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
    }

    /// <summary>
    /// A run of the project that finished at <paramref name="finishedAt"/> with Claude's figures,
    /// written as the superuser: a build of a card number no world has, so no other gate reads it.
    /// </summary>
    internal static async Task Spent(Jobs jobs, DateTimeOffset finishedAt, long? input = null, long? cacheCreation = null, long? cacheRead = null, long? output = null)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, started_at, finished_at, result, run_minutes,
                              input_tokens, cache_creation_input_tokens, cache_read_input_tokens, output_tokens)
            SELECT gen_random_uuid(), p.id, p.user_id, 900 + (SELECT count(*) FROM runs r WHERE r.project_id = p.id)::int, @worker, 'build',
                   'devflow/spent', 'main', @started, @finishedAt, 'succeeded', 90, @input, @cacheCreation, @cacheRead, @output
            FROM projects p WHERE p.id = @project
            """,
            new { project = jobs.Project, worker = Jobs.FirstWorker, started = finishedAt - TimeSpan.FromMinutes(10), finishedAt, input, cacheCreation, cacheRead, output },
            cancellationToken: Ct));
    }

    /// <summary>The project's runs that were handed out, not those <see cref="Spent"/> wrote.</summary>
    private static async Task<List<Jobs.RunRow>> Handed(Jobs jobs) =>
        [.. (await jobs.Runs()).Where(run => run.Branch != SpentBranch)];

    /// <summary>The branch of the runs <see cref="Spent"/> writes.</summary>
    private const string SpentBranch = "devflow/spent";

    private static string SettingsPath(Guid project) => $"/devflow/api/projects/{project}/settings";

    private static async Task<JsonObject> Settings(Jobs jobs)
    {
        var answer = await jobs.Routes.App.Get(SettingsPath(jobs.Project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(answer))!.AsObject();
    }

    private static async Task<long?> StoredBudget(Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<long?>(new CommandDefinition(
            "SELECT daily_token_budget FROM project_settings WHERE project_id = @project",
            new { project = jobs.Project },
            cancellationToken: Ct));
    }

    /// <summary>Moves the app's clock to <paramref name="at"/>, then reads the project and has the
    /// worker say hello, so its default branch is watched and its worker seen, as they would be.</summary>
    private static async Task MoveTo(Jobs jobs, DateTimeOffset at)
    {
        jobs.Routes.Time.SetUtcNow(at);
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        await jobs.SayHello(Jobs.FirstWorker);
    }

    /// <summary>The first midnight after <paramref name="now"/> in Stockholm.</summary>
    internal static DateTimeOffset NextMidnight(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Stockholm);
        var midnight = local.Date.AddDays(1);
        return new DateTimeOffset(midnight, Stockholm.GetUtcOffset(midnight));
    }

    private static DateTimeOffset Utc(string instant) => DateTimeOffset.Parse(instant + "Z", CultureInfo.InvariantCulture);

    /// <summary>The rule's decision for the project as the first worker finds it.</summary>
    private static async Task<Decision> Rule(Jobs jobs)
    {
        var situation = await RunDecision.Read(jobs.Routes.Door, Jobs.User, jobs.Project, Jobs.FirstWorker, Jobs.Image, jobs.Routes.Time.GetUtcNow(), Ct);
        Assert.NotNull(situation);
        return RunDecision.Of(situation);
    }

    /// <summary>The reason "Next up" on Running now gives the one card next, and where it links.</summary>
    private static async Task<(string? Reason, string? Link)> NextReason(Jobs jobs)
    {
        var response = await jobs.Routes.App.Get("/devflow/api/running", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var card = Assert.Single(JsonNode.Parse(await Harness.Body(response))!["next"]!.AsArray())!;
        return ((string?)card["reason"], (string?)card["link"]);
    }
}