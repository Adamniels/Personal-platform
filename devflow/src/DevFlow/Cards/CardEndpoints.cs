using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Cards;

/// <summary>A card as the project's page lists it.</summary>
/// <param name="Number">The issue's number.</param>
/// <param name="Title">The issue's title as last read; plain text to show as text.</param>
/// <param name="WebUrl">Where the issue is on GitHub.</param>
/// <param name="State"><c>queued</c>, <c>held</c>, <c>ignored</c>, or one of the states a run
/// moves it through: <c>building</c>, <c>checking</c>, <c>reviewing</c>, <c>fixing</c>,
/// <c>waiting_for_owner</c>, <c>done</c> and <c>stopped</c>.</param>
/// <param name="Reason">Why it is where it is, a sentence, or null when there is nothing to
/// say.</param>
/// <param name="WaitsFor">What it is blocked by that is still open, as <c>#12</c> or
/// <c>owner/name#12</c>.</param>
/// <param name="Priority"><c>high</c>, <c>none</c> or <c>low</c>.</param>
/// <param name="ChangedAt">When its state, its reason or what it waits for last changed.</param>
public sealed record CardView(int Number, string Title, string WebUrl, string State, string? Reason, IReadOnlyList<string> WaitsFor, string Priority, DateTimeOffset ChangedAt);

/// <summary>What <c>GET /devflow/api/projects/{id}/cards</c> answers.</summary>
public sealed record CardList(IReadOnlyList<CardView> Cards);

/// <summary>A project's cards over HTTP, under the <c>Api</c> policy. See "Cards" in
/// devflow/README.md.</summary>
public static class CardEndpoints
{
    public static IEndpointRouteBuilder MapCards(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/projects/{id:guid}/cards", async Task<Results<Ok<CardList>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] UserDb database) =>
                await CardStore.List(database, context.UserId(), id, context.RequestAborted) is { } list
                    ? TypedResults.Ok(list)
                    : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist"))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("listCards")
            .WithSummary("The project's cards")
            .WithDescription("Every card of the project as its last read found it, newest change first: its number, title and web_url, its state, the reason it is there, null when there is nothing to say, what it waits for, its priority and changed_at. Read from what Dev flow stored, never from GitHub. 404 for a project the caller does not have. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapGet("/devflow/api/projects/{id:guid}/cards/{number:int}", async Task<Results<Ok<CardStoryView>, ProblemHttpResult>> (Guid id, int number, HttpContext context, [FromServices] UserDb database) =>
                await CardStory.Of(database, context.UserId(), id, number, context.RequestAborted) is { } story
                    ? TypedResults.Ok(story)
                    : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such card", detail: "You have no card with that number in that project."))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getCard")
            .WithSummary("A card's whole story so far")
            .WithDescription("The card as last read, its number, title, web_url, state and reason, and its pull_request, number and url, null before one was opened; each of its runs, newest first, with its kind, the worker it was handed to, when it started and finished, how long it took, its result and, for a failure, its reason and whether it lay in the card, the auth method, the tokens in and out, the check's exit code and the last lines of its output, null when it did not run, and the commit it left; and the latest report in its form, null when no run has handed one back. Read from what Dev flow stored, never from GitHub; every text from GitHub or an agent is to be shown as text. 404 for a project or a card the caller does not have. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }
}