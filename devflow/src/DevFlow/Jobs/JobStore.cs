using System.Globalization;
using System.Text;
using System.Text.Json;

using Dapper;

using DevFlow.Briefs;
using DevFlow.Cards;
using DevFlow.Fixing;
using DevFlow.Projects;
using DevFlow.Protocol;
using DevFlow.Reviews;

using Npgsql;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>A card taken for a job, with the run made for it.</summary>
/// <param name="RunId">The run's id, the job's.</param>
/// <param name="Number">The card's issue number.</param>
/// <param name="Title">Its title as it was picked.</param>
/// <param name="Body">Its body as it was picked.</param>
/// <param name="Branch">The branch the work goes on, from <see cref="JobStore.Branch"/>.</param>
/// <param name="Comments">The owner's comments that ran it again after it stopped, each exactly as
/// he wrote it; empty for a card that never stopped.</param>
/// <param name="Base">The branch it is made from: its feature's branch for a card of a feature,
/// and otherwise the default branch.</param>
/// <param name="Feature">The feature it is a card of, with what its done cards left, null for a
/// card on its own.</param>
/// <param name="SentBack">What it was sent back with after it landed in its feature's branch, null
/// for a card never sent back.</param>
public sealed record ClaimedCard(Guid RunId, int Number, string Title, string Body, string Branch, IReadOnlyList<string> Comments, string Base, BriefFeature? Feature = null, BriefSentBack? SentBack = null);

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
/// <param name="Earlier">The card's earlier rounds, oldest first, empty on its first review.</param>
/// <param name="Screenshots">The pictures the worker took of the commit, and its note; null when
/// it tried none.</param>
/// <param name="Feature">The feature it is a card of, null for a card on its own.</param>
public sealed record ClaimedReview(Guid RunId, int Number, string Title, string Body, string Branch, string Base, string Commit, string Report, string? Check, int CheckExitCode, string CheckOutput, IReadOnlyList<EarlierRound> Earlier, ResultScreenshots? Screenshots = null, BriefFeature? Feature = null);

/// <summary>An earlier round of a card as stored: a fix that succeeded, the review it answered, and
/// its replies. Its texts are as they were stored.</summary>
/// <param name="Round">The fix round, or null when the owner's comment started it.</param>
/// <param name="ReviewedCommit">The commit the review it answered was for, null without one.</param>
/// <param name="Verdict">That review's verdict, null without one.</param>
/// <param name="Summary">That review's summary, null without one.</param>
/// <param name="Findings">That review's findings as JSON, null without one.</param>
/// <param name="Comments">The owner's comments it answered, as a JSON array.</param>
/// <param name="Replies">The fix report's replies as JSON.</param>
/// <param name="FixedCommit">The commit the fix left.</param>
public sealed record EarlierRound(int? Round, string? ReviewedCommit, string? Verdict, string? Summary, string? Findings, string Comments, string Replies, string FixedCommit);

/// <summary>A card taken for a fix, with the run made for it and what the fix answers.</summary>
/// <param name="RunId">The fix run's id, the job's.</param>
/// <param name="Number">The card's issue number.</param>
/// <param name="Title">Its title as it was picked.</param>
/// <param name="Body">Its body as it was picked.</param>
/// <param name="Branch">The card's branch, its build's.</param>
/// <param name="Base">The branch it was made from, its build's.</param>
/// <param name="FromCommit">The commit the branch is at, which the fix starts from.</param>
/// <param name="Round">The round, 1 to the project's fix rounds, or null when the owner's comment started it.</param>
/// <param name="Report">The report of the newest build or fix that succeeded, as it was stored,
/// null without one.</param>
/// <param name="Review">The review the fix answers, null when there is none.</param>
/// <param name="Comments">The owner's comments the fix answers, each exactly as he wrote it.</param>
/// <param name="Feature">The feature it is a card of, with what its done cards left, null for a
/// card on its own.</param>
public sealed record ClaimedFix(Guid RunId, int Number, string Title, string Body, string Branch, string Base, string FromCommit, int? Round, string? Report, KeptReview? Review, IReadOnlyList<string> Comments, BriefFeature? Feature = null);

/// <summary>A feature taken to resolve its conflict with the default branch, with the run made for
/// it.</summary>
/// <param name="RunId">The conflict run's id, the job's.</param>
/// <param name="Feature">The feature's parent as it was picked, and the handover note of each of
/// its done cards, in its order.</param>
/// <param name="Branch">The feature's branch, which the merge commit goes on.</param>
/// <param name="Base">The default branch, which is brought in.</param>
/// <param name="FeatureCommit">The commit the feature's branch is at, as its watch last named it,
/// which the job starts from.</param>
/// <param name="DefaultCommit">The default branch's commit, as its watch last named it, which the
/// job brings in.</param>
public sealed record ClaimedConflict(Guid RunId, BriefFeature Feature, string Branch, string Base, string FeatureCommit, string DefaultCommit);

/// <summary>A feature taken for its whole review, with the run made for it and what the review
/// reads besides the code.</summary>
/// <param name="RunId">The review run's id, the job's.</param>
/// <param name="Feature">The feature's parent as it was picked; its earlier cards are in
/// <paramref name="Cards"/>.</param>
/// <param name="Branch">The feature's branch.</param>
/// <param name="Base">The default branch, from whose merge base the diff is read.</param>
/// <param name="Commit">The commit to review, the feature branch's head as the minute found it.</param>
/// <param name="Cards">Each card of the feature, in its order, with its latest verdict and its
/// handover note.</param>
/// <param name="Check">The newest check a worker ran on the feature's branch, null without one.</param>
/// <param name="RemergeDiff">The conflicts' remerge diffs since the feature's last review, null
/// without one.</param>
/// <param name="Earlier">The feature's earlier fix rounds, oldest first, empty on its first
/// review.</param>
public sealed record ClaimedFeatureReview(Guid RunId, BriefFeature Feature, string Branch, string Base, string Commit, IReadOnlyList<JobFeatureCard> Cards, JobFeatureCheck? Check, string? RemergeDiff, IReadOnlyList<EarlierRound> Earlier);

/// <summary>A feature taken for a fix of the whole feature, with the run made for it and what the
/// fix answers.</summary>
/// <param name="RunId">The fix run's id, the job's.</param>
/// <param name="Feature">The feature's parent as it was picked, and the handover note of each of its
/// done cards, in its order.</param>
/// <param name="Branch">The feature's branch, which the fix is pushed to.</param>
/// <param name="Base">The default branch, which the feature's branch was made from.</param>
/// <param name="FromCommit">The commit the feature's branch is at, as its watch last named it,
/// which the fix starts from.</param>
/// <param name="Round">The round, 1 to the project's fix rounds, or null when the owner's comment started it.</param>
/// <param name="Report">The report of the feature's newest fix that succeeded, as it was stored,
/// null without one.</param>
/// <param name="Review">The review of the whole feature the fix answers, null when there is
/// none.</param>
/// <param name="Comments">The owner's comments the fix answers, each exactly as he wrote it.</param>
public sealed record ClaimedFeatureFix(Guid RunId, BriefFeature Feature, string Branch, string Base, string FromCommit, int? Round, string? Report, KeptFeatureReview? Review, IReadOnlyList<string> Comments);

/// <summary>The project's first feature to resolve, as read before it is taken: its id, and the
/// commits its job would start from and bring in, which the files in conflict are read
/// between.</summary>
public sealed record DueConflict(Guid Id, string FeatureCommit, string DefaultCommit);

/// <summary>
/// Jobs in the database, through the door as the project's user: which kind of job a project has
/// to hand out, reviews before fixes, a card's before a feature's, before conflicts before builds,
/// taking a card or a feature for
/// one with its lock, and which run a worker holds. See "Jobs", "Review" and "A conflict" in
/// devflow/README.md.
/// </summary>
public static class JobStore
{
    /// <summary>The longest slug a branch takes from its card's title.</summary>
    public const int MaxSlug = 40;

    /// <summary>
    /// What lets a card of a feature start, as SQL over <c>cards</c>, and nothing for a card on its
    /// own: its feature is queued or building, no card of the feature has a run under way, and no
    /// card before it holds it back, <see cref="CardStore.HeldBackBy"/>. So one card of a feature
    /// runs at a time, the first by position free to start, and a second worker asking meanwhile
    /// finds the feature's next card held back by the one it took. See "A feature's cards, one at
    /// a time" in devflow/docs/devflow-architecture.md.
    /// </summary>
    private const string FeatureLetsItStart = $"""
        (cards.feature_id IS NULL OR (
            EXISTS (SELECT 1 FROM features f WHERE f.id = cards.feature_id AND f.state IN ('queued', 'building'))
            AND {CardStore.HeldBackBy} IS NULL
            AND NOT EXISTS (SELECT 1 FROM runs r JOIN cards s ON s.project_id = r.project_id AND s.number = r.card_number
                            WHERE r.project_id = cards.project_id AND s.feature_id = cards.feature_id AND r.finished_at IS NULL)))
        """;

    /// <summary>What makes a card free to start, as SQL over <c>cards</c>: queued, waiting for
    /// nothing, picked with its body kept, with no run under way, and, for a card of a feature,
    /// what <see cref="FeatureLetsItStart"/> says, and its feature's branch watched, by
    /// <see cref="Watching.WatchStore.FeatureWatchedSql"/>, with <c>@watchedSince</c>. A read can leave a card
    /// queued while its run still goes on, when it ignored the card and a later one picked it
    /// again; such a card waits for its run to end, so the unique index <c>runs_one_under_way</c>
    /// stays a backstop rather than the guard.</summary>
    internal const string FreeToStart = $"""
        project_id = @projectId AND state = 'queued' AND cardinality(waits_for) = 0 AND picked_body IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM runs
                        WHERE runs.project_id = cards.project_id AND runs.card_number = cards.number AND runs.finished_at IS NULL)
        AND {FeatureLetsItStart}
        AND {Watching.WatchStore.FeatureWatchedSql}
        """;

    /// <summary>What makes a card one to hand out, as SQL over <c>cards</c>: free to start, and,
    /// for a card of a feature, its feature's branch made, which <see cref="FeatureBranchStore"/>
    /// records before the card is taken.</summary>
    private const string Takeable = $"""
        {FreeToStart}
        AND (cards.feature_id IS NULL
             OR EXISTS (SELECT 1 FROM features f WHERE f.id = cards.feature_id AND f.branch_base_commit IS NOT NULL))
        """;

    /// <summary>
    /// A card's head commit, as SQL over <c>cards</c>: the commit its newest build or fix that
    /// succeeded left on its branch, the one its pull request was opened at or a fix pushed since;
    /// null before one did.
    /// </summary>
    public const string HeadOf = """
        (SELECT b.head_commit FROM runs b
         WHERE b.project_id = cards.project_id AND b.card_number = cards.number AND b.kind IN ('build', 'fix') AND b.result = 'succeeded'
         ORDER BY b.started_at DESC, b.id DESC LIMIT 1)
        """;

    /// <summary>
    /// The commit a card's branch is at, as SQL over <c>cards</c>: the newest one any build or fix
    /// of the card left there, whatever its result, since a worker pushes what its agent made even
    /// when the check failed. A fix starts from it; null before anything was pushed.
    /// </summary>
    public const string BranchHeadOf = """
        (SELECT b.head_commit FROM runs b
         WHERE b.project_id = cards.project_id AND b.card_number = cards.number AND b.kind IN ('build', 'fix') AND b.head_commit IS NOT NULL
         ORDER BY b.started_at DESC, b.id DESC LIMIT 1)
        """;

    /// <summary>What makes a card one to fix, as SQL over <c>cards</c>: fixing, with its pull
    /// request, picked with its body kept, no run under way, a commit on its branch to start
    /// from, and, for a card of a feature, its feature's branch watched.</summary>
    private const string Fixable = $"""
        cards.project_id = @projectId AND cards.state = 'fixing' AND cards.pull_request_number IS NOT NULL
        AND cards.picked_body IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM runs
                        WHERE runs.project_id = cards.project_id AND runs.card_number = cards.number AND runs.finished_at IS NULL)
        AND {BranchHeadOf} IS NOT NULL
        AND {Watching.WatchStore.FeatureWatchedSql}
        """;

    /// <summary>What makes a card one to review, as SQL over <c>cards</c>: reviewing, with its pull
    /// request open, picked with its body kept, no run under way, a head commit no kept review
    /// is for, and, for a card of a feature, its feature's branch watched.</summary>
    private const string Reviewable = $"""
        cards.project_id = @projectId AND cards.state = 'reviewing' AND cards.pull_request_number IS NOT NULL
        AND cards.picked_body IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM runs
                        WHERE runs.project_id = cards.project_id AND runs.card_number = cards.number AND runs.finished_at IS NULL)
        AND {HeadOf} IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM reviews v
                        WHERE v.project_id = cards.project_id AND v.card_number = cards.number AND v.reviewed_commit = {HeadOf})
        AND {Watching.WatchStore.FeatureWatchedSql}
        """;

    /// <summary>
    /// What makes a feature one to resolve, as SQL over <c>features</c>: <c>resolving</c>, its
    /// branch made and picked with its body kept, no run of the feature under way, and its branch's
    /// watch read since <c>@watchedSince</c> and since the feature began resolving, so the commit it
    /// last named holds what Dev flow merged there just before, the card that ended the feature
    /// among it; the commits the job starts from and brings in are that watch's and the default
    /// branch's, <see cref="FeatureHeadOf"/> and <see cref="DefaultHeadOf"/>, each known.
    /// </summary>
    private const string Resolvable = $"""
        features.project_id = @projectId AND features.state = 'resolving' AND features.branch IS NOT NULL
        AND features.branch_base_commit IS NOT NULL AND features.picked_body_sha256 IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM runs
                        WHERE runs.project_id = features.project_id AND runs.feature_number = features.number AND runs.finished_at IS NULL)
        AND EXISTS (SELECT 1 FROM branch_watches wb
                    WHERE wb.project_id = features.project_id AND wb.feature_id = features.id AND wb.last_read_at >= @watchedSince
                      AND wb.last_read_at > features.changed_at)
        AND {FeatureHeadOf} IS NOT NULL
        AND {DefaultHeadOf} IS NOT NULL
        """;

    /// <summary>
    /// What makes a feature one to review as a whole, as SQL over <c>features</c>:
    /// <c>reviewing</c>, with its pull request, its branch made and picked with its body kept, no run
    /// of the feature under way, a commit the minute found no review covering, which is still where
    /// its branch is, <see cref="FeatureHeadOf"/>, with no review kept for it and no review that
    /// failed there, and its branch's watch read since <c>@watchedSince</c>. See "Reviewing a
    /// feature" in devflow/README.md.
    /// </summary>
    private const string FeatureReviewable = $"""
        features.project_id = @projectId AND features.state = 'reviewing' AND features.pull_request_number IS NOT NULL
        AND features.branch IS NOT NULL AND features.branch_base_commit IS NOT NULL AND features.picked_body_sha256 IS NOT NULL
        AND features.review_commit IS NOT NULL AND features.review_commit = {FeatureHeadOf}
        AND features.review_commit IS DISTINCT FROM features.review_failed_at
        AND NOT EXISTS (SELECT 1 FROM runs
                        WHERE runs.project_id = features.project_id AND runs.feature_number = features.number AND runs.finished_at IS NULL)
        AND NOT EXISTS (SELECT 1 FROM feature_reviews v
                        WHERE v.project_id = features.project_id AND v.feature_number = features.number AND v.reviewed_commit = features.review_commit)
        AND EXISTS (SELECT 1 FROM branch_watches wb
                    WHERE wb.project_id = features.project_id AND wb.feature_id = features.id AND wb.last_read_at >= @watchedSince)
        """;

    /// <summary>
    /// What makes a feature one to fix as a whole, as SQL over <c>features</c>: <c>fixing</c>, with
    /// its pull request, its branch made and picked with its body kept, no run of the feature under
    /// way, and its branch's watch read since <c>@watchedSince</c> and since Dev flow's last merge or
    /// push there, so the commit it names, <see cref="FeatureHeadOf"/>, which the fix starts from,
    /// holds what Dev flow put there. See "Fixing a feature" in devflow/README.md.
    /// </summary>
    private const string FeatureFixable = """
        features.project_id = @projectId AND features.state = 'fixing' AND features.pull_request_number IS NOT NULL
        AND features.branch IS NOT NULL AND features.branch_base_commit IS NOT NULL AND features.picked_body_sha256 IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM runs
                        WHERE runs.project_id = features.project_id AND runs.feature_number = features.number AND runs.finished_at IS NULL)
        AND EXISTS (SELECT 1 FROM branch_watches wb
                    WHERE wb.project_id = features.project_id AND wb.feature_id = features.id AND wb.last_read_at >= @watchedSince
                      AND wb.last_commit IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM merges_made m
                                      WHERE m.project_id = features.project_id AND m.branch = features.branch AND m.merged_at >= wb.last_read_at)
                      AND NOT EXISTS (SELECT 1 FROM pushes_made p
                                      WHERE p.project_id = features.project_id AND p.branch = features.branch AND p.recorded_at >= wb.last_read_at))
        """;

    /// <summary>The commit a feature's branch is at, as SQL over <c>features</c>: the one its watch
    /// judged last, or the one it was made at before its watch recorded any.</summary>
    internal const string FeatureHeadOf = """
        coalesce((SELECT wb.last_commit FROM branch_watches wb WHERE wb.project_id = features.project_id AND wb.feature_id = features.id),
                 features.branch_base_commit)
        """;

    /// <summary>The default branch's commit as its watch last named it, as SQL over
    /// <c>features</c>; null before its watch has.</summary>
    private const string DefaultHeadOf = """
        (SELECT wd.last_commit FROM branch_watches wd WHERE wd.project_id = features.project_id AND wd.feature_id IS NULL)
        """;

    /// <summary>
    /// The branch a card's work goes on, <c>devflow/&lt;number&gt;-&lt;slug&gt;</c>: the slug is the
    /// title lowercased, each run of anything but ASCII letters and digits one hyphen, trimmed of
    /// hyphens, cut to <see cref="MaxSlug"/> characters and trimmed again; <c>devflow/&lt;number&gt;</c>
    /// when nothing is left. A card sent back <paramref name="sentBacks"/> times after it landed
    /// takes <c>-&lt;k&gt;</c> after it, <c>k</c> 2 for the first time, 3 for the second, so a branch
    /// that already merged is never used again.
    /// </summary>
    public static string Branch(int number, string title, int sentBacks = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sentBacks);
        var slug = Slug(title);
        var branch = slug.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"devflow/{number}")
            : string.Create(CultureInfo.InvariantCulture, $"devflow/{number}-{slug}");
        return sentBacks == 0 ? branch : string.Create(CultureInfo.InvariantCulture, $"{branch}-{sentBacks + 1}");
    }

    /// <summary>The slug a branch takes from a title, by <see cref="Branch"/>'s rules; empty when
    /// nothing is left.</summary>
    internal static string Slug(string title)
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
        return cut.Length > MaxSlug ? cut[..MaxSlug].TrimEnd('-') : cut;
    }

    /// <summary>The kind of job the project has to hand out now, <see cref="DevFlow.Protocol.Job.Review"/>,
    /// then <see cref="DevFlow.Protocol.Job.FeatureReview"/>, then <see cref="DevFlow.Protocol.Job.Fix"/>,
    /// then <see cref="DevFlow.Protocol.Job.FeatureFix"/>, then <see cref="DevFlow.Protocol.Job.Conflict"/>,
    /// then <see cref="DevFlow.Protocol.Job.Build"/>, so work in progress finishes before new work
    /// starts; null when it has none. A build counts a
    /// card of a feature whose branch is still to be made, which the dispatcher makes first; a card
    /// of a feature whose branch has not been read since five minutes before <paramref name="now"/>
    /// counts for none. Read without a lock, so a worker with nothing to do costs Dev flow one query and GitHub
    /// nothing.</summary>
    public static async Task<string?> NextKind(UserDb database, Guid userId, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM cards WHERE {Reviewable}) THEN 'review'
                        WHEN EXISTS (SELECT 1 FROM features WHERE {FeatureReviewable}) THEN 'feature_review'
                        WHEN EXISTS (SELECT 1 FROM cards WHERE {Fixable}) THEN 'fix'
                        WHEN EXISTS (SELECT 1 FROM features WHERE {FeatureFixable}) THEN 'feature_fix'
                        WHEN EXISTS (SELECT 1 FROM features WHERE {Resolvable}) THEN 'conflict'
                        WHEN EXISTS (SELECT 1 FROM cards WHERE {FreeToStart}) THEN 'build' END
            """,
            new { projectId, watchedSince = Watching.WatchStore.WatchedSince(now) },
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
    public static async Task<ClaimedReview?> ClaimReview(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, RunSettings run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var card = await transaction.Connection!.QuerySingleOrDefaultAsync<ToReview>(new CommandDefinition(
            $"""
            SELECT cards.number AS Number, cards.picked_title AS Title, cards.picked_body AS Body, b.branch AS Branch, b.base AS Base,
                   b.head_commit AS Commit, b.report::text AS Report, b.check_command AS "Check", b.check_exit_code AS CheckExitCode,
                   b.check_output AS CheckOutput, b.screenshots::text AS Screenshots, b.screenshots_note AS ScreenshotsNote
            FROM cards
            JOIN LATERAL (SELECT * FROM runs
                          WHERE runs.project_id = cards.project_id AND runs.card_number = cards.number
                            AND runs.kind IN ('build', 'fix') AND runs.result = 'succeeded'
                          ORDER BY runs.started_at DESC, runs.id DESC LIMIT 1) b ON true
            WHERE {Reviewable} AND b.report IS NOT NULL AND b.check_exit_code IS NOT NULL
            ORDER BY {CardStore.QueueOrder}
            LIMIT 1
            FOR UPDATE OF cards SKIP LOCKED
            """,
            new { projectId, watchedSince = Watching.WatchStore.WatchedSince(now) },
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
        var earlier = await Earlier(transaction, projectId, card.Number, cancellationToken);
        var claimed = new ClaimedReview(
            Guid.CreateVersion7(now), card.Number, card.Title, card.Body, card.Branch, card.Base, card.Commit, card.Report, card.Check, card.CheckExitCode, card.CheckOutput ?? "", earlier,
            Pictures(card.Screenshots, card.ScreenshotsNote), await FeatureOf(transaction, projectId, card.Number, cancellationToken));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, check_command, head_commit, started_at, run_minutes)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'review', @branch, @baseBranch, @check, @commit, @now, @minutes)
            """,
            new { id = claimed.RunId, projectId, userId, number = claimed.Number, workerId, branch = claimed.Branch, baseBranch = claimed.Base, check = claimed.Check, commit = claimed.Commit, now, minutes = run.Minutes },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>
    /// A card's earlier rounds, oldest first, read inside <paramref name="transaction"/>, which runs
    /// as the card's user: each fix of it that succeeded, with the review it answered, if any, and
    /// its report's replies.
    /// </summary>
    public static async Task<IReadOnlyList<EarlierRound>> Earlier(NpgsqlTransaction transaction, Guid projectId, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return [.. await transaction.Connection!.QueryAsync<EarlierRound>(new CommandDefinition(
            """
            SELECT f.fix_round AS Round, v.reviewed_commit AS ReviewedCommit, v.verdict AS Verdict, v.summary AS Summary,
                   v.findings::text AS Findings, coalesce(f.owner_comments, '[]'::jsonb)::text AS Comments,
                   coalesce(f.report->'replies', '[]'::jsonb)::text AS Replies, f.head_commit AS FixedCommit
            FROM runs f
            LEFT JOIN reviews v ON v.project_id = f.project_id AND v.run_id = f.fix_review_id
            WHERE f.project_id = @projectId AND f.card_number = @number AND f.kind = 'fix' AND f.result = 'succeeded'
            ORDER BY f.started_at, f.id
            """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken))];
    }

    /// <summary>
    /// Takes the project's first card to fix, by <see cref="CardStore.QueueOrder"/>, inside
    /// <paramref name="transaction"/>, which runs as the project's user, as <see cref="Claim"/> takes
    /// one to build: locks its row with <c>FOR UPDATE SKIP LOCKED</c>, and records a fix run for the
    /// worker, on the card's branch and base, from the commit the branch is at, with the round, the
    /// review and the owner's comments the card's move to fixing set, and the
    /// <paramref name="check"/> its job carries. The
    /// card stays <c>fixing</c>, the run under way holding it. Null when there is none.
    /// </summary>
    public static async Task<ClaimedFix?> ClaimFix(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, RunSettings run, string check, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(check);
        var card = await transaction.Connection!.QuerySingleOrDefaultAsync<ToFix>(new CommandDefinition(
            $"""
            SELECT cards.number AS Number, cards.picked_title AS Title, cards.picked_body AS Body, b.branch AS Branch, b.base AS Base,
                   {BranchHeadOf} AS FromCommit, cards.fix_round AS Round, cards.fix_review_id AS ReviewId,
                   coalesce(cards.fix_comments, '[]'::jsonb)::text AS Comments,
                   (SELECT r.report::text FROM runs r
                    WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.kind IN ('build', 'fix') AND r.result = 'succeeded'
                    ORDER BY r.started_at DESC, r.id DESC LIMIT 1) AS Report
            FROM cards
            JOIN LATERAL (SELECT branch, base FROM runs
                          WHERE runs.project_id = cards.project_id AND runs.card_number = cards.number AND runs.kind = 'build'
                          ORDER BY runs.started_at DESC, runs.id DESC LIMIT 1) b ON true
            WHERE {Fixable}
            ORDER BY {CardStore.QueueOrder}
            LIMIT 1
            FOR UPDATE OF cards SKIP LOCKED
            """,
            new { projectId, watchedSince = Watching.WatchStore.WatchedSince(now) },
            transaction,
            cancellationToken: cancellationToken));
        if (card is null)
        {
            return null;
        }

        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET reason = @reason, changed_at = @now WHERE project_id = @projectId AND number = @number",
            new { projectId, number = card.Number, reason = FixRounds.Fixing(card.Round, run.FixRoundsCard), now },
            transaction,
            cancellationToken: cancellationToken));
        var review = card.ReviewId is { } reviewId ? await ReviewStore.Of(transaction, reviewId, cancellationToken) : null;
        var comments = JsonSerializer.Deserialize<List<string>>(card.Comments) ?? [];
        var claimed = new ClaimedFix(Guid.CreateVersion7(now), card.Number, card.Title, card.Body, card.Branch, card.Base, card.FromCommit, card.Round, card.Report, review, comments,
            await FeatureOf(transaction, projectId, card.Number, cancellationToken));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, check_command, from_commit, fix_round,
                              fix_review_id, owner_comments, started_at, run_minutes)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'fix', @branch, @baseBranch, @check, @fromCommit, @round, @reviewId,
                    @comments::jsonb, @now, @minutes)
            """,
            new
            {
                minutes = run.Minutes,
                id = claimed.RunId,
                projectId,
                userId,
                number = claimed.Number,
                workerId,
                branch = claimed.Branch,
                baseBranch = claimed.Base,
                check,
                fromCommit = claimed.FromCommit,
                round = claimed.Round,
                reviewId = card.ReviewId,
                comments = comments.Count == 0 ? null : card.Comments,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>The project's first feature to resolve, by <see cref="CardStore.QueueOrder"/>, read
    /// without a lock as the project's user, so the files in conflict can be read from the code host
    /// before anything is taken; null when there is none.</summary>
    public static async Task<DueConflict?> ConflictDue(UserDb database, Guid userId, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<DueConflict>(new CommandDefinition(
            $"""
            SELECT features.id AS Id, {FeatureHeadOf} AS FeatureCommit, {DefaultHeadOf} AS DefaultCommit
            FROM features
            WHERE {Resolvable}
            ORDER BY {CardStore.QueueOrder}
            LIMIT 1
            """,
            new { projectId, watchedSince = Watching.WatchStore.WatchedSince(now) },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>
    /// Takes the feature <paramref name="due"/> names to resolve, while it is still one to resolve at
    /// the same two commits, inside
    /// <paramref name="transaction"/>, which runs as the project's user, as <see cref="Claim"/> takes
    /// a card: locks its row with <c>FOR UPDATE SKIP LOCKED</c>, and records a conflict run for the
    /// worker, for the feature by its number, on its branch with the default branch,
    /// <paramref name="defaultBranch"/>, as its base, from the commit the feature's branch is at,
    /// bringing in the default branch's, with the <paramref name="check"/> its job carries. The
    /// feature stays <c>resolving</c>, the run under way holding it. Null when it is not, when
    /// another transaction holds it, or when either commit has moved since, so the files read for it
    /// are never handed out with other commits.
    /// </summary>
    public static async Task<ClaimedConflict?> ClaimConflict(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, RunSettings run, DueConflict due, string defaultBranch, string check, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(due);
        ArgumentException.ThrowIfNullOrEmpty(defaultBranch);
        ArgumentNullException.ThrowIfNull(check);
        var feature = await transaction.Connection!.QuerySingleOrDefaultAsync<ToResolve>(new CommandDefinition(
            $"""
            SELECT features.id AS Id, features.number AS Number, coalesce(features.picked_title, features.title) AS Title,
                   coalesce(features.picked_body, '') AS Body, features.branch AS Branch,
                   {FeatureHeadOf} AS FeatureCommit, {DefaultHeadOf} AS DefaultCommit
            FROM features
            WHERE {Resolvable} AND features.id = @id AND {FeatureHeadOf} = @featureCommit AND {DefaultHeadOf} = @defaultCommit
            FOR UPDATE OF features SKIP LOCKED
            """,
            new { projectId, watchedSince = Watching.WatchStore.WatchedSince(now), id = due.Id, featureCommit = due.FeatureCommit, defaultCommit = due.DefaultCommit },
            transaction,
            cancellationToken: cancellationToken));
        if (feature is null)
        {
            return null;
        }

        var done = await transaction.Connection!.QueryAsync<BriefHandover>(new CommandDefinition(
            """
            SELECT c.number AS Number, coalesce(c.picked_title, c.title) AS Title,
                   coalesce((SELECT r.report->>'handover' FROM runs r
                             WHERE r.project_id = c.project_id AND r.card_number = c.number AND r.kind IN ('build', 'fix') AND r.result = 'succeeded'
                             ORDER BY r.started_at DESC, r.id DESC LIMIT 1), '') AS Note
            FROM cards c
            WHERE c.project_id = @projectId AND c.feature_id = @id AND c.state = 'done'
            ORDER BY c.position
            """,
            new { projectId, id = feature.Id },
            transaction,
            cancellationToken: cancellationToken));
        var claimed = new ClaimedConflict(
            Guid.CreateVersion7(now), new BriefFeature(feature.Number, feature.Title, feature.Body, [.. done]), feature.Branch, defaultBranch,
            feature.FeatureCommit, feature.DefaultCommit);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, feature_number, worker_id, kind, branch, base, check_command, from_commit,
                              default_commit, started_at, run_minutes)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'conflict', @branch, @baseBranch, @check, @fromCommit, @defaultCommit, @now, @minutes)
            """,
            new
            {
                minutes = run.Minutes,
                id = claimed.RunId,
                projectId,
                userId,
                number = feature.Number,
                workerId,
                branch = claimed.Branch,
                baseBranch = claimed.Base,
                check,
                fromCommit = claimed.FeatureCommit,
                defaultCommit = claimed.DefaultCommit,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>
    /// Takes the project's first feature to review as a whole, by <see cref="CardStore.QueueOrder"/>,
    /// inside <paramref name="transaction"/>, which runs as the project's user, as
    /// <see cref="ClaimReview"/> takes a card: locks its row with <c>FOR UPDATE SKIP LOCKED</c>, and
    /// records a review run for the worker, for the feature by its number, on its branch with the
    /// default branch, <paramref name="defaultBranch"/>, as its base, with the commit it reviews and
    /// the <paramref name="check"/> its job carries. What the review reads is read in the same
    /// transaction: each card in the feature's order with its latest review's verdict and summary and
    /// the handover note of its newest build or fix that succeeded; the newest check a worker ran on
    /// the feature's branch, a card's build or fix that succeeded, which ran on what then landed, or a
    /// conflict's; and the remerge diff of each conflict resolved since the feature's last review.
    /// The feature's earlier fix rounds are read too, by <see cref="FeatureEarlier"/>.
    /// The feature stays <c>reviewing</c>, the run under way holding it. Null when there is none.
    /// </summary>
    public static async Task<ClaimedFeatureReview?> ClaimFeatureReview(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, RunSettings run, string defaultBranch, string check, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrEmpty(defaultBranch);
        ArgumentNullException.ThrowIfNull(check);
        var feature = await transaction.Connection!.QuerySingleOrDefaultAsync<ToReviewFeature>(new CommandDefinition(
            $"""
            SELECT features.id AS Id, features.number AS Number, coalesce(features.picked_title, features.title) AS Title,
                   coalesce(features.picked_body, '') AS Body, features.branch AS Branch, features.review_commit AS Commit
            FROM features
            WHERE {FeatureReviewable}
            ORDER BY {CardStore.QueueOrder}
            LIMIT 1
            FOR UPDATE OF features SKIP LOCKED
            """,
            new { projectId, watchedSince = Watching.WatchStore.WatchedSince(now) },
            transaction,
            cancellationToken: cancellationToken));
        if (feature is null)
        {
            return null;
        }

        var cards = await transaction.Connection!.QueryAsync<JobFeatureCard>(new CommandDefinition(
            """
            SELECT c.number AS Number, coalesce(c.picked_title, c.title) AS Title, v.verdict AS Verdict, v.summary AS Summary,
                   coalesce((SELECT r.report->>'handover' FROM runs r
                             WHERE r.project_id = c.project_id AND r.card_number = c.number AND r.kind IN ('build', 'fix') AND r.result = 'succeeded'
                             ORDER BY r.started_at DESC, r.id DESC LIMIT 1), '') AS Handover
            FROM cards c
            LEFT JOIN LATERAL (SELECT verdict, summary FROM reviews
                               WHERE reviews.project_id = c.project_id AND reviews.card_number = c.number
                               ORDER BY reviewed_at DESC, run_id DESC LIMIT 1) v ON true
            WHERE c.project_id = @projectId AND c.feature_id = @id
            ORDER BY c.position
            """,
            new { projectId, id = feature.Id },
            transaction,
            cancellationToken: cancellationToken));
        var checkRun = await transaction.Connection!.QuerySingleOrDefaultAsync<FeatureCheckRow>(new CommandDefinition(
            """
            SELECT coalesce(r.check_command, '') AS Command, r.head_commit AS Commit, r.check_exit_code AS ExitCode, coalesce(r.check_output, '') AS Output
            FROM runs r
            WHERE r.project_id = @projectId AND r.result = 'succeeded' AND r.check_exit_code IS NOT NULL AND r.head_commit IS NOT NULL
              AND ((r.kind IN ('build', 'fix') AND r.card_number IN (SELECT number FROM cards WHERE project_id = @projectId AND feature_id = @id))
                   OR (r.kind = 'conflict' AND r.feature_number = @number))
            ORDER BY r.finished_at DESC, r.id DESC
            LIMIT 1
            """,
            new { projectId, id = feature.Id, number = feature.Number },
            transaction,
            cancellationToken: cancellationToken));
        var remerges = (await transaction.Connection!.QueryAsync<RemergeRow>(new CommandDefinition(
            """
            SELECT r.id AS RunId, r.head_commit AS Commit, r.default_commit AS DefaultCommit, r.remerge_diff AS Diff
            FROM runs r
            WHERE r.project_id = @projectId AND r.feature_number = @number AND r.kind = 'conflict' AND r.result = 'succeeded'
              AND r.remerge_diff IS NOT NULL
              AND r.started_at > coalesce((SELECT max(v.reviewed_at) FROM feature_reviews v
                                           WHERE v.project_id = r.project_id AND v.feature_number = r.feature_number), '-infinity')
            ORDER BY r.started_at, r.id
            """,
            new { projectId, number = feature.Number },
            transaction,
            cancellationToken: cancellationToken))).ToList();

        var earlier = await FeatureEarlier(transaction, projectId, feature.Number, cancellationToken);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE features SET reason = @reason, changed_at = @now WHERE id = @id",
            new { id = feature.Id, reason = Features.FeatureReview.BeingReviewed, now },
            transaction,
            cancellationToken: cancellationToken));
        var claimed = new ClaimedFeatureReview(
            Guid.CreateVersion7(now),
            new BriefFeature(feature.Number, feature.Title, feature.Body, []),
            feature.Branch,
            defaultBranch,
            feature.Commit,
            [.. cards],
            checkRun is null ? null : new JobFeatureCheck(checkRun.Command, checkRun.Commit, checkRun.ExitCode, checkRun.Output),
            remerges.Count == 0 ? null : RemergeDiffs.Cut(string.Concat(remerges.Select(Remerge))),
            earlier);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, feature_number, worker_id, kind, branch, base, check_command, head_commit, started_at, run_minutes)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'feature_review', @branch, @baseBranch, @check, @commit, @now, @minutes)
            """,
            new { id = claimed.RunId, projectId, userId, number = feature.Number, workerId, branch = claimed.Branch, baseBranch = claimed.Base, check, commit = claimed.Commit, now, minutes = run.Minutes },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>
    /// A feature's earlier fix rounds, oldest first, read inside <paramref name="transaction"/>,
    /// which runs as the feature's user: each fix of the whole feature that succeeded, with the
    /// review of the whole feature it answered, if any, and its report's replies, as
    /// <see cref="Earlier"/> reads a card's.
    /// </summary>
    public static async Task<IReadOnlyList<EarlierRound>> FeatureEarlier(NpgsqlTransaction transaction, Guid projectId, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return [.. await transaction.Connection!.QueryAsync<EarlierRound>(new CommandDefinition(
            """
            SELECT f.fix_round AS Round, v.reviewed_commit AS ReviewedCommit, v.verdict AS Verdict, v.summary AS Summary,
                   v.findings::text AS Findings, coalesce(f.owner_comments, '[]'::jsonb)::text AS Comments,
                   coalesce(f.report->'replies', '[]'::jsonb)::text AS Replies, f.head_commit AS FixedCommit
            FROM runs f
            LEFT JOIN feature_reviews v ON v.project_id = f.project_id AND v.run_id = f.fix_review_id
            WHERE f.project_id = @projectId AND f.feature_number = @number AND f.kind = 'feature_fix' AND f.result = 'succeeded'
            ORDER BY f.started_at, f.id
            """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken))];
    }

    /// <summary>
    /// Takes the project's first feature to fix as a whole, by <see cref="CardStore.QueueOrder"/>,
    /// inside <paramref name="transaction"/>, which runs as the project's user, as
    /// <see cref="ClaimFix"/> takes a card: locks its row with <c>FOR UPDATE SKIP LOCKED</c>, and
    /// records a fix run for the worker, for the feature by its number, on its branch with the
    /// default branch, <paramref name="defaultBranch"/>, as its base, from the commit its watch last
    /// named, with the round, the review and the owner's comments the feature's move to fixing set,
    /// and the <paramref name="check"/> its job carries. What the fix reads is read in the same
    /// transaction: the parent as picked, each done card's handover note in the feature's order, the
    /// report of the feature's newest fix that succeeded and the review it answers. The feature stays
    /// <c>fixing</c>, the run under way holding it. Null when there is none.
    /// </summary>
    public static async Task<ClaimedFeatureFix?> ClaimFeatureFix(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, RunSettings run, string defaultBranch, string check, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrEmpty(defaultBranch);
        ArgumentNullException.ThrowIfNull(check);
        var feature = await transaction.Connection!.QuerySingleOrDefaultAsync<ToFixFeature>(new CommandDefinition(
            $"""
            SELECT features.id AS Id, features.number AS Number, coalesce(features.picked_title, features.title) AS Title,
                   coalesce(features.picked_body, '') AS Body, features.branch AS Branch, {FeatureHeadOf} AS FromCommit,
                   features.next_fix_round AS Round, features.fix_review_id AS ReviewId,
                   coalesce(features.fix_comments, '[]'::jsonb)::text AS Comments,
                   (SELECT r.report::text FROM runs r
                    WHERE r.project_id = features.project_id AND r.feature_number = features.number AND r.kind = 'feature_fix' AND r.result = 'succeeded'
                    ORDER BY r.started_at DESC, r.id DESC LIMIT 1) AS Report
            FROM features
            WHERE {FeatureFixable}
            ORDER BY {CardStore.QueueOrder}
            LIMIT 1
            FOR UPDATE OF features SKIP LOCKED
            """,
            new { projectId, watchedSince = Watching.WatchStore.WatchedSince(now) },
            transaction,
            cancellationToken: cancellationToken));
        if (feature is null)
        {
            return null;
        }

        var done = await transaction.Connection!.QueryAsync<BriefHandover>(new CommandDefinition(
            """
            SELECT c.number AS Number, coalesce(c.picked_title, c.title) AS Title,
                   coalesce((SELECT r.report->>'handover' FROM runs r
                             WHERE r.project_id = c.project_id AND r.card_number = c.number AND r.kind IN ('build', 'fix') AND r.result = 'succeeded'
                             ORDER BY r.started_at DESC, r.id DESC LIMIT 1), '') AS Note
            FROM cards c
            WHERE c.project_id = @projectId AND c.feature_id = @id AND c.state = 'done'
            ORDER BY c.position
            """,
            new { projectId, id = feature.Id },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE features SET reason = @reason, changed_at = @now WHERE id = @id",
            new { id = feature.Id, reason = Features.FeatureFix.Fixing(feature.Round, run.FixRoundsFeature), now },
            transaction,
            cancellationToken: cancellationToken));
        var review = feature.ReviewId is { } reviewId ? await FeatureReviewStore.Of(transaction, reviewId, cancellationToken) : null;
        var comments = JsonSerializer.Deserialize<List<string>>(feature.Comments) ?? [];
        var claimed = new ClaimedFeatureFix(
            Guid.CreateVersion7(now), new BriefFeature(feature.Number, feature.Title, feature.Body, [.. done]), feature.Branch, defaultBranch,
            feature.FromCommit, feature.Round, feature.Report, review, comments);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, feature_number, worker_id, kind, branch, base, check_command, from_commit, fix_round,
                              fix_review_id, owner_comments, started_at, run_minutes)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'feature_fix', @branch, @baseBranch, @check, @fromCommit, @round, @reviewId,
                    @comments::jsonb, @now, @minutes)
            """,
            new
            {
                minutes = run.Minutes,
                id = claimed.RunId,
                projectId,
                userId,
                number = feature.Number,
                workerId,
                branch = claimed.Branch,
                baseBranch = claimed.Base,
                check,
                fromCommit = claimed.FromCommit,
                round = claimed.Round,
                reviewId = feature.ReviewId,
                comments = comments.Count == 0 ? null : feature.Comments,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>One conflict's remerge diff as a whole feature's review reads it, under a line naming
    /// its run and the commits it merged.</summary>
    private static string Remerge(RemergeRow row)
    {
        var diff = row.Diff.EndsWith('\n') ? row.Diff : row.Diff + "\n";
        return string.Create(CultureInfo.InvariantCulture,
            $"# The conflict resolved by run {row.RunId}: {Short(row.DefaultCommit)} brought into the feature's branch as {Short(row.Commit)}\n\n{diff}\n");
    }

    /// <summary>A commit's first seven characters, or "an unknown commit" when there is none.</summary>
    private static string Short(string? commit) => commit is { Length: >= 7 } ? commit[..7] : "an unknown commit";

    /// <summary>
    /// Takes the project's first card to hand out, by <see cref="CardStore.QueueOrder"/>, inside
    /// <paramref name="transaction"/>, which runs as the project's user: locks its row with
    /// <c>FOR UPDATE SKIP LOCKED</c>, moves it to <c>building</c>, and records a run for the worker,
    /// with the <paramref name="check"/> its job carries, made from its feature's branch for a card
    /// of a feature and from <paramref name="baseBranch"/>, the default branch, for one on its own.
    /// Null when there is none, a card another transaction holds included, so two workers asking at
    /// once never get the same card and neither waits for the other. A card sent back after it
    /// landed goes on a new branch, by <see cref="Branch"/>, and carries the pull request it landed
    /// in, the report it landed with and the owner's comments that sent it back.
    /// </summary>
    public static async Task<ClaimedCard?> Claim(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, RunSettings run, string baseBranch, string check, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrEmpty(baseBranch);
        ArgumentNullException.ThrowIfNull(check);
        var card = await transaction.Connection!.QuerySingleOrDefaultAsync<Picked>(new CommandDefinition(
            $"""
            SELECT number AS Number, picked_title AS Title, picked_body AS Body, coalesce(fix_comments, '[]'::jsonb)::text AS Comments,
                   (SELECT f.branch FROM features f WHERE f.id = cards.feature_id) AS FeatureBranch,
                   sent_backs AS SentBacks, sent_back_from AS SentBackFrom, coalesce(sent_back_comments, '[]'::jsonb)::text AS SentBackComments,
                   (SELECT r.report::text FROM runs r
                    WHERE r.project_id = cards.project_id AND r.card_number = cards.number AND r.kind IN ('build', 'fix') AND r.result = 'succeeded'
                    ORDER BY r.started_at DESC, r.id DESC LIMIT 1) AS Report
            FROM cards
            WHERE {Takeable}
            ORDER BY {CardStore.QueueOrder}
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """,
            new { projectId, watchedSince = Watching.WatchStore.WatchedSince(now) },
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
        var comments = JsonSerializer.Deserialize<List<string>>(card.Comments) ?? [];
        var sentBack = card is { SentBacks: > 0, SentBackFrom: { } from }
            ? new BriefSentBack(from, card.Report, JsonSerializer.Deserialize<List<string>>(card.SentBackComments) ?? [])
            : null;
        var claimed = new ClaimedCard(Guid.CreateVersion7(now), card.Number, card.Title, card.Body, Branch(card.Number, card.Title, card.SentBacks), comments, card.FeatureBranch ?? baseBranch,
            await FeatureOf(transaction, projectId, card.Number, cancellationToken), sentBack);
        // The run keeps every comment of his its brief quotes as his input: what sent it back, then
        // what ran it again.
        List<string> input = [.. sentBack?.Comments ?? [], .. comments];
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, card_number, worker_id, kind, branch, base, check_command, owner_comments, started_at, run_minutes)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'build', @branch, @baseBranch, @check, @comments::jsonb, @now, @minutes)
            """,
            new { id = claimed.RunId, projectId, userId, number = claimed.Number, workerId, branch = claimed.Branch, baseBranch = claimed.Base, check, comments = input.Count == 0 ? null : JsonSerializer.Serialize(input), now, minutes = run.Minutes },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>
    /// The feature the card is a card of, as its briefs quote it, read inside
    /// <paramref name="transaction"/>, which runs as the card's user: the parent's number, title and
    /// body as they were picked; and each other card of it that is done, in position order, with its
    /// title as picked and the handover note of its newest build or fix that succeeded, empty when
    /// that report has none. Null for a card on its own.
    /// </summary>
    public static async Task<BriefFeature?> FeatureOf(NpgsqlTransaction transaction, Guid projectId, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var parent = await transaction.Connection!.QuerySingleOrDefaultAsync<FeatureParent>(new CommandDefinition(
            """
            SELECT f.number AS Number, coalesce(f.picked_title, f.title) AS Title, coalesce(f.picked_body, '') AS Body
            FROM cards c JOIN features f ON f.id = c.feature_id
            WHERE c.project_id = @projectId AND c.number = @number
            """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken));
        if (parent is null)
        {
            return null;
        }

        var done = await transaction.Connection!.QueryAsync<BriefHandover>(new CommandDefinition(
            """
            SELECT e.number AS Number, coalesce(e.picked_title, e.title) AS Title,
                   coalesce((SELECT r.report->>'handover' FROM runs r
                             WHERE r.project_id = e.project_id AND r.card_number = e.number AND r.kind IN ('build', 'fix') AND r.result = 'succeeded'
                             ORDER BY r.started_at DESC, r.id DESC LIMIT 1), '') AS Note
            FROM cards c JOIN cards e ON e.project_id = c.project_id AND e.feature_id = c.feature_id
            WHERE c.project_id = @projectId AND c.number = @number AND e.number <> c.number AND e.state = 'done'
            ORDER BY e.position
            """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken));
        return new BriefFeature(parent.Number, parent.Title, parent.Body, [.. done]);
    }

    /// <summary>Whether <paramref name="runId"/> is a build, a fix, a conflict or a feature's fix run of the project,
    /// under way, held by <paramref name="workerId"/>, the kinds of run a push key is made for; read
    /// as the project's user, so another user's run is never found.</summary>
    public static async Task<bool> HoldsBuild(UserDb database, Guid userId, Guid projectId, Guid runId, Guid workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM runs
                           WHERE id = @runId AND project_id = @projectId AND worker_id = @workerId AND kind IN ('build', 'fix', 'conflict', 'feature_fix')
                             AND finished_at IS NULL)
            """,
            new { runId, projectId, workerId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>What a card under review says while its run is under way.</summary>
    public const string BeingReviewed = "The reviewer is reading it";

    private sealed record Picked(int Number, string Title, string Body, string Comments, string? FeatureBranch, int SentBacks, int? SentBackFrom, string SentBackComments, string? Report);

    private sealed record FeatureParent(int Number, string Title, string Body);

    private sealed record ToFixFeature(Guid Id, int Number, string Title, string Body, string Branch, string FromCommit, int? Round, Guid? ReviewId, string Comments, string? Report);

    private sealed record ToReviewFeature(Guid Id, int Number, string Title, string Body, string Branch, string Commit);

    private sealed record FeatureCheckRow(string Command, string Commit, int ExitCode, string Output);

    private sealed record RemergeRow(Guid RunId, string? Commit, string? DefaultCommit, string Diff);

    private sealed record ToResolve(Guid Id, int Number, string Title, string Body, string Branch, string FeatureCommit, string DefaultCommit);

    private sealed record ToFix(int Number, string Title, string Body, string Branch, string Base, string FromCommit, int? Round, Guid? ReviewId, string Comments, string? Report);

    private sealed record ToReview(int Number, string Title, string Body, string Branch, string Base, string Commit, string Report, string? Check, int CheckExitCode, string? CheckOutput, string? Screenshots, string? ScreenshotsNote);

    /// <summary>A run's pictures as it keeps them, or null when it tried none.</summary>
    internal static ResultScreenshots? Pictures(string? names, string? note) =>
        names is null && note is null
            ? null
            : new ResultScreenshots(names is null ? [] : JsonSerializer.Deserialize<List<string>>(names) ?? [], note);
}