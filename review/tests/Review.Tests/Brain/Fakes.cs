using System.Collections.Concurrent;
using System.Net;
using System.Text;

using Review.Brain;

namespace Review.Tests.Brain;

/// <summary>
/// A brain in memory, behind the port: it keeps every event it is given, with the token it came
/// with, and answers what <see cref="Answer"/> says, which a test may change between requests.
/// </summary>
internal sealed class FakeBrain(Func<AssessmentEvent, CancellationToken, Task<BrainAnswer>> answer) : IBrain
{
    private readonly ConcurrentQueue<(string Token, AssessmentEvent Event)> _calls = new();

    public Func<AssessmentEvent, CancellationToken, Task<BrainAnswer>> Answer { get; set; } = answer;

    /// <summary>Every event given, in the order it was given.</summary>
    public IReadOnlyList<(string Token, AssessmentEvent Event)> Calls => [.. _calls];

    /// <summary>A brain that records everything.</summary>
    public static FakeBrain Recording() => new((_, _) => Task.FromResult(BrainAnswer.Recorded));

    /// <summary>A brain that is down: every call is unavailable at once.</summary>
    public static FakeBrain Down() => new((_, _) => Task.FromResult(BrainAnswer.Unavailable("down")));

    /// <summary>A brain that never answers, until the call is cancelled.</summary>
    public static FakeBrain Silent() => new(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    });

    public Task<BrainAnswer> RecordEvent(string token, AssessmentEvent assessment, CancellationToken ct)
    {
        _calls.Enqueue((token, assessment));
        return Answer(assessment, ct);
    }
}

/// <summary>
/// The brain over HTTP, answered from memory, for the adapter: it keeps each request with its
/// body read, and answers what <see cref="Answer"/> says.
/// </summary>
internal sealed class StubBrainHttp : HttpMessageHandler
{
    private readonly ConcurrentQueue<Sent> _sent = new();

    /// <summary>The answer to a request, given its body. A status of 201 by default.</summary>
    public Func<Sent, CancellationToken, Task<HttpResponseMessage>> Answer { get; set; } = (_, _) => Task.FromResult(Status(HttpStatusCode.Created));

    public IReadOnlyList<Sent> Requests => [.. _sent];

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status)
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
    };

    /// <summary>An answer that never comes, until the call is cancelled.</summary>
    public static async Task<HttpResponseMessage> Never(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new Sent(request, body);
        _sent.Enqueue(sent);
        return await Answer(sent, cancellationToken);
    }

    /// <summary>A request as it was sent, its body read.</summary>
    internal sealed record Sent(HttpRequestMessage Request, string Body);
}

internal static class Wait
{
    /// <summary>Waits, polling, until <paramref name="condition"/> holds, failing the test after
    /// ten seconds, which nothing here comes close to on a working machine.</summary>
    public static async Task Until(string what, Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"waited ten seconds for {what}");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public static Task Until(string what, Func<bool> condition) => Until(what, () => Task.FromResult(condition()));
}