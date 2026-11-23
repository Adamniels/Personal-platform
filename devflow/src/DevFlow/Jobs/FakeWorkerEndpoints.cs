using System.Globalization;

using DevFlow.Api;
using DevFlow.CodeHost;
using DevFlow.Decisions;
using DevFlow.Features;
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
/// prepared report is the verdict, as it is for a whole feature's review, and for a conflict job the prepared remerge diff is sent with
/// the merge commit it makes, when the result passed its check. With <paramref name="LeaveNoCommit"/>, a fix makes no commit, as
/// an agent that changed nothing, and names the commit it was handed; and a build makes none and
/// names none, as a run that failed before anything was pushed. For a build or a fix whose
/// result lists pictures, <paramref name="Pictures"/> holds their bytes by name, base64 in the
/// JSON, which it puts where a worker pushes them. With <paramref name="KeepRunning"/>, it takes the
/// job and sends nothing back, as a worker still at it, so the card stays where taking it put it;
/// no result is needed then. With <paramref name="ForCheck"/>, the prepared result is a check's and
/// ends the check the worker is handed, as a check's worker sends it; without it, a check the worker
/// is handed first is passed, as a recipe that works, and the next job taken, so a test of anything
/// after setting up never meets it. With <paramref name="Setup"/>, it says hello as a worker built
/// from no recipe, the base image itself, as a setup run's worker is started, and is handed the
/// project's setup jobs: a setup's run commits on the setup's branch, and what is staged on that
/// branch in the fake's world is what Dev flow validates there. Without <paramref name="SettingsWait"/>,
/// a project whose settings step is not done has its settings taken as accepted, as they are, as if
/// the owner had saved them, before the job is asked for, so a test of anything after setting up
/// never meets the step, as it never meets the check; with it, the step is left waiting.</summary>
public sealed record FakeWorkerRun(Guid? ProjectId, ResultRequest? Result, bool? LeaveNoCommit = null, IReadOnlyDictionary<string, byte[]>? Pictures = null, bool? KeepRunning = null, bool? ForCheck = null, bool? Setup = null, bool? SettingsWait = null);

/// <summary>What the fake worker did: the job it took, its kind, its card, or for a conflict or a
/// reading its feature, null for a check, the commit it left on the branch, or for a review the commit it reviewed,
/// null for a build or a conflict that left none and for a reading; and the brief the job carried,
/// so a test reads what the agent would have been asked.</summary>
public sealed record FakeWorkerRan(Guid JobId, string Kind, int? Card, string Branch, string? HeadCommit, string Brief);

/// <summary>
/// The fake worker, in the fake mode only, under the <c>Api</c> policy and the origin check, acting
/// on the caller's own project: one call says hello in the project as a worker on the image
/// expected, takes its next job through the same <see cref="Dispatcher"/> a real worker asks, makes
/// a commit on the job's branch in the fake code host for a build, on its head for a fix, of a card
/// or of a whole feature, the second only when its result passed, a merge
/// commit of the default branch's on a feature's branch for a conflict, or none for a review of a
/// card or of a whole feature, which names the commit it was handed, and completes the job with the prepared result through the same code a
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
        [FromServices] Values.ProjectValues values,
        [FromServices] PullRequestOpener opener,
        [FromServices] ReviewPoster poster,
        [FromServices] MergeChecker merger,
        [FromServices] DecisionIssues decisions,
        [FromServices] LandedCards landed,
        [FromServices] FeatureEnds ends,
        [FromServices] FeatureReadiness readiness,
        [FromServices] StopMarker marker,
        [FromServices] Readings.QuestionComments questions,
        [FromServices] Readings.RefusalMarker refusals,
        [FromServices] Setup.SetupLoop setups,
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
        var forCheck = request.ForCheck == true;
        if (request.Result is { } given && JobEndpoints.Errors(given with { WorkerId = WorkerId, Branch = "devflow/0", HeadCommit = null }, forCheck ? Job.RecipeCheck : null) is { Count: > 0 } errors)
        {
            return Invalid(string.Join("; ", errors.Select(pair => $"result.{pair.Key} {pair.Value[0]}")));
        }

        var userId = context.UserId();
        if (await workers.List(userId, projectId, start, cancellationToken) is not { } list)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");
        }

        var caller = new WorkerCaller(projectId, userId);
        if (request.SettingsWait != true)
        {
            await Projects.ProjectSettingsStore.AcceptAsTheyAre(database, userId, projectId, Now(time), cancellationToken);
        }

        await workers.Hello(caller, new WorkerHello(WorkerId, Name, request.Setup == true ? WorkerApi.NoVersion : list.RecipeVersion ?? "fake", start.Image), cancellationToken);
        // Whether a finished feature needs its review, or is ready, is decided on the minute; a
        // worker asks after a minute has, so the fake decides it first, and a test that reads a
        // project and then asks finds its feature's review as a worker would.
        await readiness.DecidePending(projectId, userId, cancellationToken);
        if (await dispatcher.Next(caller, WorkerId, cancellationToken) is not NextJob.Handed { Job: var job }
            || await workers.RepositoryOf(caller, cancellationToken) is not { } repository)
        {
            return TypedResults.NoContent();
        }

        if (job.Kind == Job.RecipeCheck && !forCheck)
        {
            // Not the job the test prepared a result for: the check passes, as a recipe that works
            // would, and the worker asks again.
            await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, PassedCheck(job.Branch), Now(time), cancellationToken);
            if (await dispatcher.Next(caller, WorkerId, cancellationToken) is not NextJob.Handed { Job: var next })
            {
                return TypedResults.NoContent();
            }

            job = next;
        }

        if (forCheck)
        {
            if (job.Kind != Job.RecipeCheck)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not a check", detail: $"the job handed out is a {job.Kind}, not a check; it was taken and is left running");
            }

            // A check reads the commit it was handed and pushes nothing, so it names no commit.
            var checkedOne = request.Result! with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = null };
            await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, JobEndpoints.Read(checkedOne, Job.RecipeCheck)!, Now(time), cancellationToken);
            return TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, null, job.Branch, null, job.Brief));
        }

        if (keepRunning)
        {
            // Taken and still running: nothing is pushed and no result comes back.
            return TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card?.Number, job.Branch, null, job.Brief));
        }

        // Checked above: a result is given unless the job keeps running.
        var prepared = request.Result!;
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % 10));
        if (job.ToRead is not null)
        {
            // A reading reads the code and changes nothing, so it names no commit.
            var read = prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = null };
            await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, JobEndpoints.Read(read)!, now, cancellationToken);
            // The questions' comment is posted, a refused card marked and a stopped card marked, on
            // the next minute for a real worker; the fake worker waits for each, and the label of a
            // card queued again comes off.
            await questions.PostPending(projectId, userId, cancellationToken);
            await refusals.MarkPending(projectId, userId, cancellationToken);
            await marker.MarkPending(projectId, userId, cancellationToken);
            return TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card?.Number, job.Branch, null, job.Brief));
        }

        if (job.ToReview is { } toReview)
        {
            // A review reads the commit it was handed and changes nothing.
            var reviewed = prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = prepared.Outcome == ResultRequest.Succeeded ? toReview.Commit : null };
            await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, JobEndpoints.Read(reviewed)!, now, cancellationToken);
            // Its copy is posted, an approved card checked for a merge, its decisions' issues opened,
            // a landed card's issue told, the end of a feature whose cards are now all done run, and
            // a stopped card marked, on the next minute or right after for a real worker; the fake
            // worker waits for all of it.
            await poster.PostPending(projectId, userId, cancellationToken);
            await merger.CheckPending(projectId, userId, cancellationToken);
            await decisions.OpenPending(projectId, userId, cancellationToken);
            await landed.TellPending(projectId, userId, cancellationToken);
            await ends.EndPending(projectId, userId, cancellationToken);
            await readiness.DecidePending(projectId, userId, cancellationToken);
            await marker.MarkPending(projectId, userId, cancellationToken);
            await setups.TendPending(projectId, userId, cancellationToken);
            return TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card?.Number, job.Branch, toReview.Commit, job.Brief));
        }

        if (job.ToReviewFeature is { } toReviewFeature)
        {
            // A whole feature's review reads the commit it was handed and changes nothing.
            var reviewed = prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = prepared.Outcome == ResultRequest.Succeeded ? toReviewFeature.Commit : null };
            await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, JobEndpoints.Read(reviewed)!, now, cancellationToken);
            // Its copy is posted and whether the feature is ready read, on the next minute for a
            // real worker; the fake worker waits for both.
            await poster.PostPending(projectId, userId, cancellationToken);
            await readiness.DecidePending(projectId, userId, cancellationToken);
            return TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card?.Number, job.Branch, toReviewFeature.Commit, job.Brief));
        }

        var committer = new Committer(job.Committer.Name, job.Committer.Email);

        if (job.Kind == Job.Setup)
        {
            // A setup commits on its branch, a fix from the commit it was handed and a first run
            // from the default branch, as the worker pushes whatever its agent left; what it pushed
            // is validated as a real result is, and its pull request opened as on the minute.
            if (!await fake.HasBranch(userId, repository, job.Base, cancellationToken))
            {
                await fake.CommitOnBranch(userId, repository, job.Base, job.Base, "The first commit, by the fake worker", committer, cancellationToken);
            }

            var from = job.ToFix is not null || await fake.HasBranch(userId, repository, job.Branch, cancellationToken) ? job.Branch : job.Base;
            var setupCommit = request.LeaveNoCommit == true
                ? job.ToFix?.Commit
                : await fake.CommitOnBranch(userId, repository, job.Branch, from, "Set up Dev flow, by the fake worker", committer, cancellationToken);
            var setupResult = JobEndpoints.Read(prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = setupCommit, Check = null })!;
            if (await Setup.SetupFiles.Validated(fake, userId, repository, setupResult, cancellationToken) is not { } validated)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The fake did not answer", detail: "what the setup pushed could not be read to validate it; the job is left running");
            }

            await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, validated, now, cancellationToken);
            await setups.TendPending(projectId, userId, cancellationToken);
            return TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, null, job.Branch, setupCommit, job.Brief));
        }

        if (job.ToResolve is { } toResolve)
        {
            // A conflict brings the default branch's commit into the feature's branch as one merge
            // commit, pushed, as a worker pushes it, only when the agent succeeded and its check
            // passed; the remerge diff is the prepared one.
            var passed = prepared.Outcome == ResultRequest.Succeeded && prepared.Check is { ExitCode: 0 } && request.LeaveNoCommit != true;
            var merged = passed
                ? await fake.MergeOnBranch(userId, repository, job.Branch, toResolve.FeatureCommit, toResolve.DefaultCommit,
                    string.Create(CultureInfo.InvariantCulture, $"Merge {job.Base} into {job.Branch}, by the fake worker"), committer, cancellationToken)
                : null;
            var resolved = passed && merged is null
                ? prepared with { WorkerId = WorkerId, Outcome = ResultRequest.Failed, Report = null, Failure = new RunFailure(NoCommit, false), Check = null, Branch = job.Branch, HeadCommit = null, RemergeDiff = null }
                : prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = merged, RemergeDiff = merged is null ? null : prepared.RemergeDiff };
            await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, JobEndpoints.Read(resolved)!, now, cancellationToken);
            // The feature's end goes on, and its review is decided, on the next minute for a real
            // worker; the fake waits for both.
            await ends.EndPending(projectId, userId, cancellationToken);
            await readiness.DecidePending(projectId, userId, cancellationToken);
            return passed && merged is null
                ? TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The fake's git failed", detail: "no merge commit was made on the feature's branch, so the job ended as a failure outside it and the feature is resolving again")
                : TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card?.Number, job.Branch, merged, job.Brief));
        }

        // Each picture the result lists, on pr-screenshots under the commit's folder, as a worker
        // pushes them; one the request gave no bytes for stays missing.
        void PutPictures(ResultRequest sent, string commit)
        {
            foreach (var name in sent.Screenshots?.Names ?? [])
            {
                if (request.Pictures?.GetValueOrDefault(name) is { } bytes)
                {
                    fake.PutFile(userId, repository, $"{PageShots.Folder(job.Card!.Number, commit)}/{name}.png", PageShots.Branch, bytes);
                }
            }
        }

        if (job.ToFix is { } feature && job.Kind == Job.FeatureFix)
        {
            // A feature's fix continues the feature's branch from the commit it was handed, pushed
            // by the app, listed among the branch's changes as GitHub lists a push, only when the agent succeeded with a done report and
            // its check passed; with leave_no_commit it names the commit it was handed, as an agent that changed nothing.
            var passed = prepared.Outcome == ResultRequest.Succeeded
                && prepared.Report is { } report && Report.TryRead(report, out var read, out _) && read.Outcome == ReportOutcome.Done
                && prepared.Check is { ExitCode: 0 };
            var pushed = request.LeaveNoCommit == true ? feature.Commit
                : passed ? await fake.Push(userId, repository, job.Branch, string.Create(CultureInfo.InvariantCulture, $"Fix the feature #{job.Card!.Number}, by the fake worker"), FakeCodeHost.AppLogin, cancellationToken)
                : null;
            var lost = passed && request.LeaveNoCommit != true && pushed is null;
            var answered = lost
                ? prepared with { WorkerId = WorkerId, Outcome = ResultRequest.Failed, Report = null, Failure = new RunFailure(NoCommit, false), Check = null, Branch = job.Branch, HeadCommit = null }
                : prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = pushed };
            await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, JobEndpoints.Read(answered)!, now, cancellationToken);
            // Whether the feature is reviewed again is decided on the minute, once its watch has
            // read the push; the fake worker runs it, as for a review.
            await readiness.DecidePending(projectId, userId, cancellationToken);
            return lost
                ? TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The fake's git failed", detail: "no commit was made on the feature's branch, so the job ended as a failure outside it and the feature is fixing again")
                : TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card?.Number, job.Branch, pushed, job.Brief));
        }

        if (job.ToFix is { } toFix)
        {
            // A fix continues the card's branch from the commit it was handed, as a push would.
            var fixedUp = request.LeaveNoCommit == true
                ? toFix.Commit
                : await fake.CommitOnBranch(userId, repository, job.Branch, job.Branch, string.Create(CultureInfo.InvariantCulture, $"Fix #{job.Card!.Number}, by the fake worker"), committer, cancellationToken);
            var result = fixedUp is null
                ? prepared with { WorkerId = WorkerId, Outcome = ResultRequest.Failed, Report = null, Failure = new RunFailure(NoCommit, false), Check = null, Branch = job.Branch, HeadCommit = null }
                : prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = fixedUp };
            if (fixedUp is not null)
            {
                PutPictures(result, fixedUp);
            }

            await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, JobEndpoints.Read(result)!, now, cancellationToken);
            await marker.MarkPending(projectId, userId, cancellationToken);
            return fixedUp is null
                ? TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The fake's git failed", detail: "no commit was made on the job's branch, so the job ended as a failure outside the card and the card is fixing again")
                : TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card?.Number, job.Branch, fixedUp, job.Brief));
        }

        var message = string.Create(CultureInfo.InvariantCulture, $"Build #{job.Card!.Number}, by the fake worker");
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
            await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, JobEndpoints.Read(unpushed)!, now, cancellationToken);
            await marker.MarkPending(projectId, userId, cancellationToken);
            return TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card?.Number, job.Branch, null, job.Brief));
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

        await JobEndpoints.Complete(database, values, caller, job.JobId, WorkerId, JobEndpoints.Read(sent)!, now, cancellationToken);
        // A real worker's result is answered without waiting for GitHub, and its pull request opens
        // on the next minute; the fake worker waits for it, so a test sees it as soon as it answers.
        await opener.OpenPending(projectId, userId, cancellationToken);
        await marker.MarkPending(projectId, userId, cancellationToken);
        return commit is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The fake's git failed", detail: "no commit was made on the job's branch, so the job ended as a failure outside the card and the card is queued again")
            : TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Kind, job.Card?.Number, job.Branch, commit, job.Brief));
    }

    /// <summary>What the fake sends for a check it passes unasked: the check ran and exited 0, and
    /// the project has no pages to show.</summary>
    public const string PassedOutput = "The fake's check passed";

    private static RunResult PassedCheck(string branch) =>
        new(true, null, null, false, 0, PassedOutput, branch, null, null, System.Text.Json.JsonSerializer.SerializeToElement(new { }), 0, "", Screenshots: new ResultScreenshots([], PageShots.NoPages));

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    private static ProblemHttpResult Invalid(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The request breaks a rule", detail: detail);
}