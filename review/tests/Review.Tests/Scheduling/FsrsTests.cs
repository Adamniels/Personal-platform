using System.Globalization;
using System.Text.Json.Nodes;

using Review.Scheduling;

namespace Review.Tests.Scheduling;

/// <summary>
/// The scheduler, a pure function, against py-fsrs 6.3.3 itself: <c>fsrs-vectors.json</c> beside
/// this file, written by <c>review/tests/tools/fsrs_vectors.py</c>. See "When a card comes back"
/// in review/README.md.
/// </summary>
public sealed class FsrsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);

    private static JsonArray Sequences =>
        JsonNode.Parse(File.ReadAllText(Repository.Path("review", "tests", "Review.Tests", "Scheduling", "fsrs-vectors.json")))!["sequences"]!.AsArray();

    private static DateTimeOffset? Time(JsonNode? value) =>
        value is null ? null : DateTimeOffset.Parse((string)value!, CultureInfo.InvariantCulture);

    private static CardState StateOf(JsonNode node) => new(
        CardState.ParseState((string)node["state"]!),
        (string)node["state"]! == "new" ? null : (int?)node["step"],
        (double?)node["stability"],
        (double?)node["difficulty"],
        Time(node["due"])!.Value,
        Time(node["last_review"]));

    private static void Close(double? expected, double? actual, string what)
    {
        Assert.True(expected.HasValue == actual.HasValue, $"{what}: expected {expected}, got {actual}");
        if (expected is { } e && actual is { } a)
        {
            Assert.True(Math.Abs(a - e) <= 1e-9 * Math.Abs(e), $"{what}: expected {e:R}, got {a:R}");
        }
    }

    [Fact]
    public void EveryReferenceVectorIsReproduced()
    {
        var sequences = Sequences;
        Assert.True(sequences.Count >= 30, "at least 30 sequences, as the slice settles");
        var grades = 0;
        foreach (var sequence in sequences)
        {
            var name = (string)sequence!["name"]!;
            var card = StateOf(sequence["start"]!);
            var count = 0;
            foreach (var grade in sequence["grades"]!.AsArray())
            {
                var rating = (Rating)(int)grade!["rating"]!;
                var at = Time(grade["at"])!.Value;
                var expected = StateOf(grade["after"]!);
                var what = $"{name}, grade {++count} ({rating} at {at:O})";

                card = Fsrs.Grade(card, rating, at);

                Assert.True(expected.State == card.State, $"{what}: state {expected.State}, got {card.State}");
                Assert.True(expected.Step == card.Step, $"{what}: step {expected.Step}, got {card.Step}");
                Close(expected.Stability, card.Stability, $"{what}: stability");
                Close(expected.Difficulty, card.Difficulty, $"{what}: difficulty");
                Assert.True(
                    Math.Abs((card.Due - expected.Due).TotalSeconds) < 1,
                    $"{what}: due {expected.Due:O}, got {card.Due:O}");
                Assert.Equal(at, card.LastReview);
                grades++;
            }
        }

        Assert.True(grades >= sequences.Count, "every sequence holds at least one grade");
    }

    [Fact]
    public void PreviewsAreWhatGradingWouldDo()
    {
        // From each kind of state a card can be in, early and on time.
        var learning = Fsrs.Grade(CardState.New(T0), Rating.Good, T0);
        var review = Fsrs.Grade(learning, Rating.Good, learning.Due);
        var relearning = Fsrs.Grade(review, Rating.Again, review.Due);
        foreach (var card in new[] { CardState.New(T0), learning, review, relearning })
        {
            foreach (var now in new[] { card.Due, card.Due.AddHours(-3), card.Due.AddDays(4) })
            {
                var previews = Fsrs.Preview(card, now);

                Assert.Equal(Fsrs.Grade(card, Rating.Again, now).Due, previews.Again);
                Assert.Equal(Fsrs.Grade(card, Rating.Hard, now).Due, previews.Hard);
                Assert.Equal(Fsrs.Grade(card, Rating.Good, now).Due, previews.Good);
                Assert.Equal(Fsrs.Grade(card, Rating.Easy, now).Due, previews.Easy);
                // And never in the past, nor out of order once the card is past its first step.
                Assert.True(previews.Again > now);
                Assert.True(previews.Again <= previews.Hard && previews.Hard <= previews.Good && previews.Good <= previews.Easy);
            }
        }

        // Previewing changes nothing: the four answers are what grading would do, not what it did.
        var once = Fsrs.Preview(review, review.Due);
        Assert.Equal(once, Fsrs.Preview(review, review.Due));
    }

    [Fact]
    public void NoIntervalExceedsTheMaximum()
    {
        // A card that has been easy for decades, then easy again and again.
        var card = new CardState(State.Review, null, 30_000, 1, T0, T0.AddDays(-29_000));
        var longest = TimeSpan.Zero;
        for (var i = 0; i < 40; i++)
        {
            var now = card.Due;
            card = Fsrs.Grade(card, Rating.Easy, now);
            var interval = card.Due - now;
            Assert.True(interval <= TimeSpan.FromDays(Fsrs.MaximumInterval), $"grade {i + 1}: {interval.TotalDays} days");
            Assert.True(Fsrs.Preview(card, card.Due).Easy - card.Due <= TimeSpan.FromDays(Fsrs.MaximumInterval));
            longest = interval > longest ? interval : longest;
        }

        // It reaches the maximum rather than stopping short of it.
        Assert.Equal(TimeSpan.FromDays(Fsrs.MaximumInterval), longest);
    }

    [Fact]
    public void TheSchedulerNeverReadsTheClock()
    {
        // The same card, grade and time give the same answer, whenever it is asked.
        var card = Fsrs.Grade(CardState.New(T0), Rating.Easy, T0);
        Assert.Equal(Fsrs.Grade(card, Rating.Good, T0.AddDays(9)), Fsrs.Grade(card, Rating.Good, T0.AddDays(9)));
        Assert.Equal(T0.AddDays(9), Fsrs.Grade(card, Rating.Good, T0.AddDays(9)).LastReview);
    }
}