using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Values;

/// <summary>
/// A project's values over HTTP, every route under the <c>Api</c> policy and the origin check:
/// the values step, setting a value and removing one. No route answers a value, once given. See
/// "Setting up a project" in devflow/README.md.
/// </summary>
public static class ValuesEndpoints
{
    public static IEndpointRouteBuilder MapValues(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var values = routes.MapGroup("/devflow/api/projects/{id:guid}/values");

        values.MapGet("", async Task<Results<Ok<ProjectValueList>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] ProjectValues store) =>
                await store.List(context.UserId(), id, context.RequestAborted) is { } answer
                    ? TypedResults.Ok(answer)
                    : NoSuchProject())
            .Route("getProjectValues", "A project's values step",
                "values, each name the project's newest setup run that succeeded listed, in its order, then every other name the caller gave, by name: its name; why, what needs it as the setup said, or null for one it did not list; asked, whether it listed it; set_at, when the caller last gave it, or null while it has none; and problem, why a listed name cannot be given, or null. Never a value. done, whether every listed name that can be given has a value, true when none is listed. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound);

        values.MapPut("/{name}", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, string name, ProjectValueRequest request, HttpContext context, [FromServices] ProjectValues store, [FromServices] TimeProvider time) =>
                {
                    if (await store.List(context.UserId(), id, context.RequestAborted) is null)
                    {
                        return NoSuchProject();
                    }

                    if (ProjectValues.NameProblem(name) is { } badName)
                    {
                        return Acting.ActionEndpoints.Invalid("name", badName);
                    }

                    if (ProjectValues.ValueProblem(request.Value) is { } badValue)
                    {
                        return Acting.ActionEndpoints.Invalid("value", badValue);
                    }

                    return await store.Set(context.UserId(), id, name, request.Value!, Now(time), context.RequestAborted)
                        ? TypedResults.NoContent()
                        : NoSuchProject();
                })
            .Route("setProjectValue", "Give one of a project's values",
                "Seals value in the vault as the project's value named name, replacing any before it, for every job that runs the project's code, a build, a fix, a conflict, a feature's fix, a setup and a check, to carry into the environment of the agent, the check and the start; Dev flow scrubs it from everything it stores and posts. It is never answered again. In order: 404 for a project the caller does not have; 422 naming name when it is not capital letters, digits and underscores not beginning with a digit, is over 100 characters, begins with DEVFLOW_, CLAUDE_, ANTHROPIC_, GIT_ or PLAYWRIGHT_, or is PATH, HOME, LANG, USER, SHELL or TERM; 422 naming value when it is missing, holds a line break or a NUL, or is under 8 or over 4,096 characters, a shorter one being one scrubbing could not find without scrubbing ordinary words. 204 once sealed.",
                StatusCodes.Status404NotFound, StatusCodes.Status422UnprocessableEntity);

        values.MapDelete("/{name}", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, string name, HttpContext context, [FromServices] ProjectValues store) =>
                await store.Remove(context.UserId(), id, name, context.RequestAborted) switch
                {
                    ValueRemoval.Removed => TypedResults.NoContent(),
                    ValueRemoval.NotSet => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such value", detail: "The project has no value by that name."),
                    _ => NoSuchProject(),
                })
            .Route("removeProjectValue", "Remove one of a project's values",
                "Removes the project's value named name; jobs handed out after carry it no more. 204. 404 for a project the caller does not have, or a name it has no value for.",
                StatusCodes.Status404NotFound);

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

/// <summary>What the values step sends to give a value.</summary>
/// <param name="Value">The value, exactly as given; it is never answered back.</param>
public sealed record ProjectValueRequest(string? Value)
{
    /// <summary>Never the value.</summary>
    public override string ToString() => "ProjectValueRequest { sealed }";
}