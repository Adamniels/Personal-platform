using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using DevFlow.Api;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using PlatformAuth;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>
/// A run's log as it happens, the owner's, under the <c>Api</c> policy, through the door as the
/// caller: the lines stored, then each new one from <see cref="RunLogFeed"/> as the worker's log
/// route stores it, so no stream polls the database; each a <c>line</c> event whose id is its seq,
/// so a page that reconnects takes up after the last line it had. An <c>end</c> event once the run
/// has ended. See "Jobs" in devflow/README.md.
/// </summary>
public static class RunLogStreamEndpoints
{
    /// <summary>The most stored lines a stream starts with, the last ones, after its
    /// <c>Last-Event-ID</c> when it has one, since a page shows the last 200.</summary>
    public const int MostSent = 200;

    /// <summary>The event sent first, at once, data null: the stream's headers go out with its
    /// first event, so without it a run with no line yet would leave the page, and any proxy
    /// between, waiting for the stream to open.</summary>
    public const string StartEvent = "start";

    /// <summary>The event that carries a line.</summary>
    public const string LineEvent = "line";

    /// <summary>The event that says the run has ended, after its last line.</summary>
    public const string EndEvent = "end";

    public static IEndpointRouteBuilder MapRunLogStream(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/devflow/api/projects/{id:guid}/runs/{run:guid}/log/events", Stream)
            .RequireAuthorization(PlatformAuthPolicies.Api)
            .WithName("streamRunLog")
            .WithSummary("A run's log, as it happens")
            .WithDescription(string.Create(CultureInfo.InvariantCulture, $"Server-sent events, text/event-stream: a start event at once, data null, then a line event for each of the last {MostSent} lines the run stored, after the seq in Last-Event-ID when one is sent, then one for each new line as the worker's log route stores it, its data the line, seq, at, kind and text, and its id the seq; each text is shown as text, never as HTML. Once the run has ended, its last lines and then an end event, data null, and the stream closes; a run that has ended sends them at once. It ends without one when the token it was opened with runs out, or when it falls too far behind, and EventSource opens it again from its Last-Event-ID. 404 for a project or run the caller does not have. Without a valid token, 401 with an empty body."))
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        return routes;
    }

    private static async Task<Results<ServerSentEventsResult<RunLogLine?>, ProblemHttpResult>> Stream(Guid id, Guid run, HttpContext context, [FromServices] UserDb database, [FromServices] RunLogFeed feed, [FromServices] TimeProvider time)
    {
        var userId = context.UserId();
        var after = LastEventId(context.Request.Headers["Last-Event-ID"]);

        // Subscribed before the table is read, so a line stored between the two is in one or the
        // other; one in both is sent once, by its seq. Disposed with the request, whatever happens.
        var subscription = feed.Subscribe(run);
        context.Response.RegisterForDispose(subscription);
        if (await RunLogStore.Since(database, userId, id, run, after, MostSent, context.RequestAborted) is not { } stored)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such run", detail: "The project has no such run of yours.");
        }

        return TypedResults.ServerSentEvents(Events(database, userId, id, run, after, stored, subscription.Lines, time, context.TokenExpiresAt(), context.RequestAborted));
    }

    /// <summary>The seq a reconnecting page last had, from its <c>Last-Event-ID</c>, or 0 for none
    /// or one that is not a seq.</summary>
    internal static long LastEventId(string? header) =>
        long.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ? seq : 0;

    /// <summary>
    /// <paramref name="stored"/>, then each line of <paramref name="lines"/> past the last seq
    /// sent. When <paramref name="lines"/> ends, the run has ended or the subscription fell behind:
    /// the table is read once after the last seq sent, and its last <see cref="MostSent"/> lines
    /// sent, since a page shows no more, with the end event when the run has ended; otherwise the
    /// stream closes for the page to open it again. It closes too
    /// at <paramref name="until"/>, or when the page goes.
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<RunLogLine?>> Events(UserDb database, Guid userId, Guid projectId, Guid runId, long after, StoredLog stored, ChannelReader<RunLogLine> lines, TimeProvider time, DateTimeOffset until, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new SseItem<RunLogLine?>(null, StartEvent);
        var sent = after;
        foreach (var line in stored.Lines)
        {
            sent = line.Seq;
            yield return Line(line);
        }

        if (stored.Ended)
        {
            yield return new SseItem<RunLogLine?>(null, EndEvent);
            yield break;
        }

        var left = until - time.GetUtcNow();
        if (left <= TimeSpan.Zero)
        {
            yield break;
        }

        using var expiry = new CancellationTokenSource(left, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        while (await WaitToRead(lines, stop.Token) is { } more)
        {
            if (more)
            {
                while (lines.TryRead(out var line))
                {
                    if (line.Seq > sent)
                    {
                        sent = line.Seq;
                        yield return Line(line);
                    }
                }

                continue;
            }

            if (await RunLogStore.Since(database, userId, projectId, runId, sent, MostSent, cancellationToken) is not { } rest)
            {
                yield break;
            }

            foreach (var line in rest.Lines)
            {
                yield return Line(line);
            }

            if (rest.Ended)
            {
                yield return new SseItem<RunLogLine?>(null, EndEvent);
            }

            yield break;
        }
    }

    private static SseItem<RunLogLine?> Line(RunLogLine line) =>
        new(line, LineEvent) { EventId = line.Seq.ToString(CultureInfo.InvariantCulture) };

    /// <summary>Whether <paramref name="lines"/> has a line to read, false once it has ended, or
    /// null when <paramref name="cancellationToken"/> came first.</summary>
    private static async Task<bool?> WaitToRead(ChannelReader<RunLogLine> lines, CancellationToken cancellationToken)
    {
        try
        {
            return await lines.WaitToReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}