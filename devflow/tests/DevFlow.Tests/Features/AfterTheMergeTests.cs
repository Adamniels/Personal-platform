using DevFlow.CodeHost;
using DevFlow.Features;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Features;

using Merged = DevFlow.Tests.Merging.Merged;

/// <summary>
/// What follows a feature's pull request leaving the list of open ones: merged, the parent is closed
/// by its <c>Closes</c> line and any card of the feature still open is closed by the app with a
/// comment; closed without merging, each of its cards is opened again by the app with a comment,
/// and the feature is <c>closed</c>, until it is labelled again later and starts afresh. Either way
/// its branch is no longer watched. Against real Postgres and the fake code host, with the fake
/// worker. See "Merging a feature" in devflow/README.md.
/// </summary>
public sealed class AfterTheMergeTests
{
    [Fact]
    public async Task TheParentAndItsCardsAreClosed()
    {
        await using var jobs = await FeatureMerging.Feature();
        var head = await FeatureMerging.Reviewed(jobs);
        var pull = await FeatureMerging.Pull(jobs);
        // Card 11 landed and was closed by the app as it landed; card 12 is still open.
        Assert.False(FeatureMerging.Issue(jobs, Ending.First).Open);
        Assert.True(FeatureMerging.Issue(jobs, Ending.Second).Open);
        var told = FeatureMerging.AppWritesTo(jobs, Ending.First).Count;

        Assert.Equal(StatusCodes.Status204NoContent, (await FeatureMerging.Merge(jobs, head)).Response.StatusCode);

        // The parent is closed by its Closes line as the pull request merged into main, not by the
        // app.
        Assert.False(FeatureMerging.Issue(jobs, Ending.Parent).Open);
        Assert.Empty(FeatureMerging.AppWritesTo(jobs, Ending.Parent));

        await FeatureMerging.Tell(jobs);

        // Card 12, still open, is closed by the app with a comment saying where its feature went;
        // card 11, closed already, is left as it was.
        Assert.Equal(
            [("comment", FeatureOutcomes.MergedComment(Ending.Parent, pull, "main")), ("issue_closed", (string?)null)],
            FeatureMerging.AppWritesTo(jobs, Ending.Second).Select(write => (write.Kind, write.Body)));
        Assert.False(FeatureMerging.Issue(jobs, Ending.Second).Open);
        Assert.Equal(told, FeatureMerging.AppWritesTo(jobs, Ending.First).Count);

        // Told once: the next minute writes nothing more.
        var writes = jobs.Fake.Writes(FeatureMerging.User).Count;
        await FeatureMerging.Tell(jobs);
        Assert.Equal(writes, jobs.Fake.Writes(FeatureMerging.User).Count);

        // Merged, its branch is no longer watched: the next read does not read it.
        var watched = await FeatureMerging.WatchedAt(jobs);
        var reads = jobs.Routes.Host.Calls.Count(call => call.Operation == "Activity" && call.Argument == Ending.Branch);
        await Reviewing.ReadAgain(jobs);
        Assert.Equal(watched, await FeatureMerging.WatchedAt(jobs));
        Assert.Equal(reads, jobs.Routes.Host.Calls.Count(call => call.Operation == "Activity" && call.Argument == Ending.Branch));
        Assert.Equal("merged", (await Ending.Row(jobs)).State);
        Assert.False(await Ending.Stopped(jobs));
    }

    [Fact]
    public async Task AClosedPullRequestGivesTheCardsBack()
    {
        await using var jobs = await FeatureMerging.Feature();
        await FeatureMerging.Reviewed(jobs);
        var pull = await FeatureMerging.Pull(jobs);
        // Both cards landed, and both issues are closed.
        Merged.Issue(jobs, Ending.Second, issue => issue with { Open = false });

        // I close the feature's pull request on GitHub without merging it.
        Built.OnGitHub(jobs, pull, PullRequestState.Closed);
        await Reviewing.ReadAgain(jobs);

        var row = await Ending.Row(jobs);
        Assert.Equal(("closed", (string?)null), (row.State, row.Reason));
        Assert.Null((await FeatureMerging.Story(jobs))["merge"]);

        await FeatureMerging.Tell(jobs);

        // Each card is open again, by the app, with the comment saying why.
        var comment = $"The feature's pull request #{pull} was closed without merging, so this card is open again.";
        Assert.Equal(comment, FeatureOutcomes.ClosedComment(pull));
        foreach (var card in new[] { Ending.First, Ending.Second })
        {
            Assert.True(FeatureMerging.Issue(jobs, card).Open);
            Assert.Equal(
                [("comment", comment), ("issue_opened", (string?)null)],
                FeatureMerging.AppWritesTo(jobs, card).Where(write => write.Kind != "issue_closed" && write.Body?.StartsWith("Built and merged", StringComparison.Ordinal) != true)
                    .Select(write => (write.Kind, write.Body)));
        }

        // Told once.
        var writes = jobs.Fake.Writes(FeatureMerging.User).Count;
        await FeatureMerging.Tell(jobs);
        Assert.Equal(writes, jobs.Fake.Writes(FeatureMerging.User).Count);

        // Closed, its branch is no longer watched, and it stays closed while it is labelled.
        var watched = await FeatureMerging.WatchedAt(jobs);
        await Reviewing.ReadAgain(jobs);
        Assert.Equal(watched, await FeatureMerging.WatchedAt(jobs));
        Assert.Equal("closed", (await Ending.Row(jobs)).State);
        Assert.False(await Ending.Stopped(jobs));
    }

    [Fact]
    public async Task AClosedFeatureLabelledAgainStartsAfresh()
    {
        await using var jobs = await FeatureMerging.Feature();
        await FeatureMerging.Reviewed(jobs);
        Built.OnGitHub(jobs, await FeatureMerging.Pull(jobs), PullRequestState.Closed);
        await Reviewing.ReadAgain(jobs);
        await FeatureMerging.Tell(jobs);
        Assert.Equal("closed", (await Ending.Row(jobs)).State);

        // I take the label off the parent, and later put it back.
        Merged.Issue(jobs, Ending.Parent, issue => issue with { Labels = [] });
        await Reviewing.ReadAgain(jobs);
        Merged.Issue(jobs, Ending.Parent, issue => issue with { Labels = ["devflow"] });
        await Reviewing.ReadAgain(jobs);

        // A feature as new: queued, no branch and no pull request, its cards queued in order.
        var row = await Ending.Row(jobs);
        Assert.Equal(("queued", (string?)null, (int?)null), (row.State, row.Branch, row.PullRequestNumber));
        Assert.Equal(
            [(Ending.First, "queued"), (Ending.Second, "queued")],
            (await jobs.Cards()).Where(card => card.Number != Ending.Parent).OrderBy(card => card.Number).Select(card => (card.Number, card.State)));
        Assert.Null((await FeatureMerging.Story(jobs))["branch"]);
    }
}