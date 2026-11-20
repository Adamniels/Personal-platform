using System.Text.RegularExpressions;

using DevFlow.CodeHost;
using DevFlow.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A feature's branch: made by the app with a push key at the default branch's head when its
/// first card is about to be handed out, recorded on the feature, never taken when one of that
/// name is there already; each card of the feature built on its own branch from it, with its pull
/// request into it. Against the fake and a real Postgres. See "Jobs" and "The pull request Dev
/// flow opens" in devflow/README.md.
/// </summary>
public sealed partial class FeatureBranchTests
{
    private const string Branch = "feature-10-dev-flow-features";

    private static CancellationToken Ct => Jobs.Ct;

    private static Task<Jobs> Feature() => Jobs.Start([
        FeatureJobs.Parent(10, "Dev flow: features", [11, 12]),
        FeatureJobs.Sub(11, "Read the sub-issues"),
        FeatureJobs.Sub(12, "Show each feature"),
    ]);

    [Fact]
    public async Task TheFirstCardMakesTheBranch()
    {
        await using var jobs = await Feature();
        var head = await FeatureJobs.FirstCommit(jobs);
        Assert.Empty(FeatureJobs.Branches(jobs));

        var job = await jobs.Job();

        // One branch, made by the app with a push key, at the default branch's head.
        var made = Assert.Single(FeatureJobs.Branches(jobs));
        Assert.Equal(FakeCodeHost.AppLogin, made.By);
        Assert.Equal("push", made.Purpose);
        Assert.Equal(Branch, made.Branch);
        Assert.Equal(head, made.Commit);
        Assert.Equal(Reading.Reading.RepositoryId, made.Repository);
        Assert.True(await jobs.Fake.HasBranch(Jobs.User, FeatureJobs.Repo, Branch, Ct));

        // The feature records it and the commit, and is building.
        var feature = await FeatureJobs.Feature(jobs, 10);
        Assert.Equal("building", feature.State);
        Assert.Null(feature.Reason);
        Assert.Equal(Branch, feature.Branch);
        Assert.Equal(head, feature.BranchBaseCommit);
        Assert.Equal(11, (int)job["card"]!["number"]!);

        // The next card of it, once the first is done, makes no second branch.
        await FeatureJobs.Finish(jobs, 11, "done");
        Assert.Equal(12, (int)(await jobs.Job())["card"]!["number"]!);
        Assert.Single(FeatureJobs.Branches(jobs));
    }

    [Fact]
    public async Task ABranchDevFlowDidNotMakeIsNotTaken()
    {
        await using var jobs = await Feature();
        await FeatureJobs.FirstCommit(jobs);
        // Someone else's branch of the very name, which the feature has no record of.
        await jobs.Fake.CommitOnBranch(Jobs.User, FeatureJobs.Repo, Branch, "main", "Someone's work", new Committer("someone", "someone@example.com"), Ct);

        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);

        var feature = await FeatureJobs.Feature(jobs, 10);
        Assert.Equal("waiting_for_owner", feature.State);
        Assert.Equal($"A branch named {Branch} already exists", feature.Reason);
        Assert.Null(feature.Branch);
        Assert.Null(feature.BranchBaseCommit);
        Assert.Empty(await jobs.Runs());
        Assert.Empty(FeatureJobs.Branches(jobs));

        // Asking again takes it no more.
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Equal("queued", (await jobs.Card(11)).State);
    }

    [Fact]
    public async Task TwoWorkersMakeOneBranch()
    {
        await using var jobs = await Feature();
        var door = jobs.Routes.Door;
        var now = jobs.Routes.Time.GetUtcNow();
        var due = await FeatureBranchStore.Due(door, Jobs.User, jobs.Project, now, Ct);
        Assert.NotNull(due);
        Assert.Equal(Branch, FeatureBranchStore.Name(due.Number, due.Title));

        // The first to claim it makes it; a second asking meanwhile does not ask GitHub at all.
        Assert.True(await FeatureBranchStore.Claim(door, Jobs.User, due.Id, Branch, now, Ct));
        Assert.False(await FeatureBranchStore.Claim(door, Jobs.User, due.Id, Branch, now.AddSeconds(30), Ct));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.Empty(FeatureJobs.Branches(jobs));

        // A claim whose maker never came back is taken over once it has lasted its time.
        Assert.True(await FeatureBranchStore.Claim(door, Jobs.User, due.Id, Branch, now + FeatureBranchStore.ClaimLasts + TimeSpan.FromSeconds(1), Ct));

        // Recorded, it is made: nothing is due, and nothing claims it again.
        await FeatureBranchStore.Made(door, Jobs.User, due.Id, Branch, new string('c', 40), now, Ct);
        Assert.Null(await FeatureBranchStore.Due(door, Jobs.User, jobs.Project, now, Ct));
        Assert.False(await FeatureBranchStore.Claim(door, Jobs.User, due.Id, Branch, now.AddHours(1), Ct));
        var feature = await FeatureJobs.Feature(jobs, 10);
        Assert.Equal(("building", Branch, new string('c', 40)), (feature.State, feature.Branch, feature.BranchBaseCommit));
    }

    [Fact]
    public async Task ACardIsBuiltFromTheFeaturesBranch()
    {
        await using var jobs = await Feature();
        await FeatureJobs.FirstCommit(jobs);

        var job = await jobs.Job();

        Assert.Equal(Branch, (string)job["base"]!);
        Assert.Equal("devflow/11-read-the-sub-issues", (string)job["branch"]!);
        Assert.Equal("main", (string)job["repository"]!["default_branch"]!);
        Assert.Contains($"You are on the branch `devflow/11-read-the-sub-issues`, made from `{Branch}`", (string)job["brief"]!, StringComparison.Ordinal);
        var run = Assert.Single(await jobs.Runs());
        Assert.Equal(Branch, run.Base);
        Assert.Equal("devflow/11-read-the-sub-issues", run.Branch);
    }

    [Fact]
    public async Task ItsPullRequestGoesIntoTheFeaturesBranch()
    {
        await using var jobs = await Feature();
        await FeatureJobs.FirstCommit(jobs);

        var ran = await Built.Run(jobs, Built.Prepared(Built.Report()));

        var opened = Assert.Single(Built.Opened(jobs));
        Assert.Equal(FakeCodeHost.AppLogin, opened.By);
        Assert.Equal("devflow/11-read-the-sub-issues", opened.Head);
        Assert.Equal(Branch, opened.Base);
        Assert.Equal("Read the sub-issues", opened.Title);

        var body = opened.Body!;
        Assert.Contains("\n\nCard #11 of the feature #10.\n\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Closes", body, StringComparison.Ordinal);
        Assert.Equal(
            ["## Summary", "## Tasks", "## Decisions the agent made", "## Verification", "## Follow ups"],
            Headings().Matches(body).Select(match => match.Value));
        var run = Assert.Single(await jobs.Runs());
        Assert.EndsWith($"Built by Dev flow for #11, run {run.Id}.\n", body, StringComparison.Ordinal);
        // The rest of it is a card's on its own, line for line, but for the Summary's last line.
        Assert.Equal(PullRequestTests.Body(11, (string)ran["head_commit"]!, run.Id).Replace("Closes #11", "Card #11 of the feature #10.", StringComparison.Ordinal), body);
        Assert.Equal("reviewing", (await Built.Card(jobs, 11)).State);
    }

    [GeneratedRegex("^## .*$", RegexOptions.Multiline)]
    private static partial Regex Headings();
}