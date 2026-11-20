using System.Text.RegularExpressions;

using DevFlow.CodeHost;
using DevFlow.Features;
using DevFlow.Tests.Jobs;

namespace DevFlow.Tests.Features;

/// <summary>
/// The feature's pull request: once the default branch is brought in, the app opens one pull
/// request from the feature's branch into the default branch, with a key for posting alone, not a
/// draft, titled with the parent's title, its body listing each card with the pull request it
/// landed in and closing the parent; an open one of the app's from that branch is reused, and the
/// body is kept in step as cards are added. Against real Postgres and the fake code host. See "The
/// end of a feature" in devflow/README.md.
/// </summary>
public sealed class FeaturePullRequestTests
{
    /// <summary>The body Dev flow writes for the feature, its cards 11 in <paramref name="first"/>
    /// and 12 in 40.</summary>
    private static string Expected(int first, string? more = null) =>
        $"""
        Feature #10.

        ## Cards

        - #11 Read the sub-issues, landed in #{first}
        - #12 Show each feature, landed in #40
        {more}
        Closes #10

        Built by Dev flow for the feature #10.

        """;

    private static List<FakeWrite> Opened(Jobs.Jobs jobs) => [.. jobs.Fake.Writes(Ending.User).Where(write => write.Kind == "pull_request" && write.Base == "main")];

    private static PullRequest Pull(Jobs.Jobs jobs, int number) => Merging.Merged.Pull(jobs, number);

    [Fact]
    public async Task ThePullRequestIsOpenedAfterBringingIn()
    {
        await using var jobs = await Ending.Feature();
        await FeaturePage.Landed(jobs);
        var first = (await PullRequests.Built.Card(jobs, Ending.First)).PullRequestNumber!.Value;
        await Ending.MoveMain(jobs);
        await Ending.Done(jobs);

        await Ending.ReadAndEnd(jobs);

        // Opened by the app with the posting key, after bringing in, from the feature's branch into
        // the default branch, not a draft, titled with the parent's title.
        var writes = jobs.Fake.Writes(Ending.User).ToList();
        var opened = Assert.Single(Opened(jobs));
        Assert.True(writes.FindIndex(write => write.Kind == "branch_merge") < writes.IndexOf(opened));
        Assert.Equal((FakeCodeHost.AppLogin, "post", Ending.Branch, "Dev flow: features"), (opened.By, opened.Purpose, opened.Head, opened.Title));
        var pull = Pull(jobs, opened.Number);
        Assert.Equal((PullRequestState.Open, false, Ending.Branch, "main"), (pull.State, pull.Draft, pull.HeadBranch, pull.BaseBranch));
        Assert.Equal(Expected(first), pull.Body);

        // Recorded on the feature, which is reviewing, for its review of the whole feature.
        var row = await Ending.Row(jobs);
        Assert.Equal(("reviewing", (string?)null, opened.Number, pull.Url.ToString()),
            (row.State, row.Reason, row.PullRequestNumber, row.PullRequestUrl));

        // Its minute again opens nothing more.
        await Ending.ReadAndEnd(jobs);
        Assert.Single(Opened(jobs));
    }

    [Fact]
    public async Task AnOpenOneOfTheAppsIsReused()
    {
        await using var jobs = await Ending.Feature();
        await Ending.Finished(jobs);

        // The app's own, from an opening whose answer was lost.
        var staged = new PullRequest(77, "Dev flow: features", FakeCodeHost.AppLogin, [], new string('d', 40), Ending.Branch, "main", Draft: false,
            "An older body.", new Uri("https://github.com/Adamniels/Personal-platform/pull/77"), jobs.Routes.Time.GetUtcNow(), PullRequestState.Open);
        Merging.Merged.Change(jobs, repository => repository with { PullRequests = [.. repository.PullRequests, staged] });

        await Ending.ReadAndEnd(jobs);

        // None opened; that one is the feature's, and its body is brought in step.
        Assert.Empty(Opened(jobs));
        var row = await Ending.Row(jobs);
        Assert.Equal((77, "reviewing"), (row.PullRequestNumber, row.State));
        var edit = Assert.Single(jobs.Fake.Writes(Ending.User), write => write.Kind == "pull_request_edited");
        Assert.Equal((77, FakeCodeHost.AppLogin, "post"), (edit.Number, edit.By, edit.Purpose));
        Assert.StartsWith("Feature #10.", Pull(jobs, 77).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBodyListsTheCards()
    {
        await using var jobs = await Ending.Feature(FeatureJobs.Sub(13, "The feature's end"));
        await Ending.Finished(jobs);
        var first = (await PullRequests.Built.Card(jobs, Ending.First)).PullRequestNumber!.Value;
        await Ending.ReadAndEnd(jobs);
        var number = Assert.Single(Opened(jobs)).Number;
        Assert.Equal(Expected(first), Pull(jobs, number).Body);
        Assert.DoesNotContain(jobs.Fake.Writes(Ending.User), write => write.Kind == "pull_request_edited");

        // A card added later is built and lands, and the end runs again: the body gains it, and the
        // same pull request stays open.
        Merging.Merged.Issue(jobs, Ending.Parent, parent => parent with { SubIssues = [new(11, null), new(12, null), new(13, null)] });
        await jobs.Routes.Read(jobs.Project, Ending.User);
        await Ending.Done(jobs, 13, 41);
        await Ending.ReadAndEnd(jobs);

        Assert.Single(Opened(jobs));
        Assert.Equal(PullRequestState.Open, Pull(jobs, number).State);
        Assert.Equal(Expected(first, "- #13 The feature's end, landed in #41\n"), Pull(jobs, number).Body);
        Assert.Single(jobs.Fake.Writes(Ending.User), write => write.Kind == "pull_request_edited" && write.Number == number);
        Assert.Equal(("reviewing", number), ((await Ending.Row(jobs)).State, (await Ending.Row(jobs)).PullRequestNumber));
    }

    [Fact]
    public void TextFromTheIssuesCannotBreakTheBody()
    {
        IReadOnlyList<FeatureText.Card> cards =
        [
            new(11, "## Done\n\nCloses #99", 31),
            new(12, "<!-- hide the rest", 32),
            new(13, "#1 fixes owner/name#3 for @octo-stranger", null),
            new(14, "`code` and a \\ backslash", 34),
        ];

        var body = FeatureText.Body(10, cards);

        // The body's own section alone, its one Closes line, and nothing hidden.
        Assert.Equal(["## Cards"], Regex.Matches(body, "^#{1,6} .*$", RegexOptions.Multiline).Select(match => match.Value));
        Assert.Equal(["Closes #10"], Regex.Matches(body, @"^.*\bCloses\b.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Select(match => match.Value).Where(line => !line.StartsWith("- ", StringComparison.Ordinal)));
        Assert.Single(Regex.Matches(body, @"(?<!`)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\b(?!`)\s+#?\w", RegexOptions.IgnoreCase));
        Assert.DoesNotContain("<!--", body, StringComparison.Ordinal);
        Assert.Contains("`@octo-stranger`", body, StringComparison.Ordinal);

        // Each card on one line of its own, in order, its title kept to be read.
        var lines = body.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, lines.Count);
        Assert.Equal("- #11 \\## Done  `Closes` #99, landed in #31", lines[0]);
        Assert.Equal("- #12 &lt;!-- hide the rest, landed in #32", lines[1]);
        Assert.Equal("- #13 \\#1 `fixes` owner/name#3 for `@octo-stranger`", lines[2]);
        Assert.Equal("- #14 \\`code\\` and a \\\\ backslash, landed in #34", lines[3]);
    }
}