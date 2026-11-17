using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Recipes;
using DevFlow.Tests.Reading;
using DevFlow.Workers;

using Microsoft.AspNetCore.Http;

using Staging = DevFlow.Tests.Reading.Reading;

namespace DevFlow.Tests.Workers;

/// <summary>
/// The recipe route, <c>GET /devflow/api/worker/recipe</c>: the project's
/// <c>.devflow/Dockerfile</c> from its default branch, handed to whoever holds the project's key,
/// as text with the commit it was read at, and refused when it would copy files into the image. See
/// "Starting a worker" in devflow/README.md.
/// </summary>
public sealed class RecipeTests
{
    private const string Recipe = """
        ARG DEVFLOW_BASE
        FROM ${DEVFLOW_BASE}
        RUN apt-get update && apt-get install -y --no-install-recommends python3 \
            && rm -rf /var/lib/apt/lists/*
        """;

    private static CancellationToken Ct => Workers.Ct;

    [Fact]
    public async Task TheDockerfileIsHandedToTheProjectsKey()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);
        var bytes = Encoding.UTF8.GetBytes(Recipe);
        Stage(routes, Workers.User, (".devflow/Dockerfile", "main", bytes), (".devflow/Dockerfile", "elsewhere", "FROM scratch\n"u8.ToArray()));

        var response = await Workers.GetRecipe(routes, key);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Response.ContentType);
        Assert.Equal(Recipe, await Harness.Body(response));
        // The fake has no history: the commit it answers for a file is the SHA-256 of its content.
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), (string?)response.Response.Headers[RecipeEndpoints.VersionHeader]);
        Assert.Equal("no-store", (string?)response.Response.Headers.CacheControl);
        Assert.Equal("nosniff", (string?)response.Response.Headers.XContentTypeOptions);
        Assert.Contains(routes.Host.Calls, call => call.Operation == "DefaultBranchFile" && call.User == Workers.User);

        // Without a key, or with another project's, nothing is read.
        Assert.Equal(StatusCodes.Status401Unauthorized, (await Workers.GetRecipe(routes, null)).Response.StatusCode);
        var bobs = await routes.Project(Workers.Bob, repository: 9002, name: "bobs");
        var bobKey = await Workers.KeyOf(routes, Workers.Bob, bobs);
        var asBob = await Workers.GetRecipe(routes, bobKey);
        Assert.Equal(StatusCodes.Status404NotFound, asBob.Response.StatusCode);
        Assert.DoesNotContain(routes.Host.Calls, call => call.User == Workers.Bob && call.Repository == Staging.RepositoryId);

        // A user's token is not a worker's key.
        Assert.Equal(StatusCodes.Status401Unauthorized, (await routes.App.Get(RecipeEndpoints.Path, Contract.Token)).Response.StatusCode);
    }

    [Fact]
    public async Task CopyAndAddAreRefused()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);

        foreach (var (recipe, line, instruction) in new[]
        {
            ("ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\nCOPY . /work\n", 3, "COPY"),
            ("FROM base\n  add https://example.com/x /x\n", 2, "ADD"),
            ("FROM base\nCopy --from=other /a /b\n", 2, "COPY"),
            // Continued lines are joined first, so an instruction cannot hide behind one.
            ("FROM base\nRUN true && \\\n    true\n\\\nCOPY x y\n", 4, "COPY"),
            ("FROM base\nRUN echo a \\\n# a comment inside the continuation\n  && echo b\nONBUILD ADD x y\n", 5, "ADD"),
            // With the escape directive the continuation is a backtick, so a backslash ends a line.
            ("# escape=`\nFROM base\nRUN echo \\\nCOPY x y\n", 4, "COPY"),
            ("FROM base\r\nADD x y\r\n", 2, "ADD"),
        })
        {
            Stage(routes, Workers.User, (".devflow/Dockerfile", "main", Encoding.UTF8.GetBytes(recipe)));

            var response = await Workers.GetRecipe(routes, key);

            Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Response.StatusCode);
            var problem = await Workers.Json(response);
            Assert.Equal(line, (int)problem["line"]!);
            Assert.Contains($"Line {line}", (string)problem["detail"]!, StringComparison.Ordinal);
            Assert.Contains(instruction, (string)problem["detail"]!, StringComparison.Ordinal);
            Assert.False(response.Response.Headers.ContainsKey(RecipeEndpoints.VersionHeader));
        }

        // A word that only begins like one, or one inside a command, is not the instruction.
        foreach (var fine in new[]
        {
            "FROM base\nRUN cp a b && echo COPY\n",
            "FROM base\nCOPYRIGHT_YEAR=2026\n",
            "FROM base\n# COPY . /work\n",
            "FROM base\nRUN echo \\\n  ADD\n",
        })
        {
            Stage(routes, Workers.User, (".devflow/Dockerfile", "main", Encoding.UTF8.GetBytes(fine)));
            Assert.Equal(StatusCodes.Status200OK, (await Workers.GetRecipe(routes, key)).Response.StatusCode);
        }
    }

    [Fact]
    public async Task AMissingDockerfileIsSaid()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);
        Stage(routes, Workers.User, (".devflow/project.json", "main", "{}"u8.ToArray()), (".devflow/Dockerfile", "feature", "FROM x\n"u8.ToArray()));

        var response = await Workers.GetRecipe(routes, key);

        Assert.Equal(StatusCodes.Status404NotFound, response.Response.StatusCode);
        Assert.Equal("This repository has no .devflow/Dockerfile", (string)(await Workers.Json(response))["detail"]!);

        // GitHub not answering is not a missing file.
        routes.Fake.SetUnavailable(Workers.User, true);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await Workers.GetRecipe(routes, key)).Response.StatusCode);
    }

    [Fact]
    public async Task ARecipeThatIsNotTextOrTooLargeIsRefused()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Workers.User);
        var key = await Workers.NewKey(routes, project);

        Stage(routes, Workers.User, (".devflow/Dockerfile", "main", [0x46, 0x52, 0xFF, 0xFE, 0x0A]));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await Workers.GetRecipe(routes, key)).Response.StatusCode);

        Stage(routes, Workers.User, (".devflow/Dockerfile", "main", Encoding.UTF8.GetBytes("FROM base\n" + new string('#', RecipeEndpoints.MostBytes))));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (await Workers.GetRecipe(routes, key)).Response.StatusCode);
    }

    /// <summary>Stages the files in the user's one repository, <see cref="Staging.RepositoryId"/>.</summary>
    internal static void Stage(Routes routes, Guid user, params (string Path, string Branch, byte[] Content)[] files) =>
        routes.Fake.Replace(user, new FakeWorld([
            new FakeInstallation(Staging.Installation, "Adamniels",
            [
                new FakeRepository(Staging.RepositoryId, "Adamniels", "Personal-platform", [], [], [],
                    [.. files.Select(file => new FakeFile(file.Path, file.Branch, file.Content))]),
            ]),
        ]));
}