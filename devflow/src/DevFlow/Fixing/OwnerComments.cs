using System.Text.Json;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Features;
using DevFlow.Jobs;

using Npgsql;

using PlatformData;

namespace DevFlow.Fixing;

/// <summary>A card whose pull request, or, stopped or landed, whose issue, is read for the owner's
/// comments: its number, its pull request, the newest of his comments already taken, and when it
/// stopped or last ran.</summary>
/// <param name="PullRequest">Its pull request, null for a stopped card that has none; for a landed
/// card, the one it landed in, which is not read.</param>
/// <param name="ReadThrough">The id of the newest of his comments taken, null before any was.</param>
/// <param name="StoppedAt">When it stopped, for a stopped card, whose comments count only when
/// newer; null otherwise.</param>
/// <param name="LandedAt">For a card landed in an open feature's branch, which his comment on its
/// issue sends back, when its last run ended, or it last changed without one: only a comment newer
/// counts. Null for any other card.</param>
public sealed record CommentableCard(int Number, int? PullRequest, long? ReadThrough, DateTimeOffset? StoppedAt = null, DateTimeOffset? LandedAt = null);

/// <summary>The owner's new comments on a card's pull request, or a stopped or landed card's issue,
/// oldest first, as a read found them, to be taken in its transaction.</summary>
/// <param name="PullRequest">The card's open pull request, null for a stopped card without one, which
/// is built again, and for a landed card.</param>
/// <param name="Bodies">Each comment, exactly as he wrote it.</param>
/// <param name="Through">The id of the newest of them.</param>
/// <param name="SentBack">Whether they are on a landed card's issue, and send it back.</param>
public sealed record TakenComments(int Card, int? PullRequest, IReadOnlyList<string> Bodies, long Through, bool SentBack = false);

/// <summary>
/// The owner's comment on a card's open pull request, the one input Dev flow reads from GitHub: one
/// by his linked login that is new starts a fix with it, read on the project's minute; it is not a
/// review round, and anyone else's comment is neither a trigger nor in the brief. A comment is new
/// when its id is past the newest of his already taken, every one of his before any was: GitHub's
/// ids only grow, so none is taken twice, and no time is compared, so none is lost to a run that
/// started after it was written. It is read only while no run of the card is under way, so one
/// written during a run waits for the first read that falls between runs. See "Fixing" in
/// devflow/README.md.
/// </summary>
public static class OwnerComments
{
    /// <summary>The states of a card whose open pull request is read for his comments: every state
    /// a card with a pull request is in between its runs, but done.</summary>
    private static readonly string[] Readable = ["reviewing", "fixing", "waiting_for_owner", "stopped"];

    /// <summary>What a stopped card without an open pull request says once his comment ran it
    /// again.</summary>
    public const string RanAgain = "You commented on it; it runs again next";

    /// <summary>The project's cards with no run under way, in a state whose pull request is read for
    /// his comments, with one, or stopped, with or without one, or landed in an open feature's
    /// branch, by <see cref="SendingBack.Sendable"/>, read as the project's user.</summary>
    public static async Task<IReadOnlyList<CommentableCard>> Cards(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT cards.number AS Number, cards.pull_request_number AS PullRequest, cards.comments_read_through AS ReadThrough,
                   -- A card stopped before stops were recorded counts from its last change.
                   CASE WHEN cards.state = 'stopped' THEN coalesce(cards.stopped_at, cards.changed_at) END AS StoppedAt,
                   -- A landed card counts from its last run's end, or its last change without one.
                   CASE WHEN {SendingBack.Sendable}
                        THEN coalesce((SELECT max(r.finished_at) FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number),
                                      cards.changed_at) END AS LandedAt
            FROM cards
            WHERE cards.project_id = @projectId
              AND ((cards.state = ANY(@readable) AND (cards.pull_request_number IS NOT NULL OR cards.state = 'stopped')) OR {SendingBack.Sendable})
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
            ORDER BY cards.number
            """,
            new { projectId, readable = Readable },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(row => new CommentableCard(row.Number, row.PullRequest, row.ReadThrough, Utc(row.StoppedAt), Utc(row.LandedAt)))];
    }

    private static DateTimeOffset? Utc(DateTime? at) => at is { } value ? new DateTimeOffset(value, TimeSpan.Zero) : null;

    private sealed record Row(int Number, int? PullRequest, long? ReadThrough, DateTime? StoppedAt, DateTime? LandedAt);

    /// <summary>The owner's new comments among <paramref name="comments"/>, oldest first, or null
    /// when there are none: by <paramref name="owner"/>, his linked login, as GitHub compares logins,
    /// whatever the case, new for <paramref name="card"/>, for a stopped card written after it
    /// stopped, and for a landed card after its last run. They are for
    /// <paramref name="pullRequest"/>, the card's pull request while it is open, or null; a landed
    /// card's send it back.</summary>
    public static TakenComments? New(CommentableCard card, IReadOnlyList<Comment> comments, string owner, int? pullRequest)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        var taken = comments
            .Where(comment => string.Equals(comment.Author, owner, StringComparison.OrdinalIgnoreCase))
            .Where(comment => card.ReadThrough is not { } through || comment.Id > through)
            .Where(comment => card.StoppedAt is not { } stopped || comment.CreatedAt > stopped)
            .Where(comment => card.LandedAt is not { } landed || comment.CreatedAt > landed)
            .DistinctBy(comment => comment.Id)
            .OrderBy(comment => comment.Id)
            .ToList();
        return taken.Count == 0
            ? null
            : new TakenComments(card.Number, card.LandedAt is null ? pullRequest : null, [.. taken.Select(comment => comment.Body)], taken[^1].Id, card.LandedAt is not null);
    }

    /// <summary>
    /// Takes <paramref name="comments"/> into their card, inside the read's transaction: the card
    /// moves to <c>fixing</c> with them, as a fix that is not a round, answering too its latest
    /// review if that is for the card's head, so never one a fix since has answered; a card already
    /// waiting to be fixed in a round keeps it, and adds them. Only while it is still in a state
    /// that is read, with that pull request and no run under way, and the comments are newer than
    /// those it took; so a read that raced a run, or another read, takes nothing.
    ///
    /// The card's row is locked first, in a statement of its own, and only then is the update run:
    /// every claim of a run locks the same row and records the run before it commits, and under
    /// read committed a statement sees what was committed before it began, so the update's check
    /// for a run sees one a claim committed while the lock was waited for. Checked inside the
    /// update alone, it would not: an update that waits on a row checks that row again, but reads
    /// <c>runs</c> as it was when it began, and would take the comments into a fix that has already
    /// begun without them.
    /// </summary>
    public static async Task Take(NpgsqlTransaction transaction, Guid projectId, TakenComments comments, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(comments);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM cards WHERE project_id = @projectId AND number = @card FOR UPDATE",
            new { projectId, card = comments.Card },
            transaction,
            cancellationToken: cancellationToken));
        if (comments.SentBack)
        {
            // A card landed in an open feature's branch is sent back: queued with his comments as
            // its input, to be built on a new branch from the feature's branch, the pull request it
            // landed in kept for its brief, its rounds and failures afresh. The app opens its issue
            // again on the minute, see LandedCards.
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                $"""
                UPDATE cards SET
                    state = 'queued', reason = @queued, fix_round = NULL, fix_review_id = NULL, fix_comments = NULL,
                    sent_backs = sent_backs + 1, sent_back_from = pull_request_number, sent_back_from_url = pull_request_url,
                    sent_back_comments = @bodies::jsonb,
                    fix_rounds = 0, in_card_failures = 0, failed_at_commit = NULL, pull_request_number = NULL, pull_request_url = NULL,
                    comments_read_through = @through, changed_at = @now
                WHERE project_id = @projectId AND number = @card AND {SendingBack.Sendable}
                  AND (comments_read_through IS NULL OR comments_read_through < @through)
                  AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
                """,
                new { projectId, card = comments.Card, bodies = JsonSerializer.Serialize(comments.Bodies), through = comments.Through, queued = SendingBack.Queued, now },
                transaction,
                cancellationToken: cancellationToken));
            return;
        }

        if (comments.PullRequest is null)
        {
            // A stopped card without an open pull request is built again, on its branch, with his
            // comments in the brief; a closed pull request is no longer its own, and the next
            // success opens another.
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                UPDATE cards SET
                    state = 'queued', reason = @ranAgain, fix_round = NULL, fix_review_id = NULL, fix_comments = @bodies::jsonb,
                    fix_rounds = 0, in_card_failures = 0, failed_at_commit = NULL, pull_request_number = NULL, pull_request_url = NULL,
                    comments_read_through = @through, changed_at = @now
                WHERE project_id = @projectId AND number = @card AND state = 'stopped'
                  AND (comments_read_through IS NULL OR comments_read_through < @through)
                  AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.finished_at IS NULL)
                """,
                new { projectId, card = comments.Card, bodies = JsonSerializer.Serialize(comments.Bodies), through = comments.Through, ranAgain = RanAgain, now },
                transaction,
                cancellationToken: cancellationToken));
            return;
        }

        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE cards SET
                state = 'fixing',
                reason = CASE WHEN state = 'fixing' THEN reason ELSE @commented END,
                fix_round = CASE WHEN state = 'fixing' THEN fix_round END,
                fix_review_id = CASE WHEN state = 'fixing' THEN fix_review_id
                                     ELSE (SELECT v.run_id FROM reviews v WHERE v.project_id = cards.project_id AND v.card_number = cards.number
                                             AND v.reviewed_commit = {JobStore.HeadOf}
                                           ORDER BY v.reviewed_at DESC, v.run_id DESC LIMIT 1) END,
                fix_comments = CASE WHEN state = 'fixing' THEN coalesce(fix_comments, '[]'::jsonb) || @bodies::jsonb ELSE @bodies::jsonb END,
                comments_read_through = @through,
                -- Run again from a stop, its count of failures and rounds starts afresh.
                fix_rounds = CASE WHEN state = 'stopped' THEN 0 ELSE fix_rounds END,
                in_card_failures = CASE WHEN state = 'stopped' THEN 0 ELSE in_card_failures END,
                failed_at_commit = CASE WHEN state = 'stopped' THEN NULL ELSE failed_at_commit END,
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