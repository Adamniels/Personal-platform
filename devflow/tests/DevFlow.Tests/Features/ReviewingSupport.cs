using System.Text.Json.Nodes;

using Dapper;

using DevFlow.Features;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Features;

/// <summary>
/// What the tests of a whole feature's review share: the feature of <see cref="Ending"/> finished,
/// the default branch brought in and its pull request opened, its branch read again a second later
/// as its next minute reads it, and the minute that decides its review and whether it is ready; the
/// fake worker answering its review; and what the database holds afterwards. Against real Postgres
/// and the fake code host. See "Reviewing a feature" in devflow/README.md.
/// </summary>
internal static class Reviewing
{
    public static CancellationToken Ct => Ending.Ct;

    /// <summary>The minute that decides each feature's review and whether it is ready, as its loop
    /// runs it.</summary>
    public static Task Decide(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<FeatureReadiness>().DecidePending(jobs.Project, Ending.User, Ct);

    /// <summary>The project read a second later, as its next minute reads it.</summary>
    public static async Task ReadAgain(Jobs.Jobs jobs)
    {
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(1));
        await jobs.Routes.Read(jobs.Project, Ending.User);
    }

    /// <summary>The project read a second later, then its end's minute and the minute that decides
    /// its review, as the loop runs them one after the other.</summary>
    public static async Task Minute(Jobs.Jobs jobs)
    {
        await ReadAgain(jobs);
        await Ending.End(jobs);
        await Decide(jobs);
    }

    /// <summary>
    /// The feature's two cards done, the default branch moved on by the owner, brought into the
    /// feature's branch and the feature's pull request opened, then the next minute, which reads its
    /// branch and decides its review. The commit the feature's branch is at, the merge that brought
    /// the default branch in.
    /// </summary>
    public static async Task<string> Published(Jobs.Jobs jobs)
    {
        await Ending.Finished(jobs);
        await Ending.MoveMain(jobs);
        await Ending.ReadAndEnd(jobs);
        await Minute(jobs);
        return await Head(jobs);
    }

    /// <summary>The fake worker takes the project's next job, the whole feature's review, and ends
    /// it with <paramref name="verdict"/>; its answer.</summary>
    public static async Task<JsonObject> Review(Jobs.Jobs jobs, JsonNode? verdict = null)
    {
        jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        var ran = await Built.Run(jobs, Built.PreparedVerdict(verdict));
        Assert.Equal("feature_review", (string)ran["kind"]!);
        return ran;
    }

    /// <summary>The commit the feature's branch is at, as its watch last named it.</summary>
    public static async Task<string> Head(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<string>(new CommandDefinition(
            """
            SELECT b.last_commit FROM branch_watches b JOIN features f ON f.id = b.feature_id
            WHERE f.project_id = @project AND f.number = @parent
            """,
            new { project = jobs.Project, parent = Ending.Parent },
            cancellationToken: Ct));
    }

    /// <summary>The owner pushes a commit on the feature's branch, which the watch finds fine; its
    /// id.</summary>
    public static async Task<string> PushOnFeature(Jobs.Jobs jobs) =>
        await jobs.Fake.Push(Ending.User, FeatureJobs.Repo, Ending.Branch, "A change of my own", Jobs.Jobs.Owner, Ct)
        ?? throw new InvalidOperationException("the fake's git made no commit");

    /// <summary>The feature's reviews, oldest first, read past row level security.</summary>
    public static async Task<List<FeatureReviewRow>> Reviews(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<FeatureReviewRow>(new CommandDefinition(
            """
            SELECT run_id AS RunId, project_id AS ProjectId, user_id AS UserId, feature_number AS FeatureNumber,
                   reviewed_commit AS ReviewedCommit, verdict AS Verdict, summary AS Summary, findings::text AS Findings,
                   for_owner::text AS ForOwner, pull_request_number AS PullRequestNumber, posted_at AS PostedAt
            FROM feature_reviews WHERE project_id = @project ORDER BY reviewed_at, run_id
            """,
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    /// <summary>The feature's own runs of <paramref name="kind"/>, oldest first, read past row level
    /// security.</summary>
    public static async Task<List<FeatureRunRow>> Runs(Jobs.Jobs jobs, string kind)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<FeatureRunRow>(new CommandDefinition(
            """
            SELECT id AS Id, card_number AS CardNumber, feature_number AS FeatureNumber, kind AS Kind, branch AS Branch, base AS Base,
                   head_commit AS HeadCommit, result AS Result, input_tokens AS InputTokens, finished_at AS FinishedAt
            FROM runs WHERE project_id = @project AND kind = @kind ORDER BY started_at, id
            """,
            new { project = jobs.Project, kind },
            cancellationToken: Ct))];
    }

    internal sealed class FeatureReviewRow
    {
        public Guid RunId { get; init; }

        public Guid ProjectId { get; init; }

        public Guid UserId { get; init; }

        public int FeatureNumber { get; init; }

        public string ReviewedCommit { get; init; } = "";

        public string Verdict { get; init; } = "";

        public string Summary { get; init; } = "";

        public string Findings { get; init; } = "";

        public string ForOwner { get; init; } = "";

        public int PullRequestNumber { get; init; }

        public DateTime? PostedAt { get; init; }
    }

    internal sealed class FeatureRunRow
    {
        public Guid Id { get; init; }

        public int? CardNumber { get; init; }

        public int? FeatureNumber { get; init; }

        public string Kind { get; init; } = "";

        public string Branch { get; init; } = "";

        public string Base { get; init; } = "";

        public string? HeadCommit { get; init; }

        public string? Result { get; init; }

        public long? InputTokens { get; init; }

        public DateTime? FinishedAt { get; init; }
    }
}