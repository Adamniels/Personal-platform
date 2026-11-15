using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The installation tokens the adapter makes for itself: each asks for read access alone, to the
/// one repository being read; each is kept in memory until five minutes before it expires; and
/// none is ever printed. The request body's test is what keeps "only the owner writes" true by
/// construction. See "GitHub" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class InstallationTokenTests
{
    private static CancellationToken Ct => GitHubHost.Ct;

    [Fact]
    public async Task TheRequestAsksForReadOnlyPermissionsOnOneRepository()
    {
        using var host = new GitHubHost();

        await host.CodeHost.OpenPullRequests(GitHubHost.User, GitHubHost.Platform, Ct);

        var request = Assert.Single(host.Stub.TokenRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/app/installations/41/access_tokens", request.Uri.AbsoluteUri);
        Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
        var body = JsonNode.Parse(request.Body!)!.AsObject();

        // Exactly these, each read, and nothing else in the body.
        Assert.Equal(["permissions", "repository_ids"], body.Select(field => field.Key).Order(StringComparer.Ordinal));
        var expected = new JsonObject
        {
            ["contents"] = "read",
            ["pull_requests"] = "read",
            ["issues"] = "read",
            ["statuses"] = "read",
            ["checks"] = "read",
            ["metadata"] = "read",
        };
        Assert.True(JsonNode.DeepEquals(expected, body["permissions"]), body["permissions"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(new JsonArray(GitHubHost.Platform.Id), body["repository_ids"]), body["repository_ids"]!.ToJsonString());

        // The read itself carries the token GitHub gave.
        Assert.Equal($"Bearer {StubGitHub.FixtureToken}", Assert.Single(host.Stub.Reads).Header("Authorization"));
    }

    [Fact]
    public async Task ATokenIsReusedUntilFiveMinutesBeforeItExpires()
    {
        using var host = new GitHubHost();
        host.Stub.TokenAnswer = _ => host.Stub.Token(TimeSpan.FromHours(1));

        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 1, Ct);
        Assert.Single(host.Stub.TokenRequests);

        // 54 minutes 59 seconds later, six seconds more than five minutes before it expires: kept.
        host.Time.Advance(TimeSpan.FromMinutes(54) + TimeSpan.FromSeconds(59));
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 2, Ct);
        await host.CodeHost.Comments(GitHubHost.User, GitHubHost.Platform, 2, Ct);
        Assert.Single(host.Stub.TokenRequests);

        // At five minutes before it expires, a new one.
        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 3, Ct);
        Assert.Equal(2, host.Stub.TokenRequests.Count);
        Assert.Equal(4, host.Stub.Reads.Count);

        // A token is per installation and repository: another repository of the same
        // installation, and the same repository id under another installation, each get their own.
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform with { Id = 7 }, 1, Ct);
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform with { Installation = 42 }, 1, Ct);
        Assert.Equal(4, host.Stub.TokenRequests.Count);
        Assert.Equal("/app/installations/42/access_tokens", host.Stub.TokenRequests[^1].Path);

        // The first is still kept.
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 4, Ct);
        Assert.Equal(4, host.Stub.TokenRequests.Count);
    }

    [Fact]
    public async Task ATokenThatCannotBeHadFailsTheRead()
    {
        using var host = new GitHubHost();

        // The installation is gone, or the repository is not in it: not found.
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity })
        {
            host.Stub.TokenAnswer = _ => new HttpResponseMessage(status);
            Assert.IsType<CodeHostResult<PullRequest>.NotFound>(
                await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 1, Ct));
        }

        // GitHub refuses the app's own token, or fails: unavailable, with no time.
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError })
        {
            host.Stub.TokenAnswer = _ => new HttpResponseMessage(status);
            Assert.Equal(new CodeHostResult<PullRequest>.Unavailable(null),
                await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 1, Ct));
        }

        // An answer that does not parse.
        host.Stub.TokenAnswer = _ => StubGitHub.Json("""{"token": 1}""", HttpStatusCode.Created);
        Assert.Equal(new CodeHostResult<PullRequest>.Unavailable(null),
            await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 1, Ct));


        // None of them was read with a token.
        Assert.Empty(host.Stub.Reads);
    }

    [Fact]
    public async Task ATokenIsNeverPrinted()
    {
        using var host = new GitHubHost();
        var token = StubGitHub.FixtureToken;
        var reads = 0;
        host.Stub.Answer = request => ++reads switch
        {
            1 => StubGitHub.Json("[]"),
            2 => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            3 => StubGitHub.Json("not json"),
            4 => throw new HttpRequestException("the network is down"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var results = new List<object>
        {
            await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct),
            await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct),
            await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct),
            await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct),
            await host.CodeHost.File(GitHubHost.User, GitHubHost.Platform, "README.md", "main", Ct),
        };

        // A caller that gives up is told so with an exception, which holds no token either.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var stopped = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform with { Id = 8 }, 1, cancelled.Token));

        // The installation token was sent, and the app's token with it, so there is something to find.
        Assert.Equal($"Bearer {token}", host.Stub.Reads[0].Header("Authorization"));
        var appToken = host.Stub.TokenRequests[0].Header("Authorization")!["Bearer ".Length..];
        Assert.NotEmpty(host.Logs.Lines);

        foreach (var text in host.Logs.Lines.Concat(results.Select(result => result.ToString()!)).Append(stopped.ToString()))
        {
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            // A part of it, so a token cut or wrapped is found as well.
            Assert.DoesNotContain(token[4..20], text, StringComparison.Ordinal);
            Assert.DoesNotContain(appToken[^40..], text, StringComparison.Ordinal);
        }
    }
}