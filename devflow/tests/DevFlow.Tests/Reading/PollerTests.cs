using DevFlow.Reading;
using DevFlow.Tests.Runner;

using Microsoft.Extensions.Logging.Abstractions;

namespace DevFlow.Tests.Reading;

/// <summary>
/// The loop that keeps the snapshot current, <see cref="Poller"/>, and the one way to ask for a
/// project to be read, <see cref="ProjectRefresher"/>, against the fake and a real Postgres, on a
/// clock the test moves. See "Reading and the snapshot" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class PollerTests
{
    private static CancellationToken Ct => Reading.Ct;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static readonly World Waiting = new();

    private static ProjectRefresher Refresher(Reading reading) => new(reading.Reader, NullLogger<ProjectRefresher>.Instance);

    private static Poller Loop(Reading reading, ProjectRefresher refresher) =>
        new(reading.Door, refresher, reading.Time, Interval, NullLogger<Poller>.Instance);

    [Fact]
    public async Task EveryTargetIsReadAsItsOwnUser()
    {
        await using var reading = await Reading.Start();
        var alices = await reading.Project(Reading.Alice);
        var bobs = await reading.Project(Reading.Bob);
        // The same repository in each one's world: read as the other, it would be another world.
        reading.Stage(Reading.Alice, [Reading.From(Waiting)], [Reading.SliceOf(Waiting)]);
        reading.Stage(Reading.Bob, [Reading.From(Waiting with { Pull = Waiting.Pull with { Title = "Bob's" } })], [Reading.SliceOf(Waiting)]);
        await using var refresher = Refresher(reading);
        using var loop = Loop(reading, refresher);

        await loop.StartAsync(Ct);
        await Reading.Until(async () => await reading.ReadOf(alices) is not null && await reading.ReadOf(bobs) is not null, "both projects being read");
        await loop.StopAsync(Ct);

        // Each read at the code host as its own user, and stored through the door as them.
        Assert.Equal(1, reading.Host.Lists(Reading.Alice));
        Assert.Equal(1, reading.Host.Lists(Reading.Bob));
        Assert.Equal((Reading.Alice, Waiting.Pull.Title), Assert.Single(await reading.Items(alices)) is var a ? (a.UserId, a.Title) : default);
        Assert.Equal((Reading.Bob, "Bob's"), Assert.Single(await reading.Items(bobs)) is var b ? (b.UserId, b.Title) : default);
        Assert.Null((await reading.ReadOf(alices))!.LastError);
        Assert.Equal(Reading.Bob, (await reading.ReadOf(bobs))!.UserId);
    }

    [Fact]
    public async Task OneProjectFailingDoesNotStopTheOthers()
    {
        await using var reading = await Reading.Start();
        var broken = await reading.Project(Reading.Alice, repository: 9002, name: "broken");
        var fine = await reading.Project(Reading.Alice);
        reading.Stage(Reading.Alice, [Reading.From(Waiting)], [Reading.SliceOf(Waiting)], 9002);
        reading.Host.Throws[9002] = true;
        await using var refresher = Refresher(reading);
        using var loop = Loop(reading, refresher);

        await loop.StartAsync(Ct);
        await Reading.Until(async () => await reading.ReadOf(broken) is not null && await reading.ReadOf(fine) is not null, "both projects being tried");
        await loop.StopAsync(Ct);

        Assert.Equal(ProjectReader.ReadFailed, (await reading.ReadOf(broken))!.LastError);
        Assert.Null((await reading.ReadOf(fine))!.LastError);
        Assert.Single(await reading.Items(fine));
    }

    [Fact]
    public async Task ItLooksAgainAfterTheInterval()
    {
        await using var reading = await Reading.Start();
        var project = await reading.Project(Reading.Alice);
        reading.Stage(Reading.Alice, [Reading.From(Waiting)], [Reading.SliceOf(Waiting)]);
        await using var refresher = Refresher(reading);
        using var loop = Loop(reading, refresher);

        // It reads once as it starts.
        await loop.StartAsync(Ct);
        await Reading.Until(async () => await reading.ReadOf(project) is not null, "the first read");
        var first = (await reading.ReadOf(project))!.LastSuccessAt;

        // Not again before the interval is up.
        reading.Time.Advance(Interval - TimeSpan.FromSeconds(1));
        await Task.Delay(300, Ct);
        Assert.Equal(1, reading.Host.Lists());

        // And again once it is, at the clock's time.
        reading.Time.Advance(TimeSpan.FromSeconds(1));
        await Reading.Until(() => Task.FromResult(reading.Host.Lists() == 2), "the second read");
        await Reading.Until(async () => (await reading.ReadOf(project))!.LastSuccessAt != first, "the second read being stored");
        await loop.StopAsync(Ct);
        Assert.Equal(reading.Time.GetUtcNow().UtcDateTime, (await reading.ReadOf(project))!.LastSuccessAt);
    }

    [Fact]
    public async Task AProjectIsNeverReadTwiceAtOnce()
    {
        await using var reading = await Reading.Start();
        var project = await reading.Project(Reading.Alice);
        reading.Stage(Reading.Alice, [Reading.From(Waiting)], [Reading.SliceOf(Waiting)]);
        await using var refresher = Refresher(reading);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reading.Host.Hold = hold;

        // A read under way, held at the code host.
        var first = refresher.Request(project, Reading.Alice);
        await reading.Host.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Two requests to look again meanwhile wait for it, and start nothing.
        reading.Host.ListStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = refresher.Request(project, Reading.Alice);
        var third = refresher.Request(project, Reading.Alice);
        await Task.Delay(300, Ct);
        Assert.Equal(1, reading.Host.Lists());
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        // Once it is done, the first is answered and one more read starts for the two, since they
        // came after it began; each is answered when that one is done.
        reading.Host.Hold = null;
        hold.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.WhenAll(second, third).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(2, reading.Host.Lists());
        Assert.Equal(1, reading.Host.MostListsAtOnce);
        Assert.Single(await reading.Items(project));

        // With nothing under way, a request reads at once.
        await refresher.Request(project, Reading.Alice).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(3, reading.Host.Lists());
    }
}