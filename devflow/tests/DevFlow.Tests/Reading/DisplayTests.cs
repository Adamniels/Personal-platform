using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Reading;
using DevFlow.Tests.Runner;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Reading;

/// <summary>
/// <c>GET /devflow/api/display</c>, what Dev flow offers the core's front page: how many pull
/// requests wait for the caller and how many stopped, how many projects they have, and whether the
/// picture is stale or their link needs renewing. Read from the stored snapshot alone. The data is
/// a contract between Dev flow and the core, pinned by contracts/display/devflow.json. See "What a
/// feature exposes for the front page" in Core/docs/core-architecture.md.
/// </summary>
public sealed class DisplayTests
{
    private const string Display = "/devflow/api/display";

    private static readonly World Waiting = new();

    private static readonly World Stopped = Numbered(Waiting, 43).Labelled("owner-blocked");

    /// <summary>The pull request as number <paramref name="number"/>, with its own head and address,
    /// approved for that head. A stopped one is stopped whatever its verdict.</summary>
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

    private static async Task<JsonObject> Displayed(Routes routes)
    {
        var response = await routes.App.Get(Display, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.StartsWith("application/json", response.Response.ContentType, StringComparison.Ordinal);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    private static JsonObject Expected(int waiting, int stopped, int projects, bool stale = false, bool needsRelink = false) => new()
    {
        ["waiting"] = waiting,
        ["stopped"] = stopped,
        ["projects"] = projects,
        ["stale"] = stale,
        ["needs_relink"] = needsRelink,
    };

    private static void AssertDisplay(JsonObject expected, JsonObject actual) =>
        Assert.True(JsonNode.DeepEquals(expected, actual), actual.ToJsonString());

    [Fact]
    public async Task TheDisplayDataCountsWhatWaits()
    {
        await using var routes = await Routes.Start();
        var mine = await routes.Project(Routes.User);
        var bobs = await routes.Project(Routes.Bob, name: "bobs-repository");
        // The owner's login, so an answer by him is told apart and sends a pull request back.
        await Reading.Link(routes.Door, Routes.User, World.Owner);
        routes.Fake.Replace(Routes.User, Reading.Staging(
        [
            Reading.From(Waiting),
            Reading.From(SentBack(Numbered(Waiting, 44))),
            Reading.From(Stopped),
            Reading.From(Numbered(Stopped, 45)),
            Reading.From(SentBack(Numbered(Stopped, 46))),
        ], [Reading.SliceOf(Waiting)]));
        // Bob's are never the caller's.
        routes.Fake.Replace(Routes.Bob, Reading.Staging([Reading.From(Waiting), Reading.From(Numbered(Waiting, 47))], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(mine, Routes.User));
        Assert.Equal(ReadResult.Read, await routes.Read(bobs, Routes.Bob));
        var stored = await Reading.StoredItems(routes.Database, mine);
        Assert.Equal(5, stored.Count);

        var display = await Displayed(routes);

        // One waiting and two stopped; the one of each sent back is the runner's, so in neither.
        AssertDisplay(Expected(waiting: 1, stopped: 2, projects: 1), display);
    }

    [Fact]
    public async Task NoProjectAndNothingWaitingAreDifferent()
    {
        await using var routes = await Routes.Start();

        // Nothing connected: no project, and nothing about one is stale.
        AssertDisplay(Expected(0, 0, projects: 0), await Displayed(routes));

        // Connected and read, with nothing that is his turn.
        var project = await routes.Project(Routes.User);
        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(Waiting.Labelled("in-review"))], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(project, Routes.User));

        AssertDisplay(Expected(0, 0, projects: 1), await Displayed(routes));
    }

    [Fact]
    public async Task AStaleOrFailingProjectIsSaid()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        // Never read is stale, by the rule the waiting list says it with.
        AssertDisplay(Expected(0, 0, 1, stale: true), await Displayed(routes));

        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(Waiting)], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(project, Routes.User));
        AssertDisplay(Expected(1, 0, 1), await Displayed(routes));

        // Five minutes without a read is not stale yet; a second more is.
        routes.Time.Advance(WaitingStore.StaleAfter);
        AssertDisplay(Expected(1, 0, 1), await Displayed(routes));
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        AssertDisplay(Expected(1, 0, 1, stale: true), await Displayed(routes));

        // A read that fails is said at once, though the last success is a minute old, and what
        // was stored still counts.
        Assert.Equal(ReadResult.Read, await routes.Read(project, Routes.User));
        routes.Time.Advance(TimeSpan.FromMinutes(1));
        routes.Fake.SetUnavailable(Routes.User, true);
        Assert.Equal(ReadResult.Failed, await routes.Read(project, Routes.User));
        AssertDisplay(Expected(1, 0, 1, stale: true), await Displayed(routes));

        // One fresh project does not hide another that is stale.
        routes.Fake.SetUnavailable(Routes.User, false);
        Assert.Equal(ReadResult.Read, await routes.Read(project, Routes.User));
        AssertDisplay(Expected(1, 0, 1), await Displayed(routes));
        await routes.Project(Routes.User, repository: 9002, name: "never-read");
        AssertDisplay(Expected(1, 0, 2, stale: true), await Displayed(routes));
    }

    [Fact]
    public async Task ANeededRelinkIsSaid()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(Waiting)], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(project, Routes.User));

        // Without a link, and linked, there is nothing to renew.
        AssertDisplay(Expected(1, 0, 1), await Displayed(routes));
        await Reading.Link(routes.Door, Routes.User, World.Owner);
        AssertDisplay(Expected(1, 0, 1), await Displayed(routes));

        await using (var admin = await routes.Database.OpenAdmin(Reading.Ct))
        {
            await TestDatabaseExtensions.Execute(admin, "UPDATE github_accounts SET state = 'needs_relink'");
        }

        // Said on its own, and not counted among the pull requests that wait.
        AssertDisplay(Expected(1, 0, 1, needsRelink: true), await Displayed(routes));
    }

    [Fact]
    public async Task TheResponseMatchesTheCommittedFixture()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(
            Repository.Path("contracts", "display", "devflow.json"), Reading.Ct));
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(Waiting), Reading.From(Stopped)], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(project, Routes.User));

        var display = await Displayed(routes);

        Assert.Equal(Shape(fixture), Shape(display));
    }

    /// <summary>A JSON value's keys and the JSON type of each value, as one line: what the core's
    /// type reads. Review's DisplayTests has the same, for its nested shape.</summary>
    private static string Shape(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(", ", obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}: {Shape(pair.Value)}")) + "}",
        JsonArray array => "[" + (array.Count == 0 ? "" : Shape(array[0])) + "]",
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.Number => value.ToString().Contains('.', StringComparison.Ordinal) ? "float" : "integer",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            var kind => kind.ToString().ToLowerInvariant(),
        },
        _ => "null",
    };

    [Fact]
    public async Task WithoutATokenIs401()
    {
        await using var routes = await Routes.Start();

        var without = await routes.App.Get(Display);
        Assert.Equal(StatusCodes.Status401Unauthorized, without.Response.StatusCode);
        Assert.Equal("", await Harness.Body(without));

        var forged = await routes.App.Send("GET", Display, token: null, ("Authorization", $"Bearer {Contract.Token}x"));
        Assert.Equal(StatusCodes.Status401Unauthorized, forged.Response.StatusCode);
        Assert.Equal("", await Harness.Body(forged));
    }

    [Fact]
    public async Task TheTokenIsReadFromTheAuthorizationHeader()
    {
        await using var routes = await Routes.Start();
        await routes.Project(Routes.User);

        // The core has no cookie jar, so it sends the user's token as a bearer token.
        var response = await routes.App.Send("GET", Display, token: null, ("Authorization", $"Bearer {Contract.Token}"));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        // The token's user, whose one project this is.
        Assert.Equal(1, (int)JsonNode.Parse(await Harness.Body(response))!["projects"]!);
    }

    [Fact]
    public async Task ItAnswersFromWhatIsStoredAndCallsNoCodeHost()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(Waiting), Reading.From(Stopped)], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(project, Routes.User));
        // What the code host says now would change the answer, were it asked.
        routes.Fake.Replace(Routes.User, Reading.Staging([], []));
        routes.Fake.SetUnavailable(Routes.User, true);
        var asked = routes.Host.Calls.Count;

        var display = await Displayed(routes);

        Assert.Equal(asked, routes.Host.Calls.Count);
        AssertDisplay(Expected(1, 1, 1), display);
    }
}