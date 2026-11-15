using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.Link;

/// <summary>What <c>POST /devflow/api/github/link</c> answers.</summary>
/// <param name="AuthorizeUrl">The code host's page to send the browser to.</param>
public sealed record LinkStarted(Uri AuthorizeUrl);

/// <summary>
/// Linking GitHub over HTTP: starting it under the <c>Api</c> policy and the origin
/// check, and the return from the code host, a <c>GET</c> under the <c>Page</c> policy. See
/// "Linking GitHub" in devflow/README.md.
/// </summary>
public static class LinkEndpoints
{
    /// <summary>Where the return sends the browser: the Projects tab.</summary>
    public const string Projects = "/devflow/projects";

    /// <summary>Where a return that did not link sends it, where the page says so.</summary>
    public const string Failed = "/devflow/projects?link=failed";

    public static IEndpointRouteBuilder MapGitHubLink(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var link = routes.MapGroup("/devflow/api/github/link")
            .RequireAuthorization(PlatformAuthPolicies.Api);

        // A POST, since it stores a sign in.
        link.MapPost("", async (HttpContext context, [FromServices] GitHubLink github) =>
                TypedResults.Ok(new LinkStarted(await github.Start(context.UserId(), context.RequestAborted))))
            .WithName("startGitHubLink")
            .WithSummary("Start linking the caller's GitHub account")
            .WithDescription("Stores a single use state and a PKCE verifier for ten minutes, and answers the code host's address to send the browser to, carrying the state and the S256 challenge. The code host sends the browser back to /devflow/github/callback. Without a valid token, 401 with an empty body; from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        // The one GET that changes state, since the code host sends the browser back with a GET.
        // What makes it safe is the single use state bound to the logged in user, PKCE and the ten
        // minutes, not the Origin header, which a redirect from another site cannot carry. See
        // "Security decisions and accepted risks" in devflow/docs/devflow-architecture.md.
        routes.MapGet(GitHubLink.CallbackPath, async Task<StatusCodeHttpResult> (
                HttpContext context,
                [FromServices] GitHubLink github,
                [FromQuery] string? code,
                [FromQuery] string? state,
                [FromQuery] string? error) =>
            {
                var linked = await github.Finish(context.UserId(), code, state, error, context.RequestAborted);
                return SeeOther(context, linked ? Projects : Failed);
            })
            .RequireAuthorization(PlatformAuthPolicies.Page)
            .ExcludeFromDescription();

        return routes;
    }

    /// <summary>A <c>303</c> to a path of Dev flow's own, never stored by a cache, set without a
    /// redirect result so nothing logs the address the browser came back with.</summary>
    internal static StatusCodeHttpResult SeeOther(HttpContext context, string location)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.Location = location;
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.StatusCode(StatusCodes.Status303SeeOther);
    }
}