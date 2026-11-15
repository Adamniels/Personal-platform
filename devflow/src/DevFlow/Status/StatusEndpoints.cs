using DevFlow.Settings;

using PlatformAuth;

namespace DevFlow.Status;

/// <summary>What <c>GET /devflow/api/status</c> answers.</summary>
/// <param name="CodeHost">Which code host Dev flow talks to, <c>fake</c> or <c>github</c>; the
/// page shows its banner for <c>fake</c>. Later slices add the state of the user's GitHub link.</param>
public sealed record DevFlowStatus(CodeHostMode CodeHost);

/// <summary>
/// The state of Dev flow the page reads before it draws: today, which code host it talks to. See
/// "What it serves" in devflow/README.md.
/// </summary>
public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatus(this IEndpointRouteBuilder routes, CodeHostMode codeHost)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var status = new DevFlowStatus(codeHost);

        routes.MapGet("/devflow/api/status", () => TypedResults.Ok(status))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("status")
            .WithSummary("Which code host Dev flow talks to")
            .WithDescription("fake when GitHub is faked, which every Dev flow page then says in a banner, and github otherwise. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }
}