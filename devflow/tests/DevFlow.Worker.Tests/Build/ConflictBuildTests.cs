using System.Text.Json;

using Dapper;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Build;

using FeatureJobs = DevFlow.Tests.Jobs.FeatureJobs;
using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// A conflict job on the worker, in process against Dev flow with the fake code host's real git: a
/// build job on the feature's branch whose copy also holds the default branch's head as
/// <c>origin/&lt;default&gt;</c>; the agent merges it and resolves the conflict, and the worker takes
/// exactly one merge commit, with the feature's head and the default branch's as its parents, checks
/// it on a clean copy, pushes it to the feature's branch without force, and sends its remerge diff.
/// Anything else is a failure in the job, and nothing is pushed. See "A conflict" in
/// devflow/README.md.
/// </summary>
public sealed class ConflictBuildTests
{
    private const int Parent = 10;

    private const string Branch = "feature-10-greet-everyone";

    /// <summary>The feature and its one card, as the owner wrote them.</summary>
    private static IReadOnlyList<CodeHost.FakeIssue> Feature() =>
        [FeatureJobs.Parent(Parent, "Greet everyone", [11]), FeatureJobs.Sub(11, "Greet the feature")];

    /// <summary>
    /// The feature's branch made from <c>main</c> and given its card's greeting in the README, then
    /// <c>main</c> given another greeting on the same line and <paramref name="mainFiles"/>, so the
    /// two conflict; the card done and the feature resolving, its branch watched at its head and
    /// <c>main</c>'s at its, as bringing in leaves them. The feature branch's head and
    /// <c>main</c>'s.
    /// </summary>
    private static async Task<(string Feature, string Default)> Resolving(Bench bench, params (string Path, string Text)[] mainFiles)
    {
        var first = (await bench.RemoteHead("main"))!;
        await Bench.Git(bench.Remote, "branch", Branch, "main");
        var feature = await bench.PushAsSomeoneElse(Branch, Branch, "Greet the feature", ("README.md", "# The project\nHello from the feature\n"), ("feature.txt", "the feature's own\n"));
        var main = await bench.PushAsSomeoneElse("main", "main", "Greet main", [("README.md", "# The project\nHello from main\n"), .. mainFiles]);

        var jobs = bench.Jobs;
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = 'resolving', reason = NULL, branch = @branch, branch_base_commit = @first, changed_at = @now
            WHERE project_id = @project AND number = @parent;
            UPDATE cards SET state = 'done' WHERE project_id = @project AND number = 11;
            """,
            new { project = jobs.Project, parent = Parent, branch = Branch, first, now = jobs.Routes.Time.GetUtcNow() },
            cancellationToken: Ct));
        // The project's next minute reads the feature's branch, after it began resolving.
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(1));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            UPDATE branch_watches SET last_commit = @feature
            WHERE project_id = @project AND feature_id = (SELECT id FROM features WHERE project_id = @project AND number = @parent);
            UPDATE branch_watches SET last_commit = @main WHERE project_id = @project AND feature_id IS NULL;
            """,
            new { project = jobs.Project, parent = Parent, feature, main },
            cancellationToken: Ct));
        return (feature, main);
    }

    /// <summary>The agent merges <c>origin/main</c>, which conflicts in the README, writes
    /// <paramref name="resolution"/> there and commits the merge, as the brief asks.</summary>
    private static async Task Merge(AgentRun run, string resolution = "# The project\nHello from the feature, and from main\n")
    {
        var (exit, _) = await Bench.TryGit(run.WorkingDirectory, "merge", "--no-edit", "origin/main");
        Assert.NotEqual(0, exit);
        await ScriptedAgent.Write(run, "README.md", resolution);
        await Bench.Git(run.WorkingDirectory, "add", "README.md");
        await Bench.Git(run.WorkingDirectory, "commit", "--quiet", "--no-edit");
    }

    /// <summary>The feature's state and reason, read past row level security.</summary>
    private static async Task<(string State, string? Reason)> FeatureState(Bench bench)
    {
        await using var admin = await bench.Jobs.Routes.Database.OpenAdmin(Ct);
        return await admin.QuerySingleAsync<(string, string?)>(new CommandDefinition(
            "SELECT state, reason FROM features WHERE project_id = @project AND number = @parent",
            new { project = bench.Jobs.Project, parent = Parent },
            cancellationToken: Ct));
    }

    [Fact]
    public async Task TheAgentBringsTheDefaultBranchInItsCopy()
    {
        await using var bench = await Bench.Start(check: $"git rev-parse HEAD > '{Bench.Out}' && grep -q 'and from main' README.md", cards: Feature());
        var (feature, main) = await Resolving(bench);
        var job = await bench.NextJob();
        Assert.Equal(Job.Conflict, job.GetProperty("kind").GetString());

        // The brief lists the file both sides changed, read from the code host, and not the
        // feature's own.
        var brief = job.GetProperty("brief").GetString()!;
        Assert.Contains("```text\nREADME.md\n```", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("feature.txt", brief, StringComparison.Ordinal);

        string? started = null;
        string? origin = null;
        var agent = new ScriptedAgent(async (run, _) =>
        {
            // The feature's branch is checked out at its head, and main's head is there as
            // origin/main.
            Assert.Equal(Branch, await Bench.Git(run.WorkingDirectory, "branch", "--show-current"));
            started = await Bench.Git(run.WorkingDirectory, "rev-parse", "HEAD");
            origin = await Bench.Git(run.WorkingDirectory, "rev-parse", "origin/main");
            await Merge(run);
        });
        await bench.Build(agent).Run(job, Ct);

        Assert.Equal((feature, main), (started, origin));
        Assert.Equal(job.GetProperty("brief").GetString(), agent.Given!.Brief);
        Assert.Equal(AgentAccess.Write, agent.Given.Access);

        // One merge commit on the feature's branch, its parents the feature's head and main's,
        // checked on a clean copy of itself, and pushed there without force.
        var head = (await bench.RemoteHead(Branch))!;
        Assert.Equal($"{head} {feature} {main}", await Bench.Git(bench.Remote, "rev-list", "--parents", "-n", "1", head));
        Assert.Equal(head, (await File.ReadAllTextAsync(bench.OutFile, Ct)).Trim());
        Assert.Equal(main, await bench.RemoteHead("main"));
        Assert.DoesNotContain(bench.AsRoot.Started, started => started.Spec.Arguments.Any(argument => argument is "--force" or "-f" || argument.StartsWith('+')));

        // Dev flow took the check's progress for a run with no card.
        var jobId = job.GetProperty("job_id").GetGuid();
        Assert.Equal("204", Assert.Single(bench.Handler.Calls, call => call.Path == WorkerApi.ProgressPath(jobId)).Answer);

        var sent = bench.SentResult(jobId);
        Assert.Equal(("succeeded", head, 0), ((string)sent["outcome"]!, (string)sent["head_commit"]!, (int)sent["check"]!["exit_code"]!));
        Assert.Equal(Branch, (string)sent["branch"]!);
        Assert.False(string.IsNullOrEmpty((string?)sent["remerge_diff"]));
        Assert.Equal("bringing_in", (await FeatureState(bench)).State);
        Assert.Empty(Bench.Files(bench.Folders.Work));
        Assert.Empty(Bench.Files(bench.Folders.Secrets));
    }

    /// <summary>What an agent may leave that is not one merge commit of the feature's head and
    /// main's, and what the failure says.</summary>
    public static TheoryData<string, string> NotOneMerge() => new()
    {
        { "nothing", "is not one new commit" },
        { "two-commits", "is not one new commit" },
        { "not-a-merge", "is not a merge" },
        { "another-parent", "is not main's head" },
    };

    [Theory]
    [MemberData(nameof(NotOneMerge))]
    public async Task OnlyOneMergeCommitIsAccepted(string left, string said)
    {
        await using var bench = await Bench.Start(check: $"touch '{Bench.Out}'", cards: Feature());
        var (feature, main) = await Resolving(bench);
        var job = await bench.NextJob();
        var agent = new ScriptedAgent(async (run, _) =>
        {
            switch (left)
            {
                case "nothing":
                    break;
                case "two-commits":
                    await Merge(run);
                    await ScriptedAgent.Commit(run, "more.txt", "more\n", "And something more");
                    break;
                case "not-a-merge":
                    await ScriptedAgent.Commit(run, "README.md", "# The project\nHello from both\n", "Resolve it by hand");
                    break;
                case "another-parent":
                    // A merge, but of a commit of the agent's own on top of main, not main's head.
                    await Bench.Git(run.WorkingDirectory, "checkout", "--quiet", "-b", "side", "origin/main");
                    await ScriptedAgent.Commit(run, "side.txt", "side\n", "A side commit");
                    await Bench.Git(run.WorkingDirectory, "checkout", "--quiet", Branch);
                    await Bench.Git(run.WorkingDirectory, "merge", "--quiet", "--no-edit", "-s", "ours", "side");
                    break;
            }
        });

        await bench.Build(agent).Run(job, Ct);

        // A failure in the job: nothing was checked or pushed, and the feature waits for the owner.
        Assert.Equal(feature, await bench.RemoteHead(Branch));
        Assert.Equal(main, await bench.RemoteHead("main"));
        Assert.False(File.Exists(bench.OutFile));
        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal("failed", (string)sent["outcome"]!);
        Assert.True((bool)sent["failure"]!["in_card"]!);
        Assert.Contains(said, (string)sent["failure"]!["reason"]!, StringComparison.Ordinal);
        Assert.Null(sent["head_commit"]?.GetValue<string>());
        Assert.Null(sent["remerge_diff"]?.GetValue<string>());
        var (state, reason) = await FeatureState(bench);
        Assert.Equal("waiting_for_owner", state);
        Assert.StartsWith("Dev flow could not resolve the conflict with main: ", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckThatFailsPushesNothing()
    {
        await using var bench = await Bench.Start(check: "exit 3", cards: Feature());
        var (feature, _) = await Resolving(bench);
        var job = await bench.NextJob();

        await bench.Build(new ScriptedAgent(async (run, _) => await Merge(run))).Run(job, Ct);

        // The feature's branch is shared, so a merge whose check failed is not put on it.
        Assert.Equal(feature, await bench.RemoteHead(Branch));
        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal(("succeeded", 3), ((string)sent["outcome"]!, (int)sent["check"]!["exit_code"]!));
        Assert.Null(sent["head_commit"]?.GetValue<string>());
        Assert.Equal(("waiting_for_owner", "Dev flow could not resolve the conflict with main: The check failed with exit code 3"), await FeatureState(bench));
    }

    [Fact]
    public async Task TheRemergeDiffIsSent()
    {
        // main also adds a file of its own, which merges cleanly.
        await using var bench = await Bench.Start(cards: Feature());
        await Resolving(bench, ("main.txt", "main's own\n"));
        var job = await bench.NextJob();

        await bench.Build(new ScriptedAgent(async (run, _) => await Merge(run, "# The project\nThe agent's own greeting\n"))).Run(job, Ct);

        // It holds what the agent chose in the README against git's conflict there, and nothing of
        // the files that merged cleanly.
        var diff = (string)bench.SentResult(job.GetProperty("job_id").GetGuid())["remerge_diff"]!;
        Assert.Contains("README.md", diff, StringComparison.Ordinal);
        Assert.Contains("+The agent's own greeting", diff, StringComparison.Ordinal);
        Assert.Contains("Hello from main", diff, StringComparison.Ordinal);
        Assert.Contains("Hello from the feature", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("main.txt", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("feature.txt", diff, StringComparison.Ordinal);

        // And Dev flow keeps it on the run, as it came.
        await using var admin = await bench.Jobs.Routes.Database.OpenAdmin(Ct);
        Assert.Equal(diff, await admin.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT remerge_diff FROM runs WHERE project_id = @project AND kind = 'conflict'",
            new { project = bench.Jobs.Project },
            cancellationToken: Ct)));
    }

    /// <summary>The commit handed is the feature watch's, which can lag behind the branch, so a
    /// branch elsewhere is tried again rather than held against the job.</summary>
    [Fact]
    public async Task ABranchThatMovedIsNotResolved()
    {
        await using var bench = await Bench.Start(cards: Feature());
        await Resolving(bench);
        // Someone pushed to the feature's branch after the conflict was handed out.
        var job = await bench.NextJob();
        var moved = await bench.PushAsSomeoneElse(Branch, Branch, "A change by hand", ("hand.txt", "by hand\n"));
        var agent = new ScriptedAgent((_, _) => Task.CompletedTask);

        await bench.Build(agent).Run(job, Ct);

        // Nothing ran on a commit Dev flow did not hand out, and nothing was pushed; the failure
        // lies outside the job, so the feature is resolving still.
        Assert.Null(agent.Given);
        Assert.Equal(moved, await bench.RemoteHead(Branch));
        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal("failed", (string)sent["outcome"]!);
        Assert.False((bool)sent["failure"]!["in_card"]!);
        Assert.Contains($"is at {moved}", (string)sent["failure"]!["reason"]!, StringComparison.Ordinal);
        Assert.Equal("resolving", (await FeatureState(bench)).State);
    }

    [Fact]
    public async Task ACutDiffSaysSo()
    {
        var line = new string('x', 999) + "\n";
        var diff = string.Concat(Enumerable.Repeat(line, RemergeDiffs.MostBytes / line.Length + 10));

        var cut = RemergeDiffs.Cut(diff);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(cut) <= RemergeDiffs.MostBytes);
        Assert.EndsWith(RemergeDiffs.CutLine(System.Text.Encoding.UTF8.GetByteCount(diff)), cut, StringComparison.Ordinal);
        Assert.StartsWith(line, cut, StringComparison.Ordinal);
        Assert.Equal("small\n", RemergeDiffs.Cut("small\n"));
        _ = JsonSerializer.Serialize(cut);
    }
}