using System.Text.Json;
using System.Text.RegularExpressions;

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
                "What the worker sends when its job ends, whatever happened: outcome, succeeded with the agent's report or failed with the failure's reason and whether it lies in the card; the check's exit code and the last 200 lines of its output, null when it did not run; the branch, which must be the job's, and its head commit; the auth method; Claude's usage as it came, an object; the duration in milliseconds; the last 200 lines of stderr; and permission_denials, the actions the agent was refused, an array or null. Each is stored on the run as it came. A build's report is read against contracts/devflow/report.schema.json, and one that does not match is a failure in the card; a review's report is its verdict, read against contracts/devflow/verdict.schema.json, and one that does not match is a failure in the run, never a verdict. A review's head_commit is the commit it was handed, or null on a failure; any other is 422. A failure outside the card puts the card back in queued, counting nothing against it; a failure in the card, a report that does not match, a report that stopped, a check that did not run or failed stop it with why; otherwise the card waits, checked, for its pull request. A check runs no agent: its report is null, a success means the check ran and carries its result, which is the verdict on the version, and pictures, its address taken at both sizes as PNGs of at most 5 MB, base64, named as screenshots names them; pictures on any other job are 422. 204. Only for the worker holding the job while it runs, and once; 404 otherwise. 422 naming a field that is missing or breaks a rule.",
                StatusCodes.Status404NotFound, StatusCodes.Status422UnprocessableEntity);

        return routes;
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

    private static async Task<Results<NoContent, ProblemHttpResult>> Result(Guid id, ResultRequest request, HttpContext context, [FromServices] UserDb database, [FromServices] WorkerStore workers, [FromServices] WorkerStart start, [FromServices] MergeRequests merges, [FromServices] TimeProvider time)
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

        var recorded = await Complete(database, caller, id, worker, result, Now(time), context.RequestAborted);
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

    /// <summary>Records a result for the run, as a worker's result route does; the fake worker
    /// completes its jobs through it too.</summary>
    internal static Task<Recorded> Complete(UserDb database, WorkerCaller caller, Guid runId, Guid workerId, RunResult result, DateTimeOffset now, CancellationToken cancellationToken) =>
        RunStore.Record(database, caller.UserId, caller.ProjectId, runId, workerId, result, now, cancellationToken);

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
                request.Pictures);

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
            default:
                errors["outcome"] = [$"must be {ResultRequest.Succeeded} or {ResultRequest.Failed}"];
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