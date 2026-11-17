using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// What watching the default branch reads, against GitHub's documented form, read on 2026-11-17,
/// and its answers recorded in contracts/github/: the repository, <c>GET /repos/{owner}/{repo}</c>,
/// for its <c>default_branch</c>; and the branch's activity,
/// <c>GET /repos/{owner}/{repo}/activity</c> with <c>ref</c> and <c>per_page</c>, newest first,
/// paged by a cursor in the <c>Link</c> header. Both as conditional requests. See "Watching the
/// default branch" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class GitHubActivityTests
{
    private const string Repo = "/repos/Adamniels/Personal-platform";

    private static CancellationToken Ct => GitHubHost.Ct;

    private static HttpResponseMessage Tagged(string body, string etag, string? next = null)
    {
        var response = StubGitHub.Json(body);
        response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        if (next is not null)
        {
            response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
        }

        return response;
    }

    [Fact]
    public async Task ActivityMatchesGitHubsDocumentedForm()
    {
        using var host = new GitHubHost();
        var recorded = Fixture.Node("activity.json").AsArray();
        host.Stub.Answer = request => (request.Path, request.Header("If-None-Match")) switch
        {
            (Repo, "W/\"repo\"") => new HttpResponseMessage(HttpStatusCode.NotModified),
            (Repo, _) => Tagged(Fixture.Text("repository.json"), "W/\"repo\""),
            ($"{Repo}/activity", "W/\"activity\"") => new HttpResponseMessage(HttpStatusCode.NotModified),
            ($"{Repo}/activity", _) => Tagged(Fixture.Text("activity.json"), "W/\"activity\""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var branch = Results.Value(await host.CodeHost.DefaultBranch(GitHubHost.User, GitHubHost.Platform, Ct));
        var newest = Results.Value(await host.CodeHost.Activity(GitHubHost.User, GitHubHost.Platform, branch, null, Ct));

        // What each answers is the recorded answer, read field by field.
        Assert.Equal((string)Fixture.Node("repository.json")["default_branch"]!, branch);
        Assert.Equal(recorded.Count, newest.Count);
        for (var i = 0; i < recorded.Count; i++)
        {
            var answer = recorded[i]!;
            Assert.Equal((long)answer["id"]!, newest[i].Id);
            Assert.Equal((string)answer["before"]!, newest[i].Before);
            Assert.Equal((string)answer["after"]!, newest[i].After);
            Assert.Equal(DateTimeOffset.Parse((string)answer["timestamp"]!, CultureInfo.InvariantCulture), newest[i].At);
            Assert.Equal((string)answer["actor"]!["login"]!, newest[i].Actor);
            Assert.Equal(ActivityKind.PullRequestMerge, newest[i].Kind);
        }

        // The requests, as GitHub documents them, as the app.
        var reads = host.Stub.Reads;
        Assert.Equal(Repo, reads[0].Path);
        Assert.Equal($"{Repo}/activity", reads[1].Path);
        Assert.Equal("main", reads[1].Query["ref"]);
        Assert.Equal("100", reads[1].Query["per_page"]);
        Assert.All(reads, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"Bearer {StubGitHub.FixtureToken}", request.Header("Authorization"));
            Assert.Equal("application/vnd.github+json", request.Header("Accept"));
        });

        // Asked again, both are conditional, and a 304 answers what was read.
        Assert.Equal(branch, Results.Value(await host.CodeHost.DefaultBranch(GitHubHost.User, GitHubHost.Platform, Ct)));
        var since = Results.Value(await host.CodeHost.Activity(GitHubHost.User, GitHubHost.Platform, branch, (long)recorded[1]!["id"]!, Ct));
        Assert.Equal("W/\"repo\"", host.Stub.Reads[2].Header("If-None-Match"));
        Assert.Equal("W/\"activity\"", host.Stub.Reads[3].Header("If-None-Match"));

        // Since the second, only the newest is newer.
        Assert.Equal([(long)recorded[0]!["id"]!], since.Select(activity => activity.Id));
    }

    [Fact]
    public async Task ActivityIsReadPageByPageUntilWhatWasSeen()
    {
        using var host = new GitHubHost();
        var recorded = Fixture.Node("activity.json").AsArray();
        var next = $"https://api.github.com/repositories/1066577263/activity?ref=main&per_page=100&after=cursor";
        host.Stub.Answer = request => request.Query.ContainsKey("after")
            ? Tagged(new JsonArray(recorded[2]!.DeepClone()).ToJsonString(), "W/\"two\"")
            : Tagged(new JsonArray(recorded[0]!.DeepClone(), recorded[1]!.DeepClone()).ToJsonString(), "W/\"one\"", next);

        // The newest page only, to begin at.
        var first = Results.Value(await host.CodeHost.Activity(GitHubHost.User, GitHubHost.Platform, "main", null, Ct));
        Assert.Equal(2, first.Count);
        Assert.Single(host.Stub.Reads);

        // Since one on the first page: no second page is asked for.
        var near = Results.Value(await host.CodeHost.Activity(GitHubHost.User, GitHubHost.Platform, "main", (long)recorded[1]!["id"]!, Ct));
        Assert.Single(near);
        Assert.Equal(2, host.Stub.Reads.Count);

        // Since one on the second page: both are read, and it is left out.
        var far = Results.Value(await host.CodeHost.Activity(GitHubHost.User, GitHubHost.Platform, "main", (long)recorded[2]!["id"]!, Ct));
        Assert.Equal(2, far.Count);
        Assert.Equal(4, host.Stub.Reads.Count);
        Assert.Equal("cursor", host.Stub.Reads[^1].Query["after"]);

        // Since one never listed, such as 0: everything.
        Assert.Equal(3, Results.Value(await host.CodeHost.Activity(GitHubHost.User, GitHubHost.Platform, "main", 0, Ct)).Count);
    }

    [Theory]
    [InlineData("activity_type", "renamed_branch")]
    [InlineData("ref", "refs/heads/other")]
    [InlineData("after", "not-a-commit")]
    [InlineData("before", "ABCDEF0000000000000000000000000000000000")]
    public async Task AnActivityThatDoesNotReadIsUnavailable(string field, string value)
    {
        using var host = new GitHubHost();
        var recorded = Fixture.Node("activity.json").AsArray();
        recorded[1]![field] = value;
        host.Stub.Answer = _ => StubGitHub.Json(recorded.ToJsonString());

        Assert.IsType<CodeHostResult<IReadOnlyList<BranchActivity>>.Unavailable>(
            await host.CodeHost.Activity(GitHubHost.User, GitHubHost.Platform, "main", null, Ct));
    }

    [Fact]
    public async Task AnActivityWithoutAnActorHasNone()
    {
        using var host = new GitHubHost();
        var recorded = Fixture.Node("activity.json").AsArray();
        recorded[0]!["actor"] = null;
        recorded[1]!["activity_type"] = "force_push";
        recorded[2]!["activity_type"] = "push";
        host.Stub.Answer = _ => StubGitHub.Json(recorded.ToJsonString());

        var read = Results.Value(await host.CodeHost.Activity(GitHubHost.User, GitHubHost.Platform, "main", null, Ct));

        Assert.Null(read[0].Actor);
        Assert.Equal([ActivityKind.PullRequestMerge, ActivityKind.ForcePush, ActivityKind.Push], read.Select(activity => activity.Kind));
    }

    [Fact]
    public async Task AMergeAnswersItsMergeCommit()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => StubGitHub.Json(Fixture.Text("merge.json"));
        var merge = Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, WritePurpose.Merge, Ct));

        var merged = Assert.IsType<MergeResult.Merged>(await host.CodeHost.MergeAsApp(GitHubHost.User, merge, GitHubHost.Platform, 352, new string('a', 40), Ct));

        Assert.Equal((string)Fixture.Node("merge.json")["sha"]!, merged.Commit);

        // One that names no commit is merged, with none.
        host.Stub.Answer = _ => StubGitHub.Json("""{"merged": true, "message": "ok", "sha": "nope"}""");
        Assert.Null(Assert.IsType<MergeResult.Merged>(await host.CodeHost.MergeAsApp(GitHubHost.User, merge, GitHubHost.Platform, 352, new string('a', 40), Ct)).Commit);
    }
}