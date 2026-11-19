using Dapper;

using Npgsql;

using PlatformData;

namespace DevFlow.Cards;

/// <summary>
/// A project's cards in the database, through the door as the project's user: what a read found
/// merged into them, and the order Queued cards are handed out in. See "Cards" in
/// devflow/README.md and devflow/docs/devflow-architecture.md.
/// </summary>
public static class CardStore
{
    /// <summary>The states of a card that has not started, which picking sets.</summary>
    private static readonly string[] NotStarted = ["queued", "held", "ignored"];

    /// <summary>The states of a card whose run is under way, which finishes before its card is
    /// removed.</summary>
    private static readonly string[] Running = ["building", "checking", "reviewing", "fixing", "waiting_for_owner"];

    /// <summary>
    /// Merges what a read found into the project's cards, inside the read's transaction:
    /// <list type="bullet">
    /// <item>A card not found any more is deleted, unless its run is under way, when it is marked
    /// unlabelled and finishes first.</item>
    /// <item>A new card is inserted where picking put it.</item>
    /// <item>A card that has not started is put where picking put it now.</item>
    /// <item>A card that has started stays where it is, unless picking ignores it now, when it
    /// is ignored with the reason; its record is replaced when the owner changed it.</item>
    /// </list>
    /// An ignored card keeps the record it had, null when it was never picked, so a run on it is
    /// still checked against what was picked. <c>changed_at</c> moves only when the state, the
    /// reason or what it waits for does.
    /// </summary>
    public static async Task Store(NpgsqlTransaction transaction, Guid projectId, Guid userId, IReadOnlyList<PickedCard> found, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(found);
        var stored = (await transaction.Connection!.QueryAsync<StoredCard>(new CommandDefinition(
            """
            SELECT number AS Number, state AS State, reason AS Reason, waits_for AS WaitsFor, unlabelled AS Unlabelled,
                   pull_request_number AS PullRequestNumber
            FROM cards WHERE project_id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken))).ToDictionary(card => card.Number);

        var numbers = found.Select(card => card.Number).ToHashSet();
        foreach (var gone in stored.Values.Where(card => !numbers.Contains(card.Number)))
        {
            // A card Dev flow carried to a merged pull request is kept: the pull request's Closes
            // line closed its issue as it merged, which takes it off the list, and the card is the
            // story of what was built.
            var command = Running.Contains(gone.State, StringComparer.Ordinal)
                ? (gone.Unlabelled ? null : "UPDATE cards SET unlabelled = true, changed_at = @now WHERE project_id = @projectId AND number = @number")
                : gone.State == "done" && gone.PullRequestNumber is not null ? null
                : "DELETE FROM cards WHERE project_id = @projectId AND number = @number";
            if (command is not null)
            {
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(command, new { projectId, number = gone.Number, now }, transaction, cancellationToken: cancellationToken));
            }
        }

        foreach (var card in found)
        {
            var before = stored.GetValueOrDefault(card.Number);
            var started = before is not null && !NotStarted.Contains(before.State, StringComparer.Ordinal);
            var (state, reason, waitsFor) = started && card.Verdict != Verdict.Ignored
                ? (before!.State, before.Reason, before.WaitsFor)
                : (State(card.Verdict), card.Reason, card.WaitsFor.ToArray());
            var changed = before is null
                || before.State != state
                || before.Reason != reason
                || !before.WaitsFor.SequenceEqual(waitsFor, StringComparer.Ordinal)
                || before.Unlabelled;
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO cards (id, project_id, user_id, number, title, web_url, state, reason, waits_for, priority, issue_created_at,
                                   picked_body_sha256, picked_title, picked_edit, picked_body, unlabelled, changed_at, read_at)
                VALUES (@id, @projectId, @userId, @number, @title, @webUrl, @state, @reason, @waitsFor, @priority, @createdAt,
                        @bodySha, @pickedTitle, @pickedEdit, @pickedBody, false, @now, @now)
                ON CONFLICT (project_id, number) DO UPDATE
                SET title = EXCLUDED.title, web_url = EXCLUDED.web_url, state = EXCLUDED.state, reason = EXCLUDED.reason,
                    waits_for = EXCLUDED.waits_for, priority = EXCLUDED.priority, issue_created_at = EXCLUDED.issue_created_at,
                    picked_body_sha256 = CASE WHEN @picked THEN EXCLUDED.picked_body_sha256 ELSE cards.picked_body_sha256 END,
                    picked_title = CASE WHEN @picked THEN EXCLUDED.picked_title ELSE cards.picked_title END,
                    picked_edit = CASE WHEN @picked THEN EXCLUDED.picked_edit ELSE cards.picked_edit END,
                    picked_body = CASE WHEN @picked THEN EXCLUDED.picked_body ELSE cards.picked_body END,
                    unlabelled = false,
                    changed_at = CASE WHEN @changed THEN EXCLUDED.changed_at ELSE cards.changed_at END,
                    read_at = EXCLUDED.read_at
                """,
                new
                {
                    id = Guid.CreateVersion7(now),
                    projectId,
                    userId,
                    number = card.Number,
                    title = card.Title,
                    webUrl = card.Url.ToString(),
                    state,
                    reason,
                    waitsFor,
                    priority = card.Priority,
                    createdAt = card.CreatedAt,
                    picked = card.Record is not null,
                    bodySha = card.Record?.BodySha256,
                    pickedTitle = card.Record?.Title,
                    pickedEdit = card.Record?.NewestEdit,
                    pickedBody = card.Record?.Body,
                    changed,
                    now,
                },
                transaction,
                cancellationToken: cancellationToken));
        }
    }

    /// <summary>The order queued cards free to start are handed out in, as SQL over <c>cards</c>:
    /// <c>priority: high</c>, then no priority, then <c>priority: low</c>, each oldest first by when
    /// the issue was written. Handing out a job takes the first, see
    /// <see cref="Jobs.JobStore.Claim"/>.</summary>
    public const string QueueOrder = "CASE priority WHEN 'high' THEN 0 WHEN 'none' THEN 1 ELSE 2 END, issue_created_at, number";

    /// <summary>The numbers of the project's cards that are queued and free to start, in the order
    /// they are handed out, <see cref="QueueOrder"/>.</summary>
    public static async Task<IReadOnlyList<int>> Queue(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<int>(new CommandDefinition(
            $"""
            SELECT number FROM cards
            WHERE project_id = @projectId AND state = 'queued' AND cardinality(waits_for) = 0
            ORDER BY {QueueOrder}
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)];
    }

    /// <summary>The project's cards as last read, newest change first, or null when the user has
    /// no such project.</summary>
    public static async Task<CardList?> List(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction<CardList?>(userId, async transaction =>
        {
            if (await transaction.Connection!.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT 1 FROM projects WHERE id = @projectId", new { projectId }, transaction, cancellationToken: cancellationToken)) is null)
            {
                return null;
            }

            var rows = await transaction.Connection!.QueryAsync<ListedCard>(new CommandDefinition(
                """
                SELECT number AS Number, title AS Title, web_url AS WebUrl, state AS State, reason AS Reason, waits_for AS WaitsFor,
                       priority AS Priority, changed_at AS ChangedAt
                FROM cards WHERE project_id = @projectId
                ORDER BY changed_at DESC, number DESC
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            return new CardList([.. rows.Select(row => new CardView(
                row.Number, row.Title, row.WebUrl, row.State, row.Reason, row.WaitsFor, row.Priority,
                new DateTimeOffset(DateTime.SpecifyKind(row.ChangedAt, DateTimeKind.Utc))))]);
        }, cancellationToken);
    }

    private static string State(Verdict verdict) => verdict switch
    {
        Verdict.Queued => "queued",
        Verdict.Held => "held",
        Verdict.Ignored => "ignored",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "not a verdict"),
    };

    /// <summary>Why a card whose pull request was closed without merging is stopped.</summary>
    public const string PullRequestClosed = "The pull request was closed";

    /// <summary>The project's cards with a pull request that is followed, each with its number:
    /// those that wait for the owner, and those under review or being fixed, read as the project's
    /// user.</summary>
    public static async Task<IReadOnlyList<(int Card, int PullRequest)>> FollowedPulls(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<(int, int)>(new CommandDefinition(
            """
            SELECT number, pull_request_number FROM cards
            WHERE project_id = @projectId AND state IN ('waiting_for_owner', 'reviewing', 'fixing') AND pull_request_number IS NOT NULL
            ORDER BY number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows];
    }

    /// <summary>
    /// Moves a card that waits for the owner, or is under review or being fixed, on as its pull
    /// request went,
    /// inside the read's transaction: merged, by anyone, is <c>done</c>; closed without merging is
    /// <c>stopped</c>, <see cref="PullRequestClosed"/>, or removed when its label was taken off while
    /// it ran, as a run that ends removes such a card. Only while it still waits, or is under
    /// review or being fixed, with that pull request; a review or fix still running then ends
    /// without moving it.
    /// </summary>
    public static async Task Follow(NpgsqlTransaction transaction, Guid projectId, FollowedPull pull, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(pull);
        const string Where = "project_id = @projectId AND number = @card AND state IN ('waiting_for_owner', 'reviewing', 'fixing') AND pull_request_number = @pullRequest";
        var parameters = new { projectId, card = pull.Card, pullRequest = pull.PullRequest, reason = PullRequestClosed, now };
        if (pull.Merged)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                $"UPDATE cards SET state = 'done', reason = NULL, changed_at = @now WHERE {Where}",
                parameters,
                transaction,
                cancellationToken: cancellationToken));
            return;
        }

        if (await transaction.Connection!.ExecuteAsync(new CommandDefinition($"DELETE FROM cards WHERE {Where} AND unlabelled", parameters, transaction, cancellationToken: cancellationToken)) > 0)
        {
            return;
        }

        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"UPDATE cards SET state = 'stopped', reason = @reason, changed_at = @now, stopped_at = @now, stop_marked_at = NULL WHERE {Where}",
            parameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>A card as listed, set by property, as <see cref="StoredCard"/> is.</summary>
    private sealed class ListedCard
    {
        public int Number { get; init; }

        public string Title { get; init; } = "";

        public string WebUrl { get; init; } = "";

        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public string[] WaitsFor { get; init; } = [];

        public string Priority { get; init; } = "";

        public DateTime ChangedAt { get; init; }
    }

    /// <summary>A card as stored, set by property: Dapper reads <c>text[]</c> as an array no
    /// constructor parameter matches.</summary>
    private sealed class StoredCard
    {
        public int Number { get; init; }

        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public string[] WaitsFor { get; init; } = [];

        public bool Unlabelled { get; init; }

        public int? PullRequestNumber { get; init; }
    }
}

/// <summary>Where a card's pull request went, as a read of the project found it: merged, or closed
/// without merging.</summary>
/// <param name="Card">The card's issue number.</param>
/// <param name="PullRequest">Its pull request's number.</param>
/// <param name="Merged">Whether it merged, rather than closed.</param>
public sealed record FollowedPull(int Card, int PullRequest, bool Merged);