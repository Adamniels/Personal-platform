using Dapper;

using DevFlow.CodeHost;
using DevFlow.Reading;

namespace DevFlow.Tests.Cards;

/// <summary>
/// Picking cards by a project's trusted accounts: a card, a feature's parent or a sub-issue written
/// by any of them is picked, an edit by any of them keeps it, and anything written or edited by a
/// login not on the list is ignored, saying who and that it is not trusted. Against the fake and a
/// real Postgres. See "Cards" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class CardPickerTests
{
    private const string Owner = "octo-owner";
    private const string Friend = "octo-friend";
    private const string Stranger = "octo-stranger";

    private static readonly DateTimeOffset At = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => Reading.Reading.Ct;

    private static FakeIssue Card(int number, string author, IReadOnlyList<IssueEdit>? edits = null, IReadOnlyList<string>? labels = null, IReadOnlyList<FakeSubIssue>? subIssues = null) => new(
        number,
        $"Card {number}",
        author,
        labels ?? ["devflow"],
        $"Build the thing of card {number}.",
        new Uri($"https://github.com/Adamniels/Personal-platform/issues/{number}"),
        At.AddMinutes(number),
        true,
        [],
        edits ?? [],
        subIssues);

    private static void Stage(Reading.Reading reading, params FakeIssue[] issues) =>
        reading.Fake.Replace(Reading.Reading.Alice, new FakeWorld([
            new FakeInstallation(Reading.Reading.Installation, "Adamniels",
                [new FakeRepository(Reading.Reading.RepositoryId, "Adamniels", "Personal-platform", [], [], [], [], issues)]),
        ]));

    /// <summary>A project of Alice's, linked as <see cref="Owner"/>, that trusts
    /// <paramref name="trusted"/> beside him, stored as the settings route stores them.</summary>
    private static async Task<(Reading.Reading Reading, Guid Project)> Start(params string[] trusted)
    {
        var reading = await Reading.Reading.Start();
        var project = await reading.Project(Reading.Reading.Alice);
        await reading.LinkOwner(Reading.Reading.Alice, Owner);
        await Trust(reading, project, trusted);
        return (reading, project);
    }

    private static async Task Trust(Reading.Reading reading, Guid project, params string[] trusted)
    {
        await using var admin = await reading.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE project_settings SET trusted_logins = @trusted WHERE project_id = @project", new { project, trusted }, cancellationToken: Ct));
    }

    private static async Task Read(Reading.Reading reading, Guid project) =>
        Assert.Equal(ReadResult.Read, await reading.Reader.Read(project, Reading.Reading.Alice, Ct));

    /// <summary>Each stored card's state, reason and the edit its record names, by number.</summary>
    private static async Task<Dictionary<int, (string State, string? Reason, string? PickedEdit)>> Cards(Reading.Reading reading, Guid project)
    {
        await using var admin = await reading.Database.OpenAdmin(Ct);
        var rows = await admin.QueryAsync<(int Number, string State, string? Reason, string? PickedEdit)>(new CommandDefinition(
            "SELECT number, state, reason, picked_edit FROM cards WHERE project_id = @project ORDER BY number", new { project }, cancellationToken: Ct));
        return rows.ToDictionary(row => row.Number, row => (row.State, row.Reason, row.PickedEdit));
    }

    private static async Task SetState(Reading.Reading reading, Guid project, int number, string state)
    {
        await using var admin = await reading.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET state = @state WHERE project_id = @project AND number = @number", new { project, number, state }, cancellationToken: Ct));
    }

    [Fact]
    public async Task ACardByATrustedAccountIsPicked()
    {
        // Stored in another case than GitHub answers it: logins are one account whatever their case.
        var (reading, project) = await Start("Octo-Friend");
        await using var _ = reading;
        Stage(reading,
            Card(12, Friend),
            // A feature whose parent and one of whose cards a trusted account wrote.
            Card(20, Friend, subIssues: [new FakeSubIssue(21, null), new FakeSubIssue(22, null)]),
            Card(21, Owner, labels: []),
            Card(22, Friend, labels: []));

        await Read(reading, project);

        var cards = await Cards(reading, project);
        Assert.Equal(("queued", null), (cards[12].State, cards[12].Reason));
        Assert.Equal(("queued", null), (cards[21].State, cards[21].Reason));
        Assert.Equal(("queued", null), (cards[22].State, cards[22].Reason));
    }

    [Fact]
    public async Task AnEditByATrustedAccountKeepsTheCard()
    {
        var (reading, project) = await Start(Friend);
        await using var _ = reading;
        Stage(reading,
            Card(12, Owner, edits: [new IssueEdit("UCE_friend", Friend, At.AddHours(1), EditKind.Body)]),
            Card(13, Owner, edits: [new IssueEdit("UCE_friend", Friend, At.AddHours(1), EditKind.Body), new IssueEdit("RTE_stranger", Stranger, At.AddHours(2), EditKind.Title)]),
            Card(14, Owner));

        await Read(reading, project);

        var cards = await Cards(reading, project);
        Assert.Equal(("queued", null, "UCE_friend"), cards[12]);
        Assert.Equal(("ignored", $"Edited by {Stranger}, who is not a trusted account of this project"), (cards[13].State, cards[13].Reason));

        // A card under way that a trusted account edits keeps its state, its record replaced.
        await SetState(reading, project, 14, "building");
        Stage(reading,
            Card(12, Owner, edits: [new IssueEdit("UCE_friend", Friend, At.AddHours(1), EditKind.Body)]),
            Card(13, Owner),
            Card(14, Owner, edits: [new IssueEdit("UCE_later", Friend, At.AddHours(3), EditKind.Body)]));

        await Read(reading, project);

        Assert.Equal(("building", null, "UCE_later"), (await Cards(reading, project))[14]);
    }

    [Fact]
    public async Task AStrangersCardSaysItIsNotTrusted()
    {
        var (reading, project) = await Start(Friend);
        await using var _ = reading;
        Stage(reading, Card(12, Stranger), Card(13, Friend));

        await Read(reading, project);

        var cards = await Cards(reading, project);
        Assert.Equal(("ignored", $"{Stranger} is not a trusted account of this project"), (cards[12].State, cards[12].Reason));
        Assert.Equal("queued", cards[13].State);

        // Taken off the list, the friend's card is no longer picked.
        await Trust(reading, project);

        await Read(reading, project);

        Assert.Equal(("ignored", $"{Friend} is not a trusted account of this project"), ((await Cards(reading, project))[13].State, (await Cards(reading, project))[13].Reason));
    }
}