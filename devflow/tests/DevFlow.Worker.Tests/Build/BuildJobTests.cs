using System.Diagnostics;

using DevFlow.CodeHost;
using DevFlow.Worker.Agents;

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

        Assert.Contains("The check exited with 0.", bench.Log.ToString(), StringComparison.Ordinal);
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
        Assert.Contains("The check exited with 0.", bench.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingCheckIsReportedAndTheBranchIsStillPushed()
    {
        // 302 lines, the last on standard error, so only the last 200 are kept.
        await using var bench = await Bench.Start(check: "echo first; i=1; while [ $i -le 300 ]; do echo line $i; i=$((i+1)); done; echo boom >&2; exit 3");
        string? committed = null;
        var agent = new ScriptedAgent(async (run, _) => committed = await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting"));

        await bench.RunJob(agent);

        Assert.Contains("The check exited with 3.", bench.Log.ToString(), StringComparison.Ordinal);
        Assert.Equal(committed, await bench.RemoteHead());
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
        Assert.Contains("The check exited with 0.", bench.Log.ToString(), StringComparison.Ordinal);
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

        // The process is ended, the run is a failure in the card, no check runs, and the commit is
        // pushed.
        Assert.False(Alive(pid));
        var said = bench.Log.ToString();
        Assert.Contains("The agent failed: it ran past the time limit of 90 minutes and was ended.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Running the check.", said, StringComparison.Ordinal);
        Assert.Equal(committed, await bench.RemoteHead());
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