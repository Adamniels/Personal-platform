using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.Stops;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Usage;

/// <summary>
/// A project's daily cap on the API key, in whole US dollars: once the runs on the key that
/// finished today in the user's zone cost it, by Claude Code's estimate, a project whose jobs carry
/// the key hands out nothing, <see cref="WaitReason.ApiCapReached"/> with the spent, the cap and
/// when it lifts, the next midnight, or for a project continuing on the key the subscription's
/// reset when that comes first. Runs on the subscription add nothing to it. Worked out each time and
/// never stored. Against real Postgres and the fake code host. See "The API key" in
/// devflow/README.md.
/// </summary>
public sealed class ApiCapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCapHoldsAKeyRun()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await jobs.RunOnTheKey();
        await SetCap(jobs, "api_key", 10);
        var now = jobs.Routes.Time.GetUtcNow();

        // Today's runs on the key cost 10 USD: nothing is handed out, and the rule says what was
        // spent of what, and that it lifts at the next midnight in Stockholm.
        await Spent(jobs, now - TimeSpan.FromHours(1), JobCredential.ApiKey, 6.25m);
        await Spent(jobs, now - TimeSpan.FromMinutes(5), JobCredential.ApiKey, 3.75m);
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Next()).Response.StatusCode);
        var midnight = BudgetTests.NextMidnight(now);
        Assert.Equal(new Decision.Wait(new WaitReason.ApiCapReached(10m, 10m, midnight)), await PauseTests.Rule(jobs));
        var said = Assert.IsType<WaitReason.ApiCapReached>(((Decision.Wait)await PauseTests.Rule(jobs)).Reason);
        Assert.Equal("API key spend reached for today, 10.00 of 10 USD", said.Sentence);
        Assert.Equal(ActedOn.RunsOn, said.Where);
        Assert.Equal(Limit.Stockholm.GetUtcOffset(midnight), said.LiftsAt.Offset);

        // A project continuing on the key, its runs on the key at its cap, runs on the subscription
        // while that is not limited.
        await using var continuing = await Jobs.Jobs.Start();
        await continuing.RunOnTheKey(runsOn: "subscription_then_api_key");
        await SetCap(continuing, "subscription_then_api_key", 10);
        await Spent(continuing, now - TimeSpan.FromMinutes(5), JobCredential.ApiKey, 10m);
        Assert.Equal(new Decision.Run(Job.Build), await PauseTests.Rule(continuing));

        // While the subscription is limited it answers the same, lifting at the subscription's
        // reset, which comes before midnight.
        var reset = now + TimeSpan.FromMinutes(20);
        await Limit.Set(continuing, reset);
        Assert.Equal(StatusCodes.Status204NoContent, (await continuing.Next()).Response.StatusCode);
        Assert.Equal(new Decision.Wait(new WaitReason.ApiCapReached(10m, 10m, reset)), await PauseTests.Rule(continuing));

        // Once it resets, the project runs on the subscription.
        await Limit.MoveTo(continuing, reset);
        Assert.Equal(JobCredential.Subscription, (string)(await continuing.Job())["credential"]!["kind"]!);
    }

    [Fact]
    public async Task SubscriptionRunsDoNotCount()
    {
        await using var jobs = await Jobs.Jobs.Start();
        await jobs.RunOnTheKey();
        await SetCap(jobs, "api_key", 10);
        var now = jobs.Routes.Time.GetUtcNow();

        // Runs on the subscription cost 50 USD by Claude Code's estimate, and 9.99 on the key: the
        // project is under its cap, and runs on the key.
        await Spent(jobs, now - TimeSpan.FromMinutes(30), JobCredential.Subscription, 50m);
        await Spent(jobs, now - TimeSpan.FromMinutes(10), JobCredential.ApiKey, 9.99m);
        Assert.Equal(new Decision.Run(Job.Build), await PauseTests.Rule(jobs));
        Assert.Equal(JobCredential.ApiKey, (string)(await jobs.Job())["credential"]!["kind"]!);
    }

    [Fact]
    public async Task ItLiftsAtMidnightOrWhenRaised()
    {
        // Raised above what was spent, the next job is handed out at once.
        await using var raised = await Jobs.Jobs.Start();
        await raised.RunOnTheKey();
        await SetCap(raised, "api_key", 10);
        await Spent(raised, raised.Routes.Time.GetUtcNow(), JobCredential.ApiKey, 12m);
        Assert.Equal(StatusCodes.Status204NoContent, (await raised.Next()).Response.StatusCode);
        await SetCap(raised, "api_key", 20);
        Assert.Equal(Job.Build, (string)(await raised.Job())["kind"]!);

        // At midnight in Stockholm the day's spend counts no more, with nothing stored to clear; a
        // second before, it still holds.
        await using var waiting = await Jobs.Jobs.Start();
        await waiting.RunOnTheKey();
        await SetCap(waiting, "api_key", 10);
        var now = waiting.Routes.Time.GetUtcNow();
        await Spent(waiting, now, JobCredential.ApiKey, 10m);
        var midnight = BudgetTests.NextMidnight(now);
        await Limit.MoveTo(waiting, midnight - TimeSpan.FromSeconds(1));
        Assert.Equal(StatusCodes.Status204NoContent, (await waiting.Next()).Response.StatusCode);
        await Limit.MoveTo(waiting, midnight);
        Assert.Equal(Job.Build, (string)(await waiting.Job())["kind"]!);
    }

    /// <summary>The owner saves the project's settings with what it runs on and its cap, as the
    /// settings page does, every other setting as it is.</summary>
    internal static async Task SetCap(Jobs.Jobs jobs, string runsOn, int? cap)
    {
        var path = $"/devflow/api/projects/{jobs.Project}/settings";
        var answer = await jobs.Routes.App.Get(path, Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, answer.Response.StatusCode);
        var settings = JsonNode.Parse(await Harness.Body(answer))!.AsObject();
        settings.Remove("preset");
        settings["trusted_logins"] = new JsonArray([.. settings["trusted_logins"]!.AsArray().Select(trusted => (JsonNode?)JsonValue.Create((string)trusted!["login"]!))]);
        settings["runs_on"] = runsOn;
        settings["api_daily_usd"] = cap;
        var saved = await jobs.Routes.App.SendJson("PUT", path, Contract.Token, settings.ToJsonString());
        Assert.Equal(StatusCodes.Status200OK, saved.Response.StatusCode);
    }

    /// <summary>
    /// A run of the project that finished at <paramref name="finishedAt"/> on
    /// <paramref name="credential"/>, costing <paramref name="cost"/> by Claude Code's estimate,
    /// written as the superuser: a build of a card number no world has, so no other gate reads it.
    /// </summary>
    internal static async Task Spent(Jobs.Jobs jobs, DateTimeOffset finishedAt, string credential, decimal cost)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, started_at, finished_at, result, run_minutes, credential, cost_usd)
            SELECT gen_random_uuid(), p.id, p.user_id, 900 + (SELECT count(*) FROM runs r WHERE r.project_id = p.id)::int, @worker, 'build',
                   'devflow/spent', 'main', @started, @finishedAt, 'succeeded', 90, @credential, @cost
            FROM projects p WHERE p.id = @project
            """,
            new { project = jobs.Project, worker = Jobs.Jobs.FirstWorker, started = finishedAt - TimeSpan.FromMinutes(10), finishedAt, credential, cost },
            cancellationToken: Ct));
    }
}