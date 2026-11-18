using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Build;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Build;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// What keeps the agent's copy from reaching root and the push key: root runs git only in its own
/// repository, nothing the agent planted runs, the key never reaches the agent, and a push is never
/// forced and never to the default branch. See "Security decisions and accepted risks" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class GitSafetyTests
{
    /// <summary>The fake's push keys begin so, the job's and each fresh one alike.</summary>
    private const string KeyPrefix = "fake_app_";

    /// <summary>The base64 that begins an <c>Authorization</c> header carrying a key,
    /// <c>x-access-token:</c>.</summary>
    /// <summary>The hooks the agent plants: every one a command the worker runs could call.</summary>
    private static readonly string[] Hooks = ["pre-push", "post-checkout", "pre-commit", "commit-msg", "post-commit", "reference-transaction", "pre-auto-gc"];

    private static readonly string HeaderPrefix = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:"))[..20];

    [Fact]
    public async Task RootNeverRunsGitInTheAgentsCopy()
    {
        await using var bench = await Bench.Start();
        var agent = new ScriptedAgent(async (run, _) =>
        {
            await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting");
            await ScriptedAgent.Write(run, "left.txt", "left uncommitted\n");
        });

        var job = await bench.RunJob(agent);

        var paths = bench.Folders.For(job.GetProperty("job_id").GetGuid());
        var asRoot = bench.AsRoot.Started;
        Assert.NotEmpty(asRoot);
        foreach (var (_, spec) in asRoot)
        {
            var arguments = spec.Arguments;
            Assert.Equal("git", spec.FileName);
            // In its own repository, or in its folder of secrets for the clone that makes it.
            Assert.True(spec.WorkingDirectory == paths.Repository || spec.WorkingDirectory == paths.Secret, $"root ran git in {spec.WorkingDirectory}");
            Assert.Equal(["-c", "core.hooksPath=/dev/null"], arguments.Take(2));
            Assert.DoesNotContain(arguments, a => a is "-C" or "--git-dir" or "--work-tree" || a.StartsWith("--git-dir=", StringComparison.Ordinal) || a.StartsWith("--work-tree=", StringComparison.Ordinal));
            Assert.DoesNotContain(spec.Environment.Keys, name => name is "GIT_DIR" or "GIT_WORK_TREE" or "GIT_INDEX_FILE" or "GIT_OBJECT_DIRECTORY");
        }

        // Root names the agent's folder once, as the source of its fetch from the copy, and writes
        // nothing there: the bundles go in its handover folder, and the agent removes its own.
        AssertRootNamesNothingOfTheAgentsButTheFetch(bench, paths);
        var fetch = Assert.Single(asRoot, started => started.Spec.Arguments.Contains("fetch")).Spec.Arguments;
        Assert.Contains("protocol.file.allow=always", fetch);
        Assert.Equal($"refs/heads/{Bench.Branch}:refs/heads/{Bench.Branch}", fetch[^1]);
        Assert.All(
            asRoot.Where(started => started.Spec.Arguments.Contains("bundle")),
            started => Assert.StartsWith(paths.Handover + "/", started.Spec.Arguments[5], StringComparison.Ordinal));
        Assert.Contains(bench.AsAgent.Started, started => started.Spec.FileName == "rm" && started.Spec.Arguments.SequenceEqual(["-rf", "--", paths.Work]));
        Assert.Equal("succeeded", (await bench.Run()).Result);
    }

    [Fact]
    public async Task AHookTheAgentPlantedNeverRuns()
    {
        await using var bench = await Bench.Start();
        var ran = Directory.CreateDirectory(Path.Combine(bench.Root, "ran")).FullName;
        var agent = new ScriptedAgent(async (run, _) =>
        {
            await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting");
            // Planted last, so the agent's own commit above runs none of them.
            var hooks = Path.Combine(run.WorkingDirectory, ".git", "hooks");
            var elsewhere = Directory.CreateDirectory(Path.Combine(run.WorkingDirectory, ".git", "elsewhere")).FullName;
            foreach (var hook in Hooks)
            {
                foreach (var folder in new[] { hooks, elsewhere })
                {
                    var file = Path.Combine(folder, hook);
                    await File.WriteAllTextAsync(file, $"#!/bin/sh\ntouch '{ran}/{hook}-{Path.GetFileName(folder)}'\n", Ct);
                    Bench.Executable(file);
                }
            }

            var monitor = Path.Combine(run.WorkingDirectory, ".git", "monitor.sh");
            await File.WriteAllTextAsync(monitor, $"#!/bin/sh\ntouch '{ran}/fsmonitor'\n", Ct);
            Bench.Executable(monitor);
            await Bench.Git(run.WorkingDirectory, "config", "core.fsmonitor", monitor);
            await Bench.Git(run.WorkingDirectory, "config", "core.hooksPath", elsewhere);
            // Left for the worker to commit, so it runs git in the copy as the agent.
            await ScriptedAgent.Write(run, "left.txt", "left uncommitted\n");
        });

        await bench.RunJob(agent);

        Assert.Empty(Directory.EnumerateFileSystemEntries(ran));
        Assert.Equal("Work the agent left uncommitted", await Bench.Git(bench.Remote, "log", "-1", "--format=%s", Bench.Branch));
        Assert.Equal("succeeded", (await bench.Run()).Result);
    }

    [Fact]
    public async Task ALinkTheAgentLeftWhereRootMightWriteIsNeverWrittenThrough()
    {
        await using var bench = await Bench.Start();
        var victim = Path.Combine(bench.Root, "victim.txt");
        await File.WriteAllTextAsync(victim, "root's file\n", Ct);
        var agent = new ScriptedAgent(async (run, _) =>
        {
            await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting");
            // Links at every name in its job's folder root wrote a bundle at, or might.
            var folder = Path.GetDirectoryName(run.WorkingDirectory)!;
            File.CreateSymbolicLink(Path.Combine(folder, "copy.bundle"), victim);
            File.CreateSymbolicLink(Path.Combine(folder, "check.bundle"), victim);
        });

        var job = await bench.RunJob(agent);

        Assert.Equal("root's file\n", await File.ReadAllTextAsync(victim, Ct));
        Assert.Equal("succeeded", (await bench.Run()).Result);
        AssertRootNamesNothingOfTheAgentsButTheFetch(bench, bench.Folders.For(job.GetProperty("job_id").GetGuid()));
    }

    [Fact]
    public async Task AJobFolderTheAgentReplacedWithALinkIsNeverWrittenThrough()
    {
        await using var bench = await Bench.Start();
        var elsewhere = Directory.CreateDirectory(Path.Combine(bench.Root, "elsewhere")).FullName;
        var victim = Path.Combine(elsewhere, "check.bundle");
        await File.WriteAllTextAsync(victim, "root's file\n", Ct);
        var agent = new ScriptedAgent(async (run, _) =>
        {
            await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting");
            // Its job's folder moved into a folder of root's choosing, and a link left in its place.
            var folder = Path.GetDirectoryName(run.WorkingDirectory)!;
            Directory.Move(Path.Combine(folder, "copy"), Path.Combine(elsewhere, "copy"));
            Directory.Delete(folder);
            Directory.CreateSymbolicLink(folder, elsewhere);
        });

        var job = await bench.RunJob(agent);

        Assert.Equal("root's file\n", await File.ReadAllTextAsync(victim, Ct));
        // The link is removed, and nothing through it: the folder it named keeps what was there.
        var paths = bench.Folders.For(job.GetProperty("job_id").GetGuid());
        Assert.False(Path.Exists(paths.Work));
        Assert.True(Directory.Exists(Path.Combine(elsewhere, "copy")));
        Assert.Equal("succeeded", (await bench.Run()).Result);
        AssertRootNamesNothingOfTheAgentsButTheFetch(bench, paths);
    }

    [Fact]
    public async Task ThePushKeyNeverReachesTheAgent()
    {
        await using var bench = await Bench.Start();
        var readable = new List<string>();
        string? remotes = null;
        string? config = null;
        // Claude Code itself, behind a starter that answers as it does, so its environment is the
        // one the real agent builds.
        var claude = new AnsweringStarter(
            new ProcessOutcome(0, File.ReadAllText(Recorded("auth-oauth-token.json")), "", TimedOut: false),
            new ProcessOutcome(0, File.ReadAllText(Recorded("success.json")), "", TimedOut: false),
            async spec =>
            {
                readable.AddRange(Bench.Files(bench.Folders.Work).Concat(Bench.Files(bench.Folders.Handover)).Where(file => Holds(file.Text)).Select(file => file.Path));
                remotes = await Bench.Git(spec.WorkingDirectory, "remote", "-v");
                config = await Bench.Git(spec.WorkingDirectory, "config", "--list", "--show-origin");
            });
        var agent = new ClaudeCodeAgent(claude, ClaudeCodeOptions.Default, new WorkerLog(TextWriter.Null, TimeProvider.System));

        var job = await bench.RunJob(agent);

        Assert.StartsWith(KeyPrefix, job.GetProperty("push_key").GetString(), StringComparison.Ordinal);
        // Nothing the agent can read holds it: no file under its folder or the handover folder, its
        // copy's config, or its remote, which is the bundle it was cloned from and holds no
        // credentials.
        Assert.Empty(readable);
        Assert.False(Holds(config!), config);
        Assert.False(Holds(remotes!), remotes);
        Assert.DoesNotContain("@", remotes!, StringComparison.Ordinal);
        Assert.Contains(".bundle", remotes!, StringComparison.Ordinal);

        // Nor Claude Code's environment or arguments, nor any process the worker ran as the agent.
        Assert.Equal(2, claude.Specs.Count);
        Assert.All(claude.Specs.Concat(bench.AsAgent.Started.Select(started => started.Spec)), spec =>
            Assert.False(Holds(string.Join('\n', spec.Arguments.Concat(spec.Environment.Select(pair => $"{pair.Key}={pair.Value}")).Append(spec.WorkingDirectory))), spec.FileName));

        // Root holds it in the environment of the commands that talk to the code host, and never in
        // an argument, which every user can see.
        var asRoot = bench.AsRoot.Started.Select(started => started.Spec).ToList();
        Assert.All(asRoot, spec => Assert.False(Holds(string.Join('\n', spec.Arguments)), string.Join(' ', spec.Arguments)));
        Assert.Contains(asRoot, spec => spec.Arguments.Contains("clone") && spec.Environment.Values.Any(Holds));
        Assert.Contains(asRoot, spec => spec.Arguments.Contains("push") && spec.Environment.Values.Any(Holds));
        Assert.All(asRoot.Where(spec => !spec.Arguments.Contains("clone") && !spec.Arguments.Contains("push")), spec =>
            Assert.DoesNotContain(spec.Environment.Values, Holds));
    }

    [Fact]
    public async Task ThePushIsNeverForcedAndNeverToTheDefaultBranch()
    {
        // A job onto the default branch is refused before anything runs.
        await using (var bench = await Bench.Start())
        {
            var job = JsonNode.Parse((await bench.NextJob()).GetRawText())!.AsObject();
            var jobId = (Guid)job["job_id"]!;
            job["branch"] = "main";
            var devFlow = new ScriptedDevFlow().Then(WorkerApi.ResultPath(jobId), HttpStatusCode.NoContent);
            using var http = DevFlowClient.CreateHttpClient(WorkerSettings.Load(Environment(("DEVFLOW_URL", "http://devflow.test"), ("DEVFLOW_PROJECT_KEY", Key)), out _)!, devFlow);
            var agent = new ScriptedAgent((_, _) => throw new InvalidOperationException("the agent must not run"));
            var build = new BuildJob(new DevFlowClient(http), Jobs.FirstWorker, new BuildTools(agent, bench.AsRoot, bench.AsAgent, bench.Folders, bench.User), bench.Clock, new WorkerLog(TextWriter.Null, bench.Clock));

            await build.Run(JsonSerializer.SerializeToElement(job), Ct);

            Assert.Empty(bench.AsRoot.Started);
            Assert.Empty(bench.AsAgent.Started);
            Assert.Null(agent.Given);
            var result = JsonNode.Parse(Assert.Single(devFlow.Calls).Body)!;
            Assert.Equal("failed", (string)result["outcome"]!);
            Assert.True((bool)result["failure"]!["in_card"]!);
            Assert.Contains("default branch", (string)result["failure"]!["reason"]!, StringComparison.Ordinal);
            Assert.Null(result["head_commit"]);
            Assert.Equal(["main"], (await Bench.Git(bench.Remote, "branch", "--format=%(refname:short)")).Split('\n'));
            Assert.Empty(Directory.EnumerateFileSystemEntries(bench.Folders.Secrets));
        }

        // Someone else moved the branch while the agent worked: the push is refused, never forced.
        await using (var bench = await Bench.Start())
        {
            string? theirs = null;
            var agent = new ScriptedAgent(async (run, _) =>
            {
                theirs = await bench.PushAsSomeoneElse(Bench.Branch, "main", "Someone else's work", ("theirs.txt", "theirs\n"));
                await ScriptedAgent.Commit(run, "greeting.txt", "Hello\n", "Add the greeting");
            });

            await bench.RunJob(agent);

            Assert.Equal(theirs, await bench.RemoteHead());
            var push = Assert.Single(bench.AsRoot.Started, started => started.Spec.Arguments.Contains("push")).Spec.Arguments;
            Assert.DoesNotContain(push, a => a.StartsWith("--force", StringComparison.Ordinal) || a is "-f" or "--mirror" or "--delete" or "--all" or "--tags");
            Assert.Matches($"^[0-9a-f]{{40}}:refs/heads/{Bench.Branch}$", push[^1]);
            var recorded = await bench.Run();
            Assert.Equal("failed_outside_card", recorded.Result);
            Assert.Contains("refused the push", recorded.Reason, StringComparison.Ordinal);
            Assert.Null(recorded.HeadCommit);
        }
    }

    /// <summary>No process root ran names a path under the agent's folder, which the agent can
    /// redirect, but the copy as the source of root's fetch, which git reads as the agent.</summary>
    private static void AssertRootNamesNothingOfTheAgentsButTheFetch(Bench bench, JobPaths paths)
    {
        var naming = bench.AsRoot.Started
            .Where(started => started.Spec.WorkingDirectory.StartsWith(bench.Folders.Work, StringComparison.Ordinal)
                || started.Spec.Arguments.Any(a => a.Contains(bench.Folders.Work, StringComparison.Ordinal)))
            .ToList();
        var fetch = Assert.Single(naming).Spec.Arguments;
        Assert.Contains("fetch", fetch);
        Assert.Equal(paths.Copy, fetch[fetch.ToList().IndexOf("--") + 1]);
    }

    /// <summary>Whether the text holds a push key, or a header carrying one.</summary>
    private static bool Holds(string text) =>
        text.Contains(KeyPrefix, StringComparison.Ordinal) || text.Contains(HeaderPrefix, StringComparison.Ordinal);

    private static string Recorded(string file) => Path.Combine(AppContext.BaseDirectory, "Claude", file);

    /// <summary>Answers Claude Code's login check and then its run, as recorded, keeping each spec,
    /// and calls <paramref name="during"/> as the run starts, while the copy is the agent's.</summary>
    private sealed class AnsweringStarter(ProcessOutcome login, ProcessOutcome run, Func<ProcessSpec, Task> during) : IProcessStarter
    {
        public List<ProcessSpec> Specs { get; } = [];

        public async Task<ProcessOutcome> Run(ProcessSpec spec, TimeSpan timeLimit, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            if (Specs.Count == 1)
            {
                return login;
            }

            await during(spec);
            return run;
        }
    }
}