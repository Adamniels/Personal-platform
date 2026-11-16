using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Runner;
using DevFlow.Tests.GitHub;

namespace DevFlow.Tests.Runner;

/// <summary>
/// The reader against real pull requests of this repository, recorded in
/// contracts/github/runner/, see contracts/README.md. Each is read through the GitHub adapter, as
/// the program will read it, then marked open, since all four have merged since.
/// </summary>
public sealed class RecordedTests
{
    [Fact]
    public async Task ADecisionPullRequestWaitsWithItsDecisionsAndTheReviewersView()
    {
        var item = await Read(267);

        Assert.NotNull(item);
        Assert.Equal(267, item.Number);
        Assert.Equal(260, item.Slice);
        Assert.Equal(RunnerItemKind.Waiting, item.Kind);
        Assert.Equal("681a2502b7d088f8e2c1e286fa7bfec5e4a0b376", item.HeadCommit);
        Assert.True(item.CanMerge);
        Assert.False(item.SentBack);
        Assert.Equal(new Uri("https://github.com/Adamniels/Personal-platform/pull/267"), item.Url);
        Assert.StartsWith("A pull request that changes what the hosted images are built from can no longer merge", item.Summary, StringComparison.Ordinal);

        // The decision marked for the owner, split, and without the mark.
        var decision = Assert.Single(item.OwnerDecisions);
        Assert.StartsWith("`hosted.yml` installs Node 26 and runs `npm ci` in `design/ui/`", decision.Chosen, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-decision", decision.Chosen, StringComparison.Ordinal);
        Assert.Equal("Node left out, as Settled says.", decision.Alternative);
        Assert.StartsWith("the acceptance suite `check.sh` runs injects", decision.Why, StringComparison.Ordinal);
        Assert.EndsWith("This goes against Settled, case 1 of step 8, hence `has-decision`.", decision.Why, StringComparison.Ordinal);
        Assert.Equal(8, item.OtherDecisions);

        // The reviewer's "For the owner".
        var view = Assert.Single(item.ReviewerView);
        Assert.StartsWith("**Node 26 and `npm ci` in `design/ui/`, in `hosted.yml`", view, StringComparison.Ordinal);
        Assert.EndsWith("skipping the accessibility tests in the hosted run weakens the suite.", view, StringComparison.Ordinal);

        Assert.Equal("Touches paths that steer the agents. High risk slice. Carries a decision for you.", item.Reason);
    }

    [Fact]
    public async Task AMergePullRequestWaitsWithTheRunnersReason()
    {
        var item = await Read(266);

        Assert.NotNull(item);
        Assert.Equal(259, item.Slice);
        Assert.Equal(RunnerItemKind.Waiting, item.Kind);
        Assert.True(item.CanMerge);
        Assert.Empty(item.OwnerDecisions);
        Assert.Equal(13, item.OtherDecisions);
        // The reviewer's section says None, with its reasons after it.
        Assert.Empty(item.ReviewerView);
        // From the handover mention, not the later comment on the slice that names no pull request.
        Assert.Equal("Touches paths that steer the agents. High risk slice.", item.Reason);
    }

    [Fact]
    public async Task ABlockedPullRequestIsStoppedWithTheRunnersReason()
    {
        var item = await Read(210);

        Assert.NotNull(item);
        Assert.Equal(167, item.Slice);
        Assert.Equal(RunnerItemKind.Stopped, item.Kind);
        // Its head has ci-ok but no review-ok, and a stopped item is never offered for merging.
        Assert.False(item.CanMerge);
        Assert.StartsWith("The owner can tell the brain what he knows.", item.Summary, StringComparison.Ordinal);
        Assert.Equal("The slice is not open.", item.Reason);
    }

    [Fact]
    public async Task APullRequestWithoutAnOwnerLabelIsNotMine()
    {
        Assert.Null(await Read(265));
    }

    /// <summary>
    /// The recorded pull request, read through the GitHub adapter from its files, with each verdict
    /// comment's first line put back from markers.json, and marked open.
    /// </summary>
    private static async Task<RunnerItem?> Read(int number)
    {
        var folder = Tests.Repository.Path("contracts", "github", "runner", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string File(string name) => System.IO.File.ReadAllText(Path.Combine(folder, name));

        var pullNode = JsonNode.Parse(File("pull.json"))!;
        var head = (string)pullNode["head"]!["sha"]!;
        var slice = RunnerReader.Slice((string)pullNode["body"]!) ?? throw new Xunit.Sdk.XunitException($"#{number} closes no slice");

        var repo = "/repos/Adamniels/Personal-platform";
        var answers = new Dictionary<string, string>
        {
            [$"{repo}/pulls/{number}"] = File("pull.json"),
            [$"{repo}/issues/{number}/comments"] = WithMarkers(File("comments.json"), File("markers.json")),
            [$"{repo}/commits/{head}/statuses"] = File("statuses.json"),
            [$"{repo}/commits/{head}/check-runs"] = File("check-runs.json"),
            [$"{repo}/issues/{slice}/comments"] = File("slice-comments.json"),
        };

        using var host = new GitHubHost();
        host.Stub.Answer = request => answers.TryGetValue(request.Path, out var body)
            ? StubGitHub.Json(body)
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        var ct = GitHubHost.Ct;
        var code = host.CodeHost;
        var pull = Results.Value(await code.PullRequest(GitHubHost.User, GitHubHost.Platform, number, ct));
        Assert.NotEqual(PullRequestState.Open, pull.State);
        var input = new RunnerInput(
            GitHubHost.Platform,
            pull with { State = PullRequestState.Open },
            Results.Value(await code.Comments(GitHubHost.User, GitHubHost.Platform, number, ct)),
            Results.Value(await code.Statuses(GitHubHost.User, GitHubHost.Platform, head, ct)),
            Results.Value(await code.CheckRuns(GitHubHost.User, GitHubHost.Platform, head, ct)),
            Results.Value(await code.Comments(GitHubHost.User, GitHubHost.Platform, slice, ct)),
            "mac-mini-agent",
            "Adamniels");
        return RunnerReader.Read(input);
    }

    /// <summary>The comments with each verdict's marker line put back as the first line of its body.</summary>
    private static string WithMarkers(string comments, string markers)
    {
        var list = JsonNode.Parse(comments)!.AsArray();
        foreach (var marker in JsonNode.Parse(markers)!.AsArray())
        {
            var id = (long)marker!["comment"]!;
            var comment = list.Single(node => (long)node!["id"]! == id)!;
            comment["body"] = Marker.Line((string)marker["kind"]!, (string)marker["sha"]!) + "\n" + (string)comment["body"]!;
        }

        return list.ToJsonString();
    }
}