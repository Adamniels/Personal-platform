using System.Net;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

using Review.Brain;

namespace Review.Tests.Brain;

/// <summary>
/// Grades reaching the brain: each grade as one assessment event per topic of its card, sent after
/// the response has gone, and the unsent ones sent again after the user's next request. See "What
/// Review tells the brain" in review/README.md.
/// </summary>
public sealed class BrainEventsTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Reviewed = new(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);

    private Api? _api;

    private Api Api => _api!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }
    }

    private static string Grades(Guid card) => $"/review/api/cards/{card}/grades";

    private static JsonObject Grade(int rating, int version) => new() { ["rating"] = rating, ["version"] = version };

    /// <summary>A grade's row, as the superuser reads it.</summary>
    private sealed record Row(Guid Id, DateTime? BrainSentAt, DateTime? BrainRefusedAt, short? BrainRefusedStatus);

    private async Task<List<Row>> GradesOf(Guid user) => await Api.AsAdmin<Row>(
        """
        SELECT id AS Id, brain_sent_at AS BrainSentAt, brain_refused_at AS BrainRefusedAt, brain_refused_status AS BrainRefusedStatus
        FROM grades WHERE user_id = @user ORDER BY reviewed_at, id
        """, new { user });

    private async Task<Row> GradeRow(Guid id) => (await Api.AsAdmin<Row>(
        """
        SELECT id AS Id, brain_sent_at AS BrainSentAt, brain_refused_at AS BrainRefusedAt, brain_refused_status AS BrainRefusedStatus
        FROM grades WHERE id = @id
        """, new { id })).Single();

    /// <summary>A deck named Databases and a card in it with the topic <c>postgres</c>, written in
    /// SQL as <paramref name="user"/>, so no request is made, and no grade sent, along the way.</summary>
    private async Task<Guid> DeckAndCard(Guid user)
    {
        var deck = Guid.CreateVersion7();
        var card = Guid.CreateVersion7();
        await Api.AsUser(user, """
            INSERT INTO decks (id, user_id, name, created_at) VALUES (@deck, @user, 'Databases', now());
            INSERT INTO cards (id, user_id, deck_id, front, back, due, created_at, updated_at)
                VALUES (@card, @user, @deck, 'What is MVCC?', 'Versions', now(), now(), now());
            INSERT INTO card_topics (card_id, user_id, position, slug, name) VALUES (@card, @user, 0, 'postgres', 'Postgres');
            """, new { deck, card, user });
        return card;
    }

    /// <summary>An unsent grade of <paramref name="card"/>, Good, given at
    /// <paramref name="at"/>, written in SQL as <paramref name="user"/>: its id.</summary>
    private async Task<Guid> UnsentGrade(Guid user, Guid card, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7(at);
        await Api.AsUser(user, """
            INSERT INTO grades (id, user_id, card_id, rating, reviewed_at, elapsed_days, state_before, stability_before,
                                difficulty_before, state_after, stability_after, difficulty_after, due_after)
            VALUES (@id, @user, @card, 3, @at, 0, 'new', NULL, NULL, 'learning', 2.3, 5.1, @due)
            """, new { id, user, card, at, due = at.AddMinutes(10) });
        return id;
    }

    /// <summary>Any authenticated request to review, which is what sends a user's unsent grades.</summary>
    private async Task Touch()
    {
        var answer = await Api.Get("/review/api/decks");
        Assert.Equal(StatusCodes.Status200OK, answer.Status);
    }

    [Fact]
    public async Task AGradeBecomesOneEventPerTopic()
    {
        var brain = FakeBrain.Recording();
        _api = await Api.Start(brain);
        var deck = await Api.Deck("Databases");
        var card = await Api.Card(deck, "What is MVCC?", "postgres", "mvcc");

        var good = await Api.Post(Grades(card.Id), Grade(3, (int)card.Body!["version"]!));
        Assert.Equal(StatusCodes.Status201Created, good.Status);
        var first = (await GradesOf(Api.Me)).Single().Id;
        await Wait.Until("the Good grade sent", async () => (await GradeRow(first)).BrainSentAt is not null);

        var events = brain.Calls.Where(c => c.Event.Run == first.ToString()).ToList();
        Assert.Equal(["postgres", "mvcc"], events.Select(c => c.Event.Topic));
        Assert.All(events, c =>
        {
            Assert.Equal(Contract.Token, c.Token);
            Assert.Equal("recalled", c.Event.Verb);
            Assert.Equal("review", c.Event.Origin);
            Assert.Equal("grade", c.Event.Source);
            Assert.Equal($"{first}:{c.Event.Topic}", c.Event.IdempotencyKey);
            Assert.Equal("Reviewed a card in \"Databases\": \"What is MVCC?\", recalled it (Good), its first review.", c.Event.Evidence);
        });

        Api.Review.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = await Api.Post(Grades(card.Id), Grade(1, (int)good.Body!["version"]!));
        Assert.Equal(StatusCodes.Status201Created, again.Status);
        var second = (await GradesOf(Api.Me)).Single(row => row.Id != first).Id;
        await Wait.Until("the Again grade sent", async () => (await GradeRow(second)).BrainSentAt is not null);

        var forgot = brain.Calls.Where(c => c.Event.Run == second.ToString()).ToList();
        Assert.Equal(["postgres", "mvcc"], forgot.Select(c => c.Event.Topic));
        Assert.All(forgot, c => Assert.Equal("forgot", c.Event.Verb));
        Assert.Equal([$"{second}:postgres", $"{second}:mvcc"], forgot.Select(c => c.Event.IdempotencyKey));
    }

    private static GradeToSend Graded(int rating, string stateBefore, double elapsedDays, string front = "What is MVCC?") => new(
        Id: Guid.Parse("019a8000-0000-7000-8000-000000000001"),
        CardId: Guid.Parse("019a8000-0000-7000-8000-0000000000c1"),
        DeckId: Guid.Parse("019a8000-0000-7000-8000-0000000000d1"),
        DeckName: "Databases",
        Front: front,
        Topics: ["postgres"],
        Rating: rating,
        ReviewedAt: Reviewed,
        ElapsedDays: elapsedDays,
        StateBefore: stateBefore,
        StabilityBefore: stateBefore == "new" ? null : 4.5,
        StabilityAfter: 9.25,
        DifficultyAfter: 5.5,
        DueAfter: Reviewed.AddDays(9));

    [Theory]
    [InlineData(3, "new", 0, "recalled it (Good), its first review.")]
    [InlineData(2, "review", 3.7, "recalled it (Hard), 3 days after the last review.")]
    [InlineData(4, "review", 1.2, "recalled it (Easy), 1 day after the last review.")]
    [InlineData(1, "learning", 0.5, "forgot it, the same day as the last review.")]
    [InlineData(1, "relearning", 30, "forgot it, 30 days after the last review.")]
    public void TheEvidenceStandsOnItsOwn(int rating, string stateBefore, double elapsedDays, string ending)
    {
        var assessment = Assert.Single(AssessmentEvents.For(Graded(rating, stateBefore, elapsedDays)));

        Assert.Equal($"Reviewed a card in \"Databases\": \"What is MVCC?\", {ending}", assessment.Evidence);
        Assert.Equal(Reviewed, assessment.OccurredAt);
        Assert.Equal(1, assessment.PayloadVersion);
        Assert.Equal(
            new AssessmentPayload(
                CardId: Guid.Parse("019a8000-0000-7000-8000-0000000000c1"),
                DeckId: Guid.Parse("019a8000-0000-7000-8000-0000000000d1"),
                Rating: rating,
                RatingName: ((Review.Scheduling.Rating)rating).ToString().ToLowerInvariant(),
                ElapsedDays: elapsedDays,
                StateBefore: stateBefore,
                StabilityBefore: stateBefore == "new" ? null : 4.5,
                StabilityAfter: 9.25,
                DifficultyAfter: 5.5,
                DueAfter: Reviewed.AddDays(9)),
            assessment.Payload);
    }

    [Fact]
    public void TheEvidenceCutsALongFrontTo200Characters()
    {
        var front = string.Concat(Enumerable.Repeat("abcdefghij", 25));

        var evidence = Assert.Single(AssessmentEvents.For(Graded(3, "new", 0, front))).Evidence;

        var quoted = $"\"{front[..199]}…\"";
        Assert.Equal($"Reviewed a card in \"Databases\": {quoted}, recalled it (Good), its first review.", evidence);
        // Exactly 200 is not cut.
        Assert.Contains($"\"{front[..200]}\"", AssessmentEvents.For(Graded(3, "new", 0, front[..200]))[0].Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCutNeverSplitsACharacter()
    {
        // 199 letters, then an emoji of two UTF-16 units, then more: the cut keeps the emoji whole
        // or leaves it out, never half of it.
        var front = new string('a', 198) + "🧠🧠" + new string('b', 10);

        var evidence = Assert.Single(AssessmentEvents.For(Graded(3, "new", 0, front))).Evidence;

        Assert.Contains($"\"{new string('a', 198)}🧠…\"", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GradingNeverWaitsForTheBrain()
    {
        var brain = FakeBrain.Silent();
        _api = await Api.Start(brain);
        var deck = await Api.Deck("Databases");
        var card = await Api.Card(deck, "What is MVCC?", "postgres");

        var grading = Api.Post(Grades(card.Id), Grade(3, (int)card.Body!["version"]!));
        var finished = await Task.WhenAny(grading, Task.Delay(TimeSpan.FromSeconds(10), Ct));

        Assert.Same(grading, finished);
        Assert.Equal(StatusCodes.Status201Created, (await grading).Status);
        var grade = (await GradesOf(Api.Me)).Single();
        await Wait.Until("the brain asked", () => brain.Calls.Any(c => c.Event.Run == grade.Id.ToString()));
        Assert.Null((await GradeRow(grade.Id)).BrainSentAt);
    }

    [Fact]
    public async Task UnsentGradesAreSentOnTheNextRequest()
    {
        var brain = FakeBrain.Down();
        _api = await Api.Start(brain);
        var (_, bobsCard) = await Api.BobsDeckAndCard();
        // Bob's is the oldest of all, so it would be first if it were reachable.
        var bobs = await UnsentGrade(Api.Bob, bobsCard, Reviewed.AddHours(-1));
        var card = await DeckAndCard(Api.Me);
        var mine = new List<Guid>();
        for (var i = 0; i < GradeSender.Batch + 2; i++)
        {
            mine.Add(await UnsentGrade(Api.Me, card, Reviewed.AddMinutes(i)));
        }

        // The brain is down: the first grade is tried, and the rest wait.
        await Touch();
        await Wait.Until("the brain tried", () => brain.Calls.Count > 0);
        await Api.Review.Sender.WhenIdle();
        Assert.All(await GradesOf(Api.Me), row => Assert.Null(row.BrainSentAt));

        brain.Answer = (_, _) => Task.FromResult(BrainAnswer.Recorded);
        var before = brain.Calls.Count;
        await Touch();
        await Wait.Until("50 grades sent", async () => (await GradesOf(Api.Me)).Count(row => row.BrainSentAt is not null) == GradeSender.Batch);
        await Api.Review.Sender.WhenIdle();

        var rows = await GradesOf(Api.Me);
        Assert.All(rows.Take(GradeSender.Batch), row => Assert.NotNull(row.BrainSentAt));
        Assert.All(rows.Skip(GradeSender.Batch), row => Assert.Null(row.BrainSentAt));
        var sent = brain.Calls.Skip(before).ToList();
        Assert.Equal(mine.Take(GradeSender.Batch).Select(id => id.ToString()), sent.Select(c => c.Event.Run));
        Assert.All(brain.Calls, c => Assert.Equal(Contract.Token, c.Token));
        Assert.DoesNotContain(brain.Calls, c => c.Event.Run == bobs.ToString());
        Assert.Null((await GradeRow(bobs)).BrainSentAt);
    }

    /// <summary>The real adapter over a stub of the brain's HTTP, so each status is what the
    /// brain would answer, with a clock of its own for the timeout.</summary>
    private static (BrainAdapter Brain, StubBrainHttp Http, FakeTimeProvider Clock) OverHttp()
    {
        var http = new StubBrainHttp();
        var clock = new FakeTimeProvider(Reviewed);
        return (new BrainAdapter(new HttpClient(http), new Uri("http://127.0.0.1:8082"), Api.Origin, clock), http, clock);
    }

    private static string KeyOf(StubBrainHttp.Sent sent) => (string)JsonNode.Parse(sent.Body)!["idempotency_key"]!;

    [Fact]
    public async Task ARefusalStopsTheRetries()
    {
        var (adapter, http, clock) = OverHttp();
        _api = await Api.Start(adapter);
        var card = await DeckAndCard(Api.Me);
        int CallsFor(Guid grade) => http.Requests.Count(sent => KeyOf(sent) == $"{grade}:postgres");

        var refused = await UnsentGrade(Api.Me, card, Reviewed);
        http.Answer = (_, _) => Task.FromResult(StubBrainHttp.Status(HttpStatusCode.UnprocessableEntity));
        await Touch();
        await Wait.Until("the refusal marked", async () => (await GradeRow(refused)).BrainRefusedAt is not null);
        var row = await GradeRow(refused);
        Assert.Equal((short)422, row.BrainRefusedStatus);
        Assert.Null(row.BrainSentAt);

        var waiting = await UnsentGrade(Api.Me, card, Reviewed.AddMinutes(1));
        var tries = 0;
        foreach (var unavailable in new Func<CancellationToken, Task<HttpResponseMessage>>[]
        {
            _ => Task.FromResult(StubBrainHttp.Status(HttpStatusCode.Unauthorized)),
            _ => Task.FromResult(StubBrainHttp.Status(HttpStatusCode.TooManyRequests)),
            _ => Task.FromResult(StubBrainHttp.Status(HttpStatusCode.InternalServerError)),
            _ => Task.FromResult(StubBrainHttp.Status(HttpStatusCode.ServiceUnavailable)),
            StubBrainHttp.Never,
        })
        {
            http.Answer = (_, ct) => unavailable(ct);
            await Touch();
            tries++;
            await Wait.Until($"try {tries}", () => CallsFor(waiting) == tries);
            // Only the call that never answers is still waiting, until its two seconds pass.
            clock.Advance(BrainAdapter.Timeout);
            await Api.Review.Sender.WhenIdle();
            row = await GradeRow(waiting);
            Assert.Null(row.BrainSentAt);
            Assert.Null(row.BrainRefusedAt);
        }

        http.Answer = (_, _) => Task.FromResult(StubBrainHttp.Status(HttpStatusCode.Created));
        await Touch();
        await Wait.Until("the waiting grade sent", async () => (await GradeRow(waiting)).BrainSentAt is not null);
        await Api.Review.Sender.WhenIdle();
        Assert.Equal(1, CallsFor(refused));
        Assert.Equal((short)422, (await GradeRow(refused)).BrainRefusedStatus);
    }

    [Fact]
    public async Task ASecondSendIsHarmless()
    {
        var (adapter, http, _) = OverHttp();
        _api = await Api.Start(adapter);
        var card = await DeckAndCard(Api.Me);
        var grade = await UnsentGrade(Api.Me, card, Reviewed);
        // The brain already has this key, so it answers 200 with the event it holds.
        http.Answer = (_, _) => Task.FromResult(StubBrainHttp.Status(HttpStatusCode.OK));

        await Touch();

        await Wait.Until("the grade sent", async () => (await GradeRow(grade)).BrainSentAt is not null);
        Assert.Null((await GradeRow(grade)).BrainRefusedAt);
    }
}