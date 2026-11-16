using System.Globalization;

using DevFlow.Api;
using DevFlow.CodeHost;
using DevFlow.Link;
using DevFlow.Reading;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Acting;

/// <summary>What <c>POST /devflow/api/waiting/{id}/merge</c> takes.</summary>
/// <param name="HeadCommit">The commit the page showed, which the pull request's head must still
/// be.</param>
public sealed record MergeRequest(string? HeadCommit);

/// <summary>
/// Acting on what waits, from the page, as the caller on the code host and never as the app:
/// merging a waiting pull request. Under the <c>Api</c> policy and the origin check. Everything
/// that can be refused without the code host is refused before it is asked; the commit the page
/// showed travels to the code host, which refuses to merge a pull request that moved on, so a
/// stale page can cost a click and never a wrong merge. Nothing the runner owns, a label or a
/// status, is ever written. See "Acting from the page" in devflow/README.md.
/// </summary>
public static class ActionEndpoints
{
    /// <summary>The title of the answer when the pull request is not as the page showed it.</summary>
    public const string Changed = "The pull request has changed since this was shown";

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

        return routes;
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
            await ReadAgain(refresher, item, userId, time, cancellationToken);
            return ChangedSinceShown();
        }

        var userToken = await tokens.For(userId, cancellationToken);
        if (userToken is not UserToken.Usable { AccessToken: var access })
        {
            return TokenProblem(userToken, context, time);
        }

        switch (await host.Merge(userId, new UserAccessToken(access), item.Repository, item.Number, item.HeadCommit, cancellationToken))
        {
            case MergeResult.Merged:
                await ReadAgain(refresher, item, userId, time, cancellationToken);
                return TypedResults.NoContent();
            case MergeResult.HeadMoved:
                await ReadAgain(refresher, item, userId, time, cancellationToken);
                return ChangedSinceShown();
            case MergeResult.NotMergeable { Message: var message }:
                return Conflict("GitHub would not merge it", message is null ? "GitHub did not say why." : $"GitHub said: {message}");
            case MergeResult.Refused { Status: var status, Message: var message }:
                return Conflict("GitHub refused the merge", HostSaid(status, message));
            case MergeResult.Unavailable { RetryAt: var retryAt }:
                // It may have merged before the answer was lost, so the list is read again.
                await ReadAgain(refresher, item, userId, time, cancellationToken);
                return Unavailable(context, time, retryAt, "It is not known whether it merged; the list was read again.");
            default:
                throw new InvalidOperationException("a merge result that is not one of the five");
        }
    }

    /// <summary>Why there is no token to act with.</summary>
    private static ProblemHttpResult TokenProblem(UserToken token, HttpContext context, TimeProvider time) => token switch
    {
        UserToken.NotLinked => Conflict("GitHub is not linked", "Link your GitHub account on the Projects tab to merge and comment as yourself."),
        UserToken.NeedsRelink => Conflict("Your GitHub link has expired", "Link GitHub again on the Projects tab to merge and comment as yourself."),
        UserToken.Unavailable { RetryAt: var retryAt } => Unavailable(context, time, retryAt, "Nothing was sent. Try again in a moment."),
        _ => throw new ArgumentException("a usable token is not a problem", nameof(token)),
    };

    /// <summary>Asks for the item's project to be read, and waits for it as the refresh route
    /// does, so the list the page reads next shows what the code host says now. A read still
    /// under way after <see cref="WaitingEndpoints.RefreshWait"/> goes on without the answer.</summary>
    private static async Task ReadAgain(ProjectRefresher refresher, ActionTarget item, Guid userId, TimeProvider time, CancellationToken cancellationToken)
    {
        try
        {
            await refresher.Request(item.ProjectId, userId).WaitAsync(WaitingEndpoints.RefreshWait, time, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }

    private static string HostSaid(int status, string? message) =>
        message is null
            ? string.Create(CultureInfo.InvariantCulture, $"GitHub answered {status}.")
            : string.Create(CultureInfo.InvariantCulture, $"GitHub answered {status}: {message}");

    private static ProblemHttpResult NoSuchItem() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such item", detail: "Nothing waits for you with that id.");

    private static ProblemHttpResult ChangedSinceShown() =>
        Conflict(Changed, "It was read again. Look at it before you merge.");

    private static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);

    private static ProblemHttpResult Invalid(string field, string reason) =>
        TypedResults.Problem(new HttpValidationProblemDetails(new Dictionary<string, string[]> { [field] = [reason] })
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "The request breaks a rule",
            Detail = $"{field} {reason}",
        });

    /// <summary>503, with <c>Retry-After</c> when the code host said when to try again.</summary>
    private static ProblemHttpResult Unavailable(HttpContext context, TimeProvider time, DateTimeOffset? retryAt, string detail)
    {
        if (retryAt is { } at)
        {
            var seconds = Math.Max(0, (long)Math.Ceiling((at - time.GetUtcNow()).TotalSeconds));
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "GitHub did not answer", detail: detail);
    }
}