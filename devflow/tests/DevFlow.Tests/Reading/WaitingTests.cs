using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Reading;
using DevFlow.Tests.Runner;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Reading;

/// <summary>
/// <c>GET /devflow/api/waiting</c>: what is stored for the caller, never the code host, with how
/// many things wait for them, whether their link needs renewing, and how old each project's picture
/// is. See "What waits" in devflow/README.md.
/// </summary>
public sealed class WaitingTests
{
    private static CancellationToken Ct => Reading.Ct;

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The runner's waiting pull request, 42, its body showing a pair of screenshots, the
    /// phone one first, and a before shot.</summary>
    private static readonly World Waiting = new World() with
    {
        Pull = new World().Pull with
        {
            Body = World.Body + "\n\n" + string.Join('\n', new[] { "item-phone", "item-desktop-before", "item-desktop" }.Select(name =>
                $"![{name}](https://github.com/Adamniels/Personal-platform/blob/pr-screenshots/42/{name}.png?raw=true)")),
        },
    };

    /// <summary>A stopped one, 43, which the runner gave up on.</summary>
    private static readonly World Stopped = new World
    {
        Pull = Waiting.Pull with { Number = 43, HeadCommit = new string('c', 40), Url = new Uri("https://github.com/Adamniels/Personal-platform/pull/43") },
        Comments = [],
    }.Labelled("owner-blocked");

    private static async Task<JsonObject> Waited(Routes routes)
    {
        var response = await routes.App.Get(Routes.WaitingPath, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    private static string At(DateTimeOffset at) => at.ToString("yyyy-MM-ddTHH:mm:ss+00:00", CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheListIsWhatIsStoredForTheCaller()
    {
        await using var routes = await Routes.Start();
        var mine = await routes.Project(Routes.User);
        var bobs = await routes.Project(Routes.Bob, name: "bobs-repository");
        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(Waiting)], [Reading.SliceOf(Waiting)]));
        routes.Fake.Replace(Routes.Bob, Reading.Staging([Reading.From(Waiting), Reading.From(Stopped)], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(mine, Routes.User));
        Assert.Equal(ReadResult.Read, await routes.Read(bobs, Routes.Bob));
        var readAt = routes.Time.GetUtcNow();
        // The code host is not asked again, and what it says now does not change the answer.
        routes.Fake.SetUnavailable(Routes.User, true);
        var asked = routes.Host.Calls.Count;

        var waited = await Waited(routes);

        Assert.Equal(asked, routes.Host.Calls.Count);
        var item = Waiting.Read()!;
        var stored = Assert.Single(await Reading.StoredItems(routes.Database, mine));
        var expected = new JsonObject
        {
            ["count"] = 1,
            ["needs_relink"] = false,
            ["items"] = new JsonArray(new JsonObject
            {
                ["id"] = stored.Id.ToString(),
                ["project_id"] = mine.ToString(),
                ["project_name"] = "Personal-platform",
                ["number"] = 42,
                ["kind"] = "waiting",
                ["sent_back"] = false,
                ["title"] = item.Title,
                ["web_url"] = item.Url.ToString(),
                ["slice_number"] = 7,
                ["head_commit"] = item.HeadCommit,
                ["can_merge"] = true,
                ["summary"] = item.Summary,
                ["reason"] = item.Reason,
                ["decisions"] = JsonSerializer.SerializeToNode(item.OwnerDecisions, Snake),
                ["other_decisions"] = item.OtherDecisions,
                ["reviewer_view"] = JsonSerializer.SerializeToNode(item.ReviewerView, Snake),
                ["screenshots"] = new JsonArray(
                    new JsonObject { ["name"] = "item", ["images"] = new JsonArray("item-desktop", "item-phone") },
                    new JsonObject { ["name"] = "item-before", ["images"] = new JsonArray("item-desktop-before") }),
                ["read_at"] = At(readAt),
            }),
            ["projects"] = new JsonArray(new JsonObject
            {
                ["id"] = mine.ToString(),
                ["owner"] = "Adamniels",
                ["name"] = "Personal-platform",
                ["last_success_at"] = At(readAt),
                ["stale"] = false,
                ["failing"] = false,
            }),
            // Nothing landed on the default branch, so nothing of Dev flow's own waits, and no
            // card of its own was built.
            ["own_items"] = new JsonArray(),
            ["cards"] = new JsonArray(),
            ["stopped_cards"] = new JsonArray(),
            ["refused_cards"] = new JsonArray(),
            ["failing_projects"] = new JsonArray(),
            ["decisions"] = new JsonArray(),
            // No feature of its own is ready or waits.
            ["features"] = new JsonArray(),
            // And no card of its own waits for answers.
            ["question_cards"] = new JsonArray(),
            // Nor has a check of its setup failed.
            ["setup_projects"] = new JsonArray(),
            ["setup_runs"] = new JsonArray(),
        };
        Assert.True(JsonNode.DeepEquals(expected, waited), waited.ToJsonString());
        // Bob's two are stored, and none of them is the caller's.
        Assert.Equal(2, (await Reading.StoredItems(routes.Database, bobs)).Count);
    }

    [Fact]
    public async Task TheCountLeavesOutWhatWasSentBack()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        await Reading.Link(routes.Door, Routes.User, World.Owner);
        // The owner answered the waiting one, so it is the runner's until it comes back.
        var answered = Waiting with { Comments = [.. Waiting.Comments, World.By(World.Owner, 2, "Rename it.", World.At.AddMinutes(5))] };
        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(answered), Reading.From(Stopped)], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(project, Routes.User));

        var waited = await Waited(routes);

        Assert.Equal(1, (int)waited["count"]!);
        var items = waited["items"]!.AsArray();
        Assert.Equal([(42, true), (43, false)], items.Select(i => ((int)i!["number"]!, (bool)i["sent_back"]!)));
    }

    [Fact]
    public async Task AProjectNotReadForFiveMinutesIsStale()
    {
        await using var routes = await Routes.Start();
        var read = await routes.Project(Routes.User);
        var never = await routes.Project(Routes.User, repository: 9002, name: "zz-never-read");
        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(Waiting)], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(read, Routes.User));
        var readAt = routes.Time.GetUtcNow();

        async Task<(JsonNode Read, JsonNode Never)> Projects()
        {
            var projects = (await Waited(routes))["projects"]!.AsArray();
            return (projects.Single(p => (string)p!["id"]! == read.ToString())!, projects.Single(p => (string)p!["id"]! == never.ToString())!);
        }

        // At five minutes it is not, and a project never read is, without a success to name.
        routes.Time.Advance(TimeSpan.FromMinutes(5));
        var (atFive, neverRead) = await Projects();
        Assert.False((bool)atFive["stale"]!);
        Assert.Equal(At(readAt), (string)atFive["last_success_at"]!);
        Assert.True((bool)neverRead["stale"]!);
        Assert.Null(neverRead["last_success_at"]);
        Assert.False((bool)neverRead["failing"]!);

        // One second later it is.
        routes.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((bool)(await Projects()).Read["stale"]!);

        // A read that fails leaves it stale, and failing; the next that succeeds ends both.
        routes.Fake.SetUnavailable(Routes.User, true);
        Assert.Equal(ReadResult.Failed, await routes.Read(read, Routes.User));
        var failed = (await Projects()).Read;
        Assert.Equal((true, true), ((bool)failed["stale"]!, (bool)failed["failing"]!));
        routes.Fake.SetUnavailable(Routes.User, false);
        Assert.Equal(ReadResult.Read, await routes.Read(read, Routes.User));
        var fresh = (await Projects()).Read;
        Assert.Equal((false, false), ((bool)fresh["stale"]!, (bool)fresh["failing"]!));
    }

    [Fact]
    public async Task ANeededRelinkIsCountedAndNamed()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        routes.Fake.Replace(Routes.User, Reading.Staging([Reading.From(Waiting)], [Reading.SliceOf(Waiting)]));
        Assert.Equal(ReadResult.Read, await routes.Read(project, Routes.User));

        // Without a link, and linked, nothing more waits.
        var unlinked = await Waited(routes);
        Assert.Equal((1, false), ((int)unlinked["count"]!, (bool)unlinked["needs_relink"]!));
        await Reading.Link(routes.Door, Routes.User, World.Owner);
        var linked = await Waited(routes);
        Assert.Equal((1, false), ((int)linked["count"]!, (bool)linked["needs_relink"]!));

        // A link GitHub stopped taking is one thing more for him to do.
        await using (var admin = await routes.Database.OpenAdmin(Ct))
        {
            await TestDatabaseExtensions.Execute(admin, "UPDATE github_accounts SET state = 'needs_relink'");
        }

        var relink = await Waited(routes);
        Assert.Equal((2, true), ((int)relink["count"]!, (bool)relink["needs_relink"]!));
        Assert.Single(relink["items"]!.AsArray());
    }

    [Fact]
    public async Task WithoutATokenItIs401()
    {
        await using var routes = await Routes.Start();

        var response = await routes.App.Get(Routes.WaitingPath);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
        Assert.Equal("", await Harness.Body(response));
    }
}