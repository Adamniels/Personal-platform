using System.Globalization;
using System.Text;

using DevFlow.PullRequests;

namespace DevFlow.Decisions;

/// <summary>
/// What the issue Dev flow opens for a decision says. Everything in it but Dev flow's own words is
/// the agent's or the reviewer's, and data, made safe as the pull request body makes the report's
/// text, see <see cref="PullRequestText"/>, so it closes no issue and notifies nobody. See "The
/// merge check" in devflow/README.md.
/// </summary>
public static class DecisionText
{
    /// <summary>The most characters of what was chosen the title holds.</summary>
    public const int MostInTitle = 80;

    /// <summary>"Decision in #&lt;pull request&gt;: &lt;chosen&gt;", what was chosen made one line
    /// and cut to <see cref="MostInTitle"/> characters, one fewer where the cut would split a
    /// surrogate pair. GitHub reads no mention or closing keyword in a title.</summary>
    public static string Title(int pullRequest, string chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var line = new StringBuilder(chosen.Length);
        foreach (var c in chosen)
        {
            line.Append(char.IsControl(c) || c is '\u2028' or '\u2029' or '\u0085' ? ' ' : c);
        }

        var said = line.ToString().Trim();
        if (said.Length > MostInTitle)
        {
            said = said[..(char.IsHighSurrogate(said[MostInTitle - 1]) ? MostInTitle - 1 : MostInTitle)];
        }

        return string.Create(CultureInfo.InvariantCulture, $"Decision in #{pullRequest}: {said}");
    }

    /// <summary>The body: what Dev flow did, the decision with its alternative, why and the
    /// reviewer's view, links to the card and the merged pull request, what is left to the owner,
    /// and a last line naming the decision, which finds an issue already opened for it.</summary>
    public static string Body(UnopenedDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"Dev flow merged #{decision.PullRequestNumber} for card #{decision.CardNumber}. The agent made this decision and marked it for you.\n\n");
        body.Append(CultureInfo.InvariantCulture, $"- **Chosen.** {Said(decision.Chosen)}\n");
        body.Append(CultureInfo.InvariantCulture, $"- **Alternative.** {Said(decision.Alternative)}\n");
        body.Append(CultureInfo.InvariantCulture, $"- **Why.** {Said(decision.Why)}\n");
        body.Append(CultureInfo.InvariantCulture, $"- **The reviewer's view.** {(decision.ReviewerView is { } view ? Said(view) : "The reviewer gave none.")}\n\n");
        body.Append(CultureInfo.InvariantCulture, $"The card: {Link(decision.CardUrl)}\n");
        body.Append(CultureInfo.InvariantCulture, $"The merged pull request: {Link(decision.PullRequestUrl)}\n\n");
        body.Append("Whether it stands is yours to judge. A fix is a new card. Close this issue once you have looked; Dev flow lists it on Waiting for you until then.\n\n");
        body.Append(Marker(decision.Id));
        return body.ToString();
    }

    /// <summary>The body's last line, which names the decision by its id.</summary>
    public static string Marker(Guid id) => $"Opened by Dev flow for decision {id}.";

    /// <summary>The agent's or the reviewer's text, made data, or a word for nothing.</summary>
    private static string Said(string text) => PullRequestText.Text(text) is { Length: > 0 } said ? said : "None given.";

    /// <summary>A code host's own address, as it was stored: kept as it is when it is an
    /// <c>https</c> address, which GitHub links, and made data otherwise.</summary>
    private static string Link(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps && !url.Any(char.IsWhiteSpace)
            ? url
            : PullRequestText.Text(url);
}