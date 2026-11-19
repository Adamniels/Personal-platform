using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Fixing;
using DevFlow.Jobs;
using DevFlow.Tests.Fixing;
using DevFlow.Tests.PullRequests;

namespace DevFlow.Tests.Stops;

/// <summary>
/// A card that cannot be made to work stops, and only it; one failure in the card alone runs it
/// again, and a failure outside it never counts against it. Against the fake, the fake worker and a
/// real Postgres. See "Stops" in devflow/README.md.
/// </summary>
public sealed class StopTests
{
    private const string TimeLimit = "it ran past the time limit of 90 minutes and was ended";

    [Fact]
    public async Task EachReasonStopsTheCard()
    {
        // The time limit, at once.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Stop.Run(jobs, Stop.InCard(TimeLimit, timedOut: true));
            var card = await Stop.Card(jobs, 4);
            Assert.Equal("stopped", card.State);
            Assert.Equal($"The run failed: {TimeLimit}", card.Reason);
            Assert.NotNull(card.StoppedAt);
        }

        // The fix rounds used up.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Fixed.BuiltAndReviewed(jobs);
            for (var round = 1; round <= 3; round++)
            {
                await Fixed.Fix(jobs);
                await Built.Review(jobs, Built.Verdict("changes"));
            }

            var card = await Stop.Card(jobs, 4);
            Assert.Equal("stopped", card.State);
            Assert.Equal(FixRounds.UsedUp, card.Reason);
            Assert.NotNull(card.StoppedAt);
        }

        // A report that says stopped, at once.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Stop.Run(jobs, Built.Prepared(Jobs.Jobs.GoodReport("stopped", "The card asks for two pages that contradict each other")));
            var card = await Stop.Card(jobs, 4);
            Assert.Equal("stopped", card.State);
            Assert.Equal("The agent stopped: The card asks for two pages that contradict each other", card.Reason);
            Assert.NotNull(card.StoppedAt);
        }

        // Two runs in a row failing in the card, with no new commit between them.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
            await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
            var card = await Stop.Card(jobs, 4);
            Assert.Equal("stopped", card.State);
            Assert.Equal(RunStore.Twice("The run failed: it reached the most turns, 200 turns"), card.Reason);
            Assert.NotNull(card.StoppedAt);
        }

        // The pull request closed without merging.
        await using (var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]))
        {
            await Fixed.BuiltAndReviewed(jobs, Built.Verdict("approve"));
            Built.OnGitHub(jobs, (await Built.Card(jobs, 4)).PullRequestNumber!.Value, PullRequestState.Closed);
            await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
            var card = await Stop.Card(jobs, 4);
            Assert.Equal("stopped", card.State);
            Assert.Equal(CardStore.PullRequestClosed, card.Reason);
            Assert.NotNull(card.StoppedAt);
        }
    }

    [Fact]
    public async Task AFailureInTheCardOnceIsTriedAgain()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        const string Failed = "The run failed: it reached the most turns, 200 turns";

        // One failure in the card queues it again, and says so; the run keeps its own result.
        await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
        var card = await Stop.Card(jobs, 4);
        Assert.Equal("queued", card.State);
        Assert.Equal(RunStore.Again(Failed), card.Reason);
        Assert.Equal(1, card.InCardFailures);
        Assert.Equal("failed_in_card", (await jobs.Runs())[0].Result);

        // A second that pushed something new is progress: the card runs once more.
        var pushed = await Stop.Run(jobs, Built.Prepared(Jobs.Jobs.GoodReport(), checkExit: 1));
        Assert.Equal(4, (int)pushed!["card"]!);
        card = await Stop.Card(jobs, 4);
        Assert.Equal("queued", card.State);
        Assert.Equal(RunStore.Again("The check failed with exit code 1"), card.Reason);

        // The next with nothing new, on top of that commit, stops it.
        await Stop.Run(jobs, Stop.InCard(), leaveNoCommit: true);
        card = await Stop.Card(jobs, 4);
        Assert.Equal("stopped", card.State);
        Assert.Equal(RunStore.Twice(Failed), card.Reason);

        // A fix is tried again the same way, in the same round, and a success between two failures
        // starts the count afresh.
        await using var fixing = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        await Stop.Run(fixing, Stop.InCard(), leaveNoCommit: true);
        // A minute on, so the build that follows is the newest, as it always is for a worker.
        Fixed.Later(fixing);
        await Fixed.BuiltAndReviewed(fixing);
        Assert.Equal(0, (await Stop.Card(fixing, 4)).InCardFailures);
        await Fixed.Fix(fixing, leaveNoCommit: true);
        card = await Stop.Card(fixing, 4);
        Assert.Equal("fixing", card.State);
        Assert.Equal(RunStore.Again(FixRounds.NoNewCommit), card.Reason);
        Assert.Equal(1, card.FixRounds);
        await Fixed.Fix(fixing, leaveNoCommit: true);
        card = await Stop.Card(fixing, 4);
        Assert.Equal("stopped", card.State);
        Assert.Equal(RunStore.Twice(FixRounds.NoNewCommit), card.Reason);
    }
}