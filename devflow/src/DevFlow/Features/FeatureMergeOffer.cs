namespace DevFlow.Features;

/// <summary>"Merge into &lt;default&gt;", as a feature's page and its item on "Waiting for you"
/// offer it.</summary>
/// <param name="Into">The default branch, as its watch names it, which the button is named
/// with.</param>
/// <param name="HeadCommit">The feature branch's head, which CI passed on, and which the merge names
/// to the code host, so a branch that moved since is never merged unseen.</param>
public sealed record FeatureMergeOffer(string Into, string HeadCommit);

/// <summary>What the confirmation of a feature's merge says, as
/// <c>GET /devflow/api/projects/{id}/features/{number}/merge</c> answers it. Every text is the code
/// host's or the reviewer's and shown as text.</summary>
/// <param name="Number">The parent's number.</param>
/// <param name="Title">The parent's title as last read.</param>
/// <param name="Into">The default branch it merges into.</param>
/// <param name="PullRequest">The feature's pull request.</param>
/// <param name="HeadCommit">The head CI passed on, which the merge names.</param>
/// <param name="Review"><c>approved</c> when the newest review approved a commit that covers the
/// head; <c>changes</c> when the newest review asked for changes; <c>not_reviewed</c>
/// otherwise.</param>
/// <param name="OpenFindings">With <c>changes</c>, the blocking findings of that review, in its
/// order; empty otherwise.</param>
/// <param name="Unanswered">How many decisions its cards' latest reports marked for the owner have
/// no answer, as its page counts them, each of which merging confirms.</param>
public sealed record FeatureMergeConfirmation(
    int Number,
    string Title,
    string Into,
    Cards.CardPullRequest PullRequest,
    string HeadCommit,
    string Review,
    IReadOnlyList<Protocol.ReviewFinding> OpenFindings,
    int Unanswered)
{
    /// <summary>The confirmation of <paramref name="story"/>'s merge into <paramref name="into"/> at
    /// <paramref name="head"/>, from its reviews and its decisions as its page reads them.</summary>
    public static FeatureMergeConfirmation Of(FeatureStoryView story, string into, int pullRequest, string pullRequestUrl, string head)
    {
        ArgumentNullException.ThrowIfNull(story);
        var newest = story.Reviews.Count > 0 ? story.Reviews[0] : null;
        var review = newest switch
        {
            { Current: true, Verdict: "approve" } => "approved",
            { Verdict: "changes" } => "changes",
            _ => "not_reviewed",
        };
        return new FeatureMergeConfirmation(
            story.Number,
            story.Title,
            into,
            new Cards.CardPullRequest(pullRequest, pullRequestUrl),
            head,
            review,
            review == "changes" ? [.. newest!.Findings.Where(finding => finding.Blocking)] : [],
            story.Cards.Sum(card => card.Decisions.Count(decision => decision.Answer is null)));
    }
}

/// <summary>
/// When a feature is offered for merging into the default branch, decided from what Dev flow stored
/// alone, so the page, the waiting list and the route agree and nothing asks the code host for it:
/// the feature is <c>ready</c> or <c>waiting_for_owner</c> with its pull request recorded, its
/// branch's watch names a head, CI passed on that head as the minute last read it, and the project
/// is not stopped. The review's verdict is not among them: the confirmation says what it is. See
/// "Merging a feature" in devflow/README.md.
/// </summary>
public static class FeatureMergeOffers
{
    /// <summary>The states a feature is merged from, the owner's turn.</summary>
    public static readonly IReadOnlyList<string> States = ["ready", "waiting_for_owner"];

    /// <summary>
    /// The default branch a feature <c>f</c> is offered for merging into, as SQL, or null when it is
    /// not offered; its head is then its branch watch's <c>last_commit</c>.
    /// </summary>
    public const string IntoSql = """
        (SELECT d.branch FROM branch_watches d
         JOIN branch_watches h ON h.project_id = d.project_id AND h.feature_id = f.id
         WHERE d.project_id = f.project_id AND d.feature_id IS NULL
           AND f.state IN ('ready', 'waiting_for_owner') AND f.pull_request_number IS NOT NULL
           AND h.last_commit IS NOT NULL AND f.ci_passed_commit = h.last_commit
           AND NOT EXISTS (SELECT 1 FROM project_stops s WHERE s.project_id = f.project_id))
        """;

    /// <summary>The feature branch's head as its watch last named it, as SQL over a feature
    /// <c>f</c>; null before it has named one.</summary>
    public const string HeadSql = "(SELECT h.last_commit FROM branch_watches h WHERE h.project_id = f.project_id AND h.feature_id = f.id)";

    /// <summary>The offer, from the two read by <see cref="IntoSql"/> and <see cref="HeadSql"/>.</summary>
    public static FeatureMergeOffer? Of(string? into, string? head) =>
        into is not null && head is not null ? new FeatureMergeOffer(into, head) : null;
}