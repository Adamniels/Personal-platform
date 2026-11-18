using System.Globalization;
using System.Text;

using DevFlow.Protocol;
using DevFlow.PullRequests;

namespace DevFlow.Reviews;

/// <summary>
/// The comment that copies a verdict onto its pull request, for people to read. Its first line
/// says which commit and what: <c>Review of abc1234: changes</c>; then the summary, the findings as
/// a list, blocking ones marked, the reviewer's view on each decision for the owner, and a last line
/// that leads back to the card and the run. Every text of the reviewer's is made data as the pull
/// request body makes the report's, by <see cref="PullRequestText"/>. Dev flow never reads it back,
/// so what it says decides nothing. See "Review" in devflow/README.md.
/// </summary>
public static class ReviewText
{
    /// <summary>The comment for <paramref name="review"/>, with a newline at its end.</summary>
    public static string Comment(UnpostedReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var verdict = review.Verdict;
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"Review of {review.Commit[..7]}: {verdict.Word}\n\n");
        var summary = PullRequestText.Text(verdict.Summary);
        if (summary.Length > 0)
        {
            body.Append(summary).Append("\n\n");
        }

        body.Append("## Findings\n\n");
        List(body, verdict.Findings.OrderByDescending(finding => finding.Blocking).Select(Finding));
        body.Append("## For the owner\n\n");
        List(body, verdict.ForOwner.Select(view => $"{PullRequestText.Text(view.Decision)}: {Sentence(view.View)}"));
        body.Append(CultureInfo.InvariantCulture, $"Reviewed by Dev flow for #{review.CardNumber}, run {review.RunId}.\n");
        return body.ToString();
    }

    /// <summary>A finding as one line: <c>**Blocking.**</c> when it is, where it is as a code span,
    /// what and why.</summary>
    private static string Finding(ReviewFinding finding)
    {
        var parts = new List<string>();
        if (finding.Blocking)
        {
            parts.Add("**Blocking.**");
        }

        if (finding.File.Trim().Length > 0)
        {
            parts.Add(PullRequestText.Code(finding.Line > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{finding.File}:{finding.Line}")
                : finding.File));
        }

        parts.Add(Sentence(finding.What));
        if (PullRequestText.Text(finding.Why).Length > 0)
        {
            parts.Add("Why: " + Sentence(finding.Why));
        }

        return string.Join(' ', parts.Where(part => part.Length > 0));
    }

    /// <summary>The text made data, ending as a sentence does.</summary>
    private static string Sentence(string text)
    {
        var said = PullRequestText.Text(text);
        return said.Length == 0 || said[^1] is '.' or '!' or '?' ? said : said + ".";
    }

    /// <summary>Each item as a list item, or <c>None.</c> when there is none.</summary>
    private static void List(StringBuilder body, IEnumerable<string> items)
    {
        var any = false;
        foreach (var item in items)
        {
            body.Append("- ").Append(item).Append('\n');
            any = true;
        }

        body.Append(any ? "\n" : "None.\n\n");
    }
}