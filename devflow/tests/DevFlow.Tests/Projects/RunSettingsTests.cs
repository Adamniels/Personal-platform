using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData.Testing;

namespace DevFlow.Tests.Projects;

/// <summary>
/// A project's models and limits: the model that reads, builds and reviews its cards, the minutes
/// and turns a run may take, and the fix rounds a card and a feature get, through
/// <c>PUT /devflow/api/projects/{id}/settings</c> against a real Postgres. See "Settings" in
/// devflow/README.md.
/// </summary>
public sealed class RunSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathOf(Guid project) => $"/devflow/api/projects/{project}/settings";

    /// <summary>The settings as a new project has them, but for <paramref name="changes"/>, the
    /// trusted accounts as plain logins.</summary>
    private static JsonObject Settings(params (string Field, JsonNode? Value)[] changes)
    {
        var settings = new JsonObject
        {
            ["pickup_label"] = "devflow",
            ["hold_label"] = "devflow-hold",
            ["attention_label"] = "devflow-attention",
            ["review_label"] = "devflow-review",
            ["waits_for_owner"] = "never",
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
        };
        foreach (var (field, value) in changes)
        {
            settings[field] = value;
        }

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

    /// <summary>The project's models and limits as its row holds them, read as the superuser past
    /// row level security.</summary>
    private static async Task<(string Reading, string Building, string Reviewing, int Minutes, int Turns, int CardRounds, int FeatureRounds)> Row(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<(string, string, string, int, int, int, int)>(new CommandDefinition(
            """
            SELECT model_reading, model_building, model_reviewing, run_minutes, run_turns, fix_rounds_card, fix_rounds_feature
            FROM project_settings WHERE project_id = @project
            """,
            new { project },
            cancellationToken: Ct));
    }

    private static async Task Refused(Routes routes, Guid project, JsonObject body, string field)
    {
        var answer = await Put(routes, project, body);
        Assert.True(answer.Response.StatusCode == StatusCodes.Status422UnprocessableEntity, $"{body.ToJsonString()} is refused");
        var problem = JsonNode.Parse(await Harness.Body(answer))!.AsObject();
        var errors = problem["errors"]!.AsObject();
        Assert.True(errors.ContainsKey(field), $"{body.ToJsonString()} names {field}");
        Assert.Single(errors);
    }

    [Fact]
    public async Task ANewProjectHasTheDefaults()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        // opus for each job, 90 minutes, 200 turns, and 3 fix rounds a card and 3 a feature.
        Assert.Equal(("opus", "opus", "opus", 90, 200, 3, 3), await Row(routes, project));
        Assert.Equal(Settings().ToJsonString(), (await Get(routes, project)).ToJsonString());

        // Each is changed and answered as saved, the extremes included.
        var changed = Settings(
            ("model_reading", "haiku"), ("model_building", "sonnet"), ("model_reviewing", "opus"),
            ("run_minutes", 240), ("run_turns", 20), ("fix_rounds_card", 0), ("fix_rounds_feature", 10));
        var saved = await Put(routes, project, changed);
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
        Assert.Equal(changed.ToJsonString(), JsonNode.Parse(await Harness.Body(saved))!.ToJsonString());
        Assert.Equal(changed.ToJsonString(), (await Get(routes, project)).ToJsonString());
        Assert.Equal(("haiku", "sonnet", "opus", 240, 20, 0, 10), await Row(routes, project));

        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, Settings(("run_minutes", 10), ("run_turns", 1000)))).Response.StatusCode);
        Assert.Equal(("opus", "opus", "opus", 10, 1000, 3, 3), await Row(routes, project));
    }

    [Fact]
    public async Task BadValuesAreRefused()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);
        var kept = await Row(routes, project);

        // A model outside opus, sonnet and haiku, exactly, is refused, a pinned model id among them.
        foreach (var field in new[] { "model_reading", "model_building", "model_reviewing" })
        {
            foreach (var model in new JsonNode?[] { "gpt", "Opus", " opus", "claude-opus-5-5", "", null })
            {
                await Refused(routes, project, Settings((field, model)), field);
            }
        }

        // Minutes outside 10 to 240, turns outside 20 to 1000, fix rounds outside 0 to 10.
        foreach (var (field, wrong) in new (string, JsonNode?)[]
        {
            ("run_minutes", 9), ("run_minutes", 241), ("run_minutes", 0), ("run_minutes", -90), ("run_minutes", null),
            ("run_turns", 19), ("run_turns", 1001), ("run_turns", null),
            ("fix_rounds_card", -1), ("fix_rounds_card", 11), ("fix_rounds_card", null),
            ("fix_rounds_feature", -1), ("fix_rounds_feature", 11), ("fix_rounds_feature", null),
        })
        {
            await Refused(routes, project, Settings((field, wrong)), field);
        }

        // Every field is required.
        foreach (var field in new[] { "model_reading", "model_building", "model_reviewing", "run_minutes", "run_turns", "fix_rounds_card", "fix_rounds_feature" })
        {
            var missing = Settings();
            missing.Remove(field);
            await Refused(routes, project, missing, field);
        }

        // Nothing refused changed anything.
        Assert.Equal(kept, await Row(routes, project));

        // The table refuses what the route refuses, so nothing but the route can store one.
        await using var admin = await routes.Database.OpenAdmin(Ct);
        foreach (var change in new[]
        {
            "model_reading = 'gpt'", "model_building = 'Opus'", "model_reviewing = ''",
            "run_minutes = 9", "run_minutes = 241", "run_turns = 19", "run_turns = 1001",
            "fix_rounds_card = -1", "fix_rounds_card = 11", "fix_rounds_feature = -1", "fix_rounds_feature = 11",
        })
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => admin.ExecuteAsync(new CommandDefinition(
                $"UPDATE project_settings SET {change} WHERE project_id = @project", new { project }, cancellationToken: Ct)));
        }
    }
}