using Dapper;

using DevFlow.Reviews;

using PlatformData;

namespace DevFlow.PullRequests;

/// <summary>
/// The loop that opens built cards' pull requests and posts their reviews: as it starts and then
/// once every <c>DEVFLOW_POLL_SECONDS</c>, the same minute the reading loop keeps, it lists
/// <c>poll_targets</c>, the one thing it reads outside a user, and asks
/// <see cref="PullRequestOpener"/> to open each project's as that project's user, then
/// <see cref="ReviewPoster"/> to post each recorded verdict's copy. A worker's result is answered
/// without waiting for GitHub, so its card's pull request opens here, and its review's comment is
/// posted here, within a minute, and one GitHub did not answer for is tried again. Its own loop rather than the reading loop's,
/// since the reading code can never reach a write key. Not run with <c>DEVFLOW_POLL_SECONDS=0</c>.
/// See "The pull request Dev flow opens" in devflow/README.md.
/// </summary>
public sealed partial class OpeningLoop(UserDb database, PullRequestOpener opener, ReviewPoster poster, TimeProvider time, TimeSpan interval, ILogger<OpeningLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    /// <summary>Every target's pull requests opened and reviews posted, one project after
    /// another, each in its own <c>try</c>, so one project's failure never stops another's.</summary>
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

        foreach (var target in targets)
        {
            try
            {
                await opener.OpenPending(target.ProjectId, target.UserId, stoppingToken);
                await poster.PostPending(target.ProjectId, target.UserId, stoppingToken);
            }
            catch (Exception failed) when (failed is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogProjectFailed(log, target.ProjectId, failed);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listing the projects to open pull requests for failed; the loop tries again at its next tick")]
    private static partial void LogListFailed(ILogger logger, Exception failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Opening the pull requests of project {Project} failed; the loop tries again at its next tick")]
    private static partial void LogProjectFailed(ILogger logger, Guid project, Exception failed);

    private sealed record Target(Guid ProjectId, Guid UserId);
}