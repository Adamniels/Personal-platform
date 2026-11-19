using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Stops;
using DevFlow.Tests.PullRequests;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Stops;

/// <summary>
/// Cards taken to a stop by the fake worker on top of <see cref="Built"/>: failures prepared in the
/// card or outside it, with or without a commit pushed, and what the database holds of a stop. See
/// "Stops" in devflow/README.md.
/// </summary>
internal static class Stop
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A prepared failure in the card, <paramref name="reason"/>; with
    /// <paramref name="timedOut"/>, one that reached its time limit.</summary>
    public static JsonObject InCard(string reason = "it reached the most turns, 200 turns", bool timedOut = false)
    {
        var result = Built.PreparedFailure(true, reason);
        if (timedOut)
        {
            result["failure"]!["timed_out"] = true;
        }

        return result;
    }

    /// <summary>A prepared failure outside the card, <paramref name="reason"/>, with the time
    /// Claude's usage limit resets when given.</summary>
    public static JsonObject Outside(string reason = "Claude's API answered an error 529", DateTimeOffset? resetsAt = null)
    {
        var result = Built.PreparedFailure(false, reason);
        if (resetsAt is { } at)
        {
            result["failure"]!["resets_at"] = at.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }

        return result;
    }

    /// <summary>
    /// A minute on, the project read as its minute would, and the fake worker takes its next job and
    /// ends it with <paramref name="result"/>; with <paramref name="leaveNoCommit"/> nothing is
    /// pushed, as a run that failed before it got that far. Its answer, or null when it had no job.
    /// </summary>
    public static async Task<JsonObject?> Run(Jobs.Jobs jobs, JsonObject result, bool leaveNoCommit = false)
    {
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(DevFlow.Reading.ReadResult.Read, await jobs.Routes.Read(jobs.Project, Jobs.Jobs.User));
        var response = await jobs.Routes.App.SendJson("POST", Built.FakeWorkerPath, Contract.Token, new JsonObject
        {
            ["project_id"] = jobs.Project,
            ["result"] = result,
            ["leave_no_commit"] = leaveNoCommit,
        }.ToJsonString());
        if (response.Response.StatusCode == StatusCodes.Status204NoContent)
        {
            return null;
        }

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        return (await Workers.Workers.Json(response)).AsObject();
    }

    /// <summary>The stops of the project marked, as the opening loop's minute would.</summary>
    public static Task Mark(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<StopMarker>().MarkPending(jobs.Project, Jobs.Jobs.User, Ct);

    /// <summary>The app's writes of <paramref name="kind"/> on the issue or pull request with the
    /// number, oldest first.</summary>
    public static List<FakeWrite> ByApp(Jobs.Jobs jobs, string kind, int number) =>
        [.. jobs.Fake.Writes(Jobs.Jobs.User).Where(write => write.Kind == kind && write.Number == number && write.By == FakeCodeHost.AppLogin)];

    /// <summary>What the card holds of its stops and its count.</summary>
    public static async Task<StopRow> Card(Jobs.Jobs jobs, int number)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<StopRow>(new CommandDefinition(
            """
            SELECT state AS State, reason AS Reason, stopped_at AS StoppedAt, stop_marked_at AS StopMarkedAt,
                   in_card_failures AS InCardFailures, fix_rounds AS FixRounds, attention_labelled AS AttentionLabelled
            FROM cards WHERE project_id = @project AND number = @number
            """,
            new { project = jobs.Project, number },
            cancellationToken: Ct));
    }

    internal sealed class StopRow
    {
        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public DateTime? StoppedAt { get; init; }

        public DateTime? StopMarkedAt { get; init; }

        public int InCardFailures { get; init; }

        public int FixRounds { get; init; }

        public bool AttentionLabelled { get; init; }
    }
}