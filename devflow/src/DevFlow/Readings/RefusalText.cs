using System.Globalization;
using System.Text;

using DevFlow.PullRequests;

namespace DevFlow.Readings;

/// <summary>
/// The comment the app posts once on a card read as too thin: that Dev flow will not build it as it
/// is written, each thing it lacks on a line of its own, that an edit of it or a comment on it reads
/// it again, a link to the card's page, and a last line naming the card and the reading, by which a
/// copy already posted is found. What is missing is the reader's, an agent's words, and is made data
/// by <see cref="PullRequestText"/>, as a stop's reason is. See "Reading a card" under "Jobs" in
/// devflow/README.md.
/// </summary>
public static class RefusalText
{
    /// <summary>The line that says what reads it again, the last the owner reads.</summary>
    public const string ReadAgain = "Edit the card, or comment here, and it is read again.";

    /// <summary>The comment on card <paramref name="number"/> for the reading
    /// <paramref name="runId"/>, which found <paramref name="missing"/> missing, with a newline at its
    /// end.</summary>
    public static string Comment(int number, Guid runId, IReadOnlyList<string> missing, Uri page)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(page);
        var body = new StringBuilder("Dev flow read this card and will not build it as it is written: it is too thin. It lacks:\n\n");
        foreach (var line in missing)
        {
            body.Append("- ").Append(PullRequestText.Text(line)).Append('\n');
        }

        body.Append('\n').Append(ReadAgain).Append("\n\n");
        body.Append(CultureInfo.InvariantCulture, $"The reading is on [the card's page]({page}).\n\n");
        body.Append(Trail(number, runId)).Append('\n');
        return body.ToString();
    }

    /// <summary>The comment's last line, which names the card and the reading.</summary>
    public static string Trail(int number, Guid runId) =>
        string.Create(CultureInfo.InvariantCulture, $"Refused by Dev flow for #{number}, reading {runId}.");
}