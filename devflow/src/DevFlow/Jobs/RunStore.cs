using System.Globalization;
using System.Text.Json;

using Dapper;

using DevFlow.Fixing;
using DevFlow.Merging;
using DevFlow.Protocol;
using DevFlow.PullRequests;
using DevFlow.Reviews;
using DevFlow.Stops;

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
/// <param name="Screenshots">A build's pictures, their names and the worker's note, null when none
/// were tried.</param>
/// <param name="TimedOut">Whether the run reached its time limit, a failure in the card that stops
/// it at once.</param>
/// <param name="ResetsAt">When Claude's usage limit resets, on a failure outside the card whose
/// answer said so.</param>
/// <param name="RemergeDiff">A conflict's remerge diff of the merge commit it pushed, null for any
/// other run.</param>
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
    JsonElement? PermissionDenials = null,
    ResultScreenshots? Screenshots = null,
    bool TimedOut = false,
    DateTimeOffset? ResetsAt = null,
    string? RemergeDiff = null);

/// <summary>How a run ended and what it does to its card: the run's <c>result</c>, the reason,
/// the state the card moves to, and what the card says there when that is not the run's reason or
/// the state's own line. For a feature's run, the state its feature moves to.</summary>
/// <param name="Retry">Whether it is a failure in the card that, alone, runs the card again: the
/// card moves to <see cref="CardState"/> only when it is the second in a row with no new commit, see
/// <see cref="RunStore.Record"/>. A time limit and a report that stopped are not: either stops the
/// card at once.</param>
public sealed record RunEnding(string Result, string? Reason, string CardState, string? CardReason = null, bool Retry = false);

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
    /// with <see cref="FixRounds.UsedUp"/>; and <c>approve</c> has it wait for the owner, due for
    /// the merge check, which merges it or says why not.
    /// </summary>
    public static RunEnding JudgeReview(RunResult result, int roundsUsed = 0)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            var why = Cut(result.FailureReason ?? "");
            return result.FailureInCard
                ? new RunEnding("failed_in_card", Cut($"The review failed: {why}"), "stopped", Retry: !result.TimedOut)
                : new RunEnding("failed_outside_card", why, "reviewing");
        }

        if (!ReviewVerdict.TryRead(result.Report ?? default, out var verdict, out var error))
        {
            return new RunEnding("report_invalid", Cut($"The verdict did not match its form: {error}"), "stopped", Retry: true);
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
                ? new RunEnding("failed_in_card", Cut($"The fix failed: {why}"), "stopped", Retry: !result.TimedOut)
                : new RunEnding("failed_outside_card", why, "fixing");
        }

        var built = Judge(result);
        if (built.Result is "report_invalid" or "stopped")
        {
            return built;
        }

        if (result.HeadCommit is null || string.Equals(result.HeadCommit, fromCommit, StringComparison.Ordinal))
        {
            return new RunEnding("failed_in_card", FixRounds.NoNewCommit, "stopped", Retry: true);
        }

        return built.Result == "succeeded" ? new RunEnding("succeeded", null, "reviewing") : built;
    }

    /// <summary>
    /// How a conflict's result ends its run, in this order: a failure outside the job leaves its
    /// feature <c>resolving</c>, to be handed out again, counting nothing; a failure in the job, a
    /// report that does not match its form or that stopped, a check that did not run or failed, as
    /// <see cref="Judge"/> words each, and a success that names no commit or the one it was handed,
    /// <see cref="NoMerge"/>, leave the feature <c>waiting_for_owner</c>, since a conflict has no
    /// rounds; anything else succeeded, and the feature goes on <c>bringing_in</c>, its default
    /// branch brought in, as after a clean merge.
    /// </summary>
    public static RunEnding JudgeConflict(RunResult result, string fromCommit)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            var why = Cut(result.FailureReason ?? "");
            return result.FailureInCard
                ? new RunEnding("failed_in_card", Cut($"The run failed: {why}"), "waiting_for_owner")
                : new RunEnding("failed_outside_card", why, "resolving");
        }

        var built = Judge(result);
        if (built.Result != "succeeded")
        {
            return built with { CardState = "waiting_for_owner", CardReason = null, Retry = false };
        }

        return result.HeadCommit is null || string.Equals(result.HeadCommit, fromCommit, StringComparison.Ordinal)
            ? new RunEnding("failed_in_card", NoMerge, "waiting_for_owner")
            : new RunEnding("succeeded", null, "bringing_in");
    }

    /// <summary>Why a conflict that answered a success failed all the same: its branch is where it
    /// was handed.</summary>
    public const string NoMerge = "No merge commit was pushed";

    /// <summary>
    /// How a whole feature's review ends its run, in this order: a failure outside the job leaves the
    /// feature <c>reviewing</c>, to be reviewed again, counting nothing; a failure in the job, or a
    /// verdict that does not match its form, which is a failure in the run and never a verdict,
    /// leaves it <c>waiting_for_owner</c> with why, since the owner reviews a feature anyway and the
    /// same commit is not reviewed again; anything else succeeded, and the feature stays
    /// <c>reviewing</c> until the minute reads whether it is ready.
    /// </summary>
    public static RunEnding JudgeFeatureReview(RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            var why = Cut(result.FailureReason ?? "");
            return result.FailureInCard
                ? new RunEnding("failed_in_card", Cut($"The run failed: {why}"), "waiting_for_owner")
                : new RunEnding("failed_outside_card", why, "reviewing");
        }

        return ReviewVerdict.TryRead(result.Report ?? default, out _, out var error)
            ? new RunEnding("succeeded", null, "reviewing")
            : new RunEnding("report_invalid", Cut($"The verdict did not match its form: {error}"), "waiting_for_owner");
    }

    /// <summary>
    /// How a whole feature's fix ends its run, in this order: a failure outside the job leaves the
    /// feature <c>fixing</c>, to be fixed again in the same round, counting nothing; a failure in the
    /// job, a report that does not match its form, a check that did not run or failed, which the
    /// worker pushes nothing for, and a branch left at <paramref name="fromCommit"/>,
    /// <see cref="FixRounds.NoNewCommit"/>, each run it again once and leave it
    /// <c>waiting_for_owner</c> as the second in a row from the same commit, see
    /// <see cref="RunEnding.Retry"/>; a time limit and a report that stopped leave it waiting at once;
    /// anything else succeeded, and the feature goes back to <c>reviewing</c>, for a fresh review of
    /// its new head. A feature is never stopped by its fix: the owner reviews it anyway.
    /// </summary>
    public static RunEnding JudgeFeatureFix(RunResult result, string fromCommit)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            var why = Cut(result.FailureReason ?? "");
            return result.FailureInCard
                ? new RunEnding("failed_in_card", Cut($"The fix failed: {why}"), "waiting_for_owner", Retry: !result.TimedOut)
                : new RunEnding("failed_outside_card", why, "fixing");
        }

        var built = Judge(result);
        if (built.Result != "succeeded")
        {
            // A check that failed is judged before the commit, since the worker pushes nothing then.
            return built with { CardState = "waiting_for_owner", CardReason = null, Retry = built.Result != "stopped" };
        }

        return result.HeadCommit is null || string.Equals(result.HeadCommit, fromCommit, StringComparison.Ordinal)
            ? new RunEnding("failed_in_card", FixRounds.NoNewCommit, "waiting_for_owner", Retry: true)
            : new RunEnding("succeeded", null, "reviewing");
    }

    /// <summary>
    /// How a build's result ends its run, in this order: a failure outside the card puts the card back in
    /// <c>queued</c>, counting nothing against it; a failure in the card, a report that does not
    /// match its form, a report that stopped, a check that did not run or failed, each stop it with
    /// why, all but a time limit and a report that stopped only as the second in a row, see
    /// <see cref="RunEnding.Retry"/>; anything else succeeded, and the card waits, checked, for its pull request.
    /// </summary>
    public static RunEnding Judge(RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            var why = Cut(result.FailureReason ?? "");
            return result.FailureInCard
                ? new RunEnding("failed_in_card", $"The run failed: {why}", "stopped", Retry: !result.TimedOut)
                : new RunEnding("failed_outside_card", why, "queued");
        }

        if (!Report.TryRead(result.Report ?? default, out var report, out var error))
        {
            return new RunEnding("report_invalid", Cut($"The report did not match its form: {error}"), "stopped", Retry: true);
        }

        if (report.Outcome == ReportOutcome.Stopped)
        {
            return new RunEnding("stopped", Cut($"The agent stopped: {report.StoppedReason.Trim()}"), "stopped");
        }

        return result.CheckExitCode switch
        {
            null => new RunEnding("check_failed", "The check did not run", "stopped", Retry: true),
            0 => new RunEnding("succeeded", null, "checking"),
            var code => new RunEnding("check_failed", string.Create(CultureInfo.InvariantCulture, $"The check failed with exit code {code}"), "stopped", Retry: true),
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

    /// <summary>Marks the run's check begun and its card <c>checking</c>, a feature's run moving no
    /// card, when the worker holds it under way. False otherwise.</summary>
    public static async Task<bool> Checking(UserDb database, Guid userId, Guid projectId, Guid runId, Guid workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            var held = await transaction.Connection!.QuerySingleOrDefaultAsync<(Guid Id, int? Card)?>(new CommandDefinition(
                """
                UPDATE runs SET checking_at = coalesce(checking_at, @now)
                WHERE id = @runId AND project_id = @projectId AND worker_id = @workerId AND finished_at IS NULL
                RETURNING id, card_number
                """,
                new { runId, projectId, workerId, now },
                transaction,
                cancellationToken: cancellationToken));
            if (held is not { } run)
            {
                return false;
            }

            // A feature's run has no card to move.
            if (run.Card is not { } number)
            {
                return true;
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
    /// with the round a review's changes starts, or by <see cref="JudgeConflict"/> for a conflict,
    /// <see cref="JudgeFeatureReview"/> for a whole feature's review or <see cref="JudgeFeatureFix"/>
    /// for a whole feature's fix, which move its feature, in one transaction; only when the
    /// worker holds the run under way, the result names the job's branch, and for a review the
    /// commit it was handed, or none on a failure. A run on a feature's branch itself records the
    /// commit it names in <c>pushes_made</c>, and the pushes the watch awaited are judged, see
    /// <see cref="Watching.BranchWatch.JudgeAwaited"/>.
    /// </summary>
    public static async Task<Recorded> Record(UserDb database, Guid userId, Guid projectId, Guid runId, Guid workerId, RunResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(result);
        // Judged on what is stored, so a report is read with the same text the owner reads.
        result = Storable.Result(result);
        return await database.InUserTransaction(userId, async transaction =>
        {
            // The project's row first, as the watch and Dev flow's own merges take it, before the
            // run's and its card's, so a run ending on a feature's branch judges what was pushed
            // there in the order every other writer locks in.
            if (!await Watching.BranchWatch.Lock(transaction, projectId, cancellationToken))
            {
                return Recorded.NotHeld;
            }

            var run = await transaction.Connection!.QuerySingleOrDefaultAsync<HeldRun>(new CommandDefinition(
                """
                SELECT card_number AS CardNumber, feature_number AS FeatureNumber, branch AS Branch, base AS Base, kind AS Kind,
                       head_commit AS HeadCommit, from_commit AS FromCommit, default_commit AS DefaultCommit, fix_round AS FixRound,
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

            var review = run.Kind is Job.Review or Job.FeatureReview;
            // A review, of a card or of a whole feature, is for the commit it was handed, which the
            // worker checked out: a success names that commit, and a failure that one or none.
            if (review && (result.Succeeded || result.HeadCommit is not null)
                && !string.Equals(run.HeadCommit, result.HeadCommit, StringComparison.Ordinal))
            {
                return Recorded.WrongCommit;
            }

            var ending = run.Kind switch
            {
                Job.Review => JudgeReview(result, run.RoundsUsed),
                Job.Fix => JudgeFix(result, run.FromCommit!),
                Job.Conflict => JudgeConflict(result, run.FromCommit!),
                Job.FeatureReview => JudgeFeatureReview(result),
                Job.FeatureFix => JudgeFeatureFix(result, run.FromCommit!),
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
                    permission_denials = @permissionDenials::jsonb,
                    screenshots = @screenshots::jsonb, screenshots_note = @screenshotsNote,
                    timed_out = @timedOut, resets_at = @resetsAt, remerge_diff = @remergeDiff
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
                    // A review takes none; only a build's are kept.
                    screenshots = !review && result.Screenshots is { Names: { } names } ? JsonSerializer.Serialize(names) : null,
                    screenshotsNote = review ? null : result.Screenshots?.Note,
                    timedOut = !result.Succeeded && result.TimedOut,
                    resetsAt = result.Succeeded ? null : result.ResetsAt,
                    // Only a conflict's, cut as its worker cuts it.
                    remergeDiff = run.Kind == Job.Conflict && result.RemergeDiff is { } diff ? RemergeDiffs.Cut(diff) : null,
                },
                transaction,
                cancellationToken: cancellationToken));
            // A run on a feature's branch itself pushed there, before it sent this: the commit it
            // names is recorded as Dev flow's own push, and what the watch read meanwhile is judged.
            // A review, of a whole feature among them, names the commit it read and pushed nothing,
            // and a fix that names the commit it was handed pushed nothing new.
            if (await transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM features WHERE project_id = @projectId AND branch = @branch)",
                    new { projectId, branch = run.Branch },
                    transaction,
                    cancellationToken: cancellationToken)))
            {
                if (!review && result.HeadCommit is { } pushed && !string.Equals(pushed, run.FromCommit, StringComparison.Ordinal))
                {
                    await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO pushes_made (project_id, user_id, branch, commit, run_id, recorded_at)
                        VALUES (@projectId, @userId, @branch, @pushed, @runId, @now)
                        ON CONFLICT (project_id, branch, commit) DO NOTHING
                        """,
                        new { projectId, userId, branch = run.Branch, pushed, runId, now },
                        transaction,
                        cancellationToken: cancellationToken));
                }

                await Watching.BranchWatch.JudgeAwaited(transaction, projectId, userId, now, cancellationToken);
            }

            // A failure outside the card, or a feature's job, never counts against it, but makes its
            // project wait; a success ends the wait.
            if (ending.Result == "failed_outside_card")
            {
                await ProjectWaits.Failed(transaction, projectId, userId, ending.Reason ?? "", result.ResetsAt, now, cancellationToken);
            }
            else if (ending.Result == "succeeded")
            {
                await ProjectWaits.Succeeded(transaction, projectId, cancellationToken);
            }

            // A feature's run moves its feature, never a card. A whole feature's verdict in its form is
            // kept for the commit the worker reviewed, the run's, in the same transaction as its
            // result, as a card's is.
            if (run.FeatureNumber is { } feature)
            {
                if (run.Kind == Job.FeatureReview)
                {
                    if (ending.Result == "succeeded" && ReviewVerdict.TryRead(result.Report!.Value, out var whole, out _))
                    {
                        await FeatureReviewStore.Record(transaction, projectId, userId, feature, runId, run.HeadCommit!, whole, now, cancellationToken);
                    }

                    await EndFeatureReview(transaction, projectId, feature, run.HeadCommit!, ending, now, cancellationToken);
                    return Recorded.Stored;
                }

                if (run.Kind == Job.FeatureFix)
                {
                    await EndFeatureFix(transaction, projectId, feature, run.FromCommit!, run.FixRound, ending, now, cancellationToken);
                    return Recorded.Stored;
                }

                await EndFeature(transaction, projectId, feature, run.Base, run.DefaultCommit, ending, now, cancellationToken);
                return Recorded.Stored;
            }

            var card = run.CardNumber!.Value;

            // A verdict in its form is kept for the commit the worker reviewed, the run's, in the
            // same transaction as its result, and is the only way Dev flow learns one.
            if (review && ending.Result == "succeeded" && ReviewVerdict.TryRead(result.Report!.Value, out var verdict, out _))
            {
                await ReviewStore.Record(transaction, projectId, userId, card, runId, run.HeadCommit!, verdict, now, cancellationToken);
            }

            // A failure in the card runs it again once; the second in a row, with its branch where
            // the first left it, stops it. A success starts the count afresh.
            if (ending.Retry)
            {
                ending = await InARow(transaction, projectId, card, run.Kind, ending, cancellationToken);
            }
            else if (ending.Result == "succeeded")
            {
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    "UPDATE cards SET in_card_failures = 0, failed_at_commit = NULL WHERE project_id = @projectId AND number = @number",
                    new { projectId, number = card },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            // A card put back in the queue says nothing, as one never started does; the next read
            // gives it its reason, if it waits for something.
            var cardReason = ending.CardReason ?? ending.CardState switch
            {
                "queued" => null,
                "checking" => Built,
                "reviewing" => PullRequestOpener.ToReview,
                "waiting_for_owner" => MergeCheck.Checking,
                "fixing" => FixRounds.Waiting(run.FixRound),
                _ => ending.Reason,
            };
            var running = run.Kind switch
            {
                Job.Review => UnderReview,
                Job.Fix => Fixing,
                _ => Building,
            };
            var moved = await EndCard(transaction, projectId, card, running, ending.CardState, cardReason, now, cancellationToken);
            if (moved && ending.CardState == "waiting_for_owner")
            {
                // Approved: the merge check reads it next, right after this and on each minute.
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    "UPDATE cards SET merge_check_due = true WHERE project_id = @projectId AND number = @number",
                    new { projectId, number = card },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            if (moved && review && ending.CardState == "fixing")
            {
                // The round the changes start, and the review its fix answers, with no comment yet.
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE cards SET fix_rounds = @round, fix_round = @round, fix_review_id = @runId, fix_comments = NULL
                    WHERE project_id = @projectId AND number = @number
                    """,
                    new { projectId, number = card, round = run.RoundsUsed + 1, runId },
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
    /// <c>reviewing</c>, a fix's in <c>fixing</c>, in the same round; and the pushes the watch
    /// awaited of them are judged, with no result to record them. A feature's run lost leaves its
    /// feature where it was, to be handed out again, a fix's in the same round. Called on each read of the project, as its
    /// user. How many were lost.
    /// </summary>
    public static async Task<int> SweepLost(UserDb database, Guid userId, Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction(userId, async transaction =>
        {
            // The project's row first, as Record takes it, since a lost run ends as a result does.
            if (!await Watching.BranchWatch.Lock(transaction, projectId, cancellationToken))
            {
                return 0;
            }

            var lost = (await transaction.Connection!.QueryAsync<(int?, string, int?, int?)>(new CommandDefinition(
                """
                UPDATE runs r SET finished_at = @now, result = 'lost', reason = @reason
                FROM workers w
                WHERE r.project_id = @projectId AND r.finished_at IS NULL AND r.started_at <= @handedBefore
                  AND w.project_id = r.project_id AND w.id = r.worker_id AND w.last_seen_at <= @seenBefore
                RETURNING r.card_number, r.kind, r.fix_round, r.feature_number
                """,
                new { projectId, now, reason = LostReason, handedBefore = now - LostAfter, seenBefore = now - QuietFor },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            foreach (var (card, kind, round, feature) in lost)
            {
                // A lost run lies outside the card: it never counts against it, and its project waits.
                await ProjectWaits.Failed(transaction, projectId, userId, LostReason, null, now, cancellationToken);
                if (card is not { } number)
                {
                    // A feature's: it stays where its run left it, resolving or reviewing, and is
                    // handed out again; a review no longer under way says so.
                    if (kind == Job.FeatureReview && feature is { } parent)
                    {
                        await EndFeatureReview(transaction, projectId, parent, null, new RunEnding("lost", LostReason, "reviewing"), now, cancellationToken);
                    }
                    else if (kind == Job.FeatureFix && feature is { } fixedOne)
                    {
                        await EndFeatureFix(transaction, projectId, fixedOne, null, round, new RunEnding("lost", LostReason, "fixing"), now, cancellationToken);
                    }

                    continue;
                }

                var (running, state, reason) = kind switch
                {
                    Job.Review => (UnderReview, "reviewing", PullRequestOpener.ToReview),
                    Job.Fix => (Fixing, "fixing", FixRounds.Waiting(round)),
                    _ => (Building, "queued", (string?)null),
                };
                await EndCard(transaction, projectId, number, running, state, reason, now, cancellationToken);
            }

            // A push a lost run made has no result to record it, so what the watch awaited of it
            // stops the project.
            if (lost.Count > 0)
            {
                await Watching.BranchWatch.JudgeAwaited(transaction, projectId, userId, now, cancellationToken);
            }

            return lost.Count;
        }, cancellationToken);
    }

    /// <summary>What a card says when one failure in the card runs it again.</summary>
    public static string Again(string reason) => Cut($"Failed once in the card, so it runs again: {reason}");

    /// <summary>What a card says when a second failure in the card in a row stopped it.</summary>
    public static string Twice(string reason) => Cut($"Failed twice in a row with no new commit: {reason}");

    /// <summary>
    /// A failure in the card that alone runs it again, judged against the one before it, inside the
    /// transaction that records it, with the card's row locked: the second in a row whose branch is
    /// at the commit the first left it at, or at none when neither pushed, stops the card with
    /// <see cref="Twice"/>; any other puts it back where its kind of run starts from, a build's in
    /// <c>queued</c>, a review's in <c>reviewing</c> and a fix's in <c>fixing</c> in the same round,
    /// with <see cref="Again"/>, and remembers the commit. A card that is gone is left to
    /// <see cref="EndCard"/>.
    /// </summary>
    private static async Task<RunEnding> InARow(NpgsqlTransaction transaction, Guid projectId, int number, string kind, RunEnding ending, CancellationToken cancellationToken)
    {
        var card = await transaction.Connection!.QuerySingleOrDefaultAsync<FailureCount>(new CommandDefinition(
            $"""
            SELECT in_card_failures AS Failures, failed_at_commit AS FailedAt, {JobStore.BranchHeadOf} AS BranchHead
            FROM cards WHERE project_id = @projectId AND number = @number
            FOR UPDATE
            """,
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken));
        var reason = ending.Reason ?? "";
        if (card is null)
        {
            return ending;
        }

        if (card.Failures > 0 && string.Equals(card.FailedAt, card.BranchHead, StringComparison.Ordinal))
        {
            return ending with { CardState = "stopped", CardReason = Twice(reason) };
        }

        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET in_card_failures = 1, failed_at_commit = @head WHERE project_id = @projectId AND number = @number",
            new { projectId, number, head = card.BranchHead },
            transaction,
            cancellationToken: cancellationToken));
        var state = kind switch
        {
            Job.Review => "reviewing",
            Job.Fix => "fixing",
            _ => "queued",
        };
        return ending with { CardState = state, CardReason = Again(reason) };
    }

    private sealed record FailureCount(int Failures, string? FailedAt, string? BranchHead);

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
            UPDATE cards SET state = @state, reason = @reason, changed_at = @now,
                stopped_at = CASE WHEN @state = 'stopped' THEN @now ELSE stopped_at END,
                stop_marked_at = CASE WHEN @state = 'stopped' THEN NULL ELSE stop_marked_at END
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

    private sealed record HeldRun(int? CardNumber, int? FeatureNumber, string Branch, string Base, string Kind, string? HeadCommit, string? FromCommit, string? DefaultCommit, int? FixRound, int RoundsUsed);

    /// <summary>
    /// Moves a feature whose conflict run has just ended to <paramref name="ending"/>'s state, only
    /// while it is still <c>resolving</c>: on a success to <c>bringing_in</c>, with
    /// <paramref name="defaultCommit"/>, the default branch's commit the run brought in, as the one
    /// brought in, so its end goes on as after a clean merge; on a failure in the job to
    /// <c>waiting_for_owner</c>, with <see cref="Features.FeatureEnd.NotResolved"/>, and the commit
    /// as the one bringing in failed at, so it is not tried again until the default branch moves; on
    /// a failure outside it, nowhere, so it is handed out again.
    /// </summary>
    private static async Task EndFeature(NpgsqlTransaction transaction, Guid projectId, int number, string defaultBranch, string? defaultCommit, RunEnding ending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ending.CardState == "resolving")
        {
            return;
        }

        var succeeded = ending.CardState == "bringing_in";
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = @state, reason = @reason, changed_at = @now,
                brought_in_commit = CASE WHEN @succeeded THEN @defaultCommit ELSE brought_in_commit END,
                bring_in_failed_at = CASE WHEN @succeeded THEN NULL ELSE @defaultCommit END
            WHERE project_id = @projectId AND number = @number AND state = 'resolving'
            """,
            new
            {
                projectId,
                number,
                state = ending.CardState,
                reason = succeeded ? null : Features.FeatureEnd.NotResolved(defaultBranch, ending.Reason ?? ""),
                succeeded,
                defaultCommit,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Moves a feature whose whole review has just ended, only while it is still <c>reviewing</c>:
    /// on a success it stays there, <see cref="Features.FeatureReview.Reviewed"/>, for the minute to
    /// read whether it is ready; on a failure outside the job, or a run lost, it stays there to be
    /// reviewed again, <see cref="Features.FeatureReview.ToReview"/>; on a failure in the job it
    /// waits for the owner with why, and <paramref name="commit"/>, the one the review was handed,
    /// is the one its review failed at, so it is not handed out again until the branch moves.
    /// </summary>
    private static async Task EndFeatureReview(NpgsqlTransaction transaction, Guid projectId, int number, string? commit, RunEnding ending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var waits = ending.CardState == "waiting_for_owner";
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = @state, reason = @reason, changed_at = @now,
                review_failed_at = CASE WHEN @waits THEN @commit ELSE review_failed_at END
            WHERE project_id = @projectId AND number = @number AND state = 'reviewing'
            """,
            new
            {
                projectId,
                number,
                state = ending.CardState,
                reason = waits ? Features.FeatureReview.Failed(ending.Reason ?? "")
                    : ending.Result == "succeeded" ? Features.FeatureReview.Reviewed
                    : Features.FeatureReview.ToReview,
                waits,
                commit,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Moves a feature whose fix has just ended, only while it is still <c>fixing</c>: on a success
    /// to <c>reviewing</c>, <see cref="Features.FeatureFix.Fixed"/>, for the minute to queue a review
    /// of its new head once its watch has read it, with what the fix answered cleared; on a failure
    /// outside the job, or a run lost, it stays there in the same round, to be fixed again; on a
    /// failure in the job that runs it again, the first from <paramref name="fromCommit"/>, it stays
    /// there with <see cref="Features.FeatureFix.Again"/> and remembers the commit; on the second in a
    /// row from that commit, or one that never runs again, it waits for the owner with why, and the
    /// commit is remembered, so its readiness leaves it so until its branch moves.
    /// </summary>
    private static async Task EndFeatureFix(NpgsqlTransaction transaction, Guid projectId, int number, string? fromCommit, int? round, RunEnding ending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var failedAt = await transaction.Connection!.QuerySingleOrDefaultAsync<(string State, string? FailedAt)?>(new CommandDefinition(
            "SELECT state, fix_failed_at FROM features WHERE project_id = @projectId AND number = @number FOR UPDATE",
            new { projectId, number },
            transaction,
            cancellationToken: cancellationToken));
        if (failedAt is not { State: "fixing" } feature)
        {
            return;
        }

        var reason = ending.Reason ?? "";
        var (state, said, remember) = ending switch
        {
            { Result: "succeeded" } => ("reviewing", Features.FeatureFix.Fixed, (string?)null),
            { CardState: "fixing" } => ("fixing", Features.FeatureFix.Waiting(round), feature.FailedAt),
            { Retry: true } when !string.Equals(feature.FailedAt, fromCommit, StringComparison.Ordinal)
                => ("fixing", Features.FeatureFix.Again(reason), fromCommit),
            { Retry: true } => ("waiting_for_owner", Features.FeatureFix.Failed(Twice(reason)), fromCommit),
            _ => ("waiting_for_owner", Features.FeatureFix.Failed(reason), fromCommit),
        };
        var succeeded = state == "reviewing";
        await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = @state, reason = @said, changed_at = @now, fix_failed_at = @remember,
                next_fix_round = CASE WHEN @succeeded THEN NULL ELSE next_fix_round END,
                fix_review_id = CASE WHEN @succeeded THEN NULL ELSE fix_review_id END,
                fix_comments = CASE WHEN @succeeded THEN NULL ELSE fix_comments END,
                review_commit = CASE WHEN @succeeded THEN NULL ELSE review_commit END
            WHERE project_id = @projectId AND number = @number AND state = 'fixing'
            """,
            new { projectId, number, state, said, remember, succeeded, now },
            transaction,
            cancellationToken: cancellationToken));
    }
}