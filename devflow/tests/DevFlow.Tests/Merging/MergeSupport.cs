using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Decisions;
using DevFlow.Merging;
using DevFlow.Tests.PullRequests;

using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Merging;

/// <summary>
/// A card carried to an approve by the fake worker on top of <see cref="Built"/>, and what the merge
/// check needs around it: CI staged on a commit as GitHub would hold it, the project's setting, a
/// stop, the merge check and the decision issues run as their loop runs them, and what the database
/// holds afterwards. See "The merge check" in devflow/README.md.
/// </summary>
internal static class Merged
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The card the harness's project holds, which every test here carries.</summary>
    public const int Card = 1;

    /// <summary>CI that passed: one check run by GitHub Actions and one status, both green.</summary>
    public static readonly IReadOnlyList<CheckRun> GreenRuns = [new("test", CheckStatus.Completed, CheckConclusion.Success, "github-actions")];

    public static readonly IReadOnlyList<CommitStatus> GreenStatuses = [new("deploy/preview", StatusState.Success, "Ready")];

    /// <summary>The card built with <see cref="Built.Report"/>, its pull request opened, CI staged
    /// green on its head unless <paramref name="green"/> is false, and its review an approve. The
    /// fake worker runs the merge check after the approve, as the result route asks for one. What
    /// the build answered.</summary>
    public static async Task<JsonObject> Approved(Jobs.Jobs jobs, bool green = true)
    {
        var built = await Built.Run(jobs, Built.Prepared(Built.Report()));
        if (green)
        {
            Ci(jobs, (string)built["head_commit"]!, GreenRuns, GreenStatuses);
        }

        await Built.Review(jobs, Built.Verdict("approve"));
        return built;
    }

    /// <summary>Stages <paramref name="checkRuns"/> and <paramref name="statuses"/> on the commit,
    /// replacing what it had, as CI posting them would leave GitHub.</summary>
    public static void Ci(Jobs.Jobs jobs, string commit, IReadOnlyList<CheckRun> checkRuns, IReadOnlyList<CommitStatus>? statuses = null) =>
        Change(jobs, repository => repository with
        {
            Commits = [.. repository.Commits.Where(each => each.Id != commit), new FakeCommit(commit, statuses ?? [], checkRuns)],
        });

    /// <summary>The issue with the number changed as someone on GitHub would change it.</summary>
    public static void Issue(Jobs.Jobs jobs, int number, Func<FakeIssue, FakeIssue> change) =>
        Change(jobs, repository => repository with
        {
            Issues = [.. repository.Issues.Select(issue => issue.Number == number ? change(issue) : issue)],
        });

    /// <summary>The user's one repository changed by <paramref name="change"/>.</summary>
    public static void Change(Jobs.Jobs jobs, Func<FakeRepository, FakeRepository> change)
    {
        var world = jobs.Fake.World(Jobs.Jobs.User);
        jobs.Fake.Replace(Jobs.Jobs.User, world with
        {
            Installations = [.. world.Installations.Select(installation => installation with
            {
                Repositories = [.. installation.Repositories.Select(change)],
            })],
        });
    }

    /// <summary>The merge check over the project's cards, as its minute runs it.</summary>
    public static Task Check(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<MergeChecker>().CheckPending(jobs.Project, Jobs.Jobs.User, Ct);

    /// <summary>The decision issues waiting to be opened, opened, as their minute does.</summary>
    public static Task OpenDecisions(Jobs.Jobs jobs) =>
        jobs.Routes.App.Services.GetRequiredService<DecisionIssues>().OpenPending(jobs.Project, Jobs.Jobs.User, Ct);

    /// <summary>The project's setting for whether a card's pull request waits for the owner.</summary>
    public static async Task Waits(Jobs.Jobs jobs, string waits)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE project_settings SET waits_for_owner = @waits WHERE project_id = @project",
            new { waits, project = jobs.Project },
            cancellationToken: Ct));
    }

    /// <summary>The project stopped, as the watch stops it for something that landed on main.</summary>
    public static async Task Stop(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        var now = jobs.Routes.Time.GetUtcNow();
        await admin.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_stops (project_id, user_id, stopped_at, branch, activity_id, activity_type, actor, commit, landed_at)
            VALUES (@project, @user, @now, 'main', 9001, 'push', 'mallory', @commit, @now)
            """,
            new { project = jobs.Project, user = Jobs.Jobs.User, now, commit = new string('e', 40) },
            cancellationToken: Ct));
    }

    /// <summary>Runs SQL past row level security, for a state no route makes.</summary>
    public static async Task Sql(Jobs.Jobs jobs, string sql)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(sql, new { project = jobs.Project }, cancellationToken: Ct));
    }

    /// <summary>The merges the app made in the user's world, oldest first.</summary>
    public static List<FakeWrite> AppMerges(Jobs.Jobs jobs) =>
        [.. jobs.Fake.Writes(Jobs.Jobs.User).Where(write => write.Kind == "merge" && write.By == FakeCodeHost.AppLogin)];

    /// <summary>The issues the app opened in the user's world, oldest first.</summary>
    public static List<FakeWrite> IssuesOpened(Jobs.Jobs jobs) =>
        [.. jobs.Fake.Writes(Jobs.Jobs.User).Where(write => write.Kind == "issue")];

    /// <summary>The merge commits Dev flow recorded for the project, by pull request.</summary>
    public static async Task<List<(string Commit, int Number)>> MergesMade(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<(string, int)>(new CommandDefinition(
            "SELECT commit, number FROM merges_made WHERE project_id = @project ORDER BY merged_at",
            new { project = jobs.Project },
            cancellationToken: Ct))];
    }

    /// <summary>The pull request with the number as the user's world holds it.</summary>
    public static PullRequest Pull(Jobs.Jobs jobs, int number) =>
        jobs.Fake.World(Jobs.Jobs.User).Installations.SelectMany(installation => installation.Repositories)
            .SelectMany(repository => repository.PullRequests).Single(pull => pull.Number == number);

    /// <summary>The card's pull request's number, as Dev flow recorded it.</summary>
    public static async Task<int> PullNumber(Jobs.Jobs jobs) => (await Built.Card(jobs, Card)).PullRequestNumber!.Value;
}