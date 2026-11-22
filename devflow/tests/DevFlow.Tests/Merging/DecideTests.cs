using System.Security.Cryptography;
using System.Text;

using DevFlow.Cards;
using DevFlow.CodeHost;
using DevFlow.Merging;
using DevFlow.Projects;
using DevFlow.Watching;

namespace DevFlow.Tests.Merging;

/// <summary>
/// <see cref="MergeCheck.Decide"/> alone: from facts where everything holds, each condition
/// changed by itself, and the reason it answers, and whether that is tried again. See "The merge
/// check" in devflow/README.md.
/// </summary>
public sealed class DecideTests
{
    private const string Head = "1111111111111111111111111111111111111111";
    private const string Older = "2222222222222222222222222222222222222222";
    private const string Body = "Build the thing of card 1.";
    private static readonly DateTimeOffset Pushed = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    private static CodeHostResult<T> Found<T>(T value) => new CodeHostResult<T>.Found(value);

    private static CodeHostResult<T> Down<T>() => new CodeHostResult<T>.Unavailable(RetryAt: null);

    private static readonly Issue CardIssue = new(1, "Card 1", "octo-owner", ["devflow"], Body,
        new Uri("https://github.com/Adamniels/Personal-platform/issues/1"), Pushed.AddDays(-1), IsPullRequest: false);

    private static readonly PullRequest Pull = new(2, "Card 1", "devflow-fake[bot]", [], Head, "devflow/1-card-1", "main",
        Draft: false, "", new Uri("https://github.com/Adamniels/Personal-platform/pull/2"), Pushed, PullRequestState.Open);

    /// <summary>Facts where every condition holds, five minutes after the push.</summary>
    private static readonly MergeFacts Good = new(
        Pushed.AddMinutes(5),
        ProjectHold.None,
        "never",
        "devflow",
        "devflow-hold",
        TrustedAccounts.Of("octo-owner", ["octo-friend"]),
        1,
        new CardRecord(SHA256.HashData(Encoding.UTF8.GetBytes(Body)), "Card 1", null),
        new CheckedHead(Head, 0, Pushed, "main"),
        new LatestReview("approve", Head),
        Found(Pull),
        Found<IReadOnlyList<CommitStatus>>([new CommitStatus("deploy/preview", StatusState.Success, null)]),
        Found<IReadOnlyList<CheckRun>>([new CheckRun("test", CheckStatus.Completed, CheckConclusion.Success, "github-actions")]),
        Found<IReadOnlyList<Issue>>([CardIssue]),
        Found<IReadOnlyList<IssueEdit>>([]),
        Found<IReadOnlyList<LabelEvent>>([new LabelEvent("devflow", LabelChange.Added, "octo-owner", Pushed.AddDays(-1))]),
        WaitConditions.Defaults,
        DecisionForOwner: false,
        Down<IReadOnlyList<ChangedFile>>());

    [Fact]
    public void EverythingHoldsAndItMergesTheHead() =>
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(Good));

    [Fact]
    public void TheOwnersOwnEditsAreFine()
    {
        var edited = Good with
        {
            Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Body = "Build it, and the empty state.", Title = "Card one" }]),
            Edits = Found<IReadOnlyList<IssueEdit>>([new IssueEdit("UCE_1", "Octo-Owner", Pushed, EditKind.Body), new IssueEdit("UCE_2", "octo-owner", Pushed.AddMinutes(1), EditKind.Title)]),
        };

        Assert.IsType<MergeDecision.Merge>(MergeCheck.Decide(edited));
    }

    [Fact]
    public void CiWithNothingButStatusesOrNothingButRunsMerges()
    {
        Assert.IsType<MergeDecision.Merge>(MergeCheck.Decide(Good with { Statuses = Found<IReadOnlyList<CommitStatus>>([]) }));
        Assert.IsType<MergeDecision.Merge>(MergeCheck.Decide(Good with { CheckRuns = Found<IReadOnlyList<CheckRun>>([]) }));
    }

    /// <summary>Each condition changed alone, the reason, and whether it is tried again.</summary>
    public static TheoryData<string, string, bool> Cases() => new()
    {
        { "stopped", MergeCheck.Stopped, false },
        { "unwatched", MergeCheck.Unwatched, true },
        { "pull request unread", MergeCheck.CouldNotRead("its pull request"), true },
        { "pull request not found", MergeCheck.CouldNotRead("its pull request"), true },
        { "pull request merged", MergeCheck.NotOpen, false },
        { "pull request closed", MergeCheck.NotOpen, false },
        { "head moved", MergeCheck.NotChecked, false },
        { "no run succeeded", MergeCheck.NotChecked, false },
        { "check failed", MergeCheck.CheckFailed, false },
        { "check did not run", MergeCheck.CheckFailed, false },
        { "goes into another branch", MergeCheck.IntoAnother("release", "main"), false },
        { "base differs only in case", MergeCheck.IntoAnother("Main", "main"), false },
        { "no review", MergeCheck.NotApproved, false },
        { "review of an older commit", MergeCheck.NotApproved, false },
        { "review asked for changes", MergeCheck.ChangesAsked, false },
        { "issues unread", MergeCheck.CouldNotRead("the card"), true },
        { "edits unread", MergeCheck.CouldNotRead("the card"), true },
        { "label events unread", MergeCheck.CouldNotRead("the card"), true },
        { "not linked", CardPicker.NotLinked, false },
        { "closed or unlabelled", MergeCheck.NotLabelled("devflow"), false },
        { "listed only as a pull request", MergeCheck.NotLabelled("devflow"), false },
        { "written by someone else", MergeCheck.WrittenBy("mallory"), false },
        { "held", MergeCheck.Held("devflow-hold"), false },
        { "edited by someone else", MergeCheck.EditedBy("mallory"), false },
        { "edited by a deleted account", MergeCheck.EditedBy("ghost"), false },
        { "changed with no history", MergeCheck.ChangedUnseen, false },
        { "always", MergeCheck.Always, false },
        { "a setting it does not know", MergeCheck.Always, false },
        { "statuses unread", MergeCheck.CouldNotRead("CI on its commit"), true },
        { "check runs unread", MergeCheck.CouldNotRead("CI on its commit"), true },
        { "check run failed", MergeCheck.CiFailed("lint"), false },
        { "check run neutral", MergeCheck.CiFailed("lint"), false },
        { "check run skipped", MergeCheck.CiFailed("lint"), false },
        { "status failed", MergeCheck.CiFailed("coverage"), false },
        { "status errored", MergeCheck.CiFailed("coverage"), false },
        { "a failure beside one running", MergeCheck.CiFailed("lint"), false },
        { "check run queued", MergeCheck.CiRunning, true },
        { "check run in progress", MergeCheck.CiRunning, true },
        { "status pending", MergeCheck.CiRunning, true },
        { "no CI yet", MergeCheck.CiNotYet, true },
        { "no CI for 30 minutes", MergeCheck.CiSilent, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachConditionAloneRefuses(string condition, string reason, bool tryAgain) =>
        Assert.Equal(new MergeDecision.Wait(reason, tryAgain), MergeCheck.Decide(Break(condition)));

    private static CheckRun Run(CheckStatus status, CheckConclusion? conclusion, string name = "lint") => new(name, status, conclusion, "github-actions");

    private static MergeFacts Break(string condition) => condition switch
    {
        "stopped" => Good with { Hold = ProjectHold.Stopped },
        "unwatched" => Good with { Hold = ProjectHold.Unwatched },
        "pull request unread" => Good with { PullRequest = Down<PullRequest>() },
        "pull request not found" => Good with { PullRequest = new CodeHostResult<PullRequest>.NotFound() },
        "pull request merged" => Good with { PullRequest = Found(Pull with { State = PullRequestState.Merged }) },
        "pull request closed" => Good with { PullRequest = Found(Pull with { State = PullRequestState.Closed }) },
        "head moved" => Good with { PullRequest = Found(Pull with { HeadCommit = Older }) },
        "no run succeeded" => Good with { Head = null },
        "check failed" => Good with { Head = new CheckedHead(Head, 1, Pushed, "main") },
        "check did not run" => Good with { Head = new CheckedHead(Head, null, Pushed, "main") },
        "goes into another branch" => Good with { PullRequest = Found(Pull with { BaseBranch = "release" }) },
        "base differs only in case" => Good with { PullRequest = Found(Pull with { BaseBranch = "Main" }) },
        "no review" => Good with { Review = null },
        "review of an older commit" => Good with { Review = new LatestReview("approve", Older) },
        "review asked for changes" => Good with { Review = new LatestReview("changes", Head) },
        "issues unread" => Good with { Labelled = Down<IReadOnlyList<Issue>>() },
        "edits unread" => Good with { Edits = new CodeHostResult<IReadOnlyList<IssueEdit>>.NotFound() },
        "label events unread" => Good with { LabelEvents = Down<IReadOnlyList<LabelEvent>>() },
        "not linked" => Good with { Trusted = TrustedAccounts.Of(null, ["octo-owner", "octo-friend"]) },
        "closed or unlabelled" => Good with { Labelled = Found<IReadOnlyList<Issue>>([]) },
        "listed only as a pull request" => Good with { Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { IsPullRequest = true }]) },
        "written by someone else" => Good with { Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Author = "mallory" }]) },
        "held" => Good with { Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Labels = ["devflow", "DevFlow-Hold"] }]) },
        "edited by someone else" => Good with { Edits = Found<IReadOnlyList<IssueEdit>>([new IssueEdit("UCE_1", "octo-owner", Pushed, EditKind.Body), new IssueEdit("UCE_2", "mallory", Pushed.AddMinutes(1), EditKind.Title)]) },
        "edited by a deleted account" => Good with { Edits = Found<IReadOnlyList<IssueEdit>>([new IssueEdit("UCE_1", "ghost", Pushed, EditKind.Body)]) },
        "changed with no history" => Good with { Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Body = "Something else." }]) },
        "always" => Good with { WaitsForOwner = "always" },
        "a setting it does not know" => Good with { WaitsForOwner = "Sometimes" },
        "statuses unread" => Good with { Statuses = Down<IReadOnlyList<CommitStatus>>() },
        "check runs unread" => Good with { CheckRuns = new CodeHostResult<IReadOnlyList<CheckRun>>.NotFound() },
        "check run failed" => Good with { CheckRuns = Found<IReadOnlyList<CheckRun>>([Run(CheckStatus.Completed, CheckConclusion.Success, "test"), Run(CheckStatus.Completed, CheckConclusion.Failure)]) },
        "check run neutral" => Good with { CheckRuns = Found<IReadOnlyList<CheckRun>>([Run(CheckStatus.Completed, CheckConclusion.Neutral)]) },
        "check run skipped" => Good with { CheckRuns = Found<IReadOnlyList<CheckRun>>([Run(CheckStatus.Completed, CheckConclusion.Skipped)]) },
        "status failed" => Good with { Statuses = Found<IReadOnlyList<CommitStatus>>([new CommitStatus("coverage", StatusState.Failure, null)]) },
        "status errored" => Good with { Statuses = Found<IReadOnlyList<CommitStatus>>([new CommitStatus("coverage", StatusState.Error, "It broke")]) },
        "a failure beside one running" => Good with { CheckRuns = Found<IReadOnlyList<CheckRun>>([Run(CheckStatus.InProgress, null, "test"), Run(CheckStatus.Completed, CheckConclusion.Cancelled)]) },
        "check run queued" => Good with { CheckRuns = Found<IReadOnlyList<CheckRun>>([Run(CheckStatus.Queued, null)]) },
        "check run in progress" => Good with { CheckRuns = Found<IReadOnlyList<CheckRun>>([Run(CheckStatus.InProgress, null)]) },
        "status pending" => Good with { Statuses = Found<IReadOnlyList<CommitStatus>>([new CommitStatus("deploy/preview", StatusState.Pending, null)]) },
        "no CI yet" => Good with
        {
            Now = Pushed.AddMinutes(29),
            Statuses = Found<IReadOnlyList<CommitStatus>>([]),
            CheckRuns = Found<IReadOnlyList<CheckRun>>([]),
        },
        "no CI for 30 minutes" => Good with
        {
            Now = Pushed.AddMinutes(30),
            Statuses = Found<IReadOnlyList<CommitStatus>>([]),
            CheckRuns = Found<IReadOnlyList<CheckRun>>([]),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "not a condition"),
    };

    private const string Branch = "feature-10-dev-flow-features";
    private const string ParentBody = "The whole of feature 10.";

    private static readonly Issue ParentIssue = new(10, "Dev flow: features", "octo-owner", ["devflow"], ParentBody,
        new Uri("https://github.com/Adamniels/Personal-platform/issues/10"), Pushed.AddDays(-2), IsPullRequest: false);

    private static readonly SubIssue CardSub = new(1, "Card 1", Body, Open: true, "octo-owner", [], Pushed.AddDays(-1), "Adamniels", "Personal-platform",
        new Uri("https://github.com/Adamniels/Personal-platform/issues/1"), IsPullRequest: false);

    /// <summary>Facts where every condition holds for card 1 of feature 10, whose parent alone
    /// carries the pick-up label, its pull request into the feature's branch.</summary>
    private static readonly MergeFacts GoodFeature = Good with
    {
        Head = new CheckedHead(Head, 0, Pushed, Branch),
        PullRequest = Found(Pull with { BaseBranch = Branch }),
        Labelled = Found<IReadOnlyList<Issue>>([ParentIssue]),
        Feature = new FeatureFacts(
            10,
            BranchUnwatched: false,
            new CardRecord(SHA256.HashData(Encoding.UTF8.GetBytes(ParentBody)), "Dev flow: features", null),
            "Adamniels",
            "Personal-platform",
            Found<IReadOnlyList<SubIssue>>([CardSub]),
            Found<IReadOnlyList<IssueEdit>>([]),
            Found<IReadOnlyList<LabelEvent>>([new LabelEvent("devflow", LabelChange.Added, "octo-owner", Pushed.AddDays(-2))])),
        // A card of a feature carries no pick-up label of its own.
        LabelEvents = Found<IReadOnlyList<LabelEvent>>([]),
    };

    private static CodeHostResult<IReadOnlyList<LabelEvent>> Events(params LabelEvent[] events) => Found<IReadOnlyList<LabelEvent>>(events);

    private static LabelEvent Added(string label, string actor, int minute) => new(label, LabelChange.Added, actor, Pushed.AddMinutes(minute));

    private static LabelEvent Removed(string label, string actor, int minute) => new(label, LabelChange.Removed, actor, Pushed.AddMinutes(minute));

    /// <summary>Condition 4 reads labels by who changed them, as picking does: the hold label a
    /// trusted account put on and only someone untrusted took off still holds the card, its
    /// feature's parent and a feature's card; taken off by a trusted account, it merges.</summary>
    [Fact]
    public void AHoldTakenOffByAStrangerIsNotMerged()
    {
        Assert.Equal(
            new MergeDecision.Wait("The card is held by the label devflow-hold, taken off by mallory, who is not a trusted account of this project", TryAgain: false),
            MergeCheck.Decide(Good with { LabelEvents = Events(Added("devflow", "octo-owner", 0), Added("devflow-hold", "octo-owner", 1), Removed("devflow-hold", "mallory", 2)) }));
        Assert.Equal(
            new MergeDecision.Wait("Its feature, #10, is held by the label devflow-hold, taken off by mallory, who is not a trusted account of this project", TryAgain: false),
            MergeCheck.Decide(GoodFeature with
            {
                Feature = GoodFeature.Feature! with { LabelEvents = Events(Added("devflow", "octo-owner", 0), Added("devflow-hold", "octo-friend", 1), Removed("devflow-hold", "mallory", 2)) },
            }));
        Assert.Equal(
            new MergeDecision.Wait("The card is held by the label devflow-hold, taken off by mallory, who is not a trusted account of this project", TryAgain: false),
            MergeCheck.Decide(GoodFeature with { LabelEvents = Events(Added("devflow-hold", "octo-owner", 1), Removed("devflow-hold", "mallory", 2)) }));

        // Taken off by me, or by an account I trust, it merges.
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(Good with
        {
            LabelEvents = Events(Added("devflow", "octo-owner", 0), Added("devflow-hold", "octo-owner", 1), Removed("devflow-hold", "mallory", 2), Removed("devflow-hold", "Octo-Friend", 3)),
        }));
    }

    /// <summary>A pick-up label no trusted account put on, on the card or its feature's parent, is
    /// not merged from, naming who put it on.</summary>
    [Fact]
    public void APickUpLabelByAStrangerIsNotMerged()
    {
        Assert.Equal(
            new MergeDecision.Wait("The card's pick-up label devflow was added by mallory, who is not a trusted account of this project", TryAgain: false),
            MergeCheck.Decide(Good with { LabelEvents = Events(Added("devflow", "octo-owner", 0), Removed("devflow", "octo-owner", 1), Added("devflow", "mallory", 2)) }));
        Assert.Equal(
            new MergeDecision.Wait("The pick-up label devflow on its feature, #10, was added by mallory, who is not a trusted account of this project", TryAgain: false),
            MergeCheck.Decide(GoodFeature with { Feature = GoodFeature.Feature! with { LabelEvents = Events(Added("devflow", "mallory", 0)) } }));
        Assert.Equal(
            new MergeDecision.Wait("The card's pick-up label devflow was not added by a trusted account of this project", TryAgain: false),
            MergeCheck.Decide(Good with { LabelEvents = Events() }));
    }

    /// <summary>Condition 4 reads the project's trusted accounts: a card, a feature's parent and
    /// a feature's card written and edited by any of them merge, whatever the case GitHub answers
    /// their login in.</summary>
    [Fact]
    public void ATrustedAccountsCardIsMerged()
    {
        var edits = Found<IReadOnlyList<IssueEdit>>([new IssueEdit("UCE_1", "Octo-Friend", Pushed, EditKind.Body)]);
        var card = Good with
        {
            Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Author = "OCTO-FRIEND", Body = "Build it, and the empty state." }]),
            Edits = edits,
        };
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(card));

        var feature = GoodFeature with
        {
            Labelled = Found<IReadOnlyList<Issue>>([ParentIssue with { Author = "octo-friend" }]),
            Edits = edits,
            Feature = GoodFeature.Feature! with
            {
                SubIssues = Found<IReadOnlyList<SubIssue>>([CardSub with { Author = "octo-friend" }]),
                Edits = Found<IReadOnlyList<IssueEdit>>([new IssueEdit("UCE_2", "octo-friend", Pushed, EditKind.Title)]),
            },
        };
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(feature));
    }

    /// <summary>A card, a feature's parent or a feature's card written or edited by a login off the
    /// list is not merged, and the reason says it is not trusted; one taken off the list since it
    /// was picked no longer merges either.</summary>
    [Fact]
    public void AStrangersCardIsNotMerged()
    {
        Assert.Equal(
            new MergeDecision.Wait("The card was written by mallory, who is not a trusted account of this project", TryAgain: false),
            MergeCheck.Decide(Good with { Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Author = "mallory" }]) }));
        Assert.Equal(
            new MergeDecision.Wait("The card was edited by mallory since it was picked", TryAgain: false),
            MergeCheck.Decide(Good with { Edits = Found<IReadOnlyList<IssueEdit>>([new IssueEdit("UCE_1", "mallory", Pushed, EditKind.Body)]) }));
        Assert.Equal(
            new MergeDecision.Wait("Its feature, #10, was written by mallory, who is not a trusted account of this project", TryAgain: false),
            MergeCheck.Decide(GoodFeature with { Labelled = Found<IReadOnlyList<Issue>>([ParentIssue with { Author = "mallory" }]) }));

        var friends = Good with { Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Author = "octo-friend" }]) };
        Assert.Equal(
            new MergeDecision.Wait("The card was written by octo-friend, who is not a trusted account of this project", TryAgain: false),
            MergeCheck.Decide(friends with { Trusted = TrustedAccounts.Of("octo-owner", []) }));
    }

    [Fact]
    public void AFeaturesCardMergesIntoItsBranch() =>
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(GoodFeature));

    /// <summary>Each condition of a feature's card, and of its parent, changed alone.</summary>
    public static TheoryData<string, string, bool> FeatureCases() => new()
    {
        { "feature's branch unwatched", MergeCheck.FeatureUnwatched, true },
        { "sub-issues unread", MergeCheck.CouldNotRead("the card"), true },
        { "parent's edits unread", MergeCheck.CouldNotRead("the card"), true },
        { "parent's label events unread", MergeCheck.CouldNotRead("the card"), true },
        { "card's label events unread", MergeCheck.CouldNotRead("the card"), true },
        { "parent unlabelled", MergeCheck.FeatureNotLabelled(10, "devflow"), false },
        { "only the card labelled", MergeCheck.FeatureNotLabelled(10, "devflow"), false },
        { "parent never picked", CardPicker.FeatureNotPicked(10), false },
        { "parent written by someone else", MergeCheck.FeatureWrittenBy(10, "mallory"), false },
        { "parent held", MergeCheck.FeatureHeld(10, "devflow-hold"), false },
        { "parent edited by someone else", MergeCheck.FeatureEditedBy(10, "mallory"), false },
        { "parent changed with no history", MergeCheck.FeatureChangedUnseen(10), false },
        { "no longer a sub-issue", MergeCheck.NotSubIssue(10), false },
        { "sub-issue closed", MergeCheck.NotSubIssue(10), false },
        { "sub-issue of another repository", MergeCheck.NotSubIssue(10), false },
        { "card written by someone else", MergeCheck.WrittenBy("mallory"), false },
        { "card held", MergeCheck.Held("devflow-hold"), false },
        { "card changed with no history", MergeCheck.ChangedUnseen, false },
        { "into the default branch", MergeCheck.IntoAnother("main", Branch), false },
    };

    [Theory]
    [MemberData(nameof(FeatureCases))]
    public void EachConditionAloneRefusesAFeaturesCard(string condition, string reason, bool tryAgain) =>
        Assert.Equal(new MergeDecision.Wait(reason, tryAgain), MergeCheck.Decide(BreakFeature(condition)));

    private static MergeFacts BreakFeature(string condition)
    {
        var feature = GoodFeature.Feature!;
        return condition switch
        {
            "feature's branch unwatched" => GoodFeature with { Feature = feature with { BranchUnwatched = true } },
            "sub-issues unread" => GoodFeature with { Feature = feature with { SubIssues = Down<IReadOnlyList<SubIssue>>() } },
            "parent's edits unread" => GoodFeature with { Feature = feature with { Edits = new CodeHostResult<IReadOnlyList<IssueEdit>>.NotFound() } },
            "parent's label events unread" => GoodFeature with { Feature = feature with { LabelEvents = Down<IReadOnlyList<LabelEvent>>() } },
            "card's label events unread" => GoodFeature with { LabelEvents = new CodeHostResult<IReadOnlyList<LabelEvent>>.NotFound() },
            "parent unlabelled" => GoodFeature with { Labelled = Found<IReadOnlyList<Issue>>([]) },
            "only the card labelled" => GoodFeature with { Labelled = Found<IReadOnlyList<Issue>>([CardIssue]) },
            "parent never picked" => GoodFeature with { Feature = feature with { Record = null } },
            "parent written by someone else" => GoodFeature with { Labelled = Found<IReadOnlyList<Issue>>([ParentIssue with { Author = "mallory" }]) },
            "parent held" => GoodFeature with { Labelled = Found<IReadOnlyList<Issue>>([ParentIssue with { Labels = ["devflow", "devflow-hold"] }]) },
            "parent edited by someone else" => GoodFeature with { Feature = feature with { Edits = Found<IReadOnlyList<IssueEdit>>([new IssueEdit("UCE_3", "mallory", Pushed, EditKind.Body)]) } },
            "parent changed with no history" => GoodFeature with { Labelled = Found<IReadOnlyList<Issue>>([ParentIssue with { Body = "Something else." }]) },
            "no longer a sub-issue" => GoodFeature with { Feature = feature with { SubIssues = Found<IReadOnlyList<SubIssue>>([]) } },
            "sub-issue closed" => GoodFeature with { Feature = feature with { SubIssues = Found<IReadOnlyList<SubIssue>>([CardSub with { Open = false }]) } },
            "sub-issue of another repository" => GoodFeature with { Feature = feature with { SubIssues = Found<IReadOnlyList<SubIssue>>([CardSub with { Name = "Other" }]) } },
            "card written by someone else" => GoodFeature with { Feature = feature with { SubIssues = Found<IReadOnlyList<SubIssue>>([CardSub with { Author = "mallory" }]) } },
            "card held" => GoodFeature with { Feature = feature with { SubIssues = Found<IReadOnlyList<SubIssue>>([CardSub with { Labels = ["devflow-hold"] }]) } },
            "card changed with no history" => GoodFeature with { Feature = feature with { SubIssues = Found<IReadOnlyList<SubIssue>>([CardSub with { Body = "Something else." }]) } },
            "into the default branch" => GoodFeature with { PullRequest = Found(Pull with { BaseBranch = "main" }) },
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "not a condition"),
        };
    }
    /// <summary>Facts where every condition holds on <c>sometimes</c> with the three conditions
    /// ticked and nothing holding: no review label, no listed path changed, no decision for the
    /// owner.</summary>
    private static readonly MergeFacts GoodSometimes = Good with
    {
        WaitsForOwner = "sometimes",
        Conditions = WaitConditions.Defaults with { Paths = ["src/auth/**", "*.sql"] },
        Files = Found<IReadOnlyList<ChangedFile>>([new ChangedFile("web/src/list.tsx", null), new ChangedFile("web/src/list.test.tsx", null)]),
    };

    private static MergeDecision.Wait Owners(string reason) => new(reason, TryAgain: false);

    /// <summary>With the review condition ticked, a card carrying the review label waits for the
    /// owner, whoever put it on; one a trusted account put on and only someone untrusted took off
    /// still counts; taken off by a trusted account it merges, and with the condition not ticked a
    /// labelled card merges.</summary>
    [Fact]
    public void SometimesWaitsForTheReviewLabel()
    {
        var labelled = GoodSometimes with { Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Labels = ["devflow", "DevFlow-Review"] }]) };
        Assert.Equal(Owners("This card is marked for your review"), MergeCheck.Decide(labelled));

        // Anyone may mark a card for the owner's review.
        Assert.Equal(Owners(MergeCheck.Marked), MergeCheck.Decide(GoodSometimes with
        {
            Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Labels = ["devflow", "devflow-review"] }]),
            LabelEvents = Events(Added("devflow", "octo-owner", 0), Added("devflow-review", "mallory", 1)),
        }));

        // Taken off by someone untrusted, it still counts.
        Assert.Equal(Owners(MergeCheck.Marked), MergeCheck.Decide(GoodSometimes with
        {
            LabelEvents = Events(Added("devflow", "octo-owner", 0), Added("devflow-review", "octo-friend", 1), Removed("devflow-review", "mallory", 2)),
        }));

        // Taken off by a trusted account, it does not.
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(GoodSometimes with
        {
            LabelEvents = Events(Added("devflow", "octo-owner", 0), Added("devflow-review", "octo-friend", 1), Removed("devflow-review", "mallory", 2), Removed("devflow-review", "octo-owner", 3)),
        }));

        // The project's own name for it.
        Assert.Equal(Owners(MergeCheck.Marked), MergeCheck.Decide(labelled with
        {
            Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Labels = ["devflow", "look at me"] }]),
            Conditions = GoodSometimes.Conditions with { ReviewLabel = "Look at me" },
        }));

        // Not ticked, a labelled card merges.
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(labelled with { Conditions = GoodSometimes.Conditions with { WhenMarked = false } }));

        // On never the label means nothing, and on always the card waits whatever it carries.
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(labelled with { WaitsForOwner = "never" }));
        Assert.Equal(Owners(MergeCheck.Always), MergeCheck.Decide(labelled with { WaitsForOwner = "always" }));
    }

    /// <summary>A card of a feature whose parent carries the review label waits, as does one
    /// carrying it itself; a parent's label taken off by someone untrusted still counts.</summary>
    [Fact]
    public void SometimesWaitsForTheParentsReviewLabel()
    {
        var feature = GoodFeature with { WaitsForOwner = "sometimes", Conditions = GoodSometimes.Conditions, Files = GoodSometimes.Files };
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(feature));

        Assert.Equal(
            Owners("Its feature, #10, is marked for your review"),
            MergeCheck.Decide(feature with { Labelled = Found<IReadOnlyList<Issue>>([ParentIssue with { Labels = ["devflow", "devflow-review"] }]) }));
        Assert.Equal(Owners(MergeCheck.FeatureMarked(10)), MergeCheck.Decide(feature with
        {
            Feature = feature.Feature! with
            {
                LabelEvents = Events(Added("devflow", "octo-owner", 0), Added("devflow-review", "octo-owner", 1), Removed("devflow-review", "mallory", 2)),
            },
        }));

        // A card's own label inside a feature counts too.
        Assert.Equal(Owners(MergeCheck.Marked), MergeCheck.Decide(feature with
        {
            Feature = feature.Feature! with { SubIssues = Found<IReadOnlyList<SubIssue>>([CardSub with { Labels = ["devflow-review"] }]) },
        }));

        // Not ticked, it merges into the feature's branch.
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(feature with
        {
            Labelled = Found<IReadOnlyList<Issue>>([ParentIssue with { Labels = ["devflow", "devflow-review"] }]),
            Conditions = GoodSometimes.Conditions with { WhenMarked = false },
        }));
    }

    /// <summary>A change touching a listed path waits, naming the path and the pattern; a rename
    /// is matched by its new and its old name; a pull request whose files are at GitHub's limit is
    /// the owner's, and one whose files could not be read is tried again.</summary>
    [Fact]
    public void SometimesWaitsForAListedPath()
    {
        Assert.Equal(
            Owners("It changes src/auth/login.rs, which matches src/auth/** on this project's list of paths"),
            MergeCheck.Decide(GoodSometimes with { Files = Found<IReadOnlyList<ChangedFile>>([new ChangedFile("web/src/list.tsx", null), new ChangedFile("src/auth/login.rs", null)]) }));
        Assert.Equal(
            Owners(MergeCheck.PathListed("schema.sql", "*.sql")),
            MergeCheck.Decide(GoodSometimes with { Files = Found<IReadOnlyList<ChangedFile>>([new ChangedFile("schema.sql", null)]) }));

        // A rename, by either name.
        Assert.Equal(
            Owners(MergeCheck.PathListed("src/auth/old.rs", "src/auth/**")),
            MergeCheck.Decide(GoodSometimes with { Files = Found<IReadOnlyList<ChangedFile>>([new ChangedFile("lib/old.rs", "src/auth/old.rs")]) }));
        Assert.Equal(
            Owners(MergeCheck.PathListed("src/auth/new.rs", "src/auth/**")),
            MergeCheck.Decide(GoodSometimes with { Files = Found<IReadOnlyList<ChangedFile>>([new ChangedFile("src/auth/new.rs", "lib/new.rs")]) }));

        // Files that cannot all be judged: at GitHub's limit, the owner's; not read, tried again.
        var limit = Enumerable.Range(0, ICodeHost.MostPullRequestFiles).Select(index => new ChangedFile(FormattableString.Invariant($"web/{index}.ts"), null)).ToList();
        Assert.Equal(Owners("Too many files to check the paths"), MergeCheck.Decide(GoodSometimes with { Files = Found<IReadOnlyList<ChangedFile>>(limit) }));
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(GoodSometimes with { Files = Found<IReadOnlyList<ChangedFile>>(limit[..^1]) }));
        Assert.Equal(
            new MergeDecision.Wait(MergeCheck.CouldNotRead("the files of its pull request"), TryAgain: true),
            MergeCheck.Decide(GoodSometimes with { Files = Down<IReadOnlyList<ChangedFile>>() }));
        Assert.Equal(
            new MergeDecision.Wait(MergeCheck.CouldNotRead("the files of its pull request"), TryAgain: true),
            MergeCheck.Decide(GoodSometimes with { Files = new CodeHostResult<IReadOnlyList<ChangedFile>>.NotFound() }));

        // Not ticked, a listed path merges, with nothing read.
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(GoodSometimes with
        {
            Conditions = GoodSometimes.Conditions with { WhenPaths = false },
            Files = Down<IReadOnlyList<ChangedFile>>(),
        }));
    }

    /// <summary>With the decision condition ticked, a card whose build or fix reports marked a
    /// decision for the owner waits; not ticked, it merges, and its attention issue is opened as
    /// with never, see OwnerDecisionTests.</summary>
    [Fact]
    public void SometimesWaitsForADecision()
    {
        Assert.Equal(Owners("The agent made a decision for you"), MergeCheck.Decide(GoodSometimes with { DecisionForOwner = true }));
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(GoodSometimes with
        {
            DecisionForOwner = true,
            Conditions = GoodSometimes.Conditions with { WhenDecision = false },
        }));
    }

    /// <summary>With nothing holding it merges as with never: every condition ticked and none
    /// holding, none ticked, and a list of paths that is empty, which needs no files read.</summary>
    [Fact]
    public void SometimesMergesWhenNothingHolds()
    {
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(GoodSometimes));
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(GoodSometimes with
        {
            Conditions = WaitConditions.Defaults,
            Files = Down<IReadOnlyList<ChangedFile>>(),
        }));

        // The other conditions still decide first: an unapproved card is not merged, and CI still
        // running is waited for.
        Assert.Equal(Owners(MergeCheck.NotApproved), MergeCheck.Decide(GoodSometimes with { Review = null }));
        Assert.Equal(
            new MergeDecision.Wait(MergeCheck.CiRunning, TryAgain: true),
            MergeCheck.Decide(GoodSometimes with { CheckRuns = Found<IReadOnlyList<CheckRun>>([Run(CheckStatus.InProgress, null)]) }));
    }

    /// <summary>Condition 4 compares the card with its last reading's record as well as the pick's:
    /// the card as GitHub has it now must be what its newest reading read, its body and its title,
    /// since that is what it was built from. A trusted edit since the reading, which the pick's
    /// record allows, still refuses, and it is the owner's; a card read as it is merges. See "The
    /// merge check" in devflow/README.md.</summary>
    [Fact]
    public void ACardChangedSinceItWasReadIsNotMerged()
    {
        var read = new CardRecord(SHA256.HashData(Encoding.UTF8.GetBytes(Body)), "Card 1", null);

        // Read as it is: it merges, on its own and in a feature.
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(Good with { Reading = read }));
        Assert.Equal(new MergeDecision.Merge(Head), MergeCheck.Decide(GoodFeature with { Reading = read }));

        // Its body edited by the owner since it was read: the pick's record allows the edit, the
        // reading's does not.
        var edited = Good with
        {
            Labelled = Found<IReadOnlyList<Issue>>([CardIssue with { Body = "Build it, and the empty state." }]),
            Edits = Found<IReadOnlyList<IssueEdit>>([new IssueEdit("UCE_1", "octo-owner", Pushed, EditKind.Body)]),
        };
        Assert.IsType<MergeDecision.Merge>(MergeCheck.Decide(edited));
        Assert.Equal(new MergeDecision.Wait(MergeCheck.ChangedSinceRead, TryAgain: false), MergeCheck.Decide(edited with { Reading = read }));

        // Its title too, and a feature's card as its parent lists it.
        Assert.Equal(
            new MergeDecision.Wait(MergeCheck.ChangedSinceRead, TryAgain: false),
            MergeCheck.Decide(Good with { Reading = read with { Title = "Card one" } }));
        var renamed = GoodFeature with
        {
            Reading = read,
            Feature = GoodFeature.Feature! with { SubIssues = Found<IReadOnlyList<SubIssue>>([CardSub with { Title = "Card one" }]) },
            Edits = Found<IReadOnlyList<IssueEdit>>([new IssueEdit("UCE_1", "octo-owner", Pushed, EditKind.Title)]),
        };
        Assert.Equal(new MergeDecision.Wait(MergeCheck.ChangedSinceRead, TryAgain: false), MergeCheck.Decide(renamed));
    }
}