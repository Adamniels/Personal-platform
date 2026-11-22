using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The adapter's two writes against GitHub's documented form, read on 2026-11-15: merging is
/// <c>PUT /repos/{owner}/{repo}/pulls/{number}/merge</c> with <c>sha</c> and <c>merge_method</c>,
/// answering <c>409</c> when the head did not match and <c>405</c> when it cannot merge; commenting
/// is <c>POST /repos/{owner}/{repo}/issues/{number}/comments</c> with <c>body</c>. Both with the
/// person's own token and never an installation's. See "Acting from the page" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class GitHubWriteTests
{
    private const string UserToken = "ghu_madeUpForTheWriteTestsNotARealToken0000";

    private static readonly string Head = new('a', 40);

    private static CancellationToken Ct => GitHubHost.Ct;

    private static UserAccessToken Token => new(UserToken);

    private static HttpResponseMessage Message(HttpStatusCode status, string message) =>
        StubGitHub.Json(new JsonObject { ["message"] = message, ["documentation_url"] = "https://docs.github.com/rest" }.ToJsonString(), status);

    [Fact]
    public async Task MergeAndCommentMatchGitHubsDocumentedForm()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => request.Method == HttpMethod.Put
            ? StubGitHub.Json("""{"sha":"6dcb09b5b57875f334f61aebed695e2e4193db5e","merged":true,"message":"Pull Request successfully merged"}""")
            : StubGitHub.Json("""{"id":1,"body":"Rename it.","user":{"login":"octo-owner"}}""", HttpStatusCode.Created);

        var merged = await host.CodeHost.Merge(GitHubHost.User, Token, GitHubHost.Platform, 42, Head, Ct);
        var posted = await host.CodeHost.Comment(GitHubHost.User, Token, GitHubHost.Platform, 42, "Rename it.\n\nThen merge.", Ct);

        Assert.IsType<MergeResult.Merged>(merged);
        // The comment's id, from GitHub's answer, by which Dev flow knows it later.
        Assert.Equal(1, Assert.IsType<CommentResult.Posted>(posted).Id);
        // As the person, and never as the app: no installation token was asked for.
        Assert.Empty(host.Stub.TokenRequests);
        var (merge, comment) = (host.Stub.Reads[0], host.Stub.Reads[1]);
        Assert.Equal(2, host.Stub.Reads.Count);
        Assert.Equal(HttpMethod.Put, merge.Method);
        Assert.Equal("/repos/Adamniels/Personal-platform/pulls/42/merge", merge.Path);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["sha"] = Head, ["merge_method"] = "merge" }, JsonNode.Parse(merge.Body!)), merge.Body);
        Assert.Equal(HttpMethod.Post, comment.Method);
        Assert.Equal("/repos/Adamniels/Personal-platform/issues/42/comments", comment.Path);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["body"] = "Rename it.\n\nThen merge." }, JsonNode.Parse(comment.Body!)), comment.Body);
        Assert.All(host.Stub.Reads, request =>
        {
            Assert.Equal($"Bearer {UserToken}", request.Header("Authorization"));
            Assert.Equal("application/vnd.github+json", request.Header("Accept"));
            Assert.Equal("2022-11-28", request.Header("X-GitHub-Api-Version"));
            Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
        });
        Assert.DoesNotContain(UserToken, string.Join('\n', host.Logs.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A409IsHeadMovedAndA405IsNotMergeable()
    {
        using var host = new GitHubHost();

        host.Stub.Answer = _ => Message(HttpStatusCode.Conflict, "Head branch was modified. Review and try the merge again.");
        Assert.IsType<MergeResult.HeadMoved>(await host.CodeHost.Merge(GitHubHost.User, Token, GitHubHost.Platform, 42, Head, Ct));

        host.Stub.Answer = _ => Message(HttpStatusCode.MethodNotAllowed, "Pull Request is not mergeable");
        Assert.Equal(new MergeResult.NotMergeable("Pull Request is not mergeable"), await host.CodeHost.Merge(GitHubHost.User, Token, GitHubHost.Platform, 42, Head, Ct));
    }

    [Fact]
    public async Task AnyOtherRefusalIsRefusedWithItsStatus()
    {
        using var host = new GitHubHost();

        host.Stub.Answer = _ => Message(HttpStatusCode.Forbidden, "Resource not accessible by integration");
        Assert.Equal(new MergeResult.Refused(403, "Resource not accessible by integration"), await host.CodeHost.Merge(GitHubHost.User, Token, GitHubHost.Platform, 42, Head, Ct));
        Assert.Equal(new CommentResult.Refused(403, "Resource not accessible by integration"), await host.CodeHost.Comment(GitHubHost.User, Token, GitHubHost.Platform, 42, "Hi.", Ct));

        host.Stub.Answer = _ => Message(HttpStatusCode.Unauthorized, "Bad credentials");
        Assert.Equal(new MergeResult.Refused(401, "Bad credentials"), await host.CodeHost.Merge(GitHubHost.User, Token, GitHubHost.Platform, 42, Head, Ct));

        // An answer without GitHub's words keeps its status alone, and a long one is cut.
        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(new CommentResult.Refused(422, null), await host.CodeHost.Comment(GitHubHost.User, Token, GitHubHost.Platform, 42, "Hi.", Ct));
        host.Stub.Answer = _ => Message(HttpStatusCode.UnprocessableEntity, new string('x', 2000));
        var cut = Assert.IsType<CommentResult.Refused>(await host.CodeHost.Comment(GitHubHost.User, Token, GitHubHost.Platform, 42, "Hi.", Ct));
        Assert.Equal(500, cut.Message!.Length);
    }

    [Fact]
    public async Task NoAnswerIsUnavailable()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => throw new HttpRequestException("the network is down");

        Assert.IsType<MergeResult.Unavailable>(await host.CodeHost.Merge(GitHubHost.User, Token, GitHubHost.Platform, 42, Head, Ct));
        Assert.IsType<CommentResult.Unavailable>(await host.CodeHost.Comment(GitHubHost.User, Token, GitHubHost.Platform, 42, "Hi.", Ct));
    }

    [Fact]
    public void AWriteTakesAUserTokenAndNothingElseThatCouldAuthoriseIt()
    {
        // The two writes take a UserAccessToken, and no string, a type an installation's token
        // is, apart from the commit and the text.
        foreach (var name in new[] { nameof(ICodeHost.Merge), nameof(ICodeHost.Comment) })
        {
            var parameters = typeof(ICodeHost).GetMethod(name)!.GetParameters();
            Assert.Single(parameters, parameter => parameter.ParameterType == typeof(UserAccessToken));
            Assert.Equal(
                name == nameof(ICodeHost.Merge) ? ["commit"] : ["body"],
                parameters.Where(parameter => parameter.ParameterType == typeof(string)).Select(parameter => parameter.Name));
        }

        // Its one way in is a value, never a conversion from a string, and it prints nothing.
        Assert.DoesNotContain(typeof(UserAccessToken).GetMethods(BindingFlags.Public | BindingFlags.Static), method => method.Name is "op_Implicit" or "op_Explicit");
        Assert.DoesNotContain(UserToken, Token.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AUserTokenIsMadeOnlyByUserTokens()
    {
        // Its constructor is internal, so nothing outside the service can make one, and inside it
        // the only code that does is UserTokens, from the person's stored token. Read from the
        // compiled code rather than the source, so a target typed new() is found too.
        // The record's own copy constructor, which a with expression calls, only copies a token
        // that already exists, so the one constructor that matters is the one from a string.
        var constructors = typeof(UserAccessToken).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(constructor => constructor.GetParameters() is not [{ ParameterType: var copied }] || copied != typeof(UserAccessToken))
            .ToList();
        var fromString = Assert.Single(constructors);
        Assert.Equal([typeof(string)], fromString.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.True(fromString.IsAssembly);

        var makers = IlScan.MethodsCalling(typeof(UserAccessToken).Assembly, [fromString])
            .Select(method => IlScan.Outermost(method.DeclaringType!))
            .Distinct()
            .ToList();
        Assert.Equal([typeof(DevFlow.Link.UserTokens)], makers);
    }
}