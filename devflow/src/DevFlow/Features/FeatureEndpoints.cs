using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Features;

/// <summary>A project's features over HTTP, under the <c>Api</c> policy. See "A feature's page" in
/// devflow/README.md.</summary>
public static class FeatureEndpoints
{
    public static IEndpointRouteBuilder MapFeatures(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/projects/{id:guid}/features/{number:int}", async Task<Results<Ok<FeatureStoryView>, ProblemHttpResult>> (Guid id, int number, HttpContext context, [FromServices] UserDb database) =>
                await FeatureStory.Of(database, context.UserId(), id, number, context.RequestAborted) is { } story
                    ? TypedResults.Ok(story)
                    : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such feature", detail: "You have no feature with that number in that project."))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getFeature")
            .WithSummary("A feature's whole story so far")
            .WithDescription("The feature as last read, its number, title, web_url, state and reason, null when there is nothing to say; its branch, the name and the commit it was at after the newest change its watch judged, or the one it was made at before any, null before the branch is made; its pull_request to the default branch, number and url, null before one is opened; and its cards in their sub-issue order, each with its number, title, web_url, state, reason, fix_rounds, the tokens of all its runs, input_tokens + cache_creation_input_tokens + output_tokens, and their cache reads beside them, the screenshots of its head commit as getCard has them, read through getCardScreenshot, and the decisions its latest report marked for the owner, each with the run_id of that report's run, its position among the report's decisions, what was chosen, the alternative, why, the reviewer_view from the newest review of the commit that run left, null without one, and the answer, fine or sent_back, null before one, can_send_back, whether the card has landed in the feature's branch of a feature neither merged nor closed, so it can be sent back, and sent_back, its last send back, how many times, the pull request it had landed in and the owner's comments, null when it was never sent back; its own runs, each with has_log and log_pruned as getCard has them, with tokens and cache_reads, their sums by the same definition, and the reviews of the whole feature, newest first; fix_rounds, the rounds its reviews have started; and rounds, each fix of the whole feature newest first, as getCard has a card's rounds. Read from what Dev flow stored, never from GitHub; every text from GitHub or an agent is to be shown as text. 404 for a project or a feature the caller does not have. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapPost("/devflow/api/projects/{id:guid}/features/{number:int}/decisions/{run:guid}/{position:int}/fine", Fine)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("answerDecisionFine")
            .WithSummary("Answers a decision of a feature's card \"Fine as it is\", in Dev flow alone")
            .WithDescription("Keeps the answer fine to the decision at position among the decisions of the report of run, a build or fix of a card of the feature, when that decision is marked for the owner and that report is still the card's latest, the one getFeature shows. Kept once, as first given: answering again changes nothing and answers the answer kept, with its answered_at. Never written to GitHub. 404 for a project, feature, run or decision the caller does not have, or one not marked for the owner; 409 when the card has a newer report than that run's, so the page is to be read again. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<Results<Ok<DecisionAnswer>, ProblemHttpResult>> Fine(
        Guid id, int number, Guid run, int position, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
        await DecisionAnswers.Fine(database, context.UserId(), id, number, run, position, time.GetUtcNow(), context.RequestAborted) switch
        {
            AnswerOutcome.Kept kept => TypedResults.Ok(kept.Answer),
            AnswerOutcome.Superseded => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "A newer report", detail: "The card has a newer report than the one this decision is from. Read the page again."),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "No such decision", detail: "You have no decision marked for you by that number in that run of the feature."),
        };
}