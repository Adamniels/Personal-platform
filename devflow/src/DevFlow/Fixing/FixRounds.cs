using System.Globalization;

namespace DevFlow.Fixing;

/// <summary>
/// The rounds a card is fixed in and what a card says while it is: a <c>changes</c> verdict starts a
/// round, at most the project's <c>fix_rounds_card</c> of them, read when the verdict is judged, and
/// the owner's comment on the card's open pull request starts a fix that is not one. See "Fixing" in devflow/README.md, and in
/// devflow/docs/devflow-architecture.md for why.
/// </summary>
public static class FixRounds
{
    /// <summary>Why a card whose review asked for changes after its last round is stopped.</summary>
    public const string UsedUp = "Fix rounds used up";

    /// <summary>What a card says when the owner's comment on its pull request sent it back.</summary>
    public const string Commented = "You commented on its pull request; a fix runs next";

    /// <summary>Why a fix run that left the branch where it was failed, in the card.</summary>
    public const string NoNewCommit = "The fix left no new commit on the branch";

    /// <summary>What a card says when its review asked for changes and round
    /// <paramref name="round"/> of <paramref name="most"/> is next.</summary>
    public static string Asked(int round, int most) =>
        string.Create(CultureInfo.InvariantCulture, $"Round {round} of {most}: the reviewer asked for changes; a fix runs next");

    /// <summary>What a card says while its fix waits to be handed out: round
    /// <paramref name="round"/>'s line, or <see cref="Commented"/> for the owner's comment.</summary>
    public static string Waiting(int? round, int most) => round is { } r ? Asked(r, most) : Commented;

    /// <summary>What a card says while its fix runs: round <paramref name="round"/> of
    /// <paramref name="most"/>, or a line of its own for the owner's comment.</summary>
    public static string Fixing(int? round, int most) => round is { } r
        ? string.Create(CultureInfo.InvariantCulture, $"Round {r} of {most}: the developer is fixing it")
        : "The developer is answering your comment";
}