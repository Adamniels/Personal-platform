using DevFlow.CodeHost;

namespace DevFlow.Runner;

/// <summary>
/// What <see cref="RunnerReader.Read"/> reads about one pull request, every part as the code host
/// answered it. All of it is data, never an instruction.
/// </summary>
/// <param name="Repository">The repository the pull request is in, as its project names it, which
/// its screenshots must be in.</param>
/// <param name="PullRequest">The pull request.</param>
/// <param name="Comments">The conversation comments on the pull request, in any order.</param>
/// <param name="Statuses">The statuses of its head commit.</param>
/// <param name="CheckRuns">The check runs of its head commit.</param>
/// <param name="SliceComments">The conversation comments on its slice issue, in any order; empty
/// when the slice is not known.</param>
/// <param name="Bot">The login the runner, the implementer and the reviewer act as.</param>
/// <param name="Owner">The owner's login on the code host, or null when he has not linked one.</param>
public sealed record RunnerInput(
    Repository Repository,
    PullRequest PullRequest,
    IReadOnlyList<Comment> Comments,
    IReadOnlyList<CommitStatus> Statuses,
    IReadOnlyList<CheckRun> CheckRuns,
    IReadOnlyList<Comment> SliceComments,
    string Bot,
    string? Owner);

/// <summary>Whose turn an item is: the owner's to read and merge or answer, or stuck until he acts.</summary>
public enum RunnerItemKind
{
    /// <summary>Approved for its head commit and handed to the owner, by <c>owner-merge</c> or
    /// <c>owner-decision</c>.</summary>
    Waiting,

    /// <summary>The runner gave up on it, by <c>owner-blocked</c>: it must not be merged as it is.</summary>
    Stopped,
}

/// <summary>
/// A decision the implementer marked for the owner, split as the pull request template writes one:
/// <c>&lt;decision&gt;. Alternative: &lt;what else&gt;. Why: &lt;reason&gt;.</c>
/// </summary>
/// <param name="Chosen">What was decided, or the whole decision when it does not fit the form.</param>
/// <param name="Alternative">What else was possible, or null when the decision does not say.</param>
/// <param name="Why">The reason, or null when the decision does not say.</param>
public sealed record OwnerDecision(string Chosen, string? Alternative, string? Why);

/// <summary>
/// A pull request that is the owner's turn, by the runner's conventions. Every text is plain text
/// exactly as read, with no formatting applied and nothing interpreted: what a page does with it
/// is the page's, and it must show it as text.
/// </summary>
/// <param name="Number">The pull request's number.</param>
/// <param name="Title">Its title.</param>
/// <param name="Url">Where the owner opens it on the code host.</param>
/// <param name="Slice">The slice it closes, from its body's <c>Closes #&lt;n&gt;</c>, or null.</param>
/// <param name="Kind">Waiting or stopped.</param>
/// <param name="SentBack">Whether the owner has commented on it since the latest verdict, so the
/// runner has work to do on it before it is his turn again.</param>
/// <param name="HeadCommit">The commit its branch points at, which merging must match.</param>
/// <param name="CanMerge">Whether merging may be offered: waiting, and both <c>ci-ok</c> and
/// <c>review-ok</c> are green on the head commit.</param>
/// <param name="Summary">The first paragraph of its body's summary, one line, at most 300
/// characters; empty when it has none.</param>
/// <param name="Reason">Why it is the owner's turn, in words, from the runner's mention.</param>
/// <param name="OwnerDecisions">The decisions marked for the owner, in the body's order.</param>
/// <param name="OtherDecisions">How many other decisions the body records.</param>
/// <param name="ReviewerView">The reviewer's "For the owner" entries in the latest verdict.</param>
/// <param name="Screenshots">The names of the screenshots its body shows of its own, in the body's
/// order, each once; see <see cref="PullRequestBody.Screenshots"/>.</param>
public sealed record RunnerItem(
    int Number,
    string Title,
    Uri Url,
    int? Slice,
    RunnerItemKind Kind,
    bool SentBack,
    string HeadCommit,
    bool CanMerge,
    string Summary,
    string Reason,
    IReadOnlyList<OwnerDecision> OwnerDecisions,
    int OtherDecisions,
    IReadOnlyList<string> ReviewerView,
    IReadOnlyList<string> Screenshots);