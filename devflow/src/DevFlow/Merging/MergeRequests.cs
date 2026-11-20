using System.Threading.Channels;

using DevFlow.Decisions;

namespace DevFlow.Merging;

/// <summary>
/// Asks for a project's merge check now, rather than on its minute: the result route asks after it
/// records a result, so a card its review approved is checked right away, without the worker
/// waiting for GitHub, which a merge may take 20 seconds for each of its requests. A request is
/// handed to <see cref="MergeRequestLoop"/> and never waits; with the loops off,
/// <c>DEVFLOW_POLL_SECONDS=0</c> as in the service's own tests, nothing is checked on its own and a
/// request is dropped. See "The merge check" in devflow/README.md.
/// </summary>
public sealed class MergeRequests(bool enabled)
{
    private readonly Channel<(Guid ProjectId, Guid UserId)> _requests = Channel.CreateUnbounded<(Guid, Guid)>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Asks for the project's merge check as <paramref name="userId"/>, its owner, whose
    /// project the caller has made sure it is.</summary>
    public void Request(Guid projectId, Guid userId)
    {
        if (enabled)
        {
            _requests.Writer.TryWrite((projectId, userId));
        }
    }

    /// <summary>The requests, in the order made.</summary>
    internal ChannelReader<(Guid ProjectId, Guid UserId)> Reader => _requests.Reader;
}

/// <summary>Runs each project's merge check, and then opens its decisions' issues and tells its
/// landed cards' issues, as <see cref="MergeRequests"/> asks, one request after another, each in
/// its own <c>try</c>. Not run with <c>DEVFLOW_POLL_SECONDS=0</c>.</summary>
public sealed partial class MergeRequestLoop(MergeRequests requests, MergeChecker checker, DecisionIssues decisions, LandedCards landed, ILogger<MergeRequestLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (projectId, userId) in requests.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await checker.CheckPending(projectId, userId, stoppingToken);
                    await decisions.OpenPending(projectId, userId, stoppingToken);
                    await landed.TellPending(projectId, userId, stoppingToken);
                }
                catch (Exception failed) when (failed is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LogFailed(log, projectId, failed);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The merge check asked for project {Project} failed; its minute tries again")]
    private static partial void LogFailed(ILogger logger, Guid project, Exception failed);
}