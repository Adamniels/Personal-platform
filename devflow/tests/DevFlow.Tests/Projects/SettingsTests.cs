using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData.Testing;

namespace DevFlow.Tests.Projects;

/// <summary>
/// A project's settings: the pick-up, hold and attention labels, whether a card's pull request
/// waits for the owner and the trusted accounts, with "sometimes" in SometimesSettingsTests, a row per project made with the project, read and changed by the
/// project's user through <c>GET</c> and <c>PUT /devflow/api/projects/{id}/settings</c>. See
/// "Settings" under "Cards" in devflow/README.md.
/// </summary>
public sealed class SettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathOf(Guid project) => $"/devflow/api/projects/{project}/settings";

    private static JsonObject Settings(string pickup = "devflow", string hold = "devflow-hold", string attention = "devflow-attention", string waits = "always") => new()
    {
        ["pickup_label"] = pickup,
        ["hold_label"] = hold,
        ["attention_label"] = attention,
        ["review_label"] = "devflow-review",
        ["waits_for_owner"] = waits,
        ["wait_when_marked"] = true,
        ["wait_when_paths"] = true,
        ["wait_when_decision"] = true,
        ["wait_paths"] = new JsonArray(),
        ["trusted_logins"] = new JsonArray(),
        ["model_reading"] = "opus",
        ["model_building"] = "opus",
        ["model_reviewing"] = "opus",
        ["run_minutes"] = 90,
        ["run_turns"] = 200,
        ["fix_rounds_card"] = 3,
        ["fix_rounds_feature"] = 3,
        ["questions"] = true,
        ["daily_token_budget"] = null,
    };

    /// <summary>What the route answers for <paramref name="body"/>: the settings, and the preset
    /// they match, see PresetTests.</summary>
    private static JsonObject Answer(JsonObject body, string preset)
    {
        var answer = body.DeepClone().AsObject();
        answer["preset"] = preset;
        return answer;
    }

    private static JsonObject Without(JsonObject settings, string field)
    {
        settings.Remove(field);
        return settings;
    }

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
        Assert.Equal((Routes.User, "devflow", "devflow-hold", "devflow-attention", "always"), await Row(routes, project));

        // Answered with exactly the ten, and nothing else: no login is linked, so nobody is
        // trusted, see TrustedAccountsTests.
        var settings = await Get(routes, project);
        Assert.Equal(Answer(Settings(), "only_i_merge").ToJsonString(), settings.ToJsonString());
    }

    [Fact]
    public async Task TheOwnerChangesThemAndBadValuesAreRefused()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        var changed = Settings(pickup: "agent: go", hold: "agent: wait", attention: "agent: look", waits: "never");
        var saved = await Put(routes, project, changed);
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
        Assert.Equal(Answer(changed, "custom").ToJsonString(), JsonNode.Parse(await Harness.Body(saved))!.ToJsonString());
        Assert.Equal(Answer(changed, "custom").ToJsonString(), (await Get(routes, project)).ToJsonString());

        // Each refused with 422 naming the field, and nothing changed.
        var refused = new (JsonObject Body, string Field)[]
        {
            (Settings(pickup: ""), "pickup_label"),
            (Settings(hold: "   "), "hold_label"),
            (Settings(attention: new string('a', 51)), "attention_label"),
            (Settings(hold: "devflow"), "hold_label"),
            // Labels are one name whatever their case on GitHub.
            (Settings(attention: "DevFlow-Hold"), "attention_label"),
            (Settings(waits: "maybe"), "waits_for_owner"),
            (Settings(waits: "Always"), "waits_for_owner"),
            (Without(Settings(), "waits_for_owner"), "waits_for_owner"),
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
        Assert.Equal(Answer(longest, "only_i_merge").ToJsonString(), (await Get(routes, project)).ToJsonString());
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

    [Fact]
    public async Task TheBudgetIsChecked()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        // Null, off, and every whole number of millions from 1 to 10 000 is taken, saved as tokens
        // and answered as given; the preset is not touched by it.
        foreach (long? budget in new long?[] { 1_000_000, 37_000_000, 10_000_000_000, null })
        {
            var body = Settings();
            body["daily_token_budget"] = budget;
            var saved = await Put(routes, project, body);
            Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
            Assert.Equal(Answer(body, "only_i_merge").ToJsonString(), JsonNode.Parse(await Harness.Body(saved))!.ToJsonString());
            Assert.Equal(budget, await Budget(routes, project));
        }

        // Anything else is refused with 422 naming the field, and the budget stays as it was.
        var kept = Settings();
        kept["daily_token_budget"] = 2_000_000;
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, kept)).Response.StatusCode);
        foreach (var wrong in new long[] { 0, 999_999, 1_500_000, -1_000_000, 10_001_000_000, 1 })
        {
            var body = Settings();
            body["daily_token_budget"] = wrong;
            var answer = await Put(routes, project, body);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, answer.Response.StatusCode);
            var problem = JsonNode.Parse(await Harness.Body(answer))!.AsObject();
            Assert.Equal(["daily_token_budget"], problem["errors"]!.AsObject().Select(error => error.Key));
            Assert.Equal(2_000_000, await Budget(routes, project));
        }

        // Left out, it is refused rather than read as off, so a client that does not know it never
        // switches a budget off by saving.
        var missing = await Put(routes, project, Without(Settings(), "daily_token_budget"));
        Assert.Equal(StatusCodes.Status400BadRequest, missing.Response.StatusCode);
        Assert.Equal(2_000_000, await Budget(routes, project));

        // The table refuses a row written around the route.
        await using var admin = await routes.Database.OpenAdmin(Ct);
        foreach (var wrong in new[] { 0L, 1_500_000L, 10_001_000_000L })
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => admin.ExecuteAsync(new CommandDefinition(
                "UPDATE project_settings SET daily_token_budget = @wrong WHERE project_id = @project",
                new { wrong, project },
                cancellationToken: Ct)));
        }
    }

    /// <summary>The project's budget as stored, read past row level security.</summary>
    private static async Task<long?> Budget(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<long?>(new CommandDefinition(
            "SELECT daily_token_budget FROM project_settings WHERE project_id = @project",
            new { project },
            cancellationToken: Ct));
    }
}