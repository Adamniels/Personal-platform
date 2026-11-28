using System.Globalization;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Protocol;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Stops;

/// <summary>
/// Claude's usage limit as the tests of the user's limit, continuing on the API key and its cap
/// meet it: a job failed outside its card with the reset Claude Code named, the user's limit as
/// the database holds it, and what a project runs on set past the route. See "Stops" and "The API
/// key" in devflow/README.md.
/// </summary>
internal static class Limit
{
    public static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The worker ends <paramref name="job"/> failed outside its card at Claude's usage
    /// limit, which resets at <paramref name="resetsAt"/>, with the project's key or
    /// <paramref name="key"/>, as <paramref name="worker"/>.</summary>
    public static async Task Fail(Jobs.Jobs jobs, JsonObject job, DateTimeOffset? resetsAt, string? key = null, Guid? worker = null)
    {
        var failure = Jobs.Jobs.Failure(worker ?? Jobs.Jobs.FirstWorker, (string)job["branch"]!, inCard: false, "Claude's usage limit was reached");
        if (resetsAt is { } at)
        {
            failure["failure"]!["resets_at"] = at.ToString("O", CultureInfo.InvariantCulture);
        }

        var sent = await Workers.Workers.AsWorker(jobs.Routes.App, WorkerApi.ResultPath((Guid)job["job_id"]!), failure.ToJsonString(), null, Workers.Workers.Bearer(key ?? jobs.Key));
        Assert.Equal(StatusCodes.Status204NoContent, sent.Response.StatusCode);
    }

    /// <summary>The user's limit as stored, its reset and when it was recorded, or null.</summary>
    public static async Task<(DateTimeOffset ResetsAt, DateTimeOffset RecordedAt)?> Of(Jobs.Jobs jobs, Guid user)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        var row = await admin.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT resets_at AS ResetsAt, recorded_at AS RecordedAt FROM subscription_limits WHERE user_id = @user",
            new { user },
            cancellationToken: Ct));
        return row is null ? null : (Utc(row.ResetsAt), Utc(row.RecordedAt));
    }

    /// <summary>The user's limit set to <paramref name="resetsAt"/> as the superuser, past the
    /// result that would record it.</summary>
    public static async Task Set(Jobs.Jobs jobs, DateTimeOffset resetsAt)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO subscription_limits (user_id, resets_at, recorded_at) VALUES (@user, @resetsAt, @now)
            ON CONFLICT (user_id) DO UPDATE SET resets_at = EXCLUDED.resets_at
            """,
            new { user = Jobs.Jobs.User, resetsAt, now = jobs.Routes.Time.GetUtcNow() },
            cancellationToken: Ct));
    }

    /// <summary>Moves the app's clock to <paramref name="at"/>, then reads each project and has its
    /// worker say hello, so its default branch is watched and its worker seen, as they would be.</summary>
    public static async Task MoveTo(Jobs.Jobs jobs, DateTimeOffset at, params (Guid Project, Guid Worker, string Key)[] others)
    {
        jobs.Routes.Time.SetUtcNow(at.ToUniversalTime());
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User));
        await jobs.SayHello(Jobs.Jobs.FirstWorker);
        foreach (var (project, worker, key) in others)
        {
            Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(project, Jobs.Jobs.User));
            var hello = new JsonObject { ["worker_id"] = worker, ["name"] = "fodr-worker", ["recipe_version"] = "1", ["base_image"] = Jobs.Jobs.Image };
            var said = await Workers.Workers.AsWorker(jobs.Routes.App, Workers.Workers.HelloPath, hello.ToJsonString(), null, Workers.Workers.Bearer(key));
            Assert.Equal(StatusCodes.Status204NoContent, said.Response.StatusCode);
        }
    }

    /// <summary><paramref name="at"/> in Stockholm's own offset, as the rule writes its times.</summary>
    public static DateTimeOffset InZone(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Stockholm);

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private sealed record Row(DateTime ResetsAt, DateTime RecordedAt);
}