using Microsoft.AspNetCore.Http.Features;

using PlatformAuth;

namespace Review.Health;

/// <summary>What <c>GET /review/api/health</c> answers.</summary>
/// <param name="Status">Always <c>ok</c>.</param>
public sealed record Health(string Status);

/// <summary>What <c>GET /review/api/whoami</c> answers.</summary>
/// <param name="AccountId">The token's <c>sub</c>, the user id.</param>
public sealed record WhoAmI(Guid AccountId);

/// <summary>
/// The two routes every service has: whether it serves, and who it sees. The brain's and canary's
/// are the same, see "What it serves" in review/README.md.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup("/review/api");

        api.MapGet("/health", () => TypedResults.Ok(new Health("ok")))
            .WithName("health")
            .WithSummary("Whether review serves")
            .WithDescription("Needs no token. It answers whenever the process serves, and says nothing about what review depends on.");

        api.MapGet("/whoami", (HttpContext context) =>
                TypedResults.Ok(new WhoAmI(context.Features.GetRequiredFeature<IPlatformToken>().Claims.AccountId)))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("whoami")
            .WithSummary("The account the request's token names")
            .WithDescription("From the token alone. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }
}