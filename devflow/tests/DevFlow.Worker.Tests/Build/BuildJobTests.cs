using System.Diagnostics;

using DevFlow.CodeHost;
using DevFlow.Worker.Agents;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Build;

/// <summary>
/// A worker carrying a build job through, against Dev flow in process and the fake code host's real
/// git repository. See "The worker" in devflow/README.md.
/// </summary>
public sealed class BuildJobTests
{
    [Fact]
    public async Task WorkLeftUncommittedIsKept()
    {
        await using var bench = await Bench.Start();
        var agent = new ScriptedAgent(async (run, _) =>
        {
            await ScriptedAgent.Write(run, "notes/todo.txt", "Count the habits\n");
            await ScriptedAgent.Write(run, "README.md", "# The project, changed\n");
        });

        await bench.RunJob(agent);

        // It was committed as the agent, in its copy, by the app's bot.
        var commit = Assert.Single(bench.AsAgent.Started, started => started.Spec.Arguments.Contains("commit"));
        Assert.EndsWith("/copy", commit.Spec.WorkingDirectory, StringComparison.Ordinal);
        Assert.Contains("Work the agent left uncommitted", commit.Spec.Arguments);
        var copy = commit.Spec.WorkingDirectory;
        Assert.Equal($"Work the agent left uncommitted|{FakeCodeHost.AppLogin}", await Bench.Git(copy, "log", "-1", "--format=%s|%an"));
        Assert.Equal("Count the habits", await Bench.Git(copy, "show", "HEAD:notes/todo.txt"));
        Assert.Equal("", await Bench.Git(copy, "status", "--porcelain"));
    }

    [Fact]
    public async Task TheTimeLimitEndsTheAgent()
    {
        await using var bench = await Bench.Start();
        var pidFile = Path.Combine(bench.Root, "agent.pid");
        var agent = new ScriptedAgent(async (run, cancellationToken) =>
        {
            await ScriptedAgent.Commit(run, "partial.txt", "half done\n", "Do half of it");
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

        // The process is ended, and the run is a failure in the card.
        Assert.False(Alive(pid));
        Assert.Contains("The agent failed: it ran past the time limit of 90 minutes and was ended.", bench.Log.ToString(), StringComparison.Ordinal);
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