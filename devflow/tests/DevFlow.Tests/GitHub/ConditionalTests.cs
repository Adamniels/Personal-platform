using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// Conditional requests: the adapter remembers, per installation and address, the last
/// <c>ETag</c> and what it read, sends <c>If-None-Match</c>, and on <c>304</c> answers what it had,
/// which GitHub does not count against the rate limit. See "GitHub" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class ConditionalTests
{
    private static CancellationToken Ct => GitHubHost.Ct;

    [Fact]
    public async Task AnUnchangedAnswerIsServedFromWhatWasRead()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => request.Header("If-None-Match") == "W/\"one\""
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Tagged(Fixture.Text("pull.json"), "W/\"one\"");

        var first = Results.Value(await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct));
        var second = Results.Value(await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct));

        Assert.Equal(2, host.Stub.Reads.Count);
        Assert.Null(host.Stub.Reads[0].Header("If-None-Match"));
        Assert.Equal("W/\"one\"", host.Stub.Reads[1].Header("If-None-Match"));
        Assert.Equal(Fingerprint(first), Fingerprint(second));
        Assert.Equal(305, second.Number);

        // A file read raw, the same way.
        host.Stub.Answer = request => request.Header("If-None-Match") == "\"file\""
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Tagged("hello", "\"file\"");
        var file = Results.Value(await host.CodeHost.File(GitHubHost.User, GitHubHost.Platform, "a.txt", "main", Ct));
        Assert.Equal(file, Results.Value(await host.CodeHost.File(GitHubHost.User, GitHubHost.Platform, "a.txt", "main", Ct)));
        Assert.Equal("\"file\"", host.Stub.Reads[^1].Header("If-None-Match"));
    }

    [Fact]
    public async Task AChangedAnswerReplacesWhatWasRead()
    {
        using var host = new GitHubHost();
        var pull = Fixture.Node("pull.json").AsObject();
        host.Stub.Answer = _ => Tagged(pull.ToJsonString(), "W/\"one\"");
        Assert.Equal((string)pull["title"]!, Results.Value(await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct)).Title);

        pull["title"] = "Renamed";
        host.Stub.Answer = request => request.Header("If-None-Match") == "W/\"two\""
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Tagged(pull.ToJsonString(), "W/\"two\"");

        Assert.Equal("Renamed", Results.Value(await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct)).Title);
        Assert.Equal("W/\"one\"", host.Stub.Reads[1].Header("If-None-Match"));

        // The new one is what is remembered now.
        Assert.Equal("Renamed", Results.Value(await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct)).Title);
        Assert.Equal("W/\"two\"", host.Stub.Reads[2].Header("If-None-Match"));
    }

    [Fact]
    public async Task WhatWasReadIsKeptPerInstallationAndAddress()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => request.Header("If-None-Match") is not null
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Tagged(Fixture.Text("pull.json"), "W/\"one\"");

        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct);
        // Another installation reaching a repository of the same name, and another address.
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform with { Installation = 42 }, 305, Ct);
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 304, Ct);

        Assert.All(host.Stub.Reads, request => Assert.Null(request.Header("If-None-Match")));
    }

    [Fact]
    public async Task A304WithNothingRememberedIsUnavailable()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotModified);

        Assert.Equal(new CodeHostResult<PullRequest>.Unavailable(null),
            await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct));
    }

    [Fact]
    public async Task AnUnchangedPageKeepsItsNextPage()
    {
        using var host = new GitHubHost();
        const string page2 = "https://api.github.com/repositories/1066577263/pulls?state=open&per_page=100&page=2";
        var pulls = Fixture.Node("pulls-open.json").AsArray();
        host.Stub.Answer = request =>
        {
            if (request.Header("If-None-Match") is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var second = request.Uri.AbsoluteUri == page2;
            var response = Tagged(new JsonArray(pulls[second ? 1 : 0]!.DeepClone()).ToJsonString(), second ? "\"p2\"" : "\"p1\"");
            if (!second)
            {
                response.Headers.Add("Link", $"<{page2}>; rel=\"next\"");
            }

            return response;
        };

        var first = Results.Value(await host.CodeHost.OpenPullRequests(GitHubHost.User, GitHubHost.Platform, Ct));
        var again = Results.Value(await host.CodeHost.OpenPullRequests(GitHubHost.User, GitHubHost.Platform, Ct));

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(pull => pull.Number), again.Select(pull => pull.Number));
        Assert.Equal(["\"p1\"", "\"p2\""], host.Stub.Reads.Skip(2).Select(request => request.Header("If-None-Match")));
    }

    private static HttpResponseMessage Tagged(string body, string etag)
    {
        var response = StubGitHub.Json(body);
        response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    /// <summary>The record with its labels' items, which a record's equality compares by
    /// reference.</summary>
    private static string Fingerprint(PullRequest pull) => $"{pull with { Labels = [] }} [{string.Join(",", pull.Labels)}]";
}