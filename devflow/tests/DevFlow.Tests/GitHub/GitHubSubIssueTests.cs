using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The adapter's read of an issue's sub-issues, which tells a feature from a card, against
/// GitHub's real answer recorded in contracts/github/sub-issues.json: each field of the code host's
/// record is what the file holds, read here by the test on its own, in GitHub's order. See
/// "Features and their cards" in devflow/docs/devflow-architecture.md and contracts/README.md.
/// </summary>
public sealed class GitHubSubIssueTests
{
    private const string Path = "/repos/Adamniels/Personal-platform/issues/330/sub_issues";

    private static CancellationToken Ct => GitHubHost.Ct;

    [Fact]
    public async Task SubIssuesAreReadInTheirOrder()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => request.Path == Path
            ? StubGitHub.Json(Fixture.Text("sub-issues.json"))
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        var subIssues = Results.Value(await host.CodeHost.SubIssues(GitHubHost.User, GitHubHost.Platform, 330, Ct));

        // One GET, as the app, a hundred a page.
        var asked = Assert.Single(host.Stub.Reads);
        Assert.Equal(HttpMethod.Get, asked.Method);
        Assert.Equal("100", asked.Query["per_page"]);
        Assert.Equal($"Bearer {StubGitHub.FixtureToken}", asked.Header("Authorization"));

        var expected = Fixture.Node("sub-issues.json").AsArray();
        Assert.Equal(expected.Count, subIssues.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var file = expected[i]!;
            var subIssue = subIssues[i];
            Assert.Equal((int)file["number"]!, subIssue.Number);
            Assert.Equal((string)file["title"]!, subIssue.Title);
            Assert.Equal((string?)file["body"] ?? "", subIssue.Body);
            Assert.Equal((string)file["state"]! == "open", subIssue.Open);
            Assert.Equal((string)file["user"]!["login"]!, subIssue.Author);
            Assert.Equal(file["labels"]!.AsArray().Select(label => (string)label!["name"]!), subIssue.Labels);
            Assert.Equal(Time(file["created_at"]), subIssue.CreatedAt);
            Assert.Equal((string)file["repository"]!["owner"]!["login"]!, subIssue.Owner);
            Assert.Equal((string)file["repository"]!["name"]!, subIssue.Name);
            Assert.Equal(new Uri((string)file["html_url"]!), subIssue.Url);
            Assert.Equal(file.AsObject().ContainsKey("pull_request"), subIssue.IsPullRequest);
        }

        // GitHub's order is the feature's, not the numbers'.
        Assert.Equal(expected.Select(file => (int)file!["number"]!), subIssues.Select(subIssue => subIssue.Number));
        Assert.All(subIssues, subIssue => Assert.Equal(("Adamniels", "Personal-platform"), (subIssue.Owner, subIssue.Name)));
    }

    [Fact]
    public async Task AnIssueGitHubDoesNotShowIsNotFoundAndAFailureUnavailable()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.IsType<CodeHostResult<IReadOnlyList<SubIssue>>.NotFound>(
            await host.CodeHost.SubIssues(GitHubHost.User, GitHubHost.Platform, 99999, Ct));

        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.IsType<CodeHostResult<IReadOnlyList<SubIssue>>.Unavailable>(
            await host.CodeHost.SubIssues(GitHubHost.User, GitHubHost.Platform, 330, Ct));
    }

    private static DateTimeOffset Time(JsonNode? value) =>
        DateTimeOffset.Parse((string)value!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}