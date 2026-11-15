using System.Text.Json.Serialization;

using DevFlow.Api;
using DevFlow.Link;
using DevFlow.Settings;

using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Status;

/// <summary>What <c>GET /devflow/api/status</c> answers.</summary>
/// <param name="CodeHost">Which code host Dev flow talks to, <c>fake</c> or <c>github</c>; the
/// page shows its banner for <c>fake</c>.</param>
/// <param name="GitHub">Whether the caller has linked their GitHub account, and as whom.</param>
public sealed record DevFlowStatus(
    CodeHostMode CodeHost,
    [property: JsonPropertyName("github")] GitHubLinkStatus GitHub);

/// <summary>
/// The state of Dev flow the page reads before it draws: which code host it talks to, and the
/// caller's GitHub link. See "What it serves" in devflow/README.md.
/// </summary>
public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatus(this IEndpointRouteBuilder routes, CodeHostMode codeHost)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/status", async (HttpContext context, [FromServices] GitHubLink github) =>
                TypedResults.Ok(new DevFlowStatus(codeHost, await github.Status(context.UserId(), context.RequestAborted))))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("status")
            .WithSummary("Which code host Dev flow talks to, and the caller's GitHub link")
            .WithDescription("code_host is fake when GitHub is faked, which every Dev flow page then says in a banner, and github otherwise. github.state is none, linked or needs_relink, with the login it was linked as, null with none. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }
}