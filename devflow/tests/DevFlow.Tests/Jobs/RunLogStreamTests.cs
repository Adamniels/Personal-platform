using System.Net;
using System.Text.Json.Nodes;

using DevFlow.Protocol;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A run's log as it happens: the lines stored, then each new one as the worker's log route stores
/// it, from <see cref="DevFlow.Jobs.RunLogFeed"/>, each event's id its seq, so a page that
/// reconnects takes up after the last line it had; and an end event once the run ends. Against
/// real Postgres and the fake code host. See "Jobs" in devflow/README.md.
/// </summary>
public sealed class RunLogStreamTests
{
    private static string Events(Jobs jobs, Guid run) => $"/devflow/api/projects/{jobs.Project}/runs/{run}/log/events";

    private static async Task Post(Jobs jobs, Guid run, params JsonObject[] lines) =>
        Assert.Equal(StatusCodes.Status200OK, (await jobs.Post(WorkerApi.LogPath(run), RunLogTests.Post(Jobs.FirstWorker, lines))).Response.StatusCode);

    /// <summary>Opens the run's log stream and reads its start event, which comes at once.</summary>
    private static async Task<EventStream> Opened(Jobs jobs, Guid run, string? lastEventId = null)
    {
        var stream = await EventStream.Open(jobs.Routes.App, Events(jobs, run), Contract.Token, lastEventId);
        Assert.Equal(HttpStatusCode.OK, stream.Status);
        Assert.Equal("start", (await stream.Expect()).EventType);
        return stream;
    }

    private static (long Seq, string Kind, string Text) Line(System.Net.ServerSentEvents.SseItem<string> item)
    {
        Assert.Equal("line", item.EventType);
        var line = JsonNode.Parse(item.Data)!;
        Assert.Equal(item.EventId, ((long)line["seq"]!).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return ((long)line["seq"]!, (string)line["kind"]!, (string)line["text"]!);
    }

    [Fact]
    public async Task LinesArriveLive()
    {
        await using var jobs = await Jobs.Start();
        var job = await jobs.Job();
        var id = (Guid)job["job_id"]!;
        await Post(jobs, id, RunLogTests.Line(1, "Cloning", "system"), RunLogTests.Line(2, "Reading the card."));

        // The start, then the lines stored, at once, each with its seq as the event's id.
        await using var stream = await Opened(jobs, id);
        Assert.Equal("text/event-stream", stream.ContentType);
        Assert.Equal((1L, "system", "Cloning"), Line(await stream.Expect()));
        Assert.Equal((2L, "text", "Reading the card."), Line(await stream.Expect()));
        Assert.Null(await stream.Next(EventStream.Quiet));

        // Then each new line as the log route stores it, a retried one never twice.
        await Post(jobs, id, RunLogTests.Line(3, "Read README.md", "tool"));
        Assert.Equal((3L, "tool", "Read README.md"), Line(await stream.Expect()));
        await Post(jobs, id, RunLogTests.Line(3, "Read README.md", "tool"), RunLogTests.Line(4, "1 failed, as expected", "check"));
        Assert.Equal((4L, "check", "1 failed, as expected"), Line(await stream.Expect()));

        // A page that reconnects with the last line it had takes up after it.
        await using (var again = await Opened(jobs, id, lastEventId: "2"))
        {
            Assert.Equal(3L, Line(await again.Expect()).Seq);
            Assert.Equal(4L, Line(await again.Expect()).Seq);
            Assert.Null(await again.Next(EventStream.Quiet));
        }

        // The run ends: an end event, and the stream closes.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath(id), Jobs.Result(Jobs.FirstWorker, (string)job["branch"]!))).Response.StatusCode);
        var end = await stream.Expect();
        Assert.Equal("end", end.EventType);
        Assert.True(await stream.Ends());

        // A run that has ended sends what it stored, then its end, at once.
        await using var finished = await Opened(jobs, id, lastEventId: "3");
        Assert.Equal(4L, Line(await finished.Expect()).Seq);
        Assert.Equal("end", (await finished.Expect()).EventType);
        Assert.True(await finished.Ends());
    }

    [Fact]
    public async Task AFreshPageGetsTheLastLines()
    {
        // A page shows the last 200 lines, so a stream opened without Last-Event-ID starts with the
        // last 200 the run stored, not its whole log.
        await using var jobs = await Jobs.Start();
        var id = (Guid)(await jobs.Job())["job_id"]!;
        await Post(jobs, id, [.. Enumerable.Range(1, 250).Select(seq => RunLogTests.Line(seq, $"line {seq}"))]);

        await using var stream = await Opened(jobs, id);
        Assert.Equal(51L, Line(await stream.Expect()).Seq);
        for (var seq = 52; seq <= 250; seq++)
        {
            Assert.Equal(seq, Line(await stream.Expect()).Seq);
        }

        Assert.Null(await stream.Next(EventStream.Quiet));
    }

    [Fact]
    public async Task AnotherUsersRunIsNone()
    {
        await using var jobs = await Jobs.Start();
        var id = (Guid)(await jobs.Job())["job_id"]!;

        // A run never made, a project that is not the caller's, and no token.
        await using (var none = await EventStream.Open(jobs.Routes.App, Events(jobs, Guid.CreateVersion7()), Contract.Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, none.Status);
        }

        await using (var elsewhere = await EventStream.Open(jobs.Routes.App, $"/devflow/api/projects/{Guid.CreateVersion7()}/runs/{id}/log/events", Contract.Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, elsewhere.Status);
        }

        await using (var anonymous = await EventStream.Open(jobs.Routes.App, Events(jobs, id)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        }
    }

    [Fact]
    public async Task TheStreamEndsWithItsToken()
    {
        // A stream is let in by the token it opened with, and ends when that token does, without an
        // end event, since the run goes on: the page opens it again with the session as it is then.
        await using var jobs = await Jobs.Start();
        var id = (Guid)(await jobs.Job())["job_id"]!;
        await using var stream = await Opened(jobs, id);

        // Waiting on the feed, as a live stream does, and still sending what arrives.
        Assert.Null(await stream.Next(EventStream.Quiet));
        await Post(jobs, id, RunLogTests.Line(1, "Reading the card."));
        Assert.Equal(1L, Line(await stream.Expect()).Seq);
        Assert.Null(await stream.Next(EventStream.Quiet));

        jobs.Routes.Time.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(Contract.ExpiresAt));
        Assert.True(await stream.Ends());
    }

    [Fact]
    public async Task ALostRunEnds()
    {
        // A run its worker went quiet on is ended lost on the project's read, and its stream ends.
        await using var jobs = await Jobs.Start();
        var id = (Guid)(await jobs.Job())["job_id"]!;
        await using var stream = await Opened(jobs, id);
        Assert.Null(await stream.Next(EventStream.Quiet));

        jobs.Routes.Time.Advance(DevFlow.Jobs.RunStore.Silence + TimeSpan.FromSeconds(1));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        Assert.Equal("end", (await stream.Expect()).EventType);
        Assert.True(await stream.Ends());
    }
}