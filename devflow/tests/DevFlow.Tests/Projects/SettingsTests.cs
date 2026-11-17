using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData.Testing;

namespace DevFlow.Tests.Projects;

/// <summary>
/// A project's settings: the pick-up, hold and attention labels and whether a card's pull request
/// waits for the owner, a row per project made with the project, read and changed by the
/// project's user through <c>GET</c> and <c>PUT /devflow/api/projects/{id}/settings</c>. See
/// "Settings" under "Cards" in devflow/README.md.
/// </summary>
public sealed class SettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathOf(Guid project) => $"/devflow/api/projects/{project}/settings";

    private static JsonObject Settings(string pickup = "devflow", string hold = "devflow-hold", string attention = "devflow-attention", string waits = "never") => new()
    {
        ["pickup_label"] = pickup,
        ["hold_label"] = hold,
        ["attention_label"] = attention,
        ["waits_for_owner"] = waits,
    };

    private static Task<HttpContext> Put(Routes routes, Guid project, JsonObject settings) =>
        routes.App.SendJson("PUT", PathOf(project), Contract.Token, settings.ToJsonString());

    private static async Task<JsonObject> Get(Routes routes, Guid project)
    {
        var answer = await routes.App.Get(PathOf(project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(answer))!.AsObject();
    }

    /// <summary>The project's row, read as the superuser past row level security, or null.</summary>
    private static async Task<(Guid User, string Pickup, string Hold, string Attention, string Waits)?> Row(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleOrDefaultAsync<(Guid, string, string, string, string)?>(new CommandDefinition(
            "SELECT user_id, pickup_label, hold_label, attention_label, waits_for_owner FROM project_settings WHERE project_id = @project",
            new { project },
            cancellationToken: Ct));
    }

    [Fact]
    public async Task ANewProjectHasTheDefaults()
    {
        await using var routes = await Routes.Start();

        var project = await routes.Project(Routes.User);

        // Made with the project, in the project's user's name.
        Assert.Equal((Routes.User, "devflow", "devflow-hold", "devflow-attention", "never"), await Row(routes, project));

        // Answered with exactly the four, and nothing else.
        var settings = await Get(routes, project);
        Assert.Equal(Settings().ToJsonString(), settings.ToJsonString());
    }

    [Fact]
    public async Task TheOwnerChangesThemAndBadValuesAreRefused()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        var changed = Settings(pickup: "agent: go", hold: "agent: wait", attention: "agent: look", waits: "always");
        var saved = await Put(routes, project, changed);
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
        Assert.Equal(changed.ToJsonString(), JsonNode.Parse(await Harness.Body(saved))!.ToJsonString());
        Assert.Equal(changed.ToJsonString(), (await Get(routes, project)).ToJsonString());

        // Each refused with 422 naming the field, and nothing changed.
        var refused = new (JsonObject Body, string Field)[]
        {
            (Settings(pickup: ""), "pickup_label"),
            (Settings(hold: "   "), "hold_label"),
            (Settings(attention: new string('a', 51)), "attention_label"),
            (Settings(hold: "devflow"), "hold_label"),
            // Labels are one name whatever their case on GitHub.
            (Settings(attention: "DevFlow-Hold"), "attention_label"),
            (Settings(waits: "sometimes"), "waits_for_owner"),
            (Settings(waits: "Always"), "waits_for_owner"),
            (new JsonObject { ["pickup_label"] = "devflow", ["hold_label"] = "devflow-hold", ["attention_label"] = "devflow-attention" }, "waits_for_owner"),
        };
        foreach (var (body, field) in refused)
        {
            var answer = await Put(routes, project, body);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, answer.Response.StatusCode);
            var problem = JsonNode.Parse(await Harness.Body(answer))!.AsObject();
            Assert.True(problem["errors"]!.AsObject().ContainsKey(field), $"{body.ToJsonString()} names {field}");
        }

        // Fifty characters is the most GitHub takes, and is taken.
        var longest = Settings(pickup: new string('p', 50));
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, longest)).Response.StatusCode);
        Assert.Equal(longest.ToJsonString(), (await Get(routes, project)).ToJsonString());
    }

    [Fact]
    public async Task OnlyTheProjectsUserReadsOrChangesThem()
    {
        await using var routes = await Routes.Start();
        var bobs = await routes.Project(Routes.Bob);

        Assert.Equal(StatusCodes.Status404NotFound, (await routes.App.Get(PathOf(bobs), Contract.Token)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await Put(routes, bobs, Settings(pickup: "mine"))).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await routes.App.Get(PathOf(Guid.CreateVersion7()), Contract.Token)).Response.StatusCode);
        Assert.Equal("devflow", (await Row(routes, bobs))!.Value.Pickup);

        // Without a token, and from another origin, before anything is read.
        var mine = await routes.Project(Routes.User);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await routes.App.Get(PathOf(mine))).Response.StatusCode);
        var elsewhere = await routes.App.Send("PUT", PathOf(mine), Contract.Token, ("Origin", "https://elsewhere.test"));
        Assert.Equal(StatusCodes.Status403Forbidden, elsewhere.Response.StatusCode);
    }

    [Fact]
    public async Task DisconnectingTakesThemWithIt()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        var gone = await routes.App.Send("DELETE", $"/devflow/api/projects/{project}", Contract.Token, ("Origin", Linking.Origin));

        Assert.Equal(StatusCodes.Status204NoContent, gone.Response.StatusCode);
        Assert.Null(await Row(routes, project));
    }
}