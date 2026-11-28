using System.Globalization;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.PullRequests;
using DevFlow.Tests.Schedules;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// "Run now anyway": an override for a feature or a card on its own that lets every job of it run
/// outside the project's schedule, and skips nothing but the schedule, until it has nothing left to
/// run, when the dispatcher deletes it. The routes are the owner's, through the door. Against real
/// Postgres, the fake code host and the fake worker, with the app's clock outside every window. See
/// "Schedules" and "Jobs" in devflow/README.md and "Handing out work" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class RunNowTests
{
    private static CancellationToken Ct => Jobs.Ct;

    [Fact]
    public async Task AFeatureRunsOutsideItsSchedule()
    {
        // Feature 10 with cards 11 and 12, and card 20 on its own beside it.
        await using var jobs = await Jobs.Start([
            Jobs.Card(20),
            FeatureJobs.Parent(10, "Dev flow: features", [11, 12]),
            FeatureJobs.Sub(11),
            FeatureJobs.Sub(12),
        ]);
        await jobs.SayHello(Jobs.SecondWorker);
        await Outside(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal(new Decision.Wait(new WaitReason.OutsideSchedule(null)), await Rule(jobs));

        // Run now anyway on the feature: its first card is built.
        Assert.Equal(StatusCodes.Status204NoContent, (await RunNow(jobs, Feature, 10)).Response.StatusCode);
        var job = await jobs.Job();
        Assert.Equal(Job.Build, (string)job["kind"]!);
        Assert.Equal(11, (int)job["card"]!["number"]!);

        // The second worker gets nothing: 12 waits for 11, and 20 keeps to the schedule, which
        // Running now's queue says of it, and of nothing of the feature.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next(Jobs.SecondWorker)).Response.StatusCode);
        var next = await NextUp(jobs);
        Assert.Equal("No schedule is on", (string?)next[20]["reason"]);
        Assert.Equal("Waits for #11, which it depends on", (string?)next[12]["reason"]);

        // 11 done, the feature's next card is handed out, and still not 20.
        await FeatureJobs.Finish(jobs, 11, "done");
        Assert.Equal(12, (int)(await jobs.Job(Jobs.SecondWorker))["card"]!["number"]!);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(20)).State);
        Assert.DoesNotContain(await jobs.Runs(), run => run.CardNumber == 20);
    }

    [Fact]
    public async Task ACardRunsOutsideItsSchedule()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(4), Jobs.Card(5)]);
        await Outside(jobs);

        // Run now anyway on card 4: it is built, its pull request reviewed, and its fix run, each
        // outside the schedule.
        Assert.Equal(StatusCodes.Status204NoContent, (await RunNow(jobs, Card, 4)).Response.StatusCode);
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        Assert.Equal(4, (int)built["card"]!);
        Assert.Equal("reviewing", (await Built.Card(jobs, 4)).State);
        await Built.Review(jobs, Built.Verdict("changes"));
        Assert.Equal("fixing", (await Built.Card(jobs, 4)).State);
        await Fixed.Fix(jobs);
        Assert.Equal(["build", "review", "fix"], (await jobs.Runs()).Select(run => run.Kind));

        // Card 5 was never handed out, and keeps to the schedule.
        Assert.Equal("queued", (await jobs.Card(5)).State);
        Assert.DoesNotContain(await jobs.Runs(), run => run.CardNumber == 5);
        Assert.Equal("No schedule is on", (string?)(await NextUp(jobs))[5]["reason"]);
    }

    [Fact]
    public async Task ItEndsWhenNothingIsLeftToRun()
    {
        // Card 13 is written, but not yet a sub-issue of feature 10.
        await using var jobs = await Jobs.Start([
            FeatureJobs.Parent(10, "Dev flow: features", [11, 12]),
            FeatureJobs.Sub(11),
            FeatureJobs.Sub(12),
            FeatureJobs.Sub(13),
            Jobs.Card(4),
        ]);
        await Outside(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await RunNow(jobs, Feature, 10)).Response.StatusCode);
        Assert.True(await On(jobs, Feature, 10));

        // 11 done and 12 stopped, the feature building: nothing of it is left to run, and its page
        // says the override is not on. The dispatcher deletes it the next time a worker asks.
        await FeatureJobs.SetState(jobs, 11, "done");
        await FeatureJobs.SetState(jobs, 12, "stopped");
        Assert.False(await On(jobs, Feature, 10));
        Assert.Equal([10], await Stored(jobs, "feature_number"));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Empty(await Stored(jobs, "feature_number"));

        // A card added to the feature later keeps to the schedule.
        Merging.Merged.Issue(jobs, 10, parent => parent with { SubIssues = [new(11, null), new(12, null), new(13, null)] });
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        Assert.Equal("queued", (await jobs.Card(13)).State);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("No schedule is on", (string?)(await NextUp(jobs))[13]["reason"]);

        // A card on its own waiting for me ends its override the same way, and one sent back to
        // the queue later keeps to the schedule.
        Assert.Equal(StatusCodes.Status204NoContent, (await RunNow(jobs, Card, 4)).Response.StatusCode);
        Assert.True(await On(jobs, Card, 4));
        await FeatureJobs.SetState(jobs, 4, "waiting_for_owner");
        Assert.False(await On(jobs, Card, 4));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Empty(await Stored(jobs, "card_number"));
        await FeatureJobs.SetState(jobs, 4, "queued");
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Empty(await jobs.Runs());

        // Inside the schedule, 4 and 13 run, so nothing but the schedule held them.
        await Schedule(jobs, "always");
        await jobs.SayHello(Jobs.SecondWorker);
        Assert.Equal([4, 13], new[] { (int)(await jobs.Job())["card"]!["number"]!, (int)(await jobs.Job(Jobs.SecondWorker))["card"]!["number"]! }.Order());
    }

    [Fact]
    public async Task ItOverridesOnlyTheSchedule()
    {
        await using var jobs = await Jobs.Start([FeatureJobs.Parent(10, "Dev flow: features", [11]), FeatureJobs.Sub(11)]);
        await Outside(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await RunNow(jobs, Feature, 10)).Response.StatusCode);

        // Paused, the feature with the override gets nothing.
        await PauseTests.Pause(jobs, true);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal(new Decision.Wait(new WaitReason.Paused()), await Rule(jobs));
        await PauseTests.Pause(jobs, false);

        // Nor with everything paused.
        await PauseTests.PauseAll(jobs, true);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal(new Decision.Wait(new WaitReason.AllPaused()), await Rule(jobs));
        Assert.Empty(await jobs.Runs());

        // Continued, it runs outside the schedule again.
        await PauseTests.PauseAll(jobs, false);
        Assert.Equal(11, (int)(await jobs.Job())["card"]!["number"]!);
    }

    [Fact]
    public async Task InsideTheScheduleItChangesNothing()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(4), Jobs.Card(5)]);

        // Always: accepted, and the rule and the queue answer as without it.
        var rule = await Rule(jobs);
        var next = (await NextUp(jobs)).ToDictionary(each => each.Key, each => (string?)each.Value["reason"]);
        Assert.Equal(StatusCodes.Status204NoContent, (await RunNow(jobs, Card, 5)).Response.StatusCode);
        Assert.Equal(rule, await Rule(jobs));
        Assert.Equal(next, (await NextUp(jobs)).ToDictionary(each => each.Key, each => (string?)each.Value["reason"]));

        // Inside a window: the same, and 4 is handed out first, in the queue's own order.
        await Schedule(jobs, "schedule", ScheduleTests.Around(jobs).Window);
        Assert.Equal(rule, await Rule(jobs));
        Assert.Equal(StatusCodes.Status204NoContent, (await RunNow(jobs, Card, 4)).Response.StatusCode);
        Assert.Equal(rule, await Rule(jobs));
        Assert.Equal(4, (int)(await jobs.Job())["card"]!["number"]!);

        // Taken back, it answers the same again.
        Assert.Equal(StatusCodes.Status204NoContent, (await Back(jobs, Card, 5)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await Back(jobs, Card, 5)).Response.StatusCode);
        Assert.Equal(rule, await Rule(jobs));
    }

    [Fact]
    public async Task RoutesAreTheUsersOwn()
    {
        await using var jobs = await Jobs.Start([
            Jobs.Card(4),
            Jobs.Card(5),
            Jobs.Card(6),
            Jobs.Card(7),
            FeatureJobs.Parent(10, "Dev flow: features", [11]),
            FeatureJobs.Sub(11),
            FeatureJobs.Parent(20, "Dev flow: more", [21]),
            FeatureJobs.Sub(21),
            FeatureJobs.Parent(30, "Dev flow: stopped", [31]),
            FeatureJobs.Sub(31),
        ]);

        // Another user's project is none, and so is a feature or a card I do not have.
        var bob = Guid.CreateVersion7();
        var bobs = await Reading.Reading.InsertProject(jobs.Routes.Door, bob, repository: 4242, name: "bobs-repository");
        foreach (var (kind, number, project) in new[] { (Feature, 10, bobs), (Card, 4, bobs), (Feature, 99, jobs.Project), (Card, 99, jobs.Project) })
        {
            await NoSuch(await RunNow(jobs, kind, number, project));
            await NoSuch(await Back(jobs, kind, number, project));
        }

        // A done, stopped or refused card, and a merged feature or one I stopped, have nothing left
        // to run.
        await FeatureJobs.SetState(jobs, 5, "done");
        await FeatureJobs.SetState(jobs, 6, "stopped");
        await FeatureJobs.SetState(jobs, 7, "refused");
        await SetFeature(jobs, 20, "state = 'merged'");
        await SetFeature(jobs, 30, "stopped_by_owner_at = now()");
        foreach (var (kind, number) in new[] { (Card, 5), (Card, 6), (Card, 7), (Feature, 20), (Feature, 30) })
        {
            var refused = await RunNow(jobs, kind, number);
            Assert.Equal(StatusCodes.Status409Conflict, refused.Response.StatusCode);
            Assert.Equal("This has nothing left to run", (string?)JsonNode.Parse(await Harness.Body(refused))!["title"]);
        }

        // A card of a feature runs with its feature, so its override is the feature's.
        var ofAFeature = await RunNow(jobs, Card, 11);
        Assert.Equal(StatusCodes.Status409Conflict, ofAFeature.Response.StatusCode);
        Assert.Equal("This card runs with its feature", (string?)JsonNode.Parse(await Harness.Body(ofAFeature))!["title"]);
        Assert.Empty(await Stored(jobs, "card_number"));
        Assert.Empty(await Stored(jobs, "feature_number"));

        // Mine is accepted once, and again is the same one.
        Assert.Equal(StatusCodes.Status204NoContent, (await RunNow(jobs, Card, 4)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await RunNow(jobs, Card, 4)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await RunNow(jobs, Feature, 10)).Response.StatusCode);
        Assert.Equal([4], await Stored(jobs, "card_number"));
        Assert.Equal([10], await Stored(jobs, "feature_number"));

        // From another origin, a change is refused.
        var elsewhere = await jobs.Routes.App.Send("POST", Path(jobs.Project, Card, 4), Contract.Token, ("Origin", "https://evil.test"));
        Assert.Equal(StatusCodes.Status403Forbidden, elsewhere.Response.StatusCode);
    }

    private const string Feature = "features";

    private const string Card = "cards";

    private static string Path(Guid project, string kind, int number) =>
        string.Create(CultureInfo.InvariantCulture, $"/devflow/api/projects/{project}/{kind}/{number}/run-now");

    /// <summary>The owner presses "Run now anyway" on a feature's or a card's page.</summary>
    private static Task<HttpContext> RunNow(Jobs jobs, string kind, int number, Guid? project = null) =>
        jobs.Routes.App.Send("POST", Path(project ?? jobs.Project, kind, number), Contract.Token, ("Origin", "https://platform.test"));

    /// <summary>The owner presses "Back to the schedule".</summary>
    private static Task<HttpContext> Back(Jobs jobs, string kind, int number, Guid? project = null) =>
        jobs.Routes.App.Send("DELETE", Path(project ?? jobs.Project, kind, number), Contract.Token, ("Origin", "https://platform.test"));

    private static async Task NoSuch(HttpContext response)
    {
        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        Assert.Equal("application/problem+json", response.Response.ContentType);
    }

    /// <summary>Whether the page of the feature or card says its override is on.</summary>
    private static async Task<bool> On(Jobs jobs, string kind, int number)
    {
        var response = await jobs.Routes.App.Get(string.Create(CultureInfo.InvariantCulture, $"/devflow/api/projects/{jobs.Project}/{kind}/{number}"), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (bool)JsonNode.Parse(await Harness.Body(response))!["run_now"]!["on"]!;
    }

    /// <summary>The numbers of the stored overrides of one kind, read past row level
    /// security.</summary>
    private static async Task<List<int>> Stored(Jobs jobs, string column)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<int>(new CommandDefinition(
            $"SELECT {column} FROM run_now WHERE project_id = @project AND {column} IS NOT NULL ORDER BY {column}",
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    private static async Task SetFeature(Jobs jobs, int number, string set)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            $"UPDATE features SET {set} WHERE project_id = @project AND number = @number",
            new { project = jobs.Project, number },
            cancellationToken: Ct));
    }

    /// <summary>By schedule with no window: outside it, whatever the clock.</summary>
    private static Task Outside(Jobs jobs) => Schedule(jobs, "schedule");

    /// <summary>The owner saves the mode and the windows, as the settings page does.</summary>
    private static async Task Schedule(Jobs jobs, string runsBy, JsonObject? window = null)
    {
        var body = new JsonObject
        {
            ["runs_by"] = runsBy,
            ["schedules"] = window is null ? new JsonArray() : new JsonArray(window.DeepClone()),
        };
        var response = await jobs.Routes.App.SendJson("PUT", ScheduleRouteTests.Path(jobs.Project), Contract.Token, body.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    /// <summary>The rule's decision for the project as the first worker finds it.</summary>
    private static async Task<Decision> Rule(Jobs jobs)
    {
        var situation = await RunDecision.Read(jobs.Routes.Door, Jobs.User, jobs.Project, Jobs.FirstWorker, Jobs.Image, jobs.Routes.Time.GetUtcNow(), Ct);
        Assert.NotNull(situation);
        return RunDecision.Of(situation);
    }

    /// <summary>Running now's queue, each card by its number.</summary>
    private static async Task<Dictionary<int, JsonNode>> NextUp(Jobs jobs)
    {
        var response = await jobs.Routes.App.Get("/devflow/api/running", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!["next"]!.AsArray().ToDictionary(card => (int)card!["number"]!, card => card!);
    }
}