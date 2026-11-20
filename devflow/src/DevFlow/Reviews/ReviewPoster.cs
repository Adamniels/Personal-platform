using Dapper;

using DevFlow.CodeHost;

using PlatformData;

namespace DevFlow.Reviews;

/// <summary>
/// Posts each recorded verdict's copy on its pull request, a card's on the card's and a whole
/// feature's on the feature's, once, as one comment by the app with a
/// <see cref="WritePurpose.Post"/> key, and marks it posted. The verdict is already kept by Dev
/// flow; the comment is for people, and nothing reads it back to learn a verdict. Before posting,
/// the app's own comments on the pull request are looked at only to find a copy of this review
/// already there, by the run its last line names, so an answer that was lost never posts it twice.
/// Nothing is held while the code host answers, and a review the code host did not answer for is
/// left unposted, and tried again on the next minute. It writes, so it lives outside
/// <c>Reading/</c>, which can never reach a write key. See "Review" in devflow/README.md.
/// </summary>
public sealed partial class ReviewPoster(UserDb database, ICodeHost host, TimeProvider time, ILogger<ReviewPoster> log)
{
    /// <summary>
    /// Posts every review of the project that waits for it, as the project's user,
    /// <paramref name="userId"/>. Never throws for what the code host answers or for one review's
    /// failure, which is logged and tried again on the next call; only cancellation and a database
    /// that cannot be reached escape.
    /// </summary>
    public async Task PostPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var cards = await ReviewStore.Unposted(database, userId, projectId, cancellationToken);
        var features = await FeatureReviewStore.Unposted(database, userId, projectId, cancellationToken);
        var pending = cards
            .Select(review => new Pending(review.RunId, review.CardNumber, review.PullRequestNumber, ReviewText.Comment(review), ReviewStore.MarkPosted))
            .Concat(features.Select(review => new Pending(review.RunId, review.FeatureNumber, review.PullRequestNumber, ReviewText.FeatureComment(review), FeatureReviewStore.MarkPosted)))
            .ToList();
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
        foreach (var review in pending)
        {
            try
            {
                switch (await AlreadyPosted(userId, repository, review, app.Name, cancellationToken))
                {
                    case true:
                        await review.MarkPosted(database, userId, review.RunId, Now(), cancellationToken);
                        continue;
                    case null:
                        LogNotNow(log, projectId, "the pull request's comments");
                        continue;
                }

                key ??= await host.ForWrite(userId, repository, WritePurpose.Post, cancellationToken) is CodeHostResult<AppWriteToken>.Found { Value: var made } ? made : null;
                if (key is null)
                {
                    LogNotNow(log, projectId, "a key to post with");
                    return;
                }

                switch (await host.CommentOnIssue(userId, key, repository, review.PullRequestNumber, review.Body, cancellationToken))
                {
                    case WriteResult<Comment>.Written:
                        await review.MarkPosted(database, userId, review.RunId, Now(), cancellationToken);
                        LogPosted(log, projectId, review.Number, review.PullRequestNumber);
                        break;
                    case WriteResult<Comment>.Refused { Status: var status }:
                        LogRefused(log, projectId, review.Number, status);
                        break;
                    case WriteResult<Comment>.Unavailable:
                        // Whether it was posted is not known; the next posting finds it if it was.
                        LogNotNow(log, projectId, "the review's comment");
                        break;
                    default:
                        throw new InvalidOperationException("a write result that is not one of the three");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, review.Number, error);
            }
        }
    }

    /// <summary>Whether the app's own copy of the review is on the pull request already, found by
    /// the run its last line names; null when the code host did not answer.</summary>
    private async Task<bool?> AlreadyPosted(Guid userId, Repository repository, Pending review, string app, CancellationToken cancellationToken) =>
        await host.Comments(userId, repository, review.PullRequestNumber, cancellationToken) switch
        {
            CodeHostResult<IReadOnlyList<Comment>>.Found { Value: var comments } => comments.Any(comment =>
                string.Equals(comment.Author, app, StringComparison.OrdinalIgnoreCase)
                && comment.Body.Contains($"run {review.RunId}.", StringComparison.Ordinal)),
            CodeHostResult<IReadOnlyList<Comment>>.NotFound => false,
            _ => null,
        };

    /// <summary>The project's repository, or null for a project the user does not have.</summary>
    private async Task<Repository?> RepositoryOf(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var project = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            "SELECT installation_id AS Installation, repository_id AS RepositoryId, owner AS Owner, name AS Name FROM projects WHERE id = @projectId",
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return project is null ? null : new Repository(project.Installation, project.RepositoryId, project.Owner, project.Name);
    }

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Posted the review of #{Card} of project {Project} on pull request {Pull}")]
    private static partial void LogPosted(ILogger logger, Guid project, int card, int pull);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host did not answer {What} for project {Project}; its reviews are posted on a later minute")]
    private static partial void LogNotNow(ILogger logger, Guid project, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host refused the review's comment of card {Card} of project {Project}, with {Status}; it is tried again on a later minute")]
    private static partial void LogRefused(ILogger logger, Guid project, int card, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Posting the review of card {Card} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int card, Exception failed);

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name);

    /// <summary>A copy to post: the review's run, the card's number or the feature's parent's, the
    /// pull request, the comment, and how it is marked posted.</summary>
    private sealed record Pending(Guid RunId, int Number, int PullRequestNumber, string Body, Func<UserDb, Guid, Guid, DateTimeOffset, CancellationToken, Task> MarkPosted);
}