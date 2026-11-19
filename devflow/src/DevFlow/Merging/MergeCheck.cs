using System.Security.Cryptography;
using System.Text;

using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Watching;

namespace DevFlow.Merging;

/// <summary>What was picked of a card, which its record holds: the body's SHA-256, the title, and
/// the id of the edit history's newest entry, null when it was never edited.</summary>
public sealed record CardRecord(byte[] BodySha256, string Title, string? NewestEdit);

/// <summary>The commit the card's newest build or fix that succeeded left, as Dev flow recorded
/// it: the commit, how the worker's check ended on it, and when the worker pushed it, which is when
/// its result came back.</summary>
public sealed record CheckedHead(string Commit, int? CheckExitCode, DateTimeOffset PushedAt);

/// <summary>The card's latest review in Dev flow's own <c>reviews</c>: <c>approve</c> or
/// <c>changes</c>, and the commit it is for.</summary>
public sealed record LatestReview(string Verdict, string Commit);

/// <summary>
/// Everything the merge check decides on, read fresh by its caller: Dev flow's own records of the
/// project and the card, and the code host's answers as they came, a failure among them, so what
/// could not be read is decided here too.
/// </summary>
/// <param name="Now">The time the reads were made.</param>
/// <param name="Hold">Whether the project is stopped, or its default branch was not read within
/// five minutes.</param>
/// <param name="WaitsForOwner">The project's setting, <c>never</c> or <c>always</c>.</param>
/// <param name="OwnerLogin">The owner's linked login, null without a link.</param>
/// <param name="Card">The card's issue number.</param>
/// <param name="Record">What was picked of the card.</param>
/// <param name="Head">What the card's runs left, null when none succeeded.</param>
/// <param name="Review">The card's latest review, null when it has none.</param>
/// <param name="PullRequest">The card's pull request.</param>
/// <param name="Statuses">The statuses on the pull request's head commit.</param>
/// <param name="CheckRuns">The check runs on it.</param>
/// <param name="Labelled">The repository's open issues carrying the pick-up label.</param>
/// <param name="Edits">The card's edit history.</param>
public sealed record MergeFacts(
    DateTimeOffset Now,
    ProjectHold Hold,
    string WaitsForOwner,
    string PickupLabel,
    string HoldLabel,
    string? OwnerLogin,
    int Card,
    CardRecord Record,
    CheckedHead? Head,
    LatestReview? Review,
    CodeHostResult<PullRequest> PullRequest,
    CodeHostResult<IReadOnlyList<CommitStatus>> Statuses,
    CodeHostResult<IReadOnlyList<CheckRun>> CheckRuns,
    CodeHostResult<IReadOnlyList<Issue>> Labelled,
    CodeHostResult<IReadOnlyList<IssueEdit>> Edits);

/// <summary>What the merge check answers. A closed set.</summary>
public abstract record MergeDecision
{
    private MergeDecision()
    {
    }

    /// <summary>Everything holds: merge the pull request while its head is
    /// <paramref name="Commit"/>.</summary>
    public sealed record Merge(string Commit) : MergeDecision;

    /// <summary>No merge. <paramref name="Reason"/> says why, in words for the owner;
    /// <paramref name="TryAgain"/> is whether it may clear by itself, so the check reads the card
    /// again on the next minute, rather than leaving it to him.</summary>
    public sealed record Wait(string Reason, bool TryAgain) : MergeDecision;
}

/// <summary>
/// The merge check, the successor of this repository's merge gate: one pure function over what was
/// read, with no database, no network, no clock and no language model in it, that answers merge
/// only when every condition holds, and otherwise why not. Anything that failed or could not be
/// read is no merge: it never guesses in favour of merging. Its caller,
/// <see cref="MergeChecker"/>, reads, calls it and merges. See "The merge check" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public static class MergeCheck
{
    /// <summary>A card just approved, before the check has read it.</summary>
    public const string Checking = "Approved; Dev flow checks whether it may merge it";

    public const string Stopped = "Its project is stopped: nothing merges until you resume it";
    public const string Unwatched = "Its project's default branch has not been read lately; Dev flow tries again each minute";
    public const string FeatureUnwatched = "Its feature's branch has not been read lately; Dev flow tries again each minute";
    public const string NotOpen = "Its pull request is no longer open";
    public const string NotChecked = "Its head commit is not one the worker checked";
    public const string CheckFailed = "The worker's check did not pass on its head commit";
    public const string NotApproved = "The reviewer has not approved its head commit";
    public const string ChangesAsked = "The reviewer asked for changes on its head commit";
    public const string ChangedUnseen = "The card changed since it was picked, and its history does not say by whom";
    public const string Always = "This project waits for you on every pull request";
    public const string CiRunning = "CI is still running on its commit";
    public const string CiNotYet = "Waiting for CI to report on its commit";
    public const string CiSilent = "CI did not report";

    /// <summary>What the merge answered, when it did not merge.</summary>
    public const string HeadMoved = "Its pull request's head moved as it was merged; Dev flow reads it again";
    public const string NotMergeable = "GitHub would not merge it as it is";
    public const string MergeRefused = "GitHub refused to merge it";
    public const string MergeUnanswered = "GitHub did not answer the merge; Dev flow tries again each minute";
    public const string NoKey = "Dev flow could not get a key to merge with; it tries again each minute";

    /// <summary>How long after its commit was pushed CI may say nothing before the card is the
    /// owner's: CI that never reports does not count as passing.</summary>
    public static readonly TimeSpan CiReportsWithin = TimeSpan.FromMinutes(30);

    public static string CouldNotRead(string what) => $"Dev flow could not read {what}; it tries again each minute";

    public static string NotLabelled(string label) => $"The card is no longer open with the label {label}";

    public static string WrittenBy(string login) => $"The card was written by {login}, not by you";

    public static string Held(string label) => $"The card is held by the label {label}";

    public static string EditedBy(string login) => $"The card was edited by {login} since it was picked";

    public static string CiFailed(string name) => $"CI failed: {name}";

    /// <summary>
    /// Merge, or why not, the first that holds deciding, in this order: the project is stopped, or
    /// its default branch unread; the pull request unread or not open; its head not the commit the
    /// worker checked, or the check failed on it; the latest review not an approve of exactly that
    /// commit; the card not still valid, written by the owner, open with the pick-up label, not
    /// held, and edited by nobody else since it was picked; the setting not <c>never</c>; and CI,
    /// every check run and status on the commit completed and succeeded. Only a wait that can clear
    /// by itself is tried again: the branch unread, a read that failed, CI running or not reported
    /// yet. Everything else is the owner's.
    /// </summary>
    public static MergeDecision Decide(MergeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        switch (facts.Hold)
        {
            case ProjectHold.Stopped:
                return Final(Stopped);
            case ProjectHold.Unwatched:
                return Again(Unwatched);
        }

        if (facts.PullRequest is not CodeHostResult<PullRequest>.Found { Value: var pull })
        {
            return Again(CouldNotRead("its pull request"));
        }

        if (pull.State != PullRequestState.Open)
        {
            return Final(NotOpen);
        }

        var head = pull.HeadCommit;
        if (facts.Head is not { } checkedHead || !string.Equals(checkedHead.Commit, head, StringComparison.Ordinal))
        {
            return Final(NotChecked);
        }

        if (checkedHead.CheckExitCode != 0)
        {
            return Final(CheckFailed);
        }

        if (facts.Review is not { } review || !string.Equals(review.Commit, head, StringComparison.Ordinal))
        {
            return Final(NotApproved);
        }

        if (!string.Equals(review.Verdict, "approve", StringComparison.Ordinal))
        {
            return Final(ChangesAsked);
        }

        if (Card(facts) is { } invalid)
        {
            return invalid;
        }

        // Anything but never, a value it does not know included, waits for the owner.
        if (!string.Equals(facts.WaitsForOwner, "never", StringComparison.Ordinal))
        {
            return Final(Always);
        }

        return (MergeDecision?)Ci(facts, checkedHead) ?? new MergeDecision.Merge(head);
    }

    /// <summary>Why the card is no longer one a run merges from, or null when it still is.</summary>
    private static MergeDecision.Wait? Card(MergeFacts facts)
    {
        if (facts.Labelled is not CodeHostResult<IReadOnlyList<Issue>>.Found { Value: var labelled }
            || facts.Edits is not CodeHostResult<IReadOnlyList<IssueEdit>>.Found { Value: var edits })
        {
            return Again(CouldNotRead("the card"));
        }

        if (facts.OwnerLogin is not { } owner)
        {
            return Final(CardPicker.NotLinked);
        }

        if (labelled.FirstOrDefault(issue => !issue.IsPullRequest && issue.Number == facts.Card) is not { } card)
        {
            return Final(NotLabelled(facts.PickupLabel));
        }

        if (!IsOwner(card.Author, owner))
        {
            return Final(WrittenBy(card.Author));
        }

        if (card.Labels.Contains(facts.HoldLabel, StringComparer.OrdinalIgnoreCase))
        {
            return Final(Held(facts.HoldLabel));
        }

        if (edits.Where(edit => !IsOwner(edit.Editor, owner)).OrderBy(edit => edit.At).FirstOrDefault() is { } stranger)
        {
            return Final(EditedBy(stranger.Editor));
        }

        // Every edit is the owner's, so a change since the record is his, which is allowed; one the
        // history does not explain is not.
        var newest = edits.OrderByDescending(edit => edit.At).Select(edit => edit.Id).FirstOrDefault();
        var same = SHA256.HashData(Encoding.UTF8.GetBytes(card.Body)).AsSpan().SequenceEqual(facts.Record.BodySha256)
            && string.Equals(card.Title, facts.Record.Title, StringComparison.Ordinal);
        return !same && string.Equals(newest, facts.Record.NewestEdit, StringComparison.Ordinal) ? Final(ChangedUnseen) : null;
    }

    /// <summary>Why CI holds the merge, or null when every check run and status on the commit
    /// completed and succeeded. A failure decides first, by name, then anything still running;
    /// nothing at all waits until <see cref="CiReportsWithin"/> after the push.</summary>
    private static MergeDecision.Wait? Ci(MergeFacts facts, CheckedHead head)
    {
        if (facts.CheckRuns is not CodeHostResult<IReadOnlyList<CheckRun>>.Found { Value: var runs }
            || facts.Statuses is not CodeHostResult<IReadOnlyList<CommitStatus>>.Found { Value: var statuses })
        {
            return Again(CouldNotRead("CI on its commit"));
        }

        var failed = runs
            .Where(run => run.Status == CheckStatus.Completed && run.Conclusion != CheckConclusion.Success)
            .Select(run => run.Name)
            .Concat(statuses.Where(status => status.State is StatusState.Failure or StatusState.Error).Select(status => status.Context))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (failed is not null)
        {
            return Final(CiFailed(failed));
        }

        if (runs.Any(run => run.Status != CheckStatus.Completed) || statuses.Any(status => status.State == StatusState.Pending))
        {
            return Again(CiRunning);
        }

        if (runs.Count == 0 && statuses.Count == 0)
        {
            return facts.Now - head.PushedAt >= CiReportsWithin ? Final(CiSilent) : Again(CiNotYet);
        }

        return null;
    }

    /// <summary>Logins are one account whatever their case, as GitHub's are.</summary>
    private static bool IsOwner(string login, string owner) => string.Equals(login, owner, StringComparison.OrdinalIgnoreCase);

    private static MergeDecision.Wait Final(string reason) => new(reason, TryAgain: false);

    private static MergeDecision.Wait Again(string reason) => new(reason, TryAgain: true);
}