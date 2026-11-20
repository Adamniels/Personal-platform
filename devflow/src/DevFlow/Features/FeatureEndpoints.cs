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
            .WithDescription("The feature as last read, its number, title, web_url, state and reason, null when there is nothing to say; its branch, the name and the commit it was at after the newest change its watch judged, or the one it was made at before any, null before the branch is made; its pull_request to the default branch, number and url, null before one is opened; and its cards in their sub-issue order, each with its number, title, web_url, state, reason, fix_rounds, the input_tokens and output_tokens of all its runs, the screenshots of its head commit as getCard has them, read through getCardScreenshot, and the decisions its latest report marked for the owner, each with the run_id of that report's run, its position among the report's decisions, what was chosen, the alternative, why, the reviewer_view from the newest review of the commit that run left, null without one, and the answer, null before one. Read from what Dev flow stored, never from GitHub; every text from GitHub or an agent is to be shown as text. 404 for a project or a feature the caller does not have. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }
}