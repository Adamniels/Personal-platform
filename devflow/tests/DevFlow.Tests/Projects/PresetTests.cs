using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Projects;
using DevFlow.Settings;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

using PlatformData;
using PlatformData.Testing;

namespace DevFlow.Tests.Projects;

/// <summary>
/// The presets, fully automatic, guided and only I merge: never stored, the settings route answers
/// the one the settings match, or custom. A new project starts as only I merge, and the migration
/// makes every project there was the same. See "Settings" in devflow/README.md.
/// </summary>
public sealed class PresetTests
{
    /// <summary>The last migration before the presets.</summary>
    private const int Before = 40;

    private static readonly Guid User = Guid.Parse("0190a8f0-0000-7000-8000-0000000a11ce");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathOf(Guid project) => $"/devflow/api/projects/{project}/settings";

    private static async Task<JsonObject> Get(Routes routes, Guid project)
    {
        var answer = await routes.App.Get(PathOf(project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(answer))!.AsObject();
    }

    [Fact]
    public async Task ANewProjectIsOnlyIMerge()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Routes.User);

        var settings = await Get(routes, project);
        Assert.True((bool)settings["questions"]!);
        Assert.Equal("always", (string?)settings["waits_for_owner"]);
        Assert.Equal("only_i_merge", (string?)settings["preset"]);

        // Saving answers it too, computed from what was saved; a preset sent along is not read.
        settings["questions"] = false;
        settings["waits_for_owner"] = "never";
        settings["preset"] = "guided";
        var saved = await routes.App.SendJson("PUT", PathOf(project), Contract.Token, settings.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
        Assert.Equal("fully_automatic", (string?)JsonNode.Parse(await Harness.Body(saved))!["preset"]);
        Assert.Equal("fully_automatic", (string?)(await Get(routes, project))["preset"]);
    }

    /// <summary>The three presets' values, as the slice settles them.</summary>
    private static readonly ProjectSettings FullyAutomatic = ProjectSettings.Defaults with { Questions = false, WaitsForOwner = "never" };

    private static readonly ProjectSettings Guided = ProjectSettings.Defaults with
    {
        Questions = false,
        WaitsForOwner = "sometimes",
        WaitWhenMarked = true,
        WaitWhenPaths = true,
        WaitWhenDecision = true,
    };

    private static readonly ProjectSettings OnlyIMerge = ProjectSettings.Defaults with { Questions = true, WaitsForOwner = "always" };

    /// <summary>Every setting no preset covers, changed from the defaults.</summary>
    private static ProjectSettings Elsewhere(ProjectSettings settings) => settings with
    {
        PickupLabel = "agent: go",
        HoldLabel = "agent: wait",
        AttentionLabel = "agent: look",
        ReviewLabel = "agent: see",
        WaitPaths = ["src/auth/**"],
        TrustedLogins = [new TrustedLogin("octo-owner", true), new TrustedLogin("octo-friend", false)],
        ModelReading = "haiku",
        ModelBuilding = "sonnet",
        ModelReviewing = "haiku",
        RunMinutes = 45,
        RunTurns = 300,
        FixRoundsCard = 1,
        FixRoundsFeature = 0,
    };

    [Fact]
    public void EachPresetIsMatched()
    {
        Assert.Equal("fully_automatic", FullyAutomatic.Preset);
        Assert.Equal("guided", Guided.Preset);
        Assert.Equal("only_i_merge", OnlyIMerge.Preset);
        Assert.Equal("only_i_merge", ProjectSettings.Defaults.Preset);

        // On never and always the conditions are not read, so they do not count.
        Assert.Equal("fully_automatic", (FullyAutomatic with { WaitWhenMarked = false, WaitWhenDecision = false }).Preset);
        Assert.Equal("only_i_merge", (OnlyIMerge with { WaitWhenPaths = false }).Preset);

        // Labels, trusted accounts, paths, models and limits never change the answer.
        Assert.Equal("fully_automatic", Elsewhere(FullyAutomatic).Preset);
        Assert.Equal("guided", Elsewhere(Guided).Preset);
        Assert.Equal("only_i_merge", Elsewhere(OnlyIMerge).Preset);

        // Any other combination of the covered settings is custom.
        foreach (var custom in new[]
        {
            FullyAutomatic with { Questions = true },
            Guided with { Questions = true },
            Guided with { WaitWhenMarked = false },
            Guided with { WaitWhenPaths = false },
            Guided with { WaitWhenDecision = false },
            OnlyIMerge with { Questions = false },
            OnlyIMerge with { WaitsForOwner = "sometimes" },
        })
        {
            Assert.Equal("custom", custom.Preset);
            Assert.Equal("custom", Elsewhere(custom).Preset);
        }
    }

    [Fact]
    public async Task ExistingProjectsAreOnlyIMerge()
    {
        await using var database = await Databases.DevFlow.Empty(Ct);
        var all = Migrator.Embedded(typeof(Commands).Assembly);
        await using (var owner = await database.OpenOwner(Ct))
        {
            await Migrator.Run(owner, DevFlowSettings.Service, [.. all.Where(migration => migration.Version <= Before)], Ct);
        }

        // Three projects there were, one on each value, the second with only one condition ticked.
        var projects = new (Guid Id, string Waits, bool Questions, bool Marked)[]
        {
            (Guid.Parse("0190a8f0-0000-7000-8000-00000000c001"), "never", false, true),
            (Guid.Parse("0190a8f0-0000-7000-8000-00000000c002"), "sometimes", false, false),
            (Guid.Parse("0190a8f0-0000-7000-8000-00000000c003"), "always", true, true),
        };
        await using (var admin = await database.OpenAdmin(Ct))
        {
            var repository = 101;
            foreach (var (id, waits, questions, marked) in projects)
            {
                await admin.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO projects (id, user_id, code_host, installation_id, repository_id, owner, name, created_at)
                    VALUES (@id, @user, 'github', 1, @repository, 'Adamniels', @name, now());
                    UPDATE project_settings
                    SET waits_for_owner = @waits, questions = @questions, wait_when_marked = @marked, wait_when_paths = false
                    WHERE project_id = @id;
                    """,
                    new { id, user = User, repository, name = $"repository-{repository}", waits, questions, marked },
                    cancellationToken: Ct));
                repository++;
            }
        }

        await using (var owner = await database.OpenOwner(Ct))
        {
            Assert.NotEmpty(await Migrator.Run(owner, DevFlowSettings.Service, all, Ct));
        }

        await using var check = await database.OpenAdmin(Ct);
        foreach (var (id, _, _, _) in projects)
        {
            var row = await check.QuerySingleAsync<(string Waits, bool Questions, bool Marked, bool Paths, bool Decision)>(new CommandDefinition(
                "SELECT waits_for_owner, questions, wait_when_marked, wait_when_paths, wait_when_decision FROM project_settings WHERE project_id = @id",
                new { id },
                cancellationToken: Ct));
            var settings = ProjectSettings.Defaults with
            {
                WaitsForOwner = row.Waits,
                Questions = row.Questions,
                WaitWhenMarked = row.Marked,
                WaitWhenPaths = row.Paths,
                WaitWhenDecision = row.Decision,
            };
            Assert.Equal("only_i_merge", settings.Preset);
        }
    }
}