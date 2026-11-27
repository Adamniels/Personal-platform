using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Usage;

/// <summary>
/// <c>GET /devflow/api/usage</c>: what each project, feature and card used in a period, its runs,
/// agent time, tokens by the one definition and cache reads, with each card's and feature's elapsed
/// time, fix rounds and sends back. Against real Postgres, through the door as the user. See "Usage
/// and history" in devflow/README.md.
/// </summary>
public sealed partial class UsageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TimeSpan Minutes(double minutes) => TimeSpan.FromMinutes(minutes);

    private static TimeSpan Hours(double hours) => TimeSpan.FromHours(hours);

    private static TimeSpan Days(double days) => TimeSpan.FromDays(days);

    private static void Figures(JsonNode row, int runs, long agentMs, long tokens, long cacheReads)
    {
        Assert.Equal(runs, (int)row["runs"]!);
        Assert.Equal(agentMs, (long)row["agent_ms"]!);
        Assert.Equal(tokens, (long)row["tokens"]!);
        Assert.Equal(cacheReads, (long)row["cache_reads"]!);
    }

    [Fact]
    public async Task ProjectsFeaturesAndCards()
    {
        await using var world = await UsageWorld.Start();
        var platform = await world.Project("Personal-platform");
        var fodr = await world.Project("fodr", repository: 99);

        // A feature with two cards, the first sent back once, and a card on its own.
        var feature = await world.Feature(platform, 10, fixRounds: 1);
        await world.Card(platform, 11, feature, position: 1, fixRounds: 2, sentBacks: 1);
        await world.Card(platform, 12, feature, position: 2);
        await world.Card(platform, 20);

        // The cards' runs: tokens are input + cache writes + output, cache reads beside.
        await world.Run(platform, "build", Hours(3), Hours(2), card: 11, durationMs: 3_000_000, input: 100, cacheCreation: 1_000, cacheRead: 50_000, output: 900);
        await world.Run(platform, "review", Hours(2), Minutes(110), card: 11, durationMs: 600_000, input: 10, cacheCreation: 0, cacheRead: 1_000, output: 90);
        await world.Run(platform, "build", Hours(1), Minutes(30), card: 12, durationMs: 1_800_000, input: 0, cacheCreation: 500, cacheRead: 0, output: 500);
        // The feature's own runs, its review, a conflict and a fix, count to it and to no card.
        await world.Run(platform, "feature_review", Minutes(20), Minutes(10), feature: 10, durationMs: 600_000, input: 1, cacheCreation: 2, cacheRead: 3, output: 7);
        await world.Run(platform, "conflict", Minutes(9), Minutes(8), feature: 10, durationMs: 60_000, input: 20, output: 20);
        await world.Run(platform, "feature_fix", Minutes(7), Minutes(6), feature: 10, durationMs: 60_000, input: 50, output: 50);
        // The card on its own.
        await world.Run(platform, "build", Hours(5), Hours(4), card: 20, durationMs: 3_600_000, input: 4_000, cacheRead: 7_000, output: 1_000);
        // Setting up: a check, which runs no model, and a setup's run.
        await world.Run(platform, "check", Days(10), Days(10) - Minutes(1), durationMs: 60_000);
        await world.Run(platform, "setup", Days(9), Days(9) - Minutes(30), durationMs: 1_800_000, input: 300, output: 700);
        // The other project used more, so it comes first.
        await world.Card(fodr, 5);
        await world.Run(fodr, "build", Days(1), Days(1) - Hours(1), card: 5, durationMs: 3_600_000, input: 60_000, output: 40_000);

        var usage = await world.Usage("?period=30d");

        Assert.Equal("30d", (string)usage["period"]!);
        var projects = usage["projects"]!.AsArray();
        Assert.Equal(["fodr", "Personal-platform"], projects.Select(project => (string)project!["name"]!));
        Assert.Equal(fodr, (Guid)projects[0]!["id"]!);
        var shown = projects[1]!;
        Assert.Equal(platform, (Guid)shown["id"]!);
        Figures(shown, runs: 9, agentMs: 11_580_000, tokens: 9_250, cacheReads: 58_003);

        // Setting up, the project's own runs.
        Figures(shown["setting_up"]!, runs: 2, agentMs: 1_860_000, tokens: 1_000, cacheReads: 0);

        // The feature: its cards' runs and its own together.
        var tenth = Assert.Single(shown["features"]!.AsArray())!;
        Assert.Equal(10, (int)tenth["number"]!);
        Assert.Equal("Feature 10", (string)tenth["title"]!);
        Figures(tenth, runs: 6, agentMs: 6_120_000, tokens: 3_250, cacheReads: 51_003);
        // From its first run's start, card 11's build 3 hours ago, to its last run's end, its fix's 6
        // minutes ago.
        Assert.Equal((long)(Hours(3) - Minutes(6)).TotalMilliseconds, (long)tenth["elapsed_ms"]!);
        Assert.Equal(1, (int)tenth["fix_rounds"]!);
        Assert.Equal(1, (int)tenth["sent_back"]!);
        // The feature's own runs, apart.
        Figures(tenth["own"]!, runs: 3, agentMs: 720_000, tokens: 150, cacheReads: 3);

        // Its cards, by tokens.
        var cards = tenth["cards"]!.AsArray();
        Assert.Equal([11, 12], cards.Select(card => (int)card!["number"]!));
        Assert.Equal("Card 11", (string)cards[0]!["title"]!);
        Figures(cards[0]!, runs: 2, agentMs: 3_600_000, tokens: 2_100, cacheReads: 51_000);
        Figures(cards[1]!, runs: 1, agentMs: 1_800_000, tokens: 1_000, cacheReads: 0);

        // The card on its own.
        var alone = Assert.Single(shown["cards"]!.AsArray())!;
        Assert.Equal(20, (int)alone["number"]!);
        Figures(alone, runs: 1, agentMs: 3_600_000, tokens: 5_000, cacheReads: 7_000);

        // A project with no check or setup run in the period has no Setting up row, and no features.
        var other = projects[0]!;
        Assert.Null(other["setting_up"]);
        Assert.Empty(other["features"]!.AsArray());

        // Without a token, 401 with an empty body.
        var refused = await world.Routes.App.Get(UsageWorld.Path);
        Assert.Equal(StatusCodes.Status401Unauthorized, refused.Response.StatusCode);
        Assert.Equal("", await Harness.Body(refused));
    }

    [Fact]
    public async Task ACardsRow()
    {
        await using var world = await UsageWorld.Start();
        var project = await world.Project("Personal-platform");
        await world.Card(project, 7, fixRounds: 3, sentBacks: 2);

        // A build 40 days ago, before the period; a fix whose worker measured no time; and a review
        // under way for 30 minutes so far.
        await world.Run(project, "build", Days(40), Days(40) - Minutes(10), card: 7, durationMs: 600_000, input: 1_000);
        await world.Run(project, "fix", Hours(2), Hours(1), card: 7, input: 10, cacheCreation: 20, cacheRead: 400, output: 30);
        await world.Run(project, "review", Minutes(30), null, card: 7, input: 5, cacheRead: 100, output: 5);
        // A feature's card that has no run is not a row.
        var feature = await world.Feature(project, 8);
        await world.Card(project, 9, feature, position: 1);

        var usage = await world.Usage();

        var card = Assert.Single(Assert.Single(usage["projects"]!.AsArray())!["cards"]!.AsArray())!;
        Assert.Equal(7, (int)card["number"]!);
        Assert.Equal("Card 7", (string)card["title"]!);
        // Its runs in the period; the fix's time from its start to its end, the review's so far.
        Figures(card, runs: 2, agentMs: (long)(Hours(1) + Minutes(30)).TotalMilliseconds, tokens: 70, cacheReads: 500);
        // Elapsed is the card's whole: from the build 40 days ago until now, the review not ended.
        Assert.Equal((long)Days(40).TotalMilliseconds, (long)card["elapsed_ms"]!);
        Assert.Equal(3, (int)card["fix_rounds"]!);
        Assert.Equal(2, (int)card["sent_back"]!);
    }

    [Fact]
    public async Task ThePeriodCountsRunsStartedInIt()
    {
        await using var world = await UsageWorld.Start();
        var project = await world.Project("Personal-platform");
        var feature = await world.Feature(project, 4);
        await world.Card(project, 1);
        await world.Card(project, 2);
        await world.Card(project, 3);
        await world.Card(project, 5);
        await world.Run(project, "build", Days(2), Days(2) - Hours(1), card: 1, durationMs: 1_000, input: 1);
        await world.Run(project, "build", Days(7), Days(7) - Hours(1), card: 2, durationMs: 1_000, input: 2);
        await world.Run(project, "build", Days(7) - Minutes(10), Days(7) - Minutes(5), card: 5, durationMs: 1_000, input: 0);
        await world.Run(project, "build", Days(40), Days(40) - Hours(1), card: 3, durationMs: 1_000, input: 3);
        await world.Run(project, "feature_review", Days(8), Days(8) - Hours(1), feature: 4, durationMs: 1_000, input: 4);

        // A project whose only run is 40 days old is in no period but all time.
        var old = await world.Project("old", repository: 77);
        await world.Card(old, 1);
        await world.Run(old, "build", Days(40), Days(40) - Hours(1), card: 1, durationMs: 1_000, input: 1);

        // Another user's project and runs never count, in any period.
        var bob = await world.Project("bobs-repository", Reading.Routes.Bob, repository: 4242);
        await world.Card(bob, 1);
        await world.Run(bob, "build", Days(1), Days(1) - Hours(1), card: 1, durationMs: 1_000, input: 1_000_000);

        static int[] Cards(JsonObject usage) =>
            [.. usage["projects"]!.AsArray().SelectMany(project => project!["cards"]!.AsArray()).Select(card => (int)card!["number"]!).Order()];

        static int[] Features(JsonObject usage) =>
            [.. usage["projects"]!.AsArray().SelectMany(project => project!["features"]!.AsArray()).Select(feature => (int)feature!["number"]!)];

        // 7 times 24 hours back from now, the edge in it; 30 days; and all time.
        var week = await world.Usage("?period=7d");
        Assert.Equal("7d", (string)week["period"]!);
        Assert.Equal([1, 2, 5], Cards(week));
        Assert.Empty(Features(week));
        var month = await world.Usage("?period=30d");
        Assert.Equal([1, 2, 5], Cards(month));
        Assert.Equal([4], Features(month));
        var all = await world.Usage("?period=all");
        Assert.Equal("all", (string)all["period"]!);
        Assert.Equal([1, 1, 2, 3, 5], Cards(all));
        Assert.Equal([4], Features(all));
        Assert.Equal(["Personal-platform", "old"], all["projects"]!.AsArray().Select(shown => (string)shown!["name"]!));
        Assert.Equal(["Personal-platform"], month["projects"]!.AsArray().Select(shown => (string)shown!["name"]!));
        Assert.Equal(10, (long)all["projects"]![0]!["tokens"]!);

        // 30 days is the period without one.
        var unnamed = await world.Usage();
        Assert.Equal("30d", (string)unnamed["period"]!);
        Assert.Equal([1, 2, 5], Cards(unnamed));

        // The period rolls with the clock: 20 minutes on, the runs of cards 2 and 5 started more
        // than 7 times 24 hours ago, and only card 1's is left in the week. The clock moves less
        // than the test's token lasts.
        world.Routes.Time.Advance(Minutes(20));
        var later = await world.Usage("?period=7d");
        Assert.Equal([1], Cards(later));

        // Any other period is refused, naming it.
        var refused = await world.Routes.App.Get(UsageWorld.Path + "?period=90d", Contract.Token);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
        Assert.Contains("period", await Harness.Body(refused), StringComparison.Ordinal);
    }
}