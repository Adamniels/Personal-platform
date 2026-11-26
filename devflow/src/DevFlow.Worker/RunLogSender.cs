using DevFlow.Protocol;
using DevFlow.Worker.Agents;

namespace DevFlow.Worker;

/// <summary>
/// Sends a job's log to Dev flow while the job runs, from the moment it is taken until its result
/// is sent, to <see cref="WorkerApi.LogRoute"/>. See "The worker" in devflow/README.md.
/// <list type="bullet">
/// <item>Each line is scrubbed of the token and the job's values, cut to
/// <see cref="LogLine.MostText"/> characters, and numbered as it is added, <c>seq</c> from 1 and
/// only growing. An empty line is no line, since Dev flow takes text of one character or
/// more.</item>
/// <item>A post goes <see cref="Every"/> when there is something new, lines or a changed usage, at
/// most <see cref="LogRequest.MostLines"/> lines, oldest first; with nothing new a post with none
/// goes <see cref="Heartbeat"/> after the last, the run's heartbeat.</item>
/// <item>A post Dev flow did not take is sent again on the next, with the same lines and seqs, which
/// Dev flow takes once. Unsent lines are kept in memory up to <see cref="MostKept"/>; past that the
/// oldest are dropped, and one <see cref="RunLineKind.System"/> line, in the place of the last one
/// dropped, says how many.</item>
/// <item>A post Dev flow answered <c>422</c> breaks a rule, and would on every try: its lines are
/// dropped, and the worker's own log says why, as it says when a post fails, never the job's.</item>
/// <item><see cref="Last"/> sends what is left before the result, and the result is sent whatever it
/// answered; the heartbeat goes on until the sender is disposed, once the result is sent.</item>
/// </list>
/// The answer's <c>stop</c> is read by "Stop now reaches the worker", #445; until then it is not.
/// Time is the <see cref="TimeProvider"/> given, so tests move it: a timer of one
/// <see cref="Tick"/> decides at each tick whether a post is due, so a moved clock decides exactly.
/// The log never holds the job up: adding a line only queues it.
/// </summary>
internal sealed class RunLogSender : IRunLog, IAsyncDisposable
{
    /// <summary>How often new lines are sent.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    /// <summary>How long after the last post one with no lines is sent.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    /// <summary>How often the sender looks whether a post is due.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>The most unsent lines kept while Dev flow is away.</summary>
    public const int MostKept = 5000;

    private readonly DevFlowClient _client;
    private readonly Guid _jobId;
    private readonly Guid _workerId;
    private readonly string _token;
    private readonly ValueScrub _scrub;
    private readonly TimeProvider _time;
    private readonly WorkerLog _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();

    /// <summary>Lines not yet taken by Dev flow, oldest first.</summary>
    private readonly Queue<LogLine> _unsent = new();

    private long _seq;

    /// <summary>The line saying how many were dropped, while it is unsent; and how many it says.</summary>
    private LogLine? _notice;
    private long _dropped;

    private RunUsage? _usage;
    private RunUsage? _usageSent;

    /// <summary>When the last post began, or the sender started.</summary>
    private DateTimeOffset _lastPost;

    /// <summary>The post on its way, at most one at a time; true when its lines are done with.</summary>
    private Task<bool>? _posting;

    /// <summary>Whether a tick came while a post was on its way, so the sender looks again once it
    /// is answered.</summary>
    private bool _missed;

    /// <summary>Whether a post failed, so a failure is logged once until one succeeds.</summary>
    private bool _failing;

    private bool _done;
    private ITimer? _timer;
    private IDisposable? _sending;

    private RunLogSender(DevFlowClient client, Guid jobId, Guid workerId, string token, IReadOnlyList<JobValue>? values, TimeProvider time, WorkerLog log)
    {
        _client = client;
        _jobId = jobId;
        _workerId = workerId;
        _token = token;
        _scrub = new ValueScrub(values ?? []);
        _time = time;
        _log = log;
        _lastPost = time.GetUtcNow();
    }

    /// <summary>A sender for the job <paramref name="jobId"/>, beating from now, and the job's log
    /// on <paramref name="log"/>, so every line the worker writes until it is disposed is sent
    /// too.</summary>
    /// <param name="token">The job's Claude token, replaced in every line.</param>
    /// <param name="values">The project's values the job carried, scrubbed from every line.</param>
    public static RunLogSender Start(DevFlowClient client, Guid jobId, Guid workerId, string token, IReadOnlyList<JobValue>? values, TimeProvider time, WorkerLog log)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        var sender = new RunLogSender(client, jobId, workerId, token, values, time, log);
        sender._sending = log.Sending(sender);
        sender._timer = time.CreateTimer(static state => ((RunLogSender)state!).OnTick(), sender, Tick, Tick);
        return sender;
    }

    public void Add(RunLineKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = LogLine.Cut(_scrub.Scrub(_token.Length == 0 ? text : text.Replace(_token, ClaudeCodeAgent.Redacted, StringComparison.Ordinal)));
        if (clean.Length == 0)
        {
            return;
        }

        var at = _time.GetUtcNow();
        lock (_lock)
        {
            if (_done)
            {
                return;
            }

            _unsent.Enqueue(new LogLine(++_seq, at, Kind(kind), clean));
            while (_unsent.Count > MostKept)
            {
                // The notice takes the place of the last line dropped, so it sits where they were
                // and its seq is one Dev flow has not seen. A post on its way may hold some of
                // them; the count then says a few more than were lost, never fewer.
                var dropped = _unsent.Dequeue();
                _dropped++;
                _notice = new LogLine(dropped.Seq, dropped.At, LogKinds.System, $"{_dropped} lines were not sent");
            }
        }
    }

    public void Used(RunUsage soFar)
    {
        ArgumentNullException.ThrowIfNull(soFar);
        lock (_lock)
        {
            _usage = soFar;
        }
    }

    /// <summary>
    /// Sends every line left, a post after another while Dev flow takes them, after the post on
    /// its way when there is one; the first post it makes that Dev flow does not take ends it, so
    /// the result is never held up by the log.
    /// </summary>
    public async Task Last(CancellationToken stopping)
    {
        while (true)
        {
            Task<bool> posting;
            bool own;
            lock (_lock)
            {
                if (_posting is { } onItsWay)
                {
                    posting = onItsWay;
                    own = false;
                }
                else if (HasNew())
                {
                    posting = _posting = Begin(stopping);
                    own = true;
                }
                else
                {
                    return;
                }
            }

            if (!await posting && own)
            {
                return;
            }
        }
    }

    /// <summary>Stops the heartbeat and the job's log on the worker's log, and waits for a post on
    /// its way; lines added from now on are not kept. Called once the result is sent.</summary>
    public async ValueTask DisposeAsync()
    {
        Task<bool>? posting;
        lock (_lock)
        {
            if (_done)
            {
                return;
            }

            _done = true;
            posting = _posting;
        }

        _sending?.Dispose();
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
        }

        await _stop.CancelAsync();
        if (posting is not null)
        {
            await posting;
        }

        _stop.Dispose();
    }

    private void OnTick()
    {
        lock (_lock)
        {
            if (_done)
            {
                return;
            }

            if (_posting is not null)
            {
                _missed = true;
                return;
            }

            BeginWhenDue();
        }
    }

    /// <summary>Begins a post when one is due. Called holding the lock, with no post on its way.</summary>
    private void BeginWhenDue()
    {
        var since = _time.GetUtcNow() - _lastPost;
        if ((HasNew() && since >= Every) || since >= Heartbeat)
        {
            _posting = Begin(_stop.Token);
        }
    }

    /// <summary>Whether there is something Dev flow has not taken. Called holding the lock.</summary>
    private bool HasNew() => _unsent.Count > 0 || _notice is not null || _usage != _usageSent;

    /// <summary>
    /// Begins a post of the oldest unsent lines, the notice first when there is one, and the usage
    /// when it changed. Called holding the lock; the post runs on the thread pool, so its answer,
    /// which takes the lock, never comes back on the caller's thread while it holds it.
    /// </summary>
    private Task<bool> Begin(CancellationToken cancellationToken)
    {
        _lastPost = _time.GetUtcNow();
        var notice = _notice;
        var lines = new List<LogLine?>(Math.Min(_unsent.Count + 1, LogRequest.MostLines));
        if (notice is not null)
        {
            lines.Add(notice);
        }

        lines.AddRange(_unsent.Take(LogRequest.MostLines - lines.Count));
        var through = lines.Count == 0 ? 0 : lines[^1]!.Seq!.Value;
        var usage = _usage != _usageSent ? _usage : null;
        var request = new LogRequest(_workerId, lines, usage is null ? null : new LogUsage(usage.InputTokens, usage.CacheCreationInputTokens, usage.CacheReadInputTokens, usage.OutputTokens));
        return Task.Run(() => Post(request, through, notice, usage, cancellationToken), CancellationToken.None);
    }

    /// <summary>Sends <paramref name="request"/> and keeps what it answered: on a <c>200</c> or a
    /// <c>422</c> its lines through <paramref name="through"/> are no longer kept, otherwise they
    /// are sent again. Whether its lines are done with, taken or refused for good.</summary>
    private async Task<bool> Post(LogRequest request, long through, LogLine? notice, RunUsage? usage, CancellationToken cancellationToken)
    {
        Answer answer;
        try
        {
            answer = await _client.Log(_jobId, request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The sender is disposed, or the worker stops: nothing more is sent.
            answer = new Answer(AnswerKind.Away, "the job ended");
        }

        string? say = null;
        var taken = answer.Kind is AnswerKind.Answered or AnswerKind.Invalid;
        lock (_lock)
        {
            _posting = null;
            if (taken)
            {
                while (_unsent.TryPeek(out var line) && line.Seq <= through)
                {
                    _unsent.Dequeue();
                }

                if (notice is not null && ReferenceEquals(_notice, notice))
                {
                    _notice = null;
                    _dropped = 0;
                }

                if (usage is not null)
                {
                    _usageSent = usage;
                }

                if (answer.Kind == AnswerKind.Invalid)
                {
                    say = $"Dev flow refused the job's log, so {request.Lines!.Count} of its lines are not sent: {answer.Detail}.";
                }

                _failing = false;
            }
            else if (!_failing && !_done)
            {
                _failing = true;
                say = $"Dev flow did not take the job's log: {answer.Detail}; its lines are kept and sent again.";
            }

            if (_missed && !_done)
            {
                _missed = false;
                BeginWhenDue();
            }
        }

        if (say is not null)
        {
            _log.Note(say);
        }

        return taken;
    }

    private static string Kind(RunLineKind kind) => kind switch
    {
        RunLineKind.Text => LogKinds.Text,
        RunLineKind.Tool => LogKinds.Tool,
        RunLineKind.ToolResult => LogKinds.ToolResult,
        RunLineKind.Check => LogKinds.Check,
        _ => LogKinds.System,
    };
}