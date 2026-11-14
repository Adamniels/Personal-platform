using Microsoft.AspNetCore.Http.HttpResults;

using PlatformAuth;

namespace Review.Api;

/// <summary>
/// The answers review's API gives when it does not do what was asked, each an RFC 9457 problem
/// document, <c>application/problem+json</c>, which the page's client reads. See "What it serves"
/// in review/README.md.
/// </summary>
internal static class Problems
{
    /// <summary>An id in the path the user does not have, another user's included: the two are
    /// one answer, so it never says whether the id exists.</summary>
    public static ProblemHttpResult NotFound(string what) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"{what} does not exist");

    /// <summary>The request is valid but clashes with what is there.</summary>
    public static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: detail);

    /// <summary>Every route under <c>/review/api/</c> that reads or changes a user's data: the
    /// <see cref="PlatformAuthPolicies.Api"/> policy, a 401 with an empty body without a valid
    /// token, and the problems it may answer, named in the schema.</summary>
    public static RouteHandlerBuilder UserRoute(this RouteHandlerBuilder route, string name, string summary, string description, params int[] problems)
    {
        ArgumentNullException.ThrowIfNull(route);
        route.RequireAuthorization(PlatformAuthPolicies.Api)
            // The app's one tag, as health and whoami have it, rather than the handler's class.
            .WithTags("Review")
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description)
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