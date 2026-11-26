using DevFlow.Acting;
using DevFlow.Api;
using DevFlow.CodeHost;
using DevFlow.Link;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Stops;

/// <summary>
/// What the owner does from the page with a card or a feature he stopped: "Run again", on either,
/// which asks nothing of the code host, and "Drop", on a card on its own, which takes its pick-up
/// label off as him, with his linked token, and never as the app. Under the <c>Api</c> policy and the
/// origin check, through the door as the caller, so another user's card or feature is none. See
/// "Acting from the page" and "Stops" in devflow/README.md.
/// </summary>
public static class OwnerStopEndpoints
{
    /// <summary>The title of the answer for a card the owner did not stop.</summary>
    public const string CardNotStoppedByYou = "This card was not stopped by you";

    /// <summary>The title of the answer for a feature the owner did not stop.</summary>
    public const string FeatureNotStoppedByYou = "This feature was not stopped by you";

    /// <summary>The title of the answer for dropping a card of a feature.</summary>
    public const string OfAFeature = "A card of a feature is not dropped";

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

        routes.MapPost("/devflow/api/projects/{id:guid}/cards/{number:int}/drop", Drop)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("dropCard")
            .WithSummary("Drop a card the caller stopped, taking its pick-up label off as them")
            .WithDescription("Takes the project's pick-up label off the card's issue as the caller's linked GitHub account, never as the app, and leaves the card ignored, \"Dropped by you\", its branch, any pull request and its issue otherwise as they were. A read of the project keeps it so while the label is off; putting the label back picks it up again, as any card. In order: 404 for a project or card the caller does not have; 409 when the card is not stopped by the caller; 409 when it is a card of a feature, whose pick-up label is its feature's; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once the label is off, or GitHub says the issue does not carry it, and the card is dropped. 409 when GitHub refuses, with its reason; 503 when GitHub did not answer, with nothing changed in Dev flow. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }

    /// <summary>
    /// The owner's "Drop": refused before the code host is asked for whatever Dev flow can tell
    /// itself, then the pick-up label taken off the card's issue as him. A label GitHub says the
    /// issue does not carry is as good as taken off, as it is when the app takes its attention label
    /// off. The project is not read again after it, unlike merging or commenting: what Dev flow
    /// shows has changed here already, and a read racing GitHub's list could find the label it just
    /// took off.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> Drop(
        Guid id,
        int number,
        HttpContext context,
        [FromServices] UserDb database,
        [FromServices] UserTokens tokens,
        [FromServices] ICodeHost host,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await OwnerStops.FindDroppable(database, userId, id, number, cancellationToken) is not { } card)
        {
            return NoSuchCard();
        }

        if (!card.StoppedByOwner)
        {
            return ActionEndpoints.Conflict(CardNotStoppedByYou, "Drop is for a card you stopped.");
        }

        if (card.OfAFeature)
        {
            return ActionEndpoints.Conflict(OfAFeature, "Its feature's pick-up label picks it, not one of its own. Run it again, or take it out of the feature on GitHub.");
        }

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not UserToken.Usable { Token: var access })
        {
            return ActionEndpoints.TokenProblem(userToken, context, time);
        }

        switch (await host.Unlabel(userId, access, card.Repository, card.Number, card.PickupLabel, cancellationToken))
        {
            case UnlabelResult.Removed:
            case UnlabelResult.Refused { Status: StatusCodes.Status404NotFound }:
                return await OwnerStops.Dropped(database, userId, id, number, Now(time), cancellationToken)
                    ? TypedResults.NoContent()
                    : ActionEndpoints.Conflict(CardNotStoppedByYou, "It changed while its label was taken off. Read the list again.");
            case UnlabelResult.Refused { Status: var status, Message: var message }:
                return ActionEndpoints.Conflict("GitHub refused to take the label off", ActionEndpoints.HostSaid(status, message));
            case UnlabelResult.Unavailable { RetryAt: var retryAt }:
                return ActionEndpoints.Unavailable(context, time, retryAt, "It is not known whether the label was taken off; nothing changed in Dev flow.");
            default:
                throw new InvalidOperationException("an unlabel result that is not one of the three");
        }
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