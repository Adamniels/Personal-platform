using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.ClaudeLogin;

/// <summary>What the page sends to give Dev flow the Claude login.</summary>
public sealed record ClaudeLoginRequest(string? Token);

/// <summary>
/// The Claude login over HTTP, the user's three routes under the <c>Api</c> policy and the origin
/// check: giving or replacing it, removing it, and reading whether one is set. None of them ever
/// answers the token. See "Claude login" in devflow/README.md.
/// </summary>
public static class ClaudeLoginEndpoints
{
    public const string Path = "/devflow/api/claude-login";

    public static IEndpointRouteBuilder MapClaudeLogin(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, async (HttpContext context, [FromServices] ClaudeLogins logins) =>
                TypedResults.Ok(await logins.Status(context.UserId(), context.RequestAborted)))
            .UserRoute("getClaudeLogin", "The caller's Claude login",
                "Whether one is set, when it was given and renew_by, a year after; state is none, ok, due_soon from 31 days before renew_by, or expired from renew_by on. Never the token or any part of it.");

        routes.MapPut(Path, async Task<Results<Ok<ClaudeLoginStatus>, ProblemHttpResult>> (ClaudeLoginRequest request, HttpContext context, [FromServices] ClaudeLogins logins) =>
            {
                if (ClaudeLogins.AsSetupToken(request.Token) is not { } token)
                {
                    // The field alone is named, never what was sent.
                    var errors = new Dictionary<string, string[]>
                    {
                        ["token"] = [$"must be the token claude setup-token makes, {ClaudeLogins.Prefix} followed by more, without spaces, at most {ClaudeLogins.MaxLength} characters"],
                    };
                    return TypedResults.Problem(new HttpValidationProblemDetails(errors)
                    {
                        Status = StatusCodes.Status422UnprocessableEntity,
                        Title = "That is not a setup token",
                        Detail = $"token {errors["token"][0]}",
                    });
                }

                return TypedResults.Ok(await logins.Set(context.UserId(), token, context.RequestAborted));
            })
            .UserRoute("setClaudeLogin", "Give Dev flow the caller's Claude login",
                "The token from claude setup-token, sealed in the vault and replacing any before it, given now, so its year starts again. The space around it is dropped. Answers the status it leaves, never the token. 422 for anything that does not look like a setup token, sk-ant-oat followed by more visible characters, and nothing is stored; Dev flow does not ask Anthropic whether it works.",
                StatusCodes.Status422UnprocessableEntity);

        routes.MapDelete(Path, async (HttpContext context, [FromServices] ClaudeLogins logins) =>
            {
                await logins.Remove(context.UserId(), context.RequestAborted);
                return TypedResults.NoContent();
            })
            .UserRoute("removeClaudeLogin", "Remove the caller's Claude login",
                "Forgets the login and its sealed token. 204, whether or not there was one.");

        return routes;
    }

    /// <summary>A user's route, under the <c>Api</c> policy, named in the schema with the
    /// problems it may answer, as the projects' and the workers' routes are.</summary>
    private static RouteHandlerBuilder UserRoute(this RouteHandlerBuilder route, string name, string summary, string description, params int[] problems)
    {
        route.RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description + " Without a valid token, 401 with an empty body; a change from another origin, 403.")
            .Produces(StatusCodes.Status401Unauthorized);
        foreach (var status in problems)
        {
            route.ProducesValidationProblem(status);
        }

        return route;
    }
}