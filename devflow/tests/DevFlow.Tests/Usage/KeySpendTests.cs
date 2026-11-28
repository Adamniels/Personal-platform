using System.Globalization;
using System.Text.Json.Nodes;

namespace DevFlow.Tests.Usage;

/// <summary>
/// The API key's spend in <c>GET /devflow/api/usage</c>: per project and per day of its last 30,
/// what its runs on the key cost by Claude Code's estimate, counted by when each finished, as the
/// daily cap counts them; and none for a day, or a project, the key was not used. Against real
/// Postgres, through the door as the user. See "Usage and history" and "The API key" in
/// devflow/README.md.
/// </summary>
public sealed partial class UsageTests
{
    [Fact]
    public async Task TheKeysSpendIsShown()
    {
        await using var world = await UsageWorld.Start();
        var platform = await world.Project("Personal-platform");
        var fodr = await world.Project("fodr", repository: 99);
        await world.Card(platform, 1);
        await world.Card(fodr, 5);

        // Two runs on the key that finished two hours ago, and one that finished yesterday; a run
        // on the subscription with an estimate, which is no spend on the key; and fodr, which never
        // ran on the key.
        await world.Run(platform, "build", TimeSpan.FromHours(3), TimeSpan.FromHours(2), card: 1, input: 10, credential: "api_key", cost: 1.25m);
        await world.Run(platform, "review", TimeSpan.FromHours(3), TimeSpan.FromHours(2), card: 1, input: 10, credential: "api_key", cost: 0.5m);
        await world.Run(platform, "build", TimeSpan.FromDays(1), TimeSpan.FromDays(1) - TimeSpan.FromMinutes(10), card: 1, input: 10, credential: "api_key", cost: 3m);
        await world.Run(platform, "build", TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), card: 1, input: 10, cost: 9m);
        await world.Run(fodr, "build", TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), card: 5, input: 10, cost: 4m);
        // Another user's run on their key never counts.
        var bob = await world.Project("bobs-repository", Reading.Routes.Bob, repository: 4242);
        await world.Card(bob, 1);
        await world.Run(bob, "build", TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), card: 1, input: 10, credential: "api_key", cost: 50m);

        var projects = (await world.Usage("?period=30d"))["projects"]!.AsArray();
        var zone = DevFlowTime.DefaultZone;
        var spent = Spend(projects.Single(project => (string)project!["name"]! == "Personal-platform")!);
        Assert.Equal(
            new Dictionary<DateOnly, decimal>
            {
                [DevFlowTime.DayOf(world.Now - TimeSpan.FromHours(2), zone)] = 1.75m,
                [DevFlowTime.DayOf(world.Now - TimeSpan.FromDays(1) + TimeSpan.FromMinutes(10), zone)] = 3m,
            },
            spent);

        // fodr never ran on the key: none on any day.
        var none = projects.Single(project => (string)project!["name"]! == "fodr")!;
        Assert.All(none["days"]!.AsArray(), day => Assert.Null(day!["api_key_usd"]));
    }

    /// <summary>A project's days that carry a spend on the key, by date.</summary>
    private static Dictionary<DateOnly, decimal> Spend(JsonNode project) =>
        project["days"]!.AsArray()
            .Where(day => day!["api_key_usd"] is not null)
            .ToDictionary(day => DateOnly.Parse((string)day!["date"]!, CultureInfo.InvariantCulture), day => (decimal)day!["api_key_usd"]!);
}