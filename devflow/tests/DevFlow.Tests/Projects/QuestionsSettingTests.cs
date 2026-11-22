using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Settings;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests.Projects;

/// <summary>
/// A project's setting "Questions before starting": on, a card the reading found questions in waits
/// for the owner's answers; off, it goes on the recommendations. On for a new project and, by its
/// migration, for every project there was. Through <c>GET</c> and
/// <c>PUT /devflow/api/projects/{id}/settings</c> against a real Postgres. See "Settings" in
/// devflow/README.md.
/// </summary>
public sealed class QuestionsSettingTests
{
    /// <summary>The last migration before the setting.</summary>
    private const int Before = 39;

    private static readonly Guid User = Guid.Parse("0190a8f0-0000-7000-8000-0000000a11ce");

    private static readonly Guid Existing = Guid.Parse("0190a8f0-0000-7000-8000-00000000c001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathOf(Guid project) => $"/devflow/api/projects/{project}/settings";

    private static async Task<JsonObject> Get(Routes routes, Guid project)
    {
        var answer = await routes.App.Get(PathOf(project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(answer))!.AsObject();
    }

    [Fact]
    public async Task OnByDefault()
    {
        // A new project has questions on.
        await using (var routes = await Routes.Start())
        {
            var project = await routes.Project(Routes.User);
            Assert.True((bool)(await Get(routes, project))["questions"]!);
        }

        // And the migration sets every project there was on.
        await using var database = await Databases.DevFlow.Empty(Ct);
        var all = Migrator.Embedded(typeof(Commands).Assembly);
        await using (var owner = await database.OpenOwner(Ct))
        {
            await Migrator.Run(owner, DevFlowSettings.Service, [.. all.Where(migration => migration.Version <= Before)], Ct);
        }

        await using (var admin = await database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO projects (id, user_id, code_host, installation_id, repository_id, owner, name, created_at)
                VALUES (@existing, @user, 'github', 1, 101, 'Adamniels', 'fodr', now())
                """,
                new { existing = Existing, user = User },
                cancellationToken: Ct));
        }

        await using (var owner = await database.OpenOwner(Ct))
        {
            Assert.NotEmpty(await Migrator.Run(owner, DevFlowSettings.Service, all, Ct));
        }

        await using var check = await database.OpenAdmin(Ct);
        Assert.True(await check.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT questions FROM project_settings WHERE project_id = @existing", new { existing = Existing }, cancellationToken: Ct)));
    }

    [Fact]
    public async Task TheOwnerTurnsThemOff()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        var settings = await Get(routes, project);

        settings["questions"] = false;
        var saved = await routes.App.SendJson("PUT", PathOf(project), Contract.Token, settings.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
        Assert.False((bool)(await Get(routes, project))["questions"]!);

        // Missing, it is refused, naming the field, and nothing changes.
        settings.Remove("questions");
        var refused = await routes.App.SendJson("PUT", PathOf(project), Contract.Token, settings.ToJsonString());
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
        Assert.True(JsonNode.Parse(await Harness.Body(refused))!["errors"]!.AsObject().ContainsKey("questions"));
        Assert.False((bool)(await Get(routes, project))["questions"]!);
    }
}