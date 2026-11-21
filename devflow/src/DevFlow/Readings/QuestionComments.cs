using Dapper;

using DevFlow.CodeHost;

using PlatformData;

namespace DevFlow.Readings;

/// <summary>
/// Posts, as the app with a <see cref="WritePurpose.Post"/> key, one comment on each card read with
/// questions, <see cref="ReadingText"/>, listing each question and the recommendation it is built on,
/// and keeps the comment's id with the reading, so Dev flow knows its own comment later. Before
/// posting, the app's own comments on the issue are looked at only to find this reading's copy
/// already there, by the last line, so an answer that was lost never posts it twice. Nothing is held
/// while the code host answers, and what it did not answer for is tried again on the next minute. It
/// writes, so it lives outside <c>Reading/</c>, which can never reach a write key. See "Jobs" in
/// devflow/README.md.
/// </summary>
public sealed partial class QuestionComments(UserDb database, ICodeHost host, string publicOrigin, ILogger<QuestionComments> log)
{
    /// <summary>
    /// Posts every comment of the project that waits, as the project's user. Never throws for what
    /// the code host answers or for one card's failure, which is logged and tried again on the next
    /// call; only cancellation and a database that cannot be reached escape.
    /// </summary>
    public async Task PostPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var pending = await ReadingStore.Pending(database, userId, projectId, cancellationToken);
        if (pending.Count == 0 || await RepositoryOf(projectId, userId, cancellationToken) is not { } repository)
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
                key ??= await host.ForWrite(userId, repository, WritePurpose.Post, cancellationToken) is CodeHostResult<AppWriteToken>.Found { Value: var made } ? made : null;
                if (key is null)
                {
                    LogNotNow(log, projectId, "a key to post with");
                    return;
                }

                await Post(projectId, userId, repository, app.Name, key, card, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, card.Number, error);
            }
        }
    }

    /// <summary>The comment once: one of the app's already there for this reading is kept, by its
    /// id; otherwise it is posted, and its id kept.</summary>
    private async Task Post(Guid projectId, Guid userId, Repository repository, string app, AppWriteToken key, PendingQuestions card, CancellationToken cancellationToken)
    {
        var trail = ReadingText.Trail(card.Number, card.RunId);
        switch (await host.Comments(userId, repository, card.Number, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<Comment>>.Found { Value: var comments }
                when comments.FirstOrDefault(comment => string.Equals(comment.Author, app, StringComparison.OrdinalIgnoreCase)
                    && comment.Body.Contains(trail, StringComparison.Ordinal)) is { } posted:
                await ReadingStore.Commented(database, userId, projectId, card.RunId, card.Number, posted.Id, cancellationToken);
                return;
            case CodeHostResult<IReadOnlyList<Comment>>.Unavailable:
                LogNotNow(log, projectId, "the issue's comments");
                return;
        }

        var page = new Uri($"{publicOrigin.TrimEnd('/')}/devflow/projects/{projectId}/cards/{card.Number}");
        switch (await host.CommentOnIssue(userId, key, repository, card.Number, ReadingText.Comment(card.Number, card.RunId, card.Questions, page), cancellationToken))
        {
            case WriteResult<Comment>.Written { Value: var comment }:
                await ReadingStore.Commented(database, userId, projectId, card.RunId, card.Number, comment.Id, cancellationToken);
                LogPosted(log, projectId, card.Number);
                return;
            case WriteResult<Comment>.Refused { Status: var status }:
                LogRefused(log, projectId, card.Number, status);
                return;
            default:
                LogNotNow(log, projectId, "the questions' comment");
                return;
        }
    }

    /// <summary>The project's repository, or null for a project the user does not have.</summary>
    private async Task<Repository?> RepositoryOf(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var row = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            "SELECT installation_id AS Installation, repository_id AS RepositoryId, owner AS Owner, name AS Name FROM projects WHERE id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return row is null ? null : new Repository(row.Installation, row.RepositoryId, row.Owner, row.Name);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Posted the questions of card {Card} of project {Project} on its issue")]
    private static partial void LogPosted(ILogger logger, Guid project, int card);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host did not answer {What} for project {Project}; its questions are posted on a later minute")]
    private static partial void LogNotNow(ILogger logger, Guid project, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host refused the questions' comment for card {Card} of project {Project}, with {Status}; it is tried again on a later minute")]
    private static partial void LogRefused(ILogger logger, Guid project, int card, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Posting the questions of card {Card} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int card, Exception failed);

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name);
}