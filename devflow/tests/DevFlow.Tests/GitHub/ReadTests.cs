using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The adapter's reads, against GitHub's real answers recorded in contracts/github/: each field of
/// the interface's records is what the file holds, read here by the test on its own. Then
/// the addresses, paging, and how each failure GitHub gives becomes not found or unavailable. See
/// "GitHub" in devflow/docs/devflow-architecture.md and contracts/README.md.
/// </summary>
public sealed class ReadTests
{
    private const string Repo = "/repos/Adamniels/Personal-platform";

    private static CancellationToken Ct => GitHubHost.Ct;

    /// <summary>Answers a read of <paramref name="path"/> with the fixture, and anything else 404.</summary>
    private static Func<Received, HttpResponseMessage> Serve(string path, string fixture) =>
        request => request.Path == path
            ? StubGitHub.Json(Fixture.Text(fixture))
            : new HttpResponseMessage(HttpStatusCode.NotFound);

    [Fact]
    public async Task OpenPullRequestsReadFromTheRecordedAnswer()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = Serve($"{Repo}/pulls", "pulls-open.json");

        var pulls = Results.Value(await host.CodeHost.OpenPullRequests(GitHubHost.User, GitHubHost.Platform, Ct));

        var read = Assert.Single(host.Stub.Reads);
        Assert.Equal("open", read.Query["state"]);
        Assert.Equal("100", read.Query["per_page"]);
        var expected = Fixture.Node("pulls-open.json").AsArray();
        Assert.Equal(expected.Count, pulls.Count);
        Assert.NotEmpty(pulls);
        for (var i = 0; i < expected.Count; i++)
        {
            SameAs(expected[i]!, pulls[i]);
        }
    }

    [Fact]
    public async Task APullRequestReadsFromTheRecordedAnswer()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = Serve($"{Repo}/pulls/305", "pull.json");

        var pull = Results.Value(await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct));

        SameAs(Fixture.Node("pull.json"), pull);
        // The recorded one is merged, which GitHub says as closed with a time it was merged.
        Assert.Equal(PullRequestState.Merged, pull.State);
    }

    [Fact]
    public async Task APullRequestsStateIsOpenMergedOrClosed()
    {
        using var host = new GitHubHost();
        var pull = Fixture.Node("pull.json").AsObject();
        host.Stub.Answer = _ => StubGitHub.Json(pull.ToJsonString());

        pull["state"] = "open";
        pull["merged_at"] = null;
        Assert.Equal(PullRequestState.Open, Results.Value(await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct)).State);

        pull["state"] = "closed";
        Assert.Equal(PullRequestState.Closed, Results.Value(await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct)).State);

        // A body GitHub sends as null is empty, and an author whose account is deleted is ghost,
        // as GitHub shows it.
        pull["body"] = null;
        pull["user"] = null;
        var read = Results.Value(await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct));
        Assert.Equal("", read.Body);
        Assert.Equal("ghost", read.Author);

        // A state it does not know is an answer that does not parse.
        pull["state"] = "locked";
        Assert.Equal(new CodeHostResult<PullRequest>.Unavailable(null),
            await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 305, Ct));
    }

    [Fact]
    public async Task CommentsReadFromTheRecordedAnswer()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = Serve($"{Repo}/issues/305/comments", "issue-comments.json");

        var comments = Results.Value(await host.CodeHost.Comments(GitHubHost.User, GitHubHost.Platform, 305, Ct));

        Assert.Equal("100", Assert.Single(host.Stub.Reads).Query["per_page"]);
        var expected = Fixture.Node("issue-comments.json").AsArray();
        Assert.Equal(expected.Count, comments.Count);
        Assert.NotEmpty(comments);
        for (var i = 0; i < expected.Count; i++)
        {
            var file = expected[i]!;
            var comment = comments[i];
            Assert.Equal((long)file["id"]!, comment.Id);
            Assert.Equal((string)file["user"]!["login"]!, comment.Author);
            Assert.Equal((string)file["body"]!, comment.Body);
            Assert.Equal(Time(file["created_at"]), comment.CreatedAt);
            Assert.Equal(Time(file["updated_at"]), comment.UpdatedAt);
        }
    }

    [Fact]
    public async Task StatusesReadFromTheRecordedAnswer()
    {
        using var host = new GitHubHost();
        const string commit = "1154137dceb065f226823ec720f1f5e55d7c205d";
        host.Stub.Answer = Serve($"{Repo}/commits/{commit}/statuses", "statuses.json");

        var statuses = Results.Value(await host.CodeHost.Statuses(GitHubHost.User, GitHubHost.Platform, commit, Ct));

        Assert.Equal("100", Assert.Single(host.Stub.Reads).Query["per_page"]);
        var expected = Fixture.Node("statuses.json").AsArray();
        Assert.Equal(expected.Count, statuses.Count);
        Assert.NotEmpty(statuses);
        for (var i = 0; i < expected.Count; i++)
        {
            var file = expected[i]!;
            Assert.Equal((string)file["context"]!, statuses[i].Context);
            Assert.Equal(Enum.Parse<StatusState>((string)file["state"]!, ignoreCase: true), statuses[i].State);
            Assert.Equal((string?)file["description"], statuses[i].Description);
        }
    }

    [Fact]
    public async Task StatusesKeepTheLatestOfEachContext()
    {
        using var host = new GitHubHost();
        // GitHub lists every status posted, newest first.
        host.Stub.Answer = _ => StubGitHub.Json("""
            [
              {"context": "review-ok", "state": "success", "description": "approved"},
              {"context": "ci", "state": "failure", "description": null},
              {"context": "review-ok", "state": "pending", "description": "waiting"}
            ]
            """);

        var statuses = Results.Value(await host.CodeHost.Statuses(GitHubHost.User, GitHubHost.Platform, "abc", Ct));

        Assert.Equal(
            [new CommitStatus("review-ok", StatusState.Success, "approved"), new CommitStatus("ci", StatusState.Failure, null)],
            statuses);
    }

    [Fact]
    public async Task CheckRunsReadFromTheRecordedAnswer()
    {
        using var host = new GitHubHost();
        const string commit = "1154137dceb065f226823ec720f1f5e55d7c205d";
        host.Stub.Answer = Serve($"{Repo}/commits/{commit}/check-runs", "check-runs.json");

        var runs = Results.Value(await host.CodeHost.CheckRuns(GitHubHost.User, GitHubHost.Platform, commit, Ct));

        Assert.Equal("100", Assert.Single(host.Stub.Reads).Query["per_page"]);
        var expected = Fixture.Node("check-runs.json")["check_runs"]!.AsArray();
        Assert.Equal(expected.Count, runs.Count);
        Assert.NotEmpty(runs);
        for (var i = 0; i < expected.Count; i++)
        {
            var file = expected[i]!;
            Assert.Equal((string)file["name"]!, runs[i].Name);
            Assert.Equal(Snake<CheckStatus>((string)file["status"]!), runs[i].Status);
            Assert.Equal(file["conclusion"] is { } conclusion ? Snake<CheckConclusion>((string)conclusion!) : null, runs[i].Conclusion);
            Assert.Equal((string)file["app"]!["slug"]!, runs[i].App);
        }
    }

    [Fact]
    public async Task ACheckRunNotStartedHasNoConclusion()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => StubGitHub.Json("""
            {"total_count": 3, "check_runs": [
              {"name": "a", "status": "in_progress", "conclusion": null, "app": {"slug": "github-actions"}},
              {"name": "b", "status": "waiting", "conclusion": null, "app": {"slug": "github-actions"}},
              {"name": "c", "status": "completed", "conclusion": "timed_out", "app": {"slug": "other"}}
            ]}
            """);

        var runs = Results.Value(await host.CodeHost.CheckRuns(GitHubHost.User, GitHubHost.Platform, "abc", Ct));

        Assert.Equal(
            [
                new CheckRun("a", CheckStatus.InProgress, null, "github-actions"),
                // Waiting, requested and pending are GitHub's other words for not started yet.
                new CheckRun("b", CheckStatus.Queued, null, "github-actions"),
                new CheckRun("c", CheckStatus.Completed, CheckConclusion.TimedOut, "other"),
            ],
            runs);
    }

    [Fact]
    public async Task AListIsFollowedAcrossPages()
    {
        using var host = new GitHubHost();
        var comments = Fixture.Node("issue-comments.json").AsArray();
        var first = comments[0]!.AsObject();
        var second = first.DeepClone().AsObject();
        second["id"] = 2;
        var third = first.DeepClone().AsObject();
        third["id"] = 3;
        const string page2 = $"https://api.github.com/repositories/1066577263/issues/305/comments?per_page=100&page=2";
        const string page3 = $"https://api.github.com/repositories/1066577263/issues/305/comments?per_page=100&page=3";
        host.Stub.Answer = request =>
        {
            var (body, next) = request.Uri.AbsoluteUri switch
            {
                page2 => (new JsonArray(second.DeepClone()), page3),
                page3 => (new JsonArray(third.DeepClone()), null),
                _ => (new JsonArray(first.DeepClone()), page2),
            };
            var response = StubGitHub.Json(body.ToJsonString());
            if (next is not null)
            {
                response.Headers.Add("Link", $"<{next}>; rel=\"next\", <{page3}>; rel=\"last\"");
            }

            return response;
        };

        var read = Results.Value(await host.CodeHost.Comments(GitHubHost.User, GitHubHost.Platform, 305, Ct));

        Assert.Equal([(long)first["id"]!, 2, 3], read.Select(comment => comment.Id));
        Assert.Equal(
            [$"https://api.github.com{Repo}/issues/305/comments?per_page=100", page2, page3],
            host.Stub.Reads.Select(request => request.Uri.AbsoluteUri));
        // Every page with the one token.
        Assert.Single(host.Stub.TokenRequests);
    }

    [Fact]
    public async Task ANextPageElsewhereIsNeverFollowed()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ =>
        {
            var response = StubGitHub.Json("[]");
            response.Headers.Add("Link", "<https://example.com/steal?page=2>; rel=\"next\"");
            return response;
        };

        Assert.Equal(new CodeHostResult<IReadOnlyList<Comment>>.Unavailable(null),
            await host.CodeHost.Comments(GitHubHost.User, GitHubHost.Platform, 305, Ct));
        // The token was never sent anywhere but GitHub's API.
        Assert.All(host.Stub.Requests, request => Assert.Equal("api.github.com", request.Uri.Host));
        Assert.Single(host.Stub.Reads);
    }

    [Fact]
    public async Task AFileIsReadRawFromItsBranch()
    {
        using var host = new GitHubHost();
        var bytes = Encoding.UTF8.GetBytes("# Personal platform\nåäö\n");
        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

        var read = Results.Value(await host.CodeHost.File(GitHubHost.User, GitHubHost.Platform, "docs/my notes#1.md", "274-reading/github", Ct));

        Assert.Equal(bytes, read);
        var request = Assert.Single(host.Stub.Reads);
        Assert.Equal(HttpMethod.Get, request.Method);
        // Each segment of the path escaped, its slashes kept; the branch a query value.
        Assert.Equal($"{Repo}/contents/docs/my%20notes%231.md", request.Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["ref"] = "274-reading/github" }, request.Query);
        Assert.Equal("application/vnd.github.raw+json", request.Header("Accept"));
    }

    [Fact]
    public async Task ADefaultBranchFileIsReadAtTheLastCommitThatChangedIt()
    {
        using var host = new GitHubHost();
        var commit = (string)Fixture.Node("commits-path.json")[0]!["sha"]!;
        var bytes = Encoding.UTF8.GetBytes("FROM base\n");
        host.Stub.Answer = request => request.Path switch
        {
            $"{Repo}/commits" => StubGitHub.Json(Fixture.Text("commits-path.json")),
            $"{Repo}/contents/devflow/Dockerfile" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var read = Results.Value(await host.CodeHost.DefaultBranchFile(GitHubHost.User, GitHubHost.Platform, "devflow/Dockerfile", Ct));

        Assert.Equal(commit, read.Commit);
        Assert.Equal(bytes, read.Content);
        Assert.Equal(2, host.Stub.Reads.Count);
        // The newest commit of the default branch that changed the path, GitHub's default when no
        // sha is named; then the file at exactly that commit, so the bytes and the commit agree.
        var commits = host.Stub.Reads[0];
        Assert.Equal($"{Repo}/commits", commits.Path);
        Assert.Equal(new Dictionary<string, string> { ["path"] = "devflow/Dockerfile", ["per_page"] = "1" }, commits.Query);
        var file = host.Stub.Reads[1];
        Assert.Equal($"{Repo}/contents/devflow/Dockerfile", file.Path);
        Assert.Equal(new Dictionary<string, string> { ["ref"] = commit }, file.Query);
        Assert.Equal("application/vnd.github.raw+json", file.Header("Accept"));

        // No commit ever changed the path: the file is not found, and its contents are not asked for.
        host.Stub.Answer = request => request.Path == $"{Repo}/commits"
            ? StubGitHub.Json("[]")
            : throw new InvalidOperationException("the contents were asked for");
        Assert.Equal("NotFound { }", (await host.CodeHost.DefaultBranchFile(GitHubHost.User, GitHubHost.Platform, "devflow/Dockerfile", Ct)).ToString());

        // A sha that is not a commit id is an answer that does not parse.
        host.Stub.Answer = _ => StubGitHub.Json("""[{"sha": "main"}]""");
        Assert.Equal("Unavailable { RetryAt =  }", (await host.CodeHost.DefaultBranchFile(GitHubHost.User, GitHubHost.Platform, "devflow/Dockerfile", Ct)).ToString());
    }

    [Fact]
    public async Task A404IsNotFoundAndA5xxIsUnavailable()
    {
        using var host = new GitHubHost();

        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        foreach (var result in await EveryOperation(host))
        {
            Assert.Equal("NotFound { }", Describe(result));
        }

        foreach (var status in new[] { 500, 502, 503, 504 })
        {
            host.Stub.Answer = _ => new HttpResponseMessage((HttpStatusCode)status);
            foreach (var result in await EveryOperation(host))
            {
                Assert.Equal("Unavailable { RetryAt =  }", Describe(result));
            }
        }

        // A body that does not parse, and a request that times out, are unavailable with no time.
        host.Stub.Answer = _ => StubGitHub.Json("""{"unexpected": true}""");
        foreach (var result in (await EveryOperation(host)).SkipLast(1))
        {
            Assert.Equal("Unavailable { RetryAt =  }", Describe(result));
        }

        host.Stub.Answer = _ => throw new TaskCanceledException("the request timed out", new TimeoutException());
        foreach (var result in await EveryOperation(host))
        {
            Assert.Equal("Unavailable { RetryAt =  }", Describe(result));
        }

        // And a network that does not answer.
        host.Stub.Answer = _ => throw new HttpRequestException("connection refused");
        foreach (var result in await EveryOperation(host))
        {
            Assert.Equal("Unavailable { RetryAt =  }", Describe(result));
        }
    }

    [Fact]
    public async Task ARateLimitIsUnavailableUntilTheTimeGitHubGives()
    {
        using var host = new GitHubHost();
        var now = host.Time.GetUtcNow();
        var reset = now.AddMinutes(17).ToUnixTimeSeconds();

        // retry-after, in seconds, on a 403 or a 429.
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            host.Stub.Answer = _ => Limited(status, ("retry-after", "120"));
            foreach (var result in await EveryOperation(host))
            {
                Assert.Equal($"Unavailable {{ RetryAt = {now.AddSeconds(120)} }}", Describe(result));
            }
        }

        // x-ratelimit-reset, in seconds since the epoch, when the remaining count is zero.
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            host.Stub.Answer = _ => Limited(status, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset.ToString(CultureInfo.InvariantCulture)));
            foreach (var result in await EveryOperation(host))
            {
                Assert.Equal($"Unavailable {{ RetryAt = {DateTimeOffset.FromUnixTimeSeconds(reset)} }}", Describe(result));
            }
        }

        // retry-after wins when both are given.
        host.Stub.Answer = _ => Limited(HttpStatusCode.Forbidden, ("retry-after", "5"), ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(new CodeHostResult<PullRequest>.Unavailable(now.AddSeconds(5)),
            await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 1, Ct));

        // Without a time GitHub gives, unavailable with none: a 403 with requests left, a 429
        // alone, or a reset that does not parse.
        foreach (var answer in new Func<HttpResponseMessage>[]
        {
            () => Limited(HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "12"), ("x-ratelimit-reset", reset.ToString(CultureInfo.InvariantCulture))),
            () => Limited(HttpStatusCode.TooManyRequests),
            () => Limited(HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", "soon")),
            () => Limited(HttpStatusCode.Forbidden, ("retry-after", "-3")),
        })
        {
            host.Stub.Answer = _ => answer();
            Assert.Equal(new CodeHostResult<PullRequest>.Unavailable(null),
                await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 1, Ct));
        }
    }

    private static HttpResponseMessage Limited(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        var response = StubGitHub.Json("""{"message": "API rate limit exceeded"}""", status);
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    /// <summary>Every read the interface has, once each, the file last.</summary>
    private static async Task<IReadOnlyList<object>> EveryOperation(GitHubHost host) =>
    [
        await host.CodeHost.OpenPullRequests(GitHubHost.User, GitHubHost.Platform, Ct),
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 1, Ct),
        await host.CodeHost.Comments(GitHubHost.User, GitHubHost.Platform, 1, Ct),
        await host.CodeHost.Statuses(GitHubHost.User, GitHubHost.Platform, "abc", Ct),
        await host.CodeHost.CheckRuns(GitHubHost.User, GitHubHost.Platform, "abc", Ct),
        await host.CodeHost.DefaultBranchFile(GitHubHost.User, GitHubHost.Platform, ".devflow/Dockerfile", Ct),
        await host.CodeHost.File(GitHubHost.User, GitHubHost.Platform, "README.md", "main", Ct),
    ];

    /// <summary>A result's case and fields, the same for every type it holds.</summary>
    private static string Describe(object result) => result.ToString()!;

    private static void SameAs(JsonNode file, PullRequest pull)
    {
        Assert.Equal((int)file["number"]!, pull.Number);
        Assert.Equal((string)file["title"]!, pull.Title);
        Assert.Equal((string)file["user"]!["login"]!, pull.Author);
        Assert.Equal(file["labels"]!.AsArray().Select(label => (string)label!["name"]!), pull.Labels);
        Assert.Equal((string)file["head"]!["sha"]!, pull.HeadCommit);
        Assert.Equal((string)file["head"]!["ref"]!, pull.HeadBranch);
        Assert.Equal((string)file["base"]!["ref"]!, pull.BaseBranch);
        Assert.Equal((bool)file["draft"]!, pull.Draft);
        Assert.Equal((string?)file["body"] ?? "", pull.Body);
        Assert.Equal(new Uri((string)file["html_url"]!), pull.Url);
        Assert.Equal(Time(file["updated_at"]), pull.UpdatedAt);
        var state = (string)file["state"]! == "open"
            ? PullRequestState.Open
            : file["merged_at"] is null ? PullRequestState.Closed : PullRequestState.Merged;
        Assert.Equal(state, pull.State);
    }

    private static DateTimeOffset Time(JsonNode? value) =>
        DateTimeOffset.Parse((string)value!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>An enumeration's member by GitHub's snake case word, such as <c>timed_out</c>.</summary>
    private static T Snake<T>(string word)
        where T : struct, Enum =>
        Enum.Parse<T>(word.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);
}