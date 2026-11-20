using DevFlow.Features;
using DevFlow.Tests.Jobs;

namespace DevFlow.Tests.Features;

/// <summary>
/// Bringing the default branch into a finished feature's branch: once every card of a feature is
/// done, a read moves it to <c>bringing_in</c>, and the end's minute brings the default branch in
/// as the app, with a key for merging alone, as a merge commit, recorded under the project's lock
/// so the watch never judges it unrecorded. Nothing to bring in goes on, a conflict waits for the
/// owner, and a card not done holds the end. Against real Postgres and the fake code host. See "The
/// end of a feature" in devflow/README.md and devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class BringingInTests
{
    private static CancellationToken Ct => Ending.Ct;

    [Fact]
    public async Task TheLastCardBringsTheDefaultBranchIn()
    {
        await using var jobs = await Ending.Feature();
        await FeaturePage.Landed(jobs);
        var moved = await Ending.MoveMain(jobs);
        await jobs.Routes.Read(jobs.Project, Ending.User);
        Assert.Equal("building", (await Ending.Row(jobs)).State);

        // The last card is done: the next read moves the feature to bringing it in.
        await Ending.Done(jobs);
        await jobs.Routes.Read(jobs.Project, Ending.User);
        Assert.Equal(("bringing_in", null), ((await Ending.Row(jobs)).State, (await Ending.Row(jobs)).Reason));

        // A read started while the code host is asked cannot judge the feature's branch before the
        // merge is recorded: it waits for the project's lock, which bringing in holds throughout.
        Task? reading = null;
        jobs.Routes.Host.BeforeBringIn = async () =>
        {
            reading = jobs.Routes.Read(jobs.Project, Ending.User);
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            Assert.False(reading.IsCompleted, "a read finished while the bring in held the project's lock");
        };
        await Ending.End(jobs);
        Assert.NotNull(reading);
        await reading;

        // One merge by the app with the merging key, of the default branch into the feature's, a
        // merge commit with the message Dev flow writes, made in the fake's git.
        var write = Assert.Single(Ending.BringIns(jobs));
        Assert.Equal((CodeHost.FakeCodeHost.AppLogin, "merge", "merge", "main", Ending.Branch, Ending.Message),
            (write.By, write.Purpose, write.Method, write.Head, write.Base, write.Body));
        Assert.True(await Ending.Holds(jobs, write.Commit!));

        // Recorded with the feature's branch and no pull request, beside the card's own merge.
        var made = await Ending.MergesMade(jobs);
        Assert.Equal(2, made.Count);
        Assert.Contains((write.Commit!, (int?)null), made);

        // The feature records the default branch's commit it brought in, and goes on.
        var row = await Ending.Row(jobs);
        Assert.Equal(moved, row.BroughtInCommit);
        Assert.Null(row.BringInFailedAt);
        Assert.NotEqual("bringing_in", row.State);

        // The watch, reading the feature's branch, finds the app's merge fine.
        await jobs.Routes.Read(jobs.Project, Ending.User);
        Assert.False(await Ending.Stopped(jobs));
    }

    [Fact]
    public async Task NothingToBringIn()
    {
        await using var jobs = await Ending.Feature();
        await Ending.Finished(jobs);

        // The feature's branch was made from the default branch's head, which has not moved.
        await Ending.ReadAndEnd(jobs);

        Assert.Equal(1, Ending.Asked(jobs));
        Assert.Empty(Ending.BringIns(jobs));
        Assert.Single(await Ending.MergesMade(jobs));
        var row = await Ending.Row(jobs);
        Assert.NotEqual("bringing_in", row.State);
        Assert.NotEqual(FeatureEnd.Conflict("main"), row.Reason);
        Assert.Null(row.BringInFailedAt);
        await jobs.Routes.Read(jobs.Project, Ending.User);
        Assert.False(await Ending.Stopped(jobs));
    }

    [Fact]
    public async Task AConflictWaitsForMe()
    {
        await using var jobs = await Ending.Feature();
        await FeaturePage.Landed(jobs);
        var moved = await Ending.MoveMain(jobs);
        Merging.Merged.Change(jobs, repository => repository with { Conflicts = [Ending.Branch] });
        await Ending.Done(jobs);

        await Ending.ReadAndEnd(jobs);

        // The code host answered a conflict: nothing was made and nothing recorded.
        Assert.Equal(1, Ending.Asked(jobs));
        Assert.Empty(Ending.BringIns(jobs));
        Assert.Single(await Ending.MergesMade(jobs));
        var row = await Ending.Row(jobs);
        Assert.Equal(("waiting_for_owner", "main conflicts with the feature's branch"), (row.State, row.Reason));
        Assert.Null(row.BroughtInCommit);
        Assert.Equal(moved, row.BringInFailedAt);
        Assert.Null(row.PullRequestNumber);
    }

    /// <summary>Each state of a card that is not done and holds the feature's end, and the reason
    /// it gives the feature.</summary>
    public static TheoryData<string, string> NotDone() => new()
    {
        { "stopped", "Its end waits for #12, which is stopped" },
        { "held", "Its end waits for #12, which is held" },
        { "waiting_for_owner", "Its end waits for #12, which waits for you" },
    };

    [Theory]
    [MemberData(nameof(NotDone))]
    public async Task WhatIsNotDoneHoldsTheEnd(string state, string reason)
    {
        await using var jobs = await Ending.Feature();
        await FeaturePage.Landed(jobs);
        if (state == "held")
        {
            // Held by its label, which each read reads.
            Merging.Merged.Issue(jobs, Ending.Second, issue => issue with { Labels = ["devflow-hold"] });
        }
        else
        {
            await FeatureJobs.SetState(jobs, Ending.Second, state);
        }

        await Ending.ReadAndEnd(jobs);

        // No bringing in, and the feature's reason names the card.
        Assert.Equal(0, Ending.Asked(jobs));
        Assert.Equal(("building", reason), ((await Ending.Row(jobs)).State, (await Ending.Row(jobs)).Reason));

        // Once it is done, the end runs.
        if (state == "held")
        {
            Merging.Merged.Issue(jobs, Ending.Second, issue => issue with { Labels = [] });
        }

        await Ending.Done(jobs);
        await Ending.ReadAndEnd(jobs);
        Assert.Equal(1, Ending.Asked(jobs));
        Assert.DoesNotContain("Its end waits", (await Ending.Row(jobs)).Reason ?? "", StringComparison.Ordinal);
    }
}