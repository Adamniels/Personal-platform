using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The files a pull request changes, as the merge check reads them from GitHub as the app for the
/// list of paths of "sometimes", against GitHub's real answer recorded in
/// contracts/github/pull-files.json: every page to the end, a rename with its old name, and
/// nothing short of the whole list. See "The merge check" in devflow/README.md.
/// </summary>
public sealed class GitHubPullFilesTests
{
    private const string Repo = "/repos/Adamniels/Personal-platform";
    private const string Files = $"{Repo}/pulls/286/files";

    private static CancellationToken Ct => GitHubHost.Ct;

    [Fact]
    public async Task EachFileIsReadWithARenamesOldName()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => request.Path == Files
            ? StubGitHub.Json(Fixture.Text("pull-files.json"))
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        var files = Results.Value(await host.CodeHost.PullRequestFiles(GitHubHost.User, GitHubHost.Platform, 286, Ct));

        // As GitHub lists them, a renamed one with its previous name.
        Assert.Equal(
            [
                new ChangedFile("Brain/web/src/app.module.css", null),
                new ChangedFile("design/fonts/barlow-condensed-LICENSE.txt", "design/fonts/space-grotesk-LICENSE.txt"),
                new ChangedFile("design/fonts/barlow-condensed-latin-600-normal.woff2", null),
            ],
            files);
        var read = Assert.Single(host.Stub.Reads);
        Assert.Equal(HttpMethod.Get, read.Method);
        Assert.Equal("100", read.Query["per_page"]);
        Assert.Equal($"Bearer {StubGitHub.FixtureToken}", read.Header("Authorization"));
    }

    [Fact]
    public async Task EveryPageIsReadToTheEnd()
    {
        using var host = new GitHubHost();
        var recorded = Fixture.Node("pull-files.json").AsArray();
        const string page2 = $"https://api.github.com/repositories/1066577263/pulls/286/files?per_page=100&page=2";
        const string page3 = $"https://api.github.com/repositories/1066577263/pulls/286/files?per_page=100&page=3";
        host.Stub.Answer = request =>
        {
            var (index, next) = request.Uri.AbsoluteUri switch
            {
                page2 => (1, page3),
                page3 => (2, null),
                _ => (0, page2),
            };
            var response = StubGitHub.Json(new JsonArray(recorded[index]!.DeepClone()).ToJsonString());
            if (next is not null)
            {
                response.Headers.Add("Link", $"<{next}>; rel=\"next\", <{page3}>; rel=\"last\"");
            }

            return response;
        };

        var files = Results.Value(await host.CodeHost.PullRequestFiles(GitHubHost.User, GitHubHost.Platform, 286, Ct));

        Assert.Equal(3, files.Count);
        Assert.Equal("design/fonts/space-grotesk-LICENSE.txt", files[1].PreviousPath);
        Assert.Equal(
            [$"https://api.github.com{Files}?per_page=100", page2, page3],
            host.Stub.Reads.Select(request => request.Uri.AbsoluteUri));
    }

    /// <summary>Half a list is never the whole: a page that fails, or a file with no name, makes
    /// the read unavailable; a pull request GitHub does not have is not found.</summary>
    [Fact]
    public async Task AnythingShortOfTheWholeListIsAFailure()
    {
        using var host = new GitHubHost();
        const string page2 = $"https://api.github.com/repositories/1066577263/pulls/286/files?per_page=100&page=2";
        host.Stub.Answer = request =>
        {
            if (request.Uri.AbsoluteUri == page2)
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }

            var response = StubGitHub.Json(Fixture.Text("pull-files.json"));
            response.Headers.Add("Link", $"<{page2}>; rel=\"next\"");
            return response;
        };
        Assert.IsType<CodeHostResult<IReadOnlyList<ChangedFile>>.Unavailable>(await host.CodeHost.PullRequestFiles(GitHubHost.User, GitHubHost.Platform, 286, Ct));

        using var nameless = new GitHubHost();
        var files = Fixture.Node("pull-files.json").AsArray();
        files[1]!["filename"] = "";
        nameless.Stub.Answer = _ => StubGitHub.Json(files.ToJsonString());
        Assert.IsType<CodeHostResult<IReadOnlyList<ChangedFile>>.Unavailable>(await nameless.CodeHost.PullRequestFiles(GitHubHost.User, GitHubHost.Platform, 286, Ct));

        using var missing = new GitHubHost();
        missing.Stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.IsType<CodeHostResult<IReadOnlyList<ChangedFile>>.NotFound>(await missing.CodeHost.PullRequestFiles(GitHubHost.User, GitHubHost.Platform, 286, Ct));
    }
}