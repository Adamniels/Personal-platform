using System.Globalization;

using DevFlow.Api;
using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Features;
using DevFlow.Link;
using DevFlow.Reading;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Acting;

/// <summary>What <c>POST /devflow/api/waiting/{id}/merge</c> takes.</summary>
/// <param name="HeadCommit">The commit the page showed, which the pull request's head must still
/// be.</param>
public sealed record MergeRequest(string? HeadCommit);

/// <summary>What <c>POST /devflow/api/waiting/{id}/send-back</c> takes.</summary>
/// <param name="Comment">What the owner wrote, posted trimmed and with nothing added.</param>
public sealed record SendBackRequest(string? Comment);

/// <summary>
/// Acting on what waits, from the page, as the caller on the code host and never as the app:
/// merging a waiting pull request, and sending a waiting or stopped one back with a comment, which
/// the runner reads as the owner's input on its next turn, or Dev flow's next read of a card's. Under the <c>Api</c> policy and the origin check. Everything
/// that can be refused without the code host is refused before it is asked; the commit the page
/// showed travels to the code host, which refuses to merge a pull request that moved on, so a
/// stale page can cost a click and never a wrong merge. Nothing the runner owns, a label or a
/// status, is ever written. See "Acting from the page" in devflow/README.md.
/// </summary>
public static class ActionEndpoints
{
    /// <summary>The title of the answer when the pull request is not as the page showed it.</summary>
    public const string Changed = "The pull request has changed since this was shown";

    /// <summary>The title of the answer when CI has not passed on a feature's head.</summary>
    public const string CiNotPassed = "CI has not passed on this commit";

    /// <summary>The most characters a comment may have once trimmed.</summary>
    public const int MostComment = 10_000;

    public static IEndpointRouteBuilder MapActions(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/devflow/api/waiting/{id:guid}/merge", Merge)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("mergeWaiting")
            .WithSummary("Merge a waiting pull request, as the caller")
            .WithDescription("Merges with a merge commit, as the caller's linked GitHub account, only while the pull request's head is head_commit, the commit the page showed. In order: 404 for an item the caller does not have; 422 without head_commit; 409 when it is stopped, sent back, or its checks are not green; 409 when head_commit is not the stored head; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once merged and the project read again. 409 when GitHub says the head moved, after the project is read again, and when GitHub will not merge it or refuses, with its reason; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapPost("/devflow/api/waiting/{id:guid}/send-back", SendBack)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("sendBackWaiting")
            .WithSummary("Send a waiting or stopped pull request back, with a comment as the caller")
            .WithDescription("Posts comment, trimmed and with nothing added, on the pull request as the caller's linked GitHub account, which the runner reads as the owner's input on its next turn. In order: 404 for an item the caller does not have; 422 when comment is missing, empty once trimmed, or longer than 10,000 characters; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once posted, with the item marked sent back at once and the project read again. 409 when GitHub refuses, with its reason; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapPost("/devflow/api/projects/{id:guid}/cards/{number:int}/merge", MergeCard)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("mergeCard")
            .WithSummary("Merge a card's pull request that waits, as the caller")
            .WithDescription("Merges a card of Dev flow's own, from Waiting for you, with a merge commit, as the caller's linked GitHub account and never as the app, only while its pull request's head is head_commit, the commit its run built and checked and the page showed. In order: 404 for a project or card the caller does not have; 422 without head_commit; 409 when the card is not waiting for the caller with a pull request; 409 when the project is stopped, since something landed on its default branch that Dev flow did not put there; 409 when head_commit is not the commit the run built, after the project is read again; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once merged and the project read again, which makes the card done. 409 when GitHub says the head moved, after the project is read again, and when GitHub will not merge it or refuses, with its reason; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapPost("/devflow/api/projects/{id:guid}/cards/{number:int}/send-back", SendBackCard)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("sendBackCard")
            .WithSummary("Send a card of Dev flow's own back, with a comment as the caller")
            .WithDescription("Posts comment, trimmed and with nothing added, as the caller's linked GitHub account and never as the app: on the card's pull request for a card that waits for the caller, which the read of the project that follows takes, as any comment of his past the newest already taken, and starts a fix run with, which is not a review round; on the card's issue for a card that has stopped, which that read takes as a comment newer than the stop and runs the card again with; and on the card's issue for a card of a feature that has landed in the feature's branch, of a feature neither merged nor closed, which that read takes as a comment newer than the card's last run and sends the card back with, to be built again on a new branch. In order: 404 for a project or card the caller does not have; 422 when comment is missing, empty once trimmed, or longer than 10,000 characters; 409 when the card neither waits for the caller with its pull request, nor has stopped, nor has landed in an open feature; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once posted and the project read again. 409 when GitHub refuses, with its reason; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapPost("/devflow/api/projects/{id:guid}/features/{number:int}/send-back", SendBackFeature)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("sendBackFeature")
            .WithSummary("Send a whole feature of Dev flow's own back, with a comment as the caller")
            .WithDescription("Posts comment, trimmed and with nothing added, as the caller's linked GitHub account and never as the app, on the feature's pull request to the default branch, which the read of the project that follows takes, as any comment of his past the newest already taken, and starts a fix of the whole feature with, which is not one of its review's rounds. In order: 404 for a project or feature the caller does not have; 422 when comment is missing, empty once trimmed, or longer than 10,000 characters; 409 when the feature is neither ready nor waiting for the caller with its pull request; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once posted and the project read again. 409 when GitHub refuses, with its reason; 503 when GitHub did not answer. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapPost("/devflow/api/projects/{id:guid}/features/{number:int}/decisions/{run:guid}/{position:int}/send-back", SendBackDecision)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("sendBackDecision")
            .WithSummary("Send a feature's card back from one of its decisions, with a comment as the caller")
            .WithDescription("Posts a comment as the caller's linked GitHub account and never as the app, on the issue of the card whose report holds the decision at position of the report of run: the decision quoted, what was chosen and the alternative, then comment, trimmed. The read of the project that follows takes it as a comment newer than the card's last run and sends the card back with all of it, to be built again on a new branch, and the decision is kept answered sent_back. In order: 404 for a project, feature, run or decision the caller does not have, or one not marked for the owner; 422 when comment is missing, empty once trimmed, or longer than 10,000 characters; 409 when the card has a newer report than that run's; 409 when the decision is already answered; 409 when its card has not landed in the feature's branch, or the feature is merged or closed; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once posted, the answer kept and the project read again. 409 when GitHub refuses, with its reason; 503 when GitHub did not answer, with no answer kept. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapGet("/devflow/api/projects/{id:guid}/features/{number:int}/merge", ConfirmFeature)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getFeatureMerge")
            .WithSummary("What merging a feature confirms, for the dialog that asks first")
            .WithDescription("The feature's number and title, the default branch it merges into, its pull_request, number and url, the head_commit the merge names, review, approved when its newest review approved a commit that covers the head, changes when its newest review asked for changes, with its blocking findings as open_findings, and not_reviewed otherwise, and unanswered, how many decisions its cards marked for the caller have no answer, each of which merging confirms. Read from what Dev flow stored, never from GitHub; every text from GitHub or the reviewer is to be shown as text. In order: 404 for a project or feature the caller does not have; 409 as mergeFeature refuses, when it is not ready or waiting for the caller with its pull request, the project is stopped, or CI has not passed on its head. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        routes.MapPost("/devflow/api/projects/{id:guid}/features/{number:int}/merge", MergeFeature)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("mergeFeature")
            .WithSummary("Merge a feature into the default branch, as the caller")
            .WithDescription("Merges the feature's pull request into the default branch with a merge commit, as the caller's linked GitHub account and never as the app, only while its head is head_commit, the commit the page showed. In order: 404 for a project or feature the caller does not have; 422 without head_commit; 409 when the feature is not ready or waiting for the caller with its pull request; 409 when the project is stopped; 409 when CI has not passed on the feature's head, as the minute last read it; 409 when head_commit is not that head, after the project is read again; 409 when GitHub is not linked or the link has expired; then GitHub is asked. 204 once merged and the project read again, which makes the feature merged. 409 when GitHub says the head moved, after the project is read again, and when GitHub will not merge it or refuses, with its reason; 503 when GitHub did not answer. The review's verdict is not among the refusals: the confirmation says it. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }

    /// <summary>
    /// The owner's merge of a whole feature into the default branch, the one way part 4 reaches it:
    /// the same merge as a card's, with his linked token and a merge commit naming the head the page
    /// showed, refused before the code host is asked for whatever Dev flow can tell itself, a stopped
    /// project and CI that has not passed on the head among it. The review's verdict refuses
    /// nothing, since the owner reviews a feature himself; the confirmation says what it is. See
    /// "Merging a feature" in devflow/README.md.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> MergeFeature(
        Guid id,
        int number,
        MergeRequest request,
        HttpContext context,
        [FromServices] ActionStore store,
        [FromServices] UserTokens tokens,
        [FromServices] ICodeHost host,
        [FromServices] ProjectRefresher refresher,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await store.FindFeature(userId, id, number, cancellationToken) is not { } feature)
        {
            return NoSuchFeature();
        }

        if (string.IsNullOrEmpty(request.HeadCommit))
        {
            return Invalid("head_commit", "is missing; it is the commit the page showed");
        }

        if (NotMergeable(feature) is { } refused)
        {
            return refused;
        }

        if (!string.Equals(request.HeadCommit, feature.Head, StringComparison.Ordinal))
        {
            await ReadAgain(refresher, feature.ProjectId, userId, time, cancellationToken);
            return ChangedSinceShown();
        }

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not UserToken.Usable { Token: var access })
        {
            return TokenProblem(userToken, context, time);
        }

        return await Merged(await host.Merge(userId, access, feature.Repository, feature.PullRequestNumber!.Value, feature.Head!, cancellationToken), refresher, feature.ProjectId, userId, context, time, cancellationToken);
    }

    /// <summary>What the dialog that asks before a feature's merge shows, read from what Dev flow
    /// stored: the same refusals as the merge's own, before it, and otherwise the review's state
    /// and the decisions merging confirms, counted as the feature's page counts them.</summary>
    private static async Task<Results<Ok<FeatureMergeConfirmation>, ProblemHttpResult>> ConfirmFeature(
        Guid id,
        int number,
        HttpContext context,
        [FromServices] ActionStore store,
        [FromServices] PlatformData.UserDb database)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await store.FindFeature(userId, id, number, cancellationToken) is not { } feature)
        {
            return NoSuchFeature();
        }

        if (NotMergeable(feature) is { } refused)
        {
            return refused;
        }

        return await FeatureStory.Of(database, userId, id, number, cancellationToken) is { } story
            ? TypedResults.Ok(FeatureMergeConfirmation.Of(story, feature.Into!, feature.PullRequestNumber!.Value, feature.PullRequestUrl!, feature.Head!))
            : NoSuchFeature();
    }

    /// <summary>Why the feature cannot be merged now, from what Dev flow stored, or null when it
    /// can: it is not the owner's turn with its pull request, the project is stopped, or CI has not
    /// passed on its head. The order the route and the confirmation refuse in.</summary>
    private static ProblemHttpResult? NotMergeable(FeatureTarget feature)
    {
        if (!FeatureMergeOffers.States.Contains(feature.State, StringComparer.Ordinal) || feature.PullRequestNumber is null)
        {
            return Conflict("This feature is not waiting for you", "A feature is merged once it is ready for you, or waits for you, with its pull request open.");
        }

        if (feature.Stopped)
        {
            return Conflict("This project is stopped", "Something landed on a branch it watches that Dev flow did not put there. Look at it and resume the project first.");
        }

        return feature.Head is null || feature.Into is null || !string.Equals(feature.CiPassed, feature.Head, StringComparison.Ordinal)
            ? Conflict(CiNotPassed, "Merge it once CI has passed on the feature's head.")
            : null;
    }

    /// <summary>
    /// The owner's "Send back" on a decision of a feature's card: the card's send back, with the
    /// decision quoted above his comment, posted as him on the card's issue, which the read that
    /// follows takes; and the decision kept answered <c>sent_back</c> once it is posted, never
    /// before, so a comment GitHub refused leaves it unanswered. Only for a decision the feature's
    /// page shows unanswered, of a card that can be sent back. See "Sending a card back" in
    /// devflow/README.md.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SendBackDecision(
        Guid id,
        int number,
        Guid run,
        int position,
        SendBackRequest request,
        HttpContext context,
        [FromServices] UserDb database,
        [FromServices] ActionStore store,
        [FromServices] UserTokens tokens,
        [FromServices] ICodeHost host,
        [FromServices] ProjectRefresher refresher,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        var found = await DecisionAnswers.ToSendBack(database, userId, id, number, run, position, cancellationToken);
        if (found is DecisionFound.NoSuchDecision)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such decision", detail: "You have no decision marked for you by that number in that run of the feature.");
        }

        if (Comment(request.Comment, "write what the developer should do") is { } invalid)
        {
            return invalid;
        }

        if (found is not DecisionFound.Shown decision)
        {
            return Conflict("A newer report", "The card has a newer report than the one this decision is from. Read the page again.");
        }

        if (decision.Answered)
        {
            return Conflict("This decision is answered", "An answer is kept as first given.");
        }

        if (!decision.Sendable || await store.FindCard(userId, id, decision.Card, cancellationToken) is not { Sendable: true } card)
        {
            return Conflict("This card cannot be sent back now", "A card is sent back once it has landed in a feature that is not merged or closed.");
        }

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not UserToken.Usable { Token: var access })
        {
            return TokenProblem(userToken, context, time);
        }

        var body = SendingBack.DecisionComment(decision.Chosen, decision.Alternative, request.Comment!.Trim());
        switch (await host.Comment(userId, access, card.Repository, card.Number, body, cancellationToken))
        {
            case CommentResult.Posted:
                await DecisionAnswers.SentBack(database, userId, id, card.Number, run, position, time.GetUtcNow(), cancellationToken);
                await ReadAgain(refresher, card.ProjectId, userId, time, cancellationToken);
                return TypedResults.NoContent();
            case CommentResult.Refused { Status: var status, Message: var message }:
                return Conflict("GitHub refused the comment", HostSaid(status, message));
            case CommentResult.Unavailable { RetryAt: var retryAt }:
                // It may have been posted before the answer was lost, so the project is read again,
                // which sends the card back if it was; the decision is left unanswered.
                await ReadAgain(refresher, card.ProjectId, userId, time, cancellationToken);
                return Unavailable(context, time, retryAt, "It is not known whether the comment was posted; the project was read again.");
            default:
                throw new InvalidOperationException("a comment result that is not one of the three");
        }
    }

    /// <summary>
    /// The owner's "Send the whole feature back": his comment, posted as him on the feature's pull
    /// request, as part 1's sending back is, which the read that follows takes as the one comment
    /// read there as an input and starts a fix of the whole feature with. Only for a feature ready
    /// or waiting for him with its pull request. Dev flow itself writes nothing to the feature here,
    /// so the comment on GitHub is the one record of it. See "Fixing a feature" in
    /// devflow/README.md.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SendBackFeature(
        Guid id,
        int number,
        SendBackRequest request,
        HttpContext context,
        [FromServices] ActionStore store,
        [FromServices] UserTokens tokens,
        [FromServices] ICodeHost host,
        [FromServices] ProjectRefresher refresher,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await store.FindFeature(userId, id, number, cancellationToken) is not { } feature)
        {
            return NoSuchFeature();
        }

        if (Comment(request.Comment, "write what the developer should change") is { } invalid)
        {
            return invalid;
        }

        if (feature.State is not ("ready" or "waiting_for_owner") || feature.PullRequestNumber is not { } pull)
        {
            return Conflict("This feature cannot be sent back now", "A feature is sent back while it is ready or waits for you, with its pull request open.");
        }

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not UserToken.Usable { Token: var access })
        {
            return TokenProblem(userToken, context, time);
        }

        switch (await host.Comment(userId, access, feature.Repository, pull, request.Comment!.Trim(), cancellationToken))
        {
            case CommentResult.Posted:
                await ReadAgain(refresher, feature.ProjectId, userId, time, cancellationToken);
                return TypedResults.NoContent();
            case CommentResult.Refused { Status: var status, Message: var message }:
                return Conflict("GitHub refused the comment", HostSaid(status, message));
            case CommentResult.Unavailable { RetryAt: var retryAt }:
                // It may have been posted before the answer was lost, so the project is read again.
                await ReadAgain(refresher, feature.ProjectId, userId, time, cancellationToken);
                return Unavailable(context, time, retryAt, "It is not known whether the comment was posted; the project was read again.");
            default:
                throw new InvalidOperationException("a comment result that is not one of the three");
        }
    }

    /// <summary>
    /// The owner's send back of a card of Dev flow's own, part 1's send back: his comment, posted
    /// as him, which the read that follows takes as the one input Dev flow reads from GitHub. For a
    /// card waiting for him it goes on its pull request and starts a fix; for a stopped one it goes
    /// on its issue, which always exists whatever became of its pull request, and runs it again, as
    /// "Comment to run again"; for a card landed in an open feature's branch it goes on its issue
    /// too, closed or not, and sends it back, "Send back" on the feature's page. Dev flow itself
    /// writes nothing to the card here, so the comment on GitHub is the one record of it. See
    /// "Fixing", "Stops" and "Sending a card back" in devflow/README.md.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SendBackCard(
        Guid id,
        int number,
        SendBackRequest request,
        HttpContext context,
        [FromServices] ActionStore store,
        [FromServices] UserTokens tokens,
        [FromServices] ICodeHost host,
        [FromServices] ProjectRefresher refresher,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await store.FindCard(userId, id, number, cancellationToken) is not { } card)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such card", detail: "You have no card with that number in that project.");
        }

        if (Comment(request.Comment, "write what the developer should do") is { } invalid)
        {
            return invalid;
        }

        // A stopped card runs again on his comment on its issue, and a card landed in an open
        // feature's branch is sent back there; one waiting for him is sent back on its pull request.
        int conversation;
        if (card.State == "stopped" || card.Sendable)
        {
            conversation = card.Number;
        }
        else if (card.State == "waiting_for_owner" && card.PullRequestNumber is { } pull)
        {
            conversation = pull;
        }
        else
        {
            return Conflict("This card cannot be sent back now", "A card is sent back while it waits for you, or once it has landed in a feature that is not merged or closed, or run again once it has stopped.");
        }

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not UserToken.Usable { Token: var access })
        {
            return TokenProblem(userToken, context, time);
        }

        switch (await host.Comment(userId, access, card.Repository, conversation, request.Comment!.Trim(), cancellationToken))
        {
            case CommentResult.Posted:
                await ReadAgain(refresher, card.ProjectId, userId, time, cancellationToken);
                return TypedResults.NoContent();
            case CommentResult.Refused { Status: var status, Message: var message }:
                return Conflict("GitHub refused the comment", HostSaid(status, message));
            case CommentResult.Unavailable { RetryAt: var retryAt }:
                // It may have been posted before the answer was lost, so the project is read again.
                await ReadAgain(refresher, card.ProjectId, userId, time, cancellationToken);
                return Unavailable(context, time, retryAt, "It is not known whether the comment was posted; the project was read again.");
            default:
                throw new InvalidOperationException("a comment result that is not one of the three");
        }
    }

    /// <summary>Why a comment from the page is refused, or null when it is one to post: missing,
    /// empty once trimmed, or longer than <see cref="MostComment"/>.</summary>
    private static ProblemHttpResult? Comment(string? written, string what)
    {
        var comment = written?.Trim();
        return comment is null ? Invalid("comment", "is missing")
            : comment.Length == 0 ? Invalid("comment", $"is empty; {what}")
            : comment.Length > MostComment ? Invalid("comment", string.Create(CultureInfo.InvariantCulture, $"is {comment.Length:N0} characters; it may have at most {MostComment:N0}"))
            : null;
    }

    /// <summary>
    /// The owner's merge of a card's pull request, the same refusals and the same merge as an
    /// item's, with what a card adds: it must wait for him, and its project must not be stopped,
    /// since nothing a stopped project produced is merged until he resumes it. The commit named is
    /// the one its run built and Dev flow's check passed on, so a pull request someone pushed to
    /// since is refused, here or by GitHub, never merged unseen.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> MergeCard(
        Guid id,
        int number,
        MergeRequest request,
        HttpContext context,
        [FromServices] ActionStore store,
        [FromServices] UserTokens tokens,
        [FromServices] ICodeHost host,
        [FromServices] ProjectRefresher refresher,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await store.FindCard(userId, id, number, cancellationToken) is not { } card)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such card", detail: "You have no card with that number in that project.");
        }

        if (string.IsNullOrEmpty(request.HeadCommit))
        {
            return Invalid("head_commit", "is missing; it is the commit the page showed");
        }

        if (card.State != "waiting_for_owner" || card.PullRequestNumber is not { } pull || card.HeadCommit is not { } head)
        {
            return Conflict("This card is not waiting for you", "Only a card whose pull request waits for you is merged from the list.");
        }

        if (card.Stopped)
        {
            return Conflict("This project is stopped", "Something landed on its default branch that Dev flow did not put there. Look at it and resume the project first.");
        }

        if (!string.Equals(request.HeadCommit, head, StringComparison.Ordinal))
        {
            await ReadAgain(refresher, card.ProjectId, userId, time, cancellationToken);
            return ChangedSinceShown();
        }

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not UserToken.Usable { Token: var access })
        {
            return TokenProblem(userToken, context, time);
        }

        return await Merged(await host.Merge(userId, access, card.Repository, pull, head, cancellationToken), refresher, card.ProjectId, userId, context, time, cancellationToken);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SendBack(
        Guid id,
        SendBackRequest request,
        HttpContext context,
        [FromServices] ActionStore store,
        [FromServices] UserTokens tokens,
        [FromServices] ICodeHost host,
        [FromServices] ProjectRefresher refresher,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await store.Find(userId, id, cancellationToken) is not { } item)
        {
            return NoSuchItem();
        }

        if (Comment(request.Comment, "write what the runner should do") is { } invalid)
        {
            return invalid;
        }

        var comment = request.Comment!.Trim();

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not UserToken.Usable { Token: var access })
        {
            return TokenProblem(userToken, context, time);
        }

        switch (await host.Comment(userId, access, item.Repository, item.Number, comment, cancellationToken))
        {
            case CommentResult.Posted:
                await store.MarkSentBack(userId, id, cancellationToken);
                await ReadAgain(refresher, item.ProjectId, userId, time, cancellationToken);
                return TypedResults.NoContent();
            case CommentResult.Refused { Status: var status, Message: var message }:
                return Conflict("GitHub refused the comment", HostSaid(status, message));
            case CommentResult.Unavailable { RetryAt: var retryAt }:
                // It may have been posted before the answer was lost, so the list is read again.
                await ReadAgain(refresher, item.ProjectId, userId, time, cancellationToken);
                return Unavailable(context, time, retryAt, "It is not known whether the comment was posted; the list was read again.");
            default:
                throw new InvalidOperationException("a comment result that is not one of the three");
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Merge(
        Guid id,
        MergeRequest request,
        HttpContext context,
        [FromServices] ActionStore store,
        [FromServices] UserTokens tokens,
        [FromServices] ICodeHost host,
        [FromServices] ProjectRefresher refresher,
        [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var cancellationToken = context.RequestAborted;
        if (await store.Find(userId, id, cancellationToken) is not { } item)
        {
            return NoSuchItem();
        }

        if (string.IsNullOrEmpty(request.HeadCommit))
        {
            return Invalid("head_commit", "is missing; it is the commit the page showed");
        }

        if (item.Kind == "stopped")
        {
            return Conflict("This pull request is stopped", "The runner stopped it: do not merge it as it is. Comment to run it again.");
        }

        if (item.SentBack)
        {
            return Conflict("This pull request was sent back", "The runner has your comment, and it comes back here when it is ready again.");
        }

        if (!item.CanMerge)
        {
            return Conflict("The checks are not green on this commit yet", "Merge it once ci-ok and review-ok pass on it.");
        }

        if (!string.Equals(request.HeadCommit, item.HeadCommit, StringComparison.Ordinal))
        {
            await ReadAgain(refresher, item.ProjectId, userId, time, cancellationToken);
            return ChangedSinceShown();
        }

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not UserToken.Usable { Token: var access })
        {
            return TokenProblem(userToken, context, time);
        }

        return await Merged(await host.Merge(userId, access, item.Repository, item.Number, item.HeadCommit, cancellationToken), refresher, item.ProjectId, userId, context, time, cancellationToken);
    }

    /// <summary>What the page is answered for the code host's answer to a merge, the project read
    /// again first whenever the answer may have changed what it shows.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> Merged(MergeResult result, ProjectRefresher refresher, Guid projectId, Guid userId, HttpContext context, TimeProvider time, CancellationToken cancellationToken)
    {
        switch (result)
        {
            case MergeResult.Merged:
                await ReadAgain(refresher, projectId, userId, time, cancellationToken);
                return TypedResults.NoContent();
            case MergeResult.HeadMoved:
                await ReadAgain(refresher, projectId, userId, time, cancellationToken);
                return ChangedSinceShown();
            case MergeResult.NotMergeable { Message: var message }:
                return Conflict("GitHub would not merge it", message is null ? "GitHub did not say why." : $"GitHub said: {message}");
            case MergeResult.Refused { Status: var status, Message: var message }:
                return Conflict("GitHub refused the merge", HostSaid(status, message));
            case MergeResult.Unavailable { RetryAt: var retryAt }:
                // It may have merged before the answer was lost, so the list is read again.
                await ReadAgain(refresher, projectId, userId, time, cancellationToken);
                return Unavailable(context, time, retryAt, "It is not known whether it merged; the list was read again.");
            default:
                throw new InvalidOperationException("a merge result that is not one of the five");
        }
    }

    /// <summary>Why there is no token to act with.</summary>
    internal static ProblemHttpResult TokenProblem(UserToken token, HttpContext context, TimeProvider time) => token switch
    {
        UserToken.NotLinked => Conflict("GitHub is not linked", "Link your GitHub account on the Projects tab to merge and comment as yourself."),
        UserToken.NeedsRelink => Conflict("Your GitHub link has expired", "Link GitHub again on the Projects tab to merge and comment as yourself."),
        UserToken.Unavailable { RetryAt: var retryAt } => Unavailable(context, time, retryAt, "Nothing was sent. Try again in a moment."),
        _ => throw new ArgumentException("a usable token is not a problem", nameof(token)),
    };

    /// <summary>Asks for the project to be read, and waits for it as the refresh route
    /// does, so the list the page reads next shows what the code host says now. A read still
    /// under way after <see cref="WaitingEndpoints.RefreshWait"/> goes on without the answer.</summary>
    private static async Task ReadAgain(ProjectRefresher refresher, Guid projectId, Guid userId, TimeProvider time, CancellationToken cancellationToken)
    {
        try
        {
            await refresher.Request(projectId, userId).WaitAsync(WaitingEndpoints.RefreshWait, time, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }

    internal static string HostSaid(int status, string? message) =>
        message is null
            ? string.Create(CultureInfo.InvariantCulture, $"GitHub answered {status}.")
            : string.Create(CultureInfo.InvariantCulture, $"GitHub answered {status}: {message}");

    private static ProblemHttpResult NoSuchFeature() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such feature", detail: "You have no feature with that number in that project.");

    private static ProblemHttpResult NoSuchItem() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such item", detail: "Nothing waits for you with that id.");

    private static ProblemHttpResult ChangedSinceShown() =>
        Conflict(Changed, "It was read again. Look at it before you merge.");

    internal static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);

    internal static ProblemHttpResult Invalid(string field, string reason) =>
        TypedResults.Problem(new HttpValidationProblemDetails(new Dictionary<string, string[]> { [field] = [reason] })
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "The request breaks a rule",
            Detail = $"{field} {reason}",
        });

    /// <summary>503, with <c>Retry-After</c> when the code host said when to try again.</summary>
    internal static ProblemHttpResult Unavailable(HttpContext context, TimeProvider time, DateTimeOffset? retryAt, string detail)
    {
        if (retryAt is { } at)
        {
            var seconds = Math.Max(0, (long)Math.Ceiling((at - time.GetUtcNow()).TotalSeconds));
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "GitHub did not answer", detail: detail);
    }
}