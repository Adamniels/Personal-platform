using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

using DevFlow.Api;
using DevFlow.Recipes;

using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>
/// "Running now" over HTTP, the owner's, under the <c>Api</c> policy, through the door as the
/// caller: the runs under way of their projects, and the same list as server-sent events, sent at
/// once and again whenever it changed, compared every <see cref="Check"/>. A stream ends when the
/// token that let it in does, and the page's <c>EventSource</c> opens it again. See "The pages" and
/// "Jobs" in devflow/README.md.
/// </summary>
public static class RunningEndpoints
{
    /// <summary>How often a stream reads the list again to see whether it changed.</summary>
    public static readonly TimeSpan Check = TimeSpan.FromSeconds(2);

    /// <summary>The event that carries the list.</summary>
    public const string RunningEvent = "running";

    public static IEndpointRouteBuilder MapRunning(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/running", async (HttpContext context, [FromServices] UserDb database, [FromServices] WorkerStart start, [FromServices] TimeProvider time) =>
                TypedResults.Ok(await RunningStore.For(database, context.UserId(), start.Image, time.GetUtcNow(), context.RequestAborted)))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("listRunning")
            .WithSummary("The caller's runs under way")
            .WithDescription("Every run under way of the caller's projects, oldest first, as Running now shows each: its project, its worker by the name it said hello with, its kind, state, running or checking once its check runs, stopping when the caller marked it to stop and its worker has not ended it yet, its card's or its feature's number and title, null for a setup's run or a check, a fix's round, the model its job was handed out with, null for a check, when it started, its tokens so far, input_tokens + cache_creation_input_tokens + output_tokens, and its cache reads so far beside them; busy_workers, how many workers hold them; and next, the cards that come next, Next up: every card of the caller's projects queued, waiting to be read, or waiting for its review or its fix round, with no run under way, in each project's queue order and across projects by priority label and then age, at most 50, each with its project, number, title, state, the job due for it, the sentence of the reason the one rule gives it, null for one that runs as soon as a worker asks, and where that reason is acted on, link, card with link_card, waiting_for_you, project_page, settings, or schedules, the card \"When it runs\" on its project's settings page, null where it has no place; and next_more, how many come after those 50. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        routes.MapGet("/devflow/api/running/events", (HttpContext context, [FromServices] UserDb database, [FromServices] WorkerStart start, [FromServices] TimeProvider time) =>
                TypedResults.ServerSentEvents(Changes(database, context.UserId(), start.Image, time, context.TokenExpiresAt(), context.RequestAborted)))
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("streamRunning")
            .WithSummary("The caller's runs under way, as they change")
            .WithDescription("Server-sent events, text/event-stream: a running event at once, its data what GET /devflow/api/running answers, and another whenever that changed, a run started or ended, its state or its tokens moved, or a card next came, went or changed its reason, read again every 2 seconds; nothing while nothing changes. It ends when the token it was opened with runs out, and EventSource opens it again. Without a valid token, 401 with an empty body.")
            .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }

    /// <summary>The list at once, then again each time it is read different, every
    /// <see cref="Check"/> on <paramref name="time"/>, until <paramref name="until"/> or the page
    /// goes.</summary>
    internal static async IAsyncEnumerable<SseItem<RunningList>> Changes(UserDb database, Guid userId, string image, TimeProvider time, DateTimeOffset until, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var list = await RunningStore.For(database, userId, image, time.GetUtcNow(), cancellationToken);
        var sent = JsonSerializer.Serialize(list);
        yield return new SseItem<RunningList>(list, RunningEvent);
        while (await Waited(time, Check, until, cancellationToken))
        {
            list = await RunningStore.For(database, userId, image, time.GetUtcNow(), cancellationToken);
            var read = JsonSerializer.Serialize(list);
            if (read == sent)
            {
                continue;
            }

            sent = read;
            yield return new SseItem<RunningList>(list, RunningEvent);
        }
    }

    /// <summary>Waits <paramref name="wait"/> on <paramref name="time"/>, or until
    /// <paramref name="until"/> when that is sooner; whether the stream goes on, false once
    /// <paramref name="until"/> has come or the page has gone.</summary>
    internal static async Task<bool> Waited(TimeProvider time, TimeSpan wait, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var left = until - time.GetUtcNow();
        if (left <= TimeSpan.Zero)
        {
            return false;
        }

        await Task.Delay(left < wait ? left : wait, time, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return !cancellationToken.IsCancellationRequested && time.GetUtcNow() < until;
    }
}