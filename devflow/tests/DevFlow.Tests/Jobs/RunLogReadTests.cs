using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Features;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A finished run's log, read afterwards from the card's page and the feature's: its lines in
/// their order, 1,000 at a time, through the door as the caller; and whether each run in a story
/// has its log, or had one that was kept its 90 days. Against real Postgres and the fake code host.
/// See "Jobs", "A card's page" and "A feature's page" in devflow/README.md.
/// </summary>
public sealed class RunLogReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string LogPath(Guid project, Guid run, long? after = null) =>
        $"/devflow/api/projects/{project}/runs/{run}/log" + (after is { } seq ? $"?after={seq}" : "");

    private static async Task<(int Status, JsonNode? Body)> Read(Jobs jobs, Guid project, Guid run, long? after = null, bool signedIn = true)
    {
        var response = await jobs.Routes.App.Send("GET", LogPath(project, run, after), signedIn ? Contract.Token : null);
        var body = await Harness.Body(response);
        return (response.Response.StatusCode, body.Length == 0 ? null : JsonNode.Parse(body));
    }

    /// <summary>Lines <paramref name="from"/> to <paramref name="to"/> added to the run's log, as its
    /// worker would have posted them, past row level security.</summary>
    private static async Task AddLines(Jobs jobs, Guid run, int from, int to)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO run_log_lines (project_id, user_id, run_id, seq, at, kind, text)
            SELECT r.project_id, r.user_id, r.id, s, r.started_at, 'text', 'line ' || s
            FROM runs r, generate_series(@from, @to) s WHERE r.id = @run
            """,
            new { run, from, to },
            cancellationToken: Ct));
    }

    /// <summary>The run's lines deleted past row level security, as a run from before logs were
    /// kept has none, and with <paramref name="pruned"/> marked as the pruner marks it.</summary>
    private static async Task Forget(Jobs jobs, Guid run, bool pruned)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM run_log_lines WHERE run_id = @run;
            UPDATE runs SET log_pruned_at = CASE WHEN @pruned THEN now() END WHERE id = @run;
            """,
            new { run, pruned },
            cancellationToken: Ct));
    }

    [Fact]
    public async Task AFinishedRunsLogIsRead()
    {
        await using var jobs = await Jobs.Start();
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        var run = (Guid)built["job_id"]!;

        // The fake's own first lines, then the rest of 2,500 as a long run posts them.
        await AddLines(jobs, run, 4, 2_500);

        // The first 1,000, in seq order, each with its seq, at, kind and text, and more to come.
        var (status, first) = await Read(jobs, jobs.Project, run);
        Assert.Equal(StatusCodes.Status200OK, status);
        var lines = first!["lines"]!.AsArray();
        Assert.Equal(Enumerable.Range(1, 1_000).Select(seq => (long)seq), lines.Select(line => (long)line!["seq"]!));
        Assert.Equal("system", (string)lines[0]!["kind"]!);
        Assert.StartsWith("Cloning Adamniels/Personal-platform", (string)lines[0]!["text"]!, StringComparison.Ordinal);
        Assert.NotNull(lines[0]!["at"]);
        Assert.Equal("line 1000", (string)lines[999]!["text"]!);
        Assert.True((bool)first["more"]!);

        // After the last one read, the next 1,000, then the last 500, with no more.
        var (_, second) = await Read(jobs, jobs.Project, run, after: 1_000);
        Assert.Equal(Enumerable.Range(1_001, 1_000).Select(seq => (long)seq), second!["lines"]!.AsArray().Select(line => (long)line!["seq"]!));
        Assert.True((bool)second["more"]!);
        var (_, last) = await Read(jobs, jobs.Project, run, after: 2_000);
        Assert.Equal(Enumerable.Range(2_001, 500).Select(seq => (long)seq), last!["lines"]!.AsArray().Select(line => (long)line!["seq"]!));
        Assert.False((bool)last["more"]!);
        var (_, none) = await Read(jobs, jobs.Project, run, after: 2_500);
        Assert.Empty(none!["lines"]!.AsArray());
        Assert.False((bool)none["more"]!);

        // Exactly 1,000 left after a page is no more.
        var (_, exact) = await Read(jobs, jobs.Project, run, after: 1_500);
        Assert.Equal(1_000, exact!["lines"]!.AsArray().Count);
        Assert.False((bool)exact["more"]!);

        // A run never made, a project that is not the caller's, and no token.
        Assert.Equal(StatusCodes.Status404NotFound, (await Read(jobs, jobs.Project, Guid.CreateVersion7())).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await Read(jobs, Guid.CreateVersion7(), run)).Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await Read(jobs, jobs.Project, run, signedIn: false)).Status);

        // Another user's run, the same run once it is Bob's, in a project of his: not found, by
        // either project, and none of its lines given.
        var bob = Guid.CreateVersion7();
        var bobs = await Reading.Reading.InsertProject(jobs.Routes.Door, bob, name: "bobs-repository");
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO workers (project_id, id, user_id, name, recipe_version, base_image, first_seen_at, last_seen_at)
                VALUES (@bobs, @worker, @bob, 'bobs-worker', '1', 'base', now(), now());
                UPDATE runs SET project_id = @bobs, user_id = @bob, worker_id = @worker WHERE id = @run;
                """,
                new { bobs, bob, worker = Guid.CreateVersion7(), run },
                cancellationToken: Ct));
        }

        var (notMine, notMineBody) = await Read(jobs, jobs.Project, run);
        Assert.Equal(StatusCodes.Status404NotFound, notMine);
        Assert.Equal("No such run", (string)notMineBody!["title"]!);
        Assert.Equal(StatusCodes.Status404NotFound, (await Read(jobs, bobs, run)).Status);
    }

    [Fact]
    public async Task TheStoryTellsWhetherALogIsKept()
    {
        // A card's three runs: one from before logs were kept, one whose log was kept its 90 days,
        // and one with its log.
        await using (var jobs = await Jobs.Start([Jobs.Card(4)]))
        {
            var before = (Guid)(await Built.Run(jobs, Built.PreparedFailure(inCard: false, "Claude's API did not answer")))["job_id"]!;
            jobs.Routes.Time.Advance(DevFlow.Stops.ProjectWaits.First);
            await jobs.Routes.Read(jobs.Project, Jobs.User);
            var pruned = (Guid)(await Built.Run(jobs, Built.PreparedFailure(inCard: false, "Claude's API did not answer")))["job_id"]!;
            jobs.Routes.Time.Advance(DevFlow.Stops.ProjectWaits.Wait(2));
            await jobs.Routes.Read(jobs.Project, Jobs.User);
            var kept = (Guid)(await Built.Run(jobs, Built.Prepared(Built.Report())))["job_id"]!;
            await Forget(jobs, before, pruned: false);
            await Forget(jobs, pruned, pruned: true);

            var (status, card) = await Built.Get(jobs, $"/devflow/api/projects/{jobs.Project}/cards/4");

            Assert.Equal(StatusCodes.Status200OK, status);
            var runs = card!["runs"]!.AsArray().ToDictionary(run => Guid.Parse((string)run!["id"]!));
            Assert.Equal((true, false), Log(runs[kept]!));
            Assert.Equal((false, true), Log(runs[pruned]!));
            Assert.Equal((false, false), Log(runs[before]!));
        }

        // A feature's own run, its whole review, with its log, and then kept its 90 days.
        await using (var jobs = await Ending.Feature())
        {
            await Reviewing.Published(jobs);
            var review = (Guid)(await Reviewing.Review(jobs))["job_id"]!;

            var (_, feature) = await Built.Get(jobs, FeaturePage.PathOf(jobs.Project));
            var own = Assert.Single(feature!["runs"]!.AsArray(), run => Guid.Parse((string)run!["id"]!) == review)!;
            Assert.Equal((true, false), Log(own));

            await Forget(jobs, review, pruned: true);
            (_, feature) = await Built.Get(jobs, FeaturePage.PathOf(jobs.Project));
            own = Assert.Single(feature!["runs"]!.AsArray(), run => Guid.Parse((string)run!["id"]!) == review)!;
            Assert.Equal((false, true), Log(own));
        }
    }

    private static (bool HasLog, bool LogPruned) Log(JsonNode run) => ((bool)run["has_log"]!, (bool)run["log_pruned"]!);
}