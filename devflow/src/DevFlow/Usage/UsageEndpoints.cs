using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Usage;

/// <summary>
/// "Usage and history" over HTTP, the owner's, under the <c>Api</c> policy, through the door as the
/// caller: what each of their projects, features and cards used in a period. See "Usage and history"
/// in devflow/README.md.
/// </summary>
public static class UsageEndpoints
{
    public static IEndpointRouteBuilder MapUsage(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/usage", Usage)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("getUsage")
            .WithSummary("What the caller's projects, features and cards used")
            .WithDescription("Counts the runs started in period, 7d or 30d, rolling, so many times 24 hours back from now, or all, 30d without one. period, as counted, and projects, each of the caller's projects with a run in it, most tokens first: its id and name; runs, agent_ms, the sum of the runs' times, each as its worker measured it, or from its start to its end, or to now while it runs; tokens, input_tokens + cache_creation_input_tokens + output_tokens; and cache_reads beside them, never in them; setting_up, the same for its checks and its setup's runs, null with none; features, each with a run in the period of its own or of one of its cards, most tokens first, with its number, title, the same figures over its own runs and its cards', elapsed_ms, from the first start of any of them to the last end, or now while one runs, fix_rounds, the rounds of the whole feature its reviews started, sent_back, how many times its cards were sent back, own, the same figures for its own runs alone, its readings, reviews, fixes and conflicts, null with none, and cards, its cards with a run in the period; and cards, its cards on their own with a run in the period. A card, most tokens first, has its number, title, null for one Dev flow no longer holds, the same figures, elapsed_ms over all its runs, fix_rounds, the rounds its reviews started, and sent_back, how many times it was sent back; elapsed, fix rounds and sends back are the card's or the feature's whole, not the period's. Read from what Dev flow stored, never from GitHub. 422 naming period for any other. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return routes;
    }

    private static async Task<Results<Ok<UsageView>, ProblemHttpResult>> Usage(HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time, [FromQuery] string? period = null)
    {
        var chosen = period ?? UsageStore.DefaultPeriod;
        if (!UsageStore.Periods.ContainsKey(chosen))
        {
            return TypedResults.Problem(new HttpValidationProblemDetails(new Dictionary<string, string[]> { ["period"] = ["must be 7d, 30d or all"] })
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "The request breaks a rule",
                Detail = "period must be 7d, 30d or all",
            });
        }

        return TypedResults.Ok(await UsageStore.For(database, context.UserId(), chosen, time.GetUtcNow(), context.RequestAborted));
    }
}