using System.Security.Cryptography;
using System.Text;

using Dapper;

using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Reading;
using DevFlow.Tests.Reading;

namespace DevFlow.Tests.Cards;

/// <summary>
/// Picking cards in each project's read: an open issue, not a pull request, carrying the pick-up
/// label becomes a card; it is queued only when the owner's linked login wrote it, nobody else
/// edited it, it is not held and nothing it is blocked by is open; anything else says why. Against
/// the fake and a real Postgres. See "Cards" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class PickingTests
{
    private const string Owner = "octo-owner";
    private const string Stranger = "octo-stranger";

    private static readonly DateTimeOffset At = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => Reading.Reading.Ct;

    private static FakeIssue Card(
        int number,
        string? author = null,
        IReadOnlyList<string>? labels = null,
        bool open = true,
        IReadOnlyList<int>? blockedBy = null,
        IReadOnlyList<IssueEdit>? edits = null,
        DateTimeOffset? created = null,
        string? body = null) => new(
        number,
        $"Card {number}",
        author ?? Owner,
        labels ?? ["devflow"],
        body ?? $"Build the thing of card {number}.",
        new Uri($"https://github.com/Adamniels/Personal-platform/issues/{number}"),
        created ?? At.AddMinutes(number),
        open,
        blockedBy ?? [],
        edits ?? []);

    /// <summary>The issues, and any pull requests, staged in <paramref name="user"/>'s one
    /// repository, or in <paramref name="repository"/>.</summary>
    private static void Stage(Reading.Reading reading, Guid user, IReadOnlyList<FakeIssue> issues, IReadOnlyList<PullRequest>? pulls = null, long repository = Reading.Reading.RepositoryId)
    {
        var world = reading.Fake.World(user);
        var installation = world.Installations.Count > 0 ? world.Installations[0] : new FakeInstallation(Reading.Reading.Installation, "Adamniels", []);
        var staged = new FakeRepository(repository, "Adamniels", repository == Reading.Reading.RepositoryId ? "Personal-platform" : $"repository-{repository}", pulls ?? [], [], [], [], issues);
        installation = installation with { Repositories = [.. installation.Repositories.Where(r => r.Id != repository), staged] };
        reading.Fake.Replace(user, new FakeWorld([installation]));
    }

    private static async Task<(Reading.Reading Reading, Guid Project)> Start()
    {
        var reading = await Reading.Reading.Start();
        var project = await reading.Project(Reading.Reading.Alice);
        await reading.LinkOwner(Reading.Reading.Alice, Owner);
        return (reading, project);
    }

    private static async Task Read(Reading.Reading reading, Guid project) =>
        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Reading.Alice, Ct));

    /// <summary>The project's cards, by number, read as the superuser past row level security.</summary>
    private static async Task<List<CardRow>> Cards(Reading.Reading reading, Guid project)
    {
        await using var admin = await reading.Database.OpenAdmin(Ct);
        return [.. await admin.QueryAsync<CardRow>(new CommandDefinition(
            """
            SELECT user_id AS UserId, number AS Number, title AS Title, web_url AS WebUrl, state AS State, reason AS Reason,
                   waits_for AS WaitsFor, priority AS Priority, issue_created_at AS IssueCreatedAt,
                   picked_body_sha256 AS PickedBodySha256, picked_title AS PickedTitle, picked_edit AS PickedEdit,
                   unlabelled AS Unlabelled
            FROM cards WHERE project_id = @project ORDER BY number
            """,
            new { project },
            cancellationToken: Ct))];
    }

    private static async Task SetState(Reading.Reading reading, Guid project, int number, string state)
    {
        await using var admin = await reading.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET state = @state WHERE project_id = @project AND number = @number", new { project, number, state }, cancellationToken: Ct));
    }

    private static byte[] Sha(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task ALabelledCardByTheOwnerIsQueued()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        var edits = new IssueEdit[]
        {
            new("UCE_first", Owner, At.AddMinutes(1), EditKind.Body),
            new("UCE_newest", Owner, At.AddMinutes(30), EditKind.Body),
            new("RTE_middle", Owner, At.AddMinutes(10), EditKind.Title),
        };
        // The owner's login is matched whatever its case, as GitHub's are.
        Stage(reading, Reading.Reading.Alice, [Card(12, author: "Octo-Owner", edits: edits), Card(13, labels: ["something-else"]), Card(14, open: false)]);

        await Read(reading, project);

        var card = Assert.Single(await Cards(reading, project));
        Assert.Equal(Reading.Reading.Alice, card.UserId);
        Assert.Equal(12, card.Number);
        Assert.Equal("Card 12", card.Title);
        Assert.Equal("https://github.com/Adamniels/Personal-platform/issues/12", card.WebUrl);
        Assert.Equal("queued", card.State);
        Assert.Null(card.Reason);
        Assert.Empty(card.WaitsFor);
        Assert.Equal("none", card.Priority);
        Assert.Equal(At.AddMinutes(12).UtcDateTime, card.IssueCreatedAt);

        // The record of what was read: the body's SHA-256, the title, and the newest edit.
        Assert.Equal(Sha("Build the thing of card 12."), card.PickedBodySha256);
        Assert.Equal("Card 12", card.PickedTitle);
        Assert.Equal("UCE_newest", card.PickedEdit);
    }

    [Fact]
    public async Task PullRequestsAreNeverCards()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        var pull = new PullRequest(20, "A labelled pull request", Owner, ["devflow"], new string('a', 40), "20-x", "main", false, "",
            new Uri("https://github.com/Adamniels/Personal-platform/pull/20"), At, PullRequestState.Open);
        Stage(reading, Reading.Reading.Alice, [Card(21)], [pull]);

        await Read(reading, project);

        Assert.Equal([21], (await Cards(reading, project)).Select(card => card.Number));
        // Nothing more is read of it than the list.
        Assert.DoesNotContain(reading.Host.Calls, call => call.Operation is "Edits" or "BlockedBy" && call.Argument == "20");
    }

    [Fact]
    public async Task ACardSomeoneElseWroteOrEditedIsIgnoredAndSaysSo()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading, Reading.Reading.Alice,
        [
            Card(30, author: Stranger),
            Card(31, edits: [new("UCE_1", Owner, At, EditKind.Body), new("UCE_2", Stranger, At.AddMinutes(5), EditKind.Body)]),
            Card(32, edits: [new("RTE_1", Stranger, At.AddMinutes(5), EditKind.Title)]),
            // An account that is gone is not the owner either.
            Card(33, edits: [new("UCE_3", "ghost", At.AddMinutes(5), EditKind.Body)]),
        ]);

        await Read(reading, project);

        var cards = await Cards(reading, project);
        Assert.All(cards, card => Assert.Equal("ignored", card.State));
        Assert.Equal(
        [
            $"{Stranger} is not a trusted account of this project",
            $"Edited by {Stranger}, who is not a trusted account of this project",
            $"Edited by {Stranger}, who is not a trusted account of this project",
            "Edited by ghost, who is not a trusted account of this project",
        ], cards.Select(card => card.Reason));
        // Never picked, so nothing was recorded of it.
        Assert.All(cards, card => Assert.Null(card.PickedBodySha256));
        Assert.Empty(await CardStore.Queue(reading.Door, Reading.Reading.Alice, project, Ct));
        // A card by someone else is not read further.
        Assert.DoesNotContain(reading.Host.Calls, call => call.Operation is "Edits" or "BlockedBy" && call.Argument == "30");
    }

    [Fact]
    public async Task WithoutALinkNoCardIsTheOwners()
    {
        await using var reading = await Reading.Reading.Start();
        var project = await reading.Project(Reading.Reading.Alice);
        Stage(reading, Reading.Reading.Alice, [Card(40)]);

        await Read(reading, project);

        var card = Assert.Single(await Cards(reading, project));
        Assert.Equal("ignored", card.State);
        Assert.Equal("Dev flow does not know your GitHub login: link GitHub on the Projects tab", card.Reason);
    }

    [Fact]
    public async Task AHeldCardWaits()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading, Reading.Reading.Alice, [Card(50, labels: ["devflow", "DevFlow-Hold"])]);

        await Read(reading, project);

        var held = Assert.Single(await Cards(reading, project));
        Assert.Equal("held", held.State);
        Assert.Equal("Held by the label devflow-hold", held.Reason);
        Assert.NotNull(held.PickedBodySha256);
        Assert.Empty(await CardStore.Queue(reading.Door, Reading.Reading.Alice, project, Ct));

        Stage(reading, Reading.Reading.Alice, [Card(50)]);
        await Read(reading, project);

        var queued = Assert.Single(await Cards(reading, project));
        Assert.Equal("queued", queued.State);
        Assert.Null(queued.Reason);
        Assert.Equal([50], await CardStore.Queue(reading.Door, Reading.Reading.Alice, project, Ct));
    }

    [Fact]
    public async Task ABlockedCardWaitsAndNamesWhatItWaitsFor()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        // 60 waits for 61, open and not a card, and 62, already closed; 63 waits for two open ones.
        Stage(reading, Reading.Reading.Alice,
        [
            Card(60, blockedBy: [61, 62]),
            Card(61, labels: []),
            Card(62, open: false),
            Card(63, blockedBy: [61, 60]),
        ]);

        await Read(reading, project);

        var cards = await Cards(reading, project);
        Assert.Equal([(60, "queued", "Waits for #61"), (63, "queued", "Waits for #60 and #61")], cards.Select(card => (card.Number, card.State, card.Reason)));
        Assert.Equal(["#61"], cards[0].WaitsFor);
        Assert.Equal(["#60", "#61"], cards[1].WaitsFor);
        Assert.Empty(await CardStore.Queue(reading.Door, Reading.Reading.Alice, project, Ct));

        // 61 is closed: 60 is free; 63 still waits for 60.
        Stage(reading, Reading.Reading.Alice,
        [
            Card(60, blockedBy: [61, 62]),
            Card(61, labels: [], open: false),
            Card(62, open: false),
            Card(63, blockedBy: [61, 60]),
        ]);
        await Read(reading, project);

        cards = await Cards(reading, project);
        Assert.Equal([(60, "queued", (string?)null), (63, "queued", "Waits for #60")], cards.Select(card => (card.Number, card.State, card.Reason)));
        Assert.Equal([60], await CardStore.Queue(reading.Door, Reading.Reading.Alice, project, Ct));
    }

    [Fact]
    public async Task TakingTheLabelOffTakesTheCardOut()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading, Reading.Reading.Alice, [Card(70), Card(71), Card(72)]);
        await Read(reading, project);
        // 71 is being worked on, and 72 has finished, as later parts will set them.
        await SetState(reading, project, 71, "building");
        await SetState(reading, project, 72, "done");

        Stage(reading, Reading.Reading.Alice, [Card(70, labels: []), Card(71, labels: []), Card(72, labels: [])]);
        await Read(reading, project);

        // The queued one is gone, and so is the finished one; the one being worked on finishes
        // first, marked as having lost its label.
        var left = Assert.Single(await Cards(reading, project));
        Assert.Equal((71, "building", true), (left.Number, left.State, left.Unlabelled));

        // The label put back, it is the owner's card again.
        Stage(reading, Reading.Reading.Alice, [Card(71)]);
        await Read(reading, project);
        left = Assert.Single(await Cards(reading, project));
        Assert.Equal((71, "building", false), (left.Number, left.State, left.Unlabelled));
    }

    [Fact]
    public async Task AChangedCardIsReadAgain()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading, Reading.Reading.Alice, [Card(80), Card(81)]);
        await Read(reading, project);
        await SetState(reading, project, 80, "building");
        await SetState(reading, project, 81, "building");

        // The owner edits 80: its record is replaced and it stays where it is. Someone else edits
        // 81: it is ignored, and keeps the record a run on it is checked against.
        Stage(reading, Reading.Reading.Alice,
        [
            Card(80, body: "Build it differently.", edits: [new("UCE_80", Owner, At.AddHours(1), EditKind.Body)]),
            Card(81, body: "Do something else.", edits: [new("UCE_81", Stranger, At.AddHours(1), EditKind.Body)]),
        ]);
        await Read(reading, project);

        var cards = await Cards(reading, project);
        Assert.Equal(("building", (string?)null), (cards[0].State, cards[0].Reason));
        Assert.Equal(Sha("Build it differently."), cards[0].PickedBodySha256);
        Assert.Equal("UCE_80", cards[0].PickedEdit);
        Assert.Equal(("ignored", $"Edited by {Stranger}, who is not a trusted account of this project"), (cards[1].State, cards[1].Reason));
        Assert.Equal(Sha("Build the thing of card 81."), cards[1].PickedBodySha256);
        Assert.Null(cards[1].PickedEdit);
    }

    [Fact]
    public async Task TheOrderIsPriorityThenOldest()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        Stage(reading, Reading.Reading.Alice,
        [
            Card(90, labels: ["devflow", "priority: low"], created: At.AddDays(-9)),
            Card(91, labels: ["devflow"], created: At.AddDays(-1)),
            Card(92, labels: ["devflow", "Priority: High"], created: At),
            Card(93, labels: ["devflow"], created: At.AddDays(-2)),
            Card(94, labels: ["devflow", "priority: high"], created: At.AddDays(-3)),
            Card(95, labels: ["devflow", "priority: low"], created: At.AddDays(-10)),
        ]);

        await Read(reading, project);

        Assert.Equal([94, 92, 93, 91, 95, 90], await CardStore.Queue(reading.Door, Reading.Reading.Alice, project, Ct));
        Assert.Equal(["low", "none", "high", "none", "high", "low"], (await Cards(reading, project)).Select(card => card.Priority));
    }

    [Fact]
    public async Task OneProjectsFailureDoesNotStopAnother()
    {
        var (reading, failing) = await Start();
        await using var _ = reading;
        var working = await reading.Project(Reading.Reading.Alice, repository: 9002, name: "repository-9002");
        Stage(reading, Reading.Reading.Alice, [Card(100)]);
        Stage(reading, Reading.Reading.Alice, [Card(200)], repository: 9002);
        await Read(reading, failing);
        reading.Host.Throws[Reading.Reading.RepositoryId] = true;
        Stage(reading, Reading.Reading.Alice, [Card(100), Card(101)]);

        var refresher = new ProjectRefresher(reading.Reader, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectRefresher>.Instance);
        await using (refresher)
        {
            await Task.WhenAll(refresher.Request(failing, Reading.Reading.Alice), refresher.Request(working, Reading.Reading.Alice));
        }

        // The failing one keeps its cards as they were, and says why; the other is read.
        Assert.Equal([100], (await Cards(reading, failing)).Select(card => card.Number));
        Assert.Equal(ProjectReader.ReadFailed, (await reading.ReadOf(failing))!.LastError);
        Assert.Equal([200], (await Cards(reading, working)).Select(card => card.Number));
        Assert.Null((await reading.ReadOf(working))!.LastError);
    }

    [Fact]
    public async Task AProjectsOwnLabelsAreRead()
    {
        var (reading, project) = await Start();
        await using var _ = reading;
        await using (var admin = await reading.Database.OpenAdmin(Ct))
        {
            await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE project_settings SET pickup_label = 'agent: go', hold_label = 'agent: wait' WHERE project_id = @project",
                new { project },
                cancellationToken: Ct));
        }

        Stage(reading, Reading.Reading.Alice, [Card(110, labels: ["agent: go"]), Card(111, labels: ["agent: go", "agent: wait"]), Card(112)]);

        await Read(reading, project);

        Assert.Equal([(110, "queued"), (111, "held")], (await Cards(reading, project)).Select(card => (card.Number, card.State)));
        Assert.Contains(reading.Host.Calls, call => call.Operation == "LabelledIssues" && call.Argument == "agent: go");
    }

    /// <summary>A stored card, set by property: Dapper reads <c>text[]</c> as an array no
    /// constructor parameter matches.</summary>
    internal sealed class CardRow
    {
        public Guid UserId { get; init; }

        public int Number { get; init; }

        public string Title { get; init; } = "";

        public string WebUrl { get; init; } = "";

        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public string[] WaitsFor { get; init; } = [];

        public string Priority { get; init; } = "";

        public DateTime IssueCreatedAt { get; init; }

        public byte[]? PickedBodySha256 { get; init; }

        public string? PickedTitle { get; init; }

        public string? PickedEdit { get; init; }

        public bool Unlabelled { get; init; }
    }
}