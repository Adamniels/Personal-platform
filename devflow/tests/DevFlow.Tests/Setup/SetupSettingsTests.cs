using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Protocol;
using DevFlow.Settings;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests.Setup;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// The settings step: the preset and paths a setup run proposed wait on the project's setup step
/// "Settings" until the owner accepts them or saves the settings panel, and the project hands out
/// no reading or build until he has; a project with no setup run saves the panel once, existing
/// projects among them. See "Setting up a project" in devflow/README.md.
/// </summary>
public sealed class SetupSettingsTests
{
    /// <summary>The last migration before the settings step.</summary>
    private const int Before = 43;

    private static CancellationToken Ct => Jobs.Ct;

    private static string SettingsPath(Jobs jobs) => $"/devflow/api/projects/{jobs.Project}/settings";

    private static async Task<JsonObject> Settings(Jobs jobs)
    {
        var answer = await jobs.Routes.App.Get(SettingsPath(jobs), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(answer))!.AsObject();
    }

    /// <summary>Saves <paramref name="settings"/> through the settings route, as the panel does,
    /// the trusted accounts as plain logins.</summary>
    private static async Task<JsonObject> Save(Jobs jobs, JsonObject settings)
    {
        settings["trusted_logins"] = new JsonArray([.. settings["trusted_logins"]!.AsArray().Select(entry => (JsonNode?)(string)entry!["login"]!)]);
        var saved = await jobs.Routes.App.SendJson("PUT", SettingsPath(jobs), Contract.Token, settings.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(saved))!.AsObject();
    }

    private static IEnumerable<(string, bool)> Steps(JsonNode setup) =>
        setup["steps"]!.AsArray().Select(step => ((string)step!["step"]!, (bool)step!["done"]!));

    private static bool SettingsDone(JsonNode setup) =>
        Steps(setup).Single(step => step.Item1 == "settings").Item2;

    /// <summary>A project with no <c>.devflow/</c>, its settings not accepted, whose setup run
    /// succeeded with the report <see cref="SetupRunTests.Report"/>, proposing guided and
    /// <c>migrations/**</c>.</summary>
    private static async Task<Jobs> Proposed()
    {
        var jobs = await SetupRunTests.Unset(settingsAccepted: false);
        var job = await SetupRunTests.Next(jobs);
        SetupRunTests.Stage(jobs, (".devflow/Dockerfile", "devflow/setup", SetupRunTests.GoodRecipe), (".devflow/project.json", "devflow/setup", Jobs.ProjectFile()));
        var head = await SetupRunTests.Push(jobs);
        Assert.Equal(StatusCodes.Status204NoContent, (await SetupRunTests.Send(jobs, job, SetupRunTests.Result(head))).Response.StatusCode);
        return jobs;
    }

    /// <summary>A valid <c>.devflow/</c> is on the default branch and the loop read it, as once the
    /// setup's pull request merged.</summary>
    private static async Task Valid(Jobs jobs)
    {
        SetupRunTests.Stage(jobs, (".devflow/Dockerfile", "main", SetupRunTests.GoodRecipe), (".devflow/project.json", "main", Jobs.ProjectFile()));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
    }

    [Fact]
    public async Task TheProposalWaitsOnItsStep()
    {
        await using var jobs = await Proposed();

        // The step "Settings" comes after the recipe and before a worker on it, and is not done; it
        // shows the preset the setup run proposed, with why, and the paths.
        var setup = await SetupRunTests.Steps(jobs);
        Assert.Equal(["setup", "recipe", "settings", "worker", "check"], Steps(setup).Select(step => step.Item1));
        Assert.False(SettingsDone(setup));
        Assert.Equal("setting_up", (string)setup["state"]!);
        var proposal = setup["proposal"]!;
        Assert.Equal("guided", (string)proposal["preset"]!);
        Assert.Equal("Its migrations are worth a look.", (string)proposal["why"]!);
        Assert.Equal(["migrations/**"], proposal["paths"]!.AsArray().Select(path => (string)path!));

        // Nothing was saved for it: the settings are still as they were, the harness's own.
        var settings = await Settings(jobs);
        Assert.Equal(("never", true), ((string)settings["waits_for_owner"]!, (bool)settings["questions"]!));
        Assert.Empty(settings["wait_paths"]!.AsArray());

        // The step waits once the setup's pull request merged, with the same proposal.
        Assert.False((bool)setup["settings_waits"]!);
        await Valid(jobs);
        setup = await SetupRunTests.Steps(jobs);
        Assert.True((bool)setup["settings_waits"]!);
        Assert.False(SettingsDone(setup));
        Assert.Equal("guided", (string)setup["proposal"]!["preset"]!);
    }

    [Fact]
    public async Task NotBeforeTheSetupsPullRequest()
    {
        // The setup run proposed its settings and its pull request is open: the step does not wait
        // yet, and a save of the panel is kept but does not do it, so the proposal is still put.
        await using var jobs = await Proposed();
        var settings = await Settings(jobs);
        settings["hold_label"] = "agent: wait";
        await Save(jobs, settings);
        var setup = await SetupRunTests.Steps(jobs);
        Assert.False(SettingsDone(setup));
        Assert.False((bool)setup["settings_waits"]!);
        Assert.Equal("agent: wait", (string)(await Settings(jobs))["hold_label"]!);

        // Merged, it waits, and the next save does it.
        await Valid(jobs);
        Assert.True((bool)(await SetupRunTests.Steps(jobs))["settings_waits"]!);
        await Save(jobs, await Settings(jobs));
        setup = await SetupRunTests.Steps(jobs);
        Assert.True(SettingsDone(setup));
        Assert.False((bool)setup["settings_waits"]!);
    }

    [Fact]
    public async Task AcceptingSavesTheProposal()
    {
        await using var jobs = await Proposed();
        await Valid(jobs);
        var proposal = (await SetupRunTests.Steps(jobs))["proposal"]!;

        // "Accept" fills in the proposed preset's settings and the paths, and saves them through
        // the settings route, as the panel saves any change.
        var settings = await Settings(jobs);
        settings["questions"] = false;
        settings["waits_for_owner"] = "sometimes";
        settings["wait_when_marked"] = true;
        settings["wait_when_paths"] = true;
        settings["wait_when_decision"] = true;
        settings["wait_paths"] = proposal["paths"]!.DeepClone();
        var saved = await Save(jobs, settings);
        Assert.Equal("guided", (string)saved["preset"]!);

        // The step is done, and the settings are the proposal's.
        Assert.True(SettingsDone(await SetupRunTests.Steps(jobs)));
        settings = await Settings(jobs);
        Assert.Equal("guided", (string)settings["preset"]!);
        Assert.Equal(["migrations/**"], settings["wait_paths"]!.AsArray().Select(path => (string)path!));
    }

    [Fact]
    public async Task SavingTheSettingsAlsoDoesIt()
    {
        // A project whose .devflow/ was valid when connected has no setup run and no proposal: its
        // step waits for the panel to be saved once.
        await using var jobs = await Jobs.Start([Jobs.Card(1)], settingsAccepted: false);
        SetupRunTests.Stage(jobs, (".devflow/Dockerfile", "main", SetupRunTests.GoodRecipe), (".devflow/project.json", "main", Jobs.ProjectFile()));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.User));
        var setup = await SetupRunTests.Steps(jobs);
        Assert.False(SettingsDone(setup));
        Assert.Null(setup["proposal"]);
        Assert.True((bool)setup["settings_waits"]!);
        Assert.Equal("setting_up", (string)setup["state"]!);

        // Saving the panel while it waits marks it done with what was saved: here a changed hold
        // label, the rest as it was.
        var settings = await Settings(jobs);
        settings["hold_label"] = "agent: wait";
        await Save(jobs, settings);
        setup = await SetupRunTests.Steps(jobs);
        Assert.True(SettingsDone(setup));
        Assert.Equal("registered", (string)setup["state"]!);
        Assert.Equal("agent: wait", (string)(await Settings(jobs))["hold_label"]!);

        // Done once: saving again leaves it done.
        await Save(jobs, await Settings(jobs));
        Assert.True(SettingsDone(await SetupRunTests.Steps(jobs)));
    }

    [Fact]
    public async Task NoCardBeforeTheSettings()
    {
        // The worker's version unchecked and the settings not accepted: its check is handed out
        // first, before the settings, as a setup run is.
        await using (var unchecked_ = await Jobs.Start([Jobs.Card(1)], checkedRecipe: false, settingsAccepted: false))
        {
            Assert.Equal(Job.RecipeCheck, (string)(await unchecked_.Job())["kind"]!);
        }

        // A card to read and one to build, the check passed and the settings step not done: no
        // reading and no build is handed out.
        await using var jobs = await Jobs.Start([Jobs.Card(1)], read: false, settingsAccepted: false);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        Assert.False(SettingsDone(await SetupRunTests.Steps(jobs)));

        // Saved once its .devflow/ is valid, the card's reading is handed out.
        await Valid(jobs);
        await Save(jobs, await Settings(jobs));
        Assert.Equal(Job.Read, (string)(await jobs.Job())["kind"]!);

        // A card already read and ready to build: no build either, until the settings are saved.
        await using var ready = await Jobs.Start([Jobs.Card(1)], settingsAccepted: false);
        Assert.Equal(StatusCodes.Status204NoContent, (await ready.Next()).Response.StatusCode);
        await Valid(ready);
        await Save(ready, await Settings(ready));
        Assert.Equal(Job.Build, (string)(await ready.Job())["kind"]!);
    }

    [Fact]
    public async Task ExistingProjectsAcceptOnce()
    {
        await using var database = await Databases.DevFlow.Empty(Ct);
        var all = Migrator.Embedded(typeof(Commands).Assembly);
        await using (var owner = await database.OpenOwner(Ct))
        {
            await Migrator.Run(owner, DevFlowSettings.Service, [.. all.Where(migration => migration.Version <= Before)], Ct);
        }

        // Two projects there were, one registered by a check that passed, one still setting up.
        var projects = new[] { Guid.Parse("0190a8f0-0000-7000-8000-00000000d001"), Guid.Parse("0190a8f0-0000-7000-8000-00000000d002") };
        await using (var admin = await database.OpenAdmin(Ct))
        {
            var repository = 201;
            foreach (var id in projects)
            {
                await admin.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO projects (id, user_id, code_host, installation_id, repository_id, owner, name, created_at)
                    VALUES (@id, @user, 'github', 1, @repository, 'Adamniels', @name, now())
                    """,
                    new { id, user = Routes.User, repository, name = $"repository-{repository}" },
                    cancellationToken: Ct));
                repository++;
            }

            await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO recipe_checks (run_id, project_id, user_id, version, commit, passed, exit_code, output, checked_at)
                VALUES (gen_random_uuid(), @id, @user, 'v1', @commit, true, 0, 'passed', now())
                """,
                new { id = projects[0], user = Routes.User, commit = SetupFixture.Commit },
                cancellationToken: Ct));
        }

        await using (var owner = await database.OpenOwner(Ct))
        {
            Assert.NotEmpty(await Migrator.Run(owner, DevFlowSettings.Service, all, Ct));
        }

        // After the migration, every project's settings step is not done, the registered one's too.
        await using var check = await database.OpenAdmin(Ct);
        Assert.Equal(
            [true, true],
            await check.QueryAsync<bool>(new CommandDefinition(
                "SELECT accepted_at IS NULL FROM project_settings WHERE project_id = ANY(@projects) ORDER BY project_id",
                new { projects },
                cancellationToken: Ct)));
    }
}