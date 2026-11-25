using System.Net;
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
/// moves time only once the loop waits, and reads the waits it chose.
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
        _waits.Writer.TryWrite(dueTime);
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
/// worker is never answered something the test did not say.
/// </summary>
internal sealed class ScriptedDevFlow : HttpMessageHandler
{
    private readonly Queue<(string Path, Func<HttpResponseMessage> Answer)> _steps = new();
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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);
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
/// so a test sees the arguments and the environment of every process without running one.</summary>
internal sealed class FakeStarter(params ProcessOutcome[] outcomes) : IProcessStarter
{
    private readonly Queue<ProcessOutcome> _outcomes = new(outcomes);

    public List<(ProcessSpec Spec, TimeSpan TimeLimit)> Started { get; } = [];

    public Task<ProcessOutcome> Run(ProcessSpec spec, TimeSpan timeLimit, CancellationToken cancellationToken)
    {
        Started.Add((spec, timeLimit));
        return Task.FromResult(_outcomes.Dequeue());
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