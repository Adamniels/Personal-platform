using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Features;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Features;

using Merged = DevFlow.Tests.Merging.Merged;

/// <summary>
/// The owner merges a feature into the default branch from its page or from "Waiting for you", as
/// himself with his linked token and never as the app, a merge commit naming the head the page
/// showed. It is offered only when CI passed on that head and the project is not stopped, and the
/// route refuses everything else before the code host is asked; the dialog reads whether the review
/// approved the head, its open findings when it did not, and how many decisions he has not
/// answered, which merging confirms. Against real Postgres and the fake code host, with the fake
/// worker. See "Merging a feature" in devflow/README.md.
/// </summary>
public sealed class FeatureMergeTests
{
    [Fact]
    public async Task MergingIsMine()
    {
        await using var jobs = await FeatureMerging.Feature();
        var head = await FeatureMerging.Reviewed(jobs);
        Assert.Equal("ready", (await Ending.Row(jobs)).State);
        var pull = await FeatureMerging.Pull(jobs);

        // Offered on its page and on its item, named with the default branch and the head shown.
        var story = await FeatureMerging.Story(jobs);
        Assert.Equal(("main", head), ((string)story["merge"]!["into"]!, (string)story["merge"]!["head_commit"]!));
        var item = await FeatureMerging.Item(jobs);
        Assert.Equal(("main", head), ((string)item["merge"]!["into"]!, (string)item["merge"]!["head_commit"]!));

        var response = await FeatureMerging.Merge(jobs, head);

        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
        // One merge of the feature's pull request, a merge commit with the head shown as its sha,
        // made with my linked token, which the fake knows as me: never the app's key.
        var merge = Assert.Single(FeatureMerging.Merges(jobs));
        Assert.Equal(new FakeWrite("merge", Reading.Reading.RepositoryId, pull, head, "merge", null, Jobs.Jobs.Owner), merge);
        Assert.Null(merge.Purpose);
        Assert.DoesNotContain(jobs.Routes.Host.Calls, call => call.Operation == "MergeAsApp" && call.Argument == pull.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(PullRequestState.Merged, FeatureMerging.PullRequest(jobs, pull).State);

        // Read again before the answer: the feature is merged, offered nowhere, and off the list.
        Assert.Equal("merged", (await Ending.Row(jobs)).State);
        Assert.Null((await FeatureMerging.Story(jobs))["merge"]);
        var (_, list) = await Built.Get(jobs, "/devflow/api/waiting");
        Assert.Empty(list!["features"]!.AsArray());
    }

    /// <summary>CI on the head, as the case names it.</summary>
    public static TheoryData<string> NotGreen() => ["pending", "failed", "never reported"];

    [Theory]
    [MemberData(nameof(NotGreen))]
    public async Task OnlyWithGreenCi(string ci)
    {
        await using var jobs = await FeatureMerging.Feature();
        IReadOnlyList<CheckRun> runs = ci switch
        {
            "pending" => [new CheckRun("test", CheckStatus.InProgress, null, "github-actions")],
            "failed" => [new CheckRun("test", CheckStatus.Completed, CheckConclusion.Failure, "github-actions")],
            _ => [],
        };
        var head = await FeatureMerging.Reviewed(jobs, "approve", runs);
        Assert.Equal("waiting_for_owner", (await Ending.Row(jobs)).State);

        // Not offered, on its page or on its item.
        Assert.Null((await FeatureMerging.Story(jobs))["merge"]);
        Assert.Null((await FeatureMerging.Item(jobs))["merge"]);

        // The route refuses the same way, before the code host is asked, and neither does the
        // dialog's.
        var refused = await Acting.Acting.Problem(await FeatureMerging.Merge(jobs, head), StatusCodes.Status409Conflict);
        Assert.Equal("CI has not passed on this commit", (string)refused["title"]!);
        var (confirm, _) = await Built.Get(jobs, FeatureMerging.MergePath(jobs.Project));
        Assert.Equal(StatusCodes.Status409Conflict, confirm);
        Assert.Equal(0, FeatureMerging.AskedToMerge(jobs));
        Assert.Empty(FeatureMerging.Merges(jobs));
    }

    [Fact]
    public async Task AHeadThatMovedIsRefused()
    {
        await using var jobs = await FeatureMerging.Feature();
        var head = await FeatureMerging.Reviewed(jobs);
        var pull = await FeatureMerging.Pull(jobs);

        // The page showed another commit than the one stored: refused before GitHub is asked, and
        // the project read again.
        var lists = jobs.Routes.Host.Lists(FeatureMerging.User);
        var stale = await Acting.Acting.Problem(await FeatureMerging.Merge(jobs, new string('a', 40)), StatusCodes.Status409Conflict);
        Assert.Equal(FeatureMerging.Changed, (string)stale["title"]!);
        Assert.Equal(0, FeatureMerging.AskedToMerge(jobs));
        Assert.Equal(lists + 1, jobs.Routes.Host.Lists(FeatureMerging.User));

        // I push to the feature's branch on GitHub after the last read: GitHub refuses the head the
        // page showed. Nothing is merged, the project is read again, and the page says it changed.
        var mine = await Reviewing.PushOnFeature(jobs);
        Assert.Equal(mine, FeatureMerging.PullRequest(jobs, pull).HeadCommit);
        lists = jobs.Routes.Host.Lists(FeatureMerging.User);
        var moved = await Acting.Acting.Problem(await FeatureMerging.Merge(jobs, head), StatusCodes.Status409Conflict);

        Assert.Equal(FeatureMerging.Changed, (string)moved["title"]!);
        Assert.Equal(1, FeatureMerging.AskedToMerge(jobs));
        Assert.Empty(FeatureMerging.Merges(jobs));
        Assert.Equal(PullRequestState.Open, FeatureMerging.PullRequest(jobs, pull).State);
        Assert.Equal(lists + 1, jobs.Routes.Host.Lists(FeatureMerging.User));
        // Read again, the feature is at my commit, on which CI has not passed: no longer offered.
        Assert.Equal(mine, await Reviewing.Head(jobs));
        Assert.Null((await FeatureMerging.Story(jobs))["merge"]);
        Assert.NotEqual("merged", (await Ending.Row(jobs)).State);
    }

    [Fact]
    public async Task TheConfirmationSaysWhatIsOpen()
    {
        // Approved: nothing open, and the one decision card 11 marked for me, not answered.
        await using (var jobs = await FeatureMerging.Feature())
        {
            var head = await FeatureMerging.Reviewed(jobs);
            var (status, confirm) = await Built.Get(jobs, FeatureMerging.MergePath(jobs.Project));
            Assert.Equal(StatusCodes.Status200OK, status);
            Assert.Equal(("main", head, await FeatureMerging.Pull(jobs)),
                ((string)confirm!["into"]!, (string)confirm["head_commit"]!, (int)confirm["pull_request"]!["number"]!));
            Assert.Equal("approved", (string)confirm["review"]!);
            Assert.Empty(confirm["open_findings"]!.AsArray());
            Assert.Equal(1, (int)confirm["unanswered"]!);
        }

        // The review asked for changes and CI passed: offered, with its blocking findings open.
        await using (var jobs = await FeatureMerging.Feature())
        {
            await FeatureMerging.Reviewed(jobs, "changes", null,
                Built.Finding(blocking: true, what: "The two cards read the branch twice", why: "One read would do"),
                Built.Finding(blocking: false, what: "A name could be shorter"));
            Assert.Equal(("waiting_for_owner", FeatureReview.ChangesAsked), ((await Ending.Row(jobs)).State, (await Ending.Row(jobs)).Reason));
            var (status, confirm) = await Built.Get(jobs, FeatureMerging.MergePath(jobs.Project));
            Assert.Equal(StatusCodes.Status200OK, status);
            Assert.Equal("changes", (string)confirm!["review"]!);
            var open = Assert.Single(confirm["open_findings"]!.AsArray())!;
            Assert.Equal(("The two cards read the branch twice", "One read would do", true),
                ((string)open["what"]!, (string)open["why"]!, (bool)open["blocking"]!));
            Assert.Equal(1, (int)confirm["unanswered"]!);
        }

        // Its review failed in the job and CI passed: offered, not reviewed as a whole. Merging
        // records the decision I had not answered as confirmed by merging.
        await using (var jobs = await FeatureMerging.Feature())
        {
            var head = await FeatureMerging.Reviewed(jobs, verdict: null);
            Assert.Equal("waiting_for_owner", (await Ending.Row(jobs)).State);
            var (status, confirm) = await Built.Get(jobs, FeatureMerging.MergePath(jobs.Project));
            Assert.Equal(StatusCodes.Status200OK, status);
            Assert.Equal("not_reviewed", (string)confirm!["review"]!);
            Assert.Empty(confirm["open_findings"]!.AsArray());
            Assert.Empty(await FeaturePage.Answers(jobs));

            Assert.Equal(StatusCodes.Status204NoContent, (await FeatureMerging.Merge(jobs, head)).Response.StatusCode);

            var answer = Assert.Single(await FeaturePage.Answers(jobs));
            Assert.Equal((Ending.First, "merged"), (answer.CardNumber, answer.Answer));
            var decision = (await FeatureMerging.Story(jobs))["cards"]![0]!["decisions"]![0]!;
            Assert.Equal("merged", (string)decision["answer"]!["answer"]!);
        }
    }

    [Fact]
    public async Task NotForAStoppedProject()
    {
        await using var jobs = await FeatureMerging.Feature();
        var head = await FeatureMerging.Reviewed(jobs);
        Assert.Equal("ready", (await Ending.Row(jobs)).State);

        await Merged.Stop(jobs);

        Assert.Null((await FeatureMerging.Story(jobs))["merge"]);
        var refused = await Acting.Acting.Problem(await FeatureMerging.Merge(jobs, head), StatusCodes.Status409Conflict);
        Assert.Equal("This project is stopped", (string)refused["title"]!);
        Assert.Equal(0, FeatureMerging.AskedToMerge(jobs));
        Assert.Empty(FeatureMerging.Merges(jobs));
    }

    [Fact]
    public async Task GitHubsOwnButtonIsNoticed()
    {
        await using var jobs = await FeatureMerging.Feature();
        await FeatureMerging.Reviewed(jobs);
        var pull = await FeatureMerging.Pull(jobs);

        // I merge it on GitHub, which closes the parent by its Closes line.
        Built.OnGitHub(jobs, pull, PullRequestState.Merged, closeIssue: Ending.Parent);
        await Reviewing.ReadAgain(jobs);

        Assert.Equal("merged", (await Ending.Row(jobs)).State);
        Assert.Null((await FeatureMerging.Story(jobs))["merge"]);
        Assert.Equal(0, FeatureMerging.AskedToMerge(jobs));
    }

    [Fact]
    public async Task AnotherUsersFeatureIs404()
    {
        await using var jobs = await FeatureMerging.Feature();
        var head = await FeatureMerging.Reviewed(jobs);
        var bobs = await FeaturePage.BobsFeature(jobs);

        var merge = await Acting.Acting.Problem(
            await jobs.Routes.App.SendJson("POST", FeatureMerging.MergePath(bobs), Contract.Token, System.Text.Json.JsonSerializer.Serialize(new { head_commit = head })),
            StatusCodes.Status404NotFound);
        var (status, _) = await Built.Get(jobs, FeatureMerging.MergePath(bobs));

        Assert.Equal("No such feature", (string)merge["title"]!);
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal(0, FeatureMerging.AskedToMerge(jobs));
    }

    [Fact]
    public async Task WithoutTheCommitIs422AndWithoutALinkNothingIsSent()
    {
        await using var jobs = await FeatureMerging.Feature();
        var head = await FeatureMerging.Reviewed(jobs);

        var missing = await Acting.Acting.Problem(await FeatureMerging.Merge(jobs, null), StatusCodes.Status422UnprocessableEntity);
        Assert.Contains("head_commit", (string)missing["detail"]!, StringComparison.Ordinal);

        await Merged.Sql(jobs, "UPDATE github_accounts SET state = 'needs_relink'");
        var expired = await Acting.Acting.Problem(await FeatureMerging.Merge(jobs, head), StatusCodes.Status409Conflict);

        Assert.Equal("Your GitHub link has expired", (string)expired["title"]!);
        Assert.Equal(0, FeatureMerging.AskedToMerge(jobs));
        Assert.Empty(FeatureMerging.Merges(jobs));
    }
}