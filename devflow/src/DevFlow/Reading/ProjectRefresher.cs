namespace DevFlow.Reading;

/// <summary>
/// The one seam through which anything that knows a repository may have changed asks for its
/// project to be read: the loop for every target on each tick, the refresh route, later slices
/// after an action, and a webhook once the platform is reachable. A project is read by one reader
/// at a time: a request made while it is being read waits for that read and then for one more,
/// since it came after the read began, and every request made during the same read shares that one.
/// See "Reading and the snapshot" in devflow/docs/devflow-architecture.md.
/// </summary>
/// <remarks>
/// Reads run on the thread pool under the refresher's own lifetime, never a caller's, so a caller
/// that stops waiting, such as a request the browser abandoned, does not cancel a read others wait
/// for. Disposing it cancels the reads under way and waits for them.
/// </remarks>
public sealed partial class ProjectRefresher(ProjectReader reader, ILogger<ProjectRefresher> log) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Slot> _slots = [];
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>A project being read: the read under way, and the one asked for since it began,
    /// if any.</summary>
    private sealed class Slot
    {
        public Task Running { get; set; } = Task.CompletedTask;

        public TaskCompletionSource? Next { get; set; }
    }

    /// <summary>
    /// Asks for the project to be read as <paramref name="userId"/>, its owner, whose project the
    /// caller has made sure it is. The task completes when a read that began after this request has
    /// finished, whether it succeeded or failed; it never faults. The caller decides how long to
    /// wait for it.
    /// </summary>
    public Task Request(Guid projectId, Guid userId)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(projectId, out var slot))
            {
                slot.Next ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return slot.Next.Task;
            }

            if (_stopping.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new Slot();
            _slots[projectId] = started;
            started.Running = Task.Run(() => Run(projectId, userId, started, done));
            return done.Task;
        }
    }

    /// <summary>Reads the project, answers <paramref name="done"/>, and reads once more for as long
    /// as a request came in during the read before.</summary>
    private async Task Run(Guid projectId, Guid userId, Slot slot, TaskCompletionSource done)
    {
        while (true)
        {
            try
            {
                await reader.Read(projectId, userId, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception failed)
            {
                // The reader records what it can; this is a database it could not reach. One
                // project's failure never reaches another's, or the loop.
                LogFailed(log, projectId, failed);
            }

            done.TrySetResult();
            lock (_gate)
            {
                if (slot.Next is null || _stopping.IsCancellationRequested)
                {
                    slot.Next?.TrySetResult();
                    _slots.Remove(projectId);
                    return;
                }

                done = slot.Next;
                slot.Next = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            _stopping.Cancel();
            running = [.. _slots.Values.Select(slot => slot.Running)];
        }

        await Task.WhenAll(running);
        _stopping.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Reading project {Project} failed, and its failure could not be recorded")]
    private static partial void LogFailed(ILogger logger, Guid project, Exception failed);
}