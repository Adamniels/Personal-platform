using System.Globalization;

using DevFlow.Api;
using DevFlow.CodeHost;
using DevFlow.Decisions;
using DevFlow.Merging;
using DevFlow.Protocol;
using DevFlow.PullRequests;
using DevFlow.Recipes;
using DevFlow.Reviews;
using DevFlow.Stops;
using DevFlow.Workers;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>What the fake worker is asked to do: take the project's next job and end it with the
/// prepared result, whose worker id, branch and head commit it fills itself; for a review job the
/// prepared report is the verdict. With <paramref name="LeaveNoCommit"/>, a fix makes no commit, as
/// an agent that changed nothing, and names the commit it was handed; and a build makes none and
/// names none, as a run that failed before anything was pushed. For a build or a fix whose
/// result lists pictures, <paramref name="Pictures"/> holds their bytes by name, base64 in the
/// JSON, which it puts where a worker pushes them. With <paramref name="KeepRunning"/>, it takes the
/// job and sends nothing back, as a worker still at it, so the card stays where taking it put it;
/// no result is needed then.</summary>
public sealed record FakeWorkerRun(Guid? ProjectId, ResultRequest? Result, bool? LeaveNoCommit = null, IReadOnlyDictionary<string, byte[]>? Pictures = null, bool? KeepRunning = null);

/// <summary>What the fake worker did: the job it took, its kind, its card, and the commit it left
/// on the branch, or for a review the commit it reviewed; null for a build that left none.</summary>
public sealed record FakeWorkerRan(Guid JobId, string Kind, int Card, string Branch, string? HeadCommit);

/// <summary>
/// The fake worker, in the fake mode only, under the <c>Api</c> policy and the origin check, acting
/// on the caller's own project: one call says hello in the project as a worker on the image
/// expected, takes its next job through the same <see cref="Dispatcher"/> a real worker asks, makes
/// a commit on the job's branch in the fake code host for a build, on its head for a fix, or none
/// for a review, which names the commit it was handed, and completes the job with the prepared result through the same code a
/// real worker's result goes through. So an acceptance test drives the whole chain without a
/// container. Left out of the schema, as every fake address is. See "Jobs"
/// in devflow/README.md.
/// </summary>
public static class FakeWorkerEndpoints
{
    /// <summary>The fake worker's id, the same in every project.</summary>
    public static readonly Guid WorkerId = Guid.Parse("0190a8f0-0000-7000-8000-00000000fa4e");

    /// <summary>Its name, as its project's workers list it.</summary>
    public const string Name = "devflow-fake-worker";

    /// <summary>Why a job ends when the fake's git made no commit on its branch.</summary>
    public const string NoCommit = "The fake's git made no commit on the job's branch";

    public static IEndpointRouteBuilder MapFakeWorker(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/devflow/api/fake/worker/run", Run)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .ExcludeFromDescription();
        return routes;
    }

    private static async Task<Results<Ok<FakeWorkerRan>, NoContent, ProblemHttpResult>> Run(
        FakeWorkerRun request,
        HttpContext context,
        [FromServices] FakeCodeHost fake,
        [FromServices] Dispatcher dispatcher,
        [FromServices] WorkerStore workers,
        [FromServices] WorkerStart start,
        [FromServices] UserDb database,
        [FromServices] PullRequestOpener opener,
        [FromServices] ReviewPoster poster,
        [FromServices] MergeChecker merger,
        [FromServices] DecisionIssues decisions,
        [FromServices] StopMarker marker,
        [FromServices] TimeProvider time)
    {
        var cancellationToken = context.RequestAborted;
        var keepRunning = request.KeepRunning == true;
        if (request.ProjectId is not { } projectId || (request.Result is null && !keepRunning))
        {
            return Invalid("project_id and result are both required, unless keep_running is true");
        }

        // The prepared result is checked before a job is taken, with stand ins for what the fake
        // fills, so a result that breaks a rule leaves the card where it was.
        if (request.Result is { } given && JobEndpoints.Errors(given with { WorkerId = WorkerId, Branch = "devflow/0", HeadCommit = null }) is { Count: > 0 } errors)
        {
            return Invalid(string.Join("; ", errors.Select(pair => $"result.{pair.Key} {pair.Value[0]}")));
        }

        var userId = context.UserId();
        if (await workers.List(userId, projectId, start, cancellationToken) is not { } list)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");
        }

        var caller = new WorkerCaller(projectId, userId);
        await workers.Hello(caller, new WorkerHello(WorkerId, Name, list.RecipeVersion ?? "fake", start.Image), cancellationToken);
        if (await dispatcher.Next(caller, WorkerId, cancellationToken) is not NextJob.Handed { Job: var job }
            || await workers.RepositoryOf(caller, cancellationToken) is not { } repository)
        {
            return TypedResults.NoContent();
        }

        if (keepRunning)
        {
            // Taken and still running: nothing is pushed and no result comes back.
            return TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card.Number, job.Branch, null));
        }

        // Checked above: a result is given unless the job keeps running.
        var prepared = request.Result!;
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % 10));
        if (job.ToReview is { } toReview)
        {
            // A review reads the commit it was handed and changes nothing.
            var reviewed = prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = prepared.Outcome == ResultRequest.Succeeded ? toReview.Commit : null };
            await JobEndpoints.Complete(database, caller, job.JobId, WorkerId, JobEndpoints.Read(reviewed)!, now, cancellationToken);
            // Its copy is posted, an approved card checked for a merge and its decisions' issues
            // opened, and a stopped card marked, on the next minute or right after for a real
            // worker; the fake worker waits for all of it.
            await poster.PostPending(projectId, userId, cancellationToken);
            await merger.CheckPending(projectId, userId, cancellationToken);
            await decisions.OpenPending(projectId, userId, cancellationToken);
            await marker.MarkPending(projectId, userId, cancellationToken);
            return TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card.Number, job.Branch, toReview.Commit));
        }

        var committer = new Committer(job.Committer.Name, job.Committer.Email);

        // Each picture the result lists, on pr-screenshots under the commit's folder, as a worker
        // pushes them; one the request gave no bytes for stays missing.
        void PutPictures(ResultRequest sent, string commit)
        {
            foreach (var name in sent.Screenshots?.Names ?? [])
            {
                if (request.Pictures?.GetValueOrDefault(name) is { } bytes)
                {
                    fake.PutFile(userId, repository, $"{PageShots.Folder(job.Card.Number, commit)}/{name}.png", PageShots.Branch, bytes);
                }
            }
        }

        if (job.ToFix is { } toFix)
        {
            // A fix continues the card's branch from the commit it was handed, as a push would.
            var fixedUp = request.LeaveNoCommit == true
                ? toFix.Commit
                : await fake.CommitOnBranch(userId, repository, job.Branch, job.Branch, string.Create(CultureInfo.InvariantCulture, $"Fix #{job.Card.Number}, by the fake worker"), committer, cancellationToken);
            var result = fixedUp is null
                ? prepared with { WorkerId = WorkerId, Outcome = ResultRequest.Failed, Report = null, Failure = new RunFailure(NoCommit, false), Check = null, Branch = job.Branch, HeadCommit = null }
                : prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = fixedUp };
            if (fixedUp is not null)
            {
                PutPictures(result, fixedUp);
            }

            await JobEndpoints.Complete(database, caller, job.JobId, WorkerId, JobEndpoints.Read(result)!, now, cancellationToken);
            await marker.MarkPending(projectId, userId, cancellationToken);
            return fixedUp is null
                ? TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The fake's git failed", detail: "no commit was made on the job's branch, so the job ended as a failure outside the card and the card is fixing again")
                : TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card.Number, job.Branch, fixedUp));
        }

        var message = string.Create(CultureInfo.InvariantCulture, $"Build #{job.Card.Number}, by the fake worker");
        // A repository has a commit on its default branch before anything is built on it, so a
        // pull request has a base to go into; the fake's own git starts empty.
        if (!await fake.HasBranch(userId, repository, job.Base, cancellationToken))
        {
            await fake.CommitOnBranch(userId, repository, job.Base, job.Base, "The first commit, by the fake worker", committer, cancellationToken);
        }

        if (request.LeaveNoCommit == true)
        {
            // Nothing pushed, as a run that failed before it got that far: the branch stays where
            // it was, and the result names no commit.
            var unpushed = prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = null };
            await JobEndpoints.Complete(database, caller, job.JobId, WorkerId, JobEndpoints.Read(unpushed)!, now, cancellationToken);
            await marker.MarkPending(projectId, userId, cancellationToken);
            return TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card.Number, job.Branch, null));
        }

        var commit = await fake.CommitOnBranch(userId, repository, job.Branch, job.Base, message, committer, cancellationToken);
        // With no commit the chain did not get as far as the prepared result says, so the job ends
        // as a failure outside the card instead: the card goes back to queued, as it would for a
        // real worker whose push failed, rather than moving on with nothing on its branch.
        var sent = commit is null
            ? prepared with { WorkerId = WorkerId, Outcome = ResultRequest.Failed, Report = null, Failure = new RunFailure(NoCommit, false), Check = null, Branch = job.Branch, HeadCommit = null }
            : prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = commit };
        if (commit is not null)
        {
            PutPictures(sent, commit);
        }

        await JobEndpoints.Complete(database, caller, job.JobId, WorkerId, JobEndpoints.Read(sent)!, now, cancellationToken);
        // A real worker's result is answered without waiting for GitHub, and its pull request opens
        // on the next minute; the fake worker waits for it, so a test sees it as soon as it answers.
        await opener.OpenPending(projectId, userId, cancellationToken);
        await marker.MarkPending(projectId, userId, cancellationToken);
        return commit is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The fake's git failed", detail: "no commit was made on the job's branch, so the job ended as a failure outside the card and the card is queued again")
            : TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card.Number, job.Branch, commit));
    }

    private static ProblemHttpResult Invalid(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The request breaks a rule", detail: detail);
}