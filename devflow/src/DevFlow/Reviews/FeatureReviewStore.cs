using System.Text.Json;

using Dapper;

using DevFlow.Protocol;

using Npgsql;

using PlatformData;

namespace DevFlow.Reviews;

/// <summary>A whole feature's review as Dev flow kept it: the verdict, for the commit the worker
/// reviewed.</summary>
/// <param name="RunId">The review run that read it, which holds its usage.</param>
/// <param name="FeatureNumber">The feature's parent's number.</param>
/// <param name="Commit">The commit the worker checked out and reviewed.</param>
/// <param name="Verdict">The verdict, in its form.</param>
/// <param name="PullRequestNumber">The feature's pull request it is posted on.</param>
/// <param name="ReviewedAt">When it was recorded.</param>
/// <param name="PostedAt">When its copy was posted, null until then.</param>
public sealed record KeptFeatureReview(Guid RunId, int FeatureNumber, string Commit, ReviewVerdict Verdict, int PullRequestNumber, DateTimeOffset ReviewedAt, DateTimeOffset? PostedAt);

/// <summary>
/// Whole features' reviews in the database, through the door as the project's user: a verdict
/// recorded for the commit the worker reviewed, in the same transaction as its run's result; a
/// feature's reviews, newest first; and the reviews whose copy waits to be posted on the feature's
/// pull request. A verdict is learned only from a review run's result, never from a comment, as a
/// card's is. See "Reviewing a feature" in devflow/README.md.
/// </summary>
public static class FeatureReviewStore
{
    private const string Columns = """
        run_id AS RunId, feature_number AS FeatureNumber, reviewed_commit AS ReviewedCommit, verdict AS Verdict, summary AS Summary,
        findings::text AS Findings, for_owner::text AS ForOwner, pull_request_number AS PullRequestNumber,
        reviewed_at AS ReviewedAt, posted_at AS PostedAt
        """;

    /// <summary>
    /// Records <paramref name="verdict"/>, read in its form, for <paramref name="commit"/>, the one
    /// the review run <paramref name="runId"/> was handed and its worker checked out, inside the
    /// transaction that stores the run's result, on the feature's pull request. False, recording
    /// nothing, when the feature no longer has a pull request to post it on.
    /// </summary>
    public static async Task<bool> Record(NpgsqlTransaction transaction, Guid projectId, Guid userId, int feature, Guid runId, string commit, ReviewVerdict verdict, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(verdict);
        return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO feature_reviews (run_id, project_id, user_id, feature_number, reviewed_commit, verdict, summary, findings, for_owner,
                                         pull_request_number, reviewed_at)
            SELECT @runId, @projectId, @userId, @feature, @commit, @verdict, @summary, @findings::jsonb, @forOwner::jsonb,
                   f.pull_request_number, @now
            FROM features f WHERE f.project_id = @projectId AND f.number = @feature AND f.pull_request_number IS NOT NULL
            """,
            new
            {
                runId,
                projectId,
                userId,
                feature,
                commit,
                verdict = verdict.Word,
                summary = verdict.Summary,
                findings = JsonSerializer.Serialize(verdict.Findings, ReviewVerdict.Json),
                forOwner = JsonSerializer.Serialize(verdict.ForOwner, ReviewVerdict.Json),
                now,
            },
            transaction,
            cancellationToken: cancellationToken)) > 0;
    }

    /// <summary>Every review of the feature, newest first, read inside
    /// <paramref name="transaction"/>, which runs as the feature's user.</summary>
    public static async Task<IReadOnlyList<KeptFeatureReview>> OfFeature(NpgsqlTransaction transaction, Guid projectId, int feature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var rows = await transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM feature_reviews WHERE project_id = @projectId AND feature_number = @feature ORDER BY reviewed_at DESC, run_id DESC",
            new { projectId, feature },
            transaction,
            cancellationToken: cancellationToken));
        return [.. rows.Select(Kept)];
    }

    /// <summary>The review its run names, read inside <paramref name="transaction"/>, which runs as
    /// its user; null when there is none.</summary>
    public static async Task<KeptFeatureReview?> Of(NpgsqlTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var row = await transaction.Connection!.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM feature_reviews WHERE run_id = @runId",
            new { runId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : Kept(row);
    }

    /// <summary>The project's reviews whose copy waits to be posted, oldest first.</summary>
    public static async Task<IReadOnlyList<KeptFeatureReview>> Unposted(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var rows = await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM feature_reviews WHERE project_id = @projectId AND posted_at IS NULL ORDER BY reviewed_at, run_id",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return [.. rows.Select(Kept)];
    }

    /// <summary>Marks the review's copy posted, once.</summary>
    public static Task MarkPosted(UserDb database, Guid userId, Guid runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE feature_reviews SET posted_at = @now WHERE run_id = @runId AND posted_at IS NULL",
            new { runId, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>A stored review read back; its verdict was in its form when it was recorded, and
    /// is read again by the same type.</summary>
    private static KeptFeatureReview Kept(Row row) => new(
        row.RunId,
        row.FeatureNumber,
        row.ReviewedCommit,
        new ReviewVerdict(
            row.Verdict == "approve" ? ReviewVerdictKind.Approve : ReviewVerdictKind.Changes,
            row.Summary,
            JsonSerializer.Deserialize<List<ReviewFinding>>(row.Findings, ReviewVerdict.Json) ?? [],
            JsonSerializer.Deserialize<List<ReviewOwnerView>>(row.ForOwner, ReviewVerdict.Json) ?? []),
        row.PullRequestNumber,
        Utc(row.ReviewedAt),
        row.PostedAt is { } posted ? Utc(posted) : null);

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private sealed class Row
    {
        public Guid RunId { get; init; }

        public int FeatureNumber { get; init; }

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