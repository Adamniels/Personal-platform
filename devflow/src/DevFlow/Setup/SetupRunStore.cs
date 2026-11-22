using System.Globalization;
using System.Text.Json;

using Dapper;

using DevFlow.Fixing;
using DevFlow.Jobs;
using DevFlow.Projects;
using DevFlow.Protocol;

using Npgsql;

using PlatformData;

namespace DevFlow.Setup;

/// <summary>A setup taken for its first run, or for a fix of its pull request, with the run made
/// for it.</summary>
/// <param name="RunId">The run's id, the job's.</param>
/// <param name="Number">Which setup of the project it is, from 1.</param>
/// <param name="Branch">The setup's branch.</param>
/// <param name="Base">The default branch it is made from.</param>
/// <param name="FromCommit">For a fix, the commit its branch is at, which the fix starts from; null
/// for a first run.</param>
/// <param name="Round">The fix round, or null when the owner's comment started it or it is no
/// fix.</param>
/// <param name="Report">The report of its newest run that succeeded, as stored, null without one.</param>
/// <param name="Review">The review the fix answers, null when there is none.</param>
/// <param name="Comments">The owner's comments it answers, each exactly as he wrote it.</param>
public sealed record ClaimedSetup(Guid RunId, int Number, string Branch, string Base, string? FromCommit, int? Round, string? Report, ReviewVerdict? Review, IReadOnlyList<string> Comments);

/// <summary>A setup taken for its review, with the run made for it and what the review reads.</summary>
/// <param name="RunId">The review run's id, the job's.</param>
/// <param name="Number">Which setup it is.</param>
/// <param name="Branch">The setup's branch.</param>
/// <param name="Base">The default branch.</param>
/// <param name="Commit">The commit to review, the setup's head.</param>
/// <param name="Report">The report of the run that made it, as stored.</param>
/// <param name="ExitCode">Dev flow's validation of what it pushed, 0 when valid.</param>
/// <param name="Output">What the validation said.</param>
/// <param name="PullRequest">Its pull request's number.</param>
/// <param name="Earlier">Its earlier rounds, oldest first.</param>
public sealed record ClaimedSetupReview(Guid RunId, int Number, string Branch, string Base, string Commit, string Report, int ExitCode, string Output, int PullRequest, IReadOnlyList<EarlierRound> Earlier);

/// <summary>The review of a setup's head, as its step shows it.</summary>
/// <param name="Verdict"><c>approve</c> or <c>changes</c>.</param>
/// <param name="Summary">The reviewer's summary, its words and so data.</param>
/// <param name="Blocking">How many of its findings block the commit.</param>
/// <param name="Commit">The commit it is for.</param>
public sealed record SetupReview(string Verdict, string Summary, int Blocking, string Commit);

/// <summary>The project's newest setup, as its step shows it.</summary>
/// <param name="Number">Which setup of the project it is.</param>
/// <param name="Branch">Its branch.</param>
/// <param name="State">queued, checking, reviewing, fixing, waiting_for_owner, stopped or done.</param>
/// <param name="Reason">What it says, why it stopped among them.</param>
/// <param name="PullRequestNumber">Its pull request, null before it is opened.</param>
/// <param name="PullRequestUrl">Where its pull request is.</param>
/// <param name="Running">Whether a run of it is under way.</param>
/// <param name="HeadCommit">The commit its newest run that succeeded pushed, which a merge
/// names.</param>
/// <param name="Report">The newest report in its form, null before one came back.</param>
/// <param name="Review">The review of its head, null without one.</param>
public sealed record SetupRun(int Number, string Branch, string State, string? Reason, int? PullRequestNumber, string? PullRequestUrl, bool Running, string? HeadCommit, SetupReport? Report, SetupReview? Review);

/// <summary>A setup whose pull request is to be opened, or whose body is to be written again.</summary>
public sealed record PendingSetup(int Number, string Branch, string State, int? PullRequestNumber, Guid RunId, string Report, string HeadCommit);

/// <summary>A setup whose pull request is followed on the minute: merged, closed, or commented.</summary>
public sealed record FollowedSetup(int Number, string State, string? Reason, int PullRequestNumber, long? ReadThrough, bool Running, DateTime ChangedAt);

/// <summary>
/// The setup runs of each project, through the door as the project's user: whether its
/// <c>.devflow/</c> is valid as the loop last read it, which of a setup's jobs is due for a worker
/// built from no recipe, taking one, judging what came back, and what its step shows. A setup is
/// reviewed and fixed like a card, and its pull request always waits for the owner. See "Setting up a
/// project" in devflow/README.md, and in devflow/docs/devflow-architecture.md for why.
/// </summary>
public static class SetupRunStore
{
    /// <summary>The title of every setup's pull request.</summary>
    public const string Title = "Set up Dev flow";

    /// <summary>The step of the setup run: <c>.devflow/</c> on the default branch is valid.</summary>
    public const string SetupStep = "setup";

    /// <summary>What a setup to be run says.</summary>
    public const string Queued = "An agent reads the repository and writes .devflow/ next";

    /// <summary>What a setup whose run succeeded and was valid says, until its pull request opens.</summary>
    public const string Opening = "Written, and valid; its pull request opens next";

    /// <summary>What a setup whose pull request is open says until it is reviewed.</summary>
    public const string ToReviewReason = "Its pull request is open; the reviewer reads it next";

    /// <summary>What an approved setup says: it always waits for the owner.</summary>
    public const string Approved = "Approved; it waits for you to merge it";

    /// <summary>What a setup whose pull request was closed without merging says.</summary>
    public const string Closed = "The pull request was closed";

    /// <summary>What a setup the owner commented on says until its run begins.</summary>
    public const string CommentedReason = "You commented on its pull request; it runs again next";

    /// <summary>What a setup the owner commented on from its step says until its run begins.</summary>
    public const string RunAgainReason = "You commented on it; it runs again next";

    /// <summary>What a merged setup says.</summary>
    public const string Merged = "Merged; .devflow/ is on the default branch";

    /// <summary>The kinds of a setup's runs in the database: its first run or a fix, and its
    /// review. On the wire a setup's review is a <see cref="Job.Review"/>.</summary>
    public const string SetupKind = "setup";

    public const string ReviewKind = "setup_review";

    /// <summary>Whether a run of setup <c>s</c> is under way, as SQL.</summary>
    private const string Running = """
        EXISTS (SELECT 1 FROM runs r WHERE r.project_id = s.project_id AND r.setup_number = s.number AND r.finished_at IS NULL)
        """;

    /// <summary>The commit setup <c>s</c>'s newest run that succeeded pushed, as SQL; null before one
    /// did.</summary>
    internal const string HeadOf = """
        (SELECT b.head_commit FROM runs b
         WHERE b.project_id = s.project_id AND b.setup_number = s.number AND b.kind = 'setup' AND b.result = 'succeeded'
         ORDER BY b.started_at DESC, b.id DESC LIMIT 1)
        """;

    /// <summary>The commit setup <c>s</c>'s branch is at, as SQL: the newest any of its runs pushed,
    /// whatever its result, since a worker pushes what its agent made; a fix starts from it.</summary>
    private const string BranchHeadOf = """
        (SELECT b.head_commit FROM runs b
         WHERE b.project_id = s.project_id AND b.setup_number = s.number AND b.kind = 'setup' AND b.head_commit IS NOT NULL
         ORDER BY b.started_at DESC, b.id DESC LIMIT 1)
        """;

    /// <summary>Whether project <c>@projectId</c> needs a setup, as SQL: its <c>.devflow/</c> was read
    /// and is not valid. Before the loop has read it, nothing is handed out on a guess.</summary>
    private const string NeedsSetup = "EXISTS (SELECT 1 FROM projects p WHERE p.id = @projectId AND p.devflow_valid IS FALSE)";

    private const string Reviewable = $"""
        s.project_id = @projectId AND s.state = 'reviewing' AND s.pull_request_number IS NOT NULL AND NOT {Running}
        AND {HeadOf} IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM runs v WHERE v.project_id = s.project_id AND v.setup_number = s.number
                        AND v.kind = 'setup_review' AND v.result = 'succeeded' AND v.head_commit = {HeadOf})
        """;

    private const string Fixable = $"s.project_id = @projectId AND s.state = 'fixing' AND NOT {Running} AND {BranchHeadOf} IS NOT NULL";

    private const string Runnable = $"s.project_id = @projectId AND s.state = 'queued' AND NOT {Running}";

    /// <summary>The branch of the project's <paramref name="number"/>th setup: <c>devflow/setup</c>,
    /// then <c>devflow/setup-&lt;k&gt;</c>, so a branch whose pull request merged is never used
    /// again.</summary>
    public static string Branch(int number) =>
        number == 1 ? "devflow/setup" : string.Create(CultureInfo.InvariantCulture, $"devflow/setup-{number}");

    /// <summary>Records what the loop read of the project's <c>.devflow/</c>: valid when
    /// <paramref name="problem"/> is null, and otherwise why not.</summary>
    public static async Task DevflowRead(UserDb database, Guid userId, Guid projectId, string? problem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE projects SET devflow_valid = @valid, devflow_problem = @problem
            WHERE id = @projectId AND (devflow_valid IS DISTINCT FROM @valid OR devflow_problem IS DISTINCT FROM @problem)
            """,
            new { projectId, valid = problem is null, problem },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>
    /// The setup job due for the project's worker built from no recipe, in the order a card's are:
    /// a review, <see cref="ReviewKind"/>, then a fix, <see cref="Job.Fix"/>, then a run,
    /// <see cref="SetupKind"/>, of a setup queued or, when the project has none that is not done, a
    /// new one. Null when the project needs no setup or has nothing of one to hand out. Read without
    /// a lock, as <see cref="JobStore.NextKind"/> is.
    /// </summary>
    public static async Task<string?> NextKind(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"""
            SELECT CASE WHEN NOT {NeedsSetup} THEN NULL
                        WHEN EXISTS (SELECT 1 FROM setups s WHERE {Reviewable}) THEN '{ReviewKind}'
                        WHEN EXISTS (SELECT 1 FROM setups s WHERE {Fixable}) THEN '{Job.Fix}'
                        WHEN EXISTS (SELECT 1 FROM setups s WHERE {Runnable}) THEN '{SetupKind}'
                        WHEN NOT EXISTS (SELECT 1 FROM setups s WHERE s.project_id = @projectId AND s.state <> 'done') THEN '{SetupKind}' END
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>
    /// Takes the project's setup to run, inside <paramref name="transaction"/>, which runs as the
    /// project's user under the project's lock: the one queued, or a new one when it has none that
    /// is not done, numbered after the last; and records a <see cref="SetupKind"/> run for the worker
    /// on its branch, from <paramref name="defaultBranch"/>, with the owner's comments it was queued
    /// with. Null when there is none to take.
    /// </summary>
    public static async Task<ClaimedSetup?> ClaimRun(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, RunSettings run, string defaultBranch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(run);
        var setup = await transaction.Connection!.QuerySingleOrDefaultAsync<(int Number, string Branch, string Comments)?>(new CommandDefinition(
            $"""
            SELECT s.number, s.branch, coalesce(s.fix_comments, '[]'::jsonb)::text FROM setups s
            WHERE {Runnable} AND {NeedsSetup}
            ORDER BY s.number LIMIT 1 FOR UPDATE OF s SKIP LOCKED
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        if (setup is null)
        {
            var number = await transaction.Connection!.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                $"""
                INSERT INTO setups (project_id, user_id, number, branch, state, reason, created_at, changed_at)
                SELECT @projectId, @userId, n, CASE WHEN n = 1 THEN 'devflow/setup' ELSE 'devflow/setup-' || n END, 'queued', @reason, @now, @now
                FROM (SELECT coalesce(max(number), 0) + 1 AS n FROM setups WHERE project_id = @projectId) next
                WHERE {NeedsSetup} AND NOT EXISTS (SELECT 1 FROM setups WHERE project_id = @projectId AND state <> 'done')
                ON CONFLICT (project_id) WHERE state <> 'done' DO NOTHING
                RETURNING number
                """,
                new { projectId, userId, reason = Queued, now },
                transaction,
                cancellationToken: cancellationToken));
            if (number is not { } made)
            {
                return null;
            }

            setup = (made, Branch(made), "[]");
        }

        var (taken, branch, comments) = setup.Value;
        var said = JsonSerializer.Deserialize<List<string>>(comments) ?? [];
        var claimed = new ClaimedSetup(Guid.CreateVersion7(now), taken, branch, defaultBranch, null, null, null, null, said);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, setup_number, worker_id, kind, branch, base, check_command, owner_comments, started_at, run_minutes)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'setup', @branch, @baseBranch, @check, @comments::jsonb, @now, @minutes)
            """,
            new { id = claimed.RunId, projectId, userId, number = taken, workerId, branch, baseBranch = defaultBranch, check = SetupFiles.Validation, comments = said.Count == 0 ? null : comments, now, minutes = run.Minutes },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>
    /// Takes the project's setup to fix, as a card's fix is taken: fixing, no run under way, a commit
    /// on its branch to start from; and records a <see cref="SetupKind"/> run from that commit with
    /// the round, the review and the owner's comments its move to fixing set. Null when there is none.
    /// </summary>
    public static async Task<ClaimedSetup?> ClaimFix(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, RunSettings run, string defaultBranch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(run);
        var setup = await transaction.Connection!.QuerySingleOrDefaultAsync<ToFix>(new CommandDefinition(
            $"""
            SELECT s.number AS Number, s.branch AS Branch, {BranchHeadOf} AS FromCommit, s.fix_round AS Round, s.fix_review_id AS ReviewId,
                   coalesce(s.fix_comments, '[]'::jsonb)::text AS Comments,
                   (SELECT r.report::text FROM runs r WHERE r.project_id = s.project_id AND r.setup_number = s.number AND r.kind = 'setup'
                    AND r.result = 'succeeded' ORDER BY r.started_at DESC, r.id DESC LIMIT 1) AS Report,
                   (SELECT v.report::text FROM runs v WHERE v.project_id = s.project_id AND v.id = s.fix_review_id) AS Verdict
            FROM setups s
            WHERE {Fixable} AND {NeedsSetup}
            ORDER BY s.number LIMIT 1 FOR UPDATE OF s SKIP LOCKED
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        if (setup is null)
        {
            return null;
        }

        var comments = JsonSerializer.Deserialize<List<string>>(setup.Comments) ?? [];
        ReviewVerdict? review = null;
        if (setup.Verdict is { } verdict)
        {
            using var document = JsonDocument.Parse(verdict);
            review = ReviewVerdict.TryRead(document.RootElement, out var read, out _) ? read : null;
        }

        var claimed = new ClaimedSetup(Guid.CreateVersion7(now), setup.Number, setup.Branch, defaultBranch, setup.FromCommit, setup.Round, setup.Report, review, comments);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE setups SET reason = @reason, changed_at = @now WHERE project_id = @projectId AND number = @number
            """,
            new { projectId, number = setup.Number, reason = FixRounds.Fixing(setup.Round, run.FixRoundsCard), now },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO runs (id, project_id, user_id, setup_number, worker_id, kind, branch, base, check_command, from_commit, fix_round,
                              fix_review_id, owner_comments, started_at, run_minutes)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'setup', @branch, @baseBranch, @check, @fromCommit, @round, @reviewId,
                    @comments::jsonb, @now, @minutes)
            """,
            new
            {
                id = claimed.RunId,
                projectId,
                userId,
                number = setup.Number,
                workerId,
                branch = setup.Branch,
                baseBranch = defaultBranch,
                check = SetupFiles.Validation,
                fromCommit = setup.FromCommit,
                round = setup.Round,
                reviewId = setup.ReviewId,
                comments = comments.Count == 0 ? null : setup.Comments,
                now,
                minutes = run.Minutes,
            },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>
    /// Takes the project's setup to review: reviewing, its pull request open, no run under way, and
    /// a head no review that succeeded is for; and records a <see cref="ReviewKind"/> run of that
    /// commit, with the report of the run that made it and its validation. Null when there is none.
    /// </summary>
    public static async Task<ClaimedSetupReview?> ClaimReview(NpgsqlTransaction transaction, Guid projectId, Guid userId, Guid workerId, RunSettings run, string defaultBranch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(run);
        var setup = await transaction.Connection!.QuerySingleOrDefaultAsync<ToReview>(new CommandDefinition(
            $"""
            SELECT s.number AS Number, s.branch AS Branch, s.pull_request_number AS PullRequest, b.head_commit AS Commit,
                   b.report::text AS Report, b.check_exit_code AS ExitCode, b.check_output AS Output
            FROM setups s
            JOIN LATERAL (SELECT * FROM runs WHERE runs.project_id = s.project_id AND runs.setup_number = s.number
                          AND runs.kind = 'setup' AND runs.result = 'succeeded' ORDER BY runs.started_at DESC, runs.id DESC LIMIT 1) b ON true
            WHERE {Reviewable} AND {NeedsSetup} AND b.report IS NOT NULL
            ORDER BY s.number LIMIT 1 FOR UPDATE OF s SKIP LOCKED
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        if (setup is null)
        {
            return null;
        }

        var earlier = (await transaction.Connection!.QueryAsync<EarlierRound>(new CommandDefinition(
            """
            SELECT f.fix_round AS Round, v.head_commit AS ReviewedCommit, v.report->>'verdict' AS Verdict, v.report->>'summary' AS Summary,
                   (v.report->'findings')::text AS Findings, coalesce(f.owner_comments, '[]'::jsonb)::text AS Comments,
                   '[]' AS Replies, f.head_commit AS FixedCommit
            FROM runs f
            LEFT JOIN runs v ON v.project_id = f.project_id AND v.id = f.fix_review_id
            WHERE f.project_id = @projectId AND f.setup_number = @number AND f.kind = 'setup' AND f.from_commit IS NOT NULL AND f.result = 'succeeded'
            ORDER BY f.started_at, f.id
            """,
            new { projectId, number = setup.Number },
            transaction,
            cancellationToken: cancellationToken))).ToList();
        var claimed = new ClaimedSetupReview(Guid.CreateVersion7(now), setup.Number, setup.Branch, defaultBranch, setup.Commit, setup.Report, setup.ExitCode ?? 0, setup.Output ?? "", setup.PullRequest, earlier);
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE setups SET reason = @reason, changed_at = @now WHERE project_id = @projectId AND number = @number;
            INSERT INTO runs (id, project_id, user_id, setup_number, worker_id, kind, branch, base, check_command, head_commit, started_at, run_minutes)
            VALUES (@id, @projectId, @userId, @number, @workerId, 'setup_review', @branch, @baseBranch, @check, @commit, @now, @minutes)
            """,
            new { id = claimed.RunId, projectId, userId, number = setup.Number, workerId, branch = setup.Branch, baseBranch = defaultBranch, check = SetupFiles.Validation, commit = setup.Commit, now, minutes = run.Minutes, reason = JobStore.BeingReviewed },
            transaction,
            cancellationToken: cancellationToken));
        return claimed;
    }

    /// <summary>
    /// How a setup's run ends, as a card's of the same kind does, its <see cref="RunEnding.CardState"/>
    /// the state the setup moves to. A run, the first or a fix: a failure outside puts it back,
    /// queued or fixing; a failure in it, a report out of its form, nothing pushed, a fix that left no
    /// new commit, and what it pushed not valid, judged as a check that failed, each stop it, all but
    /// a time limit only as the second in a row; anything else succeeded, a first run's pull request
    /// opening next and a fix's going back to review. A review: a failure outside leaves it
    /// reviewing; a failure in it, or a verdict out of its form, stops it, as a card's; changes start
    /// the next fix round, or stop it once its rounds are used; and approve has it wait for the owner,
    /// always.
    /// </summary>
    public static RunEnding Judge(string kind, RunResult result, string? fromCommit, int roundsUsed, int mostRounds)
    {
        ArgumentNullException.ThrowIfNull(result);
        var back = fromCommit is null ? "queued" : "fixing";
        if (kind == ReviewKind)
        {
            if (!result.Succeeded)
            {
                var why = RunStore.Cut(result.FailureReason ?? "");
                return result.FailureInCard
                    ? new RunEnding("failed_in_card", RunStore.Cut($"The review failed: {why}"), "stopped", Retry: !result.TimedOut)
                    : new RunEnding("failed_outside_card", why, "reviewing");
            }

            if (!ReviewVerdict.TryRead(result.Report ?? default, out var verdict, out var wrong))
            {
                return new RunEnding("report_invalid", RunStore.Cut($"The verdict did not match its form: {wrong}"), "stopped", Retry: true);
            }

            return verdict.Verdict != ReviewVerdictKind.Changes ? new RunEnding("succeeded", null, "waiting_for_owner", Approved)
                : roundsUsed >= mostRounds ? new RunEnding("succeeded", null, "stopped", FixRounds.UsedUp)
                : new RunEnding("succeeded", null, "fixing", FixRounds.Asked(roundsUsed + 1, mostRounds));
        }

        if (!result.Succeeded)
        {
            var why = RunStore.Cut(result.FailureReason ?? "");
            return result.FailureInCard
                ? new RunEnding("failed_in_card", RunStore.Cut($"The setup failed: {why}"), "stopped", Retry: !result.TimedOut)
                : new RunEnding("failed_outside_card", why, back);
        }

        if (!SetupReport.TryRead(result.Report ?? default, out _, out var error))
        {
            return new RunEnding("report_invalid", RunStore.Cut($"The report did not match its form: {error}"), "stopped", Retry: true);
        }

        if (result.HeadCommit is null)
        {
            return new RunEnding("failed_in_card", "Nothing was pushed to the setup's branch", "stopped", Retry: true);
        }

        if (fromCommit is not null && string.Equals(result.HeadCommit, fromCommit, StringComparison.Ordinal))
        {
            return new RunEnding("failed_in_card", FixRounds.NoNewCommit, "stopped", Retry: true);
        }

        return result.CheckExitCode switch
        {
            null => new RunEnding("check_failed", "What it wrote was not validated", "stopped", Retry: true),
            0 => new RunEnding("succeeded", null, fromCommit is null ? "checking" : "reviewing"),
            _ => new RunEnding("check_failed", RunStore.Cut($"What it wrote is not valid: {result.CheckOutput}"), "stopped", Retry: true),
        };
    }

    /// <summary>
    /// Moves the setup its run ended, inside the transaction that records the result, as a card's
    /// run moves its card: a failure that runs it again once does so, the second in a row stopping
    /// it, and a success starts the count afresh; changes start the round the review asked for; a
    /// run that succeeded clears the comments it answered; and a setup whose pull request is not
    /// open, as after one closed, opens one again rather than going to review. A merged setup is not
    /// moved, nor one whose pull request was closed while the run was under way: it stays stopped
    /// until the owner's comment on that pull request runs it again.
    /// </summary>
    internal static async Task End(NpgsqlTransaction transaction, Guid projectId, int number, Guid runId, string kind, string? fromCommit, int roundsUsed, RunEnding ending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (state, reason) = (ending.CardState, ending.CardReason ?? ending.Reason);
        var failures = 0;
        if (ending.Retry)
        {
            var before = await transaction.Connection!.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT failures FROM setups WHERE project_id = @projectId AND number = @number",
                new { projectId, number },
                transaction,
                cancellationToken: cancellationToken));
            if (before == 0)
            {
                (state, reason, failures) = (kind == ReviewKind ? "reviewing" : fromCommit is null ? "queued" : "fixing", RunStore.Cut($"Failed once, so it runs again: {ending.Reason}"), 1);
            }
            else
            {
                reason = RunStore.Cut($"Failed twice in a row: {ending.Reason}");
            }
        }
        else if (ending.Result != "succeeded")
        {
            // A failure outside, or a time limit: the count is left as it was.
            failures = -1;
        }

        reason ??= state switch
        {
            "queued" => Queued,
            "checking" => Opening,
            "reviewing" => ToReviewReason,
            "fixing" => CommentedReason,
            _ => null,
        };
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE setups SET
                state = CASE WHEN @state IN ('checking', 'reviewing') AND pull_request_number IS NULL THEN 'checking'
                             WHEN @state = 'checking' THEN 'reviewing' ELSE @state END,
                reason = CASE WHEN @state IN ('checking', 'reviewing') AND pull_request_number IS NULL THEN @opening
                              WHEN @state = 'checking' THEN @toReview ELSE @reason END,
                failures = CASE WHEN @failures < 0 THEN failures ELSE @failures END,
                fix_rounds = CASE WHEN @changes THEN @round ELSE fix_rounds END,
                fix_round = CASE WHEN @changes THEN @round ELSE fix_round END,
                fix_review_id = CASE WHEN @changes THEN @runId ELSE fix_review_id END,
                fix_comments = CASE WHEN @changes OR (@succeeded AND @kind = 'setup') THEN NULL ELSE fix_comments END,
                changed_at = @now
            WHERE project_id = @projectId AND number = @number AND state <> 'done'
              AND NOT (state = 'stopped' AND reason = @closed AND pull_request_number IS NOT NULL)
            """,
            new
            {
                projectId,
                number,
                closed = Closed,
                state,
                reason,
                failures,
                changes = kind == ReviewKind && ending.Result == "succeeded" && ending.CardState == "fixing",
                round = roundsUsed + 1,
                runId,
                succeeded = ending.Result == "succeeded",
                kind,
                opening = Opening,
                toReview = ToReviewReason,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>The setups of the project whose pull request is to be opened, one written and valid,
    /// and those whose body was written from an older run than their newest that succeeded. Each
    /// with that run's report, as stored. A setup that stopped before it had a pull request gets
    /// none: it is run again from its step, see <see cref="RunAgain"/>.</summary>
    public static async Task<IReadOnlyList<PendingSetup>> Pending(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<PendingSetup>(new CommandDefinition(
            """
            SELECT s.number AS Number, s.branch AS Branch, s.state AS State, s.pull_request_number AS PullRequestNumber,
                   b.id AS RunId, b.report::text AS Report, b.head_commit AS HeadCommit
            FROM setups s
            JOIN LATERAL (SELECT * FROM runs WHERE runs.project_id = s.project_id AND runs.setup_number = s.number AND runs.kind = 'setup'
                          AND runs.report IS NOT NULL AND runs.head_commit IS NOT NULL AND runs.result = 'succeeded'
                          ORDER BY runs.started_at DESC, runs.id DESC LIMIT 1) b ON true
            WHERE s.project_id = @projectId
              AND ((s.pull_request_number IS NULL AND s.state = 'checking')
                   OR (s.pull_request_number IS NOT NULL AND s.state IN ('reviewing', 'fixing', 'waiting_for_owner') AND s.body_run_id IS DISTINCT FROM b.id))
            ORDER BY s.number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)];
    }

    /// <summary>Records the setup's pull request, opened or found, with the run its body was written
    /// from; the setup, written and valid, moves to review.</summary>
    public static async Task Opened(UserDb database, Guid userId, Guid projectId, int number, int pullRequest, Uri url, Guid bodyRunId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(url);
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE setups SET pull_request_number = @pullRequest, pull_request_url = @url, body_run_id = @bodyRunId,
                              state = 'reviewing', reason = @toReview, changed_at = @now
            WHERE project_id = @projectId AND number = @number AND state = 'checking' AND pull_request_number IS NULL
            """,
            new { projectId, number, pullRequest, url = url.ToString(), bodyRunId, toReview = ToReviewReason, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>Records that the setup's pull request body was written again from
    /// <paramref name="bodyRunId"/>'s report.</summary>
    public static async Task Rewritten(UserDb database, Guid userId, Guid projectId, int number, Guid bodyRunId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE setups SET body_run_id = @bodyRunId WHERE project_id = @projectId AND number = @number",
            new { projectId, number, bodyRunId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>The project's setups with a pull request that is followed: every one not done.</summary>
    public static async Task<IReadOnlyList<FollowedSetup>> Followed(UserDb database, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return [.. await database.InUserTransaction(userId, transaction => transaction.Connection!.QueryAsync<FollowedSetup>(new CommandDefinition(
            $"""
            SELECT s.number AS Number, s.state AS State, s.reason AS Reason, s.pull_request_number AS PullRequestNumber,
                   s.comments_read_through AS ReadThrough, {Running} AS Running, s.changed_at AS ChangedAt
            FROM setups s
            WHERE s.project_id = @projectId AND s.pull_request_number IS NOT NULL AND s.state <> 'done'
            ORDER BY s.number
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken)];
    }

    /// <summary>
    /// What the setup's pull request came to: merged, by the owner from the page or on GitHub, the
    /// setup is done; closed without merging, it stops, and its pull request is forgotten so the
    /// owner's comment on it opens a new one from the same branch.
    /// </summary>
    public static async Task Ended(UserDb database, Guid userId, Guid projectId, int number, bool merged, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE setups SET state = CASE WHEN @merged THEN 'done' ELSE 'stopped' END,
                              reason = CASE WHEN @merged THEN @mergedReason ELSE @closedReason END,
                              changed_at = @now
            WHERE project_id = @projectId AND number = @number AND state <> 'done'
            """,
            new { projectId, number, merged, mergedReason = Merged, closedReason = Closed, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    /// <summary>
    /// Takes the owner's comments on a setup's pull request, those after <paramref name="through"/>:
    /// a setup waiting for him or stopped, with no run under way, runs again with them, as a fix of
    /// its branch that is not a round, or a first run again when nothing was ever pushed. A setup
    /// whose pull request was closed forgets it, so the run opens a new one. Comments while it runs
    /// or is reviewed are only marked read.
    /// </summary>
    public static async Task Commented(UserDb database, Guid userId, Guid projectId, int number, IReadOnlyList<string> comments, long through, bool closed, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(comments);
        await database.InUserTransaction(userId, async transaction =>
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "SELECT 1 FROM setups WHERE project_id = @projectId AND number = @number FOR UPDATE",
                new { projectId, number },
                transaction,
                cancellationToken: cancellationToken));
            return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                $"""
                UPDATE setups s SET
                    state = CASE WHEN {BranchHeadOf} IS NULL THEN 'queued' ELSE 'fixing' END,
                    reason = @reason, fix_round = NULL, fix_review_id = NULL, failures = 0,
                    fix_comments = coalesce(s.fix_comments, '[]'::jsonb) || @comments::jsonb,
                    pull_request_number = CASE WHEN @closed THEN NULL ELSE s.pull_request_number END,
                    pull_request_url = CASE WHEN @closed THEN NULL ELSE s.pull_request_url END,
                    comments_read_through = @through, changed_at = @now
                WHERE s.project_id = @projectId AND s.number = @number AND s.state IN ('waiting_for_owner', 'stopped') AND NOT {Running}
                  AND (s.comments_read_through IS NULL OR s.comments_read_through < @through)
                """,
                new { projectId, number, reason = CommentedReason, comments = JsonSerializer.Serialize(comments), closed, through, now },
                transaction,
                cancellationToken: cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// The owner's comment from the step of a setup that stopped before it had a pull request, the
    /// one he would otherwise write on it: the setup runs again with it, as <see cref="Commented"/>
    /// runs one again, a fix of its branch that is not a round, or a first run again when nothing was
    /// ever pushed. Only while it is stopped with no pull request and no run under way, and its
    /// project still needs a setup. Whether it was taken.
    /// </summary>
    public static async Task<bool> RunAgain(UserDb database, Guid userId, Guid projectId, string comment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(comment);
        return await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE setups s SET
                state = CASE WHEN {BranchHeadOf} IS NULL THEN 'queued' ELSE 'fixing' END,
                reason = @reason, fix_round = NULL, fix_review_id = NULL, failures = 0,
                fix_comments = coalesce(s.fix_comments, '[]'::jsonb) || @comments::jsonb,
                changed_at = @now
            WHERE s.project_id = @projectId AND s.state = 'stopped' AND s.pull_request_number IS NULL AND NOT {Running} AND {NeedsSetup}
            """,
            new { projectId, reason = RunAgainReason, comments = JsonSerializer.Serialize(new[] { comment }), now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken) == 1;
    }

    /// <summary>The project's newest setup as its step shows it, or null before it has one, read
    /// inside <paramref name="transaction"/> as the project's user.</summary>
    internal static async Task<SetupRun?> Newest(NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        var row = await transaction.Connection!.QuerySingleOrDefaultAsync<NewestRow>(new CommandDefinition(
            $"""
            SELECT s.number AS Number, s.branch AS Branch, s.state AS State, s.reason AS Reason, s.pull_request_number AS PullRequestNumber,
                   s.pull_request_url AS PullRequestUrl, {Running} AS Running, {HeadOf} AS HeadCommit,
                   (SELECT r.report::text FROM runs r WHERE r.project_id = s.project_id AND r.setup_number = s.number AND r.kind = 'setup'
                    AND r.report IS NOT NULL ORDER BY r.started_at DESC, r.id DESC LIMIT 1) AS Report,
                   (SELECT v.report::text FROM runs v WHERE v.project_id = s.project_id AND v.setup_number = s.number AND v.kind = 'setup_review'
                    AND v.result = 'succeeded' AND v.head_commit = {HeadOf} ORDER BY v.started_at DESC, v.id DESC LIMIT 1) AS Verdict
            FROM setups s WHERE s.project_id = @projectId ORDER BY s.number DESC LIMIT 1
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        SetupReport? report = null;
        if (row.Report is { } text)
        {
            using var document = JsonDocument.Parse(text);
            report = SetupReport.TryRead(document.RootElement, out var read, out _) ? read : null;
        }

        SetupReview? review = null;
        if (row.Verdict is { } verdict && row.HeadCommit is { } head)
        {
            using var document = JsonDocument.Parse(verdict);
            if (ReviewVerdict.TryRead(document.RootElement, out var read, out _))
            {
                review = new SetupReview(read.Verdict == ReviewVerdictKind.Approve ? "approve" : "changes", read.Summary, read.Findings.Count(finding => finding.Blocking), head);
            }
        }

        return new SetupRun(row.Number, row.Branch, row.State, row.Reason, row.PullRequestNumber, row.PullRequestUrl, row.Running, row.HeadCommit, report, review);
    }

    private sealed record ToFix(int Number, string Branch, string FromCommit, int? Round, Guid? ReviewId, string Comments, string? Report, string? Verdict);

    private sealed record ToReview(int Number, string Branch, int PullRequest, string Commit, string Report, int? ExitCode, string? Output);

    private sealed record NewestRow(int Number, string Branch, string State, string? Reason, int? PullRequestNumber, string? PullRequestUrl, bool Running, string? HeadCommit, string? Report, string? Verdict);
}