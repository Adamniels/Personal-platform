using Dapper;

using DevFlow.CodeHost;

using PlatformData;

namespace DevFlow.Decisions;

/// <summary>
/// Opens one issue for each decision recorded with a card Dev flow merged, as the app with a
/// <see cref="WritePurpose.Post"/> key, labelled with the project's attention label, and records it,
/// so "Waiting for you" lists it until the owner closes it on GitHub. Before opening, the app's own
/// open issues with the label are looked at for one whose body names the decision, so an answer
/// that was lost never opens a second. Nothing is held while the code host answers, and a decision
/// it did not answer for is tried again on the next minute. It writes, so it lives outside
/// <c>Reading/</c>, which can never reach a write key. See "The merge check" in devflow/README.md.
/// </summary>
public sealed partial class DecisionIssues(UserDb database, ICodeHost host, TimeProvider time, ILogger<DecisionIssues> log)
{
    /// <summary>
    /// Opens the issue of every decision of the project still without one, as the project's user,
    /// <paramref name="userId"/>. Never throws for what the code host answers or for one decision's
    /// failure, which is logged and tried again on the next call; only cancellation and a database
    /// that cannot be reached escape.
    /// </summary>
    public async Task OpenPending(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var pending = await DecisionStore.Unopened(database, userId, projectId, cancellationToken);
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
        var listed = new Dictionary<string, IReadOnlyList<Issue>?>(StringComparer.Ordinal);
        foreach (var decision in pending)
        {
            try
            {
                if (!listed.TryGetValue(decision.Label, out var open))
                {
                    open = listed[decision.Label] = await host.LabelledIssues(userId, repository, decision.Label, cancellationToken) switch
                    {
                        CodeHostResult<IReadOnlyList<Issue>>.Found { Value: var found } => found,
                        CodeHostResult<IReadOnlyList<Issue>>.NotFound => [],
                        _ => null,
                    };
                }

                if (open is null)
                {
                    LogNotNow(log, projectId, "the open issues with its attention label");
                    continue;
                }

                var marker = DecisionText.Marker(decision.Id);
                if (open.FirstOrDefault(issue => !issue.IsPullRequest
                        && string.Equals(issue.Author, app.Name, StringComparison.OrdinalIgnoreCase)
                        && issue.Body.Contains(marker, StringComparison.Ordinal)) is { } already)
                {
                    await DecisionStore.Opened(database, userId, decision.Id, already.Number, already.Url, Now(), cancellationToken);
                    continue;
                }

                key ??= await host.ForWrite(userId, repository, WritePurpose.Post, cancellationToken) is CodeHostResult<AppWriteToken>.Found { Value: var made } ? made : null;
                if (key is null)
                {
                    LogNotNow(log, projectId, "a key to post with");
                    return;
                }

                switch (await host.OpenIssue(userId, key, repository, DecisionText.Title(decision.PullRequestNumber, decision.Chosen), DecisionText.Body(decision), [decision.Label], cancellationToken))
                {
                    case WriteResult<Issue>.Written { Value: var issue }:
                        await DecisionStore.Opened(database, userId, decision.Id, issue.Number, issue.Url, Now(), cancellationToken);
                        LogOpened(log, projectId, decision.CardNumber, issue.Number);
                        break;
                    case WriteResult<Issue>.Refused { Status: var status }:
                        LogRefused(log, projectId, decision.CardNumber, status);
                        break;
                    case WriteResult<Issue>.Unavailable:
                        // Whether it was opened is not known; the next minute finds it if it was.
                        listed.Remove(decision.Label);
                        LogNotNow(log, projectId, "the decision's issue");
                        break;
                    default:
                        throw new InvalidOperationException("a write result that is not one of the three");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(log, projectId, decision.CardNumber, error);
            }
        }
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened issue {Issue} for a decision of card {Card} of project {Project}")]
    private static partial void LogOpened(ILogger logger, Guid project, int card, int issue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host did not answer {What} for project {Project}; its decisions' issues open on a later minute")]
    private static partial void LogNotNow(ILogger logger, Guid project, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The code host refused the issue for a decision of card {Card} of project {Project}, with {Status}; it is tried again on a later minute")]
    private static partial void LogRefused(ILogger logger, Guid project, int card, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Opening the issue for a decision of card {Card} of project {Project} failed; it is tried again on a later minute")]
    private static partial void LogFailed(ILogger logger, Guid project, int card, Exception failed);

    private sealed record ProjectRow(long Installation, long RepositoryId, string Owner, string Name);
}