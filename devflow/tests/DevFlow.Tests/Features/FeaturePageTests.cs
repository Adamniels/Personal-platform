using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

using static DevFlow.Tests.Features.FeaturePage;

namespace DevFlow.Tests.Features;

/// <summary>
/// <c>GET /devflow/api/projects/{id}/features/{number}</c>: a feature's whole story, read from
/// what Dev flow stored and never from the code host, through the door as the user. See "A
/// feature's page" in devflow/README.md.
/// </summary>
public sealed class FeaturePageTests
{
    [Fact]
    public async Task TheStoryIsComplete()
    {
        await using var jobs = await Feature();
        var built = await Landed(jobs);
        var buildRun = (await jobs.Runs()).Single(run => run.CardNumber == First && run.Kind == "build");
        var reads = jobs.Routes.Host.Calls.Count;

        var (status, story) = await Built.Get(jobs, PathOf(jobs.Project));

        Assert.Equal(StatusCodes.Status200OK, status);
        // Nothing is read from the code host to answer it.
        Assert.Equal(reads, jobs.Routes.Host.Calls.Count);
        var feature = await FeatureJobs.Feature(jobs, Parent);
        Assert.Equal(Parent, (int)story!["number"]!);
        // The repository the header names, and the default branch the feature merges into.
        Assert.Equal("Adamniels/Personal-platform", (string)story["repository"]!);
        Assert.Equal("main", (string?)story["default_branch"]);
        Assert.Equal("Dev flow: features", (string)story["title"]!);
        Assert.Equal($"https://github.com/Adamniels/Personal-platform/issues/{Parent}", (string)story["web_url"]!);
        Assert.Equal(feature.State, (string)story["state"]!);
        Assert.Equal(feature.Reason, (string?)story["reason"]);

        // The branch, at the commit the watch last judged on it: the first card's merge.
        Assert.Equal(Branch, (string)story["branch"]!["name"]!);
        Assert.Equal(await MergeCommit(jobs), (string)story["branch"]!["commit"]!);

        // No pull request before the feature's is opened.
        Assert.Null(story["pull_request"]);

        // The cards in their order, each with its state, fix rounds and tokens.
        var cards = story["cards"]!.AsArray();
        Assert.Equal([First, Second, Third], cards.Select(card => (int)card!["number"]!));
        Assert.Equal(["Read the sub-issues", "Show each feature", "The feature's page"], cards.Select(card => (string)card!["title"]!));
        var first = cards[0]!;
        Assert.Equal("done", (string)first["state"]!);
        Assert.Equal($"https://github.com/Adamniels/Personal-platform/issues/{First}", (string)first["web_url"]!);
        Assert.Equal(0, (int)first["fix_rounds"]!);
        var (tokens, cacheReads) = await Tokens(jobs, First);
        Assert.True(tokens > 0);
        Assert.Equal(tokens, (long)first["tokens"]!);
        Assert.Equal(cacheReads, (long)first["cache_reads"]!);
        Assert.Equal("queued", (string)cards[1]!["state"]!);
        Assert.Equal(0, (long)cards[1]!["tokens"]!);
        Assert.Equal(0, (long)cards[1]!["cache_reads"]!);

        // The names of its screenshots, of its head commit, read through the card's route.
        var screenshots = first["screenshots"]!;
        Assert.Equal((string)built["head_commit"]!, (string)screenshots["commit"]!);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""[{"name": "plan", "images": ["plan-desktop", "plan-phone"]}]"""),
            screenshots["groups"]), screenshots["groups"]!.ToJsonString());
        Assert.Null(cards[1]!["screenshots"]);

        // The decisions its latest report marked for me, the one that is not left out, each with
        // what was chosen, the alternative, why, the reviewer's view, and no answer yet.
        var decision = Assert.Single(first["decisions"]!.AsArray())!;
        Assert.Equal(buildRun.Id, Guid.Parse((string)decision["run_id"]!));
        Assert.Equal(1, (int)decision["position"]!);
        Assert.Equal("Show it on every list", (string)decision["chosen"]!);
        Assert.Equal("Only on habits", (string)decision["alternative"]!);
        Assert.Equal("The lists look the same", (string)decision["why"]!);
        Assert.Equal("Agreed: the lists share one component.", (string)decision["reviewer_view"]!);
        Assert.Null(decision["answer"]);
        Assert.Empty(cards[1]!["decisions"]!.AsArray());

        // Its pull request once it is opened.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE features SET pull_request_number = 52, pull_request_url = 'https://github.com/Adamniels/Personal-platform/pull/52' WHERE project_id = @project AND number = @number",
                new { project = jobs.Project, number = Parent },
                cancellationToken: Ct));
        }

        var (_, opened) = await Built.Get(jobs, PathOf(jobs.Project));
        Assert.Equal(52, (int)opened!["pull_request"]!["number"]!);
        Assert.Equal("https://github.com/Adamniels/Personal-platform/pull/52", (string)opened["pull_request"]!["url"]!);
    }

    [Fact]
    public async Task TokensByTheOneDefinition()
    {
        await using var jobs = await Feature();
        await Landed(jobs);
        // A run of the feature's own, its read, with figures of its own: 7 in, 300 cache writes,
        // 90,000 cache reads and 2,000 out.
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO runs (id, project_id, user_id, feature_number, worker_id, kind, branch, base, started_at, finished_at, result, run_minutes,
                                  input_tokens, cache_creation_input_tokens, cache_read_input_tokens, output_tokens)
                VALUES (gen_random_uuid(), @project, @user, @feature, @worker, 'read', 'main', 'main', now(), now(), 'succeeded', 90, 7, 300, 90000, 2000)
                """,
                new { project = jobs.Project, user = User, feature = Parent, worker = Jobs.Jobs.FirstWorker },
                cancellationToken: Ct));
        }

        var (_, story) = await Built.Get(jobs, PathOf(jobs.Project));

        // The feature's own runs, input plus cache writes plus output, and their cache reads beside.
        Assert.Equal(7 + 300 + 2_000, (long)story!["tokens"]!);
        Assert.Equal(90_000, (long)story["cache_reads"]!);
        var own = Assert.Single(story["runs"]!.AsArray())!;
        Assert.Equal(2_307, (long)own["tokens"]!);
        Assert.Equal(90_000, (long)own["cache_reads"]!);
        // Each card's, the same over its runs, and never its feature's.
        var (tokens, cacheReads) = await Tokens(jobs, First);
        var first = story["cards"]![0]!;
        Assert.Equal(tokens, (long)first["tokens"]!);
        Assert.Equal(cacheReads, (long)first["cache_reads"]!);
        Assert.True(cacheReads > 0);
        foreach (var shown in new[] { story.AsObject(), first.AsObject(), own.AsObject() })
        {
            Assert.False(shown.ContainsKey("input_tokens"));
            Assert.False(shown.ContainsKey("output_tokens"));
        }
    }

    [Fact]
    public async Task ACardsPageNamesItsFeature()
    {
        await using var jobs = await Feature();
        await jobs.Routes.Read(jobs.Project, User);

        var (_, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/{Second}");

        Assert.Equal(Parent, (int)card!["feature"]!["number"]!);
        Assert.Equal("Dev flow: features", (string)card["feature"]!["title"]!);
    }

    [Fact]
    public async Task ABranchNotYetMadeHasNone()
    {
        await using var jobs = await Feature();
        await jobs.Routes.Read(jobs.Project, User);

        var (status, story) = await Built.Get(jobs, PathOf(jobs.Project));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Null(story!["branch"]);
        Assert.Equal(3, story["cards"]!.AsArray().Count);
    }

    [Fact]
    public async Task AnotherUsersFeatureIs404()
    {
        await using var jobs = await Feature();
        await jobs.Routes.Read(jobs.Project, User);
        var bobs = await BobsFeature(jobs);

        // Bob's feature, by his project, is not there for Alice.
        Assert.Equal(StatusCodes.Status404NotFound, (await Built.Get(jobs, PathOf(bobs))).Status);
        // Nor a project that does not exist, a number no feature has, or a card's number.
        Assert.Equal(StatusCodes.Status404NotFound, (await Built.Get(jobs, PathOf(Guid.CreateVersion7()))).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await Built.Get(jobs, PathOf(jobs.Project, 99))).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await Built.Get(jobs, PathOf(jobs.Project, First))).Status);

        // Without a token, 401 with an empty body.
        var anonymous = await jobs.Routes.App.Get(PathOf(jobs.Project), null);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);
        Assert.Empty(await Harness.Body(anonymous));
    }
}