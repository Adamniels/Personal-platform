using System.Net;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Protocol;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// "Running now": every run under way of the caller's projects, as a panel's facts, and the stream
/// that sends the list again whenever it changed, checked every 2 seconds. Against real Postgres
/// and the fake code host. See "The pages" and "Jobs" in devflow/README.md.
/// </summary>
public sealed class RunningTests
{
    private const string Path = "/devflow/api/running";
    private const string Events = "/devflow/api/running/events";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Usage(long input, long creation, long read, long output) => new()
    {
        ["input_tokens"] = input,
        ["cache_creation_input_tokens"] = creation,
        ["cache_read_input_tokens"] = read,
        ["output_tokens"] = output,
    };

    private static async Task<JsonObject> List(Jobs jobs)
    {
        var response = await jobs.Routes.App.Get(Path, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(response))!.AsObject();
    }

    [Fact]
    public async Task EveryRunUnderWayIsAPanel()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1, "Sort the list"), Jobs.Card(2, "Share a plan")]);
        await jobs.SayHello(Jobs.SecondWorker, name: "platform-2");

        // Nothing under way is an empty list, and no worker busy.
        var none = await List(jobs);
        Assert.Empty(none["runs"]!.AsArray());
        Assert.Equal(0, (int)none["busy_workers"]!);

        // Two builds, the second three minutes after the first, each on a worker of its own.
        var first = await jobs.Job();
        var firstId = (Guid)first["job_id"]!;
        var firstStarted = jobs.Routes.Time.GetUtcNow();
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(3));
        var second = await jobs.Job(Jobs.SecondWorker);
        var secondId = (Guid)second["job_id"]!;

        // The first has used tokens so far and is checking; the second is marked to stop.
        Assert.Equal(StatusCodes.Status200OK, (await jobs.Post(WorkerApi.LogPath(firstId), RunLogTests.Post(Jobs.FirstWorker, [], Usage(1_000, 20_000, 2_100_000, 99_000)))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ProgressPath(firstId), new JsonObject { ["worker_id"] = Jobs.FirstWorker, ["state"] = "checking" })).Response.StatusCode);
        Assert.Equal(StatusCodes.Status202Accepted, (await StopRunTests.StopRun(jobs, secondId)).Response.StatusCode);

        // A run of another user's, in a project of theirs, is never listed.
        var bob = Guid.CreateVersion7();
        var bobs = await Reading.Reading.InsertProject(jobs.Routes.Door, bob, repository: 4242, name: "bobs-repository");
        await using (var admin = await jobs.Routes.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO workers (project_id, id, user_id, name, recipe_version, base_image, first_seen_at, last_seen_at)
                VALUES (@bobs, @worker, @bob, 'bobs-worker', '1', 'base', now(), now());
                INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, started_at, run_minutes)
                VALUES (@run, @bobs, @bob, 7, @worker, 'build', 'devflow/7-bobs', 'main', now() - interval '1 hour', 90);
                """,
                new { bobs, bob, worker = Guid.CreateVersion7(), run = Guid.CreateVersion7() },
                cancellationToken: Ct));
        }

        var listed = await List(jobs);
        var runs = listed["runs"]!.AsArray();
        Assert.Equal(2, (int)listed["busy_workers"]!);
        Assert.Equal([firstId, secondId], runs.Select(run => (Guid)run!["run_id"]!));

        var building = runs[0]!;
        Assert.Equal(jobs.Project, (Guid)building["project_id"]!);
        Assert.Equal("Personal-platform", (string)building["project_name"]!);
        Assert.Equal(Jobs.FirstWorker, (Guid)building["worker_id"]!);
        Assert.Equal("fodr-worker", (string)building["worker_name"]!);
        Assert.Equal("build", (string)building["kind"]!);
        Assert.Equal("checking", (string)building["state"]!);
        Assert.False((bool)building["stopping"]!);
        Assert.Equal((int)first["card"]!["number"]!, (int)building["card_number"]!);
        Assert.Null(building["feature_number"]);
        Assert.Equal((string)first["card"]!["title"]!, (string)building["title"]!);
        Assert.Null(building["fix_round"]);
        Assert.Equal("opus", (string)building["model"]!);
        Assert.Equal(firstStarted, (DateTimeOffset)building["started_at"]!);
        // Tokens by the one definition, input, cache creation and output; cache reads beside them.
        Assert.Equal(120_000, (long)building["tokens"]!);
        Assert.Equal(2_100_000, (long)building["cache_reads"]!);

        var stopping = runs[1]!;
        Assert.Equal("platform-2", (string)stopping["worker_name"]!);
        Assert.Equal("running", (string)stopping["state"]!);
        Assert.True((bool)stopping["stopping"]!);
        Assert.Equal(0, (long)stopping["tokens"]!);
        Assert.Equal(0, (long)stopping["cache_reads"]!);

        // A run that has ended is no panel.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath(secondId), StopRunTests.Stopped(Jobs.SecondWorker, (string)second["branch"]!))).Response.StatusCode);
        var left = await List(jobs);
        Assert.Equal([firstId], left["runs"]!.AsArray().Select(run => (Guid)run!["run_id"]!));
        Assert.Equal(1, (int)left["busy_workers"]!);

        // Without a token, 401 with an empty body.
        var refused = await jobs.Routes.App.Get(Path);
        Assert.Equal(StatusCodes.Status401Unauthorized, refused.Response.StatusCode);
        Assert.Equal("", await Harness.Body(refused));
    }

    [Fact]
    public async Task TheStreamSendsChanges()
    {
        await using var jobs = await Jobs.Start([Jobs.Card(1), Jobs.Card(2)]);
        await using var stream = await EventStream.Open(jobs.Routes.App, Events, Contract.Token);
        Assert.Equal(HttpStatusCode.OK, stream.Status);
        Assert.Equal("text/event-stream", stream.ContentType);

        // The list at once, nothing under way.
        var first = await stream.Expect();
        Assert.Equal("running", first.EventType);
        Assert.Empty(JsonNode.Parse(first.Data)!["runs"]!.AsArray());

        // Nothing changes, so nothing is sent, however often it is checked.
        for (var i = 0; i < 3; i++)
        {
            jobs.Routes.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.Null(await stream.Next(EventStream.Quiet));
        }

        // A run starts.
        var job = await jobs.Job();
        var id = (Guid)job["job_id"]!;
        var started = await stream.AfterChecks(jobs.Routes.Time);
        Assert.NotNull(started);
        Assert.Equal("running", started.Value.EventType);
        Assert.Equal([id], JsonNode.Parse(started.Value.Data)!["runs"]!.AsArray().Select(run => (Guid)run!["run_id"]!));

        // A heartbeat with nothing new changes nothing.
        Assert.Equal(StatusCodes.Status200OK, (await jobs.Post(WorkerApi.LogPath(id), RunLogTests.Post(Jobs.FirstWorker, []))).Response.StatusCode);
        Assert.Null(await stream.AfterChecks(jobs.Routes.Time, most: 3));

        // Its tokens change.
        Assert.Equal(StatusCodes.Status200OK, (await jobs.Post(WorkerApi.LogPath(id), RunLogTests.Post(Jobs.FirstWorker, [], Usage(10, 0, 5, 20)))).Response.StatusCode);
        var used = await stream.AfterChecks(jobs.Routes.Time);
        Assert.NotNull(used);
        Assert.Equal(30, (long)JsonNode.Parse(used.Value.Data)!["runs"]![0]!["tokens"]!);

        // It ends.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, (string)job["branch"]!))).Response.StatusCode);
        var ended = await stream.AfterChecks(jobs.Routes.Time);
        Assert.NotNull(ended);
        Assert.Empty(JsonNode.Parse(ended.Value.Data)!["runs"]!.AsArray());
    }

    [Fact]
    public async Task TheStreamNeedsAToken()
    {
        await using var jobs = await Jobs.Start();
        await using var stream = await EventStream.Open(jobs.Routes.App, Events);
        Assert.Equal(HttpStatusCode.Unauthorized, stream.Status);
    }

    [Fact]
    public async Task TheStreamEndsWithItsToken()
    {
        // A stream is let in by the token it opened with, and ends when that token does, so the page
        // opens it again with the session as it is then.
        await using var jobs = await Jobs.Start();
        await using var stream = await EventStream.Open(jobs.Routes.App, Events, Contract.Token);
        await stream.Expect();
        jobs.Routes.Time.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(Contract.ExpiresAt));
        Assert.True(await stream.Ends());
    }
}