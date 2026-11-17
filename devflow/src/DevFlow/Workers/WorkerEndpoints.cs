using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Workers;

/// <summary>What making a key answers, the one time the key is ever sent.</summary>
public sealed record WorkerKey(string Key);

/// <summary>
/// Workers over HTTP: the user's route that makes a project's key, under the <c>Api</c> policy and
/// the origin check. See "Workers" in devflow/README.md.
/// </summary>
public static class WorkerEndpoints
{
    public static IEndpointRouteBuilder MapWorkers(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/devflow/api/projects/{id:guid}/worker-key", async Task<Results<Ok<WorkerKey>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] WorkerKeys keys) =>
            {
                if (await keys.Make(context.UserId(), id, context.RequestAborted) is not { } key)
                {
                    return NoSuchProject();
                }

                // Sent once, and kept by no cache on the way.
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(new WorkerKey(key));
            })
            .UserRoute("makeWorkerKey", "Make the project's worker key",
                "A new key for the project's workers, answered this once: Dev flow keeps only its SHA-256 and never shows it again. It replaces the project's last key at once, so a worker holding that one is refused from then on. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound);

        return routes;
    }

    private static ProblemHttpResult NoSuchProject() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");

    /// <summary>A user's route, under the <c>Api</c> policy, named in the schema with the
    /// problems it may answer, as the projects' routes are.</summary>
    private static RouteHandlerBuilder UserRoute(this RouteHandlerBuilder route, string name, string summary, string description, params int[] problems)
    {
        route.RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description + " Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized);
        foreach (var status in problems)
        {
            route.ProducesProblem(status);
        }

        return route;
    }
}