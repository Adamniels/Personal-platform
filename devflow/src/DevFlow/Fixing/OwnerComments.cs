using System.Text.Json;

using Dapper;

using DevFlow.CodeHost;

using Npgsql;

using PlatformData;

namespace DevFlow.Fixing;

/// <summary>A card whose pull request is read for the owner's comments: its number, its pull
/// request, and the newest of his comments already taken.</summary>
/// <param name="ReadThrough">The id of the newest of his comments taken, null before any was.</param>
public sealed record CommentableCard(int Number, int PullRequest, long? ReadThrough);

/// <summary>The owner's new comments on a card's pull request, oldest first, as a read found them,
/// to be taken in its transaction.</summary>
/// <param name="Bodies">Each comment, exactly as he wrote it.</param>
/// <param name="Through">The id of the newest of them.</param>
public sealed record TakenComments(int Card, int PullRequest, IReadOnlyList<string> Bodies, long Through);

/// <summary>
/// The owner's comment on a card's open pull request, the one input Dev flow reads from GitHub: one
/// by his linked login that is new starts a fix with it, read on the project's minute; it is not a
/// review round, and anyone else's comment is neither a trigger nor in the brief. A comment is new
/// when its id is past the newest of his already taken, every one of his before any was: GitHub's
/// ids only grow, so none is taken twice, and no time is compared, so none is lost to a run that
/// started after it was written. It is read only while no run of the card is under way, so one
/// written during a run is taken by the first read after the run ends. See "Fixing" in
/// devflow/README.md.
/// </summary>
public static class OwnerComments
{
    /// <summary>The states of a card whose open pull request is read for his comments: every state
    /// a card with a pull request is in between its runs, but done.</summary>
    private static readonly string[] Readable = ["reviewing", "fixing", "waiting_for_owner", "stopped"];

    /// <summary>The project's cards with a pull request and no run under way, in a state whose pull
    /// request is read for his comments, read as the project's user.</summary>
    public static async Task<IReadOnlyList<CommentableCard>> Cards(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<CommentableCard>(new CommandDefinition(
            """
            SELECT c.number AS Number, c.pull_request_number AS PullRequest, c.comments_read_through AS ReadThrough
            FROM cards c
            WHERE c.project_id = @projectId AND c.state = ANY(@readable) AND c.pull_request_number IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = c.project_id AND r.card_number = c.number AND r.finished_at IS NULL)
            ORDER BY c.number
            """,
            new { projectId, readable = Readable },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows];
    }

    /// <summary>The owner's new comments among <paramref name="comments"/>, oldest first, or null
    /// when there are none: by <paramref name="owner"/>, his linked login, as GitHub compares logins,
    /// whatever the case, and new for <paramref name="card"/>.</summary>
    public static TakenComments? New(CommentableCard card, IReadOnlyList<Comment> comments, string owner)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        var taken = comments
            .Where(comment => string.Equals(comment.Author, owner, StringComparison.OrdinalIgnoreCase))
            .Where(comment => card.ReadThrough is not { } through || comment.Id > through)
            .OrderBy(comment => comment.Id)
            .ToList();
        return taken.Count == 0 ? null : new TakenComments(card.Number, card.PullRequest, [.. taken.Select(comment => comment.Body)], taken[^1].Id);
    }

    /// <summary>
    /// Takes <paramref name="comments"/> into their card, inside the read's transaction: the card
    /// moves to <c>fixing</c> with them, answering its latest review too, if it has one, as a fix
    /// that is not a round; a card already waiting to be fixed in a round keeps it, and adds them.
    /// Only while it is still in a state that is read, with that pull request and no run under way,
    /// and the comments are newer than those it took; so a read that raced a run, or another read,
    /// takes nothing.
    /// </summary>
    public static async Task Take(NpgsqlTransaction transaction, Guid projectId, TakenComments comments, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(comments);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE cards SET
                state = 'fixing',
                reason = CASE WHEN state = 'fixing' THEN reason ELSE @commented END,
                fix_round = CASE WHEN state = 'fixing' THEN fix_round END,
                fix_review_id = CASE WHEN state = 'fixing' THEN fix_review_id
                                     ELSE (SELECT v.run_id FROM reviews v WHERE v.project_id = cards.project_id AND v.card_number = cards.number
                                           ORDER BY v.reviewed_at DESC, v.run_id DESC LIMIT 1) END,
                fix_comments = CASE WHEN state = 'fixing' THEN coalesce(fix_comments, '[]'::jsonb) || @bodies::jsonb ELSE @bodies::jsonb END,
                comments_read_through = @through,
                changed_at = @now
            WHERE project_id = @projectId AND number = @card AND pull_request_number = @pullRequest AND state = ANY(@readable)
              AND (comments_read_through IS NULL OR comments_read_through < @through)
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
            """,
            new
            {
                projectId,
                card = comments.Card,
                pullRequest = comments.PullRequest,
                bodies = JsonSerializer.Serialize(comments.Bodies),
                through = comments.Through,
                commented = FixRounds.Commented,
                readable = Readable,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

}