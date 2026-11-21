using System.Text;

namespace DevFlow.Features;

/// <summary>
/// Sending back a card that has landed in its feature's branch: the owner's comment on its issue,
/// posted from the feature's page or written on GitHub, opens it again with his comment as its
/// input, and a fresh agent builds it on a new branch from the feature's branch. See "Sending a card
/// back" in devflow/README.md, and in devflow/docs/devflow-architecture.md for why.
/// </summary>
public static class SendingBack
{
    /// <summary>What a card sent back says until its build starts.</summary>
    public const string Queued = "You sent it back; it is built again on a new branch";

    /// <summary>What a landed card says while its issue is open again with no comment of the
    /// owner's to send it back.</summary>
    public const string OpenAgain = "Its issue is open again, but only your comment on it sends it back";

    /// <summary>
    /// The comment that sends a card back from one of its decisions: the decision quoted, what was
    /// chosen and the alternative, each line of them as a quote, then <paramref name="text"/>, the
    /// owner's words as he wrote them. The agent's words reach the brief inside his comment, which
    /// is fenced there as his input whatever it holds.
    /// </summary>
    public static string DecisionComment(string chosen, string alternative, string text)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(alternative);
        ArgumentNullException.ThrowIfNull(text);
        var quote = new StringBuilder();
        Quote(quote, "Chosen", chosen);
        if (alternative.Trim().Length > 0)
        {
            Quote(quote, "Instead of", alternative);
        }

        return quote.Append('\n').Append(text).ToString();
    }

    private static void Quote(StringBuilder quote, string label, string text)
    {
        var lines = text.Trim().ReplaceLineEndings("\n").Split('\n');
        quote.Append("> ").Append(label).Append(": ").Append(lines[0]).Append('\n');
        foreach (var line in lines.Skip(1))
        {
            quote.Append("> ").Append(line).Append('\n');
        }
    }

    /// <summary>
    /// What makes a card one to send back, as SQL over <c>cards</c>: a card of a feature that has
    /// landed, <c>done</c> with the pull request it merged in, of a feature that is neither
    /// <c>merged</c> nor <c>closed</c>. The page offers "Send back" by it, the routes refuse by it,
    /// and a read takes the owner's comment on the card's issue by it, so the three never disagree.
    /// </summary>
    public const string Sendable = """
        (cards.feature_id IS NOT NULL AND cards.state = 'done' AND cards.pull_request_number IS NOT NULL
         AND EXISTS (SELECT 1 FROM features sf WHERE sf.id = cards.feature_id AND sf.state NOT IN ('merged', 'closed')))
        """;
}