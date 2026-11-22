using System.Text.Json;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Projects;

using Npgsql;

using PlatformData;

namespace DevFlow.Readings;

/// <summary>A refused card read for what reads it again: its number, the newest comment already
/// taken, when the reading that refused it came back, and Dev flow's own comment on it.</summary>
/// <param name="ReadThrough">The id of the newest comment taken, null before any was.</param>
/// <param name="RefusedAt">When the reading that refused it came back; only a comment newer
/// counts.</param>
/// <param name="CommentId">The refusal's comment, Dev flow's own, which never reads it again; null
/// before it is posted.</param>
public sealed record RefusedCard(int Number, long? ReadThrough, DateTimeOffset RefusedAt, long? CommentId);

/// <summary>A comment that asks for a card to be read again, as the reader's brief quotes it.</summary>
/// <param name="Login">The trusted account that wrote it, as GitHub names it.</param>
/// <param name="Body">Its text, exactly as written.</param>
public sealed record ReadAgainComment(string Login, string Body);

/// <summary>The trusted accounts' new comments on a refused card, oldest first, as a read found them,
/// to be taken in its transaction.</summary>
/// <param name="Through">The id of the newest of them.</param>
public sealed record RefusalComments(int Card, IReadOnlyList<ReadAgainComment> Comments, long Through);

/// <summary>
/// What reads a refused card again: a trusted account's comment on its issue newer than the
/// refusal, or an edit of its body or title, since picking replaces a card's record whenever every
/// editor is trusted, and one edited by anyone else is ignored. Either queues it, and a queued card
/// whose newest reading is too thin is due a reading of it alone,
/// <see cref="ReadingStore.ReadAgain"/>, with the comments in the reader's brief. Dev flow's own
/// comments never read it again, by their stored ids. See "Reading a card" under "Jobs" in
/// devflow/README.md.
/// </summary>
public static class Refusals
{
    /// <summary>What a refused card edited since says until it is read again.</summary>
    public const string Edited = "You edited it; it is read again next";

    /// <summary>What a refused card commented on since says until it is read again.</summary>
    public const string Commented = "You commented on it; it is read again next";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The project's refused cards whose newest reading refused them, with no run under
    /// way, read as the project's user, by number.</summary>
    public static async Task<IReadOnlyList<RefusedCard>> Cards(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT c.number AS Number, c.comments_read_through AS ReadThrough, cr.read_at AS RefusedAt, cr.comment_id AS CommentId
            FROM cards c
            JOIN LATERAL (SELECT * FROM card_readings newest
                          WHERE newest.project_id = c.project_id AND newest.card_number = c.number AND newest.outcome IS NOT NULL
                          ORDER BY newest.read_at DESC, newest.run_id DESC
                          LIMIT 1) cr ON true
            WHERE c.project_id = @projectId AND c.state = 'refused' AND cr.outcome = 'too_thin'
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = c.project_id AND r.card_number = c.number AND r.finished_at IS NULL)
            ORDER BY c.number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(row => new RefusedCard(row.Number, row.ReadThrough, new DateTimeOffset(row.RefusedAt, TimeSpan.Zero), row.CommentId))];
    }

    /// <summary>The new comments among <paramref name="comments"/> that read <paramref name="card"/>
    /// again, oldest first, or null when there are none: by a login <paramref name="trusted"/> trusts,
    /// whatever the case, as GitHub compares logins, written after the refusal, past those already
    /// taken, and never Dev flow's own refusal.</summary>
    public static RefusalComments? New(RefusedCard card, IReadOnlyList<Comment> comments, TrustedAccounts trusted)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(trusted);
        var taken = comments
            .Where(comment => trusted.Trusts(comment.Author))
            .Where(comment => comment.Id != card.CommentId)
            .Where(comment => card.ReadThrough is not { } through || comment.Id > through)
            .Where(comment => comment.CreatedAt > card.RefusedAt)
            .DistinctBy(comment => comment.Id)
            .OrderBy(comment => comment.Id)
            .ToList();
        return taken.Count == 0
            ? null
            : new RefusalComments(card.Number, [.. taken.Select(comment => new ReadAgainComment(comment.Author, comment.Body))], taken[^1].Id);
    }

    /// <summary>
    /// Takes <paramref name="comments"/> into their card, inside the read's transaction: it moves to
    /// <c>queued</c>, <see cref="Commented"/>, with them kept for the reader's brief. Only while it is
    /// still refused, with no run under way, and they are newer than those it took; the row is locked
    /// first, as <see cref="Fixing.OwnerComments.Take"/> locks it and for the same reason.
    /// </summary>
    public static async Task Take(NpgsqlTransaction transaction, Guid projectId, RefusalComments comments, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(comments);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM cards WHERE project_id = @projectId AND number = @card FOR UPDATE",
            new { projectId, card = comments.Card },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE cards SET state = 'queued', reason = @commented, read_again_comments = @bodies::jsonb,
                             comments_read_through = @through, changed_at = @now
            WHERE project_id = @projectId AND number = @card AND state = 'refused'
              AND (comments_read_through IS NULL OR comments_read_through < @through)
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
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
    /// Queues every refused card of the project whose record, as this read picked it, differs from
    /// what the reading that refused it was handed, its body's SHA-256 or its title, inside the
    /// read's transaction, after the cards are stored: <see cref="Edited"/>. Only a card with no run
    /// under way.
    /// </summary>
    public static Task<int> TakeEdits(NpgsqlTransaction transaction, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE cards SET state = 'queued', reason = @edited, changed_at = @now
            WHERE project_id = @projectId AND state = 'refused' AND picked_body IS NOT NULL
              AND EXISTS (SELECT 1 FROM (SELECT cr.outcome, cr.body_sha256, cr.title FROM card_readings cr
                                         WHERE cr.project_id = cards.project_id AND cr.card_number = cards.number AND cr.outcome IS NOT NULL
                                         ORDER BY cr.read_at DESC, cr.run_id DESC
                                         LIMIT 1) newest
                          WHERE newest.outcome = 'too_thin'
                            AND (newest.body_sha256 <> cards.picked_body_sha256 OR newest.title <> cards.picked_title))
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
            """,
            new { projectId, edited = Edited, now },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>The comments kept for a card's reading again, as stored, oldest first.</summary>
    internal static IReadOnlyList<ReadAgainComment> Kept(string? stored) =>
        stored is null ? [] : JsonSerializer.Deserialize<List<ReadAgainComment>>(stored, Json) ?? [];

    private sealed record Row(int Number, long? ReadThrough, DateTime RefusedAt, long? CommentId);
}