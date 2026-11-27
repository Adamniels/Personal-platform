using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using DevFlow.CodeHost;
using DevFlow.Merging;
using DevFlow.Protocol;
using DevFlow.Recipes;
using DevFlow.Workers;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>
/// A job's progress and result over HTTP, the worker's, under
/// <see cref="WorkerAuthentication.Policy"/> and outside the origin check, at
/// <c>DevFlow.Protocol</c>'s paths with its types. Each answers only the worker holding the job
/// while it runs, and the same <c>404</c> for anything else. See "Jobs" in devflow/README.md.
/// </summary>
public static partial class JobEndpoints
{
    /// <summary>The longest text a worker sends for a branch or an auth method.</summary>
    private const int MaxText = 255;

    public static IEndpointRouteBuilder MapJobs(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost(WorkerApi.ProgressRoute, Progress)
            .WorkerRoute("workerJobProgress", "A running job moves on",
                "With state checking, the agent has finished and the check runs: the card moves from building to checking. 204. Only for the worker holding the job, named by worker_id, while it runs; 404 for any other worker, a job that is not one of the project's, or one that has finished. 422 without worker_id, or with a state that is not checking.",
                StatusCodes.Status404NotFound, StatusCodes.Status422UnprocessableEntity);

        routes.MapPost(WorkerApi.ResultRoute, Result)
            .WorkerRoute("workerJobResult", "A job has ended",
                "What the worker sends when its job ends, whatever happened: outcome, succeeded with the agent's report, failed with the failure's reason and whether it lies in the card, or stopped, when the owner stopped the run, with report, failure, check and pictures null and the commit a pushing job pushed of what it did, or none; the check's exit code and the last 200 lines of its output, null when it did not run; the branch, which must be the job's, and its head commit; the auth method; Claude's usage as it came, an object; the duration in milliseconds; the last 200 lines of stderr; and permission_denials, the actions the agent was refused, an array or null. Each is stored on the run as it came. A build's report is read against contracts/devflow/report.schema.json, and one that does not match is a failure in the card; a review's report is its verdict, read against contracts/devflow/verdict.schema.json, and one that does not match is a failure in the run, never a verdict. A review's head_commit is the commit it was handed, or null on a failure; any other is 422. A run stopped is stopped_by_owner, never a failure: a card's build, review or fix leaves the card stopped, \"Stopped by you\", counting nothing; a feature's own run holds the feature; a reading, a setup or a check is put back. A failure outside the card puts the card back in queued, counting nothing against it; a failure in the card, a report that does not match, a report that stopped, a check that did not run or failed stop it with why; otherwise the card waits, checked, for its pull request. A check runs no agent: its report is null, a success means the check ran and carries its result, which is the verdict on the version, and pictures, its address taken at both sizes as PNGs of at most 5 MB, base64, named as screenshots names them; pictures on any other job are 422. A setup's success is validated by Dev flow at the commit it names, its .devflow/Dockerfile by the recipe route's rules and its .devflow/project.json by its schema, as its check; 503 when GitHub did not answer, and the worker sends the result again. 204. Only for the worker holding the job while it runs, and once; 404 otherwise. 422 naming a field that is missing or breaks a rule.",
                StatusCodes.Status404NotFound, StatusCodes.Status422UnprocessableEntity, StatusCodes.Status503ServiceUnavailable);

        routes.MapPost(WorkerApi.LogRoute, Log)
            .WorkerRoute("workerJobLog", "A running job's log, and its heartbeat",
                string.Create(CultureInfo.InvariantCulture, $"What the worker sends from the job's start until its result is sent: every 2 seconds when it has new lines, every 15 with none. lines, at most {LogRequest.MostLines}, each with seq, its place in the run's log from 1, a positive whole number; at, a time; kind, one of {string.Join(", ", LogKinds.All)}; and text, 1 to {LogLine.MostText} characters, a longer one cut to {LogLine.MostText} ending in \"{LogLine.CutMark}\". Each text has every value of the project scrubbed from it before it is stored. A line whose seq the run already holds is skipped, so a retried post never doubles one, and gaps are allowed. A run keeps at most {RunLogStore.MostLines:N0} lines; past that one system line, \"{RunLogStore.NotKept}\", and nothing more. usage, optional, the run's token figures so far, input_tokens, cache_creation_input_tokens, cache_read_input_tokens and output_tokens, each a whole number, 0 or more; it sets the run's figures, which its result's usage replaces. Every post, with lines or none, is the run's heartbeat and marks the worker seen: a run whose worker has sent nothing for 2 minutes is lost. 200 with stop, whether the owner marked the run to stop, so the worker ends it now. Only for the worker holding the job, named by worker_id, while it runs; 404 for any other worker, a job that is not one of the project's, or one that has finished. 422 naming a field that is missing or breaks a rule."),
                StatusCodes.Status404NotFound, StatusCodes.Status422UnprocessableEntity)
            .Produces<LogAnswer>(StatusCodes.Status200OK);

        return routes;
    }

    private static async Task<Results<Ok<LogAnswer>, ProblemHttpResult>> Log(Guid id, LogRequest request, HttpContext context, [FromServices] UserDb database, [FromServices] Values.ProjectValues values, [FromServices] WorkerStore workers, [FromServices] WorkerStart start, [FromServices] RunLogFeed feed, [FromServices] TimeProvider time)
    {
        var errors = LogErrors(request);
        if (errors.Count > 0)
        {
            return WorkerEndpoints.Invalid(errors);
        }

        var caller = context.Worker();
        var worker = request.WorkerId!.Value;
        if (await workers.Seen(caller, worker, start.Image, context.RequestAborted) == WorkerState.Unknown)
        {
            return WorkerEndpoints.NoSuchJob();
        }

        // The owner's stop reaches the worker here, on its next post, at most 15 seconds after it
        // was asked for, since the worker posts at least that often.
        return await Hear(database, values, feed, caller, id, worker, request, Now(time), context.RequestAborted) is { } stop
            ? TypedResults.Ok(new LogAnswer(stop))
            : WorkerEndpoints.NoSuchJob();
    }

    /// <summary>
    /// Records a post to the run's log, as the worker's log route does, whose rules
    /// <see cref="LogErrors"/> found <paramref name="request"/> to keep: each line's text scrubbed of
    /// every value of the project, then stored by <see cref="RunLogStore.Heard"/> with the run's
    /// heartbeat and usage, and the lines stored handed to <paramref name="feed"/> once committed.
    /// The fake worker posts its lines through it too, so nothing reaches a run's log any other way.
    /// Whether the owner marked the run to stop; null when the worker holds no such run under way.
    /// </summary>
    internal static async Task<bool?> Hear(UserDb database, Values.ProjectValues values, RunLogFeed feed, WorkerCaller caller, Guid runId, Guid workerId, LogRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        // Scrubbed as a result's text is, by the values the project has now; a post of none, the
        // heartbeat alone, opens no value.
        var scrub = request.Lines!.Count > 0 ? await values.ScrubFor(caller.UserId, caller.ProjectId, cancellationToken) : ValueScrub.None;
        var lines = request.Lines!.Select(line => LogLineOf(line!, scrub)).ToList();
        var usage = request.Usage is { } given
            ? new RunUsageSoFar(given.InputTokens!.Value, given.CacheCreationInputTokens!.Value, given.CacheReadInputTokens!.Value, given.OutputTokens!.Value)
            : null;
        if (await RunLogStore.Heard(database, caller.UserId, caller.ProjectId, runId, workerId, lines, usage, now, cancellationToken) is not { } heard)
        {
            return null;
        }

        // Only once the lines are committed, so a reader never sees one the table does not hold.
        feed.Publish(runId, heard.Stored);
        return heard.Stop;
    }

    /// <summary>A line whose rules <see cref="LogErrors"/> checked, as it is stored: its text
    /// scrubbed of each of <paramref name="scrub"/>'s values, made storable, then cut, the
    /// placeholder being longer than some values; its time to the microsecond.</summary>
    internal static RunLogLine LogLineOf(LogLine line, ValueScrub scrub)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(scrub);
        return new RunLogLine(line.Seq!.Value, RunLogStore.Microseconds(line.At!.Value), line.Kind!, LogLine.Cut(Storable.Text(scrub.Scrub(line.Text!))));
    }

    /// <summary>Each field of <paramref name="request"/> that is missing or breaks a rule: the
    /// worker's id; the lines, given and at most <see cref="LogRequest.MostLines"/>, and of the
    /// first line that breaks a rule each field that does, as <c>lines[i].field</c>; and the usage,
    /// null or all four figures, each 0 or more.</summary>
    internal static Dictionary<string, string[]> LogErrors(LogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        WorkerEndpoints.CheckId(request.WorkerId, errors);
        if (request.Lines is not { } lines || lines.Count > LogRequest.MostLines)
        {
            errors["lines"] = [$"must be a list of at most {LogRequest.MostLines} lines, empty when there are none"];
        }
        else
        {
            for (var i = 0; i < lines.Count && !errors.Keys.Any(key => key.StartsWith("lines[", StringComparison.Ordinal)); i++)
            {
                CheckLine(lines[i], $"lines[{i}]", errors);
            }
        }

        if (request.Usage is { } usage
            && (usage.InputTokens is not >= 0 || usage.CacheCreationInputTokens is not >= 0
                || usage.CacheReadInputTokens is not >= 0 || usage.OutputTokens is not >= 0))
        {
            errors["usage"] = ["must be null, or hold input_tokens, cache_creation_input_tokens, cache_read_input_tokens and output_tokens, each a whole number, 0 or more"];
        }

        return errors;
    }

    private static void CheckLine(LogLine? line, string at, Dictionary<string, string[]> errors)
    {
        if (line is null)
        {
            errors[at] = ["must be a line, an object"];
            return;
        }

        if (line.Seq is not > 0)
        {
            errors[$"{at}.seq"] = ["must be a positive whole number"];
        }

        if (line.At is null)
        {
            errors[$"{at}.at"] = ["must be a time"];
        }

        if (line.Kind is null || !LogKinds.All.Contains(line.Kind, StringComparer.Ordinal))
        {
            errors[$"{at}.kind"] = [$"must be one of {string.Join(", ", LogKinds.All)}"];
        }

        if (string.IsNullOrEmpty(line.Text))
        {
            errors[$"{at}.text"] = ["must be text of at least 1 character"];
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Progress(Guid id, ProgressRequest request, HttpContext context, [FromServices] UserDb database, [FromServices] WorkerStore workers, [FromServices] WorkerStart start, [FromServices] TimeProvider time)
    {
        var errors = new Dictionary<string, string[]>();
        WorkerEndpoints.CheckId(request.WorkerId, errors);
        if (request.State != ProgressRequest.Checking)
        {
            errors["state"] = [$"must be {ProgressRequest.Checking}"];
        }

        if (errors.Count > 0)
        {
            return WorkerEndpoints.Invalid(errors);
        }

        var caller = context.Worker();
        var worker = request.WorkerId!.Value;
        return await workers.Seen(caller, worker, start.Image, context.RequestAborted) != WorkerState.Unknown
            && await RunStore.Checking(database, caller.UserId, caller.ProjectId, id, worker, Now(time), context.RequestAborted)
                ? TypedResults.NoContent()
                : WorkerEndpoints.NoSuchJob();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Result(Guid id, ResultRequest request, HttpContext context, [FromServices] UserDb database, [FromServices] Values.ProjectValues values, [FromServices] WorkerStore workers, [FromServices] WorkerStart start, [FromServices] MergeRequests merges, [FromServices] ICodeHost host, [FromServices] RunLogFeed feed, [FromServices] TimeProvider time)
    {
        var caller = context.Worker();
        // The rules depend on the job's kind, a check's being its own; a run the worker does not
        // hold is judged as any other, and answered 404 below.
        var kind = request.WorkerId is { } asking && asking != Guid.Empty
            ? await RunStore.KindOf(database, caller.UserId, caller.ProjectId, id, asking, context.RequestAborted)
            : null;
        if (Read(request, kind) is not { } result)
        {
            return WorkerEndpoints.Invalid(Errors(request, kind));
        }

        var worker = request.WorkerId!.Value;
        if (await workers.Seen(caller, worker, start.Image, context.RequestAborted) == WorkerState.Unknown)
        {
            return WorkerEndpoints.NoSuchJob();
        }

        if (kind == Setup.SetupRunStore.SetupKind)
        {
            // What a setup pushed is validated by Dev flow, its check: GitHub not answering is no
            // verdict, so the worker is asked to send the result again.
            if (await workers.RepositoryOf(caller, context.RequestAborted) is not { } repository
                || await Setup.SetupFiles.Validated(host, caller.UserId, repository, result, context.RequestAborted) is not { } validated)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "GitHub did not answer", detail: "What the setup pushed could not be read to validate it. Send the result again.");
            }

            result = validated;
        }

        var recorded = await Complete(database, values, feed, caller, id, worker, result, Now(time), context.RequestAborted);
        if (recorded == Recorded.Stored)
        {
            // A review that approved has its card checked for a merge now, without the worker
            // waiting for it; any other result leaves nothing due, which costs the check nothing.
            merges.Request(caller.ProjectId, caller.UserId);
        }

        return recorded switch
        {
            Recorded.Stored => TypedResults.NoContent(),
            Recorded.WrongBranch => WorkerEndpoints.Invalid(new Dictionary<string, string[]> { ["branch"] = ["must be the job's branch"] }),
            Recorded.WrongCommit => WorkerEndpoints.Invalid(new Dictionary<string, string[]> { ["head_commit"] = ["must be the commit the review was handed, or null on a failure"] }),
            _ => WorkerEndpoints.NoSuchJob(),
        };
    }

    /// <summary>Records a result for the run, as a worker's result route does, every known value
    /// of the project scrubbed from it first; the fake worker completes its jobs through it too, so
    /// nothing a worker sends is stored any other way.</summary>
    internal static async Task<Recorded> Complete(UserDb database, Values.ProjectValues values, RunLogFeed feed, WorkerCaller caller, Guid runId, Guid workerId, RunResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(caller);
        var scrub = await values.ScrubFor(caller.UserId, caller.ProjectId, cancellationToken);
        var recorded = await RunStore.Record(database, caller.UserId, caller.ProjectId, runId, workerId, Scrubbed(result, scrub), now, cancellationToken);
        if (recorded == Recorded.Stored)
        {
            // Once the run's end is committed, so a live log ends on a run that has.
            feed.End(runId);
        }

        return recorded;
    }

    /// <summary>
    /// <paramref name="result"/> with each of <paramref name="scrub"/>'s values scrubbed from every
    /// text it holds: the report, a reading's and a verdict's among them, the failure's reason, the
    /// check's output, the auth method, Claude's usage, the standard error the worker kept, the
    /// refused actions, the pictures' note and a conflict's remerge diff. The branch and the commit
    /// are the job's own and checked against it, a picture's name is one Dev flow's rule allows and
    /// its bytes are not text, so these are kept. A text with a length limit is cut to it after,
    /// the placeholder being longer than some values.
    /// </summary>
    internal static RunResult Scrubbed(RunResult result, ValueScrub scrub)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(scrub);
        return scrub.IsEmpty ? result : result with
        {
            Report = scrub.ScrubOrNull(result.Report),
            FailureReason = scrub.ScrubOrNull(result.FailureReason),
            CheckOutput = scrub.ScrubOrNull(result.CheckOutput),
            AuthMethod = Cut(scrub.ScrubOrNull(result.AuthMethod), MaxText),
            Usage = scrub.Scrub(result.Usage),
            Stderr = scrub.Scrub(result.Stderr),
            PermissionDenials = scrub.ScrubOrNull(result.PermissionDenials),
            Screenshots = result.Screenshots is { } taken ? taken with { Note = Cut(scrub.ScrubOrNull(taken.Note), PageShots.MostNote) } : null,
            RemergeDiff = scrub.ScrubOrNull(result.RemergeDiff),
        };
    }

    private static string? Cut(string? text, int most) => text is not null && text.Length > most ? text[..most] : text;

    /// <summary>The result, when <paramref name="request"/> breaks no rule for a job of
    /// <paramref name="kind"/>; null otherwise, and <see cref="Errors"/> names each.</summary>
    internal static RunResult? Read(ResultRequest request, string? kind = null) =>
        Errors(request, kind).Count > 0
            ? null
            : new RunResult(
                request.Outcome == ResultRequest.Succeeded,
                request.Report,
                request.Failure?.Reason,
                request.Failure?.InCard ?? false,
                request.Check?.ExitCode,
                request.Check?.Output,
                request.Branch!,
                request.HeadCommit,
                request.AuthMethod,
                request.Usage!.Value,
                request.DurationMs!.Value,
                request.Stderr!,
                request.PermissionDenials is { ValueKind: JsonValueKind.Array } denials ? denials : null,
                request.Screenshots,
                request.Failure?.TimedOut ?? false,
                request.Failure?.ResetsAt,
                request.RemergeDiff,
                request.Pictures,
                request.Outcome == ResultRequest.Stopped);

    /// <summary>Each field of <paramref name="request"/> that is missing or breaks a rule, for a
    /// job of <paramref name="kind"/>, or any job but a check when it is null.</summary>
    internal static Dictionary<string, string[]> Errors(ResultRequest request, string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        WorkerEndpoints.CheckId(request.WorkerId, errors);
        var hasReport = request.Report is { ValueKind: not JsonValueKind.Null };
        var isCheck = kind == Job.RecipeCheck;
        switch (request.Outcome)
        {
            case ResultRequest.Succeeded when isCheck:
                // A check runs no agent: it succeeded when the check ran, and that is its result.
                if (hasReport)
                {
                    errors["report"] = ["must be null for a check, which runs no agent"];
                }

                if (request.Check is null)
                {
                    errors["check"] = ["is required when a check succeeded, since the check ran"];
                }

                if (request.Failure is not null)
                {
                    errors["failure"] = ["must be null when the outcome is succeeded"];
                }

                break;
            case ResultRequest.Succeeded:
                if (!hasReport)
                {
                    errors["report"] = ["is required when the outcome is succeeded"];
                }

                if (request.Failure is not null)
                {
                    errors["failure"] = ["must be null when the outcome is succeeded"];
                }

                break;
            case ResultRequest.Failed:
                if (hasReport)
                {
                    errors["report"] = ["must be null when the outcome is failed"];
                }

                if (request.Failure is not { Reason: { } reason, InCard: not null } || reason.Trim().Length == 0)
                {
                    errors["failure"] = ["must hold a reason and in_card when the outcome is failed"];
                }
                else if (request.Failure is { TimedOut: true, InCard: false })
                {
                    errors["failure"] = ["timed_out is a failure in the card, so in_card must be true"];
                }
                else if (request.Failure is { ResetsAt: not null, InCard: true })
                {
                    errors["failure"] = ["resets_at is for a failure outside the card, so in_card must be false"];
                }

                break;
            case ResultRequest.Stopped:
                // The owner stopped it: nothing was judged, so there is nothing to judge it by.
                if (hasReport)
                {
                    errors["report"] = ["must be null when the outcome is stopped"];
                }

                if (request.Failure is not null)
                {
                    errors["failure"] = ["must be null when the outcome is stopped"];
                }

                if (request.Check is not null)
                {
                    errors["check"] = ["must be null when the outcome is stopped"];
                }

                if (request.Pictures is not null)
                {
                    errors["pictures"] = ["must be null when the outcome is stopped"];
                }

                break;
            default:
                errors["outcome"] = [$"must be {ResultRequest.Succeeded}, {ResultRequest.Failed} or {ResultRequest.Stopped}"];
                break;
        }

        if (request.Check is { } check && (check.ExitCode is null || check.Output is null))
        {
            errors["check"] = ["must be null, or hold exit_code and output"];
        }

        if (string.IsNullOrWhiteSpace(request.Branch) || request.Branch.Length > MaxText)
        {
            errors["branch"] = [$"must be text of 1 to {MaxText} characters"];
        }

        if (request.HeadCommit is { } head && !CommitId().IsMatch(head))
        {
            errors["head_commit"] = ["must be null or a full commit id"];
        }

        if (request.AuthMethod is { Length: > MaxText })
        {
            errors["auth_method"] = [$"must be null or at most {MaxText} characters"];
        }

        if (request.Usage is not { ValueKind: JsonValueKind.Object })
        {
            errors["usage"] = ["must be an object, empty when Claude gave none"];
        }

        if (request.DurationMs is not >= 0)
        {
            errors["duration_ms"] = ["must be a number of milliseconds, 0 or more"];
        }

        if (request.Stderr is null)
        {
            errors["stderr"] = ["must be text, empty when there was none"];
        }

        if (request.PermissionDenials is { ValueKind: not (JsonValueKind.Array or JsonValueKind.Null) })
        {
            errors["permission_denials"] = ["must be null, or an array, empty when none were refused"];
        }

        if (request.Screenshots is { } screenshots)
        {
            CheckScreenshots(screenshots, errors);
        }

        if (request.Pictures is { } pictures)
        {
            if (!isCheck)
            {
                errors["pictures"] = ["must be null for any job but a check"];
            }
            else
            {
                CheckPictures(pictures, request.Screenshots, errors);
            }
        }

        return errors;
    }

    /// <summary>What every PNG begins with.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// A check's pictures break no rule: at most one at each size, each named once, by a name the
    /// result's screenshots list, and each a PNG of at most <see cref="Reading.ScreenshotEndpoints.MostBytes"/>.
    /// </summary>
    private static void CheckPictures(IReadOnlyList<ResultPicture> pictures, ResultScreenshots? screenshots, Dictionary<string, string[]> errors)
    {
        var named = screenshots?.Names ?? [];
        if (pictures.Count > PageShots.Viewports.Count
            || pictures.Any(picture => picture is null || !PageShots.IsName(picture.Name) || !named.Contains(picture.Name!, StringComparer.Ordinal))
            || pictures.Select(picture => picture.Name).Distinct(StringComparer.Ordinal).Count() != pictures.Count)
        {
            errors["pictures"] = [$"must be at most {PageShots.Viewports.Count} pictures, each named once by a name screenshots.names lists"];
            return;
        }

        if (pictures.Any(picture => picture.Png is not { } png || png.Length > Reading.ScreenshotEndpoints.MostBytes || !png.AsSpan().StartsWith(PngSignature)))
        {
            errors["pictures"] = ["must each hold a PNG of at most 5 MB"];
        }
    }

    /// <summary>
    /// A build's pictures break no rule: their names, at most two for each of
    /// <see cref="PageShots.MostPages"/> pages, each once and each a name <see cref="PageShots"/>
    /// makes, since the route that serves one builds its path from it; and a note of at most
    /// <see cref="PageShots.MostNote"/> characters, not empty, or null.
    /// </summary>
    private static void CheckScreenshots(ResultScreenshots screenshots, Dictionary<string, string[]> errors)
    {
        if (screenshots.Names is not { } names
            || names.Count > PageShots.MostPages * PageShots.Viewports.Count
            || names.Distinct(StringComparer.Ordinal).Count() != names.Count
            || !names.All(PageShots.IsName))
        {
            errors["screenshots.names"] = [$"must be a list of at most {PageShots.MostPages * PageShots.Viewports.Count} names, each once, each lowercase letters, digits and hyphens ending in -desktop or -phone"];
        }

        if (screenshots.Note is { } note && (note.Trim().Length == 0 || note.Length > PageShots.MostNote))
        {
            errors["screenshots.note"] = [$"must be null, or text of 1 to {PageShots.MostNote} characters"];
        }
    }

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    /// <summary>A full commit id, 40 or 64 lowercase hex digits, as the table's check says.
    /// <c>\z</c>, since <c>$</c> would let a trailing newline through.</summary>
    [GeneratedRegex(@"^([0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitId();
}