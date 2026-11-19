using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Merging;
using DevFlow.Reading;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Merging;

/// <summary>
/// What the project's setting does with a decision the agent marked for the owner: with
/// <c>never</c> the card merges and Dev flow opens one issue per decision, as the app, with the
/// attention label, which "Waiting for you" lists until he closes it on GitHub; with <c>always</c>
/// the card waits for him. See "The merge check" in devflow/README.md.
/// </summary>
public sealed class OwnerDecisionTests
{
    [Fact]
    public async Task WithNeverAMarkedDecisionMergesAndOpensAnIssue()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Merged.Approved(jobs);
        var number = await Merged.PullNumber(jobs);
        Assert.Single(Merged.AppMerges(jobs));

        // The fake worker opens the decisions after the merge, as their minute would; once more
        // opens nothing more.
        await Merged.OpenDecisions(jobs);
        await Merged.OpenDecisions(jobs);

        // One issue, for the one decision marked for the owner, by the app with a key for posting.
        var issue = Assert.Single(Merged.IssuesOpened(jobs));
        Assert.Equal(FakeCodeHost.AppLogin, issue.By);
        Assert.Equal("post", issue.Purpose);
        Assert.Equal($"Decision in #{number}: Show it on every list", issue.Title);
        var body = issue.Body!;
        Assert.Contains("Show it on every list", body, StringComparison.Ordinal);
        Assert.Contains("Only on habits", body, StringComparison.Ordinal);
        Assert.Contains("The lists look the same", body, StringComparison.Ordinal);
        Assert.Contains("Agreed: the lists share one component.", body, StringComparison.Ordinal);
        Assert.Contains($"https://github.com/Adamniels/Personal-platform/issues/{Merged.Card}", body, StringComparison.Ordinal);
        Assert.Contains($"https://github.com/Adamniels/Personal-platform/pull/{number}", body, StringComparison.Ordinal);
        // The decision that was not marked has no issue.
        Assert.DoesNotContain("A plain sentence", body, StringComparison.Ordinal);

        // It carries the project's attention label, in the world as GitHub would hold it.
        var opened = jobs.Fake.World(Jobs.Jobs.User).Installations[0].Repositories[0].Issues.Single(each => each.Number == issue.Number);
        Assert.Equal(["devflow-attention"], opened.Labels);
        Assert.True(opened.Open);
    }

    [Fact]
    public async Task ADecisionIsTitledInEightyCharactersAndKeptAsData()
    {
        await using var jobs = await Jobs.Jobs.Start();
        var report = Built.Report();
        var chosen = "Close #9 and tell @mallory, then " + new string('x', 120);
        report["decisions"]!.AsArray()[1]!["chosen"] = chosen;
        var built = await Built.Run(jobs, Built.Prepared(report));
        Merged.Ci(jobs, (string)built["head_commit"]!, Merged.GreenRuns);
        await Built.Review(jobs, Built.Verdict("approve"));
        await Merged.OpenDecisions(jobs);

        var issue = Assert.Single(Merged.IssuesOpened(jobs));
        var prefix = $"Decision in #{await Merged.PullNumber(jobs)}: ";
        Assert.StartsWith(prefix, issue.Title, StringComparison.Ordinal);
        Assert.Equal(80, issue.Title!.Length - prefix.Length);
        // The agent's words close no issue and notify nobody.
        Assert.DoesNotContain("Close #9", issue.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(" @mallory", issue.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAlwaysTheCardWaits()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Merged.Waits(jobs, "always");
        await Merged.Approved(jobs);
        await Merged.Check(jobs);

        var card = await Built.Card(jobs, Merged.Card);
        Assert.Equal("waiting_for_owner", card.State);
        Assert.Equal(MergeCheck.Always, card.Reason);
        Assert.Empty(Merged.AppMerges(jobs));
        await Merged.OpenDecisions(jobs);
        Assert.Empty(Merged.IssuesOpened(jobs));

        // On Waiting for you, with its reason in words, and Merge offered, as his own merge.
        var (status, body) = await Built.Get(jobs, DevFlow.Tests.Reading.Routes.WaitingPath);
        Assert.Equal(StatusCodes.Status200OK, status);
        var listed = Assert.Single(body!["cards"]!.AsArray())!;
        Assert.Equal(Merged.Card, (int)listed["number"]!);
        Assert.Equal(MergeCheck.Always, (string)listed["reason"]!);
        Assert.True((bool)listed["can_merge"]!);
    }

    [Fact]
    public async Task AttentionIssuesAreOnTheList()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Merged.Approved(jobs);
        var number = await Merged.PullNumber(jobs);
        await Merged.OpenDecisions(jobs);
        var issue = Assert.Single(Merged.IssuesOpened(jobs));

        // Under Decisions to look at, with what was decided and where, and counted.
        var (_, body) = await Built.Get(jobs, DevFlow.Tests.Reading.Routes.WaitingPath);
        var decision = Assert.Single(body!["decisions"]!.AsArray())!;
        Assert.Equal(issue.Number, (int)decision["issue_number"]!);
        Assert.Equal($"https://github.com/Adamniels/Personal-platform/issues/{issue.Number}", (string)decision["issue_url"]!);
        Assert.Equal(Merged.Card, (int)decision["card_number"]!);
        Assert.Equal(number, (int)decision["pull_request_number"]!);
        Assert.Equal("Show it on every list", (string)decision["chosen"]!);
        Assert.Equal("Only on habits", (string)decision["alternative"]!);
        Assert.Equal("The lists look the same", (string)decision["why"]!);
        Assert.Equal("Agreed: the lists share one component.", (string)decision["reviewer_view"]!);
        Assert.Equal("Personal-platform", (string)decision["project_name"]!);
        Assert.Empty(body["cards"]!.AsArray());
        Assert.Equal(1, (int)body["count"]!);

        // Still there while it is open, and gone once he closes it on GitHub and the project is read.
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User));
        Assert.Single((await Built.Get(jobs, DevFlow.Tests.Reading.Routes.WaitingPath)).Body!["decisions"]!.AsArray());
        Merged.Issue(jobs, issue.Number, each => each with { Open = false });
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User));
        var after = (await Built.Get(jobs, DevFlow.Tests.Reading.Routes.WaitingPath)).Body!;
        Assert.Empty(after["decisions"]!.AsArray());
        Assert.Equal(0, (int)after["count"]!);
    }

    [Fact]
    public async Task ADecisionInTheBuildAndOneInAFixEachGetAnIssue()
    {
        await using var jobs = await Jobs.Jobs.Start();
        // Built with its decision for the owner, and sent back by the reviewer.
        await Fixing.Fixed.BuiltAndReviewed(jobs);
        // The fix's report names a decision of its own, and not the build's.
        var report = Fixing.Fixed.Report();
        report["decisions"] = new JsonArray(new JsonObject
        {
            ["chosen"] = "Keep the list's order",
            ["alternative"] = "Sort it by name",
            ["why"] = "The owner orders them himself",
            ["for_owner"] = true,
        });
        var fixedUp = await Fixing.Fixed.Fix(jobs, Built.Prepared(report));
        Merged.Ci(jobs, (string)fixedUp["head_commit"]!, Merged.GreenRuns);
        await Built.Review(jobs, Built.Verdict("approve"));

        Assert.Single(Merged.AppMerges(jobs));
        var titles = Merged.IssuesOpened(jobs).Select(issue => issue.Title).ToList();
        var number = await Merged.PullNumber(jobs);
        Assert.Equal([$"Decision in #{number}: Show it on every list", $"Decision in #{number}: Keep the list's order"], titles);
    }

    [Fact]
    public async Task ADecisionToLookAtLightsTheDotAndCounts()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await Merged.Approved(jobs);

        var (_, attention) = await Built.Get(jobs, "/devflow/api/attention");
        Assert.True((bool)attention!["attention"]!);
        var (_, display) = await Built.Get(jobs, "/devflow/api/display");
        Assert.Equal(1, (int)display!["waiting"]!);
    }

    [Fact]
    public async Task AnIssueIsOpenedOnceWhenAnAnswerWasLost()
    {
        await using var jobs = await Jobs.Jobs.Start();

        // Opened on GitHub as the card merged, but the answer never came back: the next minute
        // finds it and opens no second.
        jobs.Routes.Host.OpeningIssueLost = true;
        await Merged.Approved(jobs);
        Assert.Single(Merged.IssuesOpened(jobs));
        jobs.Routes.Host.OpeningIssueLost = false;
        await Merged.OpenDecisions(jobs);

        Assert.Single(Merged.IssuesOpened(jobs));
        var (_, body) = await Built.Get(jobs, DevFlow.Tests.Reading.Routes.WaitingPath);
        Assert.Single(body!["decisions"]!.AsArray());
    }
}