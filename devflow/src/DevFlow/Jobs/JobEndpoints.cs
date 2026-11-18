using System.Text.Json;
using System.Text.RegularExpressions;

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
                "What the worker sends when its job ends, whatever happened: outcome, succeeded with the agent's report or failed with the failure's reason and whether it lies in the card; the check's exit code and the last 200 lines of its output, null when it did not run; the branch, which must be the job's, and its head commit; the auth method; Claude's usage as it came, an object; the duration in milliseconds; the last 200 lines of stderr; and permission_denials, the actions the agent was refused, an array or null. Each is stored on the run as it came. A build's report is read against contracts/devflow/report.schema.json, and one that does not match is a failure in the card; a review's report is its verdict, read against contracts/devflow/verdict.schema.json, and one that does not match is a failure in the run, never a verdict. A review's head_commit is the commit it was handed, or null on a failure; any other is 422. A failure outside the card puts the card back in queued, counting nothing against it; a failure in the card, a report that does not match, a report that stopped, a check that did not run or failed stop it with why; otherwise the card waits, checked, for its pull request. 204. Only for the worker holding the job while it runs, and once; 404 otherwise. 422 naming a field that is missing or breaks a rule.",
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

    private static async Task<Results<NoContent, ProblemHttpResult>> Result(Guid id, ResultRequest request, HttpContext context, [FromServices] UserDb database, [FromServices] WorkerStore workers, [FromServices] WorkerStart start, [FromServices] TimeProvider time)
    {
        if (Read(request) is not { } result)
        {
            return WorkerEndpoints.Invalid(Errors(request));
        }

        var caller = context.Worker();
        var worker = request.WorkerId!.Value;
        if (await workers.Seen(caller, worker, start.Image, context.RequestAborted) == WorkerState.Unknown)
        {
            return WorkerEndpoints.NoSuchJob();
        }

        return await Complete(database, caller, id, worker, result, Now(time), context.RequestAborted) switch
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

    /// <summary>The result, when <paramref name="request"/> breaks no rule; null otherwise, and
    /// <see cref="Errors"/> names each.</summary>
    internal static RunResult? Read(ResultRequest request) =>
        Errors(request).Count > 0
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
                request.PermissionDenials is { ValueKind: JsonValueKind.Array } denials ? denials : null);

    /// <summary>Each field of <paramref name="request"/> that is missing or breaks a rule.</summary>
    internal static Dictionary<string, string[]> Errors(ResultRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        WorkerEndpoints.CheckId(request.WorkerId, errors);
        var hasReport = request.Report is { ValueKind: not JsonValueKind.Null };
        switch (request.Outcome)
        {
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

        return errors;
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