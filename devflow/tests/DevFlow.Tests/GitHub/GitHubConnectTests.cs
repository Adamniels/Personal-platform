using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

// The tests' own Repository, the repository's files, would hide the code host's record.
using CodeRepository = DevFlow.CodeHost.Repository;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The adapter's listing of what a person can connect: the installations of the app they can
/// reach, and the repositories of one, read with the person's own token and never an
/// installation's, against GitHub's documented answers in contracts/github/, written by hand since
/// a real one needs a person's token for the app. See "GitHub" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class GitHubConnectTests
{
    private const string UserToken = "ghu_madeUpForTheConnectTestsNotARealToken00";

    private static CancellationToken Ct => GitHubHost.Ct;

    private static Func<Received, HttpResponseMessage> Serve(params (string Path, string Fixture)[] answers) =>
        request => answers.FirstOrDefault(answer => answer.Path == request.Path) is { Fixture: { } fixture }
            ? StubGitHub.Json(Fixture.Text(fixture))
            : new HttpResponseMessage(HttpStatusCode.NotFound);

    [Fact]
    public async Task InstallationsAndTheirRepositoriesReadFromGitHubsDocumentedForm()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = Serve(
            ("/user/installations", "user-installations.json"),
            ("/user/installations/41/repositories", "user-installation-repositories.json"));

        var installations = Results.Value(await host.CodeHost.Installations(GitHubHost.User, UserToken, Ct));
        var repositories = Results.Value(await host.CodeHost.Repositories(GitHubHost.User, UserToken, 41, Ct));

        // Every field from the file, read here on its own.
        var expectedInstallations = Fixture.Node("user-installations.json")["installations"]!.AsArray();
        Assert.Equal(
            expectedInstallations.Select(node => new Installation((long)node!["id"]!, (string)node["account"]!["login"]!)),
            installations);
        Assert.Equal([new Installation(41, "octo-owner"), new Installation(52, "octo-lab")], installations);
        var expectedRepositories = Fixture.Node("user-installation-repositories.json")["repositories"]!.AsArray();
        Assert.Equal(
            expectedRepositories.Select(node => new CodeRepository(41, (long)node!["id"]!, (string)node["owner"]!["login"]!, (string)node["name"]!)),
            repositories);
        Assert.Equal(2, repositories.Count);

        // As the person, with their token, a hundred a page; never as the app, so no installation
        // token was asked for.
        Assert.Empty(host.Stub.TokenRequests);
        Assert.Equal(["/user/installations", "/user/installations/41/repositories"], host.Stub.Reads.Select(read => read.Path));
        Assert.All(host.Stub.Reads, read =>
        {
            Assert.Equal(HttpMethod.Get, read.Method);
            Assert.Equal($"Bearer {UserToken}", read.Header("Authorization"));
            Assert.Equal("100", read.Query["per_page"]);
            Assert.Equal("application/vnd.github+json", read.Header("Accept"));
            Assert.Equal("2022-11-28", read.Header("X-GitHub-Api-Version"));
        });
        Assert.DoesNotContain(UserToken, string.Join('\n', host.Logs.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryPageIsFollowed()
    {
        using var host = new GitHubHost();
        var first = Fixture.Node("user-installations.json").AsObject();
        var second = first.DeepClone().AsObject();
        first["installations"] = new JsonArray(first["installations"]![0]!.DeepClone());
        second["installations"] = new JsonArray(second["installations"]![1]!.DeepClone());
        host.Stub.Answer = request =>
        {
            if (request.Query.GetValueOrDefault("page") == "2")
            {
                return StubGitHub.Json(second.ToJsonString());
            }

            var answer = StubGitHub.Json(first.ToJsonString());
            answer.Headers.Add("Link", "<https://api.github.com/user/installations?per_page=100&page=2>; rel=\"next\"");
            return answer;
        };

        var installations = Results.Value(await host.CodeHost.Installations(GitHubHost.User, UserToken, Ct));

        Assert.Equal([new Installation(41, "octo-owner"), new Installation(52, "octo-lab")], installations);
        Assert.Equal(2, host.Stub.Reads.Count);
    }

    [Fact]
    public async Task AnInstallationTheyCannotReachIsNotFound()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = Serve(("/user/installations/41/repositories", "user-installation-repositories.json"));

        Assert.IsType<CodeHostResult<IReadOnlyList<CodeRepository>>.NotFound>(await host.CodeHost.Repositories(GitHubHost.User, UserToken, 77, Ct));
    }

    [Fact]
    public async Task AFailureIsUnavailable()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        Assert.IsType<CodeHostResult<IReadOnlyList<Installation>>.Unavailable>(await host.CodeHost.Installations(GitHubHost.User, UserToken, Ct));

        // And a list that does not parse is unavailable rather than half read.
        host.Stub.Answer = _ => StubGitHub.Json("""{"total_count": 1, "installations": [{"id": "41"}]}""");
        Assert.IsType<CodeHostResult<IReadOnlyList<Installation>>.Unavailable>(await host.CodeHost.Installations(GitHubHost.User, UserToken, Ct));
    }

    [Fact]
    public void TheInstallAddressIsTheAppsPageOnGitHub()
    {
        using var host = new GitHubHost();

        Assert.Equal(
            new Uri($"https://github.com/apps/{TestApp.AppSlug}/installations/new"),
            host.CodeHost.InstallAddress(new Uri("https://platform.test")));
    }
}