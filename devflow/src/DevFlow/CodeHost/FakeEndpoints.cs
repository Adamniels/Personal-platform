using Dapper;

using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.CodeHost;

/// <summary>What <c>POST /devflow/api/fake/unavailable</c> takes.</summary>
/// <param name="Unavailable">Whether every operation answers unavailable for the caller.</param>
public sealed record FakeUnavailable(bool Unavailable);

/// <summary>
/// The fake code host's addresses, through which a test stages its account's world. Mapped only
/// in the fake mode, so in the GitHub mode each is <c>404</c>; under the <c>Api</c> policy and the
/// origin check like every route; each acts on the caller's own world. Left out of
/// contracts/devflow/openapi.json, since they are not part of what Dev flow is. See "The code
/// host" in devflow/README.md.
/// </summary>
public static class FakeEndpoints
{
    public static IEndpointRouteBuilder MapFakeCodeHost(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var fake = routes.MapGroup("/devflow/api/fake")
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .ExcludeFromDescription();

        // Written with the fake's own options, so it is the document that was put.
        fake.MapGet("/world", (HttpContext context, [FromServices] FakeCodeHost host) =>
            TypedResults.Json(host.World(context.UserId()), FakeJson.Options));

        // The merges and comments the fake took for the caller, oldest first, each with whose
        // token made it.
        fake.MapGet("/writes", (HttpContext context, [FromServices] FakeCodeHost host) =>
            TypedResults.Json(host.Writes(context.UserId()), FakeJson.Options));

        fake.MapPut("/world", async Task<Results<NoContent, ProblemHttpResult>> (HttpContext context, [FromServices] FakeCodeHost host) =>
        {
            var (world, error) = await FakeJson.Read<FakeWorld>(context.Request.Body, context.RequestAborted);
            error ??= FakeJson.Check(world!);
            if (error is not null)
            {
                return Refused("The world is malformed", error);
            }

            host.Replace(context.UserId(), world!);
            return TypedResults.NoContent();
        });

        fake.MapPost("/unavailable", async Task<Results<NoContent, ProblemHttpResult>> (HttpContext context, [FromServices] FakeCodeHost host) =>
        {
            var (body, error) = await FakeJson.Read<FakeUnavailable>(context.Request.Body, context.RequestAborted);
            if (error is not null)
            {
                return Refused("The switch is malformed", error);
            }

            host.SetUnavailable(context.UserId(), body!.Unavailable);
            return TypedResults.NoContent();
        });

        // Ends the caller's access token now, at the fake and in Dev flow's record of it, so the
        // next use of the token renews it, as one ending for real would.
        fake.MapPost("/expire-token", async (HttpContext context, [FromServices] FakeCodeHost host, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
        {
            var userId = context.UserId();
            host.ExpireAccessToken(userId);
            await EndAccess(context, database, time);
            return TypedResults.NoContent();
        });

        // Ends both of the caller's tokens at the fake, as revoking the app on GitHub does, and the
        // access token in Dev flow's record, so the next use renews it, the fake refuses, and the
        // link needs linking again.
        fake.MapPost("/revoke-token", async (HttpContext context, [FromServices] FakeCodeHost host, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
        {
            host.RevokeTokens(context.UserId());
            await EndAccess(context, database, time);
            return TypedResults.NoContent();
        });

        return routes;
    }

    /// <summary>Ends the caller's access token now in Dev flow's record of it.</summary>
    private static Task<int> EndAccess(HttpContext context, UserDb database, TimeProvider time)
    {
        var userId = context.UserId();
        return database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE github_accounts SET access_expires_at = least(access_expires_at, @now) WHERE user_id = @userId",
            new { userId, now = time.GetUtcNow() },
            transaction,
            cancellationToken: context.RequestAborted)), context.RequestAborted);
    }

    private static ProblemHttpResult Refused(string title, Malformed error) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: error.ToString());
}