using System.Diagnostics;
using System.Text.Json.Nodes;

using DevFlow.CodeHost;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Build;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// A worker carrying a build job through, against Dev flow in process and the fake code host's real
/// git repository: the clean working copy, the agent, the check on a clean copy of its last
/// commit, the push and the result, and nothing left behind. See "The worker" in
/// devflow/README.md.
/// </summary>
public sealed class BuildJobTests
{
    [Fact]
    public async Task ACardIsBuiltCheckedAndPushed()
    {
        await using var bench = await Bench.Start(check: $"git rev-parse HEAD > '{Bench.Out}' && test -f greeting.txt");
        string? committed = null;
        var agent = new ScriptedAgent(async (run, _) => committed = await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting"));

        var job = await bench.RunJob(agent);

        // The agent worked in its copy of the job's branch, with the job's brief and limits.
        var jobId = job.GetProperty("job_id").GetGuid();
        Assert.Equal(bench.Folders.For(jobId).Copy, agent.Given!.WorkingDirectory);
        Assert.Equal(job.GetProperty("brief").GetString(), agent.Given.Brief);
        Assert.Equal(new AgentLimits(200, 25m, TimeSpan.FromMinutes(90)), agent.Given.Limits);
        Assert.Equal(Jobs.ClaudeToken, agent.Given.Credential.Reveal());

        // The branch is on the remote at the agent's commit, made as the app's bot.
        var head = await bench.RemoteHead();
        Assert.Equal(committed, head);
        Assert.Equal(
            $"Add the greeting|{FakeCodeHost.AppLogin}|{FakeCodeHost.AppEmail}|{FakeCodeHost.AppLogin}",
            await Bench.Git(bench.Remote, "log", "-1", "--format=%s|%an|%ae|%cn", head!));

        // The check ran on that commit.
        Assert.Equal(head, (await File.ReadAllTextAsync(bench.OutFile, Ct)).Trim());

        // Dev flow recorded the result with the head commit and the check's exit code.
        var recorded = await bench.Run();
        Assert.Equal("succeeded", recorded.Result);
        Assert.Equal(head, recorded.HeadCommit);
        Assert.Equal(0, recorded.CheckExitCode);
        Assert.Equal(Bench.Branch, recorded.Branch);
        Assert.Equal("oauth_token", recorded.AuthMethod);
        Assert.Equal("a warning\n", recorded.Stderr);
        Assert.NotNull(recorded.CheckingAt);
        Assert.Equal("checking", (await bench.Jobs.Card(1)).State);

        // Nothing of the job is left.
        Assert.Empty(Directory.EnumerateFileSystemEntries(bench.Folders.Secrets));
        Assert.Empty(Directory.EnumerateFileSystemEntries(bench.Folders.Handover));
        Assert.Empty(Directory.EnumerateFileSystemEntries(bench.Folders.Work));
    }

    [Fact]
    public async Task TheCheckRunsOnACleanCopyOfTheCommit()
    {
        await using var bench = await Bench.Start(check: $"pwd > '{Bench.Out}' && ls -A >> '{Bench.Out}' && test ! -e build/stale.txt");
        var agent = new ScriptedAgent(async (run, _) =>
        {
            await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting");
            // Left behind in the copy, uncommitted and untracked: ignored by the project.
            await ScriptedAgent.Write(run, "build/stale.txt", "a build the check must not see\n");
            Assert.Contains("build/", await Bench.Git(run.WorkingDirectory, "status", "--porcelain", "--ignored"), StringComparison.Ordinal);
        });

        var job = await bench.RunJob(agent);

        var lines = (await File.ReadAllLinesAsync(bench.OutFile, Ct)).ToList();
        var paths = bench.Folders.For(job.GetProperty("job_id").GetGuid());
        // pwd answers the folder as the system resolves it, which on macOS is under /private.
        Assert.EndsWith(paths.Check, lines[0], StringComparison.Ordinal);
        Assert.Contains("greeting.txt", lines);
        Assert.DoesNotContain("build", lines);
        Assert.Equal(0, (await bench.Run()).CheckExitCode);
        Assert.Equal("succeeded", (await bench.Run()).Result);
    }

    [Fact]
    public async Task AFailingCheckIsReportedAndTheBranchIsStillPushed()
    {
        // 302 lines, the last on standard error, so only the last 200 are kept.
        await using var bench = await Bench.Start(check: "echo first; i=1; while [ $i -le 300 ]; do echo line $i; i=$((i+1)); done; echo boom >&2; exit 3");
        string? committed = null;
        var agent = new ScriptedAgent(async (run, _) => committed = await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting"));

        await bench.RunJob(agent);

        var check = bench.SentResult()["check"]!;
        Assert.Equal(3, (int)check["exit_code"]!);
        var output = ((string)check["output"]!).TrimEnd('\n').Split('\n');
        Assert.Equal(200, output.Length);
        Assert.Equal("line 102", output[0]);
        Assert.Equal("boom", output[^1]);

        var recorded = await bench.Run();
        Assert.Equal("check_failed", recorded.Result);
        Assert.Equal(3, recorded.CheckExitCode);
        Assert.Equal(committed, recorded.HeadCommit);
        Assert.Equal(committed, await bench.RemoteHead());
        // A first failure in the card runs it again, see "Stops".
        var card = await bench.Jobs.Card(1);
        Assert.Equal("queued", card.State);
        Assert.Equal(DevFlow.Jobs.RunStore.Again("The check failed with exit code 3"), card.Reason);
    }

    [Fact]
    public async Task WorkLeftUncommittedIsKept()
    {
        await using var bench = await Bench.Start(check: "test -f notes/todo.txt && grep -q changed README.md");
        var agent = new ScriptedAgent(async (run, _) =>
        {
            await ScriptedAgent.Write(run, "notes/todo.txt", "Count the habits\n");
            await ScriptedAgent.Write(run, "README.md", "# The project, changed\n");
        });

        await bench.RunJob(agent);

        var head = await bench.RemoteHead();
        Assert.NotNull(head);
        Assert.Equal(
            $"Work the agent left uncommitted|{FakeCodeHost.AppLogin}",
            await Bench.Git(bench.Remote, "log", "-1", "--format=%s|%an", head!));
        Assert.Equal("Count the habits", await Bench.Git(bench.Remote, "show", $"{head}:notes/todo.txt"));
        Assert.Equal("The first commit", await Bench.Git(bench.Remote, "log", "-1", "--format=%s", $"{head}^"));

        // It was committed as the agent, in its copy, before the check, which passed on it.
        var commit = Assert.Single(bench.AsAgent.Started, started => started.Spec.Arguments.Contains("commit"));
        Assert.EndsWith("/copy", commit.Spec.WorkingDirectory, StringComparison.Ordinal);
        Assert.Contains("Work the agent left uncommitted", commit.Spec.Arguments);
        var recorded = await bench.Run();
        Assert.Equal(0, recorded.CheckExitCode);
        Assert.Equal(head, recorded.HeadCommit);
    }

    [Fact]
    public async Task AnExistingBranchIsContinued()
    {
        string? earlier = null;
        await using var bench = await Bench.Start(before: async bench =>
            earlier = await bench.PushAsSomeoneElse(Bench.Branch, "main", "Work from a lost run", ("earlier.txt", "from before\n")));
        var agent = new ScriptedAgent(async (run, _) =>
        {
            Assert.Equal("from before\n", await File.ReadAllTextAsync(Path.Combine(run.WorkingDirectory, "earlier.txt"), Ct));
            Assert.Equal(Bench.Branch, await Bench.Git(run.WorkingDirectory, "branch", "--show-current"));
            await ScriptedAgent.Commit(run, "later.txt", "and after\n", "Finish the work");
        });

        await bench.RunJob(agent);

        var head = await bench.RemoteHead();
        Assert.Equal(earlier, await Bench.Git(bench.Remote, "rev-parse", $"{head}^"));
        Assert.Equal("Finish the work", await Bench.Git(bench.Remote, "log", "-1", "--format=%s", head!));
        Assert.Equal("succeeded", (await bench.Run()).Result);
    }

    [Fact]
    public async Task TheTimeLimitEndsTheAgent()
    {
        await using var bench = await Bench.Start();
        var pidFile = Path.Combine(bench.Root, "agent.pid");
        string? committed = null;
        var agent = new ScriptedAgent(async (run, cancellationToken) =>
        {
            committed = await ScriptedAgent.Commit(run, "partial.txt", "half done\n", "Do half of it");
            // An agent that never returns: its process sleeps until it is ended.
            await new SystemProcessStarter(null, TimeProvider.System).Run(
                new ProcessSpec("sh", ["-c", $"echo $$ > '{pidFile}'; exec sleep 600"], run.WorkingDirectory, new Dictionary<string, string> { ["PATH"] = bench.User.Path }),
                Timeout.InfiniteTimeSpan,
                cancellationToken);
        });
        var job = await bench.NextJob();

        var running = bench.Build(agent).Run(job, Ct);
        var pid = await WaitForPid(pidFile);
        Assert.False(running.IsCompleted);
        bench.Clock.Advance(TimeSpan.FromMinutes(90));
        await running.WaitAsync(TimeSpan.FromMinutes(1), Ct);

        // The process is ended, the run is a failure in the card, and the commit is pushed.
        Assert.False(Alive(pid));
        var recorded = await bench.Run();
        Assert.Equal("failed_in_card", recorded.Result);
        Assert.Contains("time limit of 90 minutes", recorded.Reason, StringComparison.Ordinal);
        Assert.Null(recorded.CheckExitCode);
        Assert.Equal(committed, recorded.HeadCommit);
        Assert.Equal(committed, await bench.RemoteHead());
        Assert.Equal("stopped", (await bench.Jobs.Card(1)).State);
    }

    [Fact]
    public async Task TheWorkerIsSeenWhileItsCheckRuns()
    {
        // A check that runs until the test lets it end, telling it when it has started.
        await using var bench = await Bench.Start(check: $"touch '{Bench.Out}.started'; while [ ! -e '{Bench.Out}.end' ]; do sleep 0.05; done");
        var agent = new ScriptedAgent(async (run, _) => await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting"));
        var job = await bench.NextJob();
        int Progress() => bench.Handler.Calls.Count(call => call.Path.EndsWith("/progress", StringComparison.Ordinal));

        var running = bench.Build(agent).Run(job, Ct);
        try
        {
            await Until(() => File.Exists($"{bench.OutFile}.started"));
            Assert.Equal(1, Progress());

            // Every 5 minutes it says again that the check runs, which marks it seen, so a check
            // longer than Dev flow's quiet window never makes its run look lost.
            await FiveMinutesLater(bench, Progress, 2);
            await FiveMinutesLater(bench, Progress, 3);
        }
        finally
        {
            // The check ends whatever happened, so no process outlives the test.
            await File.WriteAllTextAsync($"{bench.OutFile}.end", "", Ct);
            await running.WaitAsync(TimeSpan.FromMinutes(1), Ct);
        }

        Assert.All(bench.Handler.Calls.Where(call => call.Path.EndsWith("/progress", StringComparison.Ordinal)), call => Assert.Equal("204", call.Answer));
        Assert.Equal("succeeded", (await bench.Run()).Result);
        Assert.Equal("checking", (await bench.Jobs.Card(1)).State);
    }

    /// <summary>
    /// Moves the bench's clock five minutes on until the worker has said at least
    /// <paramref name="calls"/> times that the check runs. The clock may move before the worker has
    /// begun its next five minute wait, which then never ends, so it is moved again while no new
    /// call came, a bounded number of times.
    /// </summary>
    private static async Task FiveMinutesLater(Bench bench, Func<int> progress, int calls)
    {
        for (var i = 0; i < 40 && progress() < calls; i++)
        {
            bench.Clock.Advance(BuildJob.SeenEvery);
            for (var waited = 0; waited < 10 && progress() < calls; waited++)
            {
                await Task.Delay(25, Ct);
            }
        }

        Assert.True(progress() >= calls, "it never said again that the check runs");
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(condition(), "it never happened");
    }

    private static async Task<int> WaitForPid(string pidFile)
    {
        for (var i = 0; i < 200; i++)
        {
            if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile, Ct)).Trim(), out var pid))
            {
                return pid;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("the agent's process never started");
    }

    /// <summary>Whether a process with the id runs, by <c>kill -0</c>.</summary>
    private static bool Alive(int pid)
    {
        using var kill = Process.Start(new ProcessStartInfo("kill", ["-0", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { RedirectStandardError = true })!;
        kill.WaitForExit();
        return kill.ExitCode == 0;
    }
}