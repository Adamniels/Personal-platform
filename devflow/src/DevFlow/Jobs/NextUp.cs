using System.Globalization;

using Dapper;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>Where a card's reason on "Next up" links, the place it is acted on.</summary>
public static class NextLink
{
    /// <summary>The page of the card it waits for, <see cref="NextCard.LinkCard"/>.</summary>
    public const string Card = "card";

    /// <summary>"Waiting for you".</summary>
    public const string WaitingForYou = "waiting_for_you";

    /// <summary>The card's project's page.</summary>
    public const string ProjectPage = "project_page";

    /// <summary>Dev flow's settings, its Connections, where the Claude login is given.</summary>
    public const string Settings = "settings";

    /// <summary>The card's project's schedules, "When it runs" on its settings page.</summary>
    public const string Schedules = "schedules";

    /// <summary>The card's project's daily budget, "Usage" on its settings page.</summary>
    public const string Budget = "budget";
}

/// <summary>A card that comes next, as "Next up" on "Running now" shows it.</summary>
/// <param name="ProjectId">Its project.</param>
/// <param name="ProjectName">Its project's repository's name.</param>
/// <param name="Number">Its number, which its page is under.</param>
/// <param name="Title">Its title as last read.</param>
/// <param name="State">Its state: <c>queued</c>, <c>reviewing</c> or <c>fixing</c>.</param>
/// <param name="Job">The kind of job due for it, one of <see cref="Protocol.Job"/>'s, null for a
/// card that waits for another with none due.</param>
/// <param name="Reason">The sentence of the <see cref="WaitReason"/> <see cref="RunDecision"/> gives
/// it, or null for a card that runs as soon as a worker asks.</param>
/// <param name="Link">Where that reason is acted on, one of <see cref="NextLink"/>'s, or null where
/// it has no place to act.</param>
/// <param name="LinkCard">The card it waits for, with <see cref="NextLink.Card"/>.</param>
/// <param name="Feature">The feature it is a card of, by its parent's number, null for a card on
/// its own.</param>
/// <param name="RunNow">Whether "Run now anyway" is what to do about it: it waits only for its
/// project's schedules, <see cref="WaitReason.OutsideSchedule"/>, so running its feature, or it
/// when it is on its own, outside them would run it.</param>
public sealed record NextCard(Guid ProjectId, string ProjectName, int Number, string Title, string State, string? Job, string? Reason, string? Link, int? LinkCard, int? Feature, bool RunNow);

/// <summary>The cards that come next, the first <see cref="NextUp.Most"/>, and how many more.</summary>
public sealed record NextUpList(IReadOnlyList<NextCard> Cards, int More);

/// <summary>
/// "Next up" on "Running now": every card of a user's projects that Dev flow will run without the
/// owner first, queued, waiting to be read, or waiting for its review or its fix round, with no run
/// under way, in the order work starts, each with its reason from the one rule,
/// <see cref="RunDecision"/>, so the page and the dispatcher never disagree. See "The pages" and
/// "Jobs" in devflow/README.md.
/// </summary>
public static class NextUp
{
    /// <summary>The most cards listed; the rest are counted.</summary>
    public const int Most = 50;

    /// <summary>
    /// The cards that come next for <paramref name="userId"/> at <paramref name="now"/>, with
    /// <paramref name="image"/> the base image released, which a worker's rebuild is judged by.
    /// Each project's are its decision's cards as <see cref="RunDecision.Cards"/> orders them, the
    /// order the dispatcher hands them out; across projects, whichever project's next card has the
    /// higher priority label goes first, and of the same priority the one written first, as
    /// <see cref="Cards.CardStore.QueueOrder"/> orders a project's, so the list reads top to bottom
    /// as the order work starts.
    /// </summary>
    public static async Task<NextUpList> For(UserDb database, Guid userId, string image, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var projects = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM projects ORDER BY created_at, id",
            transaction: transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        var queues = new List<Queue<Entry>>();
        foreach (var projectId in projects)
        {
            if (await RunDecision.ReadForPage(database, userId, projectId, image, now, cancellationToken) is not { } situation)
            {
                continue;
            }

            var decisions = await RunDecision.Cards(database, userId, projectId, situation, cancellationToken);
            if (decisions.Count > 0)
            {
                queues.Add(await Queue(database, userId, projectId, situation.Project, decisions, cancellationToken));
            }
        }

        var merged = new List<NextCard>();
        var total = queues.Sum(queue => queue.Count);
        while (merged.Count < Most && queues.FindIndex(queue => queue.Count > 0) >= 0)
        {
            // The project listed first wins a tie, so the order is the same each time it is read.
            var first = queues
                .Where(queue => queue.Count > 0)
                .MinBy(queue => (queue.Peek().Rank, queue.Peek().Written))!;
            merged.Add(first.Dequeue().Card);
        }

        return new NextUpList(merged, total - merged.Count);
    }

    /// <summary>The project's cards next, in the order of <paramref name="decisions"/>, with what the
    /// list shows of each, read in one query as the user; a card gone since the decisions were read
    /// is left out.</summary>
    private static async Task<Queue<Entry>> Queue(UserDb database, Guid userId, Guid projectId, string projectName, IReadOnlyList<CardDecision> decisions, CancellationToken cancellationToken)
    {
        int[] numbers = [.. decisions.Select(each => each.Number), .. decisions.Select(each => WaitedFor(each.Decision)).OfType<int>()];
        var rows = (await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT cards.number AS Number, cards.title AS Title, cards.state AS State, cards.priority AS Priority,
                   cards.issue_created_at AS IssueCreatedAt, f.number AS Feature
            FROM cards LEFT JOIN features f ON f.project_id = cards.project_id AND f.id = cards.feature_id
            WHERE cards.project_id = @projectId AND cards.number = ANY(@numbers)
            """,
            new { projectId, numbers },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)).ToDictionary(row => row.Number);
        var queue = new Queue<Entry>();
        foreach (var decision in decisions)
        {
            if (!rows.TryGetValue(decision.Number, out var row))
            {
                continue;
            }

            var (reason, link, linkCard) = Shown(decision.Decision, rows);
            queue.Enqueue(new Entry(
                new NextCard(projectId, projectName, row.Number, row.Title, row.State, decision.Kind, reason, link, linkCard, row.Feature,
                    decision.Decision is Decision.Wait { Reason: WaitReason.OutsideSchedule }),
                Rank(row.Priority),
                row.IssueCreatedAt));
        }

        return queue;
    }

    /// <summary>A decision's sentence and where it is acted on: the card it waits for when that is a
    /// card of the project, in <paramref name="cards"/>; nothing for a card that runs.</summary>
    private static (string? Reason, string? Link, int? LinkCard) Shown(Decision decision, IReadOnlyDictionary<int, Row> cards) => decision switch
    {
        Decision.Wait { Reason: WaitReason.WaitsForCard waits } when WaitedFor(decision) is { } number && cards.ContainsKey(number) =>
            (waits.Sentence, NextLink.Card, number),
        Decision.Wait { Reason: var reason } => (reason.Sentence, reason.Where switch
        {
            ActedOn.ProjectPage => NextLink.ProjectPage,
            ActedOn.Settings => NextLink.Settings,
            ActedOn.WaitingForYou => NextLink.WaitingForYou,
            ActedOn.Schedules => NextLink.Schedules,
            ActedOn.Budget => NextLink.Budget,
            _ => null,
        }, null),
        _ => (null, null, null),
    };

    /// <summary>The number of the issue in the card's own repository a card waits for, <c>#n</c>,
    /// or null for one in another repository or a decision of another kind.</summary>
    private static int? WaitedFor(Decision decision) =>
        decision is Decision.Wait { Reason: WaitReason.WaitsForCard { Issue: ['#', .. var digits] } }
        && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>A priority's place, as <see cref="Cards.CardStore.QueueOrder"/> ranks it.</summary>
    private static int Rank(string priority) => priority switch
    {
        "high" => 0,
        "none" => 1,
        _ => 2,
    };

    private sealed record Entry(NextCard Card, int Rank, DateTime Written);

    private sealed record Row(int Number, string Title, string State, string Priority, DateTime IssueCreatedAt, int? Feature);
}