using System.Globalization;

using DevFlow.Api;
using DevFlow.Workers;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Placements;

/// <summary>One entry of <c>PUT /devflow/api/projects/{id}/placements</c>: a machine and how many
/// of the project's workers it should run. The count is taken as any number, so one with a
/// fraction is refused naming it rather than as the request's shape.</summary>
public sealed record PlacementRequest(Guid? MachineId, decimal? Workers);

/// <summary>
/// Where a project's workers run, over HTTP, both the user's, under the <c>Api</c> policy and the
/// origin check: its placements, and replacing them. The page says what should exist, and the
/// machine program makes its machine match it. See "Workers" in devflow/README.md.
/// </summary>
public static class PlacementEndpoints
{
    /// <summary>What a placement's count takes.</summary>
    private static readonly Projects.RunSettings.Range Range = new(0, PlacementStore.MostWorkers);

    /// <summary>Why a machine that is another user's, or none, is refused, the same for both.</summary>
    private static readonly string[] NotYours = ["is not one of your machines"];

    public static IEndpointRouteBuilder MapPlacements(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/projects/{id:guid}/placements", async Task<Results<Ok<IReadOnlyList<Placement>>, ProblemHttpResult>> (Guid id, HttpContext context, [FromServices] PlacementStore store) =>
                await store.List(context.UserId(), id, context.RequestAborted) is { } placements
                    ? TypedResults.Ok(placements)
                    : NoSuchProject())
            .Route("listPlacements", "Where a project's workers run",
                "The machines the project's workers are placed on, by when each was placed and then by when it enrolled, each with machine_id, name, as the machine last said, online, whether it was seen less than 60 seconds ago, and workers, how many of the project's workers it should run, 0 to 8. A machine the project is not placed on is not listed. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound);

        routes.MapPut("/devflow/api/projects/{id:guid}/placements", Place)
            .Route("savePlacements", "Place a project's workers on machines",
                "Takes the whole list, [{\"machine_id\", \"workers\"}], and replaces the project's placements with it: a machine left out is no longer placed. Each machine_id is one of the caller's machines, given once, and each workers a whole number from 0 to 8; else 422 naming the entry's field, [<index>].machine_id or [<index>].workers, another user's machine refused as one that does not exist. Brings the project's worker slots up to date at once, which the machine program reads. Answers the placements as listPlacements does. 404 for a project the caller does not have.",
                StatusCodes.Status404NotFound, StatusCodes.Status422UnprocessableEntity);

        return routes;
    }

    private static async Task<Results<Ok<IReadOnlyList<Placement>>, ProblemHttpResult>> Place(Guid id, IReadOnlyList<PlacementRequest?>? request, HttpContext context, [FromServices] PlacementStore store)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["placements"] = ["is missing: a list of machines, each with its workers"];
            return WorkerEndpoints.Invalid(errors);
        }

        var seen = new HashSet<Guid>();
        for (var index = 0; index < request.Count; index++)
        {
            var entry = request[index];
            var at = string.Create(CultureInfo.InvariantCulture, $"[{index}]");
            if (entry?.MachineId is not { } machine || machine == Guid.Empty)
            {
                errors[$"{at}.machine_id"] = ["is missing"];
            }
            else if (!seen.Add(machine))
            {
                errors[$"{at}.machine_id"] = ["is given twice: each machine once, with all its workers"];
            }

            if (Range.ProblemOf(entry?.Workers) is { } wrong)
            {
                errors[$"{at}.workers"] = [wrong];
            }
        }

        if (errors.Count > 0)
        {
            return WorkerEndpoints.Invalid(errors);
        }

        var placements = request.Select(entry => (entry!.MachineId!.Value, (int)entry.Workers!.Value)).ToList();
        return await store.Place(context.UserId(), id, placements, context.RequestAborted) switch
        {
            PlaceOutcome.Placed { Placements: var placed } => TypedResults.Ok(placed),
            PlaceOutcome.NotMine { Indexes: var indexes } => WorkerEndpoints.Invalid(indexes.ToDictionary(
                index => string.Create(CultureInfo.InvariantCulture, $"[{index}].machine_id"),
                _ => NotYours)),
            _ => NoSuchProject(),
        };
    }

    private static ProblemHttpResult NoSuchProject() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist");

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