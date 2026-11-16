using System.Globalization;
using System.Text.Json.Nodes;

using DevFlow.Reading;
using DevFlow.Tests.Runner;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Reading;

/// <summary>
/// <c>GET /devflow/api/attention</c>, Dev flow's answer to the nav bar: whether anything here needs
/// the caller, an item that is not sent back or a GitHub link that has expired. Read from the stored
/// snapshot alone, never from the code host. See "The attention endpoint" in
/// High-level/docs/feature-contract.md.
/// </summary>
public sealed class AttentionTests
{
    private const string Attention = "/devflow/api/attention";

    private static readonly World Waiting = new();

    private static readonly World Stopped = Numbered(Waiting, 43).Labelled("owner-blocked");

    /// <summary>The pull request as number <paramref name="number"/>, with its own head and address,
    /// approved for that head, as <see cref="DisplayTests"/> numbers its own.</summary>
    private static World Numbered(World world, int number)
    {
        var head = number.ToString("x40", CultureInfo.InvariantCulture);
        return world with
        {
            Pull = world.Pull with
            {
                Number = number,
                HeadCommit = head,
                Url = new Uri($"https://github.com/Adamniels/Personal-platform/pull/{number}"),
            },
            Comments = [World.Verdict(1, "approve", head, World.At)],
        };
    }

    /// <summary>The world with a comment by the owner after everything else on it, so it is his
    /// answer and the runner's turn.</summary>
    private static World SentBack(World world) => world with
    {
        Comments = [.. world.Comments, World.By(World.Owner, 2, "Rename it.", World.At.AddMinutes(5))],
    };

    private static async Task<JsonObject> Asked(Routes routes)
    {
        var response = await routes.App.Get(Attention, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.StartsWith("application/json", response.Response.ContentType, StringComparison.Ordinal);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    private static void AssertAttention(bool expected, JsonObject actual) =>
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["attention"] = expected }, actual), actual.ToJsonString());

    /// <summary>The caller's one project, linked as the owner and read with <paramref name="worlds"/>
    /// staged.</summary>
    private static async Task<Guid> Staged(Routes routes, params World[] worlds)
    {
        var project = await routes.Project(Routes.User);
        // The owner's login, so an answer by him is told apart and sends a pull request back.
        await Reading.Link(routes.Door, Routes.User, World.Owner);
        routes.Fake.Replace(Routes.User, Reading.Staging([.. worlds.Select(Reading.From)], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(project, Routes.User));
        return project;
    }

    [Fact]
    public async Task ItSaysWhetherSomethingWaits()
    {
        // Nothing connected, and so nothing waits.
        await using (var routes = await Routes.Start())
        {
            AssertAttention(false, await Asked(routes));
        }

        // A pull request that waits for the owner.
        await using (var routes = await Routes.Start())
        {
            await Staged(routes, Waiting);
            AssertAttention(true, await Asked(routes));
        }

        // One the runner stopped on.
        await using (var routes = await Routes.Start())
        {
            await Staged(routes, Stopped);
            AssertAttention(true, await Asked(routes));
        }

        // Only items the owner sent back, which are the runner's turn.
        await using (var routes = await Routes.Start())
        {
            var project = await Staged(routes, SentBack(Waiting), SentBack(Stopped));
            // Both are stored, so it is their being sent back that leaves the answer false.
            Assert.Equal(2, (await Reading.StoredItems(routes.Database, project)).Count);
            AssertAttention(false, await Asked(routes));
        }

        // Nothing waits, but the GitHub link has expired.
        await using (var routes = await Routes.Start())
        {
            await Staged(routes, Waiting.Labelled("in-review"));
            AssertAttention(false, await Asked(routes));
            await using (var admin = await routes.Database.OpenAdmin(Reading.Ct))
            {
                await TestDatabaseExtensions.Execute(admin, "UPDATE github_accounts SET state = 'needs_relink'");
            }

            AssertAttention(true, await Asked(routes));
        }
    }

    [Fact]
    public async Task ItIsTheCallersOwn()
    {
        await using var routes = await Routes.Start();
        var bobs = await routes.Project(Routes.Bob, name: "bobs-repository");
        routes.Fake.Replace(Routes.Bob, Reading.Staging([Reading.From(Waiting)], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(bobs, Routes.Bob));

        // Bob's pull request waits for Bob, not for the caller.
        AssertAttention(false, await Asked(routes));
    }

    [Fact]
    public async Task WithoutATokenIs401()
    {
        await using var routes = await Routes.Start();

        var without = await routes.App.Get(Attention);
        Assert.Equal(StatusCodes.Status401Unauthorized, without.Response.StatusCode);
        Assert.Equal("", await Harness.Body(without));

        var forged = await routes.App.Send("GET", Attention, token: null, ("Authorization", $"Bearer {Contract.Token}x"));
        Assert.Equal(StatusCodes.Status401Unauthorized, forged.Response.StatusCode);
        Assert.Equal("", await Harness.Body(forged));
    }

    [Fact]
    public async Task ItAnswersFromWhatIsStoredAndCallsNoCodeHost()
    {
        await using var routes = await Routes.Start();
        await Staged(routes, Waiting);
        // What the code host says now would change the answer, were it asked.
        routes.Fake.Replace(Routes.User, Reading.Staging([], []));
        routes.Fake.SetUnavailable(Routes.User, true);
        var asked = routes.Host.Calls.Count;

        var answer = await Asked(routes);

        Assert.Equal(asked, routes.Host.Calls.Count);
        AssertAttention(true, answer);
    }
}