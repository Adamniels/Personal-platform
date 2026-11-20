using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// Bringing one branch into another as the app, against GitHub's documented form, read on
/// 2026-11-20: <c>POST /repos/{owner}/{repo}/merges</c> with <c>base</c>, <c>head</c> and
/// <c>commit_message</c>, with a key for merging alone. <c>201</c> answers the merge commit, written
/// by hand in <c>contracts/github/branch-merge.json</c> from GitHub's documented answer; <c>204</c>
/// is nothing to bring in, since the base holds the head already, and <c>409</c> a conflict. See
/// "The end of a feature" in devflow/README.md.
/// </summary>
public sealed class GitHubBranchMergeTests
{
    private const string Repo = "/repos/Adamniels/Personal-platform";
    private const string Feature = "feature-361-dev-flow-features";
    private const string Said = "Bring main into feature-361-dev-flow-features";

    private static CancellationToken Ct => GitHubHost.Ct;

    private static HttpResponseMessage Message(HttpStatusCode status, string message) =>
        StubGitHub.Json(new JsonObject { ["message"] = message, ["documentation_url"] = "https://docs.github.com/rest/branches/branches#merge-a-branch" }.ToJsonString(), status);

    private static async Task<AppWriteToken> Key(GitHubHost host, WritePurpose purpose = WritePurpose.Merge) =>
        Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, purpose, Ct));

    [Fact]
    public async Task BringingABranchInAnswersItsCommit()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => (request.Method.Method, request.Path) switch
        {
            ("POST", $"{Repo}/merges") => StubGitHub.Json(Fixture.Text("branch-merge.json"), HttpStatusCode.Created),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var merge = await Key(host);

        var brought = await host.CodeHost.BringIn(GitHubHost.User, merge, GitHubHost.Platform, Feature, "main", Said, Ct);

        // The merge commit, and the commit of the head it brought in, its second parent.
        var recorded = Fixture.Node("branch-merge.json");
        var made = Assert.IsType<BringInResult.Merged>(brought);
        Assert.Equal((string)recorded["sha"]!, made.Commit);
        Assert.Equal((string)recorded["parents"]![1]!["sha"]!, made.BroughtIn);

        // One request, as GitHub documents it, with the merging key, never a person's token.
        var request = Assert.Single(host.Stub.Reads);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{Repo}/merges", request.Path);
        Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(
            new JsonObject { ["base"] = Feature, ["head"] = "main", ["commit_message"] = Said },
            JsonNode.Parse(request.Body!)), request.Body);
        Assert.Equal($"Bearer {StubGitHub.FixtureToken}", request.Header("Authorization"));
        Assert.Equal("application/vnd.github+json", request.Header("Accept"));
        Assert.Equal("2022-11-28", request.Header("X-GitHub-Api-Version"));
        Assert.DoesNotContain(StubGitHub.FixtureToken, string.Join('\n', host.Logs.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachOtherAnswerReadsAsTheInterfacesResult()
    {
        using var host = new GitHubHost();
        var merge = await Key(host);
        Task<BringInResult> Bring() => host.CodeHost.BringIn(GitHubHost.User, merge, GitHubHost.Platform, Feature, "main", Said, Ct);

        // 204: the base holds the head already, so nothing was made.
        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        Assert.IsType<BringInResult.NothingToBringIn>(await Bring());

        // 409: the two conflict, and nothing was made.
        host.Stub.Answer = _ => Message(HttpStatusCode.Conflict, "Merge conflict");
        Assert.IsType<BringInResult.Conflict>(await Bring());

        // Any other refusal, with GitHub's status and words: a base or head it does not know, or a
        // key without contents.
        host.Stub.Answer = _ => Message(HttpStatusCode.NotFound, "Base does not exist");
        Assert.Equal(new BringInResult.Refused(404, "Base does not exist"), await Bring());
        host.Stub.Answer = _ => Message(HttpStatusCode.Forbidden, "Resource not accessible by integration");
        Assert.Equal(new BringInResult.Refused(403, "Resource not accessible by integration"), await Bring());

        // A 201 whose commit does not read: merged, but which commit is not known, so nothing can be
        // recorded and the watch stops the project when it finds the push.
        host.Stub.Answer = _ => StubGitHub.Json("""{"sha": "not a commit", "parents": []}""", HttpStatusCode.Created);
        Assert.Equal(new BringInResult.Merged(null, null), await Bring());

        // No answer: whether it was made is not known.
        host.Stub.Answer = _ => throw new HttpRequestException("the network is down");
        Assert.IsType<BringInResult.Unavailable>(await Bring());
    }

    [Fact]
    public async Task OnlyAKeyForMergingBringsIn()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var push = await Key(host, WritePurpose.Push);

        // Refused before GitHub is asked, as every write refuses a key for another purpose.
        await Assert.ThrowsAsync<ArgumentException>(() => host.CodeHost.BringIn(GitHubHost.User, push, GitHubHost.Platform, Feature, "main", Said, Ct));
        Assert.Empty(host.Stub.Reads);
    }
}