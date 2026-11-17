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
            SELECT number AS Number, state AS State, reason AS Reason, waits_for AS WaitsFor, unlabelled AS Unlabelled
            FROM cards WHERE project_id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken))).ToDictionary(card => card.Number);

        var numbers = found.Select(card => card.Number).ToHashSet();
        foreach (var gone in stored.Values.Where(card => !numbers.Contains(card.Number)))
        {
            var command = Running.Contains(gone.State, StringComparer.Ordinal)
                ? (gone.Unlabelled ? null : "UPDATE cards SET unlabelled = true, changed_at = @now WHERE project_id = @projectId AND number = @number")
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
                                   picked_body_sha256, picked_title, picked_edit, unlabelled, changed_at, read_at)
                VALUES (@id, @projectId, @userId, @number, @title, @webUrl, @state, @reason, @waitsFor, @priority, @createdAt,
                        @bodySha, @pickedTitle, @pickedEdit, false, @now, @now)
                ON CONFLICT (project_id, number) DO UPDATE
                SET title = EXCLUDED.title, web_url = EXCLUDED.web_url, state = EXCLUDED.state, reason = EXCLUDED.reason,
                    waits_for = EXCLUDED.waits_for, priority = EXCLUDED.priority, issue_created_at = EXCLUDED.issue_created_at,
                    picked_body_sha256 = CASE WHEN @picked THEN EXCLUDED.picked_body_sha256 ELSE cards.picked_body_sha256 END,
                    picked_title = CASE WHEN @picked THEN EXCLUDED.picked_title ELSE cards.picked_title END,
                    picked_edit = CASE WHEN @picked THEN EXCLUDED.picked_edit ELSE cards.picked_edit END,
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
                    changed,
                    now,
                },
                transaction,
                cancellationToken: cancellationToken));
        }
    }

    /// <summary>The numbers of the project's cards that are queued and free to start, in the order
    /// they are handed out: <c>priority: high</c>, then no priority, then <c>priority: low</c>,
    /// each oldest first by when the issue was written.</summary>
    public static async Task<IReadOnlyList<int>> Queue(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<int>(new CommandDefinition(
            """
            SELECT number FROM cards
            WHERE project_id = @projectId AND state = 'queued' AND cardinality(waits_for) = 0
            ORDER BY CASE priority WHEN 'high' THEN 0 WHEN 'none' THEN 1 ELSE 2 END, issue_created_at, number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)];
    }

    private static string State(Verdict verdict) => verdict switch
    {
        Verdict.Queued => "queued",
        Verdict.Held => "held",
        Verdict.Ignored => "ignored",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "not a verdict"),
    };

    /// <summary>A card as stored, set by property: Dapper reads <c>text[]</c> as an array no
    /// constructor parameter matches.</summary>
    private sealed class StoredCard
    {
        public int Number { get; init; }

        public string State { get; init; } = "";

        public string? Reason { get; init; }

        public string[] WaitsFor { get; init; } = [];

        public bool Unlabelled { get; init; }
    }
}