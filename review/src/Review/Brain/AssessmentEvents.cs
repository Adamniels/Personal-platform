using System.Globalization;

using Review.Scheduling;

namespace Review.Brain;

/// <summary>
/// A grade as the brain hears it: one <c>assessment</c> event per topic of the card, in the
/// card's topic order, all with the grade's id as their <c>run</c>, so consolidation counts the
/// grade once and not once per topic. See "What Review tells the brain" in review/README.md.
/// </summary>
public static class AssessmentEvents
{
    public const string Origin = "review";
    public const string Source = "grade";
    public const int PayloadVersion = 1;

    /// <summary>How much of the front the evidence quotes, the ellipsis included.</summary>
    public const int FrontMax = 200;

    public static IReadOnlyList<AssessmentEvent> For(GradeToSend grade)
    {
        ArgumentNullException.ThrowIfNull(grade);
        var rating = (Rating)grade.Rating;
        var run = grade.Id.ToString("D", CultureInfo.InvariantCulture);
        var evidence = Evidence(grade, rating);
        var payload = new AssessmentPayload(
            grade.CardId,
            grade.DeckId,
            grade.Rating,
            RatingName(rating),
            grade.ElapsedDays,
            grade.StateBefore,
            grade.StabilityBefore,
            grade.StabilityAfter,
            grade.DifficultyAfter,
            grade.DueAfter);
        return [.. grade.Topics.Select(slug => new AssessmentEvent(
            Verb: rating == Rating.Again ? "forgot" : "recalled",
            Topic: slug,
            Origin: Origin,
            Source: Source,
            Run: run,
            OccurredAt: grade.ReviewedAt,
            Evidence: evidence,
            Payload: payload,
            PayloadVersion: PayloadVersion,
            IdempotencyKey: $"{run}:{slug}"))];
    }

    /// <summary>
    /// What happened, readable years later without asking anything else: <c>Reviewed a card in
    /// "Databases": "What is MVCC?", recalled it (Good), 3 days after the last review.</c> A first
    /// grade, from a new card, ends <c>its first review.</c> instead, and one less than a day after
    /// the last ends <c>the same day as the last review.</c> Days are rounded down.
    /// </summary>
    private static string Evidence(GradeToSend grade, Rating rating)
    {
        var what = rating == Rating.Again ? "forgot it" : $"recalled it ({rating})";
        var days = (long)Math.Floor(grade.ElapsedDays);
        var when = grade.StateBefore == CardState.StateName(State.New) ? "its first review"
            : days == 0 ? "the same day as the last review"
            : days == 1 ? "1 day after the last review"
            : string.Create(CultureInfo.InvariantCulture, $"{days} days after the last review");
        return $"Reviewed a card in \"{grade.DeckName}\": \"{Cut(grade.Front)}\", {what}, {when}.";
    }

    /// <summary>The front, or its first 199 characters and an ellipsis when it is longer than
    /// <see cref="FrontMax"/>, counted in text elements, so a character drawn with two UTF-16
    /// units, such as an emoji, is never cut in half.</summary>
    private static string Cut(string front)
    {
        var elements = new StringInfo(front);
        return elements.LengthInTextElements <= FrontMax
            ? front
            : elements.SubstringByTextElements(0, FrontMax - 1) + "…";
    }

    private static string RatingName(Rating rating) => rating switch
    {
        Rating.Again => "again",
        Rating.Hard => "hard",
        Rating.Good => "good",
        Rating.Easy => "easy",
        _ => throw new ArgumentOutOfRangeException(nameof(rating), rating, "a rating is 1 to 4"),
    };
}

/// <summary>A grade not yet sent, with what of its card and deck the events tell: the card as it
/// is when the grade is sent, which for a grade sent at once is the card as it was graded.</summary>
/// <param name="Topics">The card's topic slugs, in its order.</param>
/// <param name="Rating">Again 1, Hard 2, Good 3, Easy 4.</param>
public sealed record GradeToSend(
    Guid Id,
    Guid CardId,
    Guid DeckId,
    string DeckName,
    string Front,
    IReadOnlyList<string> Topics,
    int Rating,
    DateTimeOffset ReviewedAt,
    double ElapsedDays,
    string StateBefore,
    double? StabilityBefore,
    double StabilityAfter,
    double DifficultyAfter,
    DateTimeOffset DueAfter);