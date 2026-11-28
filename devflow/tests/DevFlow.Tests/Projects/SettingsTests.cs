using System.Text.Json.Nodes;

using Dapper;

using DevFlow.ClaudeLogin;
using DevFlow.Tests.Link;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
        ["run_budget_usd"] = 25,
        ["fix_rounds_card"] = 3,
        ["fix_rounds_feature"] = 3,
        ["questions"] = true,
        ["daily_token_budget"] = null,
        ["runs_on"] = "subscription",
        ["api_daily_usd"] = null,
    };

    /// <summary>What the route answers for <paramref name="body"/>: the settings, and the preset
    /// they match, see PresetTests.</summary>
    private static JsonObject Answer(JsonObject body, string preset)
    {
        var answer = body.DeepClone().AsObject();
        // Answered by the settings route, never taken by it: a new project's 2 GB.
        answer["worker_memory_gb"] = 2;
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

    [Fact]
    public async Task TheSpendIsChecked()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        // Every whole number of US dollars from 1 to 100 is taken, saved and answered as given; the
        // preset is not touched by it.
        foreach (var spend in new[] { 1, 40, 100, 25 })
        {
            var body = Settings();
            body["run_budget_usd"] = spend;
            var saved = await Put(routes, project, body);
            Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
            Assert.Equal(Answer(body, "only_i_merge").ToJsonString(), JsonNode.Parse(await Harness.Body(saved))!.ToJsonString());
            Assert.Equal(spend, await Spend(routes, project));
        }

        // Anything else, left out among them, is refused with 422 naming the field alone, and the
        // spend stays as it was.
        var kept = Settings();
        kept["run_budget_usd"] = 60;
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, kept)).Response.StatusCode);
        var wrongs = new List<JsonObject>();
        foreach (var wrong in new JsonNode?[] { 0, 101, -25, 1000, 2.5, 99.99, null })
        {
            var body = Settings();
            body["run_budget_usd"] = wrong;
            wrongs.Add(body);
        }

        wrongs.Add(Without(Settings(), "run_budget_usd"));
        foreach (var body in wrongs)
        {
            var answer = await Put(routes, project, body);
            Assert.True(answer.Response.StatusCode == StatusCodes.Status422UnprocessableEntity, $"{body.ToJsonString()} is refused");
            var problem = JsonNode.Parse(await Harness.Body(answer))!.AsObject();
            Assert.Equal(["run_budget_usd"], problem["errors"]!.AsObject().Select(error => error.Key));
            Assert.Equal(60, await Spend(routes, project));
        }

        // The table refuses a row written around the route.
        await using var admin = await routes.Database.OpenAdmin(Ct);
        foreach (var wrong in new[] { 0, 101 })
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => admin.ExecuteAsync(new CommandDefinition(
                "UPDATE project_settings SET run_budget_usd = @wrong WHERE project_id = @project",
                new { wrong, project },
                cancellationToken: Ct)));
        }
    }

    [Fact]
    public async Task RunsOnIsChecked()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        // A new project runs on the subscription.
        Assert.Equal("subscription", (string)(await Get(routes, project))["runs_on"]!);
        Assert.Equal("subscription", await RunsOn(routes, project));

        // The API key without one given is refused, naming runs_on, with what to do first.
        var onTheKey = Settings();
        onTheKey["runs_on"] = "api_key";
        onTheKey["api_daily_usd"] = 20;
        var refused = await Put(routes, project, onTheKey);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
        var problem = JsonNode.Parse(await Harness.Body(refused))!.AsObject();
        Assert.Equal(["runs_on"], problem["errors"]!.AsObject().Select(error => error.Key));
        Assert.Equal("Give an API key on the Projects tab first", (string)problem["errors"]!["runs_on"]![0]!);
        Assert.Equal("subscription", await RunsOn(routes, project));

        // Once one is given, it is taken, saved and answered as given; the preset is not touched.
        await routes.App.Services.GetRequiredService<ApiKeys>().Set(Routes.User, "sk-ant-api03-settings-test-key", Ct);
        var saved = await Put(routes, project, onTheKey);
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
        Assert.Equal(Answer(onTheKey, "only_i_merge").ToJsonString(), JsonNode.Parse(await Harness.Body(saved))!.ToJsonString());
        Assert.Equal("api_key", await RunsOn(routes, project));
        Assert.Equal("api_key", (string)(await Get(routes, project))["runs_on"]!);

        // Anything but the three, left out among them, is refused naming the field alone, and it
        // stays as it was.
        var wrongs = new List<JsonObject>();
        foreach (var wrong in new JsonNode?[] { "Subscription", "api-key", "", "subscription_then_api_keys", "subscription_then", 1, null })
        {
            var body = Settings();
            body["runs_on"] = wrong;
            wrongs.Add(body);
        }

        wrongs.Add(Without(Settings(), "runs_on"));
        foreach (var body in wrongs)
        {
            var answer = await Put(routes, project, body);
            Assert.True(answer.Response.StatusCode is StatusCodes.Status422UnprocessableEntity or StatusCodes.Status400BadRequest, $"{body.ToJsonString()} is refused");
            if (answer.Response.StatusCode == StatusCodes.Status422UnprocessableEntity)
            {
                Assert.Equal(["runs_on"], JsonNode.Parse(await Harness.Body(answer))!["errors"]!.AsObject().Select(error => error.Key));
            }

            Assert.Equal("api_key", await RunsOn(routes, project));
        }

        // Back to the subscription is always taken.
        Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, Settings())).Response.StatusCode);
        Assert.Equal("subscription", await RunsOn(routes, project));

        // The table refuses a row written around the route.
        await using var admin = await routes.Database.OpenAdmin(Ct);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => admin.ExecuteAsync(new CommandDefinition(
            "UPDATE project_settings SET runs_on = 'both' WHERE project_id = @project",
            new { project },
            cancellationToken: Ct)));
    }

    [Fact]
    public async Task TheCapIsChecked()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        // Continuing on the key without one given is refused naming runs_on, as the key alone is.
        var continuing = Settings();
        continuing["runs_on"] = "subscription_then_api_key";
        continuing["api_daily_usd"] = 20;
        var refused = await Put(routes, project, continuing);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
        Assert.Equal(["runs_on"], await Errors(refused));

        // A new project on the subscription has no cap, and one given there is refused.
        Assert.Null((await Get(routes, project))["api_daily_usd"]);
        var capped = Settings();
        capped["api_daily_usd"] = 20;
        refused = await Put(routes, project, capped);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
        Assert.Equal(["api_daily_usd"], await Errors(refused));

        // With a key given, either way of running on it needs a whole number of US dollars from 1
        // to 1000: left out, null, below, above or with a fraction is refused, naming it alone.
        await routes.App.Services.GetRequiredService<ApiKeys>().Set(Routes.User, "sk-ant-api03-settings-test-key", Ct);
        foreach (var runsOn in new[] { "api_key", "subscription_then_api_key" })
        {
            var bodies = new List<JsonObject>();
            foreach (var wrong in new JsonNode?[] { null, 0, -5, 1001, 20.5 })
            {
                var body = Settings();
                body["runs_on"] = runsOn;
                body["api_daily_usd"] = wrong;
                bodies.Add(body);
            }

            var left = Settings();
            left["runs_on"] = runsOn;
            bodies.Add(Without(left, "api_daily_usd"));
            foreach (var body in bodies)
            {
                var answer = await Put(routes, project, body);
                Assert.Equal(StatusCodes.Status422UnprocessableEntity, answer.Response.StatusCode);
                Assert.Equal(["api_daily_usd"], await Errors(answer));
                Assert.Null(await Cap(routes, project));
            }

            foreach (var cap in new[] { 1, 1000, 20 })
            {
                var body = Settings();
                body["runs_on"] = runsOn;
                body["api_daily_usd"] = cap;
                var saved = await Put(routes, project, body);
                Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
                Assert.Equal(Answer(body, "only_i_merge").ToJsonString(), JsonNode.Parse(await Harness.Body(saved))!.ToJsonString());
                Assert.Equal(cap, await Cap(routes, project));
            }

            // Back on the subscription alone the cap is null again.
            Assert.Equal(StatusCodes.Status200OK, (await Put(routes, project, Settings())).Response.StatusCode);
            Assert.Null(await Cap(routes, project));
        }

        // The table refuses a row written around the route: the key without a cap, a cap on the
        // subscription, or one out of range.
        await using var admin = await routes.Database.OpenAdmin(Ct);
        foreach (var sql in new[]
        {
            "UPDATE project_settings SET runs_on = 'api_key', api_daily_usd = NULL WHERE project_id = @project",
            "UPDATE project_settings SET runs_on = 'subscription', api_daily_usd = 20 WHERE project_id = @project",
            "UPDATE project_settings SET runs_on = 'api_key', api_daily_usd = 1001 WHERE project_id = @project",
        })
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => admin.ExecuteAsync(new CommandDefinition(sql, new { project }, cancellationToken: Ct)));
        }
    }

    /// <summary>The fields a 422 names.</summary>
    private static async Task<List<string>> Errors(HttpContext answer) =>
        [.. JsonNode.Parse(await Harness.Body(answer))!["errors"]!.AsObject().Select(error => error.Key)];

    /// <summary>The project's cap on the key as stored, read past row level security.</summary>
    private static async Task<int?> Cap(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<int?>(new CommandDefinition(
            "SELECT api_daily_usd FROM project_settings WHERE project_id = @project",
            new { project },
            cancellationToken: Ct));
    }

    /// <summary>What the project runs on as stored, read past row level security.</summary>
    private static async Task<string> RunsOn(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT runs_on FROM project_settings WHERE project_id = @project",
            new { project },
            cancellationToken: Ct));
    }

    /// <summary>The project's spend per run as stored, read past row level security.</summary>
    private static async Task<int> Spend(Routes routes, Guid project)
    {
        await using var admin = await routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT run_budget_usd FROM project_settings WHERE project_id = @project",
            new { project },
            cancellationToken: Ct));
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