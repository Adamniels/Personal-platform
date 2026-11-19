using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// Making a branch as the app, against GitHub's documented form, read on 2026-11-19: the branch it
/// is made from is read with <c>GET /repos/{owner}/{repo}/git/ref/heads/{branch}</c>, answered with
/// contracts/github/ref.json, and the branch made at that commit with
/// <c>POST /repos/{owner}/{repo}/git/refs</c>, <c>ref</c> and <c>sha</c>, answered with
/// contracts/github/ref-created.json; both with a push key, never a person's token. A name that
/// exists is GitHub's <c>422</c> "Reference already exists". See "The program's own writes" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class GitHubBranchTests
{
    private const string Repo = "/repos/Adamniels/Personal-platform";
    private const string Name = "feature-361-dev-flow-features";

    private static CancellationToken Ct => GitHubHost.Ct;

    private static HttpResponseMessage Message(HttpStatusCode status, string message) =>
        StubGitHub.Json(new JsonObject { ["message"] = message, ["documentation_url"] = "https://docs.github.com/rest/git/refs#create-a-reference" }.ToJsonString(), status);

    private static async Task<AppWriteToken> Key(GitHubHost host, WritePurpose purpose = WritePurpose.Push) =>
        Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, purpose, Ct));

    [Fact]
    public async Task MakingABranchAnswersItsRef()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => (request.Method.Method, request.Path) switch
        {
            ("GET", $"{Repo}/git/ref/heads/main") => StubGitHub.Json(Fixture.Text("ref.json")),
            ("POST", $"{Repo}/git/refs") => StubGitHub.Json(Fixture.Text("ref-created.json"), HttpStatusCode.Created),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var push = await Key(host);

        var made = await host.CodeHost.MakeBranch(GitHubHost.User, push, GitHubHost.Platform, Name, "main", Ct);

        // The commit the branch was made at, as the answer to making it says.
        Assert.Equal((string)Fixture.Node("ref-created.json")["object"]!["sha"]!, Assert.IsType<BranchResult.Made>(made).Commit);

        // The requests, as GitHub documents them: the head of main, then the ref at it.
        var reads = host.Stub.Reads;
        Assert.Equal(2, reads.Count);
        Assert.Equal(HttpMethod.Get, reads[0].Method);
        Assert.Equal($"{Repo}/git/ref/heads/main", reads[0].Path);
        Assert.True(string.IsNullOrEmpty(reads[0].Body));
        Assert.Equal(HttpMethod.Post, reads[1].Method);
        Assert.Equal($"{Repo}/git/refs", reads[1].Path);
        Assert.StartsWith("application/json", reads[1].Header("Content-Type"), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(
            new JsonObject { ["ref"] = $"refs/heads/{Name}", ["sha"] = (string)Fixture.Node("ref.json")["object"]!["sha"]! },
            JsonNode.Parse(reads[1].Body!)));
        Assert.All(reads, request =>
        {
            // The app's key, never a person's token.
            Assert.Equal($"Bearer {StubGitHub.FixtureToken}", request.Header("Authorization"));
            Assert.Equal("application/vnd.github+json", request.Header("Accept"));
            Assert.Equal("2022-11-28", request.Header("X-GitHub-Api-Version"));
        });
        Assert.DoesNotContain(StubGitHub.FixtureToken, string.Join('\n', host.Logs.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCommitIsTheOneTheAnswerNames()
    {
        // GitHub names the commit it made the branch at; that is the one answered, not the one sent.
        var other = Fixture.Node("ref-created.json");
        other["object"]!["sha"] = new string('b', 40);
        using var host = new GitHubHost();
        host.Stub.Answer = request => (request.Method.Method, request.Path) switch
        {
            ("GET", $"{Repo}/git/ref/heads/main") => StubGitHub.Json(Fixture.Text("ref.json")),
            ("POST", $"{Repo}/git/refs") => StubGitHub.Json(other.ToJsonString(), HttpStatusCode.Created),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var made = await host.CodeHost.MakeBranch(GitHubHost.User, await Key(host), GitHubHost.Platform, Name, "main", Ct);

        Assert.Equal(new string('b', 40), Assert.IsType<BranchResult.Made>(made).Commit);
    }

    [Fact]
    public async Task ANameThatExistsReadsAsABranchThatExists()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => (request.Method.Method, request.Path) switch
        {
            ("GET", $"{Repo}/git/ref/heads/main") => StubGitHub.Json(Fixture.Text("ref.json")),
            ("POST", $"{Repo}/git/refs") => Message(HttpStatusCode.UnprocessableEntity, "Reference already exists"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        Assert.IsType<BranchResult.Exists>(await host.CodeHost.MakeBranch(GitHubHost.User, await Key(host), GitHubHost.Platform, Name, "main", Ct));
    }

    [Fact]
    public async Task AnyOtherAnswerIsNotABranchThatExists()
    {
        // Another 422, such as a name git does not take, is refused with GitHub's words; the branch
        // made from missing is refused before anything is made; no answer is unavailable.
        using var host = new GitHubHost();
        var fromMissing = false;
        host.Stub.Answer = request => (request.Method.Method, request.Path) switch
        {
            ("GET", $"{Repo}/git/ref/heads/main") when fromMissing => Message(HttpStatusCode.NotFound, "Not Found"),
            ("GET", $"{Repo}/git/ref/heads/main") => StubGitHub.Json(Fixture.Text("ref.json")),
            ("POST", $"{Repo}/git/refs") => Message(HttpStatusCode.UnprocessableEntity, "Reference update failed"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var invalid = Assert.IsType<BranchResult.Refused>(await host.CodeHost.MakeBranch(GitHubHost.User, await Key(host), GitHubHost.Platform, Name, "main", Ct));
        Assert.Equal(422, invalid.Status);
        Assert.Equal("Reference update failed", invalid.Message);

        fromMissing = true;
        var before = host.Stub.Reads.Count;
        var missing = Assert.IsType<BranchResult.Refused>(await host.CodeHost.MakeBranch(GitHubHost.User, await Key(host), GitHubHost.Platform, Name, "main", Ct));
        Assert.Equal(404, missing.Status);
        Assert.Equal(before + 1, host.Stub.Reads.Count);

        host.Stub.Answer = _ => throw new HttpRequestException("the network is down");
        Assert.IsType<BranchResult.Unavailable>(await host.CodeHost.MakeBranch(GitHubHost.User, await Key(host), GitHubHost.Platform, Name, "main", Ct));
    }

    [Fact]
    public async Task OnlyAPushKeyMakesABranch()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var post = await Key(host, WritePurpose.Post);

        await Assert.ThrowsAsync<ArgumentException>(() => host.CodeHost.MakeBranch(GitHubHost.User, post, GitHubHost.Platform, Name, "main", Ct));
        Assert.Empty(host.Stub.Reads);
    }
}