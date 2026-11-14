namespace Review.Brain;

/// <summary>
/// Review's one port to the brain, "Brain access sits behind one port per feature" in
/// <c>High-level/docs/feature-contract.md</c>: <see cref="BrainAdapter"/> is the one
/// implementation, over the client generated from <c>contracts/brain/openapi.json</c>, and tests
/// outside it use a fake. The generated types never cross it. See "What Review tells the brain"
/// in review/README.md.
/// </summary>
public interface IBrain
{
    /// <summary>
    /// Records <paramref name="assessment"/> in the brain of the user <paramref name="token"/>
    /// names, <c>POST /brain/api/events</c>. It never throws for what the brain answers, or for
    /// the brain not answering.
    /// </summary>
    /// <param name="token">The user's access token as the request carried it, from
    /// <c>IPlatformToken</c>: the brain accepts only a user's token.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    Task<BrainAnswer> RecordEvent(string token, AssessmentEvent assessment, CancellationToken ct);
}

/// <summary>What the brain made of an event.</summary>
public abstract record BrainAnswer
{
    private BrainAnswer()
    {
    }

    /// <summary>Recorded now, <c>201</c>, or recorded before under the same key, <c>200</c>.</summary>
    public static readonly BrainAnswer Recorded = new RecordedAnswer();

    /// <summary>Refused for good: a <c>422</c>, or any other <c>4xx</c> but <c>401</c> and
    /// <c>429</c>. Sending it again would be refused again.</summary>
    public static BrainAnswer Refused(int status) => new RefusedAnswer(status);

    /// <summary>No answer worth keeping: a <c>401</c>, a <c>429</c>, a <c>5xx</c>, a timeout,
    /// no connection, or anything else the brain does not document. Worth sending again later.</summary>
    /// <param name="why">For the log: a status or the kind of failure, never the token.</param>
    public static BrainAnswer Unavailable(string why) => new UnavailableAnswer(why);

    public sealed record RecordedAnswer : BrainAnswer;

    public sealed record RefusedAnswer(int Status) : BrainAnswer;

    public sealed record UnavailableAnswer(string Why) : BrainAnswer;
}

/// <summary>
/// One <c>assessment</c> event, as Review sends one per topic of a graded card: the body of
/// <c>POST /brain/api/events</c> in Review's own types. <see cref="AssessmentEvents.For"/> builds
/// them.
/// </summary>
/// <param name="Verb"><c>forgot</c> or <c>recalled</c>.</param>
/// <param name="Topic">A slug as the card holds it.</param>
/// <param name="Origin">Always <c>review</c>.</param>
/// <param name="Source">Always <c>grade</c>.</param>
/// <param name="Run">The grade's id, the same for each event of one grade.</param>
/// <param name="OccurredAt">The grade's <c>reviewed_at</c>, sent with precision <c>instant</c>.</param>
/// <param name="Evidence">Readable text that stands on its own.</param>
/// <param name="Payload">What the grade was, at <see cref="PayloadVersion"/>.</param>
/// <param name="PayloadVersion">The payload's shape, 1.</param>
/// <param name="IdempotencyKey"><c>&lt;grade id&gt;:&lt;slug&gt;</c>.</param>
public sealed record AssessmentEvent(
    string Verb,
    string Topic,
    string Origin,
    string Source,
    string Run,
    DateTimeOffset OccurredAt,
    string Evidence,
    AssessmentPayload Payload,
    int PayloadVersion,
    string IdempotencyKey);

/// <summary>An assessment's payload, version 1, each field on the wire in snake_case.</summary>
/// <param name="Rating">Again 1, Hard 2, Good 3, Easy 4.</param>
/// <param name="RatingName"><c>again</c>, <c>hard</c>, <c>good</c> or <c>easy</c>.</param>
/// <param name="ElapsedDays">Days since the card's last grade, with the fraction; 0 for its first.</param>
/// <param name="StateBefore">The card's state before the grade: <c>new</c>, <c>learning</c>,
/// <c>review</c> or <c>relearning</c>.</param>
/// <param name="StabilityBefore">Null before a card's first grade.</param>
public sealed record AssessmentPayload(
    Guid CardId,
    Guid DeckId,
    int Rating,
    string RatingName,
    double ElapsedDays,
    string StateBefore,
    double? StabilityBefore,
    double StabilityAfter,
    double DifficultyAfter,
    DateTimeOffset DueAfter);