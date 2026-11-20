using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Features;

using Merged = DevFlow.Tests.Merging.Merged;

/// <summary>
/// "Waiting for you" answers a group of the features ready for the owner's review, one item per
/// feature with its title, the review's summary and how many of its decisions he has not answered,
/// and a feature waiting for him shows in it with why; each is counted in the tab, the nav's dot
/// and the front page as every item is. Read from what Dev flow stored, never from the code host.
/// Against real Postgres and the fake code host, with the fake worker. See "Reviewing a feature"
/// and "What waits" in devflow/README.md.
/// </summary>
public sealed class WaitingFeatureTests
{
    [Fact]
    public async Task ReadyFeaturesAreOnTheList()
    {
        await using var jobs = await Ending.Feature();
        var head = await Reviewing.Published(jobs);

        // Reviewing, it is nobody's turn but the reviewer's: not on the list, and counted nowhere.
        var (_, before) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Empty(before!["features"]!.AsArray());
        Assert.Equal(0, (int)before["count"]!);

        await Reviewing.Review(jobs, Built.Verdict("approve"));
        Merged.Ci(jobs, head, Merged.GreenRuns, Merged.GreenStatuses);
        await Reviewing.Minute(jobs);
        var pull = (await Ending.Row(jobs)).PullRequestNumber!.Value;

        // Ready: one item for the feature, with its title, the review's summary, its pull request
        // and its page, and the one decision card 11 marked for me, not answered yet.
        var (status, list) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Equal(StatusCodes.Status200OK, status);
        var item = Assert.Single(list!["features"]!.AsArray())!;
        Assert.Equal((jobs.Project.ToString(), Ending.Parent, "Dev flow: features", "ready"),
            ((string)item["project_id"]!, (int)item["number"]!, (string)item["title"]!, (string)item["state"]!));
        Assert.Null(item["reason"]);
        Assert.Equal(("approve", "The empty state is there and tested.", head),
            ((string)item["review"]!["verdict"]!, (string)item["review"]!["summary"]!, (string)item["review"]!["commit"]!));
        Assert.Equal(pull, (int)item["pull_request_number"]!);
        Assert.Equal(1, (int)item["unanswered"]!);

        // Counted as every item is: in the list's count, the nav's dot and the front page.
        Assert.Equal(1, (int)list["count"]!);
        var (_, attention) = await Built.Get(jobs, "/devflow/api/attention");
        Assert.True((bool)attention!["attention"]!);
        var (_, display) = await Built.Get(jobs, "/devflow/api/display");
        Assert.Equal(1, (int)display!["waiting"]!);

        // Answered "Fine as it is" on the feature's page, it has none left to answer.
        var (_, story) = await Built.Get(jobs, FeaturePage.PathOf(jobs.Project));
        var decision = story!["cards"]![0]!["decisions"]![0]!;
        var fine = await jobs.Routes.App.SendJson("POST", FeaturePage.FinePath(jobs.Project, (Guid)decision["run_id"]!, (int)decision["position"]!), Contract.Token, "{}");
        Assert.Equal(StatusCodes.Status200OK, fine.Response.StatusCode);
        (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Equal(0, (int)Assert.Single(list!["features"]!.AsArray())!["unanswered"]!);

        // My own push on its branch, then a review that asks for changes: it waits for me, in the
        // same group, with why.
        await Reviewing.PushOnFeature(jobs);
        await Reviewing.Minute(jobs);
        await Reviewing.Review(jobs, Built.Verdict("changes"));
        await Reviewing.Minute(jobs);
        (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        item = Assert.Single(list!["features"]!.AsArray())!;
        Assert.Equal(("waiting_for_owner", DevFlow.Features.FeatureReview.ChangesAsked, "changes"),
            ((string)item["state"]!, (string)item["reason"]!, (string)item["review"]!["verdict"]!));
        Assert.Equal(1, (int)list["count"]!);
    }
}