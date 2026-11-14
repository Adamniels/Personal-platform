using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Review.Brain;

namespace Review.Tests.Brain;

/// <summary>
/// The one adapter to the brain, over a stub of its HTTP: what goes on the wire, against
/// <c>contracts/brain/openapi.json</c>, and what each answer becomes. See "What Review tells the
/// brain" in review/README.md.
/// </summary>
public sealed class BrainAdapterTests
{
    private const string Token = "header.payload.signature-of-the-user";

    private static readonly DateTimeOffset Reviewed = new(2026, 11, 14, 9, 30, 0, 125, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AssessmentEvent Assessment = new(
        Verb: "recalled",
        Topic: "postgres",
        Origin: "review",
        Source: "grade",
        Run: "019a8000-0000-7000-8000-000000000001",
        OccurredAt: Reviewed,
        Evidence: "Reviewed a card in \"Databases\": \"What is MVCC?\", recalled it (Good), its first review.",
        Payload: new AssessmentPayload(
            CardId: Guid.Parse("019a8000-0000-7000-8000-0000000000c1"),
            DeckId: Guid.Parse("019a8000-0000-7000-8000-0000000000d1"),
            Rating: 3,
            RatingName: "good",
            ElapsedDays: 0,
            StateBefore: "new",
            StabilityBefore: null,
            StabilityAfter: 2.3065,
            DifficultyAfter: 2.11810397,
            DueAfter: Reviewed.AddMinutes(10)),
        PayloadVersion: 1,
        IdempotencyKey: "019a8000-0000-7000-8000-000000000001:postgres");

    private static (BrainAdapter Brain, StubBrainHttp Http, FakeTimeProvider Clock) Adapter()
    {
        var http = new StubBrainHttp();
        var clock = new FakeTimeProvider(Reviewed);
        return (new BrainAdapter(new HttpClient(http), new Uri("http://127.0.0.1:8082"), "https://platform.test", clock), http, clock);
    }

    private static DateTimeOffset Instant(JsonNode? value) =>
        DateTimeOffset.Parse((string)value!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheRequestIsWhatTheBrainSchemaSays()
    {
        var (brain, http, _) = Adapter();

        var answer = await brain.RecordEvent(Token, Assessment, Ct);

        Assert.Equal(BrainAnswer.Recorded, answer);
        var sent = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, sent.Request.Method);
        Assert.Equal(new Uri("http://127.0.0.1:8082/brain/api/events"), sent.Request.RequestUri);
        Assert.Equal($"Bearer {Token}", Assert.Single(sent.Request.Headers.GetValues("Authorization")));
        // The brain refuses a request that changes state without the platform's own origin.
        Assert.Equal("https://platform.test", Assert.Single(sent.Request.Headers.GetValues("Origin")));
        Assert.Equal("application/json", sent.Request.Content!.Headers.ContentType!.MediaType);
        var carrying = sent.Request.Headers.Concat(sent.Request.Content.Headers)
            .Where(header => header.Value.Any(value => value.Contains(Token, StringComparison.Ordinal)))
            .Select(header => header.Key);
        Assert.Equal(["Authorization"], carrying);

        var body = JsonNode.Parse(sent.Body)!.AsObject();
        // Times are compared as instants, and each carries its zone, as the brain requires.
        Assert.Equal(Reviewed, Instant(body["occurred_at"]));
        Assert.Equal(Reviewed.AddMinutes(10), Instant(body["payload"]!["due_after"]));
        Assert.EndsWith("+00:00", (string)body["occurred_at"]!, StringComparison.Ordinal);
        body.Remove("occurred_at");
        body["payload"]!.AsObject().Remove("due_after");
        var expected = new JsonObject
        {
            ["verb"] = "recalled",
            ["topic"] = "postgres",
            ["origin"] = "review",
            ["source"] = "grade",
            ["run"] = "019a8000-0000-7000-8000-000000000001",
            ["occurred_precision"] = "instant",
            ["evidence"] = "Reviewed a card in \"Databases\": \"What is MVCC?\", recalled it (Good), its first review.",
            ["payload"] = new JsonObject
            {
                ["card_id"] = "019a8000-0000-7000-8000-0000000000c1",
                ["deck_id"] = "019a8000-0000-7000-8000-0000000000d1",
                ["rating"] = 3,
                ["rating_name"] = "good",
                ["elapsed_days"] = 0.0,
                ["state_before"] = "new",
                ["stability_before"] = null,
                ["stability_after"] = 2.3065,
                ["difficulty_after"] = 2.11810397,
            },
            ["payload_version"] = 1,
            ["idempotency_key"] = "019a8000-0000-7000-8000-000000000001:postgres",
        };
        Assert.True(JsonNode.DeepEquals(expected, body), $"sent {body.ToJsonString()}");
    }

    [Fact]
    public async Task ACallWaitsTwoSecondsAndNoMore()
    {
        var (brain, http, clock) = Adapter();
        http.Answer = (_, ct) => StubBrainHttp.Never(ct);

        var call = brain.RecordEvent(Token, Assessment, Ct);
        await Wait.Until("the request sent", () => http.Requests.Count == 1);
        clock.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(1));
        await Task.Delay(100, Ct);
        Assert.False(call.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.IsType<BrainAnswer.UnavailableAnswer>(await call);
    }

    [Theory]
    [InlineData(200, "recorded")]
    [InlineData(201, "recorded")]
    [InlineData(422, "refused")]
    [InlineData(400, "refused")]
    [InlineData(403, "refused")]
    [InlineData(404, "refused")]
    [InlineData(401, "unavailable")]
    [InlineData(429, "unavailable")]
    [InlineData(500, "unavailable")]
    [InlineData(502, "unavailable")]
    [InlineData(503, "unavailable")]
    [InlineData(302, "unavailable")]
    public async Task EachAnswerIsRecordedRefusedOrUnavailable(int status, string expected)
    {
        var (brain, http, _) = Adapter();
        http.Answer = (_, _) => Task.FromResult(StubBrainHttp.Status((HttpStatusCode)status));

        var answer = await brain.RecordEvent(Token, Assessment, Ct);

        Assert.Equal(expected, answer switch
        {
            BrainAnswer.RecordedAnswer => "recorded",
            BrainAnswer.RefusedAnswer refused when refused.Status == status => "refused",
            BrainAnswer.UnavailableAnswer => "unavailable",
            _ => $"something else: {answer}",
        });
    }

    [Fact]
    public async Task NoConnectionIsUnavailable()
    {
        var (brain, http, _) = Adapter();
        http.Answer = (_, _) => throw new HttpRequestException("Connection refused");

        Assert.IsType<BrainAnswer.UnavailableAnswer>(await brain.RecordEvent(Token, Assessment, Ct));
    }

    [Fact]
    public async Task CancellingTheCallerIsNotAnAnswer()
    {
        var (brain, http, _) = Adapter();
        http.Answer = (_, ct) => StubBrainHttp.Never(ct);
        using var stopping = new CancellationTokenSource();

        var call = brain.RecordEvent(Token, Assessment, stopping.Token);
        await Wait.Until("the request sent", () => http.Requests.Count == 1);
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }
}