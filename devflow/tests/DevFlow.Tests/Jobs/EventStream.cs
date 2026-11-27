using System.Net;
using System.Net.ServerSentEvents;

using Microsoft.Extensions.Time.Testing;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// A server-sent events stream from Dev flow on its test server, read as the page's
/// <c>EventSource</c> reads it: opened with the token in the cookie, and with
/// <c>Last-Event-ID</c> when given, its events read one at a time with a time limit, so a test
/// can say both what arrives and that nothing does.
/// </summary>
internal sealed class EventStream : IAsyncDisposable
{
    /// <summary>How long a test waits, in real time, for an event it expects.</summary>
    public static readonly TimeSpan Expected = TimeSpan.FromSeconds(10);

    /// <summary>How long a test waits, in real time, to say no event came.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _client;
    private readonly HttpResponseMessage _response;
    private readonly IAsyncEnumerator<SseItem<string>> _events;
    private Task<bool>? _pending;

    private EventStream(HttpClient client, HttpResponseMessage response, IAsyncEnumerator<SseItem<string>> events)
    {
        _client = client;
        _response = response;
        _events = events;
    }

    public HttpStatusCode Status => _response.StatusCode;

    public string? ContentType => _response.Content.Headers.ContentType?.MediaType;

    /// <summary>Opens <paramref name="path"/> as the fixture token's user, or without a token.</summary>
    public static async Task<EventStream> Open(Harness app, string path, string? token = null, string? lastEventId = null)
    {
        var client = app.Server.CreateClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (token is not null)
            {
                request.Headers.Add("Cookie", $"__Host-access={token}");
            }

            if (lastEventId is not null)
            {
                request.Headers.Add("Last-Event-ID", lastEventId);
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            var events = SseParser.Create(body).EnumerateAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            return new EventStream(client, response, events);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The next event, or null when none came within <paramref name="within"/>,
    /// <see cref="Expected"/> unless given. Throws when the stream ended instead.</summary>
    public async Task<SseItem<string>?> Next(TimeSpan? within = null)
    {
        _pending ??= _events.MoveNextAsync().AsTask();
        var finished = await Task.WhenAny(_pending, Task.Delay(within ?? Expected, TestContext.Current.CancellationToken));
        if (finished != _pending)
        {
            return null;
        }

        var more = await _pending;
        _pending = null;
        return more ? _events.Current : throw new InvalidOperationException("the stream ended");
    }

    /// <summary>The next event, which must come.</summary>
    public async Task<SseItem<string>> Expect() => await Next() ?? throw new TimeoutException("no event came");

    /// <summary>Whether the stream ends within <see cref="Expected"/>, with no event before.</summary>
    public async Task<bool> Ends()
    {
        _pending ??= _events.MoveNextAsync().AsTask();
        var finished = await Task.WhenAny(_pending, Task.Delay(Expected, TestContext.Current.CancellationToken));
        if (finished != _pending)
        {
            return false;
        }

        var more = await _pending;
        _pending = null;
        return !more;
    }

    /// <summary>Moves <paramref name="time"/> on 2 seconds at a time, the stream's check, until an
    /// event comes, at most ten times; the event, or null. The stream's own wait may not be set
    /// yet when the clock first moves, so one move is not enough to be sure.</summary>
    public async Task<SseItem<string>?> AfterChecks(FakeTimeProvider time, int most = 10)
    {
        for (var i = 0; i < most; i++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            if (await Next(Quiet) is { } item)
            {
                return item;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _response.Dispose();
        _client.Dispose();
        try
        {
            await _events.DisposeAsync();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or NotSupportedException)
        {
            // The body is gone with the response; nothing more to read.
        }
    }
}