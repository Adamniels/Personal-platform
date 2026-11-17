using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Tests.Reading;

using static DevFlow.Worker.Tests.Support;

using Keys = DevFlow.Tests.Workers.Workers;

namespace DevFlow.Worker.Tests;

/// <summary>
/// The worker's loop: hello once, then asking for work, waiting longer while there is none, and
/// keeping on while Dev flow is away; only a wrong key ends it. See "The worker" in
/// devflow/README.md.
/// </summary>
public sealed class WorkerLoopTests
{
    private static readonly TimeSpan[] Waits = [.. new[] { 5, 10, 20, 40, 60, 60 }.Select(s => TimeSpan.FromSeconds(s))];

    [Fact]
    public async Task ItSaysHelloThenAsksForWork()
    {
        await using var routes = await Routes.Start();
        var project = await routes.Project(Keys.User);
        var key = await Keys.NewKey(routes, project);
        var clock = new WatchedClock();
        using var devFlow = new RecordingHandler(routes.App.Server.CreateHandler());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var environment = Environment(
            ("DEVFLOW_URL", routes.App.Server.BaseAddress.ToString()),
            ("DEVFLOW_PROJECT_KEY", key),
            ("DEVFLOW_RECIPE_VERSION", "3"),
            ("DEVFLOW_BASE_IMAGE", "platform-hosted/devflow-worker:2026.11.17"));

        var run = WorkerProgram.Run(environment, Writer(out var output), Writer(out var error), stop.Token, devFlow, clock, name: "fodr-worker-1");

        // Hello, then a first ask, answered 204, and a wait before the next.
        await clock.Pass(TimeSpan.FromSeconds(5));
        var listed = Assert.Single((await Keys.List(routes, project))["workers"]!.AsArray())!;
        Assert.Equal("fodr-worker-1", (string)listed["name"]!);
        Assert.Equal("3", (string)listed["recipe_version"]!);
        Assert.Equal("platform-hosted/devflow-worker:2026.11.17", (string)listed["base_image"]!);
        var id = (string)listed["id"]!;
        Assert.NotEqual(Guid.Empty, Guid.Parse(id));

        // It asks again after the 204, as the worker it said hello as.
        Assert.Equal(TimeSpan.FromSeconds(10), await clock.NextWait());
        var calls = devFlow.Calls;
        Assert.Equal([(Paths.Hello, "204"), (Paths.Next, "204"), (Paths.Next, "204")], calls.Select(c => (c.Path, c.Answer)));
        Assert.All(calls, call => Assert.Equal(id, (string)JsonNode.Parse(call.Body)!["worker_id"]!));
        Assert.False(run.IsCompleted);

        stop.Cancel();
        Assert.Equal(0, await run);
        Assert.Contains(id, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, output.ToString() + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItWaitsLongerWhileThereIsNothing()
    {
        var devFlow = new ScriptedDevFlow().Then(Paths.Hello, HttpStatusCode.NoContent);
        foreach (var _ in Waits)
        {
            devFlow.Then(Paths.Next, HttpStatusCode.NoContent);
        }

        devFlow.Then(Paths.Next, HttpStatusCode.OK, """{"job": "first"}""")
            .Then(Paths.Next, HttpStatusCode.NoContent);
        var jobs = new List<string>();
        var (loop, clock, stop) = Loop(devFlow, (job, _) =>
        {
            jobs.Add(job.GetProperty("job").GetString()!);
            return Task.CompletedTask;
        });

        var run = loop.Run(stop.Token);

        // 5 seconds, doubling, to at most 60.
        foreach (var wait in Waits)
        {
            await clock.Pass(wait);
        }

        // The job is run, the next ask comes at once, and the wait starts again from 5 seconds.
        Assert.Equal(TimeSpan.FromSeconds(5), await clock.NextWait());
        Assert.Equal(["first"], jobs);
        Assert.Equal(Waits.Length + 3, devFlow.Calls.Count);

        await stop.CancelAsync();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task ItKeepsGoingWhenDevFlowIsAway()
    {
        var devFlow = new ScriptedDevFlow()
            .Then(Paths.Hello, () => throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused)))
            .Then(Paths.Hello, () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()))
            .Then(Paths.Hello, HttpStatusCode.ServiceUnavailable)
            .Then(Paths.Hello, HttpStatusCode.NoContent)
            .Then(Paths.Next, HttpStatusCode.BadGateway)
            // Back again, it says hello before it asks.
            .Then(Paths.Hello, HttpStatusCode.NoContent)
            .Then(Paths.Next, HttpStatusCode.NoContent);
        var (loop, clock, stop) = Loop(devFlow, Unused, out var log);

        var run = loop.Run(stop.Token);

        foreach (var wait in Waits[..4])
        {
            await clock.Pass(wait);
            Assert.False(run.IsCompleted);
        }

        Assert.Equal(TimeSpan.FromSeconds(60), await clock.NextWait());
        Assert.Equal(
            [
                (Paths.Hello, "HttpRequestException"), (Paths.Hello, "TaskCanceledException"), (Paths.Hello, "503"),
                (Paths.Hello, "204"), (Paths.Next, "502"), (Paths.Hello, "204"), (Paths.Next, "204"),
            ],
            devFlow.Calls.Select(c => (c.Path, c.Answer)));
        Assert.False(run.IsCompleted);
        var said = log.ToString();
        Assert.Contains("could not be reached", said, StringComparison.Ordinal);
        Assert.Contains("did not answer in time", said, StringComparison.Ordinal);
        Assert.Contains("503", said, StringComparison.Ordinal);

        await stop.CancelAsync();
        Assert.Equal(0, await run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWrongKeyStopsItWithAClearMessage(bool afterHello)
    {
        var devFlow = new ScriptedDevFlow();
        if (afterHello)
        {
            devFlow.Then(Paths.Hello, HttpStatusCode.NoContent);
        }

        devFlow.Then(afterHello ? Paths.Next : Paths.Hello, HttpStatusCode.Unauthorized);

        var code = await WorkerProgram.Run(
            Environment(("DEVFLOW_URL", "http://devflow.test:9080"), ("DEVFLOW_PROJECT_KEY", Key)),
            Writer(out var output), Writer(out var error), Ct, devFlow, new WatchedClock(), name: "fodr-worker-1");

        Assert.Equal(2, code);
        Assert.Contains("DEVFLOW_PROJECT_KEY", error.ToString(), StringComparison.Ordinal);
        // Sent as the header and nowhere else, and never printed.
        Assert.Equal($"Bearer {Key}", devFlow.Authorization);
        Assert.DoesNotContain(Key, output.ToString() + error, StringComparison.Ordinal);
        Assert.DoesNotContain(Key[4..], output.ToString() + error, StringComparison.Ordinal);
    }

    private static Task Unused(JsonElement job, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("no job was scripted");

    private static (WorkerLoop Loop, WatchedClock Clock, CancellationTokenSource Stop) Loop(ScriptedDevFlow devFlow, JobHandler runJob) =>
        Loop(devFlow, runJob, out _);

    private static (WorkerLoop Loop, WatchedClock Clock, CancellationTokenSource Stop) Loop(ScriptedDevFlow devFlow, JobHandler runJob, out System.Text.StringBuilder log)
    {
        var clock = new WatchedClock();
        var settings = WorkerSettings.Load(Environment(("DEVFLOW_URL", "http://devflow.test:9080"), ("DEVFLOW_PROJECT_KEY", Key)), out _)!;
        var http = DevFlowClient.CreateHttpClient(settings, devFlow);
        var identity = new WorkerIdentity(Guid.CreateVersion7(), "fodr-worker-1", "3", "platform-hosted/devflow-worker:2026.11.17");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var output = Writer(out log);
        return (new WorkerLoop(new DevFlowClient(http), identity, runJob, clock, new WorkerLog(output, clock), output), clock, stop);
    }
}