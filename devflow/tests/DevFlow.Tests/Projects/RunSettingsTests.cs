using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Protocol;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests.Projects;

/// <summary>
/// A project's models and limits: the model that reads, builds and reviews its cards, the minutes
/// and turns a run may take, and the fix rounds a card and a feature get, through
/// <c>PUT /devflow/api/projects/{id}/settings</c> against a real Postgres; and a change taking
/// effect at the next decision, a run under way keeping what its job carried. See "Settings" and
/// "Jobs" in devflow/README.md.
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
            ["waits_for_owner"] = "always",
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
        foreach (var (field, value) in changes)
        {
            settings[field] = value;
        }

        return settings;
    }

    /// <summary>What the route answers for <paramref name="body"/>: the settings, and the preset
    /// they match, see PresetTests.</summary>
    private static JsonObject Answer(JsonObject body, string preset)
    {
        var answer = body.DeepClone().AsObject();
        answer["preset"] = preset;
        return answer;
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

    /// <summary>The settings as <paramref name="jobs"/>'s project has them now, changed by
    /// <paramref name="changes"/> and saved through the route, as the panel saves them.</summary>
    private static async Task Change(Jobs.Jobs jobs, params (string Field, JsonNode? Value)[] changes)
    {
        var current = await Get(jobs.Routes, jobs.Project);
        current["trusted_logins"] = new JsonArray([.. current["trusted_logins"]!.AsArray().Select(entry => (JsonNode)(string)entry!["login"]!)]);
        foreach (var (field, value) in changes)
        {
            current[field] = value;
        }

        Assert.Equal(StatusCodes.Status200OK, (await Put(jobs.Routes, jobs.Project, current)).Response.StatusCode);
    }

    [Fact]
    public async Task ANewProjectHasTheDefaults()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        // opus for each job, 90 minutes, 200 turns, and 3 fix rounds a card and 3 a feature.
        Assert.Equal(("opus", "opus", "opus", 90, 200, 3, 3), await Row(routes, project));
        Assert.Equal(Answer(Settings(), "only_i_merge").ToJsonString(), (await Get(routes, project)).ToJsonString());

        // Each is changed and answered as saved, the extremes included.
        var changed = Settings(
            ("model_reading", "haiku"), ("model_building", "sonnet"), ("model_reviewing", "opus"),
            ("run_minutes", 240), ("run_turns", 20), ("fix_rounds_card", 0), ("fix_rounds_feature", 10));
        var saved = await Put(routes, project, changed);
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
        Assert.Equal(Answer(changed, "only_i_merge").ToJsonString(), JsonNode.Parse(await Harness.Body(saved))!.ToJsonString());
        Assert.Equal(Answer(changed, "only_i_merge").ToJsonString(), (await Get(routes, project)).ToJsonString());
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

    [Fact]
    public async Task AChangeTakesEffectAtTheNextDecision()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1), Jobs.Jobs.Card(2)]);

        // The first card's job carries the settings as they were when it was handed out.
        var first = await jobs.Job();
        Assert.Equal((1, "opus", 90, 200), ((int)first["card"]!["number"]!, (string)first["model"]!, (int)first["limits"]!["minutes"]!, (int)first["limits"]!["turns"]!));

        // The owner changes them while it runs: the next job carries the new ones.
        await Change(jobs, ("model_building", "sonnet"), ("run_minutes", 30), ("run_turns", 50));
        await jobs.SayHello(Jobs.Jobs.SecondWorker);
        var second = await jobs.Job(Jobs.Jobs.SecondWorker);
        Assert.Equal((2, "sonnet", 30, 50), ((int)second["card"]!["number"]!, (string)second["model"]!, (int)second["limits"]!["minutes"]!, (int)second["limits"]!["turns"]!));

        // Each run keeps the minutes its job carried, which the worker enforces; finding a run lost
        // reads no setting. Both workers quiet for 2 minutes: both are lost, whatever their minutes.
        var runs = await jobs.Runs();
        Assert.Equal((90, 30), (await RunMinutes(jobs, 1), await RunMinutes(jobs, 2)));
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(2));
        await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User);
        runs = await jobs.Runs();
        Assert.Equal(("lost", "lost"), (runs.Single(run => run.CardNumber == 1).Result, runs.Single(run => run.CardNumber == 2).Result));
    }

    [Fact]
    public async Task SpendDefaultsTo25()
    {
        // A new project's spend per run is 25 USD, in its row and as the route answers it.
        await using (var routes = await Routes.Start())
        {
            var project = await routes.Project(Routes.User);
            Assert.Equal(25, await Spend(routes.Database, project));
            Assert.Equal(25, (int)(await Get(routes, project))["run_budget_usd"]!);
        }

        // Every project made before the migration has 25 after it, whatever its other limits.
        await using var database = await Databases.DevFlow.Empty(Ct);
        var migrations = Migrator.Embedded(typeof(DevFlow.Settings.DevFlowSettings).Assembly);
        var spend = migrations.Single(migration => migration.Name == "spend_per_run");
        await using (var owner = await database.OpenOwner(Ct))
        {
            await Migrator.Run(owner, DevFlow.Settings.DevFlowSettings.Service, [.. migrations.Where(migration => migration.Version < spend.Version)], Ct);
        }

        var before = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        await using (var admin = await database.OpenAdmin(Ct))
        {
            foreach (var (project, at) in before.Select((project, at) => (project, at)))
            {
                await admin.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO projects (id, user_id, code_host, installation_id, repository_id, owner, name, created_at)
                    VALUES (@project, @user, 'github', 1, @repository, 'Adamniels', 'Personal-platform', now());
                    UPDATE project_settings SET run_turns = 50 WHERE project_id = @project AND @at = 1;
                    """,
                    new { project, user = Guid.CreateVersion7(), repository = 100 + at, at },
                    cancellationToken: Ct));
            }
        }

        await using (var owner = await database.OpenOwner(Ct))
        {
            await Migrator.Run(owner, DevFlow.Settings.DevFlowSettings.Service, migrations, Ct);
        }

        foreach (var project in before)
        {
            Assert.Equal(25, await Spend(database, project));
        }
    }

    [Fact]
    public async Task AJobCarriesTheProjectsSpend()
    {
        // Set to 40 through the route, as the page saves it, every kind that runs a model carries
        // 40: a build, a card's review, its fix, a conflict, a whole feature's review and its fix,
        // a reading and a setup.
        var kinds = new List<string>();
        await Jobs.JobModelTests.EachKind(
            jobs => Change(jobs, ("run_budget_usd", 40)),
            (kind, job) =>
            {
                kinds.Add(kind);
                Assert.Equal(40m, (decimal)job["limits"]!["budget_usd"]!);
            });
        Assert.Equal(Jobs.JobModelTests.Kinds, kinds);
    }

    [Fact]
    public async Task ARunUnderWayKeepsItsSpend()
    {
        await using var jobs = await Jobs.Jobs.Start([Jobs.Jobs.Card(1), Jobs.Jobs.Card(2)]);

        // The first card's job carries the spend as it was when it was handed out.
        var first = await jobs.Job();
        Assert.Equal((1, 25m), ((int)first["card"]!["number"]!, (decimal)first["limits"]!["budget_usd"]!));

        // The owner raises it while that run is under way: the run is left as it was, still under
        // way on its worker, which enforces what its job carried, and only the next job carries 40.
        await Change(jobs, ("run_budget_usd", 40));
        await jobs.SayHello(Jobs.Jobs.SecondWorker);
        var second = await jobs.Job(Jobs.Jobs.SecondWorker);
        Assert.Equal((2, 40m), ((int)second["card"]!["number"]!, (decimal)second["limits"]!["budget_usd"]!));
        var runs = await jobs.Runs();
        Assert.Null(runs.Single(run => run.CardNumber == 1).Result);

        // Lowered below what the first run carried, it is still not stopped, and it finishes as
        // its job said.
        await Change(jobs, ("run_budget_usd", 1));
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)first["job_id"]!), Jobs.Jobs.Result(Jobs.Jobs.FirstWorker, (string)first["branch"]!))).Response.StatusCode);
        runs = await jobs.Runs();
        Assert.Equal("succeeded", runs.Single(run => run.CardNumber == 1).Result);
    }

    /// <summary>The project's spend per run as its row holds it, read past row level
    /// security.</summary>
    private static async Task<int> Spend(TestDatabase database, Guid project)
    {
        await using var admin = await database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT run_budget_usd FROM project_settings WHERE project_id = @project",
            new { project },
            cancellationToken: Ct));
    }

    /// <summary>The minutes the run of card <paramref name="card"/> was handed out with.</summary>
    private static async Task<int> RunMinutes(Jobs.Jobs jobs, int card)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(TestContext.Current.CancellationToken);
        return await admin.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT run_minutes FROM runs WHERE project_id = @project AND card_number = @card",
            new { project = jobs.Project, card },
            cancellationToken: TestContext.Current.CancellationToken));
    }
}