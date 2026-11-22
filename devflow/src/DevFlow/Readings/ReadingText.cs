using System.Globalization;
using System.Text;

using DevFlow.PullRequests;

namespace DevFlow.Readings;

/// <summary>
/// The comment the app posts once on a card read with questions: that it is built on the reader's
/// recommendations, each question with the answer chosen and why, a link to the card's page, and a
/// last line naming the card and the reading, by which a copy already posted is found. Every question,
/// recommendation and why is the reader's, an agent's words, and is made data by
/// <see cref="PullRequestText"/>, as a stop's reason is. See "Jobs" in devflow/README.md.
/// </summary>
public static class ReadingText
{
    /// <summary>The comment on card <paramref name="number"/> for the reading
    /// <paramref name="runId"/>'s <paramref name="questions"/>, with a newline at its end.</summary>
    public static string Comment(int number, Guid runId, IReadOnlyList<CardQuestion> questions, Uri page)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(page);
        var body = new StringBuilder();
        body.Append("Dev flow read this card before building it. It leaves ")
            .Append(questions.Count == 1 ? "a choice" : "choices")
            .Append(" open that the card does not settle, so it is built on the answers chosen below, the reader's recommendations.\n\n");
        var at = 0;
        foreach (var question in questions)
        {
            body.Append(CultureInfo.InvariantCulture, $"{++at}. {PullRequestText.Text(question.Question)}\n");
            body.Append("   - Chosen: ").Append(PullRequestText.Text(question.Answer)).Append('\n');
            body.Append("   - Why: ").Append(PullRequestText.Text(question.Why)).Append('\n');
        }

        body.Append(CultureInfo.InvariantCulture, $"\nThe reading is on [the card's page]({page}).\n\n");
        body.Append(Trail(number, runId)).Append('\n');
        return body.ToString();
    }

    /// <summary>The comment's last line, which names the card and the reading.</summary>
    public static string Trail(int number, Guid runId) =>
        string.Create(CultureInfo.InvariantCulture, $"Read by Dev flow for #{number}, reading {runId}.");
}