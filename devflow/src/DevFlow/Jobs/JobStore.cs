using System.Globalization;
using System.Text;

using Dapper;

using DevFlow.Cards;

using Npgsql;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>A card taken for a job, with the run made for it.</summary>
/// <param name="RunId">The run's id, the job's.</param>
/// <param name="Number">The card's issue number.</param>
/// <param name="Title">Its title as it was picked.</param>
/// <param name="Body">Its body as it was picked.</param>
/// <param name="Branch">The branch the work goes on, from <see cref="JobStore.Branch"/>.</param>
public sealed record ClaimedCard(Guid RunId, int Number, string Title, string Body, string Branch);

/// <summary>A card taken for its review, with the run made for it and what the review reads of the
/// build that made the commit.</summary>
/// <param name="RunId">The review run's id, the job's.</param>
/// <param name="Number">The card's issue number.</param>
/// <param name="Title">Its title as it was picked.</param>
/// <param name="Body">Its body as it was picked.</param>
/// <param name="Branch">The card's branch, the build's.</param>
/// <param name="Base">The branch it was made from, the build's.</param>
/// <param name="Commit">The commit to review, the card's head.</param>
/// <param name="Report">The build's report, as it was stored.</param>
/// <param name="Check">The check the build ran, null for a build handed out before runs kept it.</param>
/// <param name="CheckExitCode">How it ended.</param>
/// <param name="CheckOutput">The last lines it printed.</param>
public sealed record ClaimedReview(Guid RunId, int Number, string Title, string Body, string Branch, string Base, string Commit, string Report, string? Check, int CheckExitCode, string CheckOutput);

/// <summary>
/// Jobs in the database, through the door as the project's user: which kind of job a project has
/// to hand out, reviews before builds, taking a card for one with its lock, and which run a worker
/// holds. See "Jobs" and "Review" in devflow/README.md.
/// </summary>
public static class JobStore
{
    /// <summary>The longest slug a branch takes from its card's title.</summary>
    public const int MaxSlug = 40;

    /// <summary>What makes a card one to hand out, as SQL over <c>cards</c>: queued, waiting for
    /// nothing, picked with its body kept, and with no run under way. A read can leave a card
    /// queued while its run still goes on, when it ignored the card and a later one picked it
    /// again; such a card waits for its run to end, so the unique index <c>runs_one_under_way</c>
    /// stays a backstop rather than the guard.</summary>
    private const string Takeable = """
        project_id = @projectId AND state = 'queued' AND cardinality(waits_for) = 0 AND picked_body IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM runs
                        WHERE runs.project_id = cards.project_id AND runs.card_number = cards.number AND runs.finished_at IS NULL)
        """;

    /// <summary>
    /// A card's head commit, as SQL over <c>cards</c>: the commit its newest build that succeeded
    /// left on its branch, the one its pull request was opened at; null before one did.
    /// </summary>
    public const string HeadOf = """
        (SELECT b.head_commit FROM runs b
         WHERE b.project_id = cards.project_id AND b.card_number = cards.number AND b.kind = 'build' AND b.result = 'succeeded'
         ORDER BY b.started_at DESC, b.id DESC LIMIT 1)
        """;

    /// <summary>What makes a card one to review, as SQL over <c>cards</c>: reviewing, with its pull
    /// request open, picked with its body kept, no run under way, and a head commit no kept review
    /// is for.</summary>
    private const string Reviewable = $"""
        cards.project_id = @projectId AND cards.state = 'reviewing' AND cards.pull_request_number IS NOT NULL
        AND cards.picked_body IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM runs
                        WHERE runs.project_id = cards.project_id AND runs.card_number = cards.number AND runs.finished_at IS NULL)
        AND {HeadOf} IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM reviews v
                        WHERE v.project_id = cards.project_id AND v.card_number = cards.number AND v.reviewed_commit = {HeadOf})
        """;

    /// <summary>
    /// The branch a card's work goes on, <c>devflow/&lt;number&gt;-&lt;slug&gt;</c>: the slug is the
    /// title lowercased, each run of anything but ASCII letters and digits one hyphen, trimmed of
    /// hyphens, cut to <see cref="MaxSlug"/> characters and trimmed again; <c>devflow/&lt;number&gt;</c>
    /// when nothing is left.
    /// </summary>
    public static string Branch(int number, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var slug = new StringBuilder(title.Length);
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var cut = slug.ToString().TrimEnd('-');
        cut = cut.Length > MaxSlug ? cut[..MaxSlug].TrimEnd('-') : cut;
        return cut.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"devflow/{number}")
            : string.Create(CultureInfo.InvariantCulture, $"devflow/{number}-{cut}");
    }

    /// <summary>The kind of job the project has to hand out now, <see cref="DevFlow.Protocol.Job.Review"/>
    /// before <see cref="DevFlow.Protocol.Job.Build"/>, so work in progress finishes before new
    /// work starts; null when it has none. Read without a lock, so a worker with nothing to do costs
    /// Dev flow one query and GitHub nothing.</summary>
    public static async Task<string?> NextKind(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM cards WHERE {Reviewable}) THEN 'review'
                        WHEN EXISTS (SELECT 1 FROM cards WHERE {Takeable}) THEN 'build' END
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>
    /// Takes the project's first card to review, by <see cref="CardStore.QueueOrder"/>, inside
    /// <paramref name="transaction"/>, which runs as the project's user, as <see cref="Claim"/> takes
    /// one to build: locks its row with <c>FOR UPDATE SKIP LOCKED</c>, and records a review run for
    /// the worker, on the build's branch and base, with the head commit it reviews and the build's
    /// check. The card stays <c>reviewing</c>, the run under way holding it. Null when there is
    /// none.
    /// </summary>
    public static async Task<ClaimedReview?> ClaimReview(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var card = await transaction.Connection!.QuerySingleOrDefaultAsync<ToReview>(new CommandDefinition(
            $"""
            SELECT cards.number AS Number, cards.picked_title AS Title, cards.picked_body AS Body, b.branch AS Branch, b.base AS Base,
                   b.head_commit AS Commit, b.report::text AS Report, b.check_command AS "Check", b.check_exit_code AS CheckExitCode,
                   b.check_output AS CheckOutput
            FROM cards
            JOIN LATERAL (SELECT * FROM runs
                          WHERE runs.project_id = cards.project_id AND runs.card_number = cards.number
                            AND runs.kind = 'build' AND runs.result = 'succeeded'
                          ORDER BY runs.started_at DESC, runs.id DESC LIMIT 1) b ON true
            WHERE {Reviewable} AND b.report IS NOT NULL AND b.check_exit_code IS NOT NULL
            ORDER BY {CardStore.QueueOrder}
            LIMIT 1
            FOR UPDATE OF cards SKIP LOCKED
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        if (card is null)
        {
            return null;
        }

        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET reason = @reason, changed_at = @now WHERE project_id = @projectId AND number = @number",
            new { projectId, number = card.Number, reason = BeingReviewed, now },
            transaction,
            cancellationToken: cancellationToken));
        var claimed = new ClaimedReview(Guid.CreateVersion7(now), card.Number, card.Title, card.Body, card.Branch, card.Base, card.Commit, card.Report, card.Check, card.CheckExitCode, card.CheckOutput ?? "");
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, check_command, head_commit, started_at)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'review', @branch, @baseBranch, @check, @commit, @now)
            """,
            new { id = claimed.RunId, projectId, userId, number = claimed.Number, workerId, branch = claimed.Branch, baseBranch = claimed.Base, check = claimed.Check, commit = claimed.Commit, now },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>
    /// Takes the project's first card to hand out, by <see cref="CardStore.QueueOrder"/>, inside
    /// <paramref name="transaction"/>, which runs as the project's user: locks its row with
    /// <c>FOR UPDATE SKIP LOCKED</c>, moves it to <c>building</c>, and records a run for the worker,
    /// with the <paramref name="check"/> its job carries. Null when there is none, a card another transaction holds included, so two workers asking at
    /// once never get the same card and neither waits for the other.
    /// </summary>
    public static async Task<ClaimedCard?> Claim(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, string baseBranch, string check, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrEmpty(baseBranch);
        ArgumentNullException.ThrowIfNull(check);
        var card = await transaction.Connection!.QuerySingleOrDefaultAsync<Picked>(new CommandDefinition(
            $"""
            SELECT number AS Number, picked_title AS Title, picked_body AS Body FROM cards
            WHERE {Takeable}
            ORDER BY {CardStore.QueueOrder}
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        if (card is null)
        {
            return null;
        }

        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET state = 'building', reason = NULL, changed_at = @now WHERE project_id = @projectId AND number = @number",
            new { projectId, number = card.Number, now },
            transaction,
            cancellationToken: cancellationToken));
        var claimed = new ClaimedCard(Guid.CreateVersion7(now), card.Number, card.Title, card.Body, Branch(card.Number, card.Title));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, check_command, started_at)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'build', @branch, @baseBranch, @check, @now)
            """,
            new { id = claimed.RunId, projectId, userId, number = claimed.Number, workerId, branch = claimed.Branch, baseBranch, check, now },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>Whether <paramref name="runId"/> is a build run of the project, under way, held by
    /// <paramref name="workerId"/>, the one kind of run a push key is made for; read as the
    /// project's user, so another user's run is never found.</summary>
    public static async Task<bool> HoldsBuild(UserDb database, Guid userId, Guid projectId, Guid runId, Guid workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM runs
                           WHERE id = @runId AND project_id = @projectId AND worker_id = @workerId AND kind = 'build'
                             AND finished_at IS NULL)
            """,
            new { runId, projectId, workerId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>What a card under review says while its run is under way.</summary>
    public const string BeingReviewed = "The reviewer is reading it";

    private sealed record Picked(int Number, string Title, string Body);

    private sealed record ToReview(int Number, string Title, string Body, string Branch, string Base, string Commit, string Report, string? Check, int CheckExitCode, string? CheckOutput);
}