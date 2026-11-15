using System.Text.RegularExpressions;

using DevFlow.CodeHost;
using DevFlow.Runner;

namespace DevFlow.Tests.Runner;

/// <summary>
/// Why a pull request is the owner's turn, in words, from the runner's mention: the merge gate's
/// reason lines in it, the review rounds used up, or a Blocked notice.
/// </summary>
public sealed partial class ReasonTests
{
    private static readonly DateTimeOffset At = World.At;

    private static string From(IEnumerable<Comment> slice, IEnumerable<Comment>? pull = null) =>
        RunnerReason.From(World.Number, slice, pull ?? [], World.Bot);

    private static Comment Bot(long id, string body, int minutes = 0) => World.By(World.Bot, id, body, At.AddMinutes(minutes));

    [Fact]
    public void TheGatesReasonsBecomeWords()
    {
        // The five Settled words.
        Assert.Equal("High risk slice.", RunnerReason.Gate("risk is not low"));
        Assert.Equal("Touches paths that steer the agents.", RunnerReason.Gate("touches protected paths"));
        Assert.Equal("Carries a decision for you.", RunnerReason.Gate("an owner label is set"));
        Assert.Equal("CI failed.", RunnerReason.Gate("ci-ok failed"));
        Assert.Equal("CI has not finished.", RunnerReason.Gate("ci-ok is pending"));

        // Every reason line the merge gate prints is a sentence of its own.
        var gate = File.ReadAllText(Repository.Path("scripts", "agent", "merge-gate.sh"));
        var lines = ReasonCall().Matches(gate).Select(match => match.Groups[1].Value).Append("pull request is already merged").ToList();
        Assert.True(lines.Count >= 14, $"expected the gate's reason lines, found {lines.Count}");
        foreach (var line in lines)
        {
            var words = RunnerReason.Gate(line);
            Assert.NotEqual(line, words);
            Assert.Matches("^[A-Z].*\\.$", words);
        }

        Assert.Equal(lines.Count, lines.Select(RunnerReason.Gate).Distinct().Count());

        // A line it does not know is shown as it is.
        Assert.Equal("the moon is full", RunnerReason.Gate("the moon is full"));
        Assert.Equal("no reasons printed", RunnerReason.Gate("no reasons printed"));

        // From the mentions, in the order the gate printed them.
        Assert.Equal(
            "Touches paths that steer the agents. High risk slice. Carries a decision for you.",
            From([Bot(1, "@Adamniels PR #42 is approved and waits for your decision: read \"Decisions the agent made\" in its body and \"For the owner\" in the review, then merge it or comment on it to change something (touches protected paths, risk is not low, an owner label is set).")]));
        Assert.Equal(
            "High risk slice.",
            From([Bot(1, "@Adamniels PR #42 is ready for you to read and merge: the merge gate leaves it to the owner (risk is not low).")]));
        Assert.Equal(
            "CI failed. The slice is not open.",
            From([Bot(1, "@Adamniels PR #42 needs you, do not merge it as it is: the merge gate refused it (ci-ok failed, slice is not open).")]));
        Assert.Equal(
            "The reviewer asked for changes a fourth time.",
            From([Bot(1, "@Adamniels PR #42 has used up its review rounds, do not merge it as it is: the reviewer asked for changes a fourth time, so it waits for you.")]));

        // A Blocked notice is on the pull request itself.
        Assert.Equal(
            "The slice is Blocked. 2 runs in a row failed on it, the last one fix (exit 1).",
            From([], [Bot(1, "@Adamniels slice #7 is Blocked: 2 runs in a row failed on it, the last one fix (exit 1). See /Users/agent/.agent-logs/x.log on the Mac mini. Comment on this PR or move the slice off Blocked to run it again.")]));
        Assert.Equal(
            "The slice is Blocked. it stopped",
            From([], [Bot(1, "@Adamniels slice #7 is Blocked: it stopped")]));

        // The latest of them, wherever it is.
        Assert.Equal(
            "CI failed.",
            From(
                [Bot(1, "@Adamniels PR #42 is ready for you to read and merge: the merge gate leaves it to the owner (risk is not low).", 0), Bot(2, "@Adamniels PR #42 needs you, do not merge it as it is: the merge gate refused it (ci-ok failed).", 5)],
                [Bot(3, "@Adamniels slice #7 is Blocked: 2 runs in a row failed on it.", 2)]));
        Assert.Equal(
            "The slice is Blocked. 2 runs in a row failed on it.",
            From(
                [Bot(1, "@Adamniels PR #42 is ready for you to read and merge: the merge gate leaves it to the owner (risk is not low).", 0)],
                [Bot(3, "@Adamniels slice #7 is Blocked: 2 runs in a row failed on it.", 2)]));
    }

    [Fact]
    public void WithoutAMentionTheReasonSaysTheRunnerDidNotSay()
    {
        Assert.Equal("The runner did not say why.", RunnerReason.NotSaid);
        Assert.Equal(RunnerReason.NotSaid, From([]));

        // Comments that are not a mention of this pull request by the bot.
        Assert.Equal(RunnerReason.NotSaid, From([Bot(1, "@Adamniels the runner's queue is empty: no pull request waits on it and no slice is Ready. It looks again every 600 s.")]));
        Assert.Equal(RunnerReason.NotSaid, From([Bot(1, "@Adamniels PR #420 is ready for you to read and merge: the merge gate leaves it to the owner (risk is not low).")]));
        Assert.Equal(RunnerReason.NotSaid, From([Bot(1, "@Adamniels PR #4 is ready for you to read and merge: the merge gate leaves it to the owner (risk is not low).")]));
        Assert.Equal(RunnerReason.NotSaid, From([World.By("someone-else", 1, "@Adamniels PR #42 is ready for you to read and merge: the merge gate leaves it to the owner (risk is not low).", At)]));
        Assert.Equal(RunnerReason.NotSaid, From([Bot(1, "Quoting the runner: @Adamniels PR #42 is ready for you to read and merge: the merge gate leaves it to the owner (risk is not low).")]));
        Assert.Equal(RunnerReason.NotSaid, From([Bot(1, "@Adamniels ci-ok has been pending on PR #42 for 31 min at head abc. The runner keeps waiting for it.")]));

        // A Blocked notice counts only on the pull request, and only by the bot.
        Assert.Equal(RunnerReason.NotSaid, From([Bot(1, "@Adamniels slice #7 is Blocked: it stopped.")]));
        Assert.Equal(RunnerReason.NotSaid, From([], [World.By(World.Owner, 1, "@Adamniels slice #7 is Blocked: it stopped.", At)]));

        // Through the reader.
        var item = (new World() with { SliceComments = [] }).Read();
        Assert.NotNull(item);
        Assert.Equal(RunnerReason.NotSaid, item.Reason);
    }

    [Fact]
    public void TheMentionsTheReaderKnowsAreTheRunners()
    {
        var runner = File.ReadAllText(Repository.Path("scripts", "agent", "run.sh"));
        Assert.NotEmpty(RunnerReason.Mentions);
        foreach (var mention in RunnerReason.Mentions)
        {
            Assert.Contains(mention, runner, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex("""\breason "([^"]+)"$""", RegexOptions.Multiline)]
    private static partial Regex ReasonCall();
}