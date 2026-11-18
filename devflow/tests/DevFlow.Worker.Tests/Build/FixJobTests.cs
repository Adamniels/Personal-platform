using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Build;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// A fix job on the worker, in process against Dev flow with the fake code host's real git: it is
/// carried as a build is, on the card's existing branch from the commit it was handed, checked on
/// the new commit and pushed to the same branch, never forced; a branch that moved off that commit
/// is not built on. See "Fixing" in devflow/README.md.
/// </summary>
public sealed class FixJobTests
{
    /// <summary>A verdict in its form, changes with one blocking finding.</summary>
    private static JsonObject Changes() => new()
    {
        ["verdict"] = "changes",
        ["summary"] = "The greeting has no test.",
        ["findings"] = new JsonArray(new JsonObject { ["blocking"] = true, ["file"] = "greeting.txt", ["line"] = 1, ["what"] = "No test reads it", ["why"] = "The card asks for one" }),
        ["for_owner"] = new JsonArray(),
    };

    /// <summary>Card 1 built and opened, then reviewed with changes, so its fix is the next job; a
    /// minute later. The commit the build left.</summary>
    private static async Task<string> Reviewed(Bench bench)
    {
        var head = await bench.BuiltAndOpened();
        await bench.Review(new ScriptedAgent((_, _) => Task.CompletedTask, () => AgentResult.Success(Changes(), [], "", "oauth_token"))).Run(await bench.NextJob(), Ct);
        bench.Jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
        return head;
    }

    [Fact]
    public async Task AFixContinuesTheBranchAndIsCheckedAndPushed()
    {
        await using var bench = await Bench.Start(check: $"git rev-parse HEAD > '{Bench.Out}'");
        var built = await Reviewed(bench);
        var job = await bench.NextJob();
        Assert.Equal(Job.Fix, job.GetProperty("kind").GetString());

        string? started = null;
        string? committed = null;
        var agent = new ScriptedAgent(async (run, _) =>
        {
            // The agent's copy is the card's branch at the commit the fix was handed.
            Assert.Equal(Bench.Branch, await Bench.Git(run.WorkingDirectory, "branch", "--show-current"));
            started = await Bench.Git(run.WorkingDirectory, "rev-parse", "HEAD");
            committed = await ScriptedAgent.Commit(run, "greeting.test.txt", "reads greeting.txt\n", "Test the greeting");
        });
        await bench.Build(agent).Run(job, Ct);

        // On top of the build's commit, checked, and pushed to the same branch.
        Assert.Equal(built, started);
        var head = await bench.RemoteHead();
        Assert.Equal(committed, head);
        Assert.Equal(built, await Bench.Git(bench.Remote, "rev-parse", $"{head}^"));
        Assert.Equal(head, (await File.ReadAllTextAsync(bench.OutFile, Ct)).Trim());
        Assert.Equal(job.GetProperty("brief").GetString(), agent.Given!.Brief);
        Assert.Equal(AgentAccess.Write, agent.Given.Access);

        var jobId = job.GetProperty("job_id").GetGuid();
        var sent = bench.SentResult(jobId);
        Assert.Equal("succeeded", (string)sent["outcome"]!);
        Assert.Equal(head, (string)sent["head_commit"]!);
        Assert.Equal(0, (int)sent["check"]!["exit_code"]!);
        Assert.Equal("reviewing", (await bench.Jobs.Card(1)).State);
        Assert.Empty(Bench.Files(bench.Folders.Work));
        Assert.Empty(Bench.Files(bench.Folders.Secrets));
    }

    [Fact]
    public async Task AFixTakesPicturesOfItsOwnCommit()
    {
        // A project with pages: the build took its pictures, and the fix takes its own, of the
        // commit it pushed, beside the build's on pr-screenshots.
        await using var bench = await Bench.Start(start: "npm run dev", url: "http://localhost:5173");
        var built = await Reviewed(bench);
        var job = await bench.NextJob();
        Assert.Equal(Job.Fix, job.GetProperty("kind").GetString());

        await bench.Build(new ScriptedAgent(async (run, _) => await ScriptedAgent.Commit(run, "greeting.test.txt", "reads greeting.txt\n", "Test the greeting"))).Run(job, Ct);

        var head = (await bench.RemoteHead())!;
        Assert.NotEqual(built, head);
        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal(["habits-desktop", "habits-phone"], sent["screenshots"]!["names"]!.AsArray().Select(name => (string)name!));
        var files = await Bench.Git(bench.Remote, "ls-tree", "-r", "--name-only", PageShots.Branch);
        Assert.Contains($"devflow/1/{built[..7]}/habits-desktop.png", files, StringComparison.Ordinal);
        Assert.Contains($"devflow/1/{head[..7]}/habits-desktop.png", files, StringComparison.Ordinal);
        Assert.Equal(2, bench.Starter.Started.Count);
    }

    [Fact]
    public async Task ABranchThatMovedIsNotFixed()
    {
        await using var bench = await Bench.Start();
        await Reviewed(bench);
        // Someone pushed to the card's branch after the review.
        var moved = await bench.PushAsSomeoneElse(Bench.Branch, Bench.Branch, "A change by hand", ("hand.txt", "by hand\n"));
        var job = await bench.NextJob();
        Assert.Equal(Job.Fix, job.GetProperty("kind").GetString());
        var agent = new ScriptedAgent((_, _) => Task.CompletedTask);

        await bench.Build(agent).Run(job, Ct);

        // Nothing ran on a commit Dev flow did not hand out, nothing was pushed, and the failure lies
        // in the card, which stops with why.
        Assert.Null(agent.Given);
        Assert.Equal(moved, await bench.RemoteHead());
        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal("failed", (string)sent["outcome"]!);
        Assert.True((bool)sent["failure"]!["in_card"]!);
        Assert.Contains($"is at {moved}", (string)sent["failure"]!["reason"]!, StringComparison.Ordinal);
        Assert.Equal("stopped", (await bench.Jobs.Card(1)).State);
    }
}