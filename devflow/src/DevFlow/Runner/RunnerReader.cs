using DevFlow.CodeHost;

namespace DevFlow.Runner;

/// <summary>
/// Reads one pull request by this repository's runner's conventions, <c>scripts/agent/</c>: whether
/// it is the owner's turn, why, and what the agent and the reviewer want him to know. One pure
/// function, with no database and no network, that never throws for what it reads: a field it
/// cannot understand becomes text or is left out. It goes when the runner is retired. See "Reading
/// this repository's runner" in devflow/docs/devflow-architecture.md.
/// </summary>
public static class RunnerReader
{
    /// <summary>The bot's verdict and the runner's labels, as <c>scripts/agent/run.sh</c> sets them.</summary>
    private const string OwnerMerge = "owner-merge";
    private const string OwnerDecision = "owner-decision";
    private const string OwnerBlocked = "owner-blocked";

    /// <summary>CI's one required check run, and the app that must have made it, as the merge
    /// gate reads it.</summary>
    private const string CiCheck = "ci-ok";
    private const string CiApp = "github-actions";

    /// <summary>The status the runner posts on a head the reviewer approved.</summary>
    private const string ReviewStatus = "review-ok";

    /// <summary>
    /// The pull request as an item when it is the owner's turn, or null when it is not. Only an
    /// open pull request by the bot that is not a draft is considered. <c>owner-blocked</c> makes
    /// it stopped; <c>owner-merge</c> or <c>owner-decision</c> make it waiting, but only while the
    /// latest verdict approves its head commit, since the runner corrects its labels once a turn
    /// and an owner label can sit for a moment on a head nobody approved.
    /// </summary>
    public static RunnerItem? Read(RunnerInput input)
    {
        if (input?.PullRequest is not { } pull || input.Bot is not { } bot)
        {
            return null;
        }

        if (pull.State != PullRequestState.Open || pull.Draft || !string.Equals(pull.Author, bot, StringComparison.Ordinal))
        {
            return null;
        }

        var comments = NotNull(input.Comments);
        var labels = NotNull(pull.Labels);
        var head = pull.HeadCommit ?? "";
        var verdict = Verdicts.Latest(comments, bot);

        RunnerItemKind kind;
        if (labels.Contains(OwnerBlocked, StringComparer.Ordinal))
        {
            kind = RunnerItemKind.Stopped;
        }
        else if ((labels.Contains(OwnerMerge, StringComparer.Ordinal) || labels.Contains(OwnerDecision, StringComparer.Ordinal))
            && verdict is { Kind: VerdictKind.Approve } approved
            && string.Equals(approved.Sha, head, StringComparison.Ordinal))
        {
            kind = RunnerItemKind.Waiting;
        }
        else
        {
            return null;
        }

        var body = pull.Body ?? "";
        var decisions = PullRequestBody.Decisions(body);
        return new RunnerItem(
            pull.Number,
            pull.Title ?? "",
            pull.Url,
            Slice(body),
            kind,
            SentBack(comments, verdict, input.Owner),
            head,
            kind == RunnerItemKind.Waiting && ChecksGreen(NotNull(input.CheckRuns), NotNull(input.Statuses)),
            PullRequestBody.Summary(body),
            RunnerReason.From(pull.Number, NotNull(input.SliceComments), comments, bot),
            [.. decisions.Where(decision => decision.IsOwners).Select(decision => decision.Text)],
            decisions.Count(decision => !decision.IsOwners),
            verdict is null ? [] : PullRequestBody.ReviewerView(verdict.Comment.Body));
    }

    /// <summary>The slice a pull request body closes, from the <c>Closes #&lt;n&gt;</c> line of its
    /// summary, or null. The program reads the slice's comments by it.</summary>
    public static int? Slice(string body) => PullRequestBody.Slice(body);

    /// <summary>Whether the owner has commented since the latest verdict, or at all when there is
    /// none, so the runner has his comment to answer first. Never without his login.</summary>
    private static bool SentBack(IReadOnlyList<Comment> comments, Verdict? verdict, string? owner) =>
        owner is not null
        && comments.Any(comment =>
            string.Equals(comment.Author, owner, StringComparison.Ordinal)
            && (verdict is null || comment.CreatedAt > verdict.Comment.CreatedAt));

    /// <summary>
    /// Both of the merge gate's checks on the head: <c>ci-ok</c>, made by GitHub Actions, every
    /// run of it completed with success as the gate requires, and the <c>review-ok</c> status a
    /// success. A check run of the same name by another app counts for nothing.
    /// </summary>
    private static bool ChecksGreen(IReadOnlyList<CheckRun> runs, IReadOnlyList<CommitStatus> statuses)
    {
        var ci = runs.Where(run =>
            string.Equals(run.Name, CiCheck, StringComparison.Ordinal) && string.Equals(run.App, CiApp, StringComparison.Ordinal)).ToList();
        var review = statuses.Where(status => string.Equals(status.Context, ReviewStatus, StringComparison.Ordinal)).ToList();
        return ci.Count > 0
            && ci.All(run => run is { Status: CheckStatus.Completed, Conclusion: CheckConclusion.Success })
            && review.Count > 0
            && review.All(status => status.State == StatusState.Success);
    }

    /// <summary>The list without its null entries, or empty when it is null itself.</summary>
    private static IReadOnlyList<T> NotNull<T>(IReadOnlyList<T?>? list)
        where T : class => list is null ? [] : [.. list.OfType<T>()];
}