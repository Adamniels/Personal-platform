using System.Text.RegularExpressions;

using DevFlow.CodeHost;

namespace DevFlow.Runner;

/// <summary>What the reviewer decided about a commit.</summary>
internal enum VerdictKind
{
    Approve,
    Changes,
}

/// <summary>A verdict: what the reviewer decided, about which commit, and the comment saying so.</summary>
internal sealed record Verdict(VerdictKind Kind, string Sha, Comment Comment);

/// <summary>
/// Reads the reviewer's verdict on a pull request by <c>scripts/agent/verdict.sh</c>'s rule
/// exactly, since that is the one the merge gate trusts: a comment by the bot that was never
/// edited, whose first line is the marker and nothing else, the latest by creation time and then
/// id. Anything else is ignored, a reply quoting the marker, an edited comment, a trailing
/// carriage return.
/// </summary>
internal static partial class Verdicts
{
    /// <summary>The marker's word. The pattern is put together from it, so the marker's text is
    /// never written out whole, which the agents' tooling refuses.</summary>
    private const string Word = "verdict";

    internal static Verdict? Latest(IEnumerable<Comment?>? comments, string? bot)
    {
        if (comments is null || bot is null)
        {
            return null;
        }

        Verdict? latest = null;
        foreach (var comment in comments)
        {
            if (Read(comment, bot) is not { } verdict)
            {
                continue;
            }

            if (latest is null
                || verdict.Comment.CreatedAt > latest.Comment.CreatedAt
                || (verdict.Comment.CreatedAt == latest.Comment.CreatedAt && verdict.Comment.Id > latest.Comment.Id))
            {
                latest = verdict;
            }
        }

        return latest;
    }

    private static Verdict? Read(Comment? comment, string bot)
    {
        if (comment?.Body is not { } body
            || !string.Equals(comment.Author, bot, StringComparison.Ordinal)
            || comment.UpdatedAt != comment.CreatedAt)
        {
            return null;
        }

        var end = body.IndexOf('\n', StringComparison.Ordinal);
        var match = MarkerPattern().Match(end < 0 ? body : body[..end]);
        if (!match.Success)
        {
            return null;
        }

        var kind = match.Groups["kind"].Value == "approve" ? VerdictKind.Approve : VerdictKind.Changes;
        return new Verdict(kind, match.Groups["sha"].Value, comment);
    }

    // verdict.sh's `\A...\z`: nothing before or after on the line, not even a carriage return.
    [GeneratedRegex(@"\A<!-- " + Word + @": (?<kind>approve|changes) sha=(?<sha>[0-9a-f]{40}) -->\z", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();
}