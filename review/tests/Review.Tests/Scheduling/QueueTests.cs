using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

using Review.Scheduling;

namespace Review.Tests.Scheduling;

/// <summary>
/// The queue, <c>GET /review/api/queue</c>: the cards due now, oldest due first, each with when
/// each grade would bring it back. See "When a card comes back" in review/README.md.
/// </summary>
public sealed class QueueTests : IAsyncLifetime
{
    private const string Queue = "/review/api/queue";

    private Api? _api;

    private Api Api => _api!;

    public async ValueTask InitializeAsync() => _api = await Api.Start();

    public async ValueTask DisposeAsync() => await Api.DisposeAsync();

    private DateTimeOffset Now => Api.Review.Clock.GetUtcNow();

    private static List<string> Fronts(Answer queue) =>
        [.. queue.Body!["cards"]!.AsArray().Select(card => (string)card!["front"]!)];

    /// <summary>Makes a card of the token's user due at <paramref name="due"/>, in Review with a
    /// schedule of its own, as weeks of grading would have left it.</summary>
    private async Task<Guid> DueAt(Guid deck, string front, DateTimeOffset due, params string[] slugs)
    {
        var id = (await Api.Card(deck, front, slugs)).Id;
        await Api.AsUser(Api.Me, """
            UPDATE cards SET state = 'review', step = NULL, stability = 14.5, difficulty = 5.5, due = @due, last_review = @last
            WHERE id = @id
            """, new { id, due, last = due.AddDays(-14) });
        return id;
    }

    private async Task<DateTimeOffset> DueOf(Guid card) =>
        Api.Time((await Api.Get($"/review/api/cards/{card}")).Body!["due"]);

    [Fact]
    public async Task TheQueueHoldsEveryDueCardOldestDueFirst()
    {
        var databases = await Api.Deck("Databases");
        var languages = await Api.Deck("Languages");
        await DueAt(databases, "due a week ago", Now.AddDays(-7), "postgres");
        await DueAt(languages, "due yesterday", Now.AddDays(-1), "rust");
        var tomorrow = await DueAt(databases, "due tomorrow", Now.AddDays(1), "postgres");
        var inAMinute = await DueAt(languages, "due in a minute", Now.AddMinutes(1), "rust");
        await DueAt(databases, "due an hour ago", Now.AddHours(-1), "postgres", "rust");
        var exactly = await DueAt(languages, "due this second", Now.AddSeconds(1), "rust");
        // Another user's due card is never in the queue.
        await Api.BobsDeckAndCard();
        Api.Review.Clock.Advance(TimeSpan.FromSeconds(1));
        var now = Now;

        var queue = await Api.Get(Queue);

        Assert.Equal(StatusCodes.Status200OK, queue.Status);
        Assert.Equal(["as_of", "cards", "next_due", "total"], queue.Body!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
        // Due at or before now only, the one due this very second included, oldest due first.
        Assert.Equal(["due a week ago", "due yesterday", "due an hour ago", "due this second"], Fronts(queue));
        Assert.Equal(4, (int)queue.Body!["total"]!);
        // The time every preview is counted from, and when the first card not yet due comes back.
        Assert.Equal(now, Api.Time(queue.Body!["as_of"]));
        Assert.Equal(await DueOf(inAMinute), Api.Time(queue.Body!["next_due"]));

        // Each as a single read answers it, plus its previews, what grading now would do.
        foreach (var item in queue.Body!["cards"]!.AsArray())
        {
            var card = item!.AsObject();
            var read = (await Api.Get($"/review/api/cards/{card["id"]}")).Body!;
            var withoutPreviews = card.DeepClone().AsObject();
            withoutPreviews.Remove("previews");
            Assert.True(JsonNode.DeepEquals(read, withoutPreviews));

            var schedule = new CardState(State.Review, null, 14.5, 5.5, Api.Time(card["due"]), Api.Time(card["due"]).AddDays(-14));
            var expected = Fsrs.Preview(schedule, now);
            var previews = card["previews"]!;
            Assert.Equal(expected.Again, Api.Time(previews["again"]));
            Assert.Equal(expected.Hard, Api.Time(previews["hard"]));
            Assert.Equal(expected.Good, Api.Time(previews["good"]));
            Assert.Equal(expected.Easy, Api.Time(previews["easy"]));
        }

        // Narrowed by deck, by topic, and by both.
        Assert.Equal(["due a week ago", "due an hour ago"], Fronts(await Api.Get($"{Queue}?deck={databases}")));
        Assert.Equal(["due yesterday", "due an hour ago", "due this second"], Fronts(await Api.Get($"{Queue}?topic=rust")));
        var both = await Api.Get($"{Queue}?deck={languages}&topic=rust");
        Assert.Equal(["due yesterday", "due this second"], Fronts(both));
        Assert.Equal(2, (int)both.Body!["total"]!);
        // The next due is narrowed with them.
        Assert.Equal(await DueOf(tomorrow), Api.Time((await Api.Get($"{Queue}?deck={databases}")).Body!["next_due"]));
        Assert.Equal(await DueOf(inAMinute), Api.Time(both.Body!["next_due"]));
        Assert.Empty(Fronts(await Api.Get($"{Queue}?deck={Guid.CreateVersion7()}")));

        // A grade takes a card out of the queue until it comes back.
        var graded = await Api.Post($"/review/api/cards/{exactly}/grades", new JsonObject { ["rating"] = 3, ["version"] = 1 });
        Assert.Equal(StatusCodes.Status201Created, graded.Status);
        Assert.Equal(["due a week ago", "due yesterday", "due an hour ago"], Fronts(await Api.Get(Queue)));

        // A topic that is no slug is a rule broken; a deck that is no id, ASP.NET Core's own 400.
        var badTopic = await Api.Get($"{Queue}?topic=Not%20a%20slug");
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, badTopic.Status);
        Assert.Equal(["topic"], badTopic.Fields);
        Assert.Equal(StatusCodes.Status400BadRequest, (await Api.Get($"{Queue}?deck=not-an-id")).Status);
    }

    [Fact]
    public async Task TheQueueAnswersAtMost200AndCountsThemAll()
    {
        var deck = await Api.Deck("Many");
        // 205 cards due a minute apart, written as grading would have left them.
        await Api.AsUser(Api.Me, """
            INSERT INTO cards (id, user_id, deck_id, front, back, state, step, stability, difficulty, due, last_review, created_at, updated_at)
            SELECT gen_random_uuid(), @me, @deck, 'card ' || lpad(n::text, 3, '0'), 'back', 'review', NULL, 3, 5,
                   @now - make_interval(mins => 300 - n), @now - make_interval(days => 3), @now, @now
            FROM generate_series(1, 205) AS n
            """, new { me = Api.Me, deck, now = Now });

        var queue = await Api.Get(Queue);

        Assert.Equal(205, (int)queue.Body!["total"]!);
        var fronts = Fronts(queue);
        Assert.Equal(200, fronts.Count);
        Assert.Equal("card 001", fronts[0]);
        Assert.Equal("card 200", fronts[^1]);
    }

    [Fact]
    public async Task ANewCardIsDueAtOnce()
    {
        var deck = await Api.Deck("Databases");
        var made = await Api.Card(deck, "What is MVCC?");
        var created = Now;

        var queue = await Api.Get(Queue);

        var card = Assert.Single(queue.Body!["cards"]!.AsArray())!;
        Assert.Equal(made.Id.ToString(), (string)card["id"]!);
        Assert.Equal("new", (string)card["state"]!);
        Assert.Equal(1, (int)queue.Body!["total"]!);
        // The first learning steps, and Easy straight to days.
        var previews = card["previews"]!;
        var expected = Fsrs.Preview(CardState.New(created), created);
        Assert.Equal(created.AddMinutes(1), Api.Time(previews["again"]));
        Assert.Equal(created.AddMinutes(5.5), Api.Time(previews["hard"]));
        Assert.Equal(created.AddMinutes(10), Api.Time(previews["good"]));
        Assert.Equal(expected.Easy, Api.Time(previews["easy"]));
        Assert.True(Api.Time(previews["easy"]) >= created.AddDays(1));

        // An empty queue is an empty list, not an error.
        Assert.Equal(StatusCodes.Status204NoContent, (await Api.Delete($"/review/api/cards/{made.Id}")).Status);
        var empty = await Api.Get(Queue);
        Assert.Equal(StatusCodes.Status200OK, empty.Status);
        Assert.Empty(Fronts(empty));
        Assert.Equal(0, (int)empty.Body!["total"]!);
        // With no card at all, nothing is coming back.
        Assert.Null(empty.Body!["next_due"]);
    }

    [Fact]
    public async Task AGradedCardIsTheNextDue()
    {
        var deck = await Api.Deck("Databases");
        var made = await Api.Card(deck, "What is MVCC?");
        var graded = Now;

        // Again on a new card: back after its first learning step, a minute.
        var answer = await Api.Post($"/review/api/cards/{made.Id}/grades", new JsonObject { ["rating"] = 1, ["version"] = 1 });
        Assert.Equal(StatusCodes.Status201Created, answer.Status);

        var queue = await Api.Get(Queue);
        Assert.Empty(Fronts(queue));
        Assert.Equal(graded.AddMinutes(1), Api.Time(queue.Body!["next_due"]));

        // Once that minute has passed it is due again, and nothing else is coming.
        Api.Review.Clock.Advance(TimeSpan.FromMinutes(1));
        var back = await Api.Get(Queue);
        Assert.Equal(["What is MVCC?"], Fronts(back));
        Assert.Equal(Now, Api.Time(back.Body!["as_of"]));
        Assert.Null(back.Body!["next_due"]);
    }
}