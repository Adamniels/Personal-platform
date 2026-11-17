using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Workers;

/// <summary>What making a key answers, the one time the key is ever sent.</summary>
public sealed record WorkerKey(string Key);

/// <summary>What a worker sends with <c>hello</c>. Every field is required.</summary>
public sealed record HelloRequest(Guid? WorkerId, string? Name, string? RecipeVersion, string? BaseImage);

/// <summary>What a worker sends when it asks for work.</summary>
public sealed record NextJobRequest(Guid? WorkerId);

/// <summary>
/// Workers over HTTP. The user's two, under the <c>Api</c> policy and the origin check: making a
/// project's key, and listing its workers. The worker's two, under
/// <see cref="WorkerAuthentication.Policy"/> and outside the origin check: <c>hello</c>, and
/// asking for work, which answers nothing in this part. See "Workers" in devflow/README.md.
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

        routes.MapGet("/devflow/api/projects/{id:guid}/workers", async Task<Results<Ok<WorkerList>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] WorkerStore store) =>
                await store.List(context.UserId(), id, context.RequestAborted) is { } list
                    ? TypedResults.Ok(list)
                    : NoSuchProject())
            .UserRoute("listWorkers", "The project's workers",
                "Every worker that has said hello with the project's key, by when it first did, with what it reported and when it was last seen; and key_made_at, when the project's key was made, null before one has been. The key itself is never in it. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound);

        var worker = routes.MapGroup(WorkerAuthentication.RoutePrefix);

        worker.MapPost("/hello", Hello)
            .WorkerRoute("workerHello", "A worker introduces itself",
                "The worker's id, a UUID it made when it started, its name, the container's hostname, and the recipe version and base image it runs. Records the worker in the key's project, or brings it up to date, and marks it seen. 204. 422 naming a field that is missing or breaks a rule: every one is required, and each text is at most 255 characters.",
                StatusCodes.Status422UnprocessableEntity);

        worker.MapPost("/jobs/next", NextJob)
            .WorkerRoute("workerNextJob", "A worker asks for work",
                "Marks the worker seen and answers its next job; in this part there is none, so it is always 204. 409 for a worker that has not said hello in the key's project, which says hello and asks again; 422 without worker_id.",
                StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

        return routes;
    }

    /// <summary>The most a worker's text may hold, as the table's checks say.</summary>
    private const int MaxText = 255;

    private static async Task<Results<NoContent, UnauthorizedHttpResult, ProblemHttpResult>> Hello(HelloRequest request, HttpContext context, [FromServices] WorkerStore store)
    {
        var errors = new Dictionary<string, string[]>();
        CheckId(request.WorkerId, errors);
        CheckText("name", request.Name, errors);
        CheckText("recipe_version", request.RecipeVersion, errors);
        CheckText("base_image", request.BaseImage, errors);
        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        var hello = new WorkerHello(request.WorkerId!.Value, request.Name!, request.RecipeVersion!, request.BaseImage!);
        // The project went between finding the key and now, and the key with it.
        return await store.Hello(context.Worker(), hello, context.RequestAborted)
            ? TypedResults.NoContent()
            : TypedResults.Unauthorized();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> NextJob(NextJobRequest request, HttpContext context, [FromServices] WorkerStore store)
    {
        var errors = new Dictionary<string, string[]>();
        CheckId(request.WorkerId, errors);
        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        return await store.Seen(context.Worker(), request.WorkerId!.Value, context.RequestAborted)
            ? TypedResults.NoContent()
            : TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Say hello first",
                detail: "This worker has not said hello in its key's project. Send hello, then ask again.");
    }

    private static void CheckId(Guid? id, Dictionary<string, string[]> errors)
    {
        if (id is not { } value || value == Guid.Empty)
        {
            errors["worker_id"] = ["must be the worker's id, a UUID that is not all zeros"];
        }
    }

    private static void CheckText(string field, string? value, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxText)
        {
            errors[field] = [$"must be text of 1 to {MaxText} characters, not only spaces"];
        }
    }

    private static ProblemHttpResult Invalid(Dictionary<string, string[]> errors) =>
        TypedResults.Problem(new HttpValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "The request breaks a rule",
            Detail = string.Join("; ", errors.Select(pair => $"{pair.Key} {pair.Value[0]}")),
        });

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

    /// <summary>A worker's route, under <see cref="WorkerAuthentication.Policy"/> alone, named in
    /// the schema with the problems it may answer.</summary>
    private static RouteHandlerBuilder WorkerRoute(this RouteHandlerBuilder route, string name, string summary, string description, params int[] problems)
    {
        route.RequireAuthorization(WorkerAuthentication.Policy)
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description + " Takes the project's worker key as Authorization: Bearer dfw_…, and nothing else: without a key the project has now, 401 with an empty body. No Origin is needed, and no cookie is read or set.")
            .Produces(StatusCodes.Status401Unauthorized);
        foreach (var status in problems)
        {
            if (status == StatusCodes.Status422UnprocessableEntity)
            {
                route.ProducesValidationProblem(status);
            }
            else
            {
                route.ProducesProblem(status);
            }
        }

        return route;
    }
}