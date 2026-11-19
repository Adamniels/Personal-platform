using Dapper;

using DevFlow.CodeHost;
using DevFlow.Reading;

namespace DevFlow.Tests.Cards;

/// <summary>
/// What the tests of features share: issues staged in the fake, with their sub-issues, a project
/// of Alice's with her GitHub linked as <see cref="Owner"/>, a read of it, and what it stored, the
/// features and every card with the feature it belongs to, read as the superuser past row level
/// security. See "Features and their cards" in devflow/docs/devflow-architecture.md.
/// </summary>
internal static class FeatureSupport
{
    public const string Owner = "octo-owner";
    public const string Stranger = "octo-stranger";

    /// <summary>The second repository of the same installation, for a sub-issue elsewhere.</summary>
    public const long Elsewhere = 9002;

    public static readonly DateTimeOffset At = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    public static CancellationToken Ct => Reading.Reading.Ct;

    /// <summary>An issue of the owner's, open and unlabelled unless said, with its sub-issues in
    /// this repository by number, in their order.</summary>
    public static FakeIssue Issue(
        int number,
        string? author = null,
        IReadOnlyList<string>? labels = null,
        bool open = true,
        IReadOnlyList<int>? blockedBy = null,
        IReadOnlyList<IssueEdit>? edits = null,
        IReadOnlyList<int>? subIssues = null,
        IReadOnlyList<FakeSubIssue>? elsewhere = null,
        DateTimeOffset? created = null,
        string? body = null) => new(
        number,
        $"Issue {number}",
        author ?? Owner,
        labels ?? [],
        body ?? $"Build the thing of issue {number}.",
        new Uri($"https://github.com/Adamniels/Personal-platform/issues/{number}"),
        created ?? At.AddMinutes(number),
        open,
        blockedBy ?? [],
        edits ?? [],
        subIssues is null && elsewhere is null ? null : [.. (subIssues ?? []).Select(sub => new FakeSubIssue(sub, null)), .. elsewhere ?? []]);

    /// <summary>A labelled issue, the pick-up label first among its labels.</summary>
    public static FakeIssue Labelled(int number, IReadOnlyList<int>? subIssues = null, IReadOnlyList<string>? more = null, string? author = null, IReadOnlyList<IssueEdit>? edits = null, IReadOnlyList<FakeSubIssue>? elsewhere = null) =>
        Issue(number, author: author, labels: ["devflow", .. more ?? []], subIssues: subIssues, edits: edits, elsewhere: elsewhere);

    /// <summary>The issues, and any pull requests, staged in Alice's one repository, and
    /// <paramref name="otherIssues"/> in <see cref="Elsewhere"/>.</summary>
    public static void Stage(Reading.Reading reading, IReadOnlyList<FakeIssue> issues, IReadOnlyList<PullRequest>? pulls = null, IReadOnlyList<FakeIssue>? otherIssues = null) =>
        reading.Fake.Replace(Reading.Reading.Alice, new FakeWorld(
        [
            new FakeInstallation(Reading.Reading.Installation, "Adamniels",
            [
                new FakeRepository(Reading.Reading.RepositoryId, "Adamniels", "Personal-platform", pulls ?? [], [], [], [], issues),
                new FakeRepository(Elsewhere, "Adamniels", "elsewhere", [], [], [], [], otherIssues ?? []),
            ]),
        ]));

    public static async Task<(Reading.Reading Reading, Guid Project)> Start()
    {
        var reading = await Reading.Reading.Start();
        var project = await reading.Project(Reading.Reading.Alice);
        await reading.LinkOwner(Reading.Reading.Alice, Owner);
        return (reading, project);
    }

    public static async Task Read(Reading.Reading reading, Guid project) =>
        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Reading.Alice, Ct));

    /// <summary>The project's features, by number.</summary>
    public static async Task<List<FeatureRow>> StoredFeatures(Reading.Reading reading, Guid project)
    {
        await using var admin = await reading.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<FeatureRow>(new CommandDefinition(
            """
            SELECT user_id AS UserId, number AS Number, title AS Title, web_url AS WebUrl, state AS State, reason AS Reason,
                   priority AS Priority, issue_created_at AS IssueCreatedAt, picked_body_sha256 AS PickedBodySha256,
                   picked_title AS PickedTitle, picked_edit AS PickedEdit, picked_body AS PickedBody, unlabelled AS Unlabelled,
                   fix_round AS FixRound
            FROM features WHERE project_id = @project ORDER BY number
            """,
            new { project },
            cancellationToken: Ct))];
    }

    /// <summary>The project's cards, by number, each with its feature's number and its
    /// position.</summary>
    public static async Task<List<FeatureCardRow>> StoredCards(Reading.Reading reading, Guid project)
    {
        await using var admin = await reading.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<FeatureCardRow>(new CommandDefinition(
            """
            SELECT c.number AS Number, c.state AS State, c.reason AS Reason, c.waits_for AS WaitsFor, c.priority AS Priority,
                   f.number AS Feature, c.position AS Position, c.picked_body_sha256 IS NOT NULL AS Picked, c.unlabelled AS Unlabelled
            FROM cards c LEFT JOIN features f ON f.id = c.feature_id
            WHERE c.project_id = @project ORDER BY c.number
            """,
            new { project },
            cancellationToken: Ct))];
    }

    /// <summary>Puts a card where a run would have, as later parts set it.</summary>
    public static async Task SetState(Reading.Reading reading, Guid project, int number, string state)
    {
        await using var admin = await reading.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET state = @state WHERE project_id = @project AND number = @number", new { project, number, state }, cancellationToken: Ct));
    }

    internal sealed class FeatureRow
    {
        public Guid UserId { get; init; }

        public int Number { get; init; }

        public string Title { get; init; } = "";

        public string WebUrl { get; init; } = "";

        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public string Priority { get; init; } = "";

        public DateTime IssueCreatedAt { get; init; }

        public byte[]? PickedBodySha256 { get; init; }

        public string? PickedTitle { get; init; }

        public string? PickedEdit { get; init; }

        public string? PickedBody { get; init; }

        public bool Unlabelled { get; init; }

        public int FixRound { get; init; }
    }

    /// <summary>A stored card, set by property: Dapper reads <c>text[]</c> as an array no
    /// constructor parameter matches.</summary>
    internal sealed class FeatureCardRow
    {
        public int Number { get; init; }

        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public string[] WaitsFor { get; init; } = [];

        public string Priority { get; init; } = "";

        public int? Feature { get; init; }

        public int? Position { get; init; }

        public bool Picked { get; init; }

        public bool Unlabelled { get; init; }
    }
}