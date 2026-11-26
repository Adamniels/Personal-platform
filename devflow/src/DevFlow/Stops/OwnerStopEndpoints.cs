using DevFlow.Acting;
using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Stops;

/// <summary>
/// What the owner does from the page with a card or a feature he stopped: "Run again", on either,
/// which asks nothing of the code host. Under the <c>Api</c> policy and the origin check, through the
/// door as the caller, so another user's card or feature is none. See "Acting from the page" and
/// "Stops" in devflow/README.md.
/// </summary>
public static class OwnerStopEndpoints
{
    /// <summary>The title of the answer for a card the owner did not stop.</summary>
    public const string CardNotStoppedByYou = "This card was not stopped by you";

    /// <summary>The title of the answer for a feature the owner did not stop.</summary>
    public const string FeatureNotStoppedByYou = "This feature was not stopped by you";

    public static IEndpointRouteBuilder MapOwnerStops(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/devflow/api/projects/{id:guid}/cards/{number:int}/run-again", RunCardAgain)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("runCardAgain")
            .WithSummary("Run a card the caller stopped again")
            .WithDescription("Puts a card the caller stopped back where the run they stopped started from: a build's in queued, a review's in reviewing, a fix's in fixing in the same round, with the reason that state has. Nothing counts against it: its failures in a row and its fix rounds are as they were. In order: 404 for a project or card the caller does not have; 409 when the card is not stopped by the caller, one Dev flow stopped included, which their comment runs again. 204 once it is back. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        routes.MapPost("/devflow/api/projects/{id:guid}/features/{number:int}/run-again", RunFeatureAgain)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("runFeatureAgain")
            .WithSummary("Run a feature the caller stopped again")
            .WithDescription("Clears the hold the caller's stop of one of the feature's own runs, a review, a fix or a conflict, put on it, so its next job is handed out, and it says what its state says. In order: 404 for a project or feature the caller does not have; 409 when the feature is not held by the caller's stop. 204 once it is released. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RunCardAgain(Guid id, int number, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
        await OwnerStops.RunCardAgain(database, context.UserId(), id, number, Now(time), context.RequestAborted) switch
        {
            OwnerStopResult.Done => TypedResults.NoContent(),
            OwnerStopResult.NotStoppedByYou => ActionEndpoints.Conflict(CardNotStoppedByYou, "Run again is for a card you stopped. A card Dev flow stopped runs again on your comment."),
            _ => NoSuchCard(),
        };

    private static async Task<Results<NoContent, ProblemHttpResult>> RunFeatureAgain(Guid id, int number, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
        await OwnerStops.RunFeatureAgain(database, context.UserId(), id, number, Now(time), context.RequestAborted) switch
        {
            OwnerStopResult.Done => TypedResults.NoContent(),
            OwnerStopResult.NotStoppedByYou => ActionEndpoints.Conflict(FeatureNotStoppedByYou, "Run again is for a feature held because you stopped one of its runs."),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such feature", detail: "You have no feature with that number in that project."),
        };

    internal static ProblemHttpResult NoSuchCard() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such card", detail: "You have no card with that number in that project.");

    /// <summary>Now, to the microsecond Postgres keeps.</summary>
    private static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }
}