using System.Text.Json.Nodes;

using DevFlow.Tests.Jobs;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Workers;

/// <summary>
/// The workers list's <c>held</c>, why a project hands out no work whichever worker asks, as the
/// project's page and the project's card on the Projects tab say it: everything paused, then the
/// project paused, ahead of every hold before them. Against real Postgres and the fake code host.
/// See "Workers" in devflow/README.md.
/// </summary>
public sealed class WorkerListTests
{
    [Fact]
    public async Task HeldSaysPaused()
    {
        await using var jobs = await DevFlow.Tests.Jobs.Jobs.Start(login: false);

        // Before any pause, the missing Claude login is why.
        Assert.Equal("no_claude_login", await Held(jobs));

        // Stopped by a commit that landed on main.
        await Merging.Merged.Stop(jobs);
        Assert.Equal("stopped", await Held(jobs));

        // Paused, ahead of the stop.
        await PauseTests.Pause(jobs, true);
        Assert.Equal("paused", await Held(jobs));

        // Everything paused, ahead of the project's own pause.
        await PauseTests.PauseAll(jobs, true);
        Assert.Equal("all_paused", await Held(jobs));

        // Continued, each in turn, back to what was there.
        await PauseTests.PauseAll(jobs, false);
        Assert.Equal("paused", await Held(jobs));
        await PauseTests.Pause(jobs, false);
        Assert.Equal("stopped", await Held(jobs));
    }

    /// <summary>The project's held as the workers list answers it, after checking the projects
    /// list's summary answers the same, and the project's <c>paused_at</c> there is set exactly
    /// while it is paused on its own.</summary>
    private static async Task<string?> Held(DevFlow.Tests.Jobs.Jobs jobs)
    {
        var workers = await jobs.Routes.App.Get($"/devflow/api/projects/{jobs.Project}/workers", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, workers.Response.StatusCode);
        var held = (string?)JsonNode.Parse(await Harness.Body(workers))!["held"];

        var projects = await jobs.Routes.App.Get("/devflow/api/projects", Contract.Token);
        Assert.Equal(StatusCodes.Status200OK, projects.Response.StatusCode);
        var listed = Assert.Single(JsonNode.Parse(await Harness.Body(projects))!["projects"]!.AsArray())!;
        Assert.Equal(held, (string?)listed["summary"]!["workers"]!["held"]);
        await using var admin = await jobs.Routes.Database.OpenAdmin(TestContext.Current.CancellationToken);
        Assert.Equal(listed["paused_at"] is not null, await Dapper.SqlMapper.QuerySingleAsync<bool>(admin, new Dapper.CommandDefinition(
            "SELECT paused_at IS NOT NULL FROM projects WHERE id = @project",
            new { project = jobs.Project },
            cancellationToken: TestContext.Current.CancellationToken)));
        return held;
    }
}