using DevFlow.Jobs;
using DevFlow.Protocol;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// The feed a live page reads a run's lines from: each line a post stored reaches the run's
/// subscriptions once it is stored, a line skipped never does, and a subscription that falls too
/// far behind is ended rather than handed lines with a hole in them. See "Jobs" in
/// devflow/README.md.
/// </summary>
public sealed class RunLogFeedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StoredLinesReachTheRunsReaders()
    {
        await using var jobs = await Jobs.Start();
        var id = (Guid)(await jobs.Job())["job_id"]!;
        var feed = jobs.Routes.App.Services.GetRequiredService<RunLogFeed>();
        using var reading = feed.Subscribe(id);
        using var other = feed.Subscribe(Guid.NewGuid());

        Assert.Equal(StatusCodes.Status200OK, (await jobs.Post(WorkerApi.LogPath(id), RunLogTests.Post(Jobs.FirstWorker, [RunLogTests.Line(1, "first"), RunLogTests.Line(2, "second")]))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await jobs.Post(WorkerApi.LogPath(id), RunLogTests.Post(Jobs.FirstWorker, [RunLogTests.Line(2, "second, again"), RunLogTests.Line(3, "third")]))).Response.StatusCode);

        // Each line once, as stored: the retried line 2 was skipped, so it is not handed on again.
        var read = new List<RunLogLine>();
        while (reading.Lines.TryRead(out var line))
        {
            read.Add(line);
        }

        Assert.Equal([(1L, "first"), (2L, "second"), (3L, "third")], read.Select(line => (line.Seq, line.Text)));
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 8, 1, 1, TimeSpan.Zero), read[0].At);
        Assert.False(other.Lines.TryRead(out _));
    }

    [Fact]
    public async Task AReaderTooFarBehindIsEnded()
    {
        var feed = new RunLogFeed();
        var run = Guid.NewGuid();
        using var behind = feed.Subscribe(run);
        var lines = Enumerable.Range(1, RunLogFeed.Buffered + 1).Select(seq => new RunLogLine(seq, DateTimeOffset.UnixEpoch, LogKinds.Text, "line")).ToList();

        feed.Publish(run, lines);

        // What fitted is read, then the subscription ends, and the run has no reader left; its
        // reader takes up again from the table after the last seq it had.
        var read = 0;
        await foreach (var _ in behind.Lines.ReadAllAsync(Ct))
        {
            read++;
        }

        Assert.Equal(RunLogFeed.Buffered, read);
        Assert.Equal(0, feed.Readers(run));
    }

    [Fact]
    public void AReaderDisposedIsGone()
    {
        var feed = new RunLogFeed();
        var run = Guid.NewGuid();
        var reading = feed.Subscribe(run);
        Assert.Equal(1, feed.Readers(run));

        reading.Dispose();
        reading.Dispose();

        Assert.Equal(0, feed.Readers(run));
        Assert.True(reading.Lines.Completion.IsCompleted);
        feed.Publish(run, [new RunLogLine(1, DateTimeOffset.UnixEpoch, LogKinds.Text, "nobody reads this")]);
    }
}