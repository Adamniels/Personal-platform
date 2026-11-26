using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Channels;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

using Microsoft.Extensions.Time.Testing;

namespace DevFlow.Worker.Tests;

/// <summary>What the worker's tests share.</summary>
internal static class Support
{
    public const string Key = "dfw_AAAAbbbbCCCCddddEEEEffffGGGGhhhhIIIIjjjjKKK";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A run's limits as a new project's settings give them: 200 turns, the 25 USD every
    /// job carries until part 8's budget, and 90 minutes.</summary>
    public static AgentLimits Limits { get; } = new(200, 25m, TimeSpan.FromMinutes(90));

    /// <summary>An environment holding exactly <paramref name="variables"/>.</summary>
    public static Func<string, string?> Environment(params (string Name, string? Value)[] variables) =>
        name => variables.FirstOrDefault(pair => pair.Name == name).Value;

    /// <summary>A synchronized writer, so a test reads what the loop writes while it runs.</summary>
    public static TextWriter Writer(out StringBuilder text)
    {
        text = new StringBuilder();
        return TextWriter.Synchronized(new StringWriter(text));
    }

    public static HttpResponseMessage Status(HttpStatusCode status, string? json = null) =>
        new(status)
        {
            Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
        };
}

/// <summary>
/// A fake clock that says each time something starts waiting on it, and for how long, so a test
/// moves time only once the loop waits, and reads the waits it chose. A timer that repeats, the
/// job's log's tick, is no wait, and is not said.
/// </summary>
internal sealed class WatchedClock : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _waits = Channel.CreateUnbounded<TimeSpan>();

    public WatchedClock()
        : base(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (period == Timeout.InfiniteTimeSpan)
        {
            _waits.Writer.TryWrite(dueTime);
        }

        return timer;
    }

    /// <summary>The next wait started, at most ten seconds of real time from now.</summary>
    public async Task<TimeSpan> NextWait() =>
        await _waits.Reader.ReadAsync(Support.Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Support.Ct);

    /// <summary>Waits for the next wait, checks it is <paramref name="expected"/>, and lets it
    /// pass.</summary>
    public async Task Pass(TimeSpan expected)
    {
        Assert.Equal(expected, await NextWait());
        Advance(expected);
    }
}

/// <summary>A call the worker made, and what it was answered: the status, or the failure.</summary>
internal sealed record Call(string Path, string Body, string Answer);

/// <summary>
/// Dev flow played from a script: each call takes the next step, which must be for the path it
/// names, and answers it or throws. Past the script, a call waits until the test ends, so the
/// worker is never answered something the test did not say. A post of a job's log is no step: it
/// goes on beside the job whenever its clock says, so it is answered as Dev flow does, and kept
/// apart in <see cref="Logs"/>.
/// </summary>
internal sealed class ScriptedDevFlow : HttpMessageHandler
{
    private readonly Queue<(string Path, Func<HttpResponseMessage> Answer)> _steps = new();
    private readonly List<Call> _calls = [];
    private readonly List<Call> _logs = [];

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The posts of a job's log, each answered 200.</summary>
    public IReadOnlyList<Call> Logs
    {
        get
        {
            lock (_logs)
            {
                return [.. _logs];
            }
        }
    }

    /// <summary>The headers of the last request, to check what the worker sent.</summary>
    public string? Authorization { get; private set; }

    public ScriptedDevFlow Then(string path, HttpStatusCode status, string? json = null) =>
        Then(path, () => Support.Status(status, json));

    public ScriptedDevFlow Then(string path, Func<HttpResponseMessage> answer)
    {
        _steps.Enqueue((path, answer));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path.StartsWith(WorkerApi.Prefix + "/jobs/", StringComparison.Ordinal) && path.EndsWith("/log", StringComparison.Ordinal))
        {
            lock (_logs)
            {
                _logs.Add(new Call(path, body, "200"));
            }

            return Support.Status(HttpStatusCode.OK, """{"stop":false}""");
        }

        Authorization = request.Headers.Authorization?.ToString();
        (string Path, Func<HttpResponseMessage> Answer) step;
        lock (_steps)
        {
            if (!_steps.TryDequeue(out step))
            {
                step = default;
            }
        }

        if (step.Answer is null)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        Assert.Equal(step.Path, path);
        try
        {
            var response = step.Answer!();
            Record(new Call(path, body, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return response;
        }
        catch (Exception e)
        {
            Record(new Call(path, body, e.GetType().Name));
            throw;
        }
    }

    private void Record(Call call)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }
    }
}

/// <summary>Passes every call to <paramref name="inner"/>, Dev flow in process, and records it
/// with the status it answered.</summary>
internal sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly List<Call> _calls = [];

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    private readonly HashSet<string> _stopping = [];

    /// <summary>Answers every log post of the job <paramref name="jobId"/> from now on <c>200</c>
    /// with <c>stop</c>, as Dev flow answers a run the owner stopped, without passing it on: for a
    /// job a test made up, which Dev flow does not know.</summary>
    public void Stop(Guid jobId)
    {
        lock (_calls)
        {
            _stopping.Add(WorkerApi.LogPath(jobId));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        bool stopping;
        lock (_calls)
        {
            stopping = _stopping.Contains(request.RequestUri!.AbsolutePath);
        }

        var response = stopping
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new LogAnswer(Stop: true), options: WorkerApi.Json) }
            : await base.SendAsync(request, cancellationToken);
        lock (_calls)
        {
            _calls.Add(new Call(request.RequestUri!.AbsolutePath, body, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return response;
    }
}

/// <summary>The worker API's paths, short.</summary>
internal static class Paths
{
    public const string Hello = WorkerApi.HelloPath;
    public const string Next = WorkerApi.NextJobPath;
}

/// <summary>Answers each start with the next outcome, and records what it was asked to start,
/// so a test sees the arguments and the environment of every process without running one. A spec's
/// line sink is handed the outcome's lines, standard output's first, as a real program's would
/// be.</summary>
internal sealed class FakeStarter(params ProcessOutcome[] outcomes) : IProcessStarter
{
    private readonly Queue<ProcessOutcome> _outcomes = new(outcomes);

    public List<(ProcessSpec Spec, TimeSpan TimeLimit)> Started { get; } = [];

    public Task<ProcessOutcome> Run(ProcessSpec spec, TimeSpan timeLimit, CancellationToken cancellationToken)
    {
        Started.Add((spec, timeLimit));
        var outcome = _outcomes.Dequeue();
        if (spec.Lines is { } sink)
        {
            foreach (var (stream, text) in new[] { (ProcessStream.Stdout, outcome.Stdout), (ProcessStream.Stderr, outcome.Stderr) })
            {
                foreach (var line in text.Split('\n')[..^(text.EndsWith('\n') ? 1 : 0)])
                {
                    sink(stream, line.TrimEnd('\r'));
                }
            }
        }

        return Task.FromResult(outcome);
    }
}
/// <summary>Claude Code's recorded answers in <c>Claude/</c>, and the streams of its runs in
/// <c>Claude/stream/</c>; see <c>Claude/README.md</c>.</summary>
internal static class Recorded
{
    /// <summary>The path of the recorded file <paramref name="name"/>.</summary>
    public static string File(string name) => Path.Combine(AppContext.BaseDirectory, "Claude", name);

    /// <summary>The text of the recorded file <paramref name="name"/>, such as the login check's
    /// answer.</summary>
    public static string Text(string name) => System.IO.File.ReadAllText(File(name));

    /// <summary>What a run of <paramref name="case"/> prints with <c>--output-format stream-json
    /// --verbose</c>, with its <c>result</c> event, its last line, changed by
    /// <paramref name="change"/> when one is given.</summary>
    public static string Stream(string @case, Action<System.Text.Json.Nodes.JsonObject>? change = null)
    {
        var text = Text(Path.Combine("stream", $"{@case}.jsonl"));
        if (change is null)
        {
            return text;
        }

        var lines = text.TrimEnd('\n').Split('\n');
        var result = System.Text.Json.Nodes.JsonNode.Parse(lines[^1])!.AsObject();
        change(result);
        lines[^1] = result.ToJsonString();
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>A run of <paramref name="case"/> as the process ends with it.</summary>
    public static ProcessOutcome Run(string @case, int exitCode, string stderr = "") =>
        new(exitCode, Stream(@case), stderr, TimedOut: false);
}

/// <summary>A line of a run's log, at the worker's clock when it was added.</summary>
internal sealed record RunLine(DateTimeOffset At, RunLineKind Kind, string Text);

/// <summary>
/// A run's log kept in memory and written nowhere, so a test reads what an agent wrote to it. The
/// worker's own is <see cref="RunLogSender"/>, which sends it to Dev flow.
/// </summary>
internal sealed class MemoryRunLog(TimeProvider time) : IRunLog
{
    private readonly Lock _lock = new();
    private readonly List<RunLine> _lines = [];
    private RunUsage _usage = RunUsage.Zero;

    public void Add(RunLineKind kind, string text)
    {
        var line = new RunLine(time.GetUtcNow(), kind, text);
        lock (_lock)
        {
            _lines.Add(line);
        }
    }

    public void Used(RunUsage soFar)
    {
        lock (_lock)
        {
            _usage = soFar;
        }
    }

    /// <summary>Every line so far, oldest first.</summary>
    public IReadOnlyList<RunLine> Lines
    {
        get
        {
            lock (_lock)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>The run's token figures as last told.</summary>
    public RunUsage Usage
    {
        get
        {
            lock (_lock)
            {
                return _usage;
            }
        }
    }
}