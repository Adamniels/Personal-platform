using System.Text.Json;

using Dapper;

using DevFlow.Jobs;
using DevFlow.Protocol;

using Npgsql;

using PlatformData;

namespace DevFlow.Reviews;

/// <summary>A review as Dev flow kept it: the verdict, for the commit the worker reviewed.</summary>
/// <param name="RunId">The review run that read it, which holds its usage.</param>
/// <param name="Commit">The commit the worker checked out and reviewed.</param>
/// <param name="Verdict">The verdict, in its form.</param>
/// <param name="ReviewedAt">When it was recorded.</param>
/// <param name="PostedAt">When its copy was posted on the pull request, null until then.</param>
public sealed record KeptReview(Guid RunId, string Commit, ReviewVerdict Verdict, DateTimeOffset ReviewedAt, DateTimeOffset? PostedAt);

/// <summary>A review whose copy waits to be posted on its pull request.</summary>
public sealed record UnpostedReview(Guid RunId, int CardNumber, int PullRequestNumber, string Commit, ReviewVerdict Verdict);

/// <summary>
/// Reviews in the database, through the door as the project's user: a verdict recorded for the
/// commit the worker reviewed, in the same transaction as its run's result; a card's current
/// review, the one for its head commit and no other; and the reviews whose copy waits to be posted.
/// A verdict is learned only from a review run's result, never from a comment. See "Review" in
/// devflow/README.md.
/// </summary>
public static class ReviewStore
{
    /// <summary>The card's reviews as SQL rows, by property.</summary>
    private const string Columns = """
        run_id AS RunId, card_number AS CardNumber, reviewed_commit AS ReviewedCommit, verdict AS Verdict, summary AS Summary,
        findings::text AS Findings, for_owner::text AS ForOwner, pull_request_number AS PullRequestNumber,
        reviewed_at AS ReviewedAt, posted_at AS PostedAt
        """;

    /// <summary>
    /// Records <paramref name="verdict"/>, read in its form, for <paramref name="commit"/>, the one
    /// the review run <paramref name="runId"/> was handed and its worker checked out, inside the
    /// transaction that stores the run's result, on the card's pull request. False, recording
    /// nothing, when the card no longer has a pull request to post it on.
    /// </summary>
    public static async Task<bool> Record(NpgsqlTransaction transaction, Guid projectId, Guid userId, int card, Guid runId, string commit, ReviewVerdict verdict, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(verdict);
        var recorded = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reviews (run_id, project_id, user_id, card_number, reviewed_commit, verdict, summary, findings, for_owner,
                                 pull_request_number, reviewed_at)
            SELECT @runId, @projectId, @userId, @card, @commit, @verdict, @summary, @findings::jsonb, @forOwner::jsonb,
                   c.pull_request_number, @now
            FROM cards c WHERE c.project_id = @projectId AND c.number = @card AND c.pull_request_number IS NOT NULL
            """,
            new
            {
                runId,
                projectId,
                userId,
                card,
                commit,
                verdict = verdict.Word,
                summary = verdict.Summary,
                findings = JsonSerializer.Serialize(verdict.Findings, ReviewVerdict.Json),
                forOwner = JsonSerializer.Serialize(verdict.ForOwner, ReviewVerdict.Json),
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
        return recorded > 0;
    }

    /// <summary>
    /// The card's current review: its newest for the card's head commit, the one its newest build
    /// that succeeded left. A review for an older commit does not count for the head, so a commit
    /// pushed after a review needs a new one. Null when the head has none, or there is no such card.
    /// </summary>
    public static async Task<KeptReview?> Current(UserDb database, Guid userId, Guid projectId, int card, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var row = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<ReviewRow>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM reviews
            WHERE project_id = @projectId AND card_number = @card
              AND reviewed_commit = (SELECT {JobStore.HeadOf} FROM cards WHERE cards.project_id = @projectId AND cards.number = @card)
            ORDER BY reviewed_at DESC, run_id DESC
            LIMIT 1
            """,
            new { projectId, card },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return row is null ? null : Kept(row);
    }

    /// <summary>Every review of the card, newest first, read inside <paramref name="transaction"/>,
    /// which runs as the card's user.</summary>
    public static async Task<IReadOnlyList<KeptReview>> OfCard(NpgsqlTransaction transaction, Guid projectId, int card, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var rows = await transaction.Connection!.QueryAsync<ReviewRow>(new CommandDefinition(
            $"SELECT {Columns} FROM reviews WHERE project_id = @projectId AND card_number = @card ORDER BY reviewed_at DESC, run_id DESC",
            new { projectId, card },
            transaction,
            cancellationToken: cancellationToken));
        return [.. rows.Select(Kept)];
    }

    /// <summary>The project's reviews whose copy waits to be posted, oldest first.</summary>
    public static async Task<IReadOnlyList<UnpostedReview>> Unposted(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<ReviewRow>(new CommandDefinition(
            $"SELECT {Columns} FROM reviews WHERE project_id = @projectId AND posted_at IS NULL ORDER BY reviewed_at, run_id",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(row => new UnpostedReview(row.RunId, row.CardNumber, row.PullRequestNumber, row.ReviewedCommit, Kept(row).Verdict))];
    }

    /// <summary>Marks the review's copy posted, once.</summary>
    public static Task MarkPosted(UserDb database, Guid userId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE reviews SET posted_at = @now WHERE run_id = @runId AND posted_at IS NULL",
            new { runId, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>A stored review read back; its verdict was in its form when it was recorded, and
    /// is read again by the same type.</summary>
    private static KeptReview Kept(ReviewRow row) => new(
        row.RunId,
        row.ReviewedCommit,
        new ReviewVerdict(
            row.Verdict == "approve" ? ReviewVerdictKind.Approve : ReviewVerdictKind.Changes,
            row.Summary,
            JsonSerializer.Deserialize<List<ReviewFinding>>(row.Findings, ReviewVerdict.Json) ?? [],
            JsonSerializer.Deserialize<List<ReviewOwnerView>>(row.ForOwner, ReviewVerdict.Json) ?? []),
        Utc(row.ReviewedAt),
        row.PostedAt is { } posted ? Utc(posted) : null);

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private sealed class ReviewRow
    {
        public Guid RunId { get; init; }

        public int CardNumber { get; init; }

        public string ReviewedCommit { get; init; } = "";

        public string Verdict { get; init; } = "";

        public string Summary { get; init; } = "";

        public string Findings { get; init; } = "";

        public string ForOwner { get; init; } = "";

        public int PullRequestNumber { get; init; }

        public DateTime ReviewedAt { get; init; }

        public DateTime? PostedAt { get; init; }
    }
}