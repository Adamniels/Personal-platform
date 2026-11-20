using System.Globalization;
using System.Text;

using DevFlow.PullRequests;

namespace DevFlow.Features;

/// <summary>
/// The body of a feature's pull request to the default branch: "Feature #&lt;parent&gt;.", a Cards
/// section listing each card in the feature's order by number and title with the pull request it
/// landed in, <c>Closes #&lt;parent&gt;</c> on a line of its own, which closes the parent once the
/// feature reaches the default branch, and a last line that leads back to the feature, as a card's
/// pull request names its card and run. Every title is the issue's, data, written by
/// <see cref="PullRequestText.Text"/> as a card's report is, so none can add a heading, hide the
/// sections after it or add a closing line. See "The end of a feature" in devflow/README.md.
/// </summary>
public static class FeatureText
{
    /// <summary>A card as the body lists it: its issue's number, its title as picked, and the
    /// pull request it landed in, null when none is recorded.</summary>
    public sealed record Card(int Number, string Title, int? PullRequest);

    /// <summary>The body, with a newline at its end.</summary>
    public static string Body(int parent, IReadOnlyList<Card> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"Feature #{parent}.\n\n");
        body.Append("## Cards\n\n");
        foreach (var card in cards)
        {
            body.Append(CultureInfo.InvariantCulture, $"- #{card.Number} {PullRequestText.Text(card.Title)}");
            if (card.PullRequest is { } pull)
            {
                body.Append(CultureInfo.InvariantCulture, $", landed in #{pull}");
            }

            body.Append('\n');
        }

        body.Append(cards.Count == 0 ? "None.\n\n" : "\n");
        body.Append(CultureInfo.InvariantCulture, $"Closes #{parent}\n\n");
        body.Append(CultureInfo.InvariantCulture, $"Built by Dev flow for the feature #{parent}.\n");
        return body.ToString();
    }
}