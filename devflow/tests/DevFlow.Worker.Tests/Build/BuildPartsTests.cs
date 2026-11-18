using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Build;

/// <summary>
/// The parts of a build job a whole run does not reach: the branch names refused before anything
/// runs, the result sent again while Dev flow is away, the tail of an output, and the fetch from the
/// agent's copy as the image runs it, with a user of its own. See "The worker" in devflow/README.md.
/// </summary>
public sealed class BuildPartsTests
{
    private static readonly Guid JobId = Guid.Parse("0190a8f0-0000-7000-8000-0000000000b1");

    [Fact]
    public void TheDefaultBranchIsRefused()
    {
        Assert.Contains("default branch", BuildJob.Refusal(Job("main")), StringComparison.Ordinal);
        Assert.Contains("default branch", BuildJob.Refusal(Job("devflow/1-x", baseBranch: "devflow/1-x")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-f")]
    [InlineData("--delete")]
    [InlineData("devflow/../main")]
    [InlineData("devflow//1")]
    [InlineData("devflow/1/")]
    [InlineData("devflow/1.")]
    [InlineData("devflow/1.lock")]
    [InlineData("devflow/@{u}")]
    [InlineData("devflow/1 2")]
    [InlineData("devflow/1\n2")]
    [InlineData("+devflow/1")]
    [InlineData("devflow/1:refs/heads/main")]
    [InlineData("")]
    public void ABranchGitCouldReadAsSomethingElseIsRefused(string branch)
    {
        Assert.NotNull(BuildJob.Refusal(Job(branch)));
        Assert.NotNull(BuildJob.Refusal(Job("devflow/1-add-the-greeting", baseBranch: branch)));
    }

    [Fact]
    public void ACardsBranchIsTaken() =>
        Assert.Null(BuildJob.Refusal(Job("devflow/12-show-the-empty_state.v2")));

    [Fact]
    public async Task TheResultIsSentAgainWhileDevFlowIsAway()
    {
        var devFlow = new ScriptedDevFlow()
            .Then(WorkerApi.ResultPath(JobId), HttpStatusCode.ServiceUnavailable)
            .Then(WorkerApi.ResultPath(JobId), () => throw new HttpRequestException("Connection refused"))
            .Then(WorkerApi.ResultPath(JobId), HttpStatusCode.NoContent);
        var clock = new WatchedClock();
        var run = Refused(devFlow, clock);

        await clock.Pass(TimeSpan.FromSeconds(5));
        await clock.Pass(TimeSpan.FromSeconds(10));
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(["503", "HttpRequestException", "204"], devFlow.Calls.Select(call => call.Answer));
        Assert.All(devFlow.Calls, call => Assert.Equal("failed", (string)JsonNode.Parse(call.Body)!["outcome"]!));
    }

    [Fact]
    public async Task ItStopsTryingAfterTenMinutes()
    {
        var devFlow = new ScriptedDevFlow();
        for (var i = 0; i < 30; i++)
        {
            devFlow.Then(WorkerApi.ResultPath(JobId), HttpStatusCode.BadGateway);
        }

        var clock = new WatchedClock();
        var run = Refused(devFlow, clock);
        var waits = new List<TimeSpan>();
        while (!run.IsCompleted)
        {
            var next = clock.NextWait();
            if (await Task.WhenAny(run, next) != next)
            {
                break;
            }

            waits.Add(await next);
            clock.Advance(waits[^1]);
        }

        await run;
        // 5 seconds doubling to 60, as the loop waits, and no wait that would end past ten minutes.
        Assert.Equal([5, 10, 20, 40, 60, 60, 60, 60, 60, 60, 60, 60], waits.Select(wait => (int)wait.TotalSeconds));
        Assert.Equal(waits.Count + 1, devFlow.Calls.Count);
    }

    [Fact]
    public void TheFetchFromTheCopyRunsGitsSideThereAsTheAgent()
    {
        var starter = new RecordingStarter("nobody");
        var git = new WorkerGit(starter, starter, AgentUser.Default);

        // One argument, which git runs through a shell in the copy: setpriv drops to agent, with
        // its groups and an environment reset, before git-upload-pack reads anything there.
        Assert.Equal(["--upload-pack=setpriv --reuid agent --regid agent --init-groups --reset-env git-upload-pack"], git.UploadPack());
        Assert.Empty(new WorkerGit(starter, starter, AgentUser.Default with { Name = null }).UploadPack());
    }

    [Theory]
    [InlineData("", 2, "")]
    [InlineData("one\n", 2, "one\n")]
    [InlineData("one\ntwo\nthree\n", 2, "two\nthree\n")]
    [InlineData("one\ntwo\nthree", 2, "two\nthree")]
    [InlineData("one\ntwo\n", 2, "one\ntwo\n")]
    [InlineData("\n\n\n", 2, "\n\n")]
    public void TheLastLinesAreKept(string text, int lines, string expected) =>
        Assert.Equal(expected, BuildJob.Last(text, lines));

    /// <summary>A job onto the default branch, refused before anything runs, against
    /// <paramref name="devFlow"/>, so only its result is sent.</summary>
    private static Task Refused(ScriptedDevFlow devFlow, TimeProvider clock)
    {
        var http = DevFlowClient.CreateHttpClient(WorkerSettings.Load(Environment(("DEVFLOW_URL", "http://devflow.test"), ("DEVFLOW_PROJECT_KEY", Key)), out _)!, devFlow);
        var none = new ScriptedAgent((_, _) => throw new InvalidOperationException("the agent must not run"));
        var starter = new RecordingStarter("nobody");
        var folders = new JobFolders("/nonexistent/run", "/nonexistent/handover", "/nonexistent/work");
        var build = new BuildJob(new DevFlowClient(http), Guid.NewGuid(), new BuildTools(none, starter, starter, folders, AgentUser.Default), clock, new WorkerLog(TextWriter.Null, clock));
        return Run();

        async Task Run()
        {
            using (http)
            {
                await build.Run(JsonSerializer.SerializeToElement(Job("main"), WorkerApi.Json), Ct);
            }

            Assert.Empty(starter.Started);
        }
    }

    private static Job Job(string branch, string baseBranch = "main") => new(
        JobId,
        DevFlow.Protocol.Job.Build,
        new JobCard(1, "Add the greeting", "Say hello."),
        new JobRepository("Adamniels", "Personal-platform", new Uri("https://github.com/Adamniels/Personal-platform.git"), "main"),
        branch,
        baseBranch,
        "The brief",
        JsonDocument.Parse("{}").RootElement,
        "true",
        "sk-ant-oat01-a-token",
        "ghs_a_push_key",
        new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero),
        new JobCommitter("devflow[bot]", "1+devflow[bot]@users.noreply.github.com"),
        JobLimits.Default);
}