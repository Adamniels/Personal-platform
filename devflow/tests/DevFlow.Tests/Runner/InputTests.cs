using DevFlow.CodeHost;
using DevFlow.Runner;

namespace DevFlow.Tests.Runner;

/// <summary>
/// Everything the reader reads was written by someone on the code host, so it comes back as the
/// text it was, never interpreted, and nothing in it makes the reader throw.
/// </summary>
public sealed class InputTests
{
    private const string Script = "<script>alert(1)</script>";
    private const string Markup = "<img src=x onerror=alert(1)> <b>bold</b> &amp; [a link](javascript:alert(1))";

    [Fact]
    public void MarkupInAnyFieldComesBackAsTextAndNothingThrows()
    {
        var longLine = new string('<', 200_000);
        var world = new World();
        var title = Script + Markup + longLine;
        var body = $"""
            ## Summary

            {Markup} {Script}

            Closes #7

            ## Decisions the agent made

            - **owner-decision** {Script} Alternative: {Markup} Why: {longLine}
            - {longLine}
            """;
        var forTheOwner = $"- {Script} {Markup}\n- {longLine}";
        world = world with
        {
            Pull = world.Pull with { Title = title, Body = body },
            Comments = [World.Verdict(1, "approve", World.Head, World.At, forTheOwner), World.By("someone", 2, Script + longLine, World.At)],
            SliceComments = [World.By(World.Bot, 3, $"@Adamniels PR #42 is ready for you to read and merge: the merge gate leaves it to the owner ({Script}, {Markup}).", World.At.AddMinutes(1))],
        };

        var item = world.Read();
        Assert.NotNull(item);
        Assert.Equal(title, item.Title);
        Assert.Equal($"{Markup} {Script}", item.Summary);
        Assert.Equal([new OwnerDecision(Script, Markup, longLine)], item.OwnerDecisions);
        Assert.Equal(1, item.OtherDecisions);
        Assert.Equal([$"{Script} {Markup}", longLine], item.ReviewerView);
        Assert.Equal($"{Script} {Markup}", item.Reason);

        // A long line in the summary is cut, never anything else.
        var cut = (world with { Pull = world.Pull with { Body = "## Summary\n\n" + longLine } }).Read();
        Assert.Equal(300, cut!.Summary.Length);
    }

    [Fact]
    public void WhatIsMissingOrEmptyIsLeftOutAndNothingThrows()
    {
        var world = new World();

        // An empty body, and nothing around the pull request.
        var bare = (world with
        {
            Pull = world.Pull with { Body = "", Title = "" },
            Statuses = [],
            CheckRuns = [],
            SliceComments = [],
        }).Read();
        Assert.NotNull(bare);
        Assert.Equal("", bare.Summary);
        Assert.Null(bare.Slice);
        Assert.Empty(bare.OwnerDecisions);
        Assert.False(bare.CanMerge);
        Assert.Equal(RunnerReason.NotSaid, bare.Reason);

        // Nulls where the records do not allow them, as a careless caller could pass.
        var nulls = (world with
        {
            Pull = world.Pull with { Body = null!, Title = null!, Labels = ["owner-blocked", null!] },
            Comments = [null!, World.By(null!, 5, null!, World.At)],
            Statuses = [null!, new CommitStatus(null!, StatusState.Success, null)],
            CheckRuns = [null!, new CheckRun(null!, CheckStatus.Completed, null, null!)],
            SliceComments = [null!, World.By(World.Bot, 6, null!, World.At)],
        }).Read();
        Assert.NotNull(nulls);
        Assert.Equal(RunnerItemKind.Stopped, nulls.Kind);
        Assert.Equal("", nulls.Title);
        Assert.Equal("", nulls.Summary);

        Assert.Null(RunnerReader.Read(null!));
        Assert.Null(RunnerReader.Read(world.Input with { PullRequest = null! }));
        Assert.Null(RunnerReader.Read(world.Input with { Bot = null! }));
        Assert.NotNull(RunnerReader.Read(world.Labelled("owner-blocked").Input with { Comments = null!, Statuses = null!, CheckRuns = null!, SliceComments = null! }));
        Assert.Null(RunnerReader.Slice(null!));
    }
}