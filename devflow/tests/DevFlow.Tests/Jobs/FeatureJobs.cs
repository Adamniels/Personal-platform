using Dapper;

using DevFlow.CodeHost;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// What the tests of a feature's cards being handed out share, on top of <see cref="Jobs"/>: a
/// parent issue with the pick-up label and its sub-issues, written by the owner and unlabelled, a
/// card finished as a run and the merge of its pull request would leave it, and the feature as
/// stored. See "Jobs" in devflow/README.md and "Handing out work" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
internal static class FeatureJobs
{
    public static CancellationToken Ct => Jobs.Ct;

    /// <summary>The project's repository as the fake and the code host name it.</summary>
    public static readonly CodeHost.Repository Repo = new(Reading.Reading.Installation, Reading.Reading.RepositoryId, "Adamniels", "Personal-platform");

    /// <summary>A parent the owner wrote and labelled, with <paramref name="cards"/> as its
    /// sub-issues, in that order.</summary>
    public static FakeIssue Parent(int number, string title, IReadOnlyList<int> cards, string? body = null) =>
        Jobs.Card(number, title, body ?? $"The whole of feature {number}.") with { SubIssues = [.. cards.Select(card => new FakeSubIssue(card, null))] };

    /// <summary>A sub-issue the owner wrote, with no label of its own, blocked by
    /// <paramref name="blockedBy"/> in the same repository.</summary>
    public static FakeIssue Sub(int number, string? title = null, IReadOnlyList<int>? blockedBy = null) =>
        Jobs.Card(number, title, labels: []) with { BlockedBy = blockedBy ?? [] };

    /// <summary>Ends every run of the card under way, as its result coming back would, and puts the
    /// card in <paramref name="state"/>, as the merge of its pull request or a stop would.</summary>
    public static async Task Finish(Jobs jobs, int number, string state)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            UPDATE runs SET finished_at = started_at + interval '1 minute', result = 'succeeded'
            WHERE project_id = @project AND card_number = @number AND finished_at IS NULL
            """,
            new { project = jobs.Project, number },
            cancellationToken: Ct));
        await SetState(jobs, number, state);
    }

    /// <summary>Puts the card in <paramref name="state"/>, as later parts set it.</summary>
    public static async Task SetState(Jobs jobs, int number, string state)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET state = @state WHERE project_id = @project AND number = @number",
            new { project = jobs.Project, number, state },
            cancellationToken: Ct));
    }

    /// <summary>The feature with the number as stored, read past row level security.</summary>
    public static async Task<FeatureRow> Feature(Jobs jobs, int number)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<FeatureRow>(new CommandDefinition(
            """
            SELECT number AS Number, state AS State, reason AS Reason, branch AS Branch, branch_base_commit AS BranchBaseCommit
            FROM features WHERE project_id = @project AND number = @number
            """,
            new { project = jobs.Project, number },
            cancellationToken: Ct));
    }

    /// <summary>The app's own commit on the default branch of the fake's git, as every real
    /// repository has one before anything is built on it; its id.</summary>
    public static async Task<string> FirstCommit(Jobs jobs) =>
        await jobs.Fake.CommitOnBranch(Jobs.User, Repo, "main", "main", "The first commit", new Committer(FakeCodeHost.AppLogin, FakeCodeHost.AppEmail), Ct)
        ?? throw new InvalidOperationException("the fake's git made no commit");

    /// <summary>The fake's writes for the test's user that made a branch.</summary>
    public static IReadOnlyList<FakeWrite> Branches(Jobs jobs) => [.. jobs.Fake.Writes(Jobs.User).Where(write => write.Kind == "branch")];

    internal sealed class FeatureRow
    {
        public int Number { get; init; }

        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public string? Branch { get; init; }

        public string? BranchBaseCommit { get; init; }
    }
}