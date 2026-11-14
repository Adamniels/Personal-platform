namespace Review.Scheduling;

/// <summary>
/// Where a card is in FSRS's model, py-fsrs's <c>State</c>, with <see cref="New"/> for a card never
/// graded. Stored in <c>cards.state</c> by its name in lowercase, see <see cref="CardState.Name"/>.
/// </summary>
public enum State
{
    /// <summary>Never graded: py-fsrs's fresh <c>Card()</c>, Learning at step 0 with no stability or
    /// difficulty. The column keeps it until the first grade, so the pages can say a card is new.</summary>
    New,

    /// <summary>In its learning steps, minutes apart.</summary>
    Learning,

    /// <summary>Scheduled in whole days by its stability.</summary>
    Review,

    /// <summary>Forgotten in review, back in a relearning step.</summary>
    Relearning,
}

/// <summary>
/// A card's schedule, py-fsrs 6.3.3's <c>Card</c> without its id: what <see cref="Fsrs.Grade"/>
/// reads and writes, and what the columns of <c>cards</c> by the same names hold.
/// </summary>
/// <param name="Step">The learning or relearning step, from 0; null in Review, and for a new card,
/// which is at step 0.</param>
/// <param name="Stability">Days until recall falls to 90%; null until the first grade.</param>
/// <param name="Difficulty">1 to 10; null until the first grade.</param>
/// <param name="Due">When it is next due.</param>
/// <param name="LastReview">When it was last graded, or null.</param>
public sealed record CardState(
    State State,
    int? Step,
    double? Stability,
    double? Difficulty,
    DateTimeOffset Due,
    DateTimeOffset? LastReview)
{
    /// <summary>A card never graded, due at <paramref name="due"/>.</summary>
    public static CardState New(DateTimeOffset due) => new(State.New, null, null, null, due, null);

    /// <summary>The state as <c>cards.state</c> and <c>grades</c> hold it: <c>new</c>,
    /// <c>learning</c>, <c>review</c> or <c>relearning</c>.</summary>
    public string Name => StateName(State);

    public static string StateName(State state) => state switch
    {
        State.New => "new",
        State.Learning => "learning",
        State.Review => "review",
        State.Relearning => "relearning",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "no such state"),
    };

    /// <summary>The state a column holds, by its name.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A name that is no state.</exception>
    public static State ParseState(string name) => name switch
    {
        "new" => State.New,
        "learning" => State.Learning,
        "review" => State.Review,
        "relearning" => State.Relearning,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such state"),
    };
}

/// <summary>A grade, py-fsrs's <c>Rating</c>, by its number on the wire and in <c>grades</c>.</summary>
public enum Rating
{
    Again = 1,
    Hard = 2,
    Good = 3,
    Easy = 4,
}

/// <summary>When each grade, given now, would bring the card back.</summary>
public sealed record Previews(DateTimeOffset Again, DateTimeOffset Hard, DateTimeOffset Good, DateTimeOffset Easy);