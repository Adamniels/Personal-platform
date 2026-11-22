using System.Text.Json;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Projects;

using Npgsql;

using PlatformData;

namespace DevFlow.Readings;

/// <summary>A card not yet started, read for what reads it again: its number, the newest comment
/// already taken, when its newest reading came back, and the comments Dev flow keeps on its
/// readings, its own and the owner's answers posted from the page.</summary>
/// <param name="ReadThrough">The id of the newest comment taken, null before any was.</param>
/// <param name="ReadAt">When its newest reading came back; only a comment newer counts.</param>
/// <param name="OwnComments">The ids of the comments Dev flow posted for its readings, which never
/// read it again.</param>
public sealed record UnstartedCard(int Number, long? ReadThrough, DateTimeOffset ReadAt, IReadOnlyList<long> OwnComments);

/// <summary>A comment that asks for a card to be read again, as the reader's brief quotes it.</summary>
/// <param name="Login">The trusted account that wrote it, as GitHub names it.</param>
/// <param name="Body">Its text, exactly as written.</param>
public sealed record ReadAgainComment(string Login, string Body);

/// <summary>The trusted accounts' new comments on a card not yet started, oldest first, as a read
/// found them, to be taken in its transaction.</summary>
/// <param name="Through">The id of the newest of them.</param>
public sealed record ReadAgainComments(int Card, IReadOnlyList<ReadAgainComment> Comments, long Through);

/// <summary>
/// What reads a card again before it starts, while it is <c>queued</c>, <c>needs_answers</c> or
/// <c>refused</c>: a trusted account's comment on its issue newer than its newest reading, or an
/// edit of its body or title since that reading, which picking records whenever every editor is
/// trusted, and one edited by anyone else is ignored. A card that waits for the owner, for his
/// answers or refused, is queued, and a queued card so changed is due a reading of it alone,
/// <see cref="ReadingStore.ReadAgain"/>, with the comments in the reader's brief. Dev flow's own
/// comments never read it again, by their stored ids, and nor does anything once the card started,
/// <see cref="ReadingStore.Started"/>. See "Reading a card" under "Jobs" in devflow/README.md.
/// </summary>
public static class ReadingAgain
{
    /// <summary>What a card that waited for the owner says, once his edit queued it, until it is
    /// read again.</summary>
    public const string Edited = "You edited it; it is read again next";

    /// <summary>What a card that waited for the owner says, once a trusted account's comment
    /// queued it, until it is read again.</summary>
    public const string Commented = "You commented on it; it is read again next";

    /// <summary>The states a card is read again from, before it starts.</summary>
    private const string BeforeItStarts = "('queued', 'needs_answers', 'refused')";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The project's cards that a comment could read again: not started, in a state before
    /// it starts, read, with no run under way, read as the project's user, by number.</summary>
    public static async Task<IReadOnlyList<UnstartedCard>> Cards(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT cards.number AS Number, cards.comments_read_through AS ReadThrough, cr.read_at AS ReadAt,
                   coalesce((SELECT array_agg(own.comment_id) FROM card_readings own
                             WHERE own.project_id = cards.project_id AND own.card_number = cards.number AND own.comment_id IS NOT NULL), ARRAY[]::bigint[]) AS OwnComments
            FROM cards
            JOIN LATERAL (SELECT * FROM card_readings newest
                          WHERE newest.project_id = cards.project_id AND newest.card_number = cards.number AND newest.outcome IS NOT NULL
                          ORDER BY newest.read_at DESC, newest.run_id DESC
                          LIMIT 1) cr ON true
            WHERE cards.project_id = @projectId AND cards.state IN {BeforeItStarts}
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
              AND NOT {ReadingStore.Started}
            ORDER BY cards.number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(row => new UnstartedCard(row.Number, row.ReadThrough, new DateTimeOffset(DateTime.SpecifyKind(row.ReadAt, DateTimeKind.Utc)), row.OwnComments))];
    }

    /// <summary>The new comments among <paramref name="comments"/> that read <paramref name="card"/>
    /// again, oldest first, or null when there are none: by a login <paramref name="trusted"/> trusts,
    /// whatever the case, as GitHub compares logins, written after its newest reading, past those
    /// already taken, and never one Dev flow posted for its readings.</summary>
    public static ReadAgainComments? New(UnstartedCard card, IReadOnlyList<Comment> comments, TrustedAccounts trusted)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(trusted);
        var taken = comments
            .Where(comment => trusted.Trusts(comment.Author))
            .Where(comment => !card.OwnComments.Contains(comment.Id))
            .Where(comment => card.ReadThrough is not { } through || comment.Id > through)
            .Where(comment => comment.CreatedAt > card.ReadAt)
            .DistinctBy(comment => comment.Id)
            .OrderBy(comment => comment.Id)
            .ToList();
        return taken.Count == 0
            ? null
            : new ReadAgainComments(card.Number, [.. taken.Select(comment => new ReadAgainComment(comment.Author, comment.Body))], taken[^1].Id);
    }

    /// <summary>
    /// Takes <paramref name="comments"/> into their card, inside the read's transaction: they are
    /// kept for the reader's brief after any taken before and not yet read, and a card that waited
    /// for the owner, <c>needs_answers</c> or <c>refused</c>, moves to <c>queued</c>,
    /// <see cref="Commented"/>. Only while it is still before it starts, not started and with no
    /// run under way, and they are newer than those it took; the row is locked first, as
    /// <see cref="Fixing.OwnerComments.Take"/> locks it and for the same reason.
    /// </summary>
    public static async Task Take(NpgsqlTransaction transaction, Guid projectId, ReadAgainComments comments, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(comments);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM cards WHERE project_id = @projectId AND number = @card FOR UPDATE",
            new { projectId, card = comments.Card },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE cards SET state = 'queued',
                             reason = CASE WHEN state = 'queued' THEN reason ELSE @commented END,
                             read_again_comments = coalesce(read_again_comments, '[]'::jsonb) || @bodies::jsonb,
                             comments_read_through = @through,
                             changed_at = CASE WHEN state = 'queued' THEN changed_at ELSE @now END
            WHERE project_id = @projectId AND number = @card AND state IN {BeforeItStarts}
              AND (comments_read_through IS NULL OR comments_read_through < @through)
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
              AND NOT {ReadingStore.Started}
            """,
            new
            {
                projectId,
                card = comments.Card,
                commented = Commented,
                bodies = JsonSerializer.Serialize(comments.Comments, Json),
                through = comments.Through,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Queues every card of the project that waited for the owner, <c>needs_answers</c> or
    /// <c>refused</c>, whose record, as this read picked it, differs from what its newest reading was
    /// handed, its body's SHA-256 or its title, inside the read's transaction, after the cards are
    /// stored: <see cref="Edited"/>. A queued card so changed stays queued, and is due a reading by
    /// <see cref="ReadingStore.ReadAgain"/> alone. Only a card not started, with no run under way.
    /// </summary>
    public static Task<int> TakeEdits(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE cards SET state = 'queued', reason = @edited, changed_at = @now
            WHERE project_id = @projectId AND state IN ('needs_answers', 'refused') AND picked_body IS NOT NULL
              AND {ReadingStore.ChangedSinceRead}
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
              AND NOT {ReadingStore.Started}
            """,
            new { projectId, edited = Edited, now },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>The comments kept for a card's reading again, as stored, oldest first.</summary>
    internal static IReadOnlyList<ReadAgainComment> Kept(string? stored) =>
        stored is null ? [] : JsonSerializer.Deserialize<List<ReadAgainComment>>(stored, Json) ?? [];

    /// <summary>A card as read, set by property: Dapper reads an array no constructor parameter
    /// matches, as <c>CardStore</c> finds.</summary>
    private sealed class Row
    {
        public int Number { get; init; }

        public long? ReadThrough { get; init; }

        public DateTime ReadAt { get; init; }

        public long[] OwnComments { get; init; } = [];
    }
}