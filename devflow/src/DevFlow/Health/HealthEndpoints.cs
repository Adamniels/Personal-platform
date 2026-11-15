using DevFlow.Api;

using PlatformAuth;

namespace DevFlow.Health;

/// <summary>What <c>GET /devflow/api/health</c> answers.</summary>
/// <param name="Status">Always <c>ok</c>.</param>
public sealed record Health(string Status);

/// <summary>What <c>GET /devflow/api/whoami</c> answers.</summary>
/// <param name="AccountId">The token's <c>sub</c>, the user id.</param>
public sealed record WhoAmI(Guid AccountId);

/// <summary>
/// The two routes every service has: whether it serves, and who it sees. Review's, the brain's and
/// canary's are the same, see "What it serves" in devflow/README.md.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup("/devflow/api");

        api.MapGet("/health", () => TypedResults.Ok(new Health("ok")))
            .WithName("health")
            .WithSummary("Whether Dev flow serves")
            .WithDescription("Needs no token. It answers whenever the process serves, and says nothing about what Dev flow depends on.");

        api.MapGet("/whoami", (HttpContext context) => TypedResults.Ok(new WhoAmI(context.UserId())))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("whoami")
            .WithSummary("The account the request's token names")
            .WithDescription("From the token alone. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }
}