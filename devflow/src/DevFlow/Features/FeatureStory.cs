using Dapper;

using DevFlow.Cards;
using DevFlow.Jobs;
using DevFlow.Protocol;
using DevFlow.Reviews;

using PlatformData;

namespace DevFlow.Features;

/// <summary>A feature's branch, as its page shows it.</summary>
/// <param name="Name">The branch, which the page's command checks out.</param>
/// <param name="Commit">The commit it was at after the newest change its watch judged, or the one it
/// was made at before any; null for a watch that has recorded neither yet.</param>
public sealed record FeatureBranch(string Name, string? Commit);

/// <summary>Your answer to a decision, kept in Dev flow and never written to the code host.</summary>
/// <param name="Answer"><c>fine</c>, "Fine as it is", the one this part gives.</param>
/// <param name="AnsweredAt">When it was first given.</param>
public sealed record DecisionAnswer(string Answer, DateTimeOffset AnsweredAt);

/// <summary>A decision a card's latest report marked for the owner, as the feature's page shows it.
/// Every text is the agent's or the reviewer's and shown as text.</summary>
/// <param name="RunId">The run whose report it is, among the card's runs.</param>
/// <param name="Position">Its place among that report's decisions, from 0, the one marked or
/// not.</param>
/// <param name="Chosen">What the agent chose.</param>
/// <param name="Alternative">What it chose against.</param>
/// <param name="Why">Why.</param>
/// <param name="ReviewerView">The view on it of the newest review of the commit that run left,
/// matched by what was chosen; null when there is none.</param>
/// <param name="Answer">Your answer, null before one.</param>
public sealed record FeatureDecision(
    Guid RunId,
    int Position,
    string Chosen,
    string Alternative,
    string Why,
    string? ReviewerView,
    DecisionAnswer? Answer);

/// <summary>One card of a feature, as its page lists it.</summary>
/// <param name="Number">The issue's number.</param>
/// <param name="Title">The issue's title as last read.</param>
/// <param name="WebUrl">Where the issue is on the code host.</param>
/// <param name="State">The card's state, as the cards list says it.</param>
/// <param name="Reason">Why it is there, null when there is nothing to say.</param>
/// <param name="FixRounds">The fix rounds its reviews have started, 0 to 3.</param>
/// <param name="InputTokens">The tokens its runs read, every kind, 0 before any.</param>
/// <param name="OutputTokens">The tokens its runs wrote, every kind, 0 before any.</param>
/// <param name="Screenshots">The pictures of its head commit, as its own page has them, read
/// through <c>getCardScreenshot</c>; null when its newest build or fix that succeeded tried
/// none.</param>
/// <param name="Decisions">The decisions its latest report marked for the owner, in the report's
/// order.</param>
public sealed record FeatureCard(
    int Number,
    string Title,
    string WebUrl,
    string State,
    string? Reason,
    int FixRounds,
    long InputTokens,
    long OutputTokens,
    CardScreenshots? Screenshots,
    IReadOnlyList<FeatureDecision> Decisions);

/// <summary>What <c>GET /devflow/api/projects/{id}/features/{number}</c> answers: a feature's whole
/// story so far.</summary>
/// <param name="Number">Its parent issue's number.</param>
/// <param name="Title">The parent's title as last read.</param>
/// <param name="WebUrl">Where the parent is on the code host.</param>
/// <param name="State">The feature's state.</param>
/// <param name="Reason">Why it is there, null when there is nothing to say.</param>
/// <param name="Branch">Its branch, null before it is made.</param>
/// <param name="PullRequest">Its pull request to the default branch, null before it is
/// opened.</param>
/// <param name="Cards">Its cards, in their sub-issue order.</param>
public sealed record FeatureStoryView(
    int Number,
    string Title,
    string WebUrl,
    string State,
    string? Reason,
    FeatureBranch? Branch,
    CardPullRequest? PullRequest,
    IReadOnlyList<FeatureCard> Cards);

/// <summary>
/// A feature's story, read from what Dev flow stored and never from the code host, through the
/// door as the user, so another user's feature is not found. See "A feature's page" in
/// devflow/README.md.
/// </summary>
public static class FeatureStory
{
    /// <summary>The user's feature with the number in their project, or null when they have no
    /// such project or feature.</summary>
    public static async Task<FeatureStoryView?> Of(UserDb database, Guid userId, Guid projectId, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction<FeatureStoryView?>(userId, async transaction =>
        {
            var feature = await transaction.Connection!.QuerySingleOrDefaultAsync<FeatureRow>(new CommandDefinition(
                """
                SELECT f.id AS Id, f.number AS Number, f.title AS Title, f.web_url AS WebUrl, f.state AS State, f.reason AS Reason,
                       CASE WHEN f.branch_base_commit IS NULL THEN NULL ELSE f.branch END AS Branch,
                       coalesce(b.last_commit, f.branch_base_commit) AS BranchCommit,
                       f.pull_request_number AS PullRequestNumber, f.pull_request_url AS PullRequestUrl
                FROM features f
                LEFT JOIN branch_watches b ON b.project_id = f.project_id AND b.feature_id = f.id
                WHERE f.project_id = @projectId AND f.number = @number
                """,
                new { projectId, number },
                transaction,
                cancellationToken: cancellationToken));
            if (feature is null)
            {
                return null;
            }

            var cards = (await transaction.Connection!.QueryAsync<CardRow>(new CommandDefinition(
                $"""
                SELECT number AS Number, title AS Title, web_url AS WebUrl, state AS State, {CardStore.ShownReason} AS Reason,
                       fix_rounds AS FixRounds
                FROM cards WHERE project_id = @projectId AND feature_id = @feature
                ORDER BY position
                """,
                new { projectId, feature = feature.Id },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            var numbers = cards.Select(card => card.Number).ToArray();
            var runs = (await transaction.Connection!.QueryAsync<RunRow>(new CommandDefinition(
                """
                SELECT id AS Id, card_number AS CardNumber, kind AS Kind, result AS Result, head_commit AS HeadCommit,
                       report::text AS Report, input_tokens AS InputTokens, output_tokens AS OutputTokens,
                       screenshots::text AS Screenshots, screenshots_note AS ScreenshotsNote
                FROM runs WHERE project_id = @projectId AND card_number = ANY (@numbers)
                ORDER BY started_at DESC, id DESC
                """,
                new { projectId, numbers },
                transaction,
                cancellationToken: cancellationToken))).ToLookup(run => run.CardNumber);
            var shown = new List<FeatureCard>(cards.Count);
            foreach (var card in cards)
            {
                var ofCard = runs[card.Number].ToList();
                var built = ofCard.FirstOrDefault(run => run is { Kind: Job.Build or Job.Fix, Result: "succeeded" });
                var reviews = await ReviewStore.OfCard(transaction, projectId, card.Number, cancellationToken);
                shown.Add(new FeatureCard(
                    card.Number,
                    card.Title,
                    card.WebUrl,
                    card.State,
                    card.Reason,
                    card.FixRounds,
                    ofCard.Sum(run => run.InputTokens ?? 0),
                    ofCard.Sum(run => run.OutputTokens ?? 0),
                    CardScreenshots.Of(built?.HeadCommit, built?.Screenshots, built?.ScreenshotsNote),
                    Decisions(ofCard, reviews)));
            }

            return new FeatureStoryView(
                feature.Number,
                feature.Title,
                feature.WebUrl,
                feature.State,
                feature.Reason,
                feature.Branch is { } branch ? new FeatureBranch(branch, feature.BranchCommit) : null,
                feature.PullRequestNumber is { } pull && feature.PullRequestUrl is { } url ? new CardPullRequest(pull, url) : null,
                shown);
        }, cancellationToken);
    }

    /// <summary>
    /// The decisions marked for the owner in the card's latest report, the newest build's or fix's
    /// that reads, as the card's page shows it, see <see cref="CardStory.Read"/>; each with
    /// the view on it of the newest review of the commit that run left, matched by what was chosen
    /// whatever its case and surrounding space, as a merge's decisions are.
    /// </summary>
    private static List<FeatureDecision> Decisions(
        IReadOnlyList<RunRow> runs, IReadOnlyList<KeptReview> reviews)
    {
        var latest = runs.Where(run => run.Kind is Job.Build or Job.Fix)
            .Select(run => (Run: run, Report: CardStory.Read(run.Report)))
            .FirstOrDefault(each => each.Report is not null);
        if (latest.Report is not { } report)
        {
            return [];
        }

        var review = latest.Run.HeadCommit is { } commit ? reviews.FirstOrDefault(each => each.Commit == commit) : null;
        return [.. report.Decisions
            .Select((decision, position) => (Decision: decision, Position: position))
            .Where(each => each.Decision.ForOwner)
            .Select(each => new FeatureDecision(
                latest.Run.Id,
                each.Position,
                each.Decision.Chosen,
                each.Decision.Alternative,
                each.Decision.Why,
                review?.Verdict.ForOwner.FirstOrDefault(view => SameDecision(view.Decision, each.Decision.Chosen))?.View is { } view
                    && !string.IsNullOrWhiteSpace(view) ? view : null,
                null))];
    }

    private static bool SameDecision(string one, string other) =>
        string.Equals(one.Trim(), other.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed record FeatureRow(
        Guid Id, int Number, string Title, string WebUrl, string State, string? Reason, string? Branch, string? BranchCommit, int? PullRequestNumber, string? PullRequestUrl);

    private sealed record CardRow(int Number, string Title, string WebUrl, string State, string? Reason, int FixRounds);

    private sealed record RunRow(
        Guid Id, int CardNumber, string Kind, string? Result, string? HeadCommit, string? Report, long? InputTokens, long? OutputTokens, string? Screenshots, string? ScreenshotsNote);
}