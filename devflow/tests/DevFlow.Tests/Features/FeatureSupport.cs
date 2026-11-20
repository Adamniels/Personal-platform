using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Tests.Jobs;
using DevFlow.Tests.PullRequests;

namespace DevFlow.Tests.Features;

/// <summary>
/// What the tests of a feature's page share: a feature of three cards, 11, 12 and 13 under the
/// parent 10, whose first card was built with pictures and a report marking a decision for the
/// owner, reviewed, and landed in the feature's branch, as the merge check does it. Against real
/// Postgres and the fake code host. See "A feature's page" in devflow/README.md.
/// </summary>
internal static class FeaturePage
{
    public const string Branch = "feature-10-dev-flow-features";

    public const int Parent = 10;

    public const int First = 11;

    public const int Second = 12;

    public const int Third = 13;

    /// <summary>The start of a PNG, all the screenshot route checks.</summary>
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    public static CancellationToken Ct => Jobs.Jobs.Ct;

    public static Guid User => Jobs.Jobs.User;

    public static string PathOf(Guid project, int number = Parent) => $"/devflow/api/projects/{project}/features/{number}";

    /// <summary>The address that answers the decision at <paramref name="position"/> of the
    /// report of <paramref name="run"/> "Fine as it is".</summary>
    public static string FinePath(Guid project, Guid run, int position, int feature = Parent) =>
        $"/devflow/api/projects/{project}/features/{feature}/decisions/{run}/{position}/fine";

    /// <summary>A feature of three cards, 11, 12 and 13, its parent 10.</summary>
    public static Task<Jobs.Jobs> Feature() =>
        Jobs.Jobs.Start(
        [
            FeatureJobs.Parent(Parent, "Dev flow: features", [First, Second, Third]),
            FeatureJobs.Sub(First, "Read the sub-issues"),
            FeatureJobs.Sub(Second, "Show each feature"),
            FeatureJobs.Sub(Third, "The feature's page"),
        ]);

    /// <summary>The feature's first card built by the fake worker with <see cref="Built.Report"/>
    /// and two pictures of one page, its pull request opened into the feature's branch, the
    /// project read so the branch is watched, CI green on its head and its review an approve, after
    /// which the merge check lands it; then the project read again, so the watch judges the merge.
    /// What the build answered.</summary>
    public static async Task<JsonObject> Landed(Jobs.Jobs jobs)
    {
        await FeatureJobs.FirstCommit(jobs);
        var result = Built.Prepared(Built.Report());
        result["screenshots"] = new JsonObject { ["names"] = new JsonArray("plan-desktop", "plan-phone"), ["note"] = null };
        var built = await Built.Run(jobs, result, new Dictionary<string, byte[]> { ["plan-desktop"] = Png, ["plan-phone"] = Png });
        Assert.Equal(First, (int)built["card"]!);
        await jobs.Routes.Read(jobs.Project, User);
        Merging.Merged.Ci(jobs, (string)built["head_commit"]!, Merging.Merged.GreenRuns, Merging.Merged.GreenStatuses);
        await Built.Review(jobs, Built.Verdict("approve"));
        Assert.Equal("done", (await Built.Card(jobs, First)).State);
        await jobs.Routes.Read(jobs.Project, User);
        return built;
    }

    /// <summary>The merge commit Dev flow recorded on the feature's branch.</summary>
    public static async Task<string> MergeCommit(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT commit FROM merges_made WHERE project_id = @project AND branch = @branch",
            new { project = jobs.Project, branch = Branch },
            cancellationToken: Ct));
    }

    /// <summary>The tokens the card's runs read and wrote, each summed, read past row level
    /// security.</summary>
    public static async Task<(long In, long Out)> Tokens(Jobs.Jobs jobs, int card)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        var row = await admin.QuerySingleAsync<(long, long)>(new CommandDefinition(
            """
            SELECT coalesce(sum(input_tokens), 0)::bigint, coalesce(sum(output_tokens), 0)::bigint
            FROM runs WHERE project_id = @project AND card_number = @card
            """,
            new { project = jobs.Project, card },
            cancellationToken: Ct));
        return row;
    }

    /// <summary>The answers kept, read past row level security.</summary>
    public static async Task<List<AnswerRow>> Answers(Jobs.Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<AnswerRow>(new CommandDefinition(
            """
            SELECT project_id AS ProjectId, user_id AS UserId, card_number AS CardNumber, run_id AS RunId, position AS Position,
                   answer AS Answer, answered_at AS AnsweredAt
            FROM decision_answers ORDER BY answered_at
            """,
            cancellationToken: Ct))];
    }

    /// <summary>Bob's project, with a feature 10 of his own of two cards, read once.</summary>
    public static async Task<Guid> BobsFeature(Jobs.Jobs jobs)
    {
        var bob = Reading.Routes.Bob;
        var project = await jobs.Routes.Project(bob, repository: 9002, name: "bobs");
        await Reading.Reading.Link(jobs.Routes.Door, bob, "octo-owner");
        jobs.Fake.Replace(bob, new FakeWorld(
        [
            new FakeInstallation(Reading.Reading.Installation, "Adamniels",
            [
                new FakeRepository(9002, "Adamniels", "bobs", [], [], [], [],
                [
                    FeatureJobs.Parent(Parent, "Bob's feature", [First, Second]),
                    FeatureJobs.Sub(First),
                    FeatureJobs.Sub(Second),
                ]),
            ]),
        ]));
        await jobs.Routes.Read(project, bob);
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        Assert.Equal(1, await admin.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM features WHERE project_id = @project", new { project }, cancellationToken: Ct)));
        return project;
    }

    internal sealed class AnswerRow
    {
        public Guid ProjectId { get; init; }

        public Guid UserId { get; init; }

        public int CardNumber { get; init; }

        public Guid RunId { get; init; }

        public int Position { get; init; }

        public string Answer { get; init; } = "";

        public DateTime AnsweredAt { get; init; }
    }
}