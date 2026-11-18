using System.Globalization;

namespace DevFlow.Fixing;

/// <summary>
/// The rounds a card is fixed in and what a card says while it is: a <c>changes</c> verdict starts a
/// round, at most <see cref="Most"/> of them, and the owner's comment on the card's open pull request
/// starts a fix that is not one. See "Fixing" in devflow/README.md, and in
/// devflow/docs/devflow-architecture.md for why.
/// </summary>
public static class FixRounds
{
    /// <summary>The most fix rounds reviews start for one card.</summary>
    public const int Most = 3;

    /// <summary>Why a card whose review asked for changes after its last round is stopped.</summary>
    public const string UsedUp = "Fix rounds used up";

    /// <summary>What a card says when the owner's comment on its pull request sent it back.</summary>
    public const string Commented = "You commented on its pull request; a fix runs next";

    /// <summary>Why a fix run that left the branch where it was failed, in the card.</summary>
    public const string NoNewCommit = "The fix left no new commit on the branch";

    /// <summary>What a card says when its review asked for changes and round
    /// <paramref name="round"/> is next.</summary>
    public static string Asked(int round) =>
        string.Create(CultureInfo.InvariantCulture, $"Round {round} of {Most}: the reviewer asked for changes; a fix runs next");

    /// <summary>What a card says while its fix waits to be handed out: round
    /// <paramref name="round"/>'s line, or <see cref="Commented"/> for the owner's comment.</summary>
    public static string Waiting(int? round) => round is { } r ? Asked(r) : Commented;

    /// <summary>What a card says while its fix runs: round <paramref name="round"/>, or null for
    /// the owner's comment.</summary>
    public static string Fixing(int? round) => round is { } r
        ? string.Create(CultureInfo.InvariantCulture, $"Round {r} of {Most}: the developer is fixing it")
        : "The developer is answering your comment";
}