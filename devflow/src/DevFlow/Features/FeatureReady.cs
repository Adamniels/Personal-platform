using DevFlow.CodeHost;
using DevFlow.Merging;

namespace DevFlow.Features;

/// <summary>A whole feature's review as its readiness reads it: the commit it is for and its
/// verdict, <c>approve</c> or <c>changes</c>.</summary>
public sealed record ReviewOfFeature(string Commit, string Verdict);

/// <summary>
/// What a feature's readiness decides on, read from what Dev flow stored by its caller: the commit
/// the feature's branch is at, as its watch last named it after Dev flow's own last change there;
/// the commit its review last failed at in the job; its latest review; and the merges of the
/// default branch Dev flow made cleanly on its branch, each by its commit with its first parent.
/// </summary>
/// <param name="Head">The feature branch's head.</param>
/// <param name="FailedAt">The commit its review last failed at in the job, null otherwise.</param>
/// <param name="Latest">Its newest review, null before any.</param>
/// <param name="CleanMerges">Each merge commit Dev flow made bringing the default branch in, and the
/// feature branch's commit it merged into, from <c>merges_made</c>; a conflict's resolution is
/// never among them, since an agent made it.</param>
public sealed record ReadyFacts(string Head, string? FailedAt, ReviewOfFeature? Latest, IReadOnlyDictionary<string, string> CleanMerges);

/// <summary>CI on the feature's head, as the code host answered it at <paramref name="Now"/>, and
/// when the head was pushed.</summary>
public sealed record ReadyCi(CodeHostResult<IReadOnlyList<CheckRun>> CheckRuns, CodeHostResult<IReadOnlyList<CommitStatus>> Statuses, DateTimeOffset Now, DateTimeOffset PushedAt);

/// <summary>Where a feature moves: its state, its reason, and the commit to review when it is to be
/// reviewed.</summary>
public sealed record ReadyDecision(string State, string? Reason, string? ReviewCommit);

/// <summary>
/// Whether a feature whose pull request is open needs a review of the whole feature, and whether it
/// is ready for the owner, decided by one pure function over what was read, with no database,
/// network or clock in it, as the merge check's is. A review covers the head when it is for it, or
/// when the head differs from it only by merges of the default branch Dev flow made cleanly, so a
/// clean bringing in needs CI again and no new review, and a conflict an agent resolved, which no
/// such merge is, needs both. See "Reviewing a feature" in devflow/README.md and
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class FeatureReady
{
    /// <summary>The latest review when it covers the head, or null: it is for the head, or the
    /// head's clean merges, followed back by their first parents, reach the commit it is for.</summary>
    public static ReviewOfFeature? Covering(ReadyFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Latest is not { } review)
        {
            return null;
        }

        var at = facts.Head;
        // Each merge is followed once at most, so a record that loops ends the walk.
        for (var steps = 0; steps <= facts.CleanMerges.Count; steps++)
        {
            if (string.Equals(at, review.Commit, StringComparison.Ordinal))
            {
                return review;
            }

            if (!facts.CleanMerges.TryGetValue(at, out var parent))
            {
                return null;
            }

            at = parent;
        }

        return null;
    }

    /// <summary>
    /// Where the feature moves, or null when it stays as it is, in this order: a head no review
    /// covers is to be reviewed, unless its review failed there in the job, when it waits for the
    /// owner as that failure left it; a head the covering review asked changes on waits for the owner,
    /// until "Fixing a feature" lands; and a head it approved is ready when CI passed there, read as
    /// the merge check reads it by <see cref="MergeCheck.Ci"/>, and otherwise waits with CI's reason,
    /// <paramref name="ci"/> null being CI that could not be read.
    /// </summary>
    public static ReadyDecision? Decide(ReadyFacts facts, ReadyCi? ci)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (Covering(facts) is not { } review)
        {
            return string.Equals(facts.Head, facts.FailedAt, StringComparison.Ordinal)
                ? null
                : new ReadyDecision("reviewing", FeatureReview.ToReview, facts.Head);
        }

        if (review.Verdict != "approve")
        {
            return new ReadyDecision("waiting_for_owner", FeatureReview.ChangesAsked, null);
        }

        if (ci is null)
        {
            return new ReadyDecision("waiting_for_owner", MergeCheck.CouldNotRead("CI on its commit"), null);
        }

        return MergeCheck.Ci(ci.CheckRuns, ci.Statuses, ci.Now, ci.PushedAt) is { } wait
            ? new ReadyDecision("waiting_for_owner", wait.Reason, null)
            : new ReadyDecision("ready", null, null);
    }
}