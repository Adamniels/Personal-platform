using DevFlow.CodeHost;
using DevFlow.Runner;

namespace DevFlow.Tests.Runner;

/// <summary>
/// The verdict is read by <c>scripts/agent/verdict.sh</c>'s rule exactly, since the merge gate
/// trusts that one: a comment by the bot, never edited, whose first line is the marker and nothing
/// else, the latest by creation time and then id.
/// </summary>
public sealed class VerdictTests
{
    private static readonly DateTimeOffset At = World.At;

    private static Verdict? Latest(params Comment[] comments) => Verdicts.Latest(comments, World.Bot);

    [Fact]
    public void OnlyAnUneditedCommentByTheBotWithTheExactFirstLineCounts()
    {
        var valid = World.Verdict(1, "approve", World.Head, At);
        var read = Latest(valid);
        Assert.NotNull(read);
        Assert.Equal(VerdictKind.Approve, read.Kind);
        Assert.Equal(World.Head, read.Sha);
        Assert.Equal(valid, read.Comment);
        Assert.Equal(VerdictKind.Changes, Latest(World.Verdict(1, "changes", World.Head, At))!.Kind);

        var line = Marker.Line("approve", World.Head);
        Comment[] ignored =
        [
            // Edited after it was written.
            valid with { UpdatedAt = At.AddSeconds(1) },
            // Another author.
            valid with { Author = World.Owner },
            valid with { Author = "mac-mini-agent-2" },
            // The marker on a later line, as a reply quoting it has it.
            valid with { Body = "Quoting the review:\n" + line },
            valid with { Body = "\n" + line },
            // Extra spaces, before, inside or after.
            valid with { Body = " " + line },
            valid with { Body = line + " " },
            valid with { Body = line.Replace(": approve", ":  approve", StringComparison.Ordinal) },
            // A trailing carriage return.
            valid with { Body = line + "\r\nThe rest." },
            valid with { Body = line + "\r" },
            // Text after the marker on its line.
            valid with { Body = line + " approved" },
            // A short, a long or an upper case sha.
            valid with { Body = Marker.Line("approve", World.Head[..39]) },
            valid with { Body = Marker.Line("approve", World.Head + "a") },
            valid with { Body = Marker.Line("approve", World.Head.ToUpperInvariant()) },
            // Another word.
            valid with { Body = Marker.Line("reject", World.Head) },
            valid with { Body = Marker.Line("Approve", World.Head) },
            valid with { Body = line.Replace("<!-- ", "<!--", StringComparison.Ordinal) },
            // No body at all.
            valid with { Body = "" },
            valid with { Body = null! },
        ];
        foreach (var comment in ignored)
        {
            Assert.Null(Latest(comment));
        }

        // One that does not count never hides an earlier one that does.
        var later = valid with { Id = 2, CreatedAt = At.AddMinutes(1), UpdatedAt = At.AddMinutes(2), Body = Marker.Line("changes", World.Head) };
        Assert.Equal(valid, Latest(valid, later)!.Comment);
    }

    [Fact]
    public void TheLatestVerdictWins()
    {
        var first = World.Verdict(1, "changes", World.Older, At);
        var second = World.Verdict(2, "approve", World.Head, At.AddMinutes(5));

        // By creation time, whatever the order they come in.
        Assert.Equal(second, Latest(first, second)!.Comment);
        Assert.Equal(second, Latest(second, first)!.Comment);

        // A lower id written later still wins.
        var laterLowerId = World.Verdict(0, "changes", World.Head, At.AddMinutes(10));
        Assert.Equal(laterLowerId, Latest(first, second, laterLowerId)!.Comment);

        // At the same time, the higher id.
        var sameTimeHigherId = World.Verdict(3, "changes", World.Head, At.AddMinutes(5));
        Assert.Equal(sameTimeHigherId, Latest(sameTimeHigherId, second)!.Comment);
        Assert.Equal(sameTimeHigherId, Latest(second, sameTimeHigherId)!.Comment);
    }
}