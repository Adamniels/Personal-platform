using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

namespace DevFlow.ClaudeLogin;

/// <summary>What the page sends to give Dev flow the API key.</summary>
public sealed record ApiKeyRequest(string? Key);

/// <summary>
/// The API key over HTTP, the user's three routes under the <c>Api</c> policy and the origin check:
/// giving or replacing it, removing it, and reading whether one is set. None of them ever answers
/// the key. See "The API key" in devflow/README.md.
/// </summary>
public static class ApiKeyEndpoints
{
    public const string Path = "/devflow/api/api-key";

    public static IEndpointRouteBuilder MapApiKey(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, async (HttpContext context, [FromServices] ApiKeys keys) =>
                TypedResults.Ok(await keys.Status(context.UserId(), context.RequestAborted)))
            .UserRoute("getApiKey", "The caller's Anthropic API key",
                "Whether one is set and when it was given. Never the key or any part of it.");

        routes.MapPut(Path, async Task<Results<Ok<ApiKeyStatus>, ProblemHttpResult>> (ApiKeyRequest request, HttpContext context, [FromServices] ApiKeys keys) =>
            {
                if (ApiKeys.AsApiKey(request.Key) is not { } key)
                {
                    // The field alone is named, never what was sent.
                    var errors = new Dictionary<string, string[]>
                    {
                        ["key"] = [$"must be an Anthropic API key, {ApiKeys.Prefix} followed by more, without spaces, at most {ApiKeys.MaxLength} characters"],
                    };
                    return TypedResults.Problem(new HttpValidationProblemDetails(errors)
                    {
                        Status = StatusCodes.Status422UnprocessableEntity,
                        Title = "That is not an API key",
                        Detail = $"key {errors["key"][0]}",
                    });
                }

                return TypedResults.Ok(await keys.Set(context.UserId(), key, context.RequestAborted));
            })
            .UserRoute("setApiKey", "Give Dev flow the caller's Anthropic API key",
                "The key from the Claude Console, sealed in the vault and replacing any before it, given now. The space around it is dropped. Answers the status it leaves, never the key. 422 naming key for anything that does not look like an API key, sk-ant-api followed by more visible characters, a setup token among them, and nothing is stored; Dev flow does not ask Anthropic whether it works.",
                StatusCodes.Status422UnprocessableEntity);

        routes.MapDelete(Path, async Task<Results<NoContent, ProblemHttpResult>> (HttpContext context, [FromServices] ApiKeys keys) =>
            {
                var inUse = await keys.Remove(context.UserId(), context.RequestAborted);
                if (inUse.Count > 0)
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "The API key is in use",
                        detail: $"These projects run on it: {string.Join(", ", inUse)}");
                }

                return TypedResults.NoContent();
            })
            .UserRoute("removeApiKey", "Remove the caller's Anthropic API key",
                "Forgets the key and its sealed secret. 204, whether or not there was one. 409, \"These projects run on it: <names>\", while any of the caller's projects runs on it, and nothing is removed.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    /// <summary>A user's route, under the <c>Api</c> policy, named in the schema with the
    /// problems it may answer, as the Claude login's routes are.</summary>
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