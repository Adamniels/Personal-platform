using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Reading;

/// <summary>
/// What waits, over HTTP, under the <c>Api</c> policy and the origin check: the list as stored, and
/// asking for a project to be read now. See "What waits" in devflow/README.md.
/// </summary>
public static class WaitingEndpoints
{
    /// <summary>How long a refresh waits for its read before it answers that the read is under way.</summary>
    public static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(10);

    public static IEndpointRouteBuilder MapWaiting(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/waiting", async (HttpContext context, [FromServices] WaitingStore store) =>
                TypedResults.Ok(await store.For(context.UserId(), context.RequestAborted)))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("listWaiting")
            .WithSummary("What waits for the caller, as last read")
            .WithDescription("Read from what Dev flow stored and never from GitHub. items are the pull requests that are the caller's turn in every project, by project and number; own_items are Dev flow's own, read from its own tables, in this part each a project stopped because something landed on its default branch that Dev flow did not put there, with the commit, who did it, when, and the commit's address on GitHub, by project; cards are Dev flow's own cards whose pull requests wait for the caller, each with its pull request, the head commit its run built and checked, the branch it merges into, the report's summary, the decisions the agent marked for the caller with the count of the others, the check Dev flow ran, review, the reviewer's verdict on its head commit with its summary and how many findings block, null when it has none, and can_merge, false while the project is stopped, by project and number; stopped_cards are Dev flow's own cards that stopped, each with its issue's address, where a comment runs it again, why it stopped, when, and its pull request's address or null, by project and number; failing_projects are the projects whose runs have failed outside their cards six times in a row, each with message, \"Dev flow cannot run <project>'s cards: <the last reason>\", the reason, how many failures, since when and until when it hands out no job, shown until a run succeeds, by project; count is the items not sent back, the own items, the cards, the stopped cards and the failing projects, plus one when needs_relink; projects says for each when it was last read, stale when that is more than five minutes ago or never, and failing when its last read failed. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        routes.MapPost("/devflow/api/projects/{id:guid}/refresh", Refresh)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("refreshProject")
            .WithSummary("Read a project from GitHub now")
            .WithDescription("204 once it has been read, whether or not GitHub answered, which the list then says; 202 when the read is still under way after 10 seconds, and it goes on. 404 for a project the caller does not have. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<Results<NoContent, Accepted, ProblemHttpResult>> Refresh(
        Guid id, HttpContext context, [FromServices] WaitingStore store, [FromServices] ProjectRefresher refresher, [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        if (!await store.Has(userId, id, context.RequestAborted))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");
        }

        try
        {
            await refresher.Request(id, userId).WaitAsync(RefreshWait, time, context.RequestAborted);
            return TypedResults.NoContent();
        }
        catch (TimeoutException)
        {
            return TypedResults.Accepted((string?)null);
        }
    }
}