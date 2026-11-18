using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.CodeHost.GitHub;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The keys the app makes for its own writes, <see cref="AppWriteToken"/>: made only by a code
/// host's issuer, each asked for with exactly its purpose's permissions on the one repository, and
/// none ever naming <c>workflows</c>. The request body's test is what keeps each key narrow by
/// construction. See "The program's own writes" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class AppWriteTokenTests
{
    private static CancellationToken Ct => GitHubHost.Ct;

    /// <summary>Every purpose and exactly what its key asks for. A purpose added to the enum and
    /// not here fails <see cref="EachPurposeAsksForExactlyItsPermissions"/>.</summary>
    private static readonly Dictionary<WritePurpose, JsonObject> Expected = new()
    {
        [WritePurpose.Push] = new() { ["contents"] = "write", ["metadata"] = "read" },
        [WritePurpose.Post] = new() { ["issues"] = "write", ["pull_requests"] = "write", ["metadata"] = "read" },
        [WritePurpose.Merge] = new() { ["contents"] = "write", ["pull_requests"] = "read", ["metadata"] = "read" },
        [WritePurpose.Fetch] = new() { ["contents"] = "read", ["metadata"] = "read" },
    };

    [Fact]
    public void OnlyTheIssuerMakesOne()
    {
        // One constructor, internal, so nothing outside the service can make a key; read from the
        // compiled code, a target typed new() included, the only code calling it is each code
        // host's issuer: GitHub's installation tokens, and the fake, which issues its own.
        var constructor = Assert.Single(typeof(AppWriteToken).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.True(constructor.IsAssembly);
        Assert.Empty(typeof(AppWriteToken).GetConstructors());
        Assert.DoesNotContain(typeof(AppWriteToken).GetMethods(BindingFlags.Public | BindingFlags.Static), method => method.Name is "op_Implicit" or "op_Explicit");

        var makers = IlScan.MethodsCalling(typeof(AppWriteToken).Assembly, [constructor])
            .Select(method => IlScan.Outermost(method.DeclaringType!))
            .Distinct()
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal([typeof(FakeCodeHost), typeof(InstallationTokens)], makers);
    }

    [Fact]
    public async Task EachPurposeAsksForExactlyItsPermissions()
    {
        Assert.Equal(Enum.GetValues<WritePurpose>().Order(), Expected.Keys.Order());
        foreach (var (purpose, permissions) in Expected)
        {
            using var host = new GitHubHost();

            var token = Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, purpose, Ct));

            var request = Assert.Single(host.Stub.TokenRequests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.github.com/app/installations/41/access_tokens", request.Uri.AbsoluteUri);
            var body = JsonNode.Parse(request.Body!)!.AsObject();
            Assert.Equal(["permissions", "repository_ids"], body.Select(field => field.Key).Order(StringComparer.Ordinal));
            Assert.True(JsonNode.DeepEquals(permissions, body["permissions"]), $"{purpose}: {body["permissions"]!.ToJsonString()}");
            Assert.True(JsonNode.DeepEquals(new JsonArray(GitHubHost.Platform.Id), body["repository_ids"]), body["repository_ids"]!.ToJsonString());

            // The key GitHub gave, for that purpose and repository, until GitHub's expiry.
            Assert.Equal(StubGitHub.FixtureToken, token.Value);
            Assert.Equal(purpose, token.Purpose);
            Assert.Equal(GitHubHost.Platform, token.Repository);
            Assert.Equal(host.Time.GetUtcNow().AddHours(1), token.ExpiresAt);
            Assert.DoesNotContain(StubGitHub.FixtureToken, token.ToString(), StringComparison.Ordinal);
            Assert.Empty(host.Stub.Reads);
        }
    }

    [Fact]
    public async Task NoPurposeAsksForWorkflows()
    {
        using var host = new GitHubHost();

        // Every key Dev flow can ask for, the read one with them.
        foreach (var purpose in Enum.GetValues<WritePurpose>())
        {
            Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, purpose, Ct));
        }

        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 1, Ct);

        Assert.Equal(Enum.GetValues<WritePurpose>().Length + 1, host.Stub.TokenRequests.Count);
        Assert.All(host.Stub.TokenRequests, request =>
            Assert.DoesNotContain("workflow", request.Body!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AWriteKeyIsNeverKeptNorUsedToRead()
    {
        using var host = new GitHubHost();

        // A read keeps its read token; a write key is asked for anew each time, and never from
        // the kept read token.
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 1, Ct);
        var first = Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, WritePurpose.Push, Ct));
        var second = Results.Value(await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, WritePurpose.Push, Ct));
        await host.CodeHost.PullRequest(GitHubHost.User, GitHubHost.Platform, 2, Ct);

        Assert.NotSame(first, second);
        Assert.Equal(3, host.Stub.TokenRequests.Count);
        var permissions = host.Stub.TokenRequests.Select(request => (string)JsonNode.Parse(request.Body!)!["permissions"]!["contents"]!).ToList();
        Assert.Equal(["read", "write", "write"], permissions);
        // The second read used the read token kept from the first, so no write key was kept for it.
        Assert.Equal(2, host.Stub.Reads.Count);
    }

    [Fact]
    public async Task AKeyThatCannotBeHadIsAFailure()
    {
        using var host = new GitHubHost();

        // The installation is gone, the repository is not in it, or it has not granted what the
        // purpose needs, such as Issues at write level before the owner approves it: not found.
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity })
        {
            host.Stub.TokenAnswer = _ => new HttpResponseMessage(status);
            Assert.IsType<CodeHostResult<AppWriteToken>.NotFound>(
                await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, WritePurpose.Post, Ct));
        }

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError })
        {
            host.Stub.TokenAnswer = _ => new HttpResponseMessage(status);
            Assert.Equal(new CodeHostResult<AppWriteToken>.Unavailable(null),
                await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, WritePurpose.Merge, Ct));
        }

        host.Stub.TokenAnswer = _ => StubGitHub.Json("""{"token": 1}""", HttpStatusCode.Created);
        Assert.Equal(new CodeHostResult<AppWriteToken>.Unavailable(null),
            await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, WritePurpose.Push, Ct));

        host.Stub.TokenAnswer = _ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
            limited.Headers.Add("retry-after", "30");
            return limited;
        };
        Assert.Equal(new CodeHostResult<AppWriteToken>.Unavailable(host.Time.GetUtcNow().AddSeconds(30)),
            await host.CodeHost.ForWrite(GitHubHost.User, GitHubHost.Platform, WritePurpose.Push, Ct));
        Assert.Empty(host.Stub.Reads);
    }
}