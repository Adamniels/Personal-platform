using System.Text.Json;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Fixing;
using DevFlow.Projects;

using Npgsql;

using PlatformData;

namespace DevFlow.Features;

/// <summary>A feature whose pull request is read for the owner's comments: its parent's number, its
/// pull request, and the newest of his comments already taken.</summary>
/// <param name="ReadThrough">The id of the newest of his comments taken, null before any was.</param>
public sealed record CommentableFeature(int Number, int PullRequest, long? ReadThrough);

/// <summary>
/// The owner's comment on a feature's pull request, the one comment read there as an input: one by
/// a trusted account of the project, his linked login or another on the list, that is new starts a
/// fix of the whole feature with it, read on the project's minute, and is not one of its review's
/// rounds; one by a login off the list, the app's review copies among them, is neither a trigger
/// nor in the brief. A comment is new by the rule a card's is, see
/// <see cref="OwnerComments"/>: its id past the newest of his already taken, every one of his
/// before any was, and no time compared, so one written while a run was under way is taken by the
/// first read that falls between runs rather than lost. See "Fixing a feature" in
/// devflow/README.md.
/// </summary>
public static class FeatureComments
{
    /// <summary>The states of a feature whose open pull request is read for his comments: every
    /// state its pull request is open in between its runs, once its review has begun.</summary>
    private static readonly string[] Readable = ["reviewing", "fixing", "waiting_for_owner", "ready"];

    /// <summary>The project's features with a pull request, no run of their own under way, in a
    /// state whose pull request is read for his comments, read as the project's user.</summary>
    public static async Task<IReadOnlyList<CommentableFeature>> Features(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<CommentableFeature>(new CommandDefinition(
            """
            SELECT f.number AS Number, f.pull_request_number AS PullRequest, f.comments_read_through AS ReadThrough
            FROM features f
            WHERE f.project_id = @projectId AND f.state = ANY(@readable) AND f.pull_request_number IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = f.project_id AND r.feature_number = f.number AND r.finished_at IS NULL)
            ORDER BY f.number
            """,
            new { projectId, readable = Readable },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows];
    }

    /// <summary>The owner's new comments among <paramref name="comments"/>, the feature's pull
    /// request's, oldest first, or null when there are none: by a login <paramref name="trusted"/>
    /// trusts, whatever its case, and new for <paramref name="feature"/>, as a card's
    /// are.</summary>
    public static TakenComments? New(CommentableFeature feature, IReadOnlyList<Comment> comments, TrustedAccounts trusted)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return OwnerComments.New(new CommentableCard(feature.Number, feature.PullRequest, feature.ReadThrough), comments, trusted, feature.PullRequest);
    }

    /// <summary>
    /// Takes <paramref name="comments"/>, whose <see cref="TakenComments.Card"/> is the feature's
    /// parent, into the feature, inside the read's transaction: it moves to <c>fixing</c> with them,
    /// as a fix that is not one of its review's rounds, answering too its latest review if no fix
    /// has succeeded since it, so never one a fix has answered; a feature already waiting to be
    /// fixed in a round keeps it, and adds them. Only while it is still in a state that is read,
    /// with that pull request and no run of its own under way, and the comments are newer than
    /// those it took. The feature's row is locked first, in a statement of its own, as a card's is
    /// in <see cref="OwnerComments.Take"/> and for the same reason: every claim of its run locks
    /// that row and records the run before it commits.
    /// </summary>
    public static async Task Take(NpgsqlTransaction transaction, Guid projectId, TakenComments comments, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(comments);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM features WHERE project_id = @projectId AND number = @number FOR UPDATE",
            new { projectId, number = comments.Card },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET
                state = 'fixing',
                reason = CASE WHEN state = 'fixing' THEN reason ELSE @commented END,
                next_fix_round = CASE WHEN state = 'fixing' THEN next_fix_round END,
                fix_review_id = CASE WHEN state = 'fixing' THEN fix_review_id
                                     ELSE (SELECT v.run_id FROM feature_reviews v
                                           WHERE v.project_id = features.project_id AND v.feature_number = features.number
                                             AND v.reviewed_at > coalesce((SELECT max(r.finished_at) FROM runs r
                                                                           WHERE r.project_id = features.project_id AND r.feature_number = features.number
                                                                             AND r.kind = 'feature_fix' AND r.result = 'succeeded'), '-infinity')
                                           ORDER BY v.reviewed_at DESC, v.run_id DESC LIMIT 1) END,
                fix_comments = CASE WHEN state = 'fixing' THEN coalesce(fix_comments, '[]'::jsonb) || @bodies::jsonb ELSE @bodies::jsonb END,
                fix_failed_at = CASE WHEN state = 'fixing' THEN fix_failed_at END,
                review_commit = CASE WHEN state = 'fixing' THEN review_commit END,
                comments_read_through = @through,
                changed_at = @now
            WHERE project_id = @projectId AND number = @number AND pull_request_number = @pullRequest AND state = ANY(@readable)
              AND (comments_read_through IS NULL OR comments_read_through < @through)
              AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.project_id = features.project_id AND r.feature_number = features.number AND r.finished_at IS NULL)
            """,
            new
            {
                projectId,
                number = comments.Card,
                pullRequest = comments.PullRequest,
                bodies = JsonSerializer.Serialize(comments.Bodies),
                through = comments.Through,
                commented = FeatureFix.Commented,
                readable = Readable,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}