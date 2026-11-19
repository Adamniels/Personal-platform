using System.Globalization;
using System.Text;

using DevFlow.PullRequests;

namespace DevFlow.Stops;

/// <summary>
/// The comment the app posts once on a stopped card's issue: <c>Dev flow stopped this card:
/// &lt;reason&gt;.</c>, what was tried, the last run's outcome, a link to the card's page, how to run
/// it again, and a last line naming the card and the stop, by which a copy already posted is found.
/// Every reason came from a run, and so from a worker or an agent, and is made data by
/// <see cref="PullRequestText"/>, as the review's copy is. See "Stops" in devflow/README.md.
/// </summary>
public static class StopText
{
    /// <summary>The line that asks for a comment, the last the owner reads.</summary>
    public const string RunAgain = "Comment here to run it again.";

    /// <summary>The comment for card <paramref name="number"/>'s stop at
    /// <paramref name="stoppedAt"/>, for <paramref name="reason"/>, after <paramref name="count"/>
    /// runs, of which <paramref name="newest"/> are the newest, oldest first; with a newline at its
    /// end.</summary>
    public static string Comment(int number, DateTimeOffset stoppedAt, string reason, int count, IReadOnlyList<TriedRun> newest, Uri page)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(newest);
        ArgumentNullException.ThrowIfNull(page);
        var body = new StringBuilder();
        body.Append("Dev flow stopped this card: ").Append(Sentence(reason)).Append("\n\n");
        body.Append("## What was tried\n\n");
        if (newest.Count == 0)
        {
            body.Append("No run.\n\n");
        }
        else
        {
            if (count > newest.Count)
            {
                body.Append(CultureInfo.InvariantCulture, $"{count} runs, the newest {newest.Count} here:\n\n");
            }

            foreach (var run in newest)
            {
                body.Append("- ").Append(Line(run)).Append('\n');
            }

            body.Append('\n');
            body.Append("The last run: ").Append(Line(newest[^1])).Append("\n\n");
        }

        body.Append(CultureInfo.InvariantCulture, $"Its whole story is on [the card's page]({page}).\n\n");
        body.Append(RunAgain).Append("\n\n");
        body.Append(Trail(number, stoppedAt)).Append('\n');
        return body.ToString();
    }

    /// <summary>The comment's last line, which names the card and the stop.</summary>
    public static string Trail(int number, DateTimeOffset stoppedAt) =>
        string.Create(CultureInfo.InvariantCulture, $"Stopped by Dev flow for #{number}, stop {stoppedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ss.ffffffZ}.");

    /// <summary>A run as one line: its kind, how it ended, and why.</summary>
    private static string Line(TriedRun run)
    {
        var kind = run.Kind switch
        {
            "review" => "Review",
            "fix" => "Fix",
            _ => "Build",
        };
        var ended = run.Result switch
        {
            null => "under way",
            "succeeded" => "succeeded",
            "failed_in_card" => "failed in the card",
            "failed_outside_card" => "failed outside the card",
            "report_invalid" => "its report did not match its form",
            "stopped" => "the agent stopped",
            "check_failed" => "its check failed",
            "lost" => "lost",
            var other => other,
        };
        var why = Sentence(run.Reason ?? "");
        return why.Length == 0 ? $"{kind}, {ended}." : $"{kind}, {ended}: {why}";
    }

    /// <summary>The text made data, ending as a sentence does.</summary>
    private static string Sentence(string text)
    {
        var said = PullRequestText.Text(text);
        return said.Length == 0 || said[^1] is '.' or '!' or '?' ? said : said + ".";
    }
}