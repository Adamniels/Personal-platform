using System.Text;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Build;

using ProjectValuesTests = DevFlow.Tests.Values.ProjectValuesTests;

/// <summary>
/// A project's values on the worker: the job carries them, and the worker puts them in the
/// environment it builds for the agent's process, the check and the start, and nowhere else,
/// keeping them only for the job, as it keeps the token. See "The worker" in devflow/README.md.
/// </summary>
public sealed class WorkerValuesTests
{
    private const string Name = "STRIPE_TEST_KEY";

    private const string Value = ProjectValuesTests.Value;

    private const string Start = "npm ci && npm run dev";

    private const string Url = "http://localhost:5173";

    [Fact]
    public async Task TheAgentCheckAndStartGetThem()
    {
        // The check writes its environment where the test reads it.
        await using var bench = await Bench.Start(check: $"env > {Bench.Out}", start: Start, url: Url);
        await ProjectValuesTests.Give(bench.Jobs.Routes.App, bench.Jobs.Project, Name, Value);
        var agent = new ScriptedAgent(async (run, _) => await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting"));

        var job = await bench.RunJob(agent);

        // The job carried them, and the agent's run was given them.
        Assert.Contains(Value, job.GetRawText(), StringComparison.Ordinal);
        Assert.Equal([new JobValue(Name, Value)], agent.Given!.Values);

        // The check had them in its environment.
        Assert.Contains($"{Name}={Value}", await File.ReadAllTextAsync(bench.OutFile, Ct), StringComparison.Ordinal);
        var check = Assert.Single(bench.AsAgent.Started, started => started.Spec.FileName == "sh" && started.Spec.Arguments.Any(argument => argument.Contains("env >", StringComparison.Ordinal)));
        Assert.Equal(Value, check.Spec.Environment[Name]);

        // The start had them, its own variables kept.
        var started = Assert.Single(bench.Starter.Started);
        Assert.Equal(Value, started.Spec.Environment[Name]);
        Assert.Equal("1", started.Spec.Environment["DEVFLOW"]);

        // Nothing else the worker ran had them: git, the copies, the pictures, the clean up.
        var others = bench.AsAgent.Started.Concat(bench.AsRoot.Started).Where(other => other != check).ToList();
        Assert.NotEmpty(others);
        Assert.All(others, other => Assert.False(other.Spec.Environment.ContainsKey(Name), $"{other.Spec.FileName} {string.Join(' ', other.Spec.Arguments)}"));
        Assert.All(others, other => Assert.DoesNotContain(Value, other.Spec.Environment.Values));

        // And the worker's own process never had them.
        Assert.Null(System.Environment.GetEnvironmentVariable(Name));
    }

    [Fact]
    public async Task ClaudeCodeRunsWithThem()
    {
        var starter = new FakeStarter(Recorded("auth-oauth-token.json"), Recorded("success.json"));
        var agent = new ClaudeCodeAgent(starter, ClaudeCodeOptions.Default, new WorkerLog(Writer(out _), new WatchedClock()));

        var result = await agent.Run(
            new AgentRun("/work/fodr", "Add the greeting.", """{"type": "object"}""", "opus", Limits, new AgentCredential("sk-ant-oat01-test"), Values: [new JobValue(Name, Value), new JobValue("HOME", "/somewhere/else")]),
            Ct);

        Assert.True(result.Succeeded, result.Failure?.Reason);
        var (login, run) = (starter.Started[0].Spec, starter.Started[1].Spec);

        // The run has them; its own variables win over a value of the same name, which the route
        // refuses anyway.
        Assert.Equal(Value, run.Environment[Name]);
        Assert.Equal(ClaudeCodeOptions.Default.Home, run.Environment["HOME"]);

        // Checking the login runs none of the project's code, and gets none.
        Assert.False(login.Environment.ContainsKey(Name));
    }

    private static ProcessOutcome Recorded(string file) =>
        new(0, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Claude", file)), "", TimedOut: false);
}