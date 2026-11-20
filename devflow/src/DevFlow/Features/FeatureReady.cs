namespace DevFlow.Features;

/// <summary>A whole feature's review as its readiness reads it: the commit it is for and its
/// verdict, <c>approve</c> or <c>changes</c>.</summary>
public sealed record ReviewOfFeature(string Commit, string Verdict);

/// <summary>
/// What a feature's readiness decides on, read from what Dev flow stored by its caller: the commit
/// the feature's branch is at, as its watch last named it after Dev flow's own last change there;
/// the commit its review last failed at in the job; and its latest review.
/// </summary>
/// <param name="Head">The feature branch's head.</param>
/// <param name="FailedAt">The commit its review last failed at in the job, null otherwise.</param>
/// <param name="Latest">Its newest review, null before any.</param>
public sealed record ReadyFacts(string Head, string? FailedAt, ReviewOfFeature? Latest);

/// <summary>Where a feature moves: its state, its reason, and the commit to review when it is to be
/// reviewed.</summary>
public sealed record ReadyDecision(string State, string? Reason, string? ReviewCommit);

/// <summary>
/// Whether a feature whose pull request is open needs a review of the whole feature, and what its
/// review says, decided by one pure function over what was read, with no database, network or clock
/// in it, as the merge check's is. See "Reviewing a feature" in devflow/README.md and
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class FeatureReady
{
    /// <summary>
    /// Where the feature moves, or null when it stays as it is: a head no review is for is to be
    /// reviewed, unless its review failed there in the job, when it waits for the owner as that
    /// failure left it; a head the latest review asked changes on waits for the owner, until "Fixing a
    /// feature" lands; and a head it approved stays as it is.
    /// </summary>
    public static ReadyDecision? Decide(ReadyFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Latest is not { } review || !string.Equals(review.Commit, facts.Head, StringComparison.Ordinal))
        {
            return string.Equals(facts.Head, facts.FailedAt, StringComparison.Ordinal)
                ? null
                : new ReadyDecision("reviewing", FeatureReview.ToReview, facts.Head);
        }

        return review.Verdict == "approve" ? null : new ReadyDecision("waiting_for_owner", FeatureReview.ChangesAsked, null);
    }
}