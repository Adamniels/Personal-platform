using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Setup;

/// <summary>
/// A project's setup over HTTP, every route under the <c>Api</c> policy and the origin check: its
/// steps and its newest check, asking for another check of a version whose check failed, and a
/// check's pictures. See "Setting up a project" in devflow/README.md.
/// </summary>
public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetup(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var setup = routes.MapGroup("/devflow/api/projects/{id:guid}/setup");

        setup.MapGet("", async Task<Results<Ok<ProjectSetup>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] UserDb database) =>
                await SetupStore.Of(database, context.UserId(), id, context.RequestAborted) is { } answer
                    ? TypedResults.Ok(answer)
                    : NoSuchProject())
            .Route("getProjectSetup", "A project's setup",
                "state, setting_up until a check of any version of its .devflow/ has passed, then registered, and a project setting up hands out no card; steps, in their order, each with whether it is done: recipe, a version of .devflow/ read from its default branch, worker, a worker built from that version said hello, and check, a check of that version passed; check, the newest check of that version, or of any before it is known, with its version, its commit, whether it passed, its exit code, the last 200 lines it printed, the names of its pictures of the project's address, why there are none, when, and whether another was asked for, or null before one ran; and checking, whether a check of that version is running. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound);

        setup.MapPost("/check-again", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
                await SetupStore.Again(database, context.UserId(), id, Now(time), context.RequestAborted) switch
                {
                    CheckAgain.Asked => TypedResults.NoContent(),
                    CheckAgain.NothingToCheck => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Nothing to check again", detail: "The newest check did not fail, or another was already asked for."),
                    _ => NoSuchProject(),
                })
            .Route("checkProjectAgain", "Check a project's setup again",
                "Asks for another check of the version whose newest check, the one getProjectSetup answers, failed: the next worker on that version that asks is handed it. 204. 409 when that check did not fail or another was already asked for; 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        setup.MapGet("/checks/{check:guid}/pictures/{name}", async Task<Results<FileContentHttpResult, ProblemHttpResult>> (Guid id, Guid check, string name, HttpContext context, [FromServices] UserDb database) =>
                {
                    Reading.ScreenshotEndpoints.Guard(context);
                    if (await SetupStore.Picture(database, context.UserId(), id, check, name, context.RequestAborted) is not { } png)
                    {
                        return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such picture", detail: "The project has no check with a picture by that name.");
                    }

                    // A check's pictures never change, so the browser keeps one an hour.
                    context.Response.Headers.CacheControl = "private, max-age=3600";
                    return TypedResults.File(png, "image/png");
                })
            .Route("getSetupPicture", "One of a check's pictures of the project's address",
                "The PNG its worker sent, as Dev flow stored it, checked when it came to be a PNG of at most 5 MB. Always image/png, with X-Content-Type-Options: nosniff, Content-Security-Policy: default-src 'none' and Cache-Control: private, max-age=3600. 404 for a project the caller does not have, or a check or name it does not hold.",
                StatusCodes.Status404NotFound)
            .Produces<Stream>(StatusCodes.Status200OK, "image/png");

        return routes;
    }

    private static ProblemHttpResult NoSuchProject() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    /// <summary>A route under the <c>Api</c> policy, named in the schema with the problems it may
    /// answer, as the projects' routes are.</summary>
    private static RouteHandlerBuilder Route(this RouteHandlerBuilder route, string name, string summary, string description, params int[] problems)
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