using System.Globalization;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Schedules;
using DevFlow.Tests.Schedules;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A project's daily budget in tokens, off by default: once the runs that finished today in the
/// user's zone used it, by the one definition of a token, <see cref="RunDecision"/> answers
/// <see cref="WaitReason.BudgetReached"/>, after the pauses and before the schedules, and no job of
/// any kind is handed out; the run under way finishes. Worked out each time and never stored, it
/// lifts at midnight in the zone, or at once when the budget is raised. Against real Postgres and
/// the fake code host. See "Settings" under "Cards" and "Stops" in devflow/README.md.
/// </summary>
public sealed class BudgetTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OffByDefault()
    {
        await using var jobs = await Jobs.Start();

        // A new project's budget is null, stored and answered.
        Assert.Null(await StoredBudget(jobs));
        var settings = await Settings(jobs);
        Assert.True(settings.ContainsKey("daily_token_budget"));
        Assert.Null(settings["daily_token_budget"]);

        // However much its runs used today, nothing holds it: the build is handed out.
        await Spent(jobs, jobs.Routes.Time.GetUtcNow(), input: 9_000_000_000, output: 9_000_000_000);
        Assert.Equal(new Decision.Run(Job.Build), await Rule(jobs));
        Assert.Equal(Job.Build, (string)(await jobs.Job())["kind"]!);
    }


    /// <summary>The owner saves the project's settings with <paramref name="budget"/>, as the
    /// settings page does, every other setting as it is.</summary>
    internal static async Task SetBudget(Jobs jobs, long? budget)
    {
        var settings = await Settings(jobs);
        settings.Remove("preset");
        settings["daily_token_budget"] = budget;
        var saved = await jobs.Routes.App.SendJson("PUT", SettingsPath(jobs.Project), Contract.Token, settings.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
    }

    /// <summary>
    /// A run of the project that finished at <paramref name="finishedAt"/> with Claude's figures,
    /// written as the superuser: a build of a card number no world has, so no other gate reads it.
    /// </summary>
    internal static async Task Spent(Jobs jobs, DateTimeOffset finishedAt, long? input = null, long? cacheCreation = null, long? cacheRead = null, long? output = null)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, started_at, finished_at, result, run_minutes,
                              input_tokens, cache_creation_input_tokens, cache_read_input_tokens, output_tokens)
            SELECT gen_random_uuid(), p.id, p.user_id, 900 + (SELECT count(*) FROM runs r WHERE r.project_id = p.id)::int, @worker, 'build',
                   'devflow/spent', 'main', @started, @finishedAt, 'succeeded', 90, @input, @cacheCreation, @cacheRead, @output
            FROM projects p WHERE p.id = @project
            """,
            new { project = jobs.Project, worker = Jobs.FirstWorker, started = finishedAt - TimeSpan.FromMinutes(10), finishedAt, input, cacheCreation, cacheRead, output },
            cancellationToken: Ct));
    }

    private static string SettingsPath(Guid project) => $"/devflow/api/projects/{project}/settings";

    private static async Task<JsonObject> Settings(Jobs jobs)
    {
        var answer = await jobs.Routes.App.Get(SettingsPath(jobs.Project), Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        return JsonNode.Parse(await Harness.Body(answer))!.AsObject();
    }

    private static async Task<long?> StoredBudget(Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<long?>(new CommandDefinition(
            "SELECT daily_token_budget FROM project_settings WHERE project_id = @project",
            new { project = jobs.Project },
            cancellationToken: Ct));
    }

    /// <summary>The rule's decision for the project as the first worker finds it.</summary>
    private static async Task<Decision> Rule(Jobs jobs)
    {
        var situation = await RunDecision.Read(jobs.Routes.Door, Jobs.User, jobs.Project, Jobs.FirstWorker, Jobs.Image, jobs.Routes.Time.GetUtcNow(), Ct);
        Assert.NotNull(situation);
        return RunDecision.Of(situation);
    }

}