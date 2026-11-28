using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Placements;

/// <summary>
/// What the tests of where a project's workers run share: the placements and the memory per worker
/// as the Workers card reads and saves them, from the platform's origin with the user's token in
/// its cookie, and the slots they make, read as the superuser past row level security. See
/// "Workers" in devflow/README.md.
/// </summary>
internal static class Placements
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A recipe's version, as the reader records a commit it read
    /// <c>.devflow/</c> at.</summary>
    public const string Recipe = "0123456789abcdef0123456789abcdef01234567";

    public static string PlacementsPath(Guid project) => $"/devflow/api/projects/{project}/placements";

    public static string MemoryPath(Guid project) => $"/devflow/api/projects/{project}/worker-memory";

    public static string SettingsPath(Guid project) => $"/devflow/api/projects/{project}/settings";

    /// <summary>The body of a <c>PUT</c> of placements, each machine with its count.</summary>
    public static string Body(params (Guid Machine, int Workers)[] placements) =>
        new JsonArray([.. placements.Select(placement => (JsonNode)new JsonObject { ["machine_id"] = placement.Machine, ["workers"] = placement.Workers })]).ToJsonString();

    /// <summary>"Save" under "Where its workers run", as the Workers card sends it.</summary>
    public static Task<HttpContext> Put(Routes routes, Guid project, string json) =>
        routes.App.SendJson("PUT", PlacementsPath(project), Contract.Token, json);

    /// <summary>Places the workers, after checking it was accepted.</summary>
    public static async Task Place(Routes routes, Guid project, params (Guid Machine, int Workers)[] placements)
    {
        var response = await Put(routes, project, Body(placements));
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    public static Task<HttpContext> Get(Routes routes, Guid project) =>
        routes.App.Get(PlacementsPath(project), Contract.Token);

    /// <summary>The project's placements as the Workers card reads them.</summary>
    public static async Task<JsonArray> List(Routes routes, Guid project)
    {
        var response = await Get(routes, project);
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (await Json(response)).AsArray();
    }

    /// <summary>The fields a <c>422</c> names.</summary>
    public static async Task<IReadOnlyList<string>> Refused(HttpContext response)
    {
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Response.StatusCode);
        return [.. (await Json(response))["errors"]!.AsObject().Select(pair => pair.Key)];
    }

    public static async Task<JsonNode> Json(HttpContext response) => JsonNode.Parse(await Harness.Body(response))!;

    /// <summary>The placements stored, read as the superuser.</summary>
    public static async Task<List<PlacementRow>> Rows(Routes routes)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<PlacementRow>(new CommandDefinition(
            "SELECT project_id AS ProjectId, machine_id AS MachineId, user_id AS UserId, workers AS Workers FROM worker_placements ORDER BY placed_at, machine_id",
            cancellationToken: Ct))];
    }

    /// <summary>The project's slots, read as the superuser, by machine, kind and number.</summary>
    public static async Task<List<SlotRow>> Slots(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<SlotRow>(new CommandDefinition(
            """
            SELECT id AS Id, machine_id AS MachineId, user_id AS UserId, kind AS Kind, number AS Number, wanted AS Wanted
            FROM worker_slots WHERE project_id = @project ORDER BY machine_id, kind, number
            """,
            new { project },
            cancellationToken: Ct))];
    }

    /// <summary>The project's wanted slots, as (machine, kind, number), sorted.</summary>
    public static async Task<List<(Guid Machine, string Kind, int Number)>> Wanted(Routes routes, Guid project) =>
        [.. (await Slots(routes, project)).Where(slot => slot.Wanted).Select(slot => (slot.MachineId, slot.Kind, slot.Number)).Order()];

    internal sealed record PlacementRow(Guid ProjectId, Guid MachineId, Guid UserId, int Workers);

    internal sealed record SlotRow(Guid Id, Guid MachineId, Guid UserId, string Kind, int Number, bool Wanted);
}