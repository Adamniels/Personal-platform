using Dapper;

using DevFlow.Features;
using DevFlow.Protocol;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Build;

using FeatureJobs = DevFlow.Tests.Jobs.FeatureJobs;
using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// A whole feature's fix on the worker, in process against Dev flow with the fake code host's real
/// git: a fix job on the feature's branch from the commit its watch last named; the agent commits,
/// the worker checks the commit on a clean copy and pushes it to the feature's branch without force,
/// and only when the agent succeeded and its check passed, since the feature's branch is shared and
/// is the head of its pull request to the default branch. See "Fixing a feature" in
/// devflow/README.md.
/// </summary>
public sealed class FeatureFixBuildTests
{
    private const int Parent = 10;

    private const string Branch = "feature-10-greet-everyone";

    /// <summary>The feature and its one card, as the owner wrote them.</summary>
    private static IReadOnlyList<CodeHost.FakeIssue> Feature() =>
        [FeatureJobs.Parent(Parent, "Greet everyone", [11]), FeatureJobs.Sub(11, "Greet the feature")];

    /// <summary>
    /// The feature's branch made from <c>main</c> and given its card's work, the card done, the
    /// feature's pull request open and its review asking for changes, so it is fixing in round 1,
    /// and its branch watched at its head, as its readiness leaves it. The feature branch's head.
    /// </summary>
    private static async Task<string> Fixing(Bench bench)
    {
        var first = (await bench.RemoteHead("main"))!;
        await Bench.Git(bench.Remote, "branch", Branch, "main");
        var head = await bench.PushAsSomeoneElse(Branch, Branch, "Greet the feature", ("README.md", "# The project\nHello from the feature\n"));

        var jobs = bench.Jobs;
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            UPDATE features SET state = 'fixing', reason = 'Round 1 of 3', branch = @branch, branch_base_commit = @first, brought_in_commit = @first,
                                pull_request_number = 50, pull_request_url = 'https://github.com/Adamniels/Personal-platform/pull/50',
                                fix_round = 1, next_fix_round = 1, changed_at = @now
            WHERE project_id = @project AND number = @parent;
            UPDATE cards SET state = 'done' WHERE project_id = @project AND number = 11;
            """,
            new { project = jobs.Project, parent = Parent, branch = Branch, first, now = jobs.Routes.Time.GetUtcNow() },
            cancellationToken: Ct));
        jobs.Routes.Time.Advance(TimeSpan.FromSeconds(1));
        await jobs.Routes.Read(jobs.Project, Jobs.User);
        await admin.ExecuteAsync(new CommandDefinition(
            """
            UPDATE branch_watches SET last_commit = @head
            WHERE project_id = @project AND feature_id = (SELECT id FROM features WHERE project_id = @project AND number = @parent)
            """,
            new { project = jobs.Project, parent = Parent, head },
            cancellationToken: Ct));
        return head;
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
    public async Task AFixIsPushedToTheFeaturesBranch()
    {
        await using var bench = await Bench.Start(check: $"git rev-parse HEAD > '{Bench.Out}'", cards: Feature());
        var head = await Fixing(bench);
        var job = await bench.NextJob();
        Assert.Equal(Job.FeatureFix, job.GetProperty("kind").GetString());

        string? started = null;
        var agent = new ScriptedAgent(async (run, _) =>
        {
            Assert.Equal(Branch, await Bench.Git(run.WorkingDirectory, "branch", "--show-current"));
            started = await Bench.Git(run.WorkingDirectory, "rev-parse", "HEAD");
            await ScriptedAgent.Commit(run, "README.md", "# The project\nHello from the whole feature\n", "Greet the whole feature");
        });
        await bench.Build(agent).Run(job, Ct);

        // From the feature's head, one new commit on it, checked on a clean copy of itself and pushed
        // to the feature's branch without force.
        Assert.Equal(head, started);
        var fixedCommit = (await bench.RemoteHead(Branch))!;
        Assert.NotEqual(head, fixedCommit);
        Assert.Equal(head, await Bench.Git(bench.Remote, "rev-parse", $"{fixedCommit}^"));
        Assert.Equal(fixedCommit, (await File.ReadAllTextAsync(bench.OutFile, Ct)).Trim());
        Assert.DoesNotContain(bench.AsRoot.Started, started => started.Spec.Arguments.Any(argument => argument is "--force" or "-f" || argument.StartsWith('+')));

        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal(("succeeded", fixedCommit, 0), ((string)sent["outcome"]!, (string)sent["head_commit"]!, (int)sent["check"]!["exit_code"]!));
        // No pictures: nobody reads them for a feature's fix.
        Assert.Null(sent["screenshots"]);
        Assert.Equal(("reviewing", FeatureFix.Fixed), await FeatureState(bench));
        Assert.Empty(Bench.Files(bench.Folders.Work));
        Assert.Empty(Bench.Files(bench.Folders.Secrets));
    }

    [Fact]
    public async Task AFixWhoseCheckFailsPushesNothing()
    {
        await using var bench = await Bench.Start(check: "exit 3", cards: Feature());
        var head = await Fixing(bench);
        var job = await bench.NextJob();

        await bench.Build(new ScriptedAgent(async (run, _) => await ScriptedAgent.Commit(run, "README.md", "broken\n", "Break it"))).Run(job, Ct);

        // The feature's branch is shared, so a fix whose check failed is not put on it, and the fix
        // runs once more.
        Assert.Equal(head, await bench.RemoteHead(Branch));
        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal(("succeeded", 3), ((string)sent["outcome"]!, (int)sent["check"]!["exit_code"]!));
        Assert.Null(sent["head_commit"]?.GetValue<string>());
        Assert.Equal(("fixing", FeatureFix.Again("The check failed with exit code 3")), await FeatureState(bench));
    }

    /// <summary>The commit handed is the feature watch's, which can lag behind the branch, so a
    /// branch elsewhere is tried again rather than held against the job.</summary>
    [Fact]
    public async Task ABranchThatMovedIsNotFixed()
    {
        await using var bench = await Bench.Start(cards: Feature());
        await Fixing(bench);
        var job = await bench.NextJob();
        var moved = await bench.PushAsSomeoneElse(Branch, Branch, "A change by hand", ("hand.txt", "by hand\n"));
        var agent = new ScriptedAgent((_, _) => Task.CompletedTask);

        await bench.Build(agent).Run(job, Ct);

        Assert.Null(agent.Given);
        Assert.Equal(moved, await bench.RemoteHead(Branch));
        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal("failed", (string)sent["outcome"]!);
        Assert.False((bool)sent["failure"]!["in_card"]!);
        Assert.Contains($"is at {moved}", (string)sent["failure"]!["reason"]!, StringComparison.Ordinal);
        Assert.Equal("fixing", (await FeatureState(bench)).State);
    }
}