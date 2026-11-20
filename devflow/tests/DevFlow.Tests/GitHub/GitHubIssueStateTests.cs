using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// Closing an issue and opening it again as the app, against GitHub's documented form, read on
/// 2026-11-20: <c>PATCH /repos/{owner}/{repo}/issues/{number}</c> with <c>state</c> and
/// <c>state_reason</c>, with a key the app made to post, answered <c>200</c> with the issue in the
/// shape of the recorded contracts/github/issue.json, which the adapter reads as it reads an issue
/// it opened. See "The merge check" in devflow/README.md.
/// </summary>
public sealed class GitHubIssueStateTests
{
    private const string Repo = "/repos/Adamniels/Personal-platform";

    private static CancellationToken Ct => GitHubHost.Ct;

    /// <summary>The recorded issue with <c>state</c> as GitHub would answer it after.</summary>
    private static string Answer(string state)
    {
        var issue = Fixture.Node("issue.json").AsObject();
        issue["state"] = state;
        return issue.ToJsonString();
    }

    [Fact]
    public async Task ClosingAndOpeningAnswerTheIssue()
    {
        using var host = new GitHubHost();
        var number = (int)Fixture.Node("issue.json")["number"]!;
        var states = new Queue<string>(["closed", "open"]);
        host.Stub.Answer = request => (request.Method.Method, request.Path) == ("PATCH", $"{Repo}/issues/{number}")
            ? StubGitHub.Json(Answer(states.Dequeue()))
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var post = Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, WritePurpose.Post, Ct));

        var closed = await host.CodeHost.SetIssueState(GitHubHost.User, post, GitHubHost.Platform, number, open: false, Ct);
        var opened = await host.CodeHost.SetIssueState(GitHubHost.User, post, GitHubHost.Platform, number, open: true, Ct);

        // Read as the adapter reads an issue it opened.
        var recorded = Fixture.Node("issue.json");
        foreach (var result in new[] { closed, opened })
        {
            var issue = Assert.IsType<WriteResult<Issue>.Written>(result).Value;
            Assert.Equal(number, issue.Number);
            Assert.Equal((string)recorded["title"]!, issue.Title);
            Assert.Equal((string)recorded["user"]!["login"]!, issue.Author);
            Assert.Equal(recorded["labels"]!.AsArray().Select(label => (string)label!["name"]!), issue.Labels);
            Assert.Equal((string)recorded["body"]!, issue.Body);
            Assert.Equal(new Uri((string)recorded["html_url"]!), issue.Url);
            Assert.Equal(DateTimeOffset.Parse((string)recorded["created_at"]!, CultureInfo.InvariantCulture), issue.CreatedAt);
            Assert.False(issue.IsPullRequest);
        }

        // The requests, as GitHub documents them, with the app's key.
        var reads = host.Stub.Reads;
        Assert.Equal(2, reads.Count);
        Assert.All(reads, request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal($"{Repo}/issues/{number}", request.Path);
            Assert.Equal($"Bearer {StubGitHub.FixtureToken}", request.Header("Authorization"));
            Assert.Equal("2022-11-28", request.Header("X-GitHub-Api-Version"));
        });
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["state"] = "closed", ["state_reason"] = "completed" }, JsonNode.Parse(reads[0].Body!)));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["state"] = "open", ["state_reason"] = "reopened" }, JsonNode.Parse(reads[1].Body!)));
        Assert.DoesNotContain(StubGitHub.FixtureToken, string.Join('\n', host.Logs.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatGitHubRefusesOrLeavesUnansweredIsSaid()
    {
        using var host = new GitHubHost();
        var post = Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, WritePurpose.Post, Ct));

        host.Stub.Answer = _ => StubGitHub.Json(new JsonObject { ["message"] = "Not Found" }.ToJsonString(), HttpStatusCode.NotFound);
        var refused = Assert.IsType<WriteResult<Issue>.Refused>(await host.CodeHost.SetIssueState(GitHubHost.User, post, GitHubHost.Platform, 9, open: false, Ct));
        Assert.Equal((404, "Not Found"), (refused.Status, refused.Message));

        host.Stub.Answer = _ => StubGitHub.Json("{\"number\":", HttpStatusCode.OK);
        Assert.IsType<WriteResult<Issue>.Unavailable>(await host.CodeHost.SetIssueState(GitHubHost.User, post, GitHubHost.Platform, 9, open: false, Ct));

        // A key for another purpose is refused before GitHub is asked.
        var merge = Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, WritePurpose.Merge, Ct));
        var asked = host.Stub.Reads.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => host.CodeHost.SetIssueState(GitHubHost.User, merge, GitHubHost.Platform, 9, open: false, Ct));
        Assert.Equal(asked, host.Stub.Reads.Count);
    }
}