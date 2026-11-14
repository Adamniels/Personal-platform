using System.Text.Json.Nodes;

using Dapper;

using Microsoft.AspNetCore.Http;

using Npgsql;

using Review.Scheduling;

namespace Review.Tests.Scheduling;

/// <summary>
/// Grading a card, <c>POST /review/api/cards/{id}/grades</c>, on a database of each test's own:
/// the schedule FSRS gives it, and the log every grade is kept in. See "When a card comes back" in
/// review/README.md.
/// </summary>
public sealed class GradeTests : IAsyncLifetime
{
    private Api? _api;

    private Api Api => _api!;

    public async ValueTask InitializeAsync() => _api = await Api.Start();

    public async ValueTask DisposeAsync() => await Api.DisposeAsync();

    private static string Grades(Guid card) => $"/review/api/cards/{card}/grades";

    private static JsonObject Grade(int rating, int version) => new() { ["rating"] = rating, ["version"] = version };

    private DateTimeOffset Now => Api.Review.Clock.GetUtcNow();

    /// <summary>A grade row as the log holds it.</summary>
    private sealed record Logged(
        Guid UserId,
        Guid CardId,
        short Rating,
        DateTime ReviewedAt,
        double ElapsedDays,
        string StateBefore,
        double? StabilityBefore,
        double? DifficultyBefore,
        string StateAfter,
        double StabilityAfter,
        double DifficultyAfter,
        DateTime DueAfter,
        DateTime? BrainSentAt);

    private Task<List<Logged>> Log() => Api.AsAdmin<Logged>(
        """
        SELECT user_id AS UserId, card_id AS CardId, rating AS Rating, reviewed_at AS ReviewedAt,
               elapsed_days AS ElapsedDays, state_before AS StateBefore, stability_before AS StabilityBefore,
               difficulty_before AS DifficultyBefore, state_after AS StateAfter, stability_after AS StabilityAfter,
               difficulty_after AS DifficultyAfter, due_after AS DueAfter, brain_sent_at AS BrainSentAt
        FROM grades ORDER BY reviewed_at, id
        """);

    /// <summary>The card's schedule as the table holds it, read past every policy.</summary>
    private async Task<CardState> Schedule(Guid card)
    {
        var row = (await Api.AsAdmin<(string State, int? Step, double? Stability, double? Difficulty, DateTime Due, DateTime? LastReview)>(
            "SELECT state, step, stability, difficulty, due, last_review FROM cards WHERE id = @card", new { card })).Single();
        return new CardState(
            CardState.ParseState(row.State), row.Step, row.Stability, row.Difficulty,
            new DateTimeOffset(row.Due), row.LastReview is { } last ? new DateTimeOffset(last) : null);
    }

    private static void HasPreviews(JsonNode card, Previews expected)
    {
        var previews = card["previews"]!.AsObject();
        Assert.Equal(["again", "easy", "good", "hard"], previews.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(expected.Again, Api.Time(previews["again"]));
        Assert.Equal(expected.Hard, Api.Time(previews["hard"]));
        Assert.Equal(expected.Good, Api.Time(previews["good"]));
        Assert.Equal(expected.Easy, Api.Time(previews["easy"]));
    }

    [Fact]
    public async Task GradingSchedulesTheCardAndLogsTheGrade()
    {
        var deck = await Api.Deck("Databases");
        var made = await Api.Card(deck, "What is MVCC?");
        var id = made.Id;
        var created = Now;

        // A new card, graded Good: its first real state, the first learning step.
        Api.Review.Clock.Advance(TimeSpan.FromSeconds(40));
        var first = Now;
        var expected = Fsrs.Grade(CardState.New(created), Rating.Good, first);
        var graded = await Api.Post(Grades(id), Grade(3, 1));

        Assert.Equal(StatusCodes.Status201Created, graded.Status);
        Assert.StartsWith("application/json", graded.ContentType, StringComparison.Ordinal);
        var card = graded.Body!;
        // The card as a read answers it, plus its previews.
        var read = (await Api.Get($"/review/api/cards/{id}")).Body!.AsObject();
        var withoutPreviews = card.DeepClone().AsObject();
        withoutPreviews.Remove("previews");
        Assert.True(JsonNode.DeepEquals(read, withoutPreviews));
        Assert.Equal("learning", (string)card["state"]!);
        Assert.Equal(first.AddMinutes(10), Api.Time(card["due"]));
        Assert.Equal(first, Api.Time(card["last_review"]));
        Assert.Equal(2, (int)card["version"]!);
        // Grading is not editing: the card's content and its updated_at stay.
        Assert.Equal(made.Body!["updated_at"]!.ToJsonString(), card["updated_at"]!.ToJsonString());
        Assert.Equal(expected, await Schedule(id));
        HasPreviews(card, Fsrs.Preview(expected, first));

        var log = Assert.Single(await Log());
        Assert.Equal(
            new Logged(Api.Me, id, 3, first.UtcDateTime, 0, "new", null, null, "learning",
                expected.Stability!.Value, expected.Difficulty!.Value, expected.Due.UtcDateTime, null),
            log);

        // Graded again, ten minutes on: out of learning, and a second row, the days since the first.
        Api.Review.Clock.Advance(TimeSpan.FromMinutes(10));
        var second = Now;
        var next = Fsrs.Grade(expected, Rating.Good, second);
        var again = await Api.Post(Grades(id), Grade(3, 2));

        Assert.Equal(StatusCodes.Status201Created, again.Status);
        Assert.Equal("review", (string)again.Body!["state"]!);
        Assert.Equal(next.Due, Api.Time(again.Body!["due"]));
        Assert.Equal(3, (int)again.Body!["version"]!);
        Assert.Equal(next, await Schedule(id));
        HasPreviews(again.Body!, Fsrs.Preview(next, second));
        var both = await Log();
        Assert.Equal(2, both.Count);
        Assert.Equal(10.0 / 1440, both[1].ElapsedDays, 12);
        Assert.Equal(
            new Logged(Api.Me, id, 3, second.UtcDateTime, 0, "learning", expected.Stability, expected.Difficulty, "review",
                next.Stability!.Value, next.Difficulty!.Value, next.Due.UtcDateTime, null),
            both[1] with { ElapsedDays = 0 });

        // A mature card, last graded weeks ago, forgotten: relearning, from the state it had.
        var mature = (await Api.Card(deck, "What is a B tree?")).Id;
        var lastReview = Now.AddDays(-30);
        await Api.AsUser(Api.Me, """
            UPDATE cards SET state = 'review', step = NULL, stability = 25.5, difficulty = 6.25, due = @due, last_review = @last
            WHERE id = @mature
            """, new { mature, due = Now.AddDays(-4), last = lastReview });
        var before = await Schedule(mature);
        var lapse = Fsrs.Grade(before, Rating.Again, Now);

        var forgotten = await Api.Post(Grades(mature), Grade(1, 1));

        Assert.Equal(StatusCodes.Status201Created, forgotten.Status);
        Assert.Equal("relearning", (string)forgotten.Body!["state"]!);
        Assert.Equal(lapse, await Schedule(mature));
        var logged = (await Log()).Single(row => row.CardId == mature);
        Assert.Equal(1, logged.Rating);
        Assert.Equal(30, logged.ElapsedDays, 9);
        Assert.Equal(("review", 25.5, 6.25), (logged.StateBefore, logged.StabilityBefore!.Value, logged.DifficultyBefore!.Value));
        Assert.Equal(("relearning", lapse.Stability!.Value, lapse.Difficulty!.Value), (logged.StateAfter, logged.StabilityAfter, logged.DifficultyAfter));
    }

    [Fact]
    public async Task AStaleGradeChangesNothing()
    {
        var deck = await Api.Deck("Databases");
        var id = (await Api.Card(deck, "What is MVCC?")).Id;

        var first = await Api.Post(Grades(id), Grade(3, 1));
        Assert.Equal(StatusCodes.Status201Created, first.Status);

        // The same grade again, as a double click or a retry sends it, and one from the future:
        // the card moved on, so neither changes it, nor logs.
        foreach (var version in new[] { 1, 7 })
        {
            var stale = await Api.Post(Grades(id), Grade(3, version));
            Assert.Equal(StatusCodes.Status409Conflict, stale.Status);
            Assert.StartsWith("application/problem+json", stale.ContentType, StringComparison.Ordinal);
        }

        Assert.Single(await Log());
        var read = (await Api.Get($"/review/api/cards/{id}")).Body!;
        Assert.Equal(2, (int)read["version"]!);

        // An edit moves the version on too, so a grade from before it is stale.
        var edited = await Api.Patch($"/review/api/cards/{id}", new JsonObject { ["version"] = 2, ["front"] = "What does MVCC stand for?" });
        Assert.Equal(StatusCodes.Status200OK, edited.Status);
        Assert.Equal(StatusCodes.Status409Conflict, (await Api.Post(Grades(id), Grade(4, 2))).Status);
        Assert.Single(await Log());

        // Editing a card never touches its schedule.
        Assert.Equal(read["due"]!.ToJsonString(), edited.Body!["due"]!.ToJsonString());
        Assert.Equal(read["state"]!.ToJsonString(), edited.Body!["state"]!.ToJsonString());

        // The conditional update itself, run twice from the same version: the second changes no row.
        var updates = new List<int>();
        for (var i = 0; i < 2; i++)
        {
            updates.Add(await Api.AsUser(Api.Me, "UPDATE cards SET version = version + 1 WHERE id = @id AND version = 3", new { id }));
        }

        Assert.Equal([1, 0], updates);
    }

    [Theory]
    [InlineData("no rating", "rating")]
    [InlineData("rating 0", "rating")]
    [InlineData("rating 5", "rating")]
    [InlineData("no version", "version")]
    public async Task EveryRuleOfAGradeIsEnforced(string rule, string field)
    {
        var deck = await Api.Deck("Databases");
        var id = (await Api.Card(deck, "What is MVCC?")).Id;
        var body = Grade(3, 1);
        switch (rule)
        {
            case "no rating": body.Remove("rating"); break;
            case "rating 0": body["rating"] = 0; break;
            case "rating 5": body["rating"] = 5; break;
            case "no version": body.Remove("version"); break;
            default: throw new ArgumentOutOfRangeException(nameof(rule), rule, null);
        }

        var refused = await Api.Post(Grades(id), body);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Status);
        Assert.Equal([field], refused.Fields);
        Assert.Empty(await Log());
        Assert.Equal(1, (int)(await Api.Get($"/review/api/cards/{id}")).Body!["version"]!);

        // A rating is a number, never a string holding one.
        var text = Grade(3, 1);
        text["rating"] = "3";
        Assert.Equal(StatusCodes.Status400BadRequest, (await Api.Post(Grades(id), text)).Status);
    }

    [Fact]
    public async Task AnotherUsersCardIs404()
    {
        var (_, bobsCard) = await Api.BobsDeckAndCard();
        var before = await Api.AsAdmin<string>("SELECT state || ' v' || version FROM cards WHERE id = @bobsCard", new { bobsCard });

        foreach (var card in new[] { bobsCard, Guid.CreateVersion7() })
        {
            var refused = await Api.Post(Grades(card), Grade(3, 1));
            Assert.Equal(StatusCodes.Status404NotFound, refused.Status);
            Assert.StartsWith("application/problem+json", refused.ContentType, StringComparison.Ordinal);
        }

        Assert.Equal(before, await Api.AsAdmin<string>("SELECT state || ' v' || version FROM cards WHERE id = @bobsCard", new { bobsCard }));
        Assert.Empty(await Log());
    }

    [Fact]
    public async Task DeletingACardDeletesItsLog()
    {
        var deck = await Api.Deck("Databases");
        var kept = (await Api.Card(deck, "What is a B tree?")).Id;
        var deleted = (await Api.Card(deck, "What is MVCC?")).Id;
        Assert.Equal(StatusCodes.Status201Created, (await Api.Post(Grades(kept), Grade(3, 1))).Status);
        Assert.Equal(StatusCodes.Status201Created, (await Api.Post(Grades(deleted), Grade(1, 1))).Status);
        Assert.Equal(StatusCodes.Status201Created, (await Api.Post(Grades(deleted), Grade(3, 2))).Status);
        Assert.Equal(3, (await Log()).Count);

        Assert.Equal(StatusCodes.Status204NoContent, (await Api.Delete($"/review/api/cards/{deleted}")).Status);

        Assert.Equal([kept], (await Log()).Select(row => row.CardId));
    }

    [Fact]
    public async Task AGradeIsNeverRewritten()
    {
        var deck = await Api.Deck("Databases");
        var id = (await Api.Card(deck, "What is MVCC?")).Id;
        Assert.Equal(StatusCodes.Status201Created, (await Api.Post(Grades(id), Grade(3, 1))).Status);

        // review_app, through the door as the grade's own user, may not change what was graded nor
        // delete it, only mark whether the brain has it.
        foreach (var sql in new[] { "UPDATE grades SET rating = 4", "UPDATE grades SET reviewed_at = now()", "DELETE FROM grades" })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => Api.AsUser(Api.Me, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }

        Assert.Equal(1, await Api.AsUser(Api.Me, "UPDATE grades SET brain_sent_at = now()"));
        var log = Assert.Single(await Log());
        Assert.Equal(3, log.Rating);
        Assert.NotNull(log.BrainSentAt);
    }
}