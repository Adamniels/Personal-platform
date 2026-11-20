using Dapper;

using DevFlow.CodeHost;
using DevFlow.Features;
using DevFlow.Tests.Jobs;

using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Features;

/// <summary>
/// What the tests of a feature's end share: a feature of two cards, 11 and 12 under the parent 10,
/// whose first card is built, reviewed and landed in the feature's branch by the merge check, as
/// <see cref="FeaturePage.Landed"/> does it, and whose second is put done as its landing would
/// leave it; the default branch moved on by the owner; the end's minute, as its loop runs it; and
/// what the database and the fake hold afterwards. Against real Postgres and the fake code host.
/// See "The end of a feature" in devflow/README.md.
/// </summary>
internal static class Ending
{
    public const string Branch = FeaturePage.Branch;

    public const int Parent = FeaturePage.Parent;

    public const int First = FeaturePage.First;

    public const int Second = FeaturePage.Second;

    /// <summary>The pull request the second card is put as having landed in.</summary>
    public const int SecondPull = 40;

    /// <summary>The message the app's merge commit says.</summary>
    public const string Message = "Bring main into feature-10-dev-flow-features";

    public static CancellationToken Ct => Jobs.Jobs.Ct;

    public static Guid User => Jobs.Jobs.User;

    /// <summary>A feature of two cards, 11 and 12, its parent 10, with <paramref name="more"/> as
    /// further issues of the repository.</summary>
    public static Task<Jobs.Jobs> Feature(params FakeIssue[] more) =>
        Jobs.Jobs.Start(
        [
            FeatureJobs.Parent(Parent, "Dev flow: features", [First, Second]),
            FeatureJobs.Sub(First, "Read the sub-issues"),
            FeatureJobs.Sub(Second, "Show each feature"),
            .. more,
        ]);

    /// <summary>The second card done, as its landing in pull request <paramref name="pull"/> and
    /// the app telling its issue would leave it.</summary>
    public static async Task Done(Jobs.Jobs jobs, int card = Second, int pull = SecondPull)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            UPDATE cards SET state = 'done', reason = NULL, pull_request_number = @pull, landing_told_for = @pull,
                             pull_request_url = 'https://github.com/Adamniels/Personal-platform/pull/' || @pull
            WHERE project_id = @project AND number = @card
            """,
            new { project = jobs.Project, card, pull },
            cancellationToken: Ct));
    }

    /// <summary>The first card landed and the second done, so every card of the feature is.</summary>
    public static async Task Finished(Jobs.Jobs jobs)
    {
        await FeaturePage.Landed(jobs);
        await Done(jobs);
    }

    /// <summary>The owner pushes a commit on the default branch, which the watch finds fine; its
    /// id.</summary>
    public static async Task<string> MoveMain(Jobs.Jobs jobs, string message = "Main moves on") =>
        await jobs.Fake.Push(User, FeatureJobs.Repo, "main", message, Jobs.Jobs.Owner, Ct)
        ?? throw new InvalidOperationException("the fake's git made no commit");

    /// <summary>Bringing the default branch in, then the feature's pull request, for every feature
    /// whose end is due, as its minute does.</summary>
    public static Task End(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<FeatureEnds>().EndPending(jobs.Project, User, Ct);

    /// <summary>The project read, then the end's minute.</summary>
    public static async Task ReadAndEnd(Jobs.Jobs jobs)
    {
        await jobs.Routes.Read(jobs.Project, User);
        await End(jobs);
    }

    /// <summary>The fake's bringing in, by anyone, oldest first.</summary>
    public static List<FakeWrite> BringIns(Jobs.Jobs jobs) =>
        [.. jobs.Fake.Writes(User).Where(write => write.Kind == "branch_merge")];

    /// <summary>How many times the code host was asked to bring a branch in.</summary>
    public static int Asked(Jobs.Jobs jobs) => jobs.Routes.Host.Calls.Count(call => call.Operation == "BringIn");

    /// <summary>The feature as stored, read past row level security.</summary>
    public static async Task<EndRow> Row(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<EndRow>(new CommandDefinition(
            """
            SELECT state AS State, reason AS Reason, branch AS Branch, brought_in_commit AS BroughtInCommit,
                   bring_in_failed_at AS BringInFailedAt, pull_request_number AS PullRequestNumber, pull_request_url AS PullRequestUrl
            FROM features WHERE project_id = @project AND number = @number
            """,
            new { project = jobs.Project, number = Parent },
            cancellationToken: Ct));
    }

    /// <summary>Puts the feature in <paramref name="state"/>, as a later slice does.</summary>
    public static async Task SetState(Jobs.Jobs jobs, string state, string? reason = null)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE features SET state = @state, reason = @reason WHERE project_id = @project AND number = @number",
            new { project = jobs.Project, number = Parent, state, reason },
            cancellationToken: Ct));
    }

    /// <summary>Every merge Dev flow recorded on the feature's branch, oldest first.</summary>
    public static async Task<List<(string Commit, int? Number)>> MergesMade(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(string, int?)>(new CommandDefinition(
            "SELECT commit, number FROM merges_made WHERE project_id = @project AND branch = @branch ORDER BY merged_at, commit",
            new { project = jobs.Project, branch = Branch },
            cancellationToken: Ct))];
    }

    /// <summary>Whether the project is stopped.</summary>
    public static async Task<bool> Stopped(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM project_stops WHERE project_id = @project)",
            new { project = jobs.Project },
            cancellationToken: Ct));
    }

    /// <summary>The commit the feature's branch is at in the fake's git.</summary>
    public static async Task<bool> Holds(Jobs.Jobs jobs, string commit) =>
        await jobs.Fake.HasCommit(User, FeatureJobs.Repo, commit, Ct);

    internal sealed class EndRow
    {
        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public string? Branch { get; init; }

        public string? BroughtInCommit { get; init; }

        public string? BringInFailedAt { get; init; }

        public int? PullRequestNumber { get; init; }

        public string? PullRequestUrl { get; init; }
    }
}