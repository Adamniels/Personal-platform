using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData;
using PlatformData.Testing;

using static DevFlow.Tests.Placements.Placements;

namespace DevFlow.Tests.Placements;

/// <summary>
/// Memory per worker, a setting of the project: 2 GB for a new project and every existing one, 1 to
/// 16 whole GB, saved from the Workers card through its own route and answered by the settings
/// route like the others, which never changes it. See "Workers" in devflow/README.md.
/// </summary>
public sealed class WorkerMemoryTests
{
    [Fact]
    public async Task MemoryIsASettingOfTheProject()
    {
        await using (var routes = await Routes.Start())
        {
            // A new project's is 2 GB, in its row and as the settings route answers it.
            var project = await routes.Project(Routes.User);
            Assert.Equal(2, await Memory(routes.Database, project));
            Assert.Equal(2, (int)(await Settings(routes, project))["worker_memory_gb"]!);

            // Saved from the Workers card, it answers what it saved, and the settings route
            // answers it too.
            var saved = await Save(routes, project, "{\"worker_memory_gb\": 8}");
            Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
            Assert.Equal(8, (int)(await Json(saved))["worker_memory_gb"]!);
            Assert.Equal(8, await Memory(routes.Database, project));
            Assert.Equal(8, (int)(await Settings(routes, project))["worker_memory_gb"]!);

            // 1 and 16 are the ends of the range.
            Assert.Equal(StatusCodes.Status200OK, (await Save(routes, project, "{\"worker_memory_gb\": 1}")).Response.StatusCode);
            Assert.Equal(StatusCodes.Status200OK, (await Save(routes, project, "{\"worker_memory_gb\": 16}")).Response.StatusCode);

            // Outside them, with a fraction, null or left out: 422 naming it, and nothing changes.
            foreach (var body in new[] { "{\"worker_memory_gb\": 0}", "{\"worker_memory_gb\": 17}", "{\"worker_memory_gb\": 2.5}", "{\"worker_memory_gb\": null}", "{}" })
            {
                Assert.Equal(["worker_memory_gb"], await Refused(await Save(routes, project, body)));
            }

            Assert.Equal(16, await Memory(routes.Database, project));

            // Saving the settings page leaves it as it is, whatever the body says of it, and its
            // answer is the memory stored.
            var settings = await Settings(routes, project);
            settings["trusted_logins"] = new JsonArray([.. settings["trusted_logins"]!.AsArray().Select(entry => (JsonNode)(string)entry!["login"]!)]);
            settings["worker_memory_gb"] = 4;
            var page = await routes.App.SendJson("PUT", SettingsPath(project), Contract.Token, settings.ToJsonString());
            Assert.Equal(StatusCodes.Status200OK, page.Response.StatusCode);
            Assert.Equal(16, (int)(await Json(page))["worker_memory_gb"]!);
            Assert.Equal(16, await Memory(routes.Database, project));

            // Another user's project is not there for the caller.
            var bobs = await routes.Project(Routes.Bob, repository: 2, name: "bobs");
            Assert.Equal(StatusCodes.Status404NotFound, (await Save(routes, bobs, "{\"worker_memory_gb\": 4}")).Response.StatusCode);
            Assert.Equal(2, await Memory(routes.Database, bobs));
        }

        // Every project made before the migration has 2 GB after it.
        await using var database = await Databases.DevFlow.Empty(Ct);
        var migrations = Migrator.Embedded(typeof(DevFlow.Settings.DevFlowSettings).Assembly);
        var memory = migrations.Single(migration => migration.Name == "worker_memory");
        await using (var owner = await database.OpenOwner(Ct))
        {
            await Migrator.Run(owner, DevFlow.Settings.DevFlowSettings.Service, [.. migrations.Where(migration => migration.Version < memory.Version)], Ct);
        }

        var before = Guid.CreateVersion7();
        await using (var admin = await database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO projects (id, user_id, code_host, installation_id, repository_id, owner, name, created_at)
                VALUES (@before, @user, 'github', 1, 100, 'Adamniels', 'Personal-platform', now())
                """,
                new { before, user = Guid.CreateVersion7() },
                cancellationToken: Ct));
        }

        await using (var owner = await database.OpenOwner(Ct))
        {
            await Migrator.Run(owner, DevFlow.Settings.DevFlowSettings.Service, migrations, Ct);
        }

        Assert.Equal(2, await Memory(database, before));
    }

    private static Task<HttpContext> Save(Routes routes, Guid project, string json) =>
        routes.App.SendJson("PUT", MemoryPath(project), Contract.Token, json);

    private static async Task<JsonObject> Settings(Routes routes, Guid project)
    {
        var response = await routes.App.Get(SettingsPath(project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (await Json(response)).AsObject();
    }

    /// <summary>The project's memory per worker as its row holds it, read as the superuser.</summary>
    private static async Task<int> Memory(TestDatabase database, Guid project)
    {
        await using var admin = await database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT worker_memory_gb FROM project_settings WHERE project_id = @project",
            new { project },
            cancellationToken: Ct));
    }
}