using System.Globalization;

using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Projects;

/// <summary>
/// Projects over HTTP, every route under the <c>Api</c> policy and the origin check: what the
/// person can connect, connecting, listing and disconnecting, and a project's settings. Each needs
/// a linked GitHub account, except listing, disconnecting and the settings. See "Connecting a
/// repository" and "Cards" in devflow/README.md.
/// </summary>
public static class ProjectEndpoints
{
    /// <param name="origin">The platform's public origin, under which the fake's install page
    /// is.</param>
    public static IEndpointRouteBuilder MapProjects(this IEndpointRouteBuilder routes, Uri origin)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(origin);

        routes.MapGet("/devflow/api/github/repositories", async Task<Results<Ok<Connectable>, ProblemHttpResult>> (HttpContext context, [FromServices] ProjectStore projects) =>
                await projects.Connectable(context.UserId(), origin, context.RequestAborted) switch
                {
                    ProjectOutcome<Connectable>.Done { Value: var connectable } => TypedResults.Ok(connectable),
                    var refused => Problem(context, refused),
                })
            .Route("listConnectableRepositories", "The repositories the caller can connect",
                "Every installation of the app the caller's GitHub account can reach, read as them, with the account it is on and its repositories, sorted by name, each saying whether it is already a project; and install_url, where to install the app. 409 when GitHub is not linked or the link has expired, 503 when GitHub did not answer.",
                StatusCodes.Status409Conflict, StatusCodes.Status503ServiceUnavailable);

        var projects = routes.MapGroup("/devflow/api/projects");

        projects.MapGet("", async (HttpContext context, [FromServices] ProjectStore store) =>
                TypedResults.Ok(new ProjectList(await store.List(context.UserId(), context.RequestAborted))))
            .Route("listProjects", "The caller's projects", "Each connected repository, by owner and name.");

        projects.MapPost("", Connect)
            .Route("connectRepository", "Connect a repository, which makes it a project",
                "Asks GitHub, as the caller, whether the repository is among those of the installation they can reach, and only then stores it, with the owner and name GitHub gave. 201 with the project. 404 for a repository the caller cannot reach through that installation, 409 when it is already a project or GitHub is not linked, 422 naming a missing field, 503 when GitHub did not answer.",
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity, StatusCodes.Status503ServiceUnavailable);

        projects.MapDelete("/{id:guid}", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] ProjectStore store) =>
                await store.Disconnect(context.UserId(), id, context.RequestAborted)
                    ? TypedResults.NoContent()
                    : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist"))
            .Route("disconnectProject", "Disconnect a project",
                "204, and everything Dev flow stored for it goes with it. Nothing changes on GitHub. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound);

        projects.MapGet("/{id:guid}/settings", async Task<Results<Ok<ProjectSettings>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] ProjectSettingsStore store) =>
                await store.Get(context.UserId(), id, context.RequestAborted) is { } settings
                    ? TypedResults.Ok(settings)
                    : NoSuchProject())
            .Route("getProjectSettings", "A project's settings",
                "The labels that pick and hold its cards, mark what needs the caller and mark a card for their review; whether a card's pull request waits for them, never, sometimes or always, with the three conditions of sometimes and the paths it watches; its trusted accounts, the GitHub logins whose cards, edits and comments count, the caller's linked login first and marked as theirs; the model that reads, builds and reviews its cards and the limits of its runs; and questions, whether a card its reading found questions in waits for the caller's answers rather than going on the reader's recommendations; and preset, the preset the settings match, fully_automatic, guided or only_i_merge, or custom, computed and never stored. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound);

        projects.MapPut("/{id:guid}/settings", SaveSettings)
            .Route("saveProjectSettings", "Change a project's settings",
                "Takes all eighteen, never a preset, and answers them as saved with the preset they match. A label is written, at most 50 characters, with no space at either end and no comma, and the four are different names whatever their case; waits_for_owner is never, sometimes or always, and sometimes has at least one of wait_when_marked, wait_when_paths and wait_when_decision; wait_paths is at most 100 glob patterns of at most 200 characters, none starting with ! or /, and a pattern given twice is kept once; trusted_logins is a list of GitHub logins that holds the caller's linked login, and a login given twice whatever its case is kept once; each model is opus, sonnet or haiku, and each limit within its range; questions is true or false. The first save also does the project's setup step settings, as accepting a setup run's proposal does. 422 naming each field that breaks a rule, 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound, StatusCodes.Status422UnprocessableEntity);

        return routes;
    }

    private static async Task<Results<Ok<ProjectSettings>, ProblemHttpResult>> SaveSettings(Guid id, SettingsRequest request, HttpContext context, [FromServices] ProjectSettingsStore store, [FromServices] TimeProvider time)
    {
        var (settings, trusted, errors) = request.Check(await store.Mine(context.UserId(), context.RequestAborted));
        if (settings is null || trusted is null)
        {
            return TypedResults.Problem(new HttpValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "The settings break a rule",
                Detail = string.Join("; ", errors.Select(pair => $"{pair.Key} {pair.Value[0]}")),
            });
        }

        return await store.Save(context.UserId(), id, settings, trusted, time.GetUtcNow(), context.RequestAborted)
            ? TypedResults.Ok(settings)
            : NoSuchProject();
    }

    private static ProblemHttpResult NoSuchProject() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");

    private static async Task<Results<Created<Project>, ProblemHttpResult>> Connect(ConnectRequest request, HttpContext context, [FromServices] ProjectStore store)
    {
        var missing = new Dictionary<string, string[]>();
        if (request.InstallationId is not > 0)
        {
            missing["installation_id"] = ["must be the installation's id, a positive number"];
        }

        if (request.RepositoryId is not > 0)
        {
            missing["repository_id"] = ["must be the repository's id, a positive number"];
        }

        if (missing.Count > 0)
        {
            return TypedResults.Problem(new HttpValidationProblemDetails(missing)
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "The request breaks a rule",
                Detail = string.Join("; ", missing.Select(pair => $"{pair.Key} {pair.Value[0]}")),
            });
        }

        var outcome = await store.Connect(context.UserId(), request.InstallationId!.Value, request.RepositoryId!.Value, context.RequestAborted);
        return outcome is ProjectOutcome<Project>.Done { Value: var project }
            ? TypedResults.Created($"/devflow/api/projects/{project.Id}", project)
            : Problem(context, outcome);
    }

    /// <summary>Why an operation did not happen, as a problem document, with
    /// <c>Retry-After</c> when GitHub said when to try again.</summary>
    private static ProblemHttpResult Problem<T>(HttpContext context, ProjectOutcome<T> outcome)
    {
        switch (outcome)
        {
            case ProjectOutcome<T>.NotLinked:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "GitHub is not linked",
                    detail: "Link your GitHub account on the Projects tab, then connect a repository.");
            case ProjectOutcome<T>.NeedsRelink:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Your GitHub link has expired",
                    detail: "Link GitHub again on the Projects tab, then connect a repository.");
            case ProjectOutcome<T>.NotReachable:
                return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such repository",
                    detail: "Your GitHub account cannot reach that repository through that installation of the app.");
            case ProjectOutcome<T>.AlreadyConnected:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This repository is already a project",
                    detail: "It is listed under Projects.");
            case ProjectOutcome<T>.Unavailable { RetryAt: var retryAt }:
                if (retryAt is { } at)
                {
                    var seconds = Math.Max(0, (long)Math.Ceiling((at - context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()).TotalSeconds));
                    context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                }

                return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "GitHub did not answer",
                    detail: "Try again in a moment.");
            default:
                throw new ArgumentException("a finished operation is not a problem", nameof(outcome));
        }
    }

    /// <summary>A route under the <c>Api</c> policy, named in the schema with the problems it may
    /// answer, as review's routes are.</summary>
    private static RouteHandlerBuilder Route(this RouteHandlerBuilder route, string name, string summary, string description, params int[] problems)
    {
        route.RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description + " Without a valid token, 401 with an empty body; a change from another origin, 403.")
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