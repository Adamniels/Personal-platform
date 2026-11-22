using System.Globalization;
using System.Text;

using DevFlow.PullRequests;

namespace DevFlow.Readings;

/// <summary>
/// The comment the owner's answers are posted in, as him, with his linked token, on the card's
/// issue: each question the reading asked, numbered, with his answer and "(recommended)" where he
/// accepted the reader's recommendation, and a last line naming the card and the reading, by which
/// a copy GitHub took while its answer was lost is found again. Every question is the reader's, an
/// agent's words, and every answer is made data as well, by <see cref="PullRequestText"/>, so
/// neither closes an issue nor mentions anyone. See "Reading a card" under "Jobs" in
/// devflow/README.md.
/// </summary>
public static class AnswerText
{
    /// <summary>The comment on card <paramref name="number"/> answering the reading
    /// <paramref name="runId"/>'s <paramref name="questions"/> with <paramref name="answers"/>, one
    /// for each, in order, with a newline at its end.</summary>
    public static string Comment(int number, Guid runId, IReadOnlyList<Protocol.ReadingQuestion> questions, IReadOnlyList<OwnerAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(answers);
        if (questions.Count != answers.Count)
        {
            throw new ArgumentException("one answer for each question", nameof(answers));
        }

        var body = new StringBuilder("My answers to the questions Dev flow's reading asked of this card before building it.\n\n");
        for (var at = 0; at < questions.Count; at++)
        {
            body.Append(CultureInfo.InvariantCulture, $"{at + 1}. {PullRequestText.Text(questions[at].Question)}\n");
            body.Append("   - Answer: ").Append(PullRequestText.Text(answers[at].Answer));
            body.Append(answers[at].Recommended ? " (recommended)\n" : "\n");
        }

        body.Append('\n').Append(Trail(number, runId)).Append('\n');
        return body.ToString();
    }

    /// <summary>The comment's last line, which names the card and the reading.</summary>
    public static string Trail(int number, Guid runId) =>
        string.Create(CultureInfo.InvariantCulture, $"Answered on Dev flow for #{number}, reading {runId}.");
}