using DevFlow.Features;
using DevFlow.Tests.Jobs;

namespace DevFlow.Tests.Features;

/// <summary>
/// A feature kept current while it waits for the owner, as its review leaves it: each read finds the default branch's
/// commit, as its watch named it, and brings it in again once it has moved past the one brought in,
/// never while the feature is worked on; a conflict its agent could not resolve is not tried again
/// until the default branch moves; and a card added to a feature at its end sends it back to building, its pull request open,
/// and its end runs again once that card is done. Against real Postgres and the fake code host. See
/// "The end of a feature" in devflow/README.md.
/// </summary>
public sealed class KeepingCurrentTests
{
    /// <summary>The feature at its end, brought in with its pull request open and reviewing; then,
    /// unless <paramref name="waiting"/> is false, waiting for the owner, as a review that asked for
    /// changes leaves it. The pull request's number.</summary>
    private static async Task<int> AtItsEnd(Jobs.Jobs jobs, bool waiting = true)
    {
        await Ending.Finished(jobs);
        await Ending.ReadAndEnd(jobs);
        var row = await Ending.Row(jobs);
        Assert.Equal(("reviewing", (string?)null), (row.State, row.Reason));
        if (waiting)
        {
            await Ending.SetState(jobs, "waiting_for_owner", FeatureReview.ChangesAsked);
        }

        return row.PullRequestNumber!.Value;
    }

    [Fact]
    public async Task WhileWaitingTheDefaultBranchIsBroughtInAgain()
    {
        await using var jobs = await Ending.Feature();
        var number = await AtItsEnd(jobs);
        Assert.Empty(Ending.BringIns(jobs));

        // The owner pushes to the default branch: the next read finds it moved and brings it in.
        var moved = await Ending.MoveMain(jobs);
        await jobs.Routes.Read(jobs.Project, Ending.User);
        Assert.Equal("bringing_in", (await Ending.Row(jobs)).State);
        await Ending.End(jobs);

        var write = Assert.Single(Ending.BringIns(jobs));
        Assert.Equal(("main", Ending.Branch), (write.Head, write.Base));
        var row = await Ending.Row(jobs);
        Assert.Equal((moved, "reviewing", (string?)null, number), (row.BroughtInCommit, row.State, row.Reason, row.PullRequestNumber));
        Assert.Contains((write.Commit!, (int?)null), await Ending.MergesMade(jobs));

        // Read again with nothing new, nothing is brought in, and the watch found the merge fine.
        await Ending.ReadAndEnd(jobs);
        Assert.Single(Ending.BringIns(jobs));
        Assert.False(await Ending.Stopped(jobs));

        // Waiting for the owner again, as its review leaves it, it moves again, and is brought in
        // again, recording the newer commit.
        await Ending.SetState(jobs, "waiting_for_owner", FeatureReview.ChangesAsked);
        var again = await Ending.MoveMain(jobs, "Main moves again");
        await Ending.ReadAndEnd(jobs);
        Assert.Equal(2, Ending.BringIns(jobs).Count);
        Assert.Equal(again, (await Ending.Row(jobs)).BroughtInCommit);
    }

    /// <summary>The states of a feature being worked on, which a later slice sets.</summary>
    public static TheoryData<string> WorkedOn() => ["reviewing", "fixing", "resolving"];

    [Theory]
    [MemberData(nameof(WorkedOn))]
    public async Task NotWhileItIsWorkedOn(string state)
    {
        await using var jobs = await Ending.Feature();
        await AtItsEnd(jobs);
        await Ending.SetState(jobs, state);
        await Ending.MoveMain(jobs);

        await Ending.ReadAndEnd(jobs);
        Assert.Equal(1, Ending.Asked(jobs));
        Assert.Equal(state, (await Ending.Row(jobs)).State);

        // Once it waits for the owner again, the next read brings the default branch in.
        await Ending.SetState(jobs, "waiting_for_owner", FeatureReview.ChangesAsked);
        await Ending.ReadAndEnd(jobs);
        Assert.Equal(2, Ending.Asked(jobs));
        Assert.Single(Ending.BringIns(jobs));
    }

    [Fact]
    public async Task NotWhileACardIsUnderWay()
    {
        // Building with its last card still under way: no bringing in, whatever the default branch
        // does.
        await using var jobs = await Ending.Feature();
        await FeaturePage.Landed(jobs);
        await FeatureJobs.SetState(jobs, Ending.Second, "building");
        await Ending.MoveMain(jobs);

        await Ending.ReadAndEnd(jobs);

        Assert.Equal(0, Ending.Asked(jobs));
        Assert.Equal(("building", null), ((await Ending.Row(jobs)).State, (await Ending.Row(jobs)).Reason));
    }

    [Fact]
    public async Task AConflictIsTriedAgainOnlyOnceTheDefaultBranchMoves()
    {
        await using var jobs = await Ending.Feature();
        await FeaturePage.Landed(jobs);
        await Ending.MoveMain(jobs);
        Merging.Merged.Change(jobs, repository => repository with { Conflicts = [Ending.Branch] });
        await Ending.Done(jobs);
        await Ending.ReadAndEnd(jobs);
        Assert.Equal(1, Ending.Asked(jobs));

        // The agent could not resolve it, so the feature waits for the owner.
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(1));
        await jobs.Routes.Read(jobs.Project, Ending.User);
        await PullRequests.Built.Run(jobs, PullRequests.Built.PreparedFailure(inCard: true, "The agent ran out of turns"));
        Assert.Equal("waiting_for_owner", (await Ending.Row(jobs)).State);

        // Read again at the same commit: not asked again.
        await Ending.ReadAndEnd(jobs);
        await Ending.ReadAndEnd(jobs);
        Assert.Equal(1, Ending.Asked(jobs));
        Assert.Equal(FeatureEnd.NotResolved("main", "The run failed: The agent ran out of turns"), (await Ending.Row(jobs)).Reason);

        // The default branch moves, the conflict is gone: brought in, and the pull request opened.
        var moved = await Ending.MoveMain(jobs, "Main resolves it");
        Merging.Merged.Change(jobs, repository => repository with { Conflicts = [] });
        await Ending.ReadAndEnd(jobs);
        Assert.Equal(2, Ending.Asked(jobs));
        var row = await Ending.Row(jobs);
        Assert.Equal((moved, null, "reviewing", (string?)null), (row.BroughtInCommit, row.BringInFailedAt, row.State, row.Reason));
        Assert.NotNull(row.PullRequestNumber);
    }

    [Fact]
    public async Task ANewCardSendsTheFeatureBackToBuilding()
    {
        await using var jobs = await Ending.Feature(FeatureJobs.Sub(13, "The feature's end"));
        var number = await AtItsEnd(jobs, waiting: false);

        // A sub-issue added to the feature at its end, reviewing, is picked on the next read, and the
        // feature is building again, its pull request still open and still its.
        Merging.Merged.Issue(jobs, Ending.Parent, parent => parent with { SubIssues = [new(11, null), new(12, null), new(13, null)] });
        await jobs.Routes.Read(jobs.Project, Ending.User);
        var card = await PullRequests.Built.Card(jobs, 13);
        Assert.Equal("queued", card.State);
        var row = await Ending.Row(jobs);
        Assert.Equal(("building", null, number), (row.State, row.Reason, row.PullRequestNumber));
        Assert.Equal(CodeHost.PullRequestState.Open, Merging.Merged.Pull(jobs, number).State);

        // While it is not done, the end does not run.
        await Ending.End(jobs);
        Assert.Equal(1, Ending.Asked(jobs));
        Assert.Equal("building", (await Ending.Row(jobs)).State);

        // Once it is, the end runs again: brought in, the same pull request, reviewing again.
        await Ending.Done(jobs, 13, 41);
        await Ending.ReadAndEnd(jobs);
        Assert.Equal(2, Ending.Asked(jobs));
        row = await Ending.Row(jobs);
        Assert.Equal(("reviewing", (string?)null, number), (row.State, row.Reason, row.PullRequestNumber));
        Assert.Single(jobs.Fake.Writes(Ending.User), write => write.Kind == "pull_request" && write.Base == "main");
    }
}