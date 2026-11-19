using System.Globalization;

using DevFlow.Stops;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Tests.Build;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Worker.Tests.Stops;

/// <summary>
/// A usage limit is a failure outside the card, and its project waits until the limit resets when
/// Claude's answer says when: the real Claude Code agent behind a starter answering as Claude Code
/// recorded, the real worker, and Dev flow against a real Postgres. See "Stops" in
/// devflow/README.md.
/// </summary>
public sealed class StopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("usage-limit-resets-today.json", "2027-01-15T15:00:00Z")]
    [InlineData("usage-limit-resets-date.json", "2027-01-20T14:30:00Z")]
    [InlineData("usage-limit.json", null)]
    public async Task AUsageLimitWaitsUntilItsReset(string answer, string? resets)
    {
        await using var bench = await Bench.Start();
        // The worker's clock is Dev flow's, as on one machine, on the morning the expected times are
        // for.
        var now = bench.Jobs.Routes.Time.GetUtcNow();
        Assert.Equal(new DateOnly(2027, 1, 15), DateOnly.FromDateTime(now.UtcDateTime));
        Assert.True(now.Hour < 15, now.ToString("O", CultureInfo.InvariantCulture));
        bench.Clock.SetUtcNow(now);
        var claude = new Answering(Recorded("auth-oauth-token.json", 0), Recorded(answer, 1));
        var agent = new ClaudeCodeAgent(claude, ClaudeCodeOptions.Default, new WorkerLog(TextWriter.Null, bench.Clock), bench.Clock);

        await bench.RunJob(agent);

        // Outside the card: nothing counted against it, and it is queued again.
        var run = await bench.Run();
        Assert.Equal("failed_outside_card", run.Result);
        Assert.Equal("queued", (await bench.Jobs.Card(1)).State);

        // The project waits until the limit resets, or, when the answer gives no time, 5 minutes.
        var wait = await ProjectWaits.Of(bench.Jobs.Routes.Door, DevFlow.Tests.Jobs.Jobs.User, bench.Jobs.Project, Ct);
        Assert.NotNull(wait);
        var until = resets is null ? wait.Since + ProjectWaits.First : DateTimeOffset.Parse(resets, CultureInfo.InvariantCulture);
        Assert.Equal(until, wait.WaitUntil);

        // No job before then; one once it has passed, the project read on its minute.
        bench.Jobs.Routes.Time.SetUtcNow(until.AddMinutes(-1));
        await bench.Jobs.Routes.Read(bench.Jobs.Project, DevFlow.Tests.Jobs.Jobs.User);
        Assert.Equal(StatusCodes.Status204NoContent, (await bench.Jobs.Next()).Response.StatusCode);
        bench.Jobs.Routes.Time.SetUtcNow(until);
        await bench.Jobs.Routes.Read(bench.Jobs.Project, DevFlow.Tests.Jobs.Jobs.User);
        Assert.Equal(StatusCodes.Status200OK, (await bench.Jobs.Next()).Response.StatusCode);
    }

    [Theory]
    [InlineData("You've hit your 5-hour limit · resets 3pm (UTC)", "2027-01-15T15:00:00Z")]
    [InlineData("You've hit your 5-hour limit · resets 7am (UTC)", "2027-01-16T07:00:00Z")]
    [InlineData("You've hit your 5-hour limit · resets 8am (UTC)", "2027-01-15T08:00:00Z")]
    [InlineData("You've hit your weekly limit · resets 3:30pm (Europe/Stockholm)", "2027-01-15T14:30:00Z")]
    [InlineData("You've hit your weekly limit · resets Jan 20, 3pm (UTC)", "2027-01-20T15:00:00Z")]
    [InlineData("You've hit your weekly limit · resets Jan 3, 2028, 12am (UTC)", "2028-01-03T00:00:00Z")]
    [InlineData("You've hit your weekly limit · resets Jan 2, 9am (UTC)", "2028-01-02T09:00:00Z")]
    [InlineData("You've hit your 5-hour limit · resets 3pm", null)]
    [InlineData("You've hit your 5-hour limit · resets 3pm (Nowhere/Atlantis)", null)]
    [InlineData("You've hit your weekly limit · resets Feb 30, 3pm (UTC)", null)]
    [InlineData("You've hit your 5-hour limit · resets 13pm (UTC)", null)]
    [InlineData("Claude usage limit reached. Your limit will reset at 3pm.", null)]
    public void TheResetIsReadAsClaudeCodeWritesIt(string message, string? resets)
    {
        var now = new DateTimeOffset(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var expected = resets is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(resets, CultureInfo.InvariantCulture);
        Assert.Equal(expected, UsageLimit.ResetsAt(message, now));
    }

    private static ProcessOutcome Recorded(string file, int exitCode) =>
        new(exitCode, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Claude", file)), "", TimedOut: false);

    /// <summary>Answers Claude Code's login check and then its run, as recorded.</summary>
    private sealed class Answering(ProcessOutcome login, ProcessOutcome run) : IProcessStarter
    {
        private int _calls;

        public Task<ProcessOutcome> Run(ProcessSpec spec, TimeSpan timeLimit, CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref _calls) == 1 ? login : run);
    }
}