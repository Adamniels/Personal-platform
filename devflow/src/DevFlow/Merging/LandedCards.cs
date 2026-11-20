using System.Globalization;

using Dapper;

using DevFlow.CodeHost;

using PlatformData;

namespace DevFlow.Merging;

/// <summary>
/// Tells each card of a feature that landed in the feature's branch on its issue, as the app with a
/// <see cref="WritePurpose.Post"/> key: one comment saying where it landed, <see cref="Comment"/>,
/// then the issue closed, since GitHub's closing keywords close an issue only from a pull request
/// into the default branch. A card is due once it is <c>done</c> with a pull request it was not told
/// of, whoever merged it, Dev flow, the owner from the page or anyone on GitHub. Its pull request is
/// read first, and only one merged into the feature's branch is told: one whose base was changed and
/// merged elsewhere landed nowhere this comment could name, so it is marked told and left alone.
/// Before commenting, the app's own comments on the issue are looked at for the same words, so an
/// answer that was lost never posts it twice. Nothing is held while the code host answers, and what
/// it did not answer for is tried again on the next minute. It writes, so it lives outside
/// <c>Reading/</c>, which can never reach a write key. See "The merge check" in devflow/README.md.
/// </summary>
public sealed partial class LandedCards(UserDb database, ICodeHost host, ILogger<LandedCards> log)
{
    /// <summary>What the app says on a card's issue when it lands in its feature's branch.</summary>
    public static string Comment(string branch, int pullRequest, string defaultBranch, int feature) =>
        string.Create(CultureInfo.InvariantCulture, $"Built and merged into the feature's branch `{branch}` in #{pullRequest}. It reaches `{defaultBranch}` with its feature, #{feature}.");

    /// <summary>
    /// Tells every landed card of the project still untold, as the project's user,
    /// <paramref name="userId"/>. Never throws for what the code host answers or for one card's
    /// failure, which is logged and tried again on the next call; only cancellation and a database
    /// that cannot be reached escape.
    /// </summary>
    public async Task TellPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var (repository, pending) = await Pending(projectId, userId, cancellationToken);
        if (repository is null || pending.Count == 0)
        {
            return;
        }

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
                switch (await host.PullRequest(userId, repository, card.PullRequest, cancellationToken))
                {
                    case CodeHostResult<PullRequest>.Found { Value: { State: PullRequestState.Merged } pull } when string.Equals(pull.BaseBranch, card.Branch, StringComparison.Ordinal):
                        break;
                    case CodeHostResult<PullRequest>.Found { Value: { State: PullRequestState.Merged } pull }:
                        // Merged, but not where the comment says; the watch judges what it changed.
                        LogElsewhere(log, projectId, card.Number, card.PullRequest, pull.BaseBranch);
                        await Told(projectId, userId, card, cancellationToken);
                        continue;
                    default:
                        LogNotNow(log, projectId, "the card's pull request");
                        continue;
                }

                key ??= await host.ForWrite(userId, repository, WritePurpose.Post, cancellationToken) is CodeHostResult<AppWriteToken>.Found { Value: var made } ? made : null;
                if (key is null)
                {
                    LogNotNow(log, projectId, "a key to post with");
                    return;
                }

                await Tell(projectId, userId, repository, app.Name, key, card, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, card.Number, error);
            }
        }
    }

    /// <summary>The comment once, then the issue closed; told when both are on the issue.</summary>
    private async Task Tell(Guid projectId, Guid userId, Repository repository, string app, AppWriteToken key, Landed card, CancellationToken cancellationToken)
    {
        var text = Comment(card.Branch, card.PullRequest, card.DefaultBranch, card.Feature);
        switch (await host.Comments(userId, repository, card.Number, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<Comment>>.Found { Value: var comments } when comments.Any(comment =>
                string.Equals(comment.Author, app, StringComparison.OrdinalIgnoreCase) && string.Equals(comment.Body, text, StringComparison.Ordinal)):
                break;
            case CodeHostResult<IReadOnlyList<Comment>>.Unavailable:
                LogNotNow(log, projectId, "the issue's comments");
                return;
            default:
                // None of the app's says it, or the issue has no conversation yet.
                if (!Written(await host.CommentOnIssue(userId, key, repository, card.Number, text, cancellationToken), projectId, card.Number, "the landing's comment"))
                {
                    return;
                }

                break;
        }

        if (Written(await host.SetIssueState(userId, key, repository, card.Number, open: false, cancellationToken), projectId, card.Number, "closing the issue"))
        {
            await Told(projectId, userId, card, cancellationToken);
            LogTold(log, projectId, card.Number, card.Branch);
        }
    }

    /// <summary>Whether the write was made; anything else is logged, and tried again on a later
    /// minute.</summary>
    private bool Written<T>(WriteResult<T> result, Guid projectId, int card, string what)
    {
        switch (result)
        {
            case WriteResult<T>.Written:
                return true;
            case WriteResult<T>.Refused { Status: var status }:
                LogRefused(log, projectId, card, what, status);
                return false;
            case WriteResult<T>.Unavailable:
                LogNotNow(log, projectId, what);
                return false;
            default:
                throw new InvalidOperationException("a write result that is not one of the three");
        }
    }

    /// <summary>Records the card told of its pull request, while it still has that one.</summary>
    private async Task Told(Guid projectId, Guid userId, Landed card, CancellationToken cancellationToken) =>
        await database.InUserTransaction(userId, transaction => transaction.Connection!.ExecuteAsync(new CommandDefinition(
            "UPDATE cards SET landing_told_for = @pull WHERE project_id = @projectId AND number = @number AND pull_request_number = @pull",
            new { projectId, number = card.Number, pull = card.PullRequest },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);

    /// <summary>The project's repository and its cards of a feature that are done with a pull
    /// request they were not told of, each with the feature's number and branch and the default
    /// branch as its watch names it, read as the user. No repository for a project the user does
    /// not have; nothing is due before the default branch's watch has named it.</summary>
    private async Task<(Repository? Repository, IReadOnlyList<Landed> Pending)> Pending(Guid projectId, Guid userId, CancellationToken cancellationToken) =>
        await database.InUserTransaction<(Repository?, IReadOnlyList<Landed>)>(userId, async transaction =>
        {
            var pending = (await transaction.Connection!.QueryAsync<Landed>(new CommandDefinition(
                """
                SELECT c.number AS Number, c.pull_request_number AS PullRequest, f.number AS Feature, f.branch AS Branch, b.branch AS DefaultBranch
                FROM cards c
                JOIN features f ON f.id = c.feature_id
                JOIN branch_watches b ON b.project_id = c.project_id AND b.feature_id IS NULL
                WHERE c.project_id = @projectId AND c.state = 'done' AND c.pull_request_number IS NOT NULL
                  AND f.branch IS NOT NULL AND c.landing_told_for IS DISTINCT FROM c.pull_request_number
                ORDER BY c.number
                """,
                new { projectId },
                transaction,
                cancellationToken: cancellationToken))).ToList();
            if (pending.Count == 0)
            {
                return (null, []);
            }

            var project = await transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
                "SELECT installation_id AS Installation, repository_id AS RepositoryId, owner AS Owner, name AS Name FROM projects WHERE id = @projectId",
                new { projectId },
                transaction,
                cancellationToken: cancellationToken));
            return (project is null ? null : new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name), pending);
        }, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed the issue of card {Card} of project {Project}, landed in {Branch}")]
    private static partial void LogTold(ILogger logger, Guid project, int card, string branch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pull request {Pull} of card {Card} of project {Project} merged into {Base}, not its feature's branch; its issue is left as it is")]
    private static partial void LogElsewhere(ILogger logger, Guid project, int card, int pull, string @base);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host did not answer {What} for project {Project}; its landed cards are told on a later minute")]
    private static partial void LogNotNow(ILogger logger, Guid project, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host refused {What} for card {Card} of project {Project}, with {Status}; it is tried again on a later minute")]
    private static partial void LogRefused(ILogger logger, Guid project, int card, string what, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telling the landing of card {Card} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int card, Exception failed);

    private sealed record Landed(int Number, int PullRequest, int Feature, string Branch, string DefaultBranch);

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name);
}