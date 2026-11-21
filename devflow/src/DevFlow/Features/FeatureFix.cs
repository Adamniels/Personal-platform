using System.Globalization;

using DevFlow.Fixing;

namespace DevFlow.Features;

/// <summary>
/// What a feature says while its whole review's fix rounds run: a <c>changes</c> verdict on the
/// feature starts a round, at most <see cref="FixRounds.Most"/> of them, and the owner's comment on
/// its pull request starts a fix that is not one. A feature is never stopped by its review: with its
/// rounds used up, or its fix failing twice in a row, it waits for the owner. See "Fixing a feature"
/// in devflow/README.md, and in devflow/docs/devflow-architecture.md for why.
/// </summary>
public static class FeatureFix
{
    /// <summary>Why a feature whose review asked for changes after its last round waits for the
    /// owner.</summary>
    public const string UsedUp = "Review rounds used up";

    /// <summary>What a feature says when the owner's comment on its pull request sent it back.</summary>
    public const string Commented = "You commented on its pull request; a fix runs next";

    /// <summary>What a feature says once its fix succeeded, until the minute queues its new
    /// review.</summary>
    public const string Fixed = "Fixed; the reviewer reads the whole feature again next";

    /// <summary>What a feature says when its review asked for changes and round
    /// <paramref name="round"/> is next.</summary>
    public static string Asked(int round) =>
        string.Create(CultureInfo.InvariantCulture, $"Round {round} of {FixRounds.Most}: the review of the whole feature asked for changes; a fix runs next");

    /// <summary>What a feature says while its fix waits to be handed out: round
    /// <paramref name="round"/>'s line, or <see cref="Commented"/> for the owner's comment.</summary>
    public static string Waiting(int? round) => round is { } r ? Asked(r) : Commented;

    /// <summary>What a feature says while its fix runs: round <paramref name="round"/>, or null for
    /// the owner's comment.</summary>
    public static string Fixing(int? round) => round is { } r
        ? string.Create(CultureInfo.InvariantCulture, $"Round {r} of {FixRounds.Most}: the developer is fixing the feature")
        : "The developer is answering your comment";

    /// <summary>What a feature says when one failure in its fix runs it again.</summary>
    public static string Again(string reason) => $"Failed once in the job, so it runs again: {reason}";

    /// <summary>Why a feature waits for the owner when its fix failed: <paramref name="reason"/> is
    /// its run's.</summary>
    public static string Failed(string reason) => $"Dev flow could not fix the whole feature: {reason}";
}