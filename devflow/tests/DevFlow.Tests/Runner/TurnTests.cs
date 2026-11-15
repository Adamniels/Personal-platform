using DevFlow.CodeHost;
using DevFlow.Runner;

namespace DevFlow.Tests.Runner;

/// <summary>
/// Whose turn a pull request is, from its labels, checked against the verdict, and whether merging
/// may be offered. See "Reading this repository's runner" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class TurnTests
{
    private static readonly World Waiting = new();

    [Fact]
    public void AnOwnerLabelIsNotTrustedWithoutAnApproveForTheHead()
    {
        foreach (var label in new[] { "owner-merge", "owner-decision" })
        {
            var world = Waiting.Labelled(label);
            Assert.Equal(RunnerItemKind.Waiting, world.Read()?.Kind);

            // Approved, but for an older commit: the runner has not corrected the label yet.
            Assert.Null((world with { Comments = [World.Verdict(1, "approve", World.Older, World.At)] }).Read());

            // The reviewer asked for changes on the head.
            Assert.Null((world with { Comments = [World.Verdict(1, "changes", World.Head, World.At)] }).Read());

            // An approve for the head, then changes for it later.
            Assert.Null((world with
            {
                Comments = [World.Verdict(1, "approve", World.Head, World.At), World.Verdict(2, "changes", World.Head, World.At.AddMinutes(1))],
            }).Read());

            // No verdict at all.
            Assert.Null((world with { Comments = [] }).Read());

            // An approve for the head that was edited is no verdict.
            var edited = World.Verdict(1, "approve", World.Head, World.At);
            Assert.Null((world with { Comments = [edited with { UpdatedAt = World.At.AddSeconds(1) }] }).Read());
        }

        // Blocked needs no verdict: it is never offered for merging.
        Assert.Equal(RunnerItemKind.Stopped, (Waiting.Labelled("owner-blocked") with { Comments = [] }).Read()?.Kind);
        // Blocked wins over a label that would make it waiting.
        Assert.Equal(RunnerItemKind.Stopped, Waiting.Labelled("owner-merge", "owner-blocked").Read()?.Kind);

        // No owner label, whatever else it has, is not the owner's turn.
        Assert.Null(Waiting.Labelled().Read());
        Assert.Null(Waiting.Labelled("in-review", "has-decision").Read());
        Assert.Null(Waiting.Labelled("Owner-Merge").Read());
    }

    [Fact]
    public void OnlyTheBotsOpenPullRequestsThatAreNotDraftsCount()
    {
        Assert.NotNull(Waiting.Read());
        Assert.NotNull(Waiting.Labelled("owner-blocked").Read());

        foreach (var world in new[] { Waiting, Waiting.Labelled("owner-blocked") })
        {
            Assert.Null((world with { Pull = world.Pull with { Draft = true } }).Read());
            Assert.Null((world with { Pull = world.Pull with { Author = World.Owner } }).Read());
            Assert.Null((world with { Pull = world.Pull with { Author = "MAC-MINI-AGENT" } }).Read());
            Assert.Null((world with { Pull = world.Pull with { State = PullRequestState.Merged } }).Read());
            Assert.Null((world with { Pull = world.Pull with { State = PullRequestState.Closed } }).Read());
        }
    }

    [Fact]
    public void MergingIsOfferedOnlyWhenBothChecksAreGreenOnTheHead()
    {
        var item = Waiting.Read();
        Assert.NotNull(item);
        Assert.True(item.CanMerge);
        Assert.Equal(World.Head, item.HeadCommit);

        var ciOk = new CheckRun("ci-ok", CheckStatus.Completed, CheckConclusion.Success, "github-actions");
        var reviewOk = new CommitStatus("review-ok", StatusState.Success, "approved by the reviewer");

        // A check run named ci-ok by another app counts for nothing, either way.
        Assert.False(CanMerge(Waiting with { CheckRuns = [ciOk with { App = "some-other-app" }] }));
        Assert.True(CanMerge(Waiting with { CheckRuns = [ciOk, ciOk with { App = "some-other-app", Conclusion = CheckConclusion.Failure }] }));

        // ci-ok missing, failed, cancelled, skipped, or not finished.
        Assert.False(CanMerge(Waiting with { CheckRuns = [] }));
        Assert.False(CanMerge(Waiting with { CheckRuns = [new("ci", CheckStatus.Completed, CheckConclusion.Success, "github-actions")] }));
        Assert.False(CanMerge(Waiting with { CheckRuns = [ciOk with { Conclusion = CheckConclusion.Failure }] }));
        Assert.False(CanMerge(Waiting with { CheckRuns = [ciOk with { Conclusion = CheckConclusion.Cancelled }] }));
        Assert.False(CanMerge(Waiting with { CheckRuns = [ciOk with { Conclusion = CheckConclusion.Skipped }] }));
        Assert.False(CanMerge(Waiting with { CheckRuns = [ciOk with { Status = CheckStatus.InProgress, Conclusion = null }] }));
        Assert.False(CanMerge(Waiting with { CheckRuns = [ciOk with { Status = CheckStatus.Queued, Conclusion = null }] }));
        // Two runs of it, one of them failed, as the merge gate reads them.
        Assert.False(CanMerge(Waiting with { CheckRuns = [ciOk, ciOk with { Conclusion = CheckConclusion.Failure }] }));

        // review-ok missing, pending, failed, or under another context.
        Assert.False(CanMerge(Waiting with { Statuses = [] }));
        Assert.False(CanMerge(Waiting with { Statuses = [reviewOk with { State = StatusState.Pending }] }));
        Assert.False(CanMerge(Waiting with { Statuses = [reviewOk with { State = StatusState.Failure }] }));
        Assert.False(CanMerge(Waiting with { Statuses = [reviewOk with { State = StatusState.Error }] }));
        Assert.False(CanMerge(Waiting with { Statuses = [reviewOk with { Context = "ci-ok" }] }));

        // review-ok as a check run is not the status.
        Assert.False(CanMerge(Waiting with { Statuses = [], CheckRuns = [ciOk, new("review-ok", CheckStatus.Completed, CheckConclusion.Success, "github-actions")] }));

        // Both green, but the item is stopped: it is shown and never offered for merging.
        var stopped = Waiting.Labelled("owner-blocked").Read();
        Assert.NotNull(stopped);
        Assert.False(stopped.CanMerge);
    }

    [Fact]
    public void ACommentByTheOwnerAfterTheVerdictMeansSentBack()
    {
        Assert.False(Waiting.Read()!.SentBack);

        var after = World.By(World.Owner, 9, "Please name it differently.", World.At.AddMinutes(3));
        var before = World.By(World.Owner, 8, "Looks good so far.", World.At.AddMinutes(-3));
        var verdict = Waiting.Comments[0];

        Assert.True(SentBack(Waiting with { Comments = [verdict, after] }));
        Assert.True(SentBack(Waiting with { Comments = [after, verdict] }));
        Assert.False(SentBack(Waiting with { Comments = [before, verdict] }));

        // Someone else's comment, or the bot's own reply, is not the owner sending it back.
        Assert.False(SentBack(Waiting with { Comments = [verdict, after with { Author = "someone-else" }] }));
        Assert.False(SentBack(Waiting with { Comments = [verdict, after with { Author = World.Bot }] }));

        // With no owner linked, nothing is sent back.
        Assert.False(SentBack(Waiting with { Comments = [verdict, after], OwnerLogin = null }));

        // A stopped item is sent back the same way, and one with no verdict by any comment of his.
        var blocked = Waiting.Labelled("owner-blocked");
        Assert.True(SentBack(blocked with { Comments = [verdict, after] }));
        Assert.False(SentBack(blocked with { Comments = [before, verdict] }));
        Assert.True(SentBack(blocked with { Comments = [before] }));
        Assert.False(SentBack(blocked with { Comments = [] }));
    }

    private static bool CanMerge(World world) => world.Read() is { } item
        ? item.CanMerge
        : throw new Xunit.Sdk.XunitException("expected an item");

    private static bool SentBack(World world) => world.Read() is { } item
        ? item.SentBack
        : throw new Xunit.Sdk.XunitException("expected an item");
}