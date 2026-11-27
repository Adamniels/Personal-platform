using Dapper;

using PlatformData;

namespace DevFlow.Jobs;

/// <summary>
/// The loop that keeps a run's log for 90 days: as it starts and then once a day, it lists
/// <c>poll_targets</c>, the one thing it reads outside a user, and has
/// <see cref="RunLogStore.Prune"/> delete, as each project's user through the door, the lines of
/// its runs finished <see cref="RunLogStore.Kept"/> ago or more, as <see cref="PullRequests.OpeningLoop"/>
/// goes through every project. The runs themselves, their results, figures and reasons, are kept.
/// Not run with <c>DEVFLOW_POLL_SECONDS=0</c>, as the other loops are not. See "Jobs" in
/// devflow/README.md.
/// </summary>
public sealed partial class RunLogPruner(UserDb database, TimeProvider time, ILogger<RunLogPruner> log) : BackgroundService
{
    /// <summary>How often it prunes.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Made first, so the day runs from the start and a long prune delays the next rather than
        // adding to it.
        using var timer = new PeriodicTimer(Every, time);
        try
        {
            do
            {
                await PruneAll(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Every project's old logs deleted, one project after another, each as its user and in
    /// its own <c>try</c>, so one project's failure never stops another's; what fails is tried again
    /// the next day.</summary>
    public async Task PruneAll(CancellationToken stoppingToken)
    {
        IEnumerable<Target> targets;
        try
        {
            targets = await database.WithoutUser(transaction => transaction.Connection!.QueryAsync<Target>(new CommandDefinition(
                "SELECT project_id AS ProjectId, user_id AS UserId FROM poll_targets ORDER BY project_id",
                transaction: transaction,
                cancellationToken: stoppingToken)), stoppingToken);
        }
        catch (Exception failed) when (failed is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            LogListFailed(log, failed);
            return;
        }

        var now = time.GetUtcNow();
        foreach (var target in targets)
        {
            try
            {
                var pruned = await RunLogStore.Prune(database, target.UserId, target.ProjectId, now, stoppingToken);
                if (pruned > 0)
                {
                    LogPruned(log, pruned, target.ProjectId);
                }
            }
            catch (Exception failed) when (failed is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogProjectFailed(log, target.ProjectId, failed);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listing the projects to prune run logs of failed; the loop tries again tomorrow")]
    private static partial void LogListFailed(ILogger logger, Exception failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pruning the run logs of project {Project} failed; the loop tries again tomorrow")]
    private static partial void LogProjectFailed(ILogger logger, Guid project, Exception failed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted the log lines of {Runs} runs of project {Project}, kept their 90 days")]
    private static partial void LogPruned(ILogger logger, int runs, Guid project);

    private sealed record Target(Guid ProjectId, Guid UserId);
}