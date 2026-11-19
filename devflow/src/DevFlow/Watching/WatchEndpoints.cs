using System.Text.RegularExpressions;

using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Watching;

/// <summary>What <c>POST /devflow/api/projects/{id}/resume</c> takes.</summary>
/// <param name="Commit">The commit the page showed the project stopped by, which it must still be
/// stopped by.</param>
public sealed record ResumeRequest(string? Commit);

/// <summary>
/// Resuming a stopped project, over HTTP, under the <c>Api</c> policy and the origin check, so only
/// the project's own user, from the platform's page, resumes it. See "Watching branches" in
/// devflow/README.md.
/// </summary>
public static partial class WatchEndpoints
{
    public static IEndpointRouteBuilder MapWatch(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/devflow/api/projects/{id:guid}/resume", Resume)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("resumeProject")
            .WithSummary("Resume a stopped project, accepting what landed on its default branch")
            .WithDescription("Accepts commit, the commit the page showed the project stopped by, and records who resumed it, when, and that commit, so the project hands out work again. In order: 404 for a project the caller does not have; 422 when commit is missing or is not a full commit id; 409 when it is not stopped; 409 when it is stopped by another commit than commit, which landed since the page was shown. 204 once resumed. Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return routes;
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Resume(Guid id, ResumeRequest request, HttpContext context, [FromServices] WatchStore store)
    {
        if (request.Commit is not { } commit || !CommitId().IsMatch(commit))
        {
            return TypedResults.Problem(new HttpValidationProblemDetails(new Dictionary<string, string[]> { ["commit"] = ["must be the full id of the commit the page showed"] })
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "The request breaks a rule",
                Detail = "commit must be the full id of the commit the page showed",
            });
        }

        return await store.Resume(context.UserId(), id, commit, context.RequestAborted) switch
        {
            ResumeOutcome.Resumed => TypedResults.NoContent(),
            ResumeOutcome.NoSuchProject => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such project", detail: "the project does not exist"),
            ResumeOutcome.NotStopped => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This project is not stopped",
                detail: "It hands out work already."),
            ResumeOutcome.Changed => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Something else has landed since this was shown",
                detail: "Look at what is shown now before you resume."),
            _ => throw new InvalidOperationException("a resume outcome that is not one of the four"),
        };
    }

    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitId();
}