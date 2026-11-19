using System.Text.Json;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Jobs;
using DevFlow.Protocol;

using PlatformData;

namespace DevFlow.PullRequests;

/// <summary>
/// Opens the pull request of each card of a project whose run built it and passed its check, as the
/// app with a <see cref="WritePurpose.Post"/> key, from the card's branch into the branch it was
/// built on, and moves the card to <c>reviewing</c> with its number, since its head commit has no
/// review yet: its review is the project's next job. A pull request the app
/// already has open for the branch is reused, so opening it again, after an answer that was lost or
/// on the next minute, never opens a second. Nothing is held while the code host answers: the card
/// is recorded only after, and only while it is still built and has none, so two openings at once
/// record one. A card the code host did not answer for is left as it is, and the next minute tries
/// again. It writes, so it lives outside <c>Reading/</c>, which can never reach a write key. See
/// "The pull request Dev flow opens" in devflow/README.md and "How a card reaches its pull request"
/// in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class PullRequestOpener(UserDb database, ICodeHost host, TimeProvider time, ILogger<PullRequestOpener> log)
{
    /// <summary>The reason of a card whose pull request is open and waits for its review.</summary>
    public const string ToReview = "Its pull request is open; the reviewer reads it next";

    /// <summary>
    /// Opens the pull request of every card of the project that is waiting for one, as the
    /// project's user, <paramref name="userId"/>. Never throws for what the code host answers or
    /// for one card's failure, which is logged and tried again on the next call; only cancellation
    /// and a database that cannot be reached escape.
    /// </summary>
    public async Task OpenPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var (repository, pending) = await Pending(projectId, userId, cancellationToken);
        if (repository is null || pending.Count == 0)
        {
            return;
        }

        // Who the app is on the code host, so only its own pull request is ever reused: one from a
        // fork, or by anyone else, can carry the same branch name.
        if (await host.AppCommitter(userId, repository, cancellationToken) is not CodeHostResult<Committer>.Found { Value: var app })
        {
            LogNotNow(log, projectId, "who the app is");
            return;
        }

        AppWriteToken? key = null;
        foreach (var card in pending)
        {
            try
            {
                if (await Existing(userId, repository, card, app.Name, cancellationToken) is { } open)
                {
                    await Record(projectId, userId, card, open, cancellationToken);
                    continue;
                }

                key ??= await Key(userId, repository, cancellationToken);
                if (key is null)
                {
                    LogNotNow(log, projectId, "a key to post with");
                    return;
                }

                var pictures = card.Screenshots is { } taken ? new PullRequestPictures(repository.Owner, repository.Name, taken.Names ?? [], taken.Note) : null;
                var body = PullRequestText.Body(new PullRequestSource(card.Number, card.RunId, card.Report, card.Check, card.HeadCommit, card.ExitCode, pictures));
                switch (await host.OpenPullRequest(userId, key, repository, card.Branch, card.Base, card.Title, body, cancellationToken))
                {
                    case WriteResult<PullRequest>.Written { Value: var opened }:
                        await Record(projectId, userId, card, opened, cancellationToken);
                        LogOpened(log, projectId, card.Number, opened.Number);
                        break;
                    case WriteResult<PullRequest>.Refused { Status: var status }:
                        // GitHub refuses a second pull request from the same branch into the same
                        // base, so one may have been opened meanwhile, by an opening at the same
                        // moment or one whose answer was lost.
                        if (await Existing(userId, repository, card, app.Name, cancellationToken) is { } raced)
                        {
                            await Record(projectId, userId, card, raced, cancellationToken);
                        }
                        else
                        {
                            LogRefused(log, projectId, card.Number, status);
                        }

                        break;
                    case WriteResult<PullRequest>.Unavailable:
                        // Whether it was opened is not known; the next opening lists it if it was.
                        LogNotNow(log, projectId, "the pull request");
                        break;
                    default:
                        throw new InvalidOperationException("a write result that is not one of the three");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, card.Number, error);
            }
        }
    }

    /// <summary>The app's own open pull request from the card's branch into its base, or null when
    /// there is none, or the code host did not answer.</summary>
    private async Task<PullRequest?> Existing(Guid userId, Repository repository, PendingCard card, string app, CancellationToken cancellationToken) =>
        await host.OpenPullRequests(userId, repository, cancellationToken) is CodeHostResult<IReadOnlyList<PullRequest>>.Found { Value: var open }
            ? open.Where(pull => pull.State == PullRequestState.Open
                    && string.Equals(pull.HeadBranch, card.Branch, StringComparison.Ordinal)
                    && string.Equals(pull.BaseBranch, card.Base, StringComparison.Ordinal)
                    && string.Equals(pull.Author, app, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pull => pull.Number)
                .FirstOrDefault()
            : null;

    private async Task<AppWriteToken?> Key(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        await host.ForWrite(userId, repository, WritePurpose.Post, cancellationToken) is CodeHostResult<AppWriteToken>.Found { Value: var key } ? key : null;

    /// <summary>The project's repository and its cards waiting for a pull request: built, checked
    /// and with none, whose latest run succeeded with a report in its form. Null for a project the
    /// user does not have.</summary>
    private async Task<(Repository? Repository, IReadOnlyList<PendingCard> Pending)> Pending(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction<(Repository?, IReadOnlyList<PendingCard>)>(userId, async transaction =>
        {
            var project = await transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
                "SELECT installation_id AS Installation, repository_id AS RepositoryId, owner AS Owner, name AS Name FROM projects WHERE id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            if (project is null)
            {
                return (null, []);
            }

            var rows = await transaction.Connection!.QueryAsync<PendingRow>(new CommandDefinition(
                """
                SELECT c.number AS Number, coalesce(c.picked_title, c.title) AS Title, r.id AS RunId, r.branch AS Branch,
                       r.base AS Base, r.head_commit AS HeadCommit, r.report::text AS Report, r.check_command AS CheckCommand,
                       r.check_exit_code AS ExitCode, r.screenshots::text AS Screenshots, r.screenshots_note AS ScreenshotsNote
                FROM cards c
                JOIN LATERAL (SELECT * FROM runs WHERE runs.project_id = c.project_id AND runs.card_number = c.number
                              ORDER BY started_at DESC, id DESC LIMIT 1) r ON true
                WHERE c.project_id = @projectId AND c.state = 'checking' AND c.pull_request_number IS NULL
                  AND r.result = 'succeeded' AND r.head_commit IS NOT NULL AND r.check_exit_code = 0
                ORDER BY c.number
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            var pending = new List<PendingCard>();
            foreach (var row in rows)
            {
                // Judged succeeded only with a report in its form, so this reads; one that does not
                // is skipped rather than opened with a body made up.
                using var document = JsonDocument.Parse(row.Report ?? "null");
                if (Report.TryRead(document.RootElement, out var report, out _))
                {
                    pending.Add(new PendingCard(row.Number, row.Title, row.RunId, row.Branch, row.Base, row.HeadCommit!, report, row.CheckCommand, row.ExitCode!.Value, JobStore.Pictures(row.Screenshots, row.ScreenshotsNote)));
                }
            }

            return (new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name), pending);
        }, cancellationToken);

    /// <summary>The card's pull request recorded and the card moved to <c>reviewing</c>, only while
    /// it is still built and has none, and its latest run is the one that built it.</summary>
    private async Task Record(Guid projectId, Guid userId, PendingCard card, PullRequest pull, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % 10));
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            """
            UPDATE cards SET state = 'reviewing', reason = @reason, pull_request_number = @pull, pull_request_url = @url, changed_at = @now
            WHERE project_id = @projectId AND number = @number AND state = 'checking' AND pull_request_number IS NULL
              AND (SELECT id FROM runs WHERE runs.project_id = cards.project_id AND runs.card_number = cards.number
                   ORDER BY started_at DESC, id DESC LIMIT 1) = @runId
            """,
            new { projectId, number = card.Number, runId = card.RunId, pull = pull.Number, url = pull.Url.ToString(), reason = ToReview, now },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened pull request {Pull} for card {Card} of project {Project}")]
    private static partial void LogOpened(ILogger logger, Guid project, int card, int pull);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host did not answer {What} for project {Project}; its built cards' pull requests open on a later minute")]
    private static partial void LogNotNow(ILogger logger, Guid project, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host refused to open the pull request of card {Card} of project {Project}, with {Status}; it is tried again on a later minute")]
    private static partial void LogRefused(ILogger logger, Guid project, int card, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Opening the pull request of card {Card} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int card, Exception failed);

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name);

    private sealed record PendingRow(int Number, string Title, Guid RunId, string Branch, string Base, string? HeadCommit, string? Report, string? CheckCommand, int? ExitCode, string? Screenshots, string? ScreenshotsNote);

    private sealed record PendingCard(int Number, string Title, Guid RunId, string Branch, string Base, string HeadCommit, Report Report, string? Check, int ExitCode, ResultScreenshots? Screenshots);
}