using Dapper;

using DevFlow.CodeHost;

using PlatformData;

namespace DevFlow.Stops;

/// <summary>
/// Marks each stopped card on its issue as the app, with a <see cref="WritePurpose.Post"/> key: the
/// project's attention label, and one comment, <see cref="StopText"/>, saying why, what was tried,
/// the last run's outcome and how to run it again; and takes the label off a card that runs again.
/// Nothing else is opened: an attention issue is only for a decision on a card that has merged.
/// Before commenting, the app's own comments on the issue are looked at only to find this stop's
/// copy already there, by the last line, so an answer that was lost never posts it twice. Nothing is
/// held while the code host answers, and what it did not answer for is tried again on the next
/// minute. It writes, so it lives outside <c>Reading/</c>, which can never reach a write key. See
/// "Stops" in devflow/README.md.
/// </summary>
public sealed partial class StopMarker(UserDb database, ICodeHost host, TimeProvider time, string publicOrigin, ILogger<StopMarker> log)
{
    /// <summary>
    /// Marks every stop of the project that waits, and takes off every label that waits, as the
    /// project's user. Never throws for what the code host answers or for one card's failure, which
    /// is logged and tried again on the next call; only cancellation and a database that cannot be
    /// reached escape.
    /// </summary>
    public async Task MarkPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var pending = await StopStore.Pending(database, userId, projectId, cancellationToken);
        if (pending.Count == 0 || await ProjectOf(projectId, userId, cancellationToken) is not { } project)
        {
            return;
        }

        if (await host.AppCommitter(userId, project.Repository, cancellationToken) is not CodeHostResult<Committer>.Found { Value: var app })
        {
            LogNotNow(log, projectId, "who the app is");
            return;
        }

        AppWriteToken? key = null;
        foreach (var card in pending)
        {
            try
            {
                key ??= await host.ForWrite(userId, project.Repository, WritePurpose.Post, cancellationToken) is CodeHostResult<AppWriteToken>.Found { Value: var made } ? made : null;
                if (key is null)
                {
                    LogNotNow(log, projectId, "a key to post with");
                    return;
                }

                if (card.Stopped)
                {
                    await Mark(projectId, userId, project, app.Name, key, card, cancellationToken);
                }
                else
                {
                    await Unlabel(projectId, userId, project, key, card, cancellationToken);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, card.Number, error);
            }
        }
    }

    /// <summary>The label first, then the comment once; the stop is marked when both are on the
    /// issue.</summary>
    private async Task Mark(Guid projectId, Guid userId, Project project, string app, AppWriteToken key, PendingMark card, CancellationToken cancellationToken)
    {
        var stoppedAt = card.StoppedAt!.Value;
        if (!card.Labelled)
        {
            if (!Written(await host.AddLabel(userId, key, project.Repository, card.Number, project.AttentionLabel, cancellationToken), projectId, card.Number, "the attention label"))
            {
                return;
            }

            await StopStore.Labelled(database, userId, projectId, card.Number, true, cancellationToken);
        }

        var trail = StopText.Trail(card.Number, stoppedAt);
        switch (await host.Comments(userId, project.Repository, card.Number, cancellationToken))
        {
            case CodeHostResult<IReadOnlyList<Comment>>.Found { Value: var comments } when comments.Any(comment =>
                string.Equals(comment.Author, app, StringComparison.OrdinalIgnoreCase) && comment.Body.Contains(trail, StringComparison.Ordinal)):
                await StopStore.Marked(database, userId, projectId, card.Number, stoppedAt, Now(), cancellationToken);
                return;
            case CodeHostResult<IReadOnlyList<Comment>>.Unavailable:
                LogNotNow(log, projectId, "the issue's comments");
                return;
        }

        var (count, newest) = await StopStore.Runs(database, userId, projectId, card.Number, cancellationToken);
        var page = new Uri($"{publicOrigin.TrimEnd('/')}/devflow/projects/{projectId}/cards/{card.Number}");
        var body = StopText.Comment(card.Number, stoppedAt, card.Reason ?? "", count, newest, page);
        if (Written(await host.CommentOnIssue(userId, key, project.Repository, card.Number, body, cancellationToken), projectId, card.Number, "the stop's comment"))
        {
            await StopStore.Marked(database, userId, projectId, card.Number, stoppedAt, Now(), cancellationToken);
            LogMarked(log, projectId, card.Number);
        }
    }

    /// <summary>The attention label taken off a card that runs again; one already gone is as good.</summary>
    private async Task Unlabel(Guid projectId, Guid userId, Project project, AppWriteToken key, PendingMark card, CancellationToken cancellationToken)
    {
        var removed = await host.RemoveLabel(userId, key, project.Repository, card.Number, project.AttentionLabel, cancellationToken);
        if (removed is WriteResult<IReadOnlyList<string>>.Refused { Status: StatusCodes.Status404NotFound } || Written(removed, projectId, card.Number, "taking the attention label off"))
        {
            await StopStore.Labelled(database, userId, projectId, card.Number, false, cancellationToken);
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

    /// <summary>The project's repository and attention label, or null for a project the user does
    /// not have.</summary>
    private async Task<Project?> ProjectOf(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var row = await database.InUserTransaction(userId, transaction => transaction.Connection!.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT p.installation_id AS Installation, p.repository_id AS RepositoryId, p.owner AS Owner, p.name AS Name, s.attention_label AS AttentionLabel
            FROM projects p JOIN project_settings s ON s.project_id = p.id
            WHERE p.id = @projectId
            """,
            new { projectId },
            transaction,
            cancellationToken: cancellationToken)), cancellationToken);
        return row is null ? null : new Project(new Repository(row.Installation, row.RepositoryId, row.Owner, row.Name), row.AttentionLabel);
    }

    /// <summary>Now, as Postgres keeps it, to the microsecond.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked the stop of card {Card} of project {Project} on its issue")]
    private static partial void LogMarked(ILogger logger, Guid project, int card);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host did not answer {What} for project {Project}; its stops are marked on a later minute")]
    private static partial void LogNotNow(ILogger logger, Guid project, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host refused {What} for card {Card} of project {Project}, with {Status}; it is tried again on a later minute")]
    private static partial void LogRefused(ILogger logger, Guid project, int card, string what, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marking the stop of card {Card} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int card, Exception failed);

    private sealed record Project(Repository Repository, string AttentionLabel);

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name, string AttentionLabel);
}