using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using DevFlow.Recipes;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Workers;

/// <summary>
/// A worker built from an older recipe than the default branch's, or on another base image than
/// the one released, gets no work and is listed as needing a rebuild, so the page says to run the
/// command again. The recipe's version is what Dev flow last read: the recipe route and the loop's
/// reader each record it. See "Workers" in devflow/README.md.
/// </summary>
public sealed class WorkerVersionTests
{
    private const string Recipe = "ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\n";

    private static CancellationToken Ct => Workers.Ct;

    private static string Version(string recipe) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(recipe)));

    [Fact]
    public async Task AWorkerOnAnOldRecipeGetsNoWork()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);
        RecipeTests.Stage(routes, Workers.User, (".devflow/Dockerfile", "main", Encoding.UTF8.GetBytes(Recipe)));
        Assert.Equal(StatusCodes.Status200OK, (await Workers.GetRecipe(routes, key)).Response.StatusCode);
        var current = Version(Recipe);
        var image = WorkerStart.DefaultImage;
        var worker = Guid.Parse("0190a8f0-0000-7000-8000-0000000000f1");

        // On the recipe as read and the image released: no rebuild.
        await Hello(routes, key, worker, current, image);
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.AskForWork(routes, key, worker)).Response.StatusCode);
        Assert.Null(Rebuild(await Workers.List(routes, project)));

        // On an older recipe: no work, and listed as needing a rebuild for its recipe.
        await Hello(routes, key, worker, "0000000000000000000000000000000000000001", image);
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.AskForWork(routes, key, worker)).Response.StatusCode);
        Assert.Equal("recipe", Rebuild(await Workers.List(routes, project)));

        // On another base image than the one released.
        await Hello(routes, key, worker, current, "platform-hosted/devflow-worker:2026.10.01");
        Assert.Equal(StatusCodes.Status204NoContent, (await Workers.AskForWork(routes, key, worker)).Response.StatusCode);
        Assert.Equal("base_image", Rebuild(await Workers.List(routes, project)));

        // The recipe changes on the default branch, and the loop's read of the project notices.
        await Hello(routes, key, worker, current, image);
        var changed = Recipe + "RUN apt-get update\n";
        RecipeTests.Stage(routes, Workers.User, (".devflow/Dockerfile", "main", Encoding.UTF8.GetBytes(changed)));
        await routes.Read(project, Workers.User);
        Assert.Equal("recipe", Rebuild(await Workers.List(routes, project)));
        Assert.Equal(Version(changed), (string?)(await Workers.List(routes, project))["recipe_version"]);

        // Rebuilt from it, it is current again.
        await Hello(routes, key, worker, Version(changed), image);
        Assert.Null(Rebuild(await Workers.List(routes, project)));

        // A worker whose project has never had its recipe read is judged by its base image alone.
        var other = await routes.Project(Workers.User, repository: 9002, name: "other");
        var otherKey = await Workers.KeyOf(routes, Workers.User, other);
        await Hello(routes, otherKey, worker, "anything", image);
        var list = await Workers.List(routes, other);
        Assert.Null((string?)list["recipe_version"]);
        Assert.Null(Rebuild(list));
    }

    [Fact]
    public async Task ARecipeRemovedFromTheDefaultBranchIsForgotten()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);
        RecipeTests.Stage(routes, Workers.User, (".devflow/Dockerfile", "main", Encoding.UTF8.GetBytes(Recipe)));
        await routes.Read(project, Workers.User);
        Assert.Equal(Version(Recipe), (string?)(await Workers.List(routes, project))["recipe_version"]);

        // GitHub not answering keeps what was read.
        routes.Fake.SetUnavailable(Workers.User, true);
        await routes.Read(project, Workers.User);
        routes.Fake.SetUnavailable(Workers.User, false);
        Assert.Equal(Version(Recipe), (string?)(await Workers.List(routes, project))["recipe_version"]);

        RecipeTests.Stage(routes, Workers.User);
        await routes.Read(project, Workers.User);
        Assert.Null((string?)(await Workers.List(routes, project))["recipe_version"]);
        Assert.Equal(StatusCodes.Status404NotFound, (await Workers.GetRecipe(routes, key)).Response.StatusCode);
    }

    private static async Task Hello(Routes routes, string key, Guid worker, string recipe, string image)
    {
        var json = $$"""{"worker_id": "{{worker}}", "name": "fodr-worker-1", "recipe_version": "{{recipe}}", "base_image": "{{image}}"}""";
        var response = await Workers.AsWorker(routes.App, Workers.HelloPath, json, null, Workers.Bearer(key));
        Assert.Equal(StatusCodes.Status204NoContent, response.Response.StatusCode);
    }

    private static string? Rebuild(JsonNode list) =>
        (string?)Assert.Single(list["workers"]!.AsArray())!["rebuild"];
}
