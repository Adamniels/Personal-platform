using System.Threading.Channels;

namespace DevFlow.Jobs;

/// <summary>
/// The lines of each run's log as they are stored, in process, by run: the log route publishes
/// the lines a post stored once its transaction has committed, and a live page reads them from a
/// subscription, so no page polls the database. Dev flow runs as one replica, so every line stored
/// passes through here.
/// <para>
/// A subscription never holds more than <see cref="Buffered"/> lines unread. One that falls further
/// behind is ended rather than given lines with a hole in them, and its reader takes up again from
/// the table, after the last seq it had, as a page's <c>Last-Event-ID</c> does. Lines reach a
/// subscription in the order their posts committed; a worker sends one post of a run at a time.
/// Once a run has ended, <see cref="End"/> ends its subscriptions too. Publishing never waits for a
/// reader. See "Jobs" in devflow/README.md.
/// </para>
/// </summary>
public sealed class RunLogFeed
{
    /// <summary>The most lines a subscription holds unread before it is ended.</summary>
    public const int Buffered = 2_000;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, List<Channel<RunLogLine>>> _readers = [];

    /// <summary>Reads the lines of <paramref name="runId"/> stored from now on, until disposed.</summary>
    public RunLogSubscription Subscribe(Guid runId)
    {
        var channel = Channel.CreateBounded<RunLogLine>(new BoundedChannelOptions(Buffered)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        lock (_lock)
        {
            if (!_readers.TryGetValue(runId, out var readers))
            {
                readers = [];
                _readers[runId] = readers;
            }

            readers.Add(channel);
        }

        return new RunLogSubscription(channel.Reader, () =>
        {
            Remove(runId, channel);
            channel.Writer.TryComplete();
        });
    }

    /// <summary>Hands <paramref name="lines"/>, just stored for <paramref name="runId"/>, to each
    /// of its subscriptions; one with no room left is ended.</summary>
    public void Publish(Guid runId, IReadOnlyList<RunLogLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return;
        }

        Channel<RunLogLine>[] readers;
        lock (_lock)
        {
            if (!_readers.TryGetValue(runId, out var listed))
            {
                return;
            }

            readers = [.. listed];
        }

        foreach (var reader in readers)
        {
            if (lines.Any(line => !reader.Writer.TryWrite(line)))
            {
                reader.Writer.TryComplete();
                Remove(runId, reader);
            }
        }
    }

    /// <summary>Ends every subscription of <paramref name="runId"/>, once its end is committed: its
    /// result recorded, or the run found lost. A reader whose lines end reads the table once to
    /// tell this from falling behind.</summary>
    public void End(Guid runId)
    {
        List<Channel<RunLogLine>>? readers;
        lock (_lock)
        {
            if (!_readers.Remove(runId, out readers))
            {
                return;
            }
        }

        foreach (var reader in readers)
        {
            reader.Writer.TryComplete();
        }
    }

    /// <summary>How many subscriptions the run has, for a test.</summary>
    internal int Readers(Guid runId)
    {
        lock (_lock)
        {
            return _readers.TryGetValue(runId, out var readers) ? readers.Count : 0;
        }
    }

    private void Remove(Guid runId, Channel<RunLogLine> channel)
    {
        lock (_lock)
        {
            if (_readers.TryGetValue(runId, out var readers) && readers.Remove(channel) && readers.Count == 0)
            {
                _readers.Remove(runId);
            }
        }
    }
}

/// <summary>A reader of one run's lines from <see cref="RunLogFeed"/>. <see cref="Lines"/> ends
/// when it fell too far behind; disposing it stops it.</summary>
public sealed class RunLogSubscription(ChannelReader<RunLogLine> lines, Action remove) : IDisposable
{
    private int _disposed;

    /// <summary>The lines, in the order stored.</summary>
    public ChannelReader<RunLogLine> Lines { get; } = lines;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            remove();
        }
    }
}