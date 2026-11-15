using System.Net;

using DevFlow.CodeHost.GitHub;

using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// What every request to GitHub carries: the API version it was written against, and a user agent
/// naming the program and nothing about the owner. And where it goes: GitHub's API, with 20
/// seconds to answer. See "GitHub" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class RequestTests
{
    private static CancellationToken Ct => GitHubHost.Ct;

    [Fact]
    public async Task EveryRequestCarriesTheApiVersionAndAUserAgent()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        await host.CodeHost.OpenPullRequests(GitHubHost.User, GitHubHost.Platform, Ct);
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 1, Ct);
        await host.CodeHost.Comments(GitHubHost.User, GitHubHost.Platform, 1, Ct);
        await host.CodeHost.Statuses(GitHubHost.User, GitHubHost.Platform, "abc", Ct);
        await host.CodeHost.CheckRuns(GitHubHost.User, GitHubHost.Platform, "abc", Ct);
        await host.CodeHost.File(GitHubHost.User, GitHubHost.Platform, "README.md", "main", Ct);

        Assert.Equal(7, host.Stub.Requests.Count);
        Assert.All(host.Stub.Requests, request =>
        {
            Assert.Equal("2022-11-28", request.Header("X-GitHub-Api-Version"));
            Assert.Equal("platform-devflow", request.Header("User-Agent"));
            Assert.Equal("api.github.com", request.Uri.Host);
            Assert.Equal("https", request.Uri.Scheme);
        });

        // GitHub's JSON, but for the file, which is read raw.
        Assert.All(host.Stub.Requests.SkipLast(1), request => Assert.Equal("application/vnd.github+json", request.Header("Accept")));
        Assert.Equal("application/vnd.github.raw+json", host.Stub.Requests[^1].Header("Accept"));
    }

    [Fact]
    public void TheClientIsGitHubsApiWithTwentySeconds()
    {
        using var host = new GitHubHost();
        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubCodeHost.HttpClientName);

        Assert.Equal(new Uri("https://api.github.com/"), client.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(20), client.Timeout);
    }
}