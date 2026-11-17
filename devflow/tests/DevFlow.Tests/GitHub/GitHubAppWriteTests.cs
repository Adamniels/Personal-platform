using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The app's own writes against GitHub's documented form, read on 2026-11-17, and answered with
/// the answers recorded in contracts/github/: opening a pull request is
/// <c>POST /repos/{owner}/{repo}/pulls</c>, commenting <c>POST .../issues/{number}/comments</c>,
/// adding a label <c>POST .../issues/{number}/labels</c>, removing one
/// <c>DELETE .../issues/{number}/labels/{name}</c>, opening an issue <c>POST .../issues</c>, and
/// merging <c>PUT .../pulls/{number}/merge</c> with <c>sha</c> and <c>merge_method</c>. Each with a
/// key the app made for that purpose, never a person's token. See "The program's own writes" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class GitHubAppWriteTests
{
    private const string Repo = "/repos/Adamniels/Personal-platform";

    private static readonly string Head = new('a', 40);

    private static CancellationToken Ct => GitHubHost.Ct;

    private static HttpResponseMessage Message(HttpStatusCode status, string message) =>
        StubGitHub.Json(new JsonObject { ["message"] = message, ["documentation_url"] = "https://docs.github.com/rest" }.ToJsonString(), status);

    private static async Task<AppWriteToken> Key(GitHubHost host, WritePurpose purpose, CodeHost.Repository? repository = null) =>
        Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, repository ?? GitHubHost.Platform, purpose, Ct));

    /// <summary>The recorded labels without <paramref name="removed"/>, as GitHub answers a
    /// removal with what is left.</summary>
    private static string LabelsWithout(string removed) =>
        new JsonArray([.. Fixture.Node("labels.json").AsArray()
            .Where(label => (string)label!["name"]! != removed)
            .Select(label => label!.DeepClone())]).ToJsonString();

    [Fact]
    public async Task EachWriteMatchesGitHubsDocumentedForm()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => (request.Method.Method, request.Path) switch
        {
            ("POST", $"{Repo}/pulls") => StubGitHub.Json(Fixture.Text("pull-opened.json"), HttpStatusCode.Created),
            ("POST", $"{Repo}/issues/352/comments") => StubGitHub.Json(Fixture.Node("issue-comments.json")[0]!.ToJsonString(), HttpStatusCode.Created),
            ("POST", $"{Repo}/issues/352/labels") => StubGitHub.Json(Fixture.Text("labels.json")),
            ("DELETE", $"{Repo}/issues/352/labels/owner-decision") => StubGitHub.Json(LabelsWithout("owner-decision")),
            ("POST", $"{Repo}/issues") => StubGitHub.Json(Fixture.Text("issue.json"), HttpStatusCode.Created),
            ("PUT", $"{Repo}/pulls/352/merge") => StubGitHub.Json(Fixture.Text("merge.json")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var post = await Key(host, WritePurpose.Post);
        var merge = await Key(host, WritePurpose.Merge);

        var opened = await host.CodeHost.OpenPullRequest(GitHubHost.User, post, GitHubHost.Platform, "334-the-base-image-and-the-start-commands", "main", "The base image and the start commands", "## Summary\n\nCloses #334", Ct);
        var commented = await host.CodeHost.CommentOnIssue(GitHubHost.User, post, GitHubHost.Platform, 352, "Picked up.\n\nWorking on it.", Ct);
        var added = await host.CodeHost.AddLabel(GitHubHost.User, post, GitHubHost.Platform, 352, "has-decision", Ct);
        var removed = await host.CodeHost.RemoveLabel(GitHubHost.User, post, GitHubHost.Platform, 352, "owner-decision", Ct);
        var issue = await host.CodeHost.OpenIssue(GitHubHost.User, post, GitHubHost.Platform, "The program's write path", "Found while running.", ["devflow"], Ct);
        var merged = await host.CodeHost.MergeAsApp(GitHubHost.User, merge, GitHubHost.Platform, 352, Head, Ct);

        // What each answers is the recorded answer, read field by field here on its own.
        var pull = Fixture.Node("pull-opened.json");
        var pr = Assert.IsType<WriteResult<PullRequest>.Written>(opened).Value;
        Assert.Equal((int)pull["number"]!, pr.Number);
        Assert.Equal((string)pull["title"]!, pr.Title);
        Assert.Equal((string)pull["user"]!["login"]!, pr.Author);
        Assert.Equal(pull["labels"]!.AsArray().Select(label => (string)label!["name"]!), pr.Labels);
        Assert.Equal((string)pull["head"]!["sha"]!, pr.HeadCommit);
        Assert.Equal((string)pull["head"]!["ref"]!, pr.HeadBranch);
        Assert.Equal((string)pull["base"]!["ref"]!, pr.BaseBranch);
        Assert.Equal(new Uri((string)pull["html_url"]!), pr.Url);
        Assert.Equal(PullRequestState.Open, pr.State);

        var recordedComment = Fixture.Node("issue-comments.json")[0]!;
        var comment = Assert.IsType<WriteResult<Comment>.Written>(commented).Value;
        Assert.Equal((long)recordedComment["id"]!, comment.Id);
        Assert.Equal((string)recordedComment["user"]!["login"]!, comment.Author);
        Assert.Equal((string)recordedComment["body"]!, comment.Body);
        Assert.Equal(DateTimeOffset.Parse((string)recordedComment["created_at"]!, CultureInfo.InvariantCulture), comment.CreatedAt);

        Assert.Equal(["owner-decision", "has-decision"], Assert.IsType<WriteResult<IReadOnlyList<string>>.Written>(added).Value);
        Assert.Equal(["has-decision"], Assert.IsType<WriteResult<IReadOnlyList<string>>.Written>(removed).Value);

        var recordedIssue = Fixture.Node("issue.json");
        var opens = Assert.IsType<WriteResult<Issue>.Written>(issue).Value;
        Assert.Equal((int)recordedIssue["number"]!, opens.Number);
        Assert.Equal((string)recordedIssue["title"]!, opens.Title);
        Assert.Equal(recordedIssue["labels"]!.AsArray().Select(label => (string)label!["name"]!), opens.Labels);
        Assert.Equal(new Uri((string)recordedIssue["html_url"]!), opens.Url);

        Assert.IsType<MergeResult.Merged>(merged);

        // The requests, as GitHub documents them.
        var reads = host.Stub.Reads;
        Assert.Equal(6, reads.Count);
        Expect(reads[0], HttpMethod.Post, $"{Repo}/pulls", new JsonObject
        {
            ["title"] = "The base image and the start commands",
            ["head"] = "334-the-base-image-and-the-start-commands",
            ["base"] = "main",
            ["body"] = "## Summary\n\nCloses #334",
        });
        Expect(reads[1], HttpMethod.Post, $"{Repo}/issues/352/comments", new JsonObject { ["body"] = "Picked up.\n\nWorking on it." });
        Expect(reads[2], HttpMethod.Post, $"{Repo}/issues/352/labels", new JsonObject { ["labels"] = new JsonArray("has-decision") });
        Expect(reads[3], HttpMethod.Delete, $"{Repo}/issues/352/labels/owner-decision", null);
        Expect(reads[4], HttpMethod.Post, $"{Repo}/issues", new JsonObject
        {
            ["title"] = "The program's write path",
            ["body"] = "Found while running.",
            ["labels"] = new JsonArray("devflow"),
        });
        Expect(reads[5], HttpMethod.Put, $"{Repo}/pulls/352/merge", new JsonObject { ["sha"] = Head, ["merge_method"] = "merge" });
        Assert.All(reads, request =>
        {
            // The app's key, never a person's token.
            Assert.Equal($"Bearer {StubGitHub.FixtureToken}", request.Header("Authorization"));
            Assert.Equal("application/vnd.github+json", request.Header("Accept"));
            Assert.Equal("2022-11-28", request.Header("X-GitHub-Api-Version"));
        });
        Assert.DoesNotContain(StubGitHub.FixtureToken, string.Join('\n', host.Logs.Lines), StringComparison.Ordinal);
    }

    private static void Expect(Received request, HttpMethod method, string path, JsonObject? body)
    {
        Assert.Equal(method, request.Method);
        Assert.Equal(path, request.Path);
        if (body is null)
        {
            Assert.True(string.IsNullOrEmpty(request.Body), request.Body);
            return;
        }

        Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(request.Body!)), request.Body);
    }

    [Fact]
    public async Task ALabelIsEscapedInTheAddress()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => StubGitHub.Json("[]");
        var post = await Key(host, WritePurpose.Post);

        await host.CodeHost.RemoveLabel(GitHubHost.User, post, GitHubHost.Platform, 7, "needs owner/review?", Ct);

        Assert.Equal($"{Repo}/issues/7/labels/needs%20owner%2Freview%3F", Assert.Single(host.Stub.Reads).Uri.AbsolutePath);
    }

    [Fact]
    public async Task FailuresReadAsTheInterfacesResults()
    {
        using var host = new GitHubHost();
        var post = await Key(host, WritePurpose.Post);
        var merge = await Key(host, WritePurpose.Merge);

        // GitHub's refusals, with its status and words.
        host.Stub.Answer = _ => Message(HttpStatusCode.UnprocessableEntity, "Validation Failed");
        Assert.Equal(new WriteResult<PullRequest>.Refused(422, "Validation Failed"),
            await host.CodeHost.OpenPullRequest(GitHubHost.User, post, GitHubHost.Platform, "x", "main", "t", "b", Ct));
        Assert.Equal(new WriteResult<Issue>.Refused(422, "Validation Failed"),
            await host.CodeHost.OpenIssue(GitHubHost.User, post, GitHubHost.Platform, "t", "b", [], Ct));
        host.Stub.Answer = _ => Message(HttpStatusCode.NotFound, "Label does not exist");
        Assert.Equal(new WriteResult<IReadOnlyList<string>>.Refused(404, "Label does not exist"),
            await host.CodeHost.RemoveLabel(GitHubHost.User, post, GitHubHost.Platform, 352, "gone", Ct));
        host.Stub.Answer = _ => Message(HttpStatusCode.Forbidden, "Resource not accessible by integration");
        Assert.Equal(new WriteResult<Comment>.Refused(403, "Resource not accessible by integration"),
            await host.CodeHost.CommentOnIssue(GitHubHost.User, post, GitHubHost.Platform, 352, "Hi.", Ct));
        Assert.Equal(new WriteResult<IReadOnlyList<string>>.Refused(403, "Resource not accessible by integration"),
            await host.CodeHost.AddLabel(GitHubHost.User, post, GitHubHost.Platform, 352, "x", Ct));

        // A merge reads as the owner's merge does.
        host.Stub.Answer = _ => Message(HttpStatusCode.Conflict, "Head branch was modified. Review and try the merge again.");
        Assert.IsType<MergeResult.HeadMoved>(await host.CodeHost.MergeAsApp(GitHubHost.User, merge, GitHubHost.Platform, 352, Head, Ct));
        host.Stub.Answer = _ => Message(HttpStatusCode.MethodNotAllowed, "Pull Request is not mergeable");
        Assert.Equal(new MergeResult.NotMergeable("Pull Request is not mergeable"), await host.CodeHost.MergeAsApp(GitHubHost.User, merge, GitHubHost.Platform, 352, Head, Ct));
        host.Stub.Answer = _ => Message(HttpStatusCode.Forbidden, "Resource not accessible by integration");
        Assert.Equal(new MergeResult.Refused(403, "Resource not accessible by integration"), await host.CodeHost.MergeAsApp(GitHubHost.User, merge, GitHubHost.Platform, 352, Head, Ct));

        // A success that does not parse: done, but what was made is not known.
        host.Stub.Answer = _ => StubGitHub.Json("""{"number": "not a number"}""", HttpStatusCode.Created);
        Assert.Equal(new WriteResult<PullRequest>.Unavailable(null),
            await host.CodeHost.OpenPullRequest(GitHubHost.User, post, GitHubHost.Platform, "x", "main", "t", "b", Ct));

        // No answer.
        host.Stub.Answer = _ => throw new HttpRequestException("the network is down");
        Assert.IsType<WriteResult<PullRequest>.Unavailable>(await host.CodeHost.OpenPullRequest(GitHubHost.User, post, GitHubHost.Platform, "x", "main", "t", "b", Ct));
        Assert.IsType<WriteResult<Comment>.Unavailable>(await host.CodeHost.CommentOnIssue(GitHubHost.User, post, GitHubHost.Platform, 352, "Hi.", Ct));
        Assert.IsType<WriteResult<IReadOnlyList<string>>.Unavailable>(await host.CodeHost.AddLabel(GitHubHost.User, post, GitHubHost.Platform, 352, "x", Ct));
        Assert.IsType<WriteResult<IReadOnlyList<string>>.Unavailable>(await host.CodeHost.RemoveLabel(GitHubHost.User, post, GitHubHost.Platform, 352, "x", Ct));
        Assert.IsType<WriteResult<Issue>.Unavailable>(await host.CodeHost.OpenIssue(GitHubHost.User, post, GitHubHost.Platform, "t", "b", [], Ct));
        Assert.IsType<MergeResult.Unavailable>(await host.CodeHost.MergeAsApp(GitHubHost.User, merge, GitHubHost.Platform, 352, Head, Ct));
    }

    [Fact]
    public async Task AKeyForAnotherPurposeOrRepositoryIsNeverSent()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => StubGitHub.Json("[]");
        var push = await Key(host, WritePurpose.Push);
        var post = await Key(host, WritePurpose.Post);
        var merge = await Key(host, WritePurpose.Merge);
        var elsewhere = await Key(host, WritePurpose.Post, GitHubHost.Platform with { Id = 7 });

        // A push key writes nothing through the API, a post key cannot merge, a merge key cannot
        // post, and a key for one repository is not used on another.
        foreach (var key in new[] { push, merge, elsewhere })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => host.CodeHost.OpenPullRequest(GitHubHost.User, key, GitHubHost.Platform, "x", "main", "t", "b", Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => host.CodeHost.CommentOnIssue(GitHubHost.User, key, GitHubHost.Platform, 1, "b", Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => host.CodeHost.AddLabel(GitHubHost.User, key, GitHubHost.Platform, 1, "x", Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => host.CodeHost.RemoveLabel(GitHubHost.User, key, GitHubHost.Platform, 1, "x", Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => host.CodeHost.OpenIssue(GitHubHost.User, key, GitHubHost.Platform, "t", "b", [], Ct));
        }

        foreach (var key in new[] { push, post, await Key(host, WritePurpose.Merge, GitHubHost.Platform with { Installation = 42 }) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => host.CodeHost.MergeAsApp(GitHubHost.User, key, GitHubHost.Platform, 1, Head, Ct));
        }

        Assert.Empty(host.Stub.Reads);
    }

    [Fact]
    public async Task TheCloneAddressIsGitHubsAndAsksForNothing()
    {
        using var host = new GitHubHost();

        var address = Results.Value(await host.CodeHost.CloneAddress(GitHubHost.User, GitHubHost.Platform, Ct));

        Assert.Equal(new Uri("https://github.com/Adamniels/Personal-platform.git"), address);
        Assert.Empty(host.Stub.Requests);
    }
}