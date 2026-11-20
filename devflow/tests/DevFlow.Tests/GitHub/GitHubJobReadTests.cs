using System.Net;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// What handing out a job reads from GitHub as the app, against GitHub's real answers recorded in
/// contracts/github/: the repository's default branch, which a job's branch is made from, and the
/// app's bot, whose name and no-reply address a worker commits as, read once and kept. See "Jobs"
/// in devflow/README.md and contracts/README.md.
/// </summary>
public sealed class GitHubJobReadTests
{
    private const string Repo = "/repos/Adamniels/Personal-platform";
    private const string BotPath = $"/users/{TestApp.AppSlug}%5Bbot%5D";

    private static CancellationToken Ct => GitHubHost.Ct;

    /// <summary>The recorded bot, renamed to the test app's, as GitHub answers for the app's own
    /// slug.</summary>
    private static string Bot(string? login = null)
    {
        var bot = Fixture.Node("bot-user.json").AsObject();
        bot["login"] = login ?? $"{TestApp.AppSlug}[bot]";
        return bot.ToJsonString();
    }

    [Fact]
    public async Task TheDefaultBranchIsTheRepositorys()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => request.Path == Repo
            ? StubGitHub.Json(Fixture.Text("repository.json"))
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        var branch = Results.Value(await host.CodeHost.DefaultBranch(GitHubHost.User, GitHubHost.Platform, Ct));

        Assert.Equal((string)Fixture.Node("repository.json")["default_branch"]!, branch);
        var read = Assert.Single(host.Stub.Reads);
        Assert.Equal(HttpMethod.Get, read.Method);
        Assert.Equal(Repo, read.Path);
        Assert.Equal($"Bearer {StubGitHub.FixtureToken}", read.Header("Authorization"));
    }

    [Fact]
    public async Task TheFilesAHeadChangedAreCompared()
    {
        const string From = "e437065c9ae0bd12e33d638268ef827aacace112";
        const string Head = "812a7cf8b9afa1482500f8c480ad2b00399af025";
        using var host = new GitHubHost();
        host.Stub.Answer = request => request.Path == $"{Repo}/compare/{From}...{Head}"
            ? StubGitHub.Json(Fixture.Text("compare.json"))
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        var files = Results.Value(await host.CodeHost.ChangedFiles(GitHubHost.User, GitHubHost.Platform, From, Head, Ct));

        // Each file's path, as GitHub lists them, read as the app with one commit a page.
        Assert.Equal([.. Fixture.Node("compare.json")["files"]!.AsArray().Select(file => (string)file!["filename"]!)], files);
        var read = Assert.Single(host.Stub.Reads);
        Assert.Equal(HttpMethod.Get, read.Method);
        Assert.Equal("1", read.Query["per_page"]);
        Assert.Equal($"Bearer {StubGitHub.FixtureToken}", read.Header("Authorization"));

        // Nothing but two full commit ids is ever sent.
        await Assert.ThrowsAsync<ArgumentException>(() => host.CodeHost.ChangedFiles(GitHubHost.User, GitHubHost.Platform, "main", Head, Ct));
        Assert.Single(host.Stub.Reads);
    }

    [Fact]
    public async Task TheCommitterIsTheAppsBotReadOnce()
    {
        using var host = new GitHubHost();
        host.Stub.Answer = request => request.Path == BotPath
            ? StubGitHub.Json(Bot())
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        var committer = Results.Value(await host.CodeHost.AppCommitter(GitHubHost.User, GitHubHost.Platform, Ct));

        // The bot's login, and GitHub's no-reply address for it, by its id.
        var id = (long)Fixture.Node("bot-user.json")["id"]!;
        Assert.Equal($"{TestApp.AppSlug}[bot]", committer.Name);
        Assert.Equal($"{id}+{TestApp.AppSlug}[bot]@users.noreply.github.com", committer.Email);
        var read = Assert.Single(host.Stub.Reads);
        Assert.Equal(HttpMethod.Get, read.Method);
        Assert.Equal(BotPath, read.Uri.AbsolutePath);

        // Kept: the second job asks GitHub nothing.
        Assert.Equal(committer, Results.Value(await host.CodeHost.AppCommitter(GitHubHost.User, GitHubHost.Platform, Ct)));
        Assert.Single(host.Stub.Reads);
    }

    [Fact]
    public async Task ABotThatIsNotTheAppsIsNotTaken()
    {
        // GitHub answering another account than the one asked for is not a committer to use, and
        // nothing is kept.
        using var host = new GitHubHost();
        host.Stub.Answer = request => request.Path == BotPath
            ? StubGitHub.Json(Bot("dependabot[bot]"))
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        Assert.IsType<CodeHostResult<Committer>.Unavailable>(await host.CodeHost.AppCommitter(GitHubHost.User, GitHubHost.Platform, Ct));

        host.Stub.Answer = request => StubGitHub.Json(Bot());
        Assert.Equal($"{TestApp.AppSlug}[bot]", Results.Value(await host.CodeHost.AppCommitter(GitHubHost.User, GitHubHost.Platform, Ct)).Name);
    }

    [Fact]
    public async Task TheRecordedBotHoldsWhatTheAdapterReads()
    {
        var bot = Fixture.Node("bot-user.json");
        Assert.EndsWith("[bot]", (string)bot["login"]!, StringComparison.Ordinal);
        Assert.Equal("Bot", (string)bot["type"]!);
        Assert.True((long)bot["id"]! > 0);
    }
}