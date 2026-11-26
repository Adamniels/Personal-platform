using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>
/// Stopping a run from the page, as the owner: the run is marked, and its worker reads the mark on
/// its next post to the run's log, at most 15 seconds later, ends what runs, keeps what a pushing
/// job did on its branch, and sends the outcome <c>stopped</c>. Under the <c>Api</c> policy and the
/// origin check, through the door as the caller, so another user's run is none. See "Jobs" in
/// devflow/README.md.
/// </summary>
public static class StopEndpoints
{
    /// <summary>The title of the answer for a run that has already ended.</summary>
    public const string AlreadyEnded = "The run has already ended";

    public static IEndpointRouteBuilder MapStops(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/devflow/api/projects/{id:guid}/runs/{run:guid}/stop", Stop)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("stopRun")
            .WithSummary("Stop a run under way, as the caller")
            .WithDescription("Marks the run to stop. Its worker's next post to the run's log, at most 15 seconds later, is answered stop: it ends Claude Code, or the check or the project's start while one runs, SIGTERM to the process and everything it started and SIGKILL 10 seconds later; a build, a fix, a conflict, a feature's fix and a setup run commit what the working copy holds and push their branch when it has a commit beyond its base; and the result comes back with the outcome stopped. The run is recorded stopped_by_owner, never a failure: a card's build, review or fix leaves the card stopped, \"Stopped by you\"; a feature's review, fix or conflict holds the feature, \"Stopped by you\"; a reading, a setup run or a check is put back to run again. 202 once marked, and again for a run already marked, which changes nothing. In order: 404 for a project or run the caller does not have; 409 when the run has already ended. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<Results<Accepted, ProblemHttpResult>> Stop(Guid id, Guid run, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time)
    {
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % 10));
        return await RunStore.RequestStop(database, context.UserId(), id, run, now, context.RequestAborted) switch
        {
            StopAsked.Marked => TypedResults.Accepted((string?)null),
            StopAsked.Ended => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: AlreadyEnded, detail: "Its result is in, so there is nothing left to stop."),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such run", detail: "The project has no such run of yours."),
        };
    }
}