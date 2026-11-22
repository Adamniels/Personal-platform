using System.Security.Cryptography;
using System.Text;

using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Projects;
using DevFlow.Watching;

namespace DevFlow.Merging;

/// <summary>What was picked of a card, which its record holds: the body's SHA-256, the title, and
/// the id of the edit history's newest entry, null when it was never edited.</summary>
public sealed record CardRecord(byte[] BodySha256, string Title, string? NewestEdit);

/// <summary>The commit the card's newest build or fix that succeeded left, as Dev flow recorded
/// it: the commit, how the worker's check ended on it, when the worker pushed it, which is when its
/// result came back, and the branch it ran on, the default branch or its feature's, which the pull
/// request must go into.</summary>
public sealed record CheckedHead(string Commit, int? CheckExitCode, DateTimeOffset PushedAt, string Base);

/// <summary>The card's latest review in Dev flow's own <c>reviews</c>: <c>approve</c> or
/// <c>changes</c>, and the commit it is for.</summary>
public sealed record LatestReview(string Verdict, string Commit);

/// <summary>
/// What the merge check reads of a card's feature, for a card of one: the parent's number, whether
/// the feature's branch was not read within five minutes, the record of what was picked of the
/// parent, null for a parent never picked, the repository a card must be in, and as the app the
/// parent's sub-issues and its edits, each as it answered.
/// </summary>
/// <param name="Number">The parent's issue number.</param>
/// <param name="BranchUnwatched">Whether the feature's branch was not read within five minutes.</param>
/// <param name="Record">What was picked of the parent.</param>
/// <param name="Owner">The owner of the project's repository, which its cards are in.</param>
/// <param name="Name">The name of the project's repository.</param>
/// <param name="SubIssues">The parent's sub-issues.</param>
/// <param name="Edits">The parent's edit history.</param>
/// <param name="LabelEvents">The parent's label events, from its timeline.</param>
public sealed record FeatureFacts(
    int Number,
    bool BranchUnwatched,
    CardRecord? Record,
    string Owner,
    string Name,
    CodeHostResult<IReadOnlyList<SubIssue>> SubIssues,
    CodeHostResult<IReadOnlyList<IssueEdit>> Edits,
    CodeHostResult<IReadOnlyList<LabelEvent>> LabelEvents);

/// <summary>
/// What the merge check reads of "sometimes", the project's settings as they are now: which of the
/// three conditions are ticked, the review label, and the paths the owner listed, in his order.
/// </summary>
/// <param name="WhenMarked">Whether a card marked with the review label, or of a feature whose
/// parent is, waits.</param>
/// <param name="WhenPaths">Whether a change touching a path on <paramref name="Paths"/> waits.</param>
/// <param name="WhenDecision">Whether a card whose agent marked a decision for the owner
/// waits.</param>
/// <param name="ReviewLabel">The label that marks a card for his review.</param>
/// <param name="Paths">The patterns he listed, read by <see cref="PathPatterns"/>.</param>
public sealed record WaitConditions(bool WhenMarked, bool WhenPaths, bool WhenDecision, string ReviewLabel, IReadOnlyList<string> Paths)
{
    /// <summary>A new project's: all three ticked, the review label <c>devflow-review</c>, no
    /// paths.</summary>
    public static WaitConditions Defaults { get; } = new(true, true, true, "devflow-review", []);
}

/// <summary>
/// Everything the merge check decides on, read fresh by its caller: Dev flow's own records of the
/// project and the card, and the code host's answers as they came, a failure among them, so what
/// could not be read is decided here too.
/// </summary>
/// <param name="Now">The time the reads were made.</param>
/// <param name="Hold">Whether the project is stopped, or its default branch was not read within
/// five minutes.</param>
/// <param name="WaitsForOwner">The project's setting, <c>never</c>, <c>sometimes</c> or
/// <c>always</c>.</param>
/// <param name="Trusted">The project's trusted accounts, with the owner's linked login, none of
/// them trusted without a link.</param>
/// <param name="Card">The card's issue number.</param>
/// <param name="Record">What was picked of the card.</param>
/// <param name="Head">What the card's runs left, null when none succeeded.</param>
/// <param name="Review">The card's latest review, null when it has none.</param>
/// <param name="PullRequest">The card's pull request.</param>
/// <param name="Statuses">The statuses on the pull request's head commit.</param>
/// <param name="CheckRuns">The check runs on it.</param>
/// <param name="Labelled">The repository's open issues carrying the pick-up label.</param>
/// <param name="Edits">The card's edit history.</param>
/// <param name="LabelEvents">The card's label events, from its timeline.</param>
/// <param name="Conditions">What "sometimes" waits for, read only on <c>sometimes</c>.</param>
/// <param name="DecisionForOwner">Whether any build's or fix's report of the card that succeeded
/// marked a decision for the owner.</param>
/// <param name="Files">The files its pull request changes, read as the app only when a ticked
/// condition needs them, a read that was not made otherwise.</param>
/// <param name="Feature">The card's feature, null for a card on its own.</param>
/// <param name="Reading">What the card's newest reading read of it, its body's SHA-256 and its
/// title, with no edit; null for a card with no reading.</param>
public sealed record MergeFacts(
    DateTimeOffset Now,
    ProjectHold Hold,
    string WaitsForOwner,
    string PickupLabel,
    string HoldLabel,
    TrustedAccounts Trusted,
    int Card,
    CardRecord Record,
    CheckedHead? Head,
    LatestReview? Review,
    CodeHostResult<PullRequest> PullRequest,
    CodeHostResult<IReadOnlyList<CommitStatus>> Statuses,
    CodeHostResult<IReadOnlyList<CheckRun>> CheckRuns,
    CodeHostResult<IReadOnlyList<Issue>> Labelled,
    CodeHostResult<IReadOnlyList<IssueEdit>> Edits,
    CodeHostResult<IReadOnlyList<LabelEvent>> LabelEvents,
    WaitConditions Conditions,
    bool DecisionForOwner,
    CodeHostResult<IReadOnlyList<ChangedFile>> Files,
    FeatureFacts? Feature = null,
    CardRecord? Reading = null);

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

    /// <summary>Why a card whose body or title is no longer what its newest reading read is not
    /// merged: it was built from that reading.</summary>
    public const string ChangedSinceRead = "The card changed since it was read, and it was built from what was read";
    public const string Always = "This project waits for you on every pull request";
    public const string Marked = "This card is marked for your review";
    public const string TooManyFiles = "Too many files to check the paths";
    public const string Decision = "The agent made a decision for you";
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

    public static string WrittenBy(string login) => $"The card was written by {login}, who is not a trusted account of this project";

    public static string Held(string label) => $"The card is held by the label {label}";

    public static string EditedBy(string login) => $"The card was edited by {login} since it was picked";

    public static string PickupNotTrusted(string label, string? by) => by is null
        ? $"The card's pick-up label {label} was not added by a trusted account of this project"
        : $"The card's pick-up label {label} was added by {by}, who is not a trusted account of this project";

    public static string HoldTakenOff(string label, string? by) => by is null
        ? $"The card is held by the label {label}, which a trusted account put on and none took off"
        : $"The card is held by the label {label}, taken off by {by}, who is not a trusted account of this project";

    public static string CiFailed(string name) => $"CI failed: {name}";

    public static string IntoAnother(string into, string built) => $"Its pull request goes into {into}, not {built}";

    public static string NotSubIssue(int feature) => Invariant($"The card is no longer an open sub-issue of its feature, #{feature}");

    public static string FeatureNotLabelled(int feature, string label) => Invariant($"Its feature, #{feature}, is no longer open with the label {label}");

    public static string FeatureWrittenBy(int feature, string login) => Invariant($"Its feature, #{feature}, was written by {login}, who is not a trusted account of this project");

    public static string FeatureHeld(int feature, string label) => Invariant($"Its feature, #{feature}, is held by the label {label}");

    public static string FeatureEditedBy(int feature, string login) => Invariant($"Its feature, #{feature}, was edited by {login} since it was picked");

    public static string FeaturePickupNotTrusted(int feature, string label, string? by) => by is null
        ? Invariant($"The pick-up label {label} on its feature, #{feature}, was not added by a trusted account of this project")
        : Invariant($"The pick-up label {label} on its feature, #{feature}, was added by {by}, who is not a trusted account of this project");

    public static string FeatureHoldTakenOff(int feature, string label, string? by) => by is null
        ? Invariant($"Its feature, #{feature}, is held by the label {label}, which a trusted account put on and none took off")
        : Invariant($"Its feature, #{feature}, is held by the label {label}, taken off by {by}, who is not a trusted account of this project");

    public static string FeatureMarked(int feature) => Invariant($"Its feature, #{feature}, is marked for your review");

    public static string PathListed(string path, string pattern) => $"It changes {path}, which matches {pattern} on this project's list of paths";

    public static string FeatureChangedUnseen(int feature) => Invariant($"Its feature, #{feature}, changed since it was picked, and its history does not say by whom");

    /// <summary>
    /// Merge, or why not, the first that holds deciding, in this order: the project is stopped, or
    /// its default branch unread, or for a card of a feature its feature's branch unread; the pull
    /// request unread or not open; its head not the commit the worker checked, the check failed on
    /// it, or the pull request going into another branch than the one it ran on; the latest review not
    /// an approve of exactly that commit; the card not still valid, written by a trusted account,
    /// open with the pick-up label a trusted account put on, not held, by the hold label on it or
    /// one a trusted account put on and only someone untrusted took off, and edited by nobody
    /// outside the list since it was picked, where for a card of a feature the parent is held to
    /// all of it and the card is an open sub-issue of it, held to all of it but the pick-up label,
    /// and the card as it is now still what its newest reading read, when it has one; the setting, <c>never</c> going on, <c>sometimes</c> going on unless a ticked condition
    /// holds, see <see cref="Sometimes"/>, and anything else waiting; and CI, every check run and
    /// status on the commit completed and succeeded. Only a wait that can clear by itself is tried
    /// again: a branch unread, a read that failed, CI running or not reported yet. Everything else
    /// is the owner's.
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

        if (facts.Feature is { BranchUnwatched: true })
        {
            return Again(FeatureUnwatched);
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

        // Git's branch names are case sensitive, so is this: a pull request whose base was changed
        // since would merge where the card was never built to land, a feature's card into the
        // default branch among it.
        if (!string.Equals(pull.BaseBranch, checkedHead.Base, StringComparison.Ordinal))
        {
            return Final(IntoAnother(pull.BaseBranch, checkedHead.Base));
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

        // Anything but never or sometimes, a value it does not know included, waits for the owner.
        switch (facts.WaitsForOwner)
        {
            case "never":
                break;
            case "sometimes":
                if (Sometimes(facts) is { } wait)
                {
                    return wait;
                }

                break;
            default:
                return Final(Always);
        }

        return (MergeDecision?)Ci(facts.CheckRuns, facts.Statuses, facts.Now, checkedHead.PushedAt) ?? new MergeDecision.Merge(head);
    }

    /// <summary>Why the card is no longer one a run merges from, or null when it still is: a card on
    /// its own as listed with the pick-up label, a card of a feature as its parent lists it, with
    /// the parent held to the same rules first.</summary>
    private static MergeDecision.Wait? Card(MergeFacts facts)
    {
        if (facts.Labelled is not CodeHostResult<IReadOnlyList<Issue>>.Found { Value: var labelled }
            || facts.Edits is not CodeHostResult<IReadOnlyList<IssueEdit>>.Found { Value: var edits }
            || facts.LabelEvents is not CodeHostResult<IReadOnlyList<LabelEvent>>.Found { Value: var events }
            || facts.Feature is { } unread && (unread.SubIssues is not CodeHostResult<IReadOnlyList<SubIssue>>.Found
                || unread.Edits is not CodeHostResult<IReadOnlyList<IssueEdit>>.Found
                || unread.LabelEvents is not CodeHostResult<IReadOnlyList<LabelEvent>>.Found))
        {
            return Again(CouldNotRead("the card"));
        }

        if (facts.Trusted.Mine is null)
        {
            return Final(CardPicker.NotLinked);
        }

        var trusted = facts.Trusted;

        if (facts.Feature is not { } feature)
        {
            return labelled.FirstOrDefault(issue => !issue.IsPullRequest && issue.Number == facts.Card) is { } card
                ? Valid(new Read(card.Author, card.Labels, card.Body, card.Title), edits, events, facts.Record, trusted, facts.PickupLabel, facts.HoldLabel, CardWords)
                    ?? SinceRead(card.Body, card.Title, facts.Reading)
                : Final(NotLabelled(facts.PickupLabel));
        }

        if (labelled.FirstOrDefault(issue => !issue.IsPullRequest && issue.Number == feature.Number) is not { } parent)
        {
            return Final(FeatureNotLabelled(feature.Number, facts.PickupLabel));
        }

        // Read above: all three answered.
        var parentEdits = ((CodeHostResult<IReadOnlyList<IssueEdit>>.Found)feature.Edits).Value;
        var parentEvents = ((CodeHostResult<IReadOnlyList<LabelEvent>>.Found)feature.LabelEvents).Value;
        if (feature.Record is not { } parentRecord)
        {
            return Final(CardPicker.FeatureNotPicked(feature.Number));
        }

        var words = FeatureWords(feature.Number);
        if (Valid(new Read(parent.Author, parent.Labels, parent.Body, parent.Title), parentEdits, parentEvents, parentRecord, trusted, facts.PickupLabel, facts.HoldLabel, words) is { } invalid)
        {
            return invalid;
        }

        if (OwnSubIssue(feature, facts.Card) is not { } own)
        {
            return Final(NotSubIssue(feature.Number));
        }

        // A card of a feature carries no pick-up label of its own: its parent's switched it on.
        return Valid(new Read(own.Author, own.Labels, own.Body, own.Title), edits, events, facts.Record, trusted, pickupLabel: null, facts.HoldLabel, CardWords)
            ?? SinceRead(own.Body, own.Title, facts.Reading);
    }

    /// <summary>Why a card is no longer what its newest reading read, or null when it still is or
    /// has no reading: its body, as GitHub has it now, by its SHA-256, or its title differs from
    /// the reading's record. That reading is what it was built from, so even a trusted account's edit
    /// since, which the pick's record allows, is the owner's to judge.</summary>
    private static MergeDecision.Wait? SinceRead(string body, string title, CardRecord? reading) =>
        reading is null
        || SHA256.HashData(Encoding.UTF8.GetBytes(body)).AsSpan().SequenceEqual(reading.BodySha256) && string.Equals(title, reading.Title, StringComparison.Ordinal)
            ? null
            : Final(ChangedSinceRead);

    /// <summary>
    /// Why "sometimes" waits for the owner, or null when nothing it ticked holds, the first that
    /// holds deciding: the review label counts on the card, or for a card of a feature on its parent
    /// or on the card, read by <see cref="LabelActors.Judge"/> as one that holds back, so anyone may
    /// put it on and only a trusted account take it off; a file the pull request changes, by its new
    /// or its old name, matches a listed path, where files at GitHub's limit cannot be known to
    /// match none and wait too, and files not read are tried again; and a decision marked for the
    /// owner in any report of the card. Called once the card was found valid, so what it looks up
    /// was read and is there.
    /// </summary>
    private static MergeDecision.Wait? Sometimes(MergeFacts facts)
    {
        var conditions = facts.Conditions;
        if (conditions.WhenMarked && MarkedFor(facts) is { } marked)
        {
            return marked;
        }

        if (conditions.WhenPaths && conditions.Paths.Count > 0)
        {
            if (facts.Files is not CodeHostResult<IReadOnlyList<ChangedFile>>.Found { Value: var files })
            {
                return Again(CouldNotRead("the files of its pull request"));
            }

            if (files.Count >= ICodeHost.MostPullRequestFiles)
            {
                return Final(TooManyFiles);
            }

            if (PathPatterns.FirstMatch(conditions.Paths, files) is { } match)
            {
                return Final(PathListed(match.Path, match.Pattern));
            }
        }

        return conditions.WhenDecision && facts.DecisionForOwner ? Final(Decision) : null;
    }

    /// <summary>Why the review label marks the card, or null when it does not: on a card of a
    /// feature its parent's first, then the card's own.</summary>
    private static MergeDecision.Wait? MarkedFor(MergeFacts facts)
    {
        var label = facts.Conditions.ReviewLabel;
        var labelled = ((CodeHostResult<IReadOnlyList<Issue>>.Found)facts.Labelled).Value;
        var events = ((CodeHostResult<IReadOnlyList<LabelEvent>>.Found)facts.LabelEvents).Value;
        bool Counts(IReadOnlyList<string> labels, IReadOnlyList<LabelEvent> changes) =>
            LabelActors.Judge(label, LabelKind.HoldsBack, labels, changes, facts.Trusted).Counts;

        if (facts.Feature is not { } feature)
        {
            var card = labelled.First(issue => !issue.IsPullRequest && issue.Number == facts.Card);
            return Counts(card.Labels, events) ? Final(Marked) : null;
        }

        var parent = labelled.First(issue => !issue.IsPullRequest && issue.Number == feature.Number);
        if (Counts(parent.Labels, ((CodeHostResult<IReadOnlyList<LabelEvent>>.Found)feature.LabelEvents).Value))
        {
            return Final(FeatureMarked(feature.Number));
        }

        var own = OwnSubIssue(feature, facts.Card)!;
        return Counts(own.Labels, events) ? Final(Marked) : null;
    }

    /// <summary>The card as its feature lists it, an open sub-issue in the project's repository, or
    /// null when it is not one.</summary>
    private static SubIssue? OwnSubIssue(FeatureFacts feature, int card) =>
        ((CodeHostResult<IReadOnlyList<SubIssue>>.Found)feature.SubIssues).Value.FirstOrDefault(sub => !sub.IsPullRequest && sub.Open && sub.Number == card
            && string.Equals(sub.Owner, feature.Owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sub.Name, feature.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>An issue as the card rules read it.</summary>
    private sealed record Read(string Author, IReadOnlyList<string> Labels, string Body, string Title);

    /// <summary>The reasons the card rules give, of the card or of its feature.</summary>
    private sealed record Words(
        Func<string, string> WrittenBy,
        Func<string, string?, string> PickupNotTrusted,
        Func<string, string> Held,
        Func<string, string?, string> HoldTakenOff,
        Func<string, string> EditedBy,
        string ChangedUnseen);

    private static readonly Words CardWords = new(WrittenBy, PickupNotTrusted, Held, HoldTakenOff, EditedBy, ChangedUnseen);

    private static Words FeatureWords(int feature) =>
        new(
            login => FeatureWrittenBy(feature, login),
            (label, by) => FeaturePickupNotTrusted(feature, label, by),
            label => FeatureHeld(feature, label),
            (label, by) => FeatureHoldTakenOff(feature, label, by),
            login => FeatureEditedBy(feature, login),
            FeatureChangedUnseen(feature));

    /// <summary>Why the issue is no longer one a run merges from, or null when it still is: written
    /// by a trusted account, its pick-up label, when <paramref name="pickupLabel"/> is given, put on
    /// by one, not held, by a hold label on it or one a trusted account put on and only someone
    /// untrusted took off, and edited by nobody outside the list since it was picked, with any
    /// change to it explained by an edit of one of them. Labels are read by
    /// <see cref="LabelActors"/>, as picking reads them.</summary>
    private static MergeDecision.Wait? Valid(Read issue, IReadOnlyList<IssueEdit> edits, IReadOnlyList<LabelEvent> events, CardRecord record, TrustedAccounts trusted, string? pickupLabel, string holdLabel, Words words)
    {
        if (!trusted.Trusts(issue.Author))
        {
            return Final(words.WrittenBy(issue.Author));
        }

        if (pickupLabel is not null && LabelActors.Judge(pickupLabel, LabelKind.SwitchesOn, issue.Labels, events, trusted) is { Counts: false, By: var putOnBy })
        {
            return Final(words.PickupNotTrusted(pickupLabel, putOnBy));
        }

        if (issue.Labels.Contains(holdLabel, StringComparer.OrdinalIgnoreCase))
        {
            return Final(words.Held(holdLabel));
        }

        if (LabelActors.Judge(holdLabel, LabelKind.HoldsBack, issue.Labels, events, trusted) is { Counts: true, By: var takenOffBy })
        {
            return Final(words.HoldTakenOff(holdLabel, takenOffBy));
        }

        if (edits.Where(edit => !trusted.Trusts(edit.Editor)).OrderBy(edit => edit.At).FirstOrDefault() is { } stranger)
        {
            return Final(words.EditedBy(stranger.Editor));
        }

        // Every edit is a trusted account's, so a change since the record is one of theirs, which
        // is allowed; one the history does not explain is not.
        var newest = edits.OrderByDescending(edit => edit.At).Select(edit => edit.Id).FirstOrDefault();
        var same = SHA256.HashData(Encoding.UTF8.GetBytes(issue.Body)).AsSpan().SequenceEqual(record.BodySha256)
            && string.Equals(issue.Title, record.Title, StringComparison.Ordinal);
        return !same && string.Equals(newest, record.NewestEdit, StringComparison.Ordinal) ? Final(words.ChangedUnseen) : null;
    }

    /// <summary>Why CI holds the commit, or null when every check run and status on it completed
    /// and succeeded, as the code host answered them at <paramref name="now"/>. A failure decides
    /// first, by name, then anything still running; nothing at all waits until
    /// <see cref="CiReportsWithin"/> after <paramref name="pushedAt"/>, when the commit was pushed,
    /// and is then "CI did not report". Anything that could not be read waits too. The one rule a
    /// card's merge and a whole feature's readiness read CI by, see "Reviewing a feature" in
    /// devflow/README.md.</summary>
    public static MergeDecision.Wait? Ci(CodeHostResult<IReadOnlyList<CheckRun>> checkRuns, CodeHostResult<IReadOnlyList<CommitStatus>> statuses, DateTimeOffset now, DateTimeOffset pushedAt)
    {
        ArgumentNullException.ThrowIfNull(checkRuns);
        ArgumentNullException.ThrowIfNull(statuses);
        if (checkRuns is not CodeHostResult<IReadOnlyList<CheckRun>>.Found { Value: var runs }
            || statuses is not CodeHostResult<IReadOnlyList<CommitStatus>>.Found { Value: var listed })
        {
            return Again(CouldNotRead("CI on its commit"));
        }

        var failed = runs
            .Where(run => run.Status == CheckStatus.Completed && run.Conclusion != CheckConclusion.Success)
            .Select(run => run.Name)
            .Concat(listed.Where(status => status.State is StatusState.Failure or StatusState.Error).Select(status => status.Context))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (failed is not null)
        {
            return Final(CiFailed(failed));
        }

        if (runs.Any(run => run.Status != CheckStatus.Completed) || listed.Any(status => status.State == StatusState.Pending))
        {
            return Again(CiRunning);
        }

        if (runs.Count == 0 && listed.Count == 0)
        {
            return now - pushedAt >= CiReportsWithin ? Final(CiSilent) : Again(CiNotYet);
        }

        return null;
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private static MergeDecision.Wait Final(string reason) => new(reason, TryAgain: false);

    private static MergeDecision.Wait Again(string reason) => new(reason, TryAgain: true);
}