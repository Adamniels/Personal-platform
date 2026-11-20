using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Features;
using DevFlow.Merging;
using DevFlow.Protocol;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Features;

using Merged = DevFlow.Tests.Merging.Merged;

/// <summary>
/// When a reviewed feature is ready for the owner: the latest review approves its head, or a head
/// that differs from the approved commit only by merges of the default branch Dev flow made
/// cleanly, and CI passed on that head, read as the merge check reads it. Anything else waits for
/// the owner with why, and a head no review covers is reviewed again. Against real Postgres and the
/// fake code host, with the fake worker. See "Reviewing a feature" in devflow/README.md.
/// </summary>
public sealed class FeatureReadyTests
{
    /// <summary>What happens after the review, and what the feature is then: ready, or each
    /// condition alone refusing it, with its reason.</summary>
    public static TheoryData<string, string, string?> Cases() => new()
    {
        { "approved, CI passed", "ready", null },
        { "an older commit approved", "reviewing", FeatureReview.ToReview },
        { "changes", "waiting_for_owner", FeatureReview.ChangesAsked },
        { "CI pending", "waiting_for_owner", MergeCheck.CiRunning },
        { "CI failed", "waiting_for_owner", MergeCheck.CiFailed("test") },
        { "no CI yet", "waiting_for_owner", MergeCheck.CiNotYet },
        { "no CI 30 minutes after the push", "waiting_for_owner", MergeCheck.CiSilent },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ReadyWhenApprovedAndCiPassed(string how, string state, string? reason)
    {
        await using var jobs = await Ending.Feature();
        var head = await Reviewing.Published(jobs);
        await Reviewing.Review(jobs, Built.Verdict(how == "changes" ? "changes" : "approve"));

        switch (how)
        {
            case "an older commit approved":
                // I push to the feature's branch myself after the approve, which the watch finds
                // fine; CI passes on my commit, but no review approved it.
                var mine = await Reviewing.PushOnFeature(jobs);
                Merged.Ci(jobs, mine, Merged.GreenRuns, Merged.GreenStatuses);
                break;
            case "CI pending":
                Merged.Ci(jobs, head, [new CheckRun("test", CheckStatus.InProgress, null, "github-actions")]);
                break;
            case "CI failed":
                Merged.Ci(jobs, head, [new CheckRun("test", CheckStatus.Completed, CheckConclusion.Failure, "github-actions")], Merged.GreenStatuses);
                break;
            case "no CI yet":
                break;
            case "no CI 30 minutes after the push":
                jobs.Routes.Time.Advance(TimeSpan.FromMinutes(30));
                break;
            default:
                Merged.Ci(jobs, head, Merged.GreenRuns, Merged.GreenStatuses);
                break;
        }

        await Reviewing.Minute(jobs);

        var row = await Ending.Row(jobs);
        Assert.Equal((state, reason), (row.State, row.Reason));
        if (how == "an older commit approved")
        {
            // Its head is reviewed again: the next job is the feature's review of my commit.
            var job = await jobs.Job();
            Assert.Equal(Job.FeatureReview, (string)job["kind"]!);
            Assert.Equal(await Reviewing.Head(jobs), (string)job["to_review_feature"]!["commit"]!);
        }
        else
        {
            // Nothing is reviewed again.
            Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
            Assert.Single(await Reviewing.Reviews(jobs));
        }
    }

    [Fact]
    public async Task ACleanBringingInNeedsNoNewReview()
    {
        await using var jobs = await Ending.Feature();
        var approved = await Reviewing.Published(jobs);
        await Reviewing.Review(jobs, Built.Verdict("approve"));
        Merged.Ci(jobs, approved, Merged.GreenRuns, Merged.GreenStatuses);
        await Reviewing.Minute(jobs);
        Assert.Equal("ready", (await Ending.Row(jobs)).State);

        // main moves on while the feature waits for me, and Dev flow brings it in cleanly: a new
        // head, a merge Dev flow recorded, whose first parent is the approved commit.
        await Ending.MoveMain(jobs, "Main moves on again");
        await Ending.ReadAndEnd(jobs);
        await Reviewing.Minute(jobs);
        var head = await Reviewing.Head(jobs);
        Assert.NotEqual(approved, head);
        Assert.Equal(2, Ending.BringIns(jobs).Count);

        // CI has not reported on it yet: it waits, and no new review is asked for.
        Assert.Equal(("waiting_for_owner", MergeCheck.CiNotYet), ((await Ending.Row(jobs)).State, (await Ending.Row(jobs)).Reason));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        // Once CI passed on the new head, it is ready, still on the one review.
        Merged.Ci(jobs, head, Merged.GreenRuns, Merged.GreenStatuses);
        await Reviewing.Minute(jobs);
        Assert.Equal(("ready", (string?)null), ((await Ending.Row(jobs)).State, (await Ending.Row(jobs)).Reason));
        Assert.Single(await Reviewing.Reviews(jobs));
        Assert.Single(await Reviewing.Runs(jobs, Job.FeatureReview));
    }

    [Fact]
    public async Task AResolvedConflictNeedsANewReview()
    {
        await using var jobs = await Ending.Feature();
        var approved = await Reviewing.Published(jobs);
        await Reviewing.Review(jobs, Built.Verdict("approve"));
        Merged.Ci(jobs, approved, Merged.GreenRuns, Merged.GreenStatuses);
        await Reviewing.Minute(jobs);
        Assert.Equal("ready", (await Ending.Row(jobs)).State);

        // main moves on and conflicts with the feature's branch: an agent resolves it, a merge
        // commit the worker pushed, with its remerge diff.
        await Ending.MoveMain(jobs, "Main moves on again");
        Merged.Change(jobs, repository => repository with { Conflicts = [Ending.Branch] });
        await Ending.ReadAndEnd(jobs);
        Assert.Equal("resolving", (await Ending.Row(jobs)).State);
        await Reviewing.ReadAgain(jobs);
        const string Remerge = "diff --git a/README.md b/README.md\nremerge CONFLICT (content): Merge conflict in README.md\n";
        var resolution = Built.Prepared();
        resolution["remerge_diff"] = Remerge;
        var resolved = await Built.Run(jobs, resolution);
        Assert.Equal("conflict", (string)resolved["kind"]!);

        // The pull request is in step again and the branch read: the resolved head needs a new
        // review, which reads the resolution apart, and CI passing on it alone is not ready.
        Merged.Ci(jobs, (string)resolved["head_commit"]!, Merged.GreenRuns, Merged.GreenStatuses);
        await Reviewing.Minute(jobs);
        Assert.Equal((string)resolved["head_commit"]!, await Reviewing.Head(jobs));
        Assert.Equal(("reviewing", FeatureReview.ToReview), ((await Ending.Row(jobs)).State, (await Ending.Row(jobs)).Reason));
        var job = await jobs.Job();
        Assert.Equal(Job.FeatureReview, (string)job["kind"]!);
        Assert.Equal((string)resolved["head_commit"]!, (string)job["to_review_feature"]!["commit"]!);
        Assert.Contains(Remerge, (string)job["to_review_feature"]!["remerge_diff"]!, StringComparison.Ordinal);
    }
}