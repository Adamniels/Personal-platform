using System.Text.Json;

using Dapper;

using DevFlow.Protocol;
using DevFlow.Reviews;

using PlatformData;

namespace DevFlow.Cards;

/// <summary>A card's pull request, as the card's page links it.</summary>
/// <param name="Number">Its number.</param>
/// <param name="Url">Where it is on the code host.</param>
public sealed record CardPullRequest(int Number, string Url);

/// <summary>The check a run's worker ran, and the last lines of what it printed.</summary>
public sealed record RunCheck(int ExitCode, string Output);

/// <summary>One run of a card, as its page shows it. Every text from a worker or an agent is shown
/// as text.</summary>
/// <param name="Kind"><c>build</c> or <c>review</c>.</param>
/// <param name="Worker">The name the worker said hello with, its container's hostname.</param>
/// <param name="StartedAt">When it was handed out.</param>
/// <param name="FinishedAt">When its result came back or it was found lost, null while it
/// runs.</param>
/// <param name="DurationMs">How long the job took, as its worker measured it, null without a
/// result.</param>
/// <param name="Result">How it ended, as the run records it, null while it runs.</param>
/// <param name="Reason">Why it ended so, null on a success.</param>
/// <param name="InCard">For a failure, whether it lay in the card, so the card has to change, or
/// outside it, so another try may succeed; null for a run that succeeded or still runs.</param>
/// <param name="AuthMethod">How Claude Code was logged in, as it said, null when it did not
/// run.</param>
/// <param name="InputTokens">The tokens it read, null when Claude gave none.</param>
/// <param name="OutputTokens">The tokens it wrote, null when Claude gave none.</param>
/// <param name="Check">The check, null when it did not run.</param>
/// <param name="HeadCommit">The commit the run left on the branch, null when it left none.</param>
public sealed record CardRun(
    Guid Id,
    string Kind,
    string Worker,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long? DurationMs,
    string? Result,
    string? Reason,
    bool? InCard,
    string? AuthMethod,
    long? InputTokens,
    long? OutputTokens,
    RunCheck? Check,
    string? HeadCommit);

/// <summary>A review of the card, as its page shows it: the verdict Dev flow kept, for the commit the
/// worker reviewed. Every text of the reviewer's is shown as text.</summary>
/// <param name="RunId">The review run that read it, among the runs.</param>
/// <param name="Commit">The commit the worker reviewed.</param>
/// <param name="Verdict"><c>approve</c> or <c>changes</c>.</param>
/// <param name="Summary">What the reviewer found.</param>
/// <param name="Findings">Each finding, blocking or not, with its file, line, what and why.</param>
/// <param name="ForOwner">The reviewer's view on each decision marked for the owner.</param>
/// <param name="ReviewedAt">When it was recorded.</param>
/// <param name="Posted">Whether its copy is on the pull request yet.</param>
/// <param name="Current">Whether it is for the card's head commit, so it counts; a review of an
/// older commit does not.</param>
public sealed record CardReview(
    Guid RunId,
    string Commit,
    string Verdict,
    string Summary,
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<ReviewOwnerView> ForOwner,
    DateTimeOffset ReviewedAt,
    bool Posted,
    bool Current);

/// <summary>What <c>GET /devflow/api/projects/{id}/cards/{number}</c> answers: a card's whole story
/// so far.</summary>
/// <param name="Title">The issue's title as last read.</param>
/// <param name="WebUrl">Where the issue is on the code host.</param>
/// <param name="State">The card's state, as the cards list says it.</param>
/// <param name="Reason">Why it is there, null when there is nothing to say.</param>
/// <param name="PullRequest">Its pull request, null before one was opened.</param>
/// <param name="Runs">Every run, newest first.</param>
/// <param name="Report">The latest report in its form, null when no run has handed one
/// back.</param>
/// <param name="Reviews">Every review, newest first.</param>
public sealed record CardStoryView(
    int Number,
    string Title,
    string WebUrl,
    string State,
    string? Reason,
    CardPullRequest? PullRequest,
    IReadOnlyList<CardRun> Runs,
    Report? Report,
    IReadOnlyList<CardReview> Reviews);

/// <summary>
/// A card's story, read from what Dev flow stored and never from the code host, through the door
/// as the user, so another user's card is not found. See "A card's page" in devflow/README.md.
/// </summary>
public static class CardStory
{
    /// <summary>The results of a run that failed in the card, which has to change for another
    /// try to succeed.</summary>
    private static readonly string[] InCard = ["failed_in_card", "report_invalid", "stopped", "check_failed"];

    /// <summary>The results of a run that failed outside the card, which another try may get
    /// past.</summary>
    private static readonly string[] OutsideCard = ["failed_outside_card", "lost"];

    /// <summary>The user's card with the number in their project, or null when they have no such
    /// project or card.</summary>
    public static async Task<CardStoryView?> Of(UserDb database, Guid userId, Guid projectId, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        return await database.InUserTransaction<CardStoryView?>(userId, async transaction =>
        {
            var card = await transaction.Connection!.QuerySingleOrDefaultAsync<CardRow>(new CommandDefinition(
                """
                SELECT number AS Number, title AS Title, web_url AS WebUrl, state AS State, reason AS Reason,
                       pull_request_number AS PullRequestNumber, pull_request_url AS PullRequestUrl
                FROM cards WHERE project_id = @projectId AND number = @number
                """,
                new { projectId, number },
                transaction,
                cancellationToken: cancellationToken));
            if (card is null)
            {
                return null;
            }

            var runs = (await transaction.Connection!.QueryAsync<RunRow>(new CommandDefinition(
                """
                SELECT r.id AS Id, r.kind AS Kind, w.name AS Worker, r.started_at AS StartedAt, r.finished_at AS FinishedAt,
                       r.duration_ms AS DurationMs, r.result AS Result, r.reason AS Reason, r.auth_method AS AuthMethod,
                       r.input_tokens AS InputTokens, r.output_tokens AS OutputTokens, r.check_exit_code AS CheckExitCode,
                       r.check_output AS CheckOutput, r.head_commit AS HeadCommit, r.report::text AS Report
                FROM runs r JOIN workers w ON w.project_id = r.project_id AND w.id = r.worker_id
                WHERE r.project_id = @projectId AND r.card_number = @number
                ORDER BY r.started_at DESC, r.id DESC
                """,
                new { projectId, number },
                transaction,
                cancellationToken: cancellationToken))).ToList();

            // The head is the commit the newest build that succeeded left, as a card's current review
            // reads it; the runs are newest first.
            var head = runs.FirstOrDefault(run => run is { Kind: Job.Build, Result: "succeeded" })?.HeadCommit;
            var reviews = await ReviewStore.OfCard(transaction, projectId, number, cancellationToken);
            return new CardStoryView(
                card.Number,
                card.Title,
                card.WebUrl,
                card.State,
                card.Reason,
                card.PullRequestNumber is { } pull && card.PullRequestUrl is { } url ? new CardPullRequest(pull, url) : null,
                [.. runs.Select(Run)],
                runs.Where(run => run.Kind == Job.Build).Select(run => Read(run.Report)).FirstOrDefault(report => report is not null),
                [.. reviews.Select(review => new CardReview(
                    review.RunId,
                    review.Commit,
                    review.Verdict.Word,
                    review.Verdict.Summary,
                    review.Verdict.Findings,
                    review.Verdict.ForOwner,
                    review.ReviewedAt,
                    review.PostedAt is not null,
                    string.Equals(review.Commit, head, StringComparison.Ordinal)))]);
        }, cancellationToken);
    }

    private static CardRun Run(RunRow row) => new(
        row.Id,
        row.Kind,
        row.Worker,
        Utc(row.StartedAt),
        row.FinishedAt is { } finished ? Utc(finished) : null,
        row.DurationMs,
        row.Result,
        row.Reason,
        row.Result is { } result && InCard.Contains(result, StringComparer.Ordinal) ? true
        : row.Result is { } outside && OutsideCard.Contains(outside, StringComparer.Ordinal) ? false
        : null,
        row.AuthMethod,
        row.InputTokens,
        row.OutputTokens,
        row.CheckExitCode is { } exit ? new RunCheck(exit, row.CheckOutput ?? "") : null,
        row.HeadCommit);

    /// <summary>A stored report read into its form, or null when there is none or it does not
    /// read, which the run then says.</summary>
    private static Report? Read(string? stored)
    {
        if (stored is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(stored);
        return Report.TryRead(document.RootElement, out var report, out _) ? report : null;
    }

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private sealed record CardRow(int Number, string Title, string WebUrl, string State, string? Reason, int? PullRequestNumber, string? PullRequestUrl);

    private sealed record RunRow(
        Guid Id,
        string Kind,
        string Worker,
        DateTime StartedAt,
        DateTime? FinishedAt,
        long? DurationMs,
        string? Result,
        string? Reason,
        string? AuthMethod,
        long? InputTokens,
        long? OutputTokens,
        int? CheckExitCode,
        string? CheckOutput,
        string? HeadCommit,
        string? Report);
}