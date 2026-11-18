using DevFlow.Api;
using DevFlow.ClaudeLogin;
using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Recipes;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Workers;

/// <summary>What making a key answers, the one time the key is ever sent.</summary>
/// <param name="Key">The key.</param>
/// <param name="SaveCommand">The command that saves it in the file the start command reads, with
/// the key in it, so shown this once too.</param>
public sealed record WorkerKey(string Key, string SaveCommand);

/// <summary>
/// Workers over HTTP. The user's two, under the <c>Api</c> policy and the origin check: making a
/// project's key, and listing its workers. The worker's, under
/// <see cref="WorkerAuthentication.Policy"/> and outside the origin check: <c>hello</c>, asking for
/// work, and a fresh push key for the job it holds; the job's progress and result are
/// <see cref="Jobs.JobEndpoints"/>. The worker's take and answer <c>DevFlow.Protocol</c>'s types at
/// its paths, which the worker program sends, so a change that breaks either side fails to compile.
/// See "Workers" and "Jobs" in devflow/README.md.
/// </summary>
public static class WorkerEndpoints
{
    public static IEndpointRouteBuilder MapWorkers(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/devflow/api/projects/{id:guid}/worker-key", async Task<Results<Ok<WorkerKey>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] WorkerKeys keys, [FromServices] WorkerStore store, [FromServices] WorkerStart start) =>
            {
                var userId = context.UserId();
                if (await store.ProjectName(userId, id, context.RequestAborted) is not { } name
                    || await keys.Make(userId, id, context.RequestAborted) is not { } key)
                {
                    return NoSuchProject();
                }

                // Sent once, and kept by no cache on the way.
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(new WorkerKey(key, start.SaveCommand(name, key)));
            })
            .UserRoute("makeWorkerKey", "Make the project's worker key",
                "A new key for the project's workers, answered this once with save_command, the command that saves it in ~/.devflow/<project>.env for the start command to read: Dev flow keeps only its SHA-256 and never shows it again. It replaces the project's last key at once, so a worker holding that one is refused from then on. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound);

        routes.MapGet("/devflow/api/projects/{id:guid}/workers", async Task<Results<Ok<WorkerList>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] WorkerStore store, [FromServices] WorkerStart start, [FromServices] ClaudeLogins logins) =>
                await store.List(context.UserId(), id, start, context.RequestAborted) is { } list
                    ? TypedResults.Ok(list with { Held = Dispatcher.HeldFor(await logins.Status(context.UserId(), context.RequestAborted)) })
                    : NoSuchProject())
            .UserRoute("listWorkers", "The project's workers",
                "Every worker that has said hello with the project's key, by when it first did, with what it reported, when it was last seen, and rebuild: recipe when it runs another version of the recipe than Dev flow last read, base_image when it runs on another base image than the one released, or null; key_made_at, when the project's key was made, null before one has been; recipe_version, the recipe's version as last read, null before then or when the default branch has none; and start_command, the command that builds and starts the project's worker; and held, why the project hands out no work whichever worker asks: no_claude_login when its user has given no Claude login, claude_login_expired when it is past its renewal, or null. The key itself is never in it. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound);

        routes.MapPost(WorkerApi.HelloPath, Hello)
            .WorkerRoute("workerHello", "A worker introduces itself",
                "The worker's id, a UUID it made when it started, its name, the container's hostname, and the recipe version and base image it runs. Records the worker in the key's project, or brings it up to date, and marks it seen. 204. 422 naming a field that is missing or breaks a rule: every one is required, and each text is at most 255 characters.",
                StatusCodes.Status422UnprocessableEntity);

        routes.MapPost(WorkerApi.NextJobPath, NextJob)
            .WorkerRoute("workerNextJob", "A worker asks for work",
                "Marks the worker seen and answers its next job, 200 with Cache-Control: no-store: the first of its project's queued cards that waits for nothing, by priority, high, none, low, then oldest first, moved to building, with a run recorded for this worker. The job holds the card, the repository, the branch, the brief, the report's schema, the check, the run's user's Claude token, a push key narrowed to the repository and its expiry, the committer and the limits. 204 when there is no work for it: no card to take, a user without a Claude login or with one past its renewal, a worker that needs a rebuild, running another version of the recipe than Dev flow last read or another base image than the one released, or the code host not answering what a job needs, such as the project's .devflow/project.json. 409 for a worker that has not said hello in the key's project, which says hello and asks again; 422 without worker_id.",
                StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity)
            .Produces<Job>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status204NoContent);

        routes.MapPost(WorkerApi.PushKeyRoute, PushKey)
            .WorkerRoute("workerPushKey", "A fresh push key for a running job",
                "A new push key for the job's repository, narrowed to fetching and pushing code, with when it expires, 200 with Cache-Control: no-store, since a key lasts an hour and a run up to 90 minutes. Only for the worker holding the job, named by worker_id, while the job runs; 404 for any other worker, a job that is not one of the project's, or one that has finished. 503 when the code host made no key now, which the worker asks for again; 422 without worker_id.",
                StatusCodes.Status404NotFound, StatusCodes.Status422UnprocessableEntity, StatusCodes.Status503ServiceUnavailable)
            .Produces<PushKeyAnswer>(StatusCodes.Status200OK);

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

    private static async Task<Results<Ok<Job>, NoContent, ProblemHttpResult>> NextJob(NextJobRequest request, HttpContext context, [FromServices] Dispatcher dispatcher)
    {
        var errors = new Dictionary<string, string[]>();
        CheckId(request.WorkerId, errors);
        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        switch (await dispatcher.Next(context.Worker(), request.WorkerId!.Value, context.RequestAborted))
        {
            case Jobs.NextJob.Unknown:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Say hello first",
                    detail: "This worker has not said hello in its key's project. Send hello, then ask again.");
            case Jobs.NextJob.Handed { Job: var job }:
                // It holds the Claude token and a push key: kept by no cache on the way.
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(job);
            default:
                return TypedResults.NoContent();
        }
    }

    private static async Task<Results<Ok<PushKeyAnswer>, ProblemHttpResult>> PushKey(Guid id, PushKeyRequest request, HttpContext context, [FromServices] Dispatcher dispatcher)
    {
        var errors = new Dictionary<string, string[]>();
        CheckId(request.WorkerId, errors);
        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        switch (await dispatcher.PushKey(context.Worker(), id, request.WorkerId!.Value, context.RequestAborted))
        {
            case FreshKey.Made { Key: var key }:
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(key);
            case FreshKey.Unavailable:
                return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "GitHub did not answer",
                    detail: "No push key could be made now. Ask again.");
            default:
                return NoSuchJob();
        }
    }

    /// <summary>The one answer for a job the worker does not hold, whatever the reason, so a
    /// worker cannot tell another's job from none.</summary>
    internal static ProblemHttpResult NoSuchJob() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such job", detail: "this worker holds no such job under way");

    internal static void CheckId(Guid? id, Dictionary<string, string[]> errors)
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

    internal static ProblemHttpResult Invalid(Dictionary<string, string[]> errors) =>
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
    internal static RouteHandlerBuilder WorkerRoute(this RouteHandlerBuilder route, string name, string summary, string description, params int[] problems)
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