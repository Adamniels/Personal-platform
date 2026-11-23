using System.Text.Json.Nodes;

using DevFlow.Worker.Agents;

namespace DevFlow.Worker.Tests.Build;

using Jobs = DevFlow.Tests.Jobs.Jobs;
using ProjectValuesTests = DevFlow.Tests.Values.ProjectValuesTests;
using Scrub = DevFlow.Tests.Values.ScrubTests;

/// <summary>
/// The worker scrubs each of the project's values its job carried from everything it sends,
/// before it leaves, as well as Dev flow scrubbing it again, and from its own log while the job
/// runs. What it sent is read as the bench recorded the request, before Dev flow saw it. See "The
/// worker" in devflow/README.md.
/// </summary>
public sealed class ScrubTests
{
    [Fact]
    public async Task TheWorkerScrubsWhatItSends()
    {
        // The check prints the value it was given; the agent wrote it in its report, Claude's usage
        // and its standard error, in three forms.
        await using var bench = await Bench.Start(check: $"echo \"connecting as ${Scrub.Name}\"");
        await ProjectValuesTests.Give(bench.Jobs.Routes.App, bench.Jobs.Project, Scrub.Name, Scrub.Value);
        var report = Jobs.GoodReport();
        report["summary"] = $"Logged in with {Scrub.Value}.";
        var usage = new JsonObject { ["note"] = Scrub.Base64Url };
        var agent = new ScriptedAgent(
            async (run, _) => await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting"),
            () => AgentResult.Success(report, usage, $"curl: Authorization: Basic {Scrub.Basic}", "oauth_token"));

        await bench.RunJob(agent);

        var sent = Scrub.Strings(bench.SentResult()).ToList();
        Assert.All(sent, text => Assert.False(Scrub.Holds(text), text));
        Assert.Contains($"Logged in with {Scrub.Placeholder}.", sent);
        Assert.Contains(sent, text => text.Contains($"connecting as {Scrub.Placeholder}", StringComparison.Ordinal));
        Assert.Contains(sent, text => text.StartsWith("curl: Authorization: Basic ", StringComparison.Ordinal) && text.Contains(Scrub.Placeholder, StringComparison.Ordinal));
        Assert.Contains(Scrub.Placeholder, sent);

        // Dev flow stored what came, scrubbed, and the log held none of it either.
        var run = await bench.Run();
        Assert.False(Scrub.Holds($"{run.Report} {run.CheckOutput} {run.Usage} {run.Stderr}"));
        Assert.False(Scrub.Holds(bench.Log.ToString()), bench.Log.ToString());
    }

    [Fact]
    public async Task TheWorkerLogsNoValue()
    {
        // The agent failed, saying the value in its reason, which the worker logs and sends.
        await using var bench = await Bench.Start();
        await ProjectValuesTests.Give(bench.Jobs.Routes.App, bench.Jobs.Project, Scrub.Name, Scrub.Value);
        var agent = new ScriptedAgent(
            (_, _) => Task.CompletedTask,
            () => AgentResult.Failed(FailureSide.Card, $"the tests could not log in as ci-user:{Scrub.UrlEncoded}", [], "", "oauth_token"));

        await bench.RunJob(agent);

        var log = bench.Log.ToString();
        Assert.False(Scrub.Holds(log), log);
        Assert.Contains($"log in as ci-user:{Scrub.Placeholder}", log, StringComparison.Ordinal);
        var reason = (string)bench.SentResult()["failure"]!["reason"]!;
        Assert.Equal($"the tests could not log in as ci-user:{Scrub.Placeholder}", reason);

    }

    [Fact]
    public void TheLogForgetsTheValuesWithTheJob()
    {
        var log = new WorkerLog(Support.Writer(out var written), new WatchedClock());
        using (log.Hiding([new DevFlow.Protocol.JobValue(Scrub.Name, Scrub.Value)]))
        {
            log.Write($"during the job: {Scrub.Value}");
        }

        // After it, the worker holds no value to scrub with, since it keeps none past its job.
        log.Write("after the job: other-text");
        Assert.Contains($"during the job: {Scrub.Placeholder}", written.ToString(), StringComparison.Ordinal);
        Assert.False(Scrub.Holds(written.ToString()));
        Assert.Contains("after the job: other-text", written.ToString(), StringComparison.Ordinal);
    }
}