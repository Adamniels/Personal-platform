using System.Globalization;
using System.Text.Json.Nodes;

using DevFlow.Usage;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Usage;

/// <summary>
/// Each project's tokens per day in <c>GET /devflow/api/usage</c>: the last 30 days ending today in
/// <see cref="DevFlowTime.Zone"/>, whatever the period. Against real Postgres, through the door as
/// the user. See "Usage and history" in devflow/README.md.
/// </summary>
public sealed partial class UsageTests
{
    private static DateTimeOffset Utc(string instant) => DateTimeOffset.Parse(instant + "Z", CultureInfo.InvariantCulture);

    /// <summary>A project's days as the route answers them, by date.</summary>
    private static List<(DateOnly Date, long Tokens)> Days(JsonNode project) =>
        [.. project["days"]!.AsArray().Select(day => (DateOnly.Parse((string)day!["date"]!, CultureInfo.InvariantCulture), (long)day["tokens"]!))];

    /// <summary>The 30 days ending <paramref name="today"/>, each with the tokens
    /// <paramref name="used"/> names for it and 0 for any other.</summary>
    private static List<(DateOnly Date, long Tokens)> Expected(DateOnly today, Dictionary<DateOnly, long> used) =>
        [.. Enumerable.Range(0, 30).Select(back => today.AddDays(back - 29)).Select(day => (day, used.GetValueOrDefault(day)))];

    [Fact]
    public async Task TokensPerDay()
    {
        await using var world = await UsageWorld.Start();
        var platform = await world.Project("Personal-platform");
        var fodr = await world.Project("fodr", repository: 99);
        var feature = await world.Feature(platform, 4);
        await world.Card(platform, 1);
        await world.Card(fodr, 5);
        var today = DevFlowTime.DayOf(world.Now);

        // A card's build two hours ago; yesterday the feature's review and a setup's run, every run
        // of the project counting; a build 29 days ago, the first of the 30; and one 30 days and a
        // minute less ago, in the rolling 30 days' period but on the day before the first.
        await world.Run(platform, "build", Hours(2), Hours(1), card: 1, input: 100, cacheCreation: 0, cacheRead: 9_000, output: 20);
        await world.Run(platform, "feature_review", Days(1), Days(1) - Minutes(5), feature: 4, input: 10, output: 20);
        await world.Run(platform, "setup", Days(1) - Hours(1), Days(1) - Hours(2), input: 5);
        await world.Run(platform, "build", Days(29), Days(29) - Hours(1), card: 1, cacheCreation: 7);
        await world.Run(platform, "build", Days(30) - Minutes(1), Days(30) - Minutes(30), card: 1, input: 1_000);
        // The other project, three days ago.
        await world.Run(fodr, "build", Days(3), Days(3) - Hours(1), card: 5, input: 1_500, output: 500);
        // Another user's runs never count.
        var bob = await world.Project("bobs-repository", Reading.Routes.Bob, repository: 4242);
        await world.Card(bob, 1);
        await world.Run(bob, "build", Hours(1), Minutes(30), card: 1, input: 1_000_000);

        var usage = await world.Usage("?period=30d");
        var projects = usage["projects"]!.AsArray();
        Assert.Equal(["fodr", "Personal-platform"], projects.Select(project => (string)project!["name"]!));

        // Per project, 30 days ending today, oldest first, each with the tokens of the runs started
        // that day by the one definition, cache reads never in them, and 0 for a day with none.
        Assert.Equal(
            Expected(today, new()
            {
                [DevFlowTime.DayOf(world.Now - Hours(2))] = 120,
                [DevFlowTime.DayOf(world.Now - Days(1))] = 30 + 5,
                [today.AddDays(-29)] = 7,
            }),
            Days(projects[1]!));
        Assert.Equal(Expected(today, new() { [DevFlowTime.DayOf(world.Now - Days(3))] = 2_000 }), Days(projects[0]!));

        // The days are always the last 30, whatever the period: the last 7 days still has the
        // build 29 days ago among them.
        var week = await world.Usage("?period=7d");
        Assert.Equal(Days(projects[1]!), Days(week["projects"]!.AsArray().Single(project => (string)project!["name"]! == "Personal-platform")!));

        // A day is a day in Stockholm: in summer, two hours ahead of UTC, a run started at 23:30 UTC
        // counts to the next day. Read at a time of its own, through the store the route calls.
        var summer = await world.Project("summer", repository: 55);
        await world.Card(summer, 1);
        await world.RunAt(summer, "build", Utc("2026-07-01T21:30:00"), Utc("2026-07-01T21:45:00"), card: 1, input: 11);
        await world.RunAt(summer, "build", Utc("2026-07-01T23:30:00"), Utc("2026-07-01T23:45:00"), card: 1, input: 13);
        await world.RunAt(summer, "build", Utc("2026-07-02T08:00:00"), Utc("2026-07-02T08:15:00"), card: 1, input: 17);
        // The night summer time ends: 00:30 on 25 October is still summer time, 23:30 is winter
        // time, and both are 25 October, a day of 25 hours.
        await world.RunAt(summer, "build", Utc("2026-10-24T21:30:00"), Utc("2026-10-24T21:45:00"), card: 1, input: 2);
        await world.RunAt(summer, "build", Utc("2026-10-24T22:30:00"), Utc("2026-10-24T22:45:00"), card: 1, input: 3);
        await world.RunAt(summer, "build", Utc("2026-10-25T22:30:00"), Utc("2026-10-25T22:45:00"), card: 1, input: 4);
        await world.RunAt(summer, "build", Utc("2026-10-25T23:05:00"), Utc("2026-10-25T23:15:00"), card: 1, input: 5);

        static IReadOnlyList<UsageDay> SummerDays(UsageView view) => view.Projects.Single(project => project.Name == "summer").Days;

        var july = SummerDays(await UsageStore.For(world.Routes.Door, UsageWorld.User, "all", Utc("2026-07-02T10:00:00"), Ct));
        Assert.Equal(
            Expected(new DateOnly(2026, 7, 2), new() { [new DateOnly(2026, 7, 1)] = 11, [new DateOnly(2026, 7, 2)] = 13 + 17 }),
            july.Select(day => (day.Date, day.Tokens)).ToList());

        // 23:30 UTC on 25 October is 00:30 on the 26th in Stockholm, so today is the 26th.
        var october = SummerDays(await UsageStore.For(world.Routes.Door, UsageWorld.User, "all", Utc("2026-10-25T23:30:00"), Ct));
        Assert.Equal(
            Expected(new DateOnly(2026, 10, 26), new()
            {
                [new DateOnly(2026, 10, 24)] = 2,
                [new DateOnly(2026, 10, 25)] = 3 + 4,
                [new DateOnly(2026, 10, 26)] = 5,
            }),
            october.Select(day => (day.Date, day.Tokens)).ToList());
    }

    [Fact]
    public async Task TheFakeStagesAMonthOfUse()
    {
        const string Path = "/devflow/api/fake/usage-history";
        await using var world = await UsageWorld.Start();
        var project = await world.Project("Personal-platform");
        await world.Card(project, 1);
        var today = DevFlowTime.DayOf(world.Now);
        string Body(string days) => new JsonObject { ["project_id"] = project, ["days"] = JsonNode.Parse(days) }.ToJsonString();

        // Nothing to copy yet: a project needs a finished run, so the copies are runs it could have.
        Assert.Equal(StatusCodes.Status404NotFound, (await world.Routes.App.SendJson("POST", Path, Contract.Token, Body("""[{"days_ago": 1, "tokens": 5}]"""))).Response.StatusCode);
        await world.Run(project, "build", Minutes(30), Minutes(10), card: 1, input: 40, output: 2, cacheRead: 900);

        // Each day named gets a copy of the project's last finished run, started that many days ago
        // in Dev flow's zone, with those tokens, and every other figure of it as it was.
        var staged = await world.Routes.App.SendJson("POST", Path, Contract.Token, Body("""[{"days_ago": 0, "tokens": 100}, {"days_ago": 3, "tokens": 5000}, {"days_ago": 29, "tokens": 7}]"""));
        Assert.Equal(StatusCodes.Status204NoContent, staged.Response.StatusCode);
        var usage = await world.Usage();
        var shown = Assert.Single(usage["projects"]!.AsArray())!;
        Assert.Equal(Expected(today, new() { [today] = 42 + 100, [today.AddDays(-3)] = 5_000, [today.AddDays(-29)] = 7 }), Days(shown));
        // The copies are finished runs of the same card, so its row counts them; cache reads were
        // the original's alone.
        Figures(Assert.Single(shown["cards"]!.AsArray())!, runs: 4, agentMs: (long)(Minutes(20).TotalMilliseconds + (3 * 600_000)), tokens: 42 + 5_107, cacheReads: 900);

        // Malformed: no project, a day in the future, or tokens below 0, each named.
        foreach (var (body, field) in new[]
        {
            ("""{"days": [{"days_ago": 1, "tokens": 5}]}""", "project_id"),
            (Body("""[{"days_ago": -1, "tokens": 5}]"""), "days[0].days_ago"),
            (Body("""[{"days_ago": 1, "tokens": 5}, {"days_ago": 2, "tokens": -5}]"""), "days[1].tokens"),
        })
        {
            var refused = await world.Routes.App.SendJson("POST", Path, Contract.Token, body);
            Assert.Equal(StatusCodes.Status400BadRequest, refused.Response.StatusCode);
            Assert.Contains(field + ":", await Harness.Body(refused), StringComparison.Ordinal);
        }

        // Another user's project is not the caller's to stage.
        var bob = await world.Project("bobs-repository", Reading.Routes.Bob, repository: 4242);
        await world.Card(bob, 1);
        await world.Run(bob, "build", Minutes(30), Minutes(10), card: 1, input: 1);
        var theirs = new JsonObject { ["project_id"] = bob, ["days"] = JsonNode.Parse("""[{"days_ago": 1, "tokens": 5}]""") }.ToJsonString();
        Assert.Equal(StatusCodes.Status404NotFound, (await world.Routes.App.SendJson("POST", Path, Contract.Token, theirs)).Response.StatusCode);
    }
}