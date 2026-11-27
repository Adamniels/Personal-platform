using System.Globalization;

using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>
/// A run's log read afterwards, the owner's, under the <c>Api</c> policy, through the door as the
/// caller: a page of its lines at a time, as the card's and the feature's pages show a finished
/// run's log. The log as it happens is <see cref="RunLogStreamEndpoints"/>. See "Jobs" in
/// devflow/README.md.
/// </summary>
public static class RunLogReadEndpoints
{
    public static IEndpointRouteBuilder MapRunLog(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/projects/{id:guid}/runs/{run:guid}/log", Read)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("readRunLog")
            .WithSummary("A run's log, a page at a time")
            .WithDescription(string.Create(CultureInfo.InvariantCulture, $"The run's lines after the seq given as after, 0 when none is given, in seq order, at most {RunLogStore.PageLines:N0} of them, each with its seq, at, kind and text, and more, whether the run holds lines after the last of these, to read with after set to its seq. Every text is shown as text, never as HTML. A run's lines are kept 90 days after it finished, so a run whose log was pruned, as one from before logs were kept, answers none; its story says which, with has_log and log_pruned. 404 for a project or run the caller does not have. Without a valid token, 401 with an empty body."))
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }

    private static async Task<Results<Ok<RunLogPage>, ProblemHttpResult>> Read(Guid id, Guid run, [FromQuery] long? after, HttpContext context, [FromServices] UserDb database) =>
        await RunLogStore.Page(database, context.UserId(), id, run, after ?? 0, context.RequestAborted) is { } page
            ? TypedResults.Ok(page)
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such run", detail: "The project has no such run of yours.");
}