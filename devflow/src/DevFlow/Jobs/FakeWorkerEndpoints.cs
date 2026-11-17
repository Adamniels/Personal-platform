using System.Globalization;

using DevFlow.Api;
using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Recipes;
using DevFlow.Workers;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>What the fake worker is asked to do: take the project's next job and end it with the
/// prepared result, whose worker id, branch and head commit it fills itself.</summary>
public sealed record FakeWorkerRun(Guid? ProjectId, ResultRequest? Result);

/// <summary>What the fake worker did: the job it took, its card, and the commit it left on the
/// branch.</summary>
public sealed record FakeWorkerRan(Guid JobId, int Card, string Branch, string HeadCommit);

/// <summary>
/// The fake worker, in the fake mode only, under the <c>Api</c> policy and the origin check, acting
/// on the caller's own project: one call says hello in the project as a worker on the image
/// expected, takes its next job through the same <see cref="Dispatcher"/> a real worker asks, makes
/// a commit on the job's branch in the fake code host, and completes the job with the prepared
/// result through the same code a real worker's result goes through. So an acceptance test drives
/// the whole chain without a container. Left out of the schema, as every fake address is. See "Jobs"
/// in devflow/README.md.
/// </summary>
public static class FakeWorkerEndpoints
{
    /// <summary>The fake worker's id, the same in every project.</summary>
    public static readonly Guid WorkerId = Guid.Parse("0190a8f0-0000-7000-8000-00000000fa4e");

    /// <summary>Its name, as its project's workers list it.</summary>
    public const string Name = "devflow-fake-worker";

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
        [FromServices] TimeProvider time)
    {
        var cancellationToken = context.RequestAborted;
        if (request.ProjectId is not { } projectId || request.Result is not { } prepared)
        {
            return Invalid("project_id and result are both required");
        }

        // The prepared result is checked before a job is taken, with stand ins for what the fake
        // fills, so a result that breaks a rule leaves the card where it was.
        var placeholder = prepared with { WorkerId = WorkerId, Branch = "devflow/0", HeadCommit = null };
        if (JobEndpoints.Errors(placeholder) is { Count: > 0 } errors)
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

        var message = string.Create(CultureInfo.InvariantCulture, $"Build #{job.Card.Number}, by the fake worker");
        var commit = await fake.CommitOnBranch(userId, repository, job.Branch, job.Base, message, new Committer(job.Committer.Name, job.Committer.Email), cancellationToken);
        var sent = prepared with { WorkerId = WorkerId, Branch = job.Branch, HeadCommit = commit };
        var now = time.GetUtcNow();
        await JobEndpoints.Complete(database, caller, job.JobId, WorkerId, JobEndpoints.Read(sent)!, now.AddTicks(-(now.Ticks % 10)), cancellationToken);
        return commit is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The fake's git failed", detail: "the job ran, but no commit was made on its branch")
            : TypedResults.Ok(new FakeWorkerRan(job.JobId, job.Card.Number, job.Branch, commit));
    }

    private static ProblemHttpResult Invalid(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The request breaks a rule", detail: detail);
}