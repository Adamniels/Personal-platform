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

/// <summary>A feature as the project's page lists it, with its cards.</summary>
/// <param name="Number">Its parent issue's number.</param>
/// <param name="Title">The parent's title as last read; plain text to show as text.</param>
/// <param name="WebUrl">Where the parent is on GitHub.</param>
/// <param name="State"><c>queued</c>, <c>held</c> or <c>ignored</c>, which picking sets, or one of
/// the states the feature's later parts move it through.</param>
/// <param name="Reason">Why it is where it is, a sentence, or null when there is nothing to
/// say.</param>
/// <param name="Priority"><c>high</c>, <c>none</c> or <c>low</c>, its parent's.</param>
/// <param name="ChangedAt">When its state or its reason last changed.</param>
/// <param name="Cards">Its cards, in their sub-issue order.</param>
public sealed record FeatureView(int Number, string Title, string WebUrl, string State, string? Reason, string Priority, DateTimeOffset ChangedAt, IReadOnlyList<CardView> Cards);

/// <summary>What <c>GET /devflow/api/projects/{id}/cards</c> answers: the features, and the cards
/// on their own.</summary>
public sealed record CardList(IReadOnlyList<FeatureView> Features, IReadOnlyList<CardView> Cards);

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
            .WithSummary("The project's features and cards")
            .WithDescription("The project's features as its last read found them, in the order their cards would be taken, priority and then the oldest: each with its number, title and web_url, its state, the reason it is there, null when there is nothing to say, its priority, changed_at, and its cards in their sub-issue order; and every card on its own, newest change first. A card has its number, title and web_url, its state, the reason it is there, null when there is nothing to say, what it waits for, its priority and changed_at. Read from what Dev flow stored, never from GitHub. 404 for a project the caller does not have. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapGet("/devflow/api/projects/{id:guid}/cards/{number:int}", async Task<Results<Ok<CardStoryView>, ProblemHttpResult>> (Guid id, int number, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
                await CardStory.Of(database, context.UserId(), id, number, time.GetUtcNow(), context.RequestAborted) is { } story
                    ? TypedResults.Ok(story)
                    : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such card", detail: "You have no card with that number in that project."))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getCard")
            .WithSummary("A card's whole story so far")
            .WithDescription("The card as last read, its number, title, web_url, state and reason, and its pull_request, number and url, null before one was opened; each of its runs, newest first, with its kind, the worker it was handed to, when it started and finished, how long it took, its result and, for a failure, its reason and whether it lay in the card, the auth method, its tokens, input_tokens + cache_creation_input_tokens + output_tokens, and its cache reads beside them, each null when Claude gave none, the check's exit code and the last lines of its output, null when it did not run, the commit it left, has_log, whether it has lines of its log, read through readRunLog, and log_pruned, whether it had a log that was deleted once kept its 90 days, both false for a run from before logs were kept; the latest report in its form, the latest build's, null when no build has handed one back; and each review, newest first, with the commit the worker reviewed, its verdict, summary, findings, each blocking or not with its file, line, what and why, the reviewer's view on each decision for the owner, when it was recorded, whether its copy is posted, and whether it is current, for the card's head commit; and the screenshots of its head commit, the commit, the pictures' names paired desktop and phone by page, read through getCardScreenshot, and the worker's note, null when its newest build that succeeded tried none. And the feature it is a card of, its number and title, null for a card on its own; and run_now, what the page says of running it outside its project's schedules: outside_schedule, whether its project is outside them now, something_to_run, whether it has something left to run, false for a card of a feature, whose feature is run now instead, and on, whether runCardNow's override of it holds. Read from what Dev flow stored, never from GitHub; every text from GitHub or an agent is to be shown as text. 404 for a project or a card the caller does not have. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }
}