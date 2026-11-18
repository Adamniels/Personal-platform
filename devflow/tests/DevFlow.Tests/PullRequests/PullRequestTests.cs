using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DevFlow.CodeHost;
using DevFlow.PullRequests;
using DevFlow.Reading;

using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.PullRequests;

/// <summary>
/// A card that built and passed its check gets its pull request, opened as the app from the card's
/// branch to the default branch, with a body in the layout the owner reads, and the card follows it
/// to merged or closed. See "The pull request Dev flow opens" in devflow/README.md.
/// </summary>
public sealed partial class PullRequestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ABuiltCardGetsItsPullRequest()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state")]);

        var ran = await Built.Run(jobs, Built.Prepared(Built.Report()));

        var opened = Assert.Single(Built.Opened(jobs));
        Assert.Equal(FakeCodeHost.AppLogin, opened.By);
        Assert.Equal("post", opened.Purpose);
        Assert.Equal("devflow/4-add-the-empty-state", opened.Head);
        Assert.Equal((string)ran["branch"]!, opened.Head);
        Assert.Equal("main", opened.Base);
        Assert.Equal("Add the empty state", opened.Title);
        var run = Assert.Single(await jobs.Runs());
        Assert.Equal(Body(4, (string)ran["head_commit"]!, run.Id), opened.Body);

        // Not a draft, from the branch's commit.
        var pull = Assert.Single(Assert.Single(Assert.Single(jobs.Fake.World(Jobs.Jobs.User).Installations).Repositories).PullRequests);
        Assert.False(pull.Draft);
        Assert.Equal((string)ran["head_commit"]!, pull.HeadCommit);

        var card = await Built.Card(jobs, 4);
        Assert.Equal("waiting_for_owner", card.State);
        Assert.Equal(opened.Number, card.PullRequestNumber);
        Assert.Equal(pull.Url.ToString(), card.PullRequestUrl);
    }

    [Fact]
    public async Task AResultThatIsNotAllThreeOpensNothing()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4), Jobs.Jobs.Card(5), Jobs.Jobs.Card(6)]);

        await Built.Run(jobs, Built.Prepared(checkExit: 1));
        await Built.Run(jobs, Built.Prepared(Jobs.Jobs.GoodReport("stopped", "The card contradicts itself")));
        var invalid = Jobs.Jobs.GoodReport();
        invalid.Remove("summary");
        await Built.Run(jobs, Built.Prepared(invalid));

        Assert.Empty(Built.Opened(jobs));
        Assert.All(await jobs.Cards(), card => Assert.Equal("stopped", card.State));
    }

    [Fact]
    public async Task TheBodyIsTheTemplatesLayout()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state")]);

        var ran = await Built.Run(jobs, Built.Prepared(Built.Report()));

        var body = Assert.Single(Built.Opened(jobs)).Body!;
        var run = Assert.Single(await jobs.Runs());
        var head = (string)ran["head_commit"]!;
        Assert.Equal(
            ["## Summary", "## Tasks", "## Decisions the agent made", "## Verification", "## Follow ups"],
            Headings(body));
        Assert.Contains("\n\nCloses #4\n\n", body, StringComparison.Ordinal);
        Assert.Contains("- [x] Add the empty state\n- [ ] Count the habits\n", body, StringComparison.Ordinal);
        Assert.Contains("- A plain sentence. Alternative: An illustration. Why: Nothing else on the page is drawn.\n", body, StringComparison.Ordinal);
        Assert.Contains("- **For you.** Show it on every list. Alternative: Only on habits. Why: The lists look the same.\n", body, StringComparison.Ordinal);
        Assert.Contains($"Dev flow ran `npm ci && npm test` on {head[..7]}: exit 0.\n\n- list.test.tsx: passed\n", body, StringComparison.Ordinal);
        Assert.Contains("## Follow ups\n\n- Count the habits in the title\n", body, StringComparison.Ordinal);
        Assert.EndsWith($"Built by Dev flow for #4, run {run.Id}.\n", body, StringComparison.Ordinal);
        Assert.Equal(Body(4, head, run.Id), body);
    }

    [Fact]
    public async Task EmptyListsSayNone()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        var report = Jobs.Jobs.GoodReport();
        report["decisions"] = new JsonArray();
        report["follow_ups"] = new JsonArray();

        await Built.Run(jobs, Built.Prepared(report));

        var body = Assert.Single(Built.Opened(jobs)).Body!;
        Assert.Contains("## Decisions the agent made\n\nNone.\n", body, StringComparison.Ordinal);
        Assert.Contains("## Follow ups\n\nNone.\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThatIsRetriedDoesNotOpenASecond()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state")]);
        // A pull request the app opened for the branch already, as one whose answer was lost would
        // leave it.
        var url = new Uri("https://github.com/Adamniels/Personal-platform/pull/77");
        Built.Stage(jobs, new PullRequest(77, "Add the empty state", FakeCodeHost.AppLogin, [], new string('e', 40),
            "devflow/4-add-the-empty-state", "main", false, "", url, jobs.Routes.Time.GetUtcNow(), PullRequestState.Open));

        await Built.Run(jobs, Built.Prepared());

        Assert.Empty(Built.Opened(jobs));
        var card = await Built.Card(jobs, 4);
        Assert.Equal("waiting_for_owner", card.State);
        Assert.Equal(77, card.PullRequestNumber);
        Assert.Equal(url.ToString(), card.PullRequestUrl);
    }

    [Fact]
    public async Task AStrangersPullRequestFromTheSameBranchIsNotReused()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4, "Add the empty state")]);
        Built.Stage(jobs, new PullRequest(77, "Something else", "octo-stranger", [], new string('e', 40),
            "devflow/4-add-the-empty-state", "main", false, "", new Uri("https://github.com/Adamniels/Personal-platform/pull/77"),
            jobs.Routes.Time.GetUtcNow(), PullRequestState.Open));

        await Built.Run(jobs, Built.Prepared());

        // Someone else's pull request is never taken for the card's, and GitHub refuses a second
        // from the same branch, so the card waits, built, with none, and is tried again.
        Assert.Empty(Built.Opened(jobs));
        var card = await Built.Card(jobs, 4);
        Assert.Equal("checking", card.State);
        Assert.Null(card.PullRequestNumber);
    }

    [Fact]
    public async Task APullRequestThatCouldNotBeOpenedIsOpenedLater()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4)]);
        jobs.Routes.Host.OpeningUnavailable = true;

        var ran = await Built.Run(jobs, Built.Prepared());

        // GitHub did not answer, so the card is left built and checked, and nothing is opened.
        Assert.Empty(Built.Opened(jobs));
        Assert.Equal("checking", (await Built.Card(jobs, 4)).State);

        // The opening runs again on the project's minute, and opens it once, however often it runs.
        jobs.Routes.Host.OpeningUnavailable = false;
        await Opening(jobs);
        await Opening(jobs);

        var opened = Assert.Single(Built.Opened(jobs));
        Assert.Equal((string)ran["branch"]!, opened.Head);
        var card = await Built.Card(jobs, 4);
        Assert.Equal("waiting_for_owner", card.State);
        Assert.Equal(opened.Number, card.PullRequestNumber);
    }

    [Fact]
    public async Task AMergedPullRequestMakesTheCardDone()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(4), Jobs.Jobs.Card(5)]);
        await Built.Run(jobs, Built.Prepared());
        await Built.Run(jobs, Built.Prepared());
        var four = (await Built.Card(jobs, 4)).PullRequestNumber!.Value;
        var five = (await Built.Card(jobs, 5)).PullRequestNumber!.Value;

        // Merged by the owner on GitHub, which closes the card's issue with its Closes line; and
        // closed without merging.
        Built.OnGitHub(jobs, four, PullRequestState.Merged, closeIssue: 4);
        Built.OnGitHub(jobs, five, PullRequestState.Closed);
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User));

        var done = await Built.Card(jobs, 4);
        Assert.Equal("done", done.State);
        var stopped = await Built.Card(jobs, 5);
        Assert.Equal("stopped", stopped.State);
        Assert.Equal("The pull request was closed", stopped.Reason);

        // A done card is its story, so it stays when its closed issue is no longer listed.
        Assert.Equal(ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User));
        Assert.Equal("done", (await Built.Card(jobs, 4)).State);
    }

    /// <summary>The opening of pull requests, run once for the project, as its loop does.</summary>
    private static Task Opening(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<PullRequestOpener>().OpenPending(jobs.Project, Jobs.Jobs.User, Ct);

    /// <summary>The body the report of <see cref="Built.Report"/> gives card 4, built on
    /// <paramref name="head"/> by <paramref name="run"/>.</summary>
    private static string Body(int card, string head, Guid run) => $"""
        ## Summary

        Added the empty state to the habits list.

        Closes #{card}

        ## Tasks

        - [x] Add the empty state
        - [ ] Count the habits

        ## Decisions the agent made

        - A plain sentence. Alternative: An illustration. Why: Nothing else on the page is drawn.
        - **For you.** Show it on every list. Alternative: Only on habits. Why: The lists look the same.

        ## Verification

        Dev flow ran `npm ci && npm test` on {head[..7]}: exit 0.

        - list.test.tsx: passed

        ## Follow ups

        - Count the habits in the title

        Built by Dev flow for #{card}, run {run}.

        """;

    /// <summary>Every line GitHub would read as a heading.</summary>
    private static List<string> Headings(string body) =>
        [.. body.Split('\n').Where(line => HeadingLine().IsMatch(line))];

    [GeneratedRegex(@"^ {0,3}#{1,6}(\s|$)")]
    private static partial Regex HeadingLine();
}