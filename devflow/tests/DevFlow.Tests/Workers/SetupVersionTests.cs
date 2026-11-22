using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Recipes;
using DevFlow.Tests.GitHub;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Workers;

/// <summary>
/// A project's setup version: the newest commit of the default branch that changed anything under
/// <c>.devflow/</c>, its recipe and its <c>project.json</c> alike, which a worker must be built from
/// and which is checked once before any other job. See "Setting up a project" in
/// devflow/README.md.
/// </summary>
public sealed class SetupVersionTests
{
    private const string Recipe = "ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\n";

    private const string Repo = "/repos/Adamniels/Personal-platform";

    private static CancellationToken Ct => Workers.Ct;

    private static byte[] ProjectFile(string check) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["version"] = 1, ["check"] = check, ["start"] = "", ["url"] = "" }.ToJsonString());

    [Fact]
    public async Task TheVersionCoversTheWholeFolder()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);
        var recipe = Encoding.UTF8.GetBytes(Recipe);
        var first = ProjectFile("npm ci && npm test");
        RecipeTests.Stage(routes, Workers.User, (".devflow/Dockerfile", "main", recipe), (".devflow/project.json", "main", first), ("README.md", "main", "hello\n"u8.ToArray()));

        // The recipe route answers the folder's version, not the Dockerfile's alone.
        var served = await Workers.GetRecipe(routes, key);
        Assert.Equal(StatusCodes.Status200OK, served.Response.StatusCode);
        Assert.Equal(Recipe, await Harness.Body(served));
        var version = FakeCodeHost.FolderVersion([(".devflow/Dockerfile", recipe), (".devflow/project.json", first)]);
        Assert.Equal(version, (string?)served.Response.Headers[RecipeEndpoints.VersionHeader]);
        Assert.Equal(version, (string?)(await Workers.List(routes, project))["recipe_version"]);

        var worker = Guid.Parse("0190a8f0-0000-7000-8000-0000000000f1");
        await Hello(routes, key, worker, version);
        Assert.Null(Rebuild(await Workers.List(routes, project)));

        // A file outside the folder changes nothing.
        RecipeTests.Stage(routes, Workers.User, (".devflow/Dockerfile", "main", recipe), (".devflow/project.json", "main", first), ("README.md", "main", "changed\n"u8.ToArray()));
        await routes.Read(project, Workers.User);
        Assert.Equal(version, (string?)(await Workers.List(routes, project))["recipe_version"]);
        Assert.Null(Rebuild(await Workers.List(routes, project)));

        // A commit changing only project.json gives a new version, and asks the worker to rebuild.
        var second = ProjectFile("npm ci && npm run test:all");
        RecipeTests.Stage(routes, Workers.User, (".devflow/Dockerfile", "main", recipe), (".devflow/project.json", "main", second));
        await routes.Read(project, Workers.User);
        var next = FakeCodeHost.FolderVersion([(".devflow/Dockerfile", recipe), (".devflow/project.json", second)]);
        Assert.NotEqual(version, next);
        Assert.Equal(next, (string?)(await Workers.List(routes, project))["recipe_version"]);
        Assert.Equal("recipe", Rebuild(await Workers.List(routes, project)));
        Assert.Contains(routes.Host.Calls, call => call.Operation == "FolderFile" && call.User == Workers.User);

        // The recipe route hands out the new version too, the Dockerfile unchanged.
        Assert.Equal(next, (string?)(await Workers.GetRecipe(routes, key)).Response.Headers[RecipeEndpoints.VersionHeader]);
    }

    [Fact]
    public async Task GitHubAnswersTheNewestCommitUnderTheFolder()
    {
        using var host = new GitHubHost();
        const string commit = "4f6c1c36b0bd2c24e3d5a8f5d8c0e0a3a0e3b9a1";
        var bytes = Encoding.UTF8.GetBytes(Recipe);
        host.Stub.Answer = request => request.Path switch
        {
            $"{Repo}/commits" => StubGitHub.Json($$"""[{"sha": "{{commit}}"}]"""),
            $"{Repo}/contents/.devflow/Dockerfile" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var read = GitHub.Results.Value(await host.CodeHost.FolderFile(GitHubHost.User, GitHubHost.Platform, ".devflow", ".devflow/Dockerfile", Ct));

        Assert.Equal(commit, read.Commit);
        Assert.Equal(bytes, read.Content);
        // The newest commit of the default branch that changed anything under the folder; then the
        // file at exactly that commit, so the bytes and the version agree.
        Assert.Equal(new Dictionary<string, string> { ["path"] = ".devflow", ["per_page"] = "1" }, host.Stub.Reads[0].Query);
        Assert.Equal(new Dictionary<string, string> { ["ref"] = commit }, host.Stub.Reads[1].Query);

        // A folder no commit changed is not found, and the file is not asked for.
        host.Stub.Answer = request => request.Path == $"{Repo}/commits"
            ? StubGitHub.Json("[]")
            : throw new InvalidOperationException("the contents were asked for");
        Assert.Equal("NotFound { }", (await host.CodeHost.FolderFile(GitHubHost.User, GitHubHost.Platform, ".devflow", ".devflow/Dockerfile", Ct)).ToString());
    }

    [Fact]
    public async Task GitHubAnswersABranchsHead()
    {
        // The commit a check is handed: the default branch's head, read from its ref, each segment
        // of the branch's name escaped.
        using var host = new GitHubHost();
        const string commit = "4f6c1c36b0bd2c24e3d5a8f5d8c0e0a3a0e3b9a1";
        host.Stub.Answer = request => request.Path == $"{Repo}/git/ref/heads/release/2026%2011"
            ? StubGitHub.Json($$$"""{"ref": "refs/heads/release/2026 11", "object": {"sha": "{{{commit}}}", "type": "commit"}}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        Assert.Equal(commit, GitHub.Results.Value(await host.CodeHost.BranchHead(GitHubHost.User, GitHubHost.Platform, "release/2026 11", Ct)));
        Assert.Equal("NotFound { }", (await host.CodeHost.BranchHead(GitHubHost.User, GitHubHost.Platform, "gone", Ct)).ToString());

        // A sha that is not a commit id is an answer that does not parse.
        host.Stub.Answer = _ => StubGitHub.Json("""{"object": {"sha": "main"}}""");
        Assert.Equal("Unavailable { RetryAt =  }", (await host.CodeHost.BranchHead(GitHubHost.User, GitHubHost.Platform, "main", Ct)).ToString());
    }

    private static async Task Hello(Routes routes, string key, Guid worker, string recipe)
    {
        var json = $$"""{"worker_id": "{{worker}}", "name": "fodr-worker-1", "recipe_version": "{{recipe}}", "base_image": "{{WorkerStart.DefaultImage}}"}""";
        var response = await Workers.AsWorker(routes.App, Workers.HelloPath, json, null, Workers.Bearer(key));
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
    }

    private static string? Rebuild(JsonNode list) =>
        (string?)Assert.Single(list["workers"]!.AsArray())!["rebuild"];
}