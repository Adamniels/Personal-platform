using Dapper;

using PlatformData;

namespace DevFlow.Reading;

/// <summary>
/// The loop that keeps every project's snapshot current: as it starts and then once every
/// <c>DEVFLOW_POLL_SECONDS</c>, it lists <c>poll_targets</c>, the one thing it reads outside a user,
/// and asks <see cref="ProjectRefresher"/> to read each project as that project's user. It reads as
/// the app, so it works when the owner's link has expired. It runs in the fake mode too, and not at
/// all with <c>DEVFLOW_POLL_SECONDS=0</c>. See "Reading and the snapshot" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class Poller(UserDb database, ProjectRefresher refresher, TimeProvider time, TimeSpan interval, ILogger<Poller> log) : BackgroundService
{
    /// <summary>How many projects a tick reads at once. Each read holds a database connection while
    /// it runs, so a tick over every project at once would open as many as there are projects;
    /// a few at a time keeps the pool small, and a tick of a hundred projects still ends in
    /// seconds.</summary>
    public const int ReadsAtOnce = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Made first, so the interval runs from the start and a tick that takes long delays the next
        // one rather than adding to it.
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            do
            {
                await Tick(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Every target requested, <see cref="ReadsAtOnce"/> at a time, and waited for. A list
    /// that cannot be read is tried again on the next tick; a project's read never fails the tick,
    /// since the refresher's tasks never fault.</summary>
    private async Task Tick(CancellationToken stoppingToken)
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

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = ReadsAtOnce, CancellationToken = stoppingToken },
            async (target, cancellationToken) => await refresher.Request(target.ProjectId, target.UserId).WaitAsync(cancellationToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listing the projects to read failed; the loop tries again at its next tick")]
    private static partial void LogListFailed(ILogger logger, Exception failed);

    private sealed record Target(Guid ProjectId, Guid UserId);
}