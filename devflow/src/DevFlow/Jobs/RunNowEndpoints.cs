using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>
/// "Run now anyway" on a feature and on a card on its own, and "Back to the schedule", from the
/// page, as the owner. Under the <c>Api</c> policy and the origin check, through the door as the
/// caller, so another user's project is none. See "Schedules" under "Jobs" in devflow/README.md.
/// </summary>
public static class RunNowEndpoints
{
    private const string Rest = " Without a valid token, 401 with an empty body; a change from another origin, 403.";

    private const string Covers = "It skips the project's schedules and nothing else: a pause, the project's or everything's, still holds it. It lasts until it has nothing left to run, every card of a feature and the feature itself waiting for the caller, done, stopped, refused, needing answers, held or ignored, when Dev flow ends it, so a card added or sent back later keeps to the schedules.";

    public static IEndpointRouteBuilder MapRunNow(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/devflow/api/projects/{id:guid}/features/{number:int}/run-now", (Guid id, int number, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
                Set(RunNowOf.Feature, id, number, context, database, time))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("runFeatureNow")
            .WithSummary("Run a feature now, outside its project's schedules, as the caller")
            .WithDescription("Every job of the feature, its cards' builds, reviews, fixes and readings, its conflicts, its review and its fix, is handed out whether or not its project is inside its schedules, and nothing else of the project is. " + Covers + " Setting it again changes nothing. 204. In order: 404 for a project or a feature the caller does not have; 409 when it has nothing left to run." + Rest)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        routes.MapPost("/devflow/api/projects/{id:guid}/cards/{number:int}/run-now", (Guid id, int number, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
                Set(RunNowOf.Card, id, number, context, database, time))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("runCardNow")
            .WithSummary("Run a card on its own now, outside its project's schedules, as the caller")
            .WithDescription("Every job of the card, its build, review, fixes and readings, is handed out whether or not its project is inside its schedules, and nothing else of the project is. " + Covers + " Setting it again changes nothing. 204. In order: 404 for a project or a card the caller does not have; 409 when it is a card of a feature, which runs with its feature, so its feature is run now instead, and when it has nothing left to run." + Rest)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        routes.MapDelete("/devflow/api/projects/{id:guid}/features/{number:int}/run-now", (Guid id, int number, HttpContext context, [FromServices] UserDb database) =>
                TakeBack(RunNowOf.Feature, id, number, context, database))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("runFeatureBySchedule")
            .WithSummary("Take back running a feature now, as the caller")
            .WithDescription("The feature keeps to its project's schedules again; once this answers, no job is handed out by the override it took back, and a run under way finishes. Taking back one not set changes nothing. 204; 404 for a project or a feature the caller does not have." + Rest)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapDelete("/devflow/api/projects/{id:guid}/cards/{number:int}/run-now", (Guid id, int number, HttpContext context, [FromServices] UserDb database) =>
                TakeBack(RunNowOf.Card, id, number, context, database))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("runCardBySchedule")
            .WithSummary("Take back running a card now, as the caller")
            .WithDescription("The card keeps to its project's schedules again; once this answers, no job is handed out by the override it took back, and a run under way finishes. Taking back one not set changes nothing. 204; 404 for a project or a card the caller does not have." + Rest)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Set(RunNowOf of, Guid id, int number, HttpContext context, UserDb database, TimeProvider time) =>
        await RunNowStore.Set(database, context.UserId(), id, of, number, Now(time), context.RequestAborted) switch
        {
            RunNowOutcome.Set => TypedResults.NoContent(),
            RunNowOutcome.NothingToRun => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "This has nothing left to run",
                detail: of == RunNowOf.Feature
                    ? "Every card of the feature, and the feature itself, wait for you, are done, stopped or refused."
                    : "The card waits for you, is done, stopped or refused."),
            RunNowOutcome.CardOfAFeature => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "This card runs with its feature",
                detail: "Run its feature now anyway, on the feature's page."),
            _ => NoSuch(of),
        };

    private static async Task<Results<NoContent, ProblemHttpResult>> TakeBack(RunNowOf of, Guid id, int number, HttpContext context, UserDb database) =>
        await RunNowStore.TakeBack(database, context.UserId(), id, of, number, context.RequestAborted)
            ? TypedResults.NoContent()
            : NoSuch(of);

    private static ProblemHttpResult NoSuch(RunNowOf of) => of == RunNowOf.Feature
        ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such feature", detail: "You have no feature with that number in that project.")
        : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such card", detail: "You have no card with that number in that project.");

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }
}