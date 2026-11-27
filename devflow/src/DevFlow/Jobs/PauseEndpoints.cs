using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>What pausing or continuing sends: <c>paused</c>, true to pause and false to
/// continue.</summary>
public sealed record PauseRequest(bool? Paused);

/// <summary>What "Stop now" on a project answers: how many of its runs under way were told to
/// stop.</summary>
public sealed record StopNowAnswer(int Stopped);

/// <summary>
/// Pausing a project, and every project of the caller, and stopping a project now, from the page,
/// as the owner. Under the
/// <c>Api</c> policy and the origin check, through the door as the caller, so another user's
/// project is none. See "Jobs" in devflow/README.md.
/// </summary>
public static class PauseEndpoints
{
    private const string Rest = " Without a valid token, 401 with an empty body; a change from another origin, 403.";

    public static IEndpointRouteBuilder MapPauses(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPut("/devflow/api/projects/{id:guid}/paused", PauseProject)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("pauseProject")
            .WithSummary("Pause a project, or continue it, as the caller")
            .WithDescription("paused true pauses the project: no job of it is handed out, of any kind, a setup's and a check among them, until it is continued; a run under way finishes and its result is recorded as always, and reading GitHub goes on. false continues it. Pausing what is paused, or continuing what runs, changes nothing, the time it was paused kept. 204. In order: 422 when paused is missing; 404 for a project the caller does not have." + Rest)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapPut("/devflow/api/paused", PauseAll)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("pauseEverything")
            .WithSummary("Pause every project of the caller, or continue them")
            .WithDescription("paused true pauses every project of the caller, laid over each project's own pause: nothing new starts in any of them, as a project's pause holds it. false continues them, and a project paused on its own stays paused. Pausing what is paused, or continuing what runs, changes nothing, the time it was paused kept. 204; 422 when paused is missing." + Rest)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        routes.MapPost("/devflow/api/projects/{id:guid}/stop-now", StopNow)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("stopProjectNow")
            .WithSummary("Pause a project and stop every run of it under way, as the caller")
            .WithDescription("Pauses the project, as pauseProject does, and then, in the same transaction, marks every run of it under way to stop, as stopRun marks one: each worker's next post to its run's log, at most 15 seconds later, is answered stop, it keeps what a pushing job did on its branch, and the run is recorded stopped_by_owner, its card \"Stopped by you\". Nothing of another project is stopped. stopped, how many runs were marked, a run marked before among them; 0 with none under way, and the project is paused all the same. 404 for a project the caller does not have." + Rest)
            .Produces<StopNowAnswer>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapGet("/devflow/api/paused", async (HttpContext context, [FromServices] UserDb database) =>
                TypedResults.Ok(await Pauses.All(database, context.UserId(), context.RequestAborted)))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getPaused")
            .WithSummary("Whether every project of the caller is paused")
            .WithDescription("paused, whether the caller paused everything, and paused_at, when, null while not. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> PauseProject(Guid id, PauseRequest request, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time)
    {
        if (request.Paused is not { } paused)
        {
            return Missing();
        }

        return await Pauses.SetProject(database, context.UserId(), id, paused, Now(time), context.RequestAborted)
            ? TypedResults.NoContent()
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");
    }

    private static async Task<Results<Ok<StopNowAnswer>, ProblemHttpResult>> StopNow(Guid id, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
        await Pauses.StopNow(database, context.UserId(), id, Now(time), context.RequestAborted) is { } stopped
            ? TypedResults.Ok(new StopNowAnswer(stopped))
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");

    private static async Task<Results<NoContent, ProblemHttpResult>> PauseAll(PauseRequest request, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time)
    {
        if (request.Paused is not { } paused)
        {
            return Missing();
        }

        await Pauses.SetAll(database, context.UserId(), paused, Now(time), context.RequestAborted);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult Missing() =>
        TypedResults.Problem(new HttpValidationProblemDetails(new Dictionary<string, string[]> { ["paused"] = ["must be true or false"] })
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "The request breaks a rule",
            Detail = "paused must be true or false",
        });

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }
}