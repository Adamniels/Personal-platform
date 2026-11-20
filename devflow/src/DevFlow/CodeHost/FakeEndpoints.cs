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

/// <summary>What <c>POST /devflow/api/fake/claude-login-age</c> takes.</summary>
/// <param name="Days">How many days ago the caller's Claude login is to have been given.</param>
public sealed record FakeClaudeLoginAge(int Days);

/// <summary>What <c>POST /devflow/api/fake/push</c> takes.</summary>
/// <param name="Repository">The id of the caller's repository, in any of its world's
/// installations.</param>
/// <param name="Branch">The branch to push a commit on, made with it when it has none.</param>
/// <param name="Message">The commit's message.</param>
public sealed record FakePush(long Repository, string Branch, string Message);

/// <summary>What <c>POST /devflow/api/fake/push</c> answers: the commit pushed.</summary>
public sealed record FakePushed(string Commit);

/// <summary>What <c>POST /devflow/api/fake/end-wait</c> takes.</summary>
/// <param name="ProjectId">The caller's project whose wait after failures outside its cards is to
/// be over.</param>
public sealed record FakeEndWait(Guid? ProjectId);

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

        // A push of one commit on a branch of the caller's repository, by the person the world signs
        // in as, made in the fake's git and listed in the branch's changes: how a test moves the
        // default branch on under a feature, as the owner pushing would.
        fake.MapPost("/push", async Task<Results<Ok<FakePushed>, ProblemHttpResult>> (HttpContext context, [FromServices] FakeCodeHost host) =>
        {
            var (push, error) = await FakeJson.Read<FakePush>(context.Request.Body, context.RequestAborted);
            if (error is not null || push!.Branch.Length == 0)
            {
                return Refused("The push is malformed", error ?? new Malformed("branch", "is empty"));
            }

            var userId = context.UserId();
            var world = host.World(userId);
            if (world.Installations.SelectMany(installation => installation.Repositories.Select(repository => (installation, repository)))
                    .FirstOrDefault(pair => pair.repository.Id == push.Repository) is not { repository: not null } found)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such repository", detail: "the caller's world holds no repository with that id");
            }

            var repository = new Repository(found.installation.Id, found.repository.Id, found.repository.Owner, found.repository.Name);
            return await host.Push(userId, repository, push.Branch, push.Message, world.SignedInUser.Login, context.RequestAborted) is { } commit
                ? TypedResults.Ok(new FakePushed(commit))
                : TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The fake's git failed", detail: "no commit was pushed");
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

        // Moves when the caller's Claude login was given back by a number of days, so a test sees
        // it due without waiting a year. 404 without a login.
        fake.MapPost("/claude-login-age", async Task<Results<NoContent, ProblemHttpResult>> (HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
        {
            var (body, error) = await FakeJson.Read<FakeClaudeLoginAge>(context.Request.Body, context.RequestAborted);
            error ??= body is { Days: < 0 } ? new Malformed("days", "must not be negative") : null;
            if (error is not null)
            {
                return Refused("The age is malformed", error);
            }

            var userId = context.UserId();
            var aged = await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "UPDATE claude_logins SET given_at = @givenAt WHERE user_id = @userId",
                new { userId, givenAt = time.GetUtcNow() - TimeSpan.FromDays(body!.Days) },
                transaction,
                cancellationToken: context.RequestAborted)), context.RequestAborted);
            return aged > 0
                ? TypedResults.NoContent()
                : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No Claude login", detail: "the caller has no Claude login to age");
        });

        // Ends a project's wait after failures outside its cards now, its count kept, so a test sees
        // the next failure, and the sixth that shows, without waiting minutes. See "Stops" in
        // devflow/README.md.
        fake.MapPost("/end-wait", async Task<Results<NoContent, ProblemHttpResult>> (HttpContext context, [FromServices] UserDb database, [FromServices] TimeProvider time) =>
        {
            var (body, error) = await FakeJson.Read<FakeEndWait>(context.Request.Body, context.RequestAborted);
            error ??= body is { ProjectId: null } ? new Malformed("project_id", "is required") : null;
            if (error is not null)
            {
                return Refused("The project is malformed", error);
            }

            var userId = context.UserId();
            var ended = await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
                "UPDATE project_waits SET wait_until = @now WHERE project_id = @projectId",
                new { projectId = body!.ProjectId, now = time.GetUtcNow() },
                transaction,
                cancellationToken: context.RequestAborted)), context.RequestAborted);
            return ended > 0
                ? TypedResults.NoContent()
                : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No wait", detail: "the caller has no such project waiting");
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