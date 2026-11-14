namespace Review.Scheduling;

/// <summary>
/// FSRS-6, the Free Spaced Repetition Scheduler, as py-fsrs 6.3.3's <c>Scheduler</c> implements it
/// with its defaults and fuzzing off, written here line for line rather than taken as a
/// dependency, since no .NET port is trusted. <c>FsrsTests.EveryReferenceVectorIsReproduced</c>
/// holds it to that release value for value, through
/// <c>tests/Review.Tests/Scheduling/fsrs-vectors.json</c>. See "When a card comes back" in
/// review/README.md.
///
/// It is pure: the time is a parameter, and it never reads the clock. The arithmetic follows
/// py-fsrs's expression by expression, <c>math.e ** x</c> as <see cref="Math.Pow"/> and Python's
/// <c>round</c> as <see cref="Math.Round(double)"/>, both of which round half to even, so a
/// difference in the last bit cannot move an interval by a day.
/// </summary>
public static class Fsrs
{
    /// <summary>py-fsrs 6.3.3's <c>DEFAULT_PARAMETERS</c>, w0 to w20, copied from that release.</summary>
    public static readonly IReadOnlyList<double> Parameters =
    [
        0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194, 0.001, 1.8722, 0.1666, 0.796,
        1.4835, 0.0614, 0.2629, 1.6483, 0.6014, 1.8729, 0.5425, 0.0912, 0.0658, 0.1542,
    ];

    /// <summary>The share of cards meant to be recalled when they come due.</summary>
    public const double DesiredRetention = 0.9;

    /// <summary>The most days a card is scheduled ahead, about a hundred years.</summary>
    public const int MaximumInterval = 36_500;

    /// <summary>A new card's steps before it is scheduled in days.</summary>
    public static readonly IReadOnlyList<TimeSpan> LearningSteps = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)];

    /// <summary>A forgotten card's steps before it is scheduled in days again.</summary>
    public static readonly IReadOnlyList<TimeSpan> RelearningSteps = [TimeSpan.FromMinutes(10)];

    private const double StabilityMin = 0.001;
    private const double MinDifficulty = 1.0;
    private const double MaxDifficulty = 10.0;

    private static double W(int index) => Parameters[index];

    private static readonly double Decay = -W(20);
    private static readonly double Factor = Math.Pow(0.9, 1 / Decay) - 1;

    /// <summary>The card as it is after <paramref name="rating"/> at <paramref name="now"/>:
    /// py-fsrs's <c>review_card</c>.</summary>
    public static CardState Grade(CardState card, Rating rating, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!Enum.IsDefined(rating))
        {
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "no such rating");
        }

        // A new card is py-fsrs's fresh Card: Learning at step 0, with no stability or difficulty.
        var state = card.State == State.New ? State.Learning : card.State;
        var step = card.State == State.New ? 0 : card.Step;
        var stability = card.Stability;
        var difficulty = card.Difficulty;
        int? daysSinceLastReview = card.LastReview is { } last ? WholeDays(now - last) : null;
        var sameDay = daysSinceLastReview < 1;
        TimeSpan interval;

        switch (state)
        {
            case State.Learning:
            case State.Relearning:
                var steps = state == State.Learning ? LearningSteps : RelearningSteps;
                var currentStep = step ?? throw new ArgumentException("a learning or relearning card has a step", nameof(card));
                if (state == State.Learning && (stability is null || difficulty is null))
                {
                    stability = InitialStability(rating);
                    difficulty = ClampDifficulty(InitialDifficulty(rating));
                }
                else
                {
                    var (s, d) = Known(stability, difficulty);
                    stability = sameDay ? ShortTermStability(s, rating) : NextStability(d, s, Retrievability(card, now), rating);
                    difficulty = NextDifficulty(d, rating);
                }

                if (steps.Count == 0 || (currentStep >= steps.Count && rating != Rating.Again))
                {
                    (state, step, interval) = (State.Review, null, Days(stability.Value));
                }
                else
                {
                    switch (rating)
                    {
                        case Rating.Again:
                            step = 0;
                            interval = steps[0];
                            break;
                        case Rating.Hard:
                            interval = currentStep == 0 && steps.Count == 1 ? steps[0] * 1.5
                                : currentStep == 0 ? (steps[0] + steps[1]) / 2.0
                                : steps[currentStep];
                            break;
                        case Rating.Good when currentStep + 1 == steps.Count:
                            (state, step, interval) = (State.Review, null, Days(stability.Value));
                            break;
                        case Rating.Good:
                            step = currentStep + 1;
                            interval = steps[currentStep + 1];
                            break;
                        default:
                            (state, step, interval) = (State.Review, null, Days(stability.Value));
                            break;
                    }
                }

                break;

            case State.Review:
                {
                    var (s, d) = Known(stability, difficulty);
                    stability = sameDay ? ShortTermStability(s, rating) : NextStability(d, s, Retrievability(card, now), rating);
                    difficulty = NextDifficulty(d, rating);
                    if (rating == Rating.Again && RelearningSteps.Count > 0)
                    {
                        (state, step, interval) = (State.Relearning, 0, RelearningSteps[0]);
                    }
                    else
                    {
                        interval = Days(stability.Value);
                    }

                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(card), card.State, "no such state");
        }

        return new CardState(state, step, stability, difficulty, now + interval, now);
    }

    /// <summary>When each grade at <paramref name="now"/> would bring the card back.</summary>
    public static Previews Preview(CardState card, DateTimeOffset now) => new(
        Grade(card, Rating.Again, now).Due,
        Grade(card, Rating.Hard, now).Due,
        Grade(card, Rating.Good, now).Due,
        Grade(card, Rating.Easy, now).Due);

    /// <summary>The predicted chance the card is recalled at <paramref name="now"/>, 0 for a card
    /// never graded: py-fsrs's <c>get_card_retrievability</c>.</summary>
    public static double Retrievability(CardState card, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.LastReview is not { } last || card.Stability is not { } stability)
        {
            return 0;
        }

        var elapsedDays = Math.Max(0, WholeDays(now - last));
        return Math.Pow(1 + (Factor * elapsedDays / stability), Decay);
    }

    /// <summary>Python's <c>timedelta.days</c>: whole days, rounded down, so a moment before is
    /// -1.</summary>
    private static int WholeDays(TimeSpan elapsed) => (int)Math.Floor((double)elapsed.Ticks / TimeSpan.TicksPerDay);

    private static (double Stability, double Difficulty) Known(double? stability, double? difficulty) =>
        stability is { } s && difficulty is { } d ? (s, d) : throw new ArgumentException("a graded card has a stability and a difficulty");

    private static double ClampStability(double stability) => Math.Max(stability, StabilityMin);

    private static double ClampDifficulty(double difficulty) => Math.Min(Math.Max(difficulty, MinDifficulty), MaxDifficulty);

    private static double InitialStability(Rating rating) => ClampStability(W((int)rating - 1));

    private static double InitialDifficulty(Rating rating) => W(4) - Math.Pow(Math.E, W(5) * ((int)rating - 1)) + 1;

    /// <summary>Whole days by the stability, at least one and at most the maximum.</summary>
    private static TimeSpan Days(double stability)
    {
        var interval = stability / Factor * (Math.Pow(DesiredRetention, 1 / Decay) - 1);
        var days = Math.Min(Math.Max(Math.Round(interval), 1), MaximumInterval);
        return TimeSpan.FromDays(days);
    }

    private static double ShortTermStability(double stability, Rating rating)
    {
        var increase = Math.Pow(Math.E, W(17) * ((int)rating - 3 + W(18))) * Math.Pow(stability, -W(19));
        if (rating != Rating.Again)
        {
            increase = Math.Max(increase, 1.0);
        }

        return ClampStability(stability * increase);
    }

    private static double NextDifficulty(double difficulty, Rating rating)
    {
        var delta = -(W(6) * ((int)rating - 3));
        var damped = difficulty + ((10.0 - difficulty) * delta / 9.0);
        var reverted = (W(7) * InitialDifficulty(Rating.Easy)) + ((1 - W(7)) * damped);
        return ClampDifficulty(reverted);
    }

    private static double NextStability(double difficulty, double stability, double retrievability, Rating rating) =>
        ClampStability(rating == Rating.Again
            ? NextForgetStability(difficulty, stability, retrievability)
            : NextRecallStability(difficulty, stability, retrievability, rating));

    private static double NextForgetStability(double difficulty, double stability, double retrievability)
    {
        var longTerm = W(11)
            * Math.Pow(difficulty, -W(12))
            * (Math.Pow(stability + 1, W(13)) - 1)
            * Math.Pow(Math.E, (1 - retrievability) * W(14));
        var shortTerm = stability / Math.Pow(Math.E, W(17) * W(18));
        return Math.Min(longTerm, shortTerm);
    }

    private static double NextRecallStability(double difficulty, double stability, double retrievability, Rating rating)
    {
        var hardPenalty = rating == Rating.Hard ? W(15) : 1;
        var easyBonus = rating == Rating.Easy ? W(16) : 1;
        return stability * (1
            + (Math.Pow(Math.E, W(8))
                * (11 - difficulty)
                * Math.Pow(stability, -W(9))
                * (Math.Pow(Math.E, (1 - retrievability) * W(10)) - 1)
                * hardPenalty
                * easyBonus));
    }
}