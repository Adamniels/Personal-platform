using System.Globalization;
using System.Text.Json;

using Dapper;

using DevFlow.Fixing;
using DevFlow.Protocol;
using DevFlow.PullRequests;
using DevFlow.Reviews;

using Npgsql;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>A result a worker sent, its rules checked: what <see cref="RunStore.Record"/> stores
/// and judges.</summary>
/// <param name="Succeeded">Whether the agent answered a report.</param>
/// <param name="Report">The report as it came, on a success.</param>
/// <param name="FailureReason">Why the run failed, on a failure.</param>
/// <param name="FailureInCard">Whether that lies in the card, on a failure.</param>
/// <param name="CheckExitCode">The check's exit code, null when it did not run.</param>
/// <param name="CheckOutput">The last lines of its output, null when it did not run.</param>
/// <param name="PermissionDenials">The actions the agent was refused, an array as Claude Code
/// listed them, null when the worker sent none.</param>
public sealed record RunResult(
    bool Succeeded,
    JsonElement? Report,
    string? FailureReason,
    bool FailureInCard,
    int? CheckExitCode,
    string? CheckOutput,
    string Branch,
    string? HeadCommit,
    string? AuthMethod,
    JsonElement Usage,
    long DurationMs,
    string Stderr,
    JsonElement? PermissionDenials = null);

/// <summary>How a run ended and what it does to its card: the run's <c>result</c>, the reason,
/// the state the card moves to, and what the card says there when that is not the run's reason or
/// the state's own line.</summary>
public sealed record RunEnding(string Result, string? Reason, string CardState, string? CardReason = null);

/// <summary>What recording a result comes to.</summary>
public enum Recorded
{
    /// <summary>Stored, and the card moved on.</summary>
    Stored,

    /// <summary>The worker holds no such run under way.</summary>
    NotHeld,

    /// <summary>The result names another branch than its job's.</summary>
    WrongBranch,

    /// <summary>A review's result names another commit than the one it was handed.</summary>
    WrongCommit,
}

/// <summary>
/// Runs after they were handed out, through the door as the project's user: the worker saying the
/// check has begun, its result stored as it came and judged, and a run that never returns found
/// lost. Only the worker holding a run under way is heard; a finished run is not reported on
/// again. The report guides and decides nothing; the check's exit code and where a failure lies
/// do. See "Jobs" in devflow/README.md and "Handing out work" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class RunStore
{
    /// <summary>What a card that built and passed its check says, until its pull request is
    /// opened.</summary>
    public const string Built = "Built, and its check passed; its pull request opens next";

    /// <summary>Why a lost run ended.</summary>
    public const string LostReason = "No result came back, and its worker was not seen for 10 minutes";

    /// <summary>The most lines of a check's output or of stderr kept, the last ones.</summary>
    public const int MostLines = 200;

    /// <summary>The most characters of either kept, the last ones, so 200 very long lines are not
    /// stored whole.</summary>
    public const int MostCharacters = 64 * 1024;

    /// <summary>The longest a reason from a worker is kept, so a card's line stays a line.</summary>
    public const int MostReason = 1000;

    /// <summary>How long after it was handed out a run with no result may be lost.</summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(100);

    /// <summary>How long its worker must not have been seen for it to be lost.</summary>
    public static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How a review's result ends its run, in this order: a failure outside the card puts the card
    /// back in <c>reviewing</c>, to be reviewed again; a failure in the card, or a verdict that does
    /// not match its form, which is a failure in the run and never a verdict, stops it with why;
    /// anything else succeeded. Then <c>changes</c> moves the card to <c>fixing</c> for its next
    /// round, or, with <paramref name="roundsUsed"/> already <see cref="FixRounds.Most"/>, stops it
    /// with <see cref="FixRounds.UsedUp"/>; and <c>approve</c> has it wait for the owner with its
    /// verdict, until the merge check acts on it.
    /// </summary>
    public static RunEnding JudgeReview(RunResult result, int roundsUsed = 0)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            var why = Cut(result.FailureReason ?? "");
            return result.FailureInCard
                ? new RunEnding("failed_in_card", Cut($"The review failed: {why}"), "stopped")
                : new RunEnding("failed_outside_card", why, "reviewing");
        }

        if (!ReviewVerdict.TryRead(result.Report ?? default, out var verdict, out var error))
        {
            return new RunEnding("report_invalid", Cut($"The verdict did not match its form: {error}"), "stopped");
        }

        return verdict.Verdict != ReviewVerdictKind.Changes ? new RunEnding("succeeded", null, "waiting_for_owner")
            : roundsUsed >= FixRounds.Most ? new RunEnding("succeeded", null, "stopped", FixRounds.UsedUp)
            : new RunEnding("succeeded", null, "fixing", FixRounds.Asked(roundsUsed + 1));
    }

    /// <summary>
    /// How a fix's result ends its run, in this order: a failure outside the card puts the card back
    /// in <c>fixing</c>, to be fixed again in the same round; a failure in the card, a report that
    /// does not match its form or that stopped, a branch left at <paramref name="fromCommit"/>, the
    /// commit the fix was handed, which is <see cref="FixRounds.NoNewCommit"/>, and a check that did
    /// not run or failed each stop it with why; anything else succeeded, and the card goes back to
    /// <c>reviewing</c>, for a fresh review of its new head.
    /// </summary>
    public static RunEnding JudgeFix(RunResult result, string fromCommit)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            var why = Cut(result.FailureReason ?? "");
            return result.FailureInCard
                ? new RunEnding("failed_in_card", Cut($"The fix failed: {why}"), "stopped")
                : new RunEnding("failed_outside_card", why, "fixing");
        }

        var built = Judge(result);
        if (built.Result is "report_invalid" or "stopped")
        {
            return built;
        }

        if (result.HeadCommit is null || string.Equals(result.HeadCommit, fromCommit, StringComparison.Ordinal))
        {
            return new RunEnding("failed_in_card", FixRounds.NoNewCommit, "stopped");
        }

        return built.Result == "succeeded" ? new RunEnding("succeeded", null, "reviewing") : built;
    }

    /// <summary>
    /// How a build's result ends its run, in this order: a failure outside the card puts the card back in
    /// <c>queued</c>, counting nothing against it; a failure in the card, a report that does not
    /// match its form, a report that stopped, a check that did not run or failed, each stop it with
    /// why; anything else succeeded, and the card waits, checked, for its pull request.
    /// </summary>
    public static RunEnding Judge(RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            var why = Cut(result.FailureReason ?? "");
            return result.FailureInCard
                ? new RunEnding("failed_in_card", $"The run failed: {why}", "stopped")
                : new RunEnding("failed_outside_card", why, "queued");
        }

        if (!Report.TryRead(result.Report ?? default, out var report, out var error))
        {
            return new RunEnding("report_invalid", Cut($"The report did not match its form: {error}"), "stopped");
        }

        if (report.Outcome == ReportOutcome.Stopped)
        {
            return new RunEnding("stopped", Cut($"The agent stopped: {report.StoppedReason.Trim()}"), "stopped");
        }

        return result.CheckExitCode switch
        {
            null => new RunEnding("check_failed", "The check did not run", "stopped"),
            0 => new RunEnding("succeeded", null, "checking"),
            var code => new RunEnding("check_failed", string.Create(CultureInfo.InvariantCulture, $"The check failed with exit code {code}"), "stopped"),
        };
    }

    /// <summary>The last <see cref="MostLines"/> lines of <paramref name="text"/>, and of those at
    /// most the last <see cref="MostCharacters"/> characters, one fewer where the cut would split a
    /// surrogate pair.</summary>
    public static string Tail(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n');
        var kept = lines.Length > MostLines ? string.Join('\n', lines[^MostLines..]) : text;
        if (kept.Length <= MostCharacters)
        {
            return kept;
        }

        var start = kept.Length - MostCharacters;
        return kept[(char.IsLowSurrogate(kept[start]) && char.IsHighSurrogate(kept[start - 1]) ? start + 1 : start)..];
    }

    /// <summary>Marks the run's check begun and its card <c>checking</c>, when the worker holds
    /// it under way. False otherwise.</summary>
    public static async Task<bool> Checking(UserDb database, Guid userId, Guid projectId, Guid runId, Guid workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            var number = await transaction.Connection!.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                """
                UPDATE runs SET checking_at = coalesce(checking_at, @now)
                WHERE id = @runId AND project_id = @projectId AND worker_id = @workerId AND finished_at IS NULL
                RETURNING card_number
                """,
                new { runId, projectId, workerId, now },
                transaction,
                cancellationToken: cancellationToken));
            if (number is null)
            {
                return false;
            }

            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "UPDATE cards SET state = 'checking', changed_at = @now WHERE project_id = @projectId AND number = @number AND state = 'building'",
                new { projectId, number, now },
                transaction,
                cancellationToken: cancellationToken));
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Stores <paramref name="result"/> on the run, as it came but made <see cref="Storable"/>,
    /// with the figures the usage page reads, judges it by <see cref="Judge"/> for a build,
    /// <see cref="JudgeReview"/> for a review or <see cref="JudgeFix"/> for a fix and moves the card,
    /// with the round a review's changes starts, in one transaction; only when the
    /// worker holds the run under way, the result names the job's branch, and for a review the
    /// commit it was handed, or none on a failure.
    /// </summary>
    public static async Task<Recorded> Record(UserDb database, Guid userId, Guid projectId, Guid runId, Guid workerId, RunResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(result);
        // Judged on what is stored, so a report is read with the same text the owner reads.
        result = Storable.Result(result);
        return await database.InUserTransaction(userId, async transaction =>
        {
            var run = await transaction.Connection!.QuerySingleOrDefaultAsync<HeldRun>(new CommandDefinition(
                """
                SELECT card_number AS CardNumber, branch AS Branch, kind AS Kind, head_commit AS HeadCommit, from_commit AS FromCommit,
                       fix_round AS FixRound,
                       coalesce((SELECT c.fix_rounds FROM cards c WHERE c.project_id = runs.project_id AND c.number = runs.card_number), 0) AS RoundsUsed
                FROM runs
                WHERE id = @runId AND project_id = @projectId AND worker_id = @workerId AND finished_at IS NULL
                FOR UPDATE OF runs
                """,
                new { runId, projectId, workerId },
                transaction,
                cancellationToken: cancellationToken));
            if (run is null)
            {
                return Recorded.NotHeld;
            }

            if (!string.Equals(run.Branch, result.Branch, StringComparison.Ordinal))
            {
                return Recorded.WrongBranch;
            }

            var review = run.Kind == Job.Review;
            // A review is for the commit it was handed, which the worker checked out: a success
            // names that commit, and a failure that one or none.
            if (review && (result.Succeeded || result.HeadCommit is not null)
                && !string.Equals(run.HeadCommit, result.HeadCommit, StringComparison.Ordinal))
            {
                return Recorded.WrongCommit;
            }

            var ending = run.Kind switch
            {
                Job.Review => JudgeReview(result, run.RoundsUsed),
                Job.Fix => JudgeFix(result, run.FromCommit!),
                _ => Judge(result),
            };
            var usage = result.Usage;
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                """
                UPDATE runs SET
                    finished_at = @now, result = @result, reason = @reason,
                    report = @report::jsonb, check_exit_code = @checkExitCode, check_output = @checkOutput,
                    head_commit = coalesce(@headCommit, head_commit), auth_method = @authMethod,
                    usage = @usage::jsonb, input_tokens = @inputTokens, output_tokens = @outputTokens,
                    cache_creation_input_tokens = @cacheCreation, cache_read_input_tokens = @cacheRead,
                    cost_usd = @cost, turns = @turns, duration_ms = @durationMs, stderr = @stderr,
                    permission_denials = @permissionDenials::jsonb
                WHERE id = @runId
                """,
                new
                {
                    runId,
                    now,
                    result = ending.Result,
                    reason = ending.Reason,
                    report = result.Report?.GetRawText(),
                    checkExitCode = result.CheckExitCode,
                    checkOutput = result.CheckOutput is { } output ? Tail(output) : null,
                    headCommit = result.HeadCommit,
                    authMethod = result.AuthMethod,
                    usage = usage.GetRawText(),
                    inputTokens = Count(usage, "input_tokens"),
                    outputTokens = Count(usage, "output_tokens"),
                    cacheCreation = Count(usage, "cache_creation_input_tokens"),
                    cacheRead = Count(usage, "cache_read_input_tokens"),
                    cost = usage.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number && cost.TryGetDecimal(out var dollars) ? dollars : (decimal?)null,
                    turns = usage.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number && turns.TryGetInt32(out var n) ? n : (int?)null,
                    durationMs = result.DurationMs,
                    stderr = Tail(result.Stderr),
                    permissionDenials = result.PermissionDenials?.GetRawText(),
                },
                transaction,
                cancellationToken: cancellationToken));
            // A verdict in its form is kept for the commit the worker reviewed, the run's, in the
            // same transaction as its result, and is the only way Dev flow learns one.
            if (review && ending.Result == "succeeded" && ReviewVerdict.TryRead(result.Report!.Value, out var verdict, out _))
            {
                await ReviewStore.Record(transaction, projectId, userId, run.CardNumber, runId, run.HeadCommit!, verdict, now, cancellationToken);
            }

            // A card put back in the queue says nothing, as one never started does; the next read
            // gives it its reason, if it waits for something.
            var cardReason = ending.CardReason ?? ending.CardState switch
            {
                "queued" => null,
                "checking" => Built,
                "reviewing" => PullRequestOpener.ToReview,
                "waiting_for_owner" => PullRequestOpener.Waiting,
                "fixing" => FixRounds.Waiting(run.FixRound),
                _ => ending.Reason,
            };
            var running = run.Kind switch
            {
                Job.Review => UnderReview,
                Job.Fix => Fixing,
                _ => Building,
            };
            var moved = await EndCard(transaction, projectId, run.CardNumber, running, ending.CardState, cardReason, now, cancellationToken);
            if (moved && review && ending.CardState == "fixing")
            {
                // The round the changes start, and the review its fix answers.
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE cards SET fix_rounds = @round, fix_round = @round, fix_review_id = @runId
                    WHERE project_id = @projectId AND number = @number
                    """,
                    new { projectId, number = run.CardNumber, round = run.RoundsUsed + 1, runId },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            return Recorded.Stored;
        }, cancellationToken);
    }

    /// <summary>
    /// Ends the project's runs that never returned: handed out at least <see cref="LostAfter"/>
    /// ago, with no result, and a worker not seen for <see cref="QuietFor"/>. Each is recorded lost
    /// and its card put back, counting nothing against it: a build's in <c>queued</c>, a review's in
    /// <c>reviewing</c>, a fix's in <c>fixing</c>, in the same round. Called on each read of the project, as its user. How many were lost.
    /// </summary>
    public static async Task<int> SweepLost(UserDb database, Guid userId, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            var lost = (await transaction.Connection!.QueryAsync<(int, string, int?)>(new CommandDefinition(
                """
                UPDATE runs r SET finished_at = @now, result = 'lost', reason = @reason
                FROM workers w
                WHERE r.project_id = @projectId AND r.finished_at IS NULL AND r.started_at <= @handedBefore
                  AND w.project_id = r.project_id AND w.id = r.worker_id AND w.last_seen_at <= @seenBefore
                RETURNING r.card_number, r.kind, r.fix_round
                """,
                new { projectId, now, reason = LostReason, handedBefore = now - LostAfter, seenBefore = now - QuietFor },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            foreach (var (number, kind, round) in lost)
            {
                var (running, state, reason) = kind switch
                {
                    Job.Review => (UnderReview, "reviewing", PullRequestOpener.ToReview),
                    Job.Fix => (Fixing, "fixing", FixRounds.Waiting(round)),
                    _ => (Building, "queued", (string?)null),
                };
                await EndCard(transaction, projectId, number, running, state, reason, now, cancellationToken);
            }

            return lost.Count;
        }, cancellationToken);
    }

    /// <summary>Where a build puts its card while it runs.</summary>
    private static readonly string[] Building = ["building", "checking"];

    /// <summary>Where a review leaves its card while it runs.</summary>
    private static readonly string[] UnderReview = ["reviewing"];

    /// <summary>Where a fix leaves its card while it runs.</summary>
    private static readonly string[] Fixing = ["fixing"];

    /// <summary>
    /// Moves a card whose run has just ended to <paramref name="state"/> with
    /// <paramref name="reason"/>, only while it is still where its run put it, one of
    /// <paramref name="running"/>: one the owner's read has since ignored stays ignored, and one
    /// whose pull request a read found merged stays done. One whose label was taken off while it
    /// ran is removed instead, as "Cards" in devflow/docs/devflow-architecture.md says; its runs
    /// stay. Whether it was moved.
    /// </summary>
    private static async Task<bool> EndCard(NpgsqlTransaction transaction, Guid projectId, int number, string[] running, string state, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var removed = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "DELETE FROM cards WHERE project_id = @projectId AND number = @number AND unlabelled AND state = ANY(@running)",
            new { projectId, number, running },
            transaction,
            cancellationToken: cancellationToken));
        if (removed > 0)
        {
            return false;
        }

        return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE cards SET state = @state, reason = @reason, changed_at = @now
            WHERE project_id = @projectId AND number = @number AND state = ANY(@running)
            """,
            new { projectId, number, running, state, reason, now },
            transaction,
            cancellationToken: cancellationToken)) > 0;
    }

    /// <summary>One of the token counts in Claude's <c>usage</c>, or null when it gave none.</summary>
    private static long? Count(JsonElement usage, string field) =>
        usage.TryGetProperty("usage", out var counts) && counts.ValueKind == JsonValueKind.Object
            && counts.TryGetProperty(field, out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var value)
            ? value
            : null;

    /// <summary>A reason from a worker or an agent, cut to <see cref="MostReason"/>, one fewer
    /// where the cut would split a surrogate pair.</summary>
    private static string Cut(string reason) =>
        reason.Length <= MostReason ? reason
        : char.IsHighSurrogate(reason[MostReason - 1]) && char.IsLowSurrogate(reason[MostReason]) ? reason[..(MostReason - 1)]
        : reason[..MostReason];

    private sealed record HeldRun(int CardNumber, string Branch, string Kind, string? HeadCommit, string? FromCommit, int? FixRound, int RoundsUsed);
}