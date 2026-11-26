using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

using Microsoft.Extensions.Time.Testing;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests;

/// <summary>
/// The job's log sent to Dev flow as the job runs, with a moved clock and a fake Dev flow: lines
/// every 2 seconds, a heartbeat every 15 without any, nothing lost while Dev flow is away up to
/// 5,000 lines, the usage so far, and every line before the result. See "The worker" in
/// devflow/README.md.
/// </summary>
public sealed class RunLogSenderTests
{
    private static readonly Guid JobId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid WorkerId = Guid.Parse("0193a000-0000-7000-8000-000000000002");
    private const string Token = "sk-ant-oat01-the-claude-token";

    [Fact]
    public async Task LinesAreSentEveryTwoSeconds()
    {
        await using var bench = new SenderBench();
        var started = bench.Clock.GetUtcNow();
        for (var i = 1; i <= 1200; i++)
        {
            bench.Sender.Add(RunLineKind.Check, $"line {i}");
        }

        // Not after one second: lines wait for the 2 seconds since the job's start.
        bench.Clock.Advance(TimeSpan.FromSeconds(1));
        bench.Clock.Advance(TimeSpan.FromSeconds(1));
        var first = await bench.DevFlow.Next();
        Assert.Equal(WorkerApi.LogPath(JobId), first.Path);
        Assert.Equal(started + TimeSpan.FromSeconds(2), first.At);
        Assert.Equal(WorkerId, (Guid)first.Body["worker_id"]!);
        Assert.Equal(Enumerable.Range(1, 500).Select(seq => (long)seq), first.Seqs);
        var line = first.Lines[0];
        Assert.Equal("check", (string)line["kind"]!);
        Assert.Equal("line 1", (string)line["text"]!);
        Assert.Equal(started, (DateTimeOffset)line["at"]!);

        // The rest, at most 500 a post, each 2 seconds after the one before.
        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        var second = await bench.DevFlow.Next();
        Assert.Equal(started + TimeSpan.FromSeconds(4), second.At);
        Assert.Equal(Enumerable.Range(501, 500).Select(seq => (long)seq), second.Seqs);
        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        var third = await bench.DevFlow.Next();
        Assert.Equal(Enumerable.Range(1001, 200).Select(seq => (long)seq), third.Seqs);

        // A line added now goes within 2 seconds, with the next seq.
        bench.Sender.Add(RunLineKind.System, "Pushing devflow/1-add-the-greeting");
        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        var fourth = await bench.DevFlow.Next();
        Assert.Equal(started + TimeSpan.FromSeconds(8), fourth.At);
        Assert.Equal([1201L], fourth.Seqs);
        Assert.Equal("system", (string)fourth.Lines[0]["kind"]!);
        Assert.Equal(4, bench.DevFlow.Posts.Count);
    }

    [Fact]
    public async Task QuietRunsStillBeat()
    {
        await using var bench = new SenderBench();
        var started = bench.Clock.GetUtcNow();

        bench.Clock.Advance(TimeSpan.FromSeconds(14));
        bench.Clock.Advance(TimeSpan.FromSeconds(1));
        var first = await bench.DevFlow.Next();
        Assert.Equal(started + TimeSpan.FromSeconds(15), first.At);
        Assert.Empty(first.Lines);
        Assert.Null(first.Body["usage"]);

        bench.Clock.Advance(TimeSpan.FromSeconds(15));
        var second = await bench.DevFlow.Next();
        Assert.Equal(started + TimeSpan.FromSeconds(30), second.At);
        Assert.Empty(second.Lines);

        // A post with lines is a beat too, so the next beat is 15 seconds after it.
        bench.Sender.Add(RunLineKind.Text, "Reading the brief.");
        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Single((await bench.DevFlow.Next()).Lines);
        bench.Clock.Advance(TimeSpan.FromSeconds(14));
        bench.Clock.Advance(TimeSpan.FromSeconds(1));
        var beat = await bench.DevFlow.Next();
        Assert.Equal(started + TimeSpan.FromSeconds(47), beat.At);
        Assert.Empty(beat.Lines);

        // Once the result is sent the sender is done, and beats no more.
        await bench.Sender.DisposeAsync();
        bench.Clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        Assert.Equal(4, bench.DevFlow.Posts.Count);
    }

    [Fact]
    public async Task DevFlowAwayLosesNoLine()
    {
        await using var bench = new SenderBench();
        bench.DevFlow.Answer = () => Support.Status(HttpStatusCode.ServiceUnavailable);
        foreach (var text in new[] { "one", "two", "three" })
        {
            bench.Sender.Add(RunLineKind.Text, text);
        }

        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        var away = await bench.DevFlow.Next();
        bench.DevFlow.Answer = () => throw new HttpRequestException("Connection refused");
        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        var unreachable = await bench.DevFlow.Next();
        bench.DevFlow.Answer = SenderBench.Taken;
        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        var taken = await bench.DevFlow.Next();

        // Each try sent the same lines, with the same seqs.
        Assert.All(new[] { away, unreachable, taken }, post => Assert.Equal([1L, 2L, 3L], post.Seqs));
        Assert.Equal(["one", "two", "three"], taken.Lines.Select(line => (string)line["text"]!));

        // Past 5,000 unsent lines the oldest go, and one line says how many.
        bench.DevFlow.Answer = () => Support.Status(HttpStatusCode.BadGateway);
        for (var i = 4; i <= 5603; i++)
        {
            bench.Sender.Add(RunLineKind.Check, $"line {i}");
        }

        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        await bench.DevFlow.Next();
        bench.DevFlow.Answer = SenderBench.Taken;
        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        var back = await bench.DevFlow.Next();
        Assert.Equal(500, back.Lines.Count);
        Assert.Equal("system", (string)back.Lines[0]["kind"]!);
        Assert.Equal("600 lines were not sent", (string)back.Lines[0]["text"]!);
        Assert.Equal(603L, back.Seqs[0]);
        Assert.Equal(Enumerable.Range(604, 499).Select(seq => (long)seq), back.Seqs.Skip(1));

        await bench.Sender.Last(Ct);
        var sent = bench.DevFlow.Posts.Where(post => post.Taken).SelectMany(post => post.Seqs).ToList();
        Assert.Equal(sent.Distinct(), sent);
        Assert.Equal([1L, 2L, 3L, 603L, .. Enumerable.Range(604, 5000).Select(seq => (long)seq)], sent);
        Assert.Single(bench.DevFlow.Posts.Where(post => post.Taken).SelectMany(post => post.Lines), line => ((string)line["text"]!).EndsWith("were not sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsageSoFarIsSent()
    {
        await using var bench = new SenderBench();
        bench.Sender.Used(new RunUsage(120, 3000, 45000, 80));
        bench.Sender.Add(RunLineKind.Text, "Looking at the tests.");
        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        var usage = (await bench.DevFlow.Next()).Body["usage"]!;
        Assert.Equal(120, (long)usage["input_tokens"]!);
        Assert.Equal(3000, (long)usage["cache_creation_input_tokens"]!);
        Assert.Equal(45000, (long)usage["cache_read_input_tokens"]!);
        Assert.Equal(80, (long)usage["output_tokens"]!);

        // Unchanged, it is not sent again.
        bench.Sender.Add(RunLineKind.Tool, "Bash: dotnet test");
        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null((await bench.DevFlow.Next()).Body["usage"]);

        // Changed, it goes on the next post, lines or none.
        bench.Sender.Used(new RunUsage(240, 3000, 90000, 200));
        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        var changed = await bench.DevFlow.Next();
        Assert.Empty(changed.Lines);
        Assert.Equal(240, (long)changed.Body["usage"]!["input_tokens"]!);
    }

    [Fact]
    public async Task TheLastLinesGoBeforeTheResult()
    {
        await using var bench = new SenderBench();
        for (var i = 1; i <= 1200; i++)
        {
            bench.Sender.Add(RunLineKind.Check, $"line {i}");
        }

        // No time has passed: the last post sends them all, 500 a post, before it returns.
        await bench.Sender.Last(Ct);
        Assert.Equal([500, 500, 200], bench.DevFlow.Posts.Select(post => post.Lines.Count));
        Assert.Equal(Enumerable.Range(1, 1200).Select(seq => (long)seq), bench.DevFlow.Posts.SelectMany(post => post.Seqs));

        // With Dev flow away, the last post fails once and the result is sent anyway.
        await using var away = new SenderBench();
        away.DevFlow.Answer = () => Support.Status(HttpStatusCode.ServiceUnavailable);
        away.Sender.Add(RunLineKind.System, "Pushing devflow/1-add-the-greeting");
        await away.Sender.Last(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Single(away.DevFlow.Posts);
    }

    [Fact]
    public async Task ALineIsScrubbedAndCutBeforeItIsKept()
    {
        await using var bench = new SenderBench(values: [new JobValue("API_KEY", "the-api-key-value")]);
        bench.Sender.Add(RunLineKind.Check, $"curl -H 'x-key: the-api-key-value' with {Token}");
        bench.Sender.Add(RunLineKind.Check, "");
        bench.Sender.Add(RunLineKind.Check, new string('a', LogLine.MostText + 10));

        await bench.Sender.Last(Ct);

        var lines = Assert.Single(bench.DevFlow.Posts).Lines;
        // An empty line is no line, since Dev flow takes text of 1 character or more.
        Assert.Equal(2, lines.Count);
        Assert.Equal($"curl -H 'x-key: {ValueScrub.Placeholder("API_KEY")}' with {Agents.ClaudeCodeAgent.Redacted}", (string)lines[0]["text"]!);
        var cut = (string)lines[1]["text"]!;
        Assert.Equal(LogLine.MostText, cut.Length);
        Assert.EndsWith(LogLine.CutMark, cut, StringComparison.Ordinal);
        Assert.Equal([1L, 2L], lines.Select(line => (long)line["seq"]!));
    }

    [Fact]
    public async Task TheWorkersOwnLinesGoWhileItSends()
    {
        await using var bench = new SenderBench();
        bench.Log.Write("Continuing devflow/1-add-the-greeting, which the remote has at abc.");
        await bench.Sender.Last(Ct);
        var line = Assert.Single(Assert.Single(bench.DevFlow.Posts).Lines);
        Assert.Equal("system", (string)line["kind"]!);
        Assert.Equal("Continuing devflow/1-add-the-greeting, which the remote has at abc.", (string)line["text"]!);

        // Once it is done, the worker's lines are its own again.
        await bench.Sender.DisposeAsync();
        bench.Log.Write("Sent the result: succeeded.");
        Assert.Single(bench.DevFlow.Posts);
    }

    /// <summary>A sender for one job against a fake Dev flow, with a clock the test moves.</summary>
    private sealed class SenderBench : IAsyncDisposable
    {
        private readonly HttpClient _http;

        public SenderBench(IReadOnlyList<JobValue>? values = null)
        {
            DevFlow = new LogDevFlow(Clock);
            var settings = WorkerSettings.Load(Environment(("DEVFLOW_URL", "http://devflow.test"), ("DEVFLOW_PROJECT_KEY", Key)), out _)!;
            _http = DevFlowClient.CreateHttpClient(settings, DevFlow);
            Log = new WorkerLog(TextWriter.Null, Clock);
            Sender = RunLogSender.Start(new DevFlowClient(_http), JobId, WorkerId, Token, values, Clock, Log);
        }

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));

        public LogDevFlow DevFlow { get; }

        public WorkerLog Log { get; }

        public RunLogSender Sender { get; }

        public static HttpResponseMessage Taken() => Support.Status(HttpStatusCode.OK, """{"stop":false}""");

        public async ValueTask DisposeAsync()
        {
            await Sender.DisposeAsync();
            _http.Dispose();
            DevFlow.Dispose();
        }
    }
}

/// <summary>A post of the log, when Dev flow had it by the test's clock, and whether it took it.</summary>
internal sealed record LogPost(string Path, DateTimeOffset At, JsonObject Body, bool Taken)
{
    public IReadOnlyList<JsonObject> Lines => [.. Body["lines"]!.AsArray().Select(line => line!.AsObject())];

    public IReadOnlyList<long> Seqs => [.. Lines.Select(line => (long)line["seq"]!)];
}

/// <summary>Dev flow's log route played by the test: it answers each post as <see cref="Answer"/>
/// says, 200 unless told otherwise, and keeps every post.</summary>
internal sealed class LogDevFlow(TimeProvider clock) : HttpMessageHandler
{
    private readonly Channel<LogPost> _next = Channel.CreateUnbounded<LogPost>();
    private readonly List<LogPost> _posts = [];

    public Func<HttpResponseMessage> Answer { get; set; } = () => Support.Status(HttpStatusCode.OK, """{"stop":false}""");

    public IReadOnlyList<LogPost> Posts
    {
        get
        {
            lock (_posts)
            {
                return [.. _posts];
            }
        }
    }

    /// <summary>The next post, at most ten seconds of real time from now.</summary>
    public async Task<LogPost> Next() =>
        await _next.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var at = clock.GetUtcNow();
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        HttpResponseMessage? answer = null;
        try
        {
            answer = Answer();
            return answer;
        }
        finally
        {
            var post = new LogPost(request.RequestUri!.AbsolutePath, at, body, answer?.StatusCode == HttpStatusCode.OK);
            lock (_posts)
            {
                _posts.Add(post);
            }

            _next.Writer.TryWrite(post);
        }
    }
}