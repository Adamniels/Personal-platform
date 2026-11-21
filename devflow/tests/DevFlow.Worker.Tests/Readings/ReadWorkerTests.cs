using System.Text.Json;
using System.Text.Json.Nodes;

using DevFlow.Protocol;
using DevFlow.Worker.Agents;
using DevFlow.Worker.Tests.Build;

using static DevFlow.Worker.Tests.Support;

namespace DevFlow.Worker.Tests.Readings;

/// <summary>
/// A reading job on the worker, in process against Dev flow with the fake code host's real git: the
/// reader gets a copy of the branch it reads, runs read only with the reading's form, and the
/// reading goes back; nothing is committed or pushed, and no push key is asked for. See "Jobs" in
/// devflow/README.md.
/// </summary>
public sealed class ReadWorkerTests
{
    /// <summary>A reading of card 1 in its form.</summary>
    private static JsonObject Reading() => new()
    {
        ["cards"] = new JsonArray(new JsonObject { ["number"] = 1, ["outcome"] = "ready", ["questions"] = new JsonArray(), ["missing"] = new JsonArray() }),
        ["touches"] = "README.md: the greeting goes beside it.",
    };

    /// <summary>A reading job of card 1 on <paramref name="branch"/>, made from the job Dev flow
    /// hands out for it, under a job id of its own.</summary>
    private static async Task<JsonElement> ReadingJob(Bench bench, string branch = "main")
    {
        var job = JsonNode.Parse((await bench.NextJob()).GetRawText())!.AsObject();
        job["job_id"] = Guid.CreateVersion7();
        job["kind"] = Job.Read;
        job["branch"] = branch;
        job["base"] = "main";
        job["brief"] = "Read card 1.";
        job["report_schema"] = Protocol.Reading.Schema();
        job["check"] = "";
        job["to_read"] = new JsonObject { ["cards"] = new JsonArray(new JsonObject { ["number"] = 1, ["title"] = Bench.Title, ["body"] = "Build it." }) };
        return JsonSerializer.SerializeToElement(job);
    }

    /// <summary>The remote's refs, each with its commit, as the test reads them.</summary>
    private static async Task<string> Refs(Bench bench) => await Bench.Git(bench.Remote, "for-each-ref", "--format=%(refname) %(objectname)");

    [Fact]
    public async Task AReadJobCommitsAndPushesNothing()
    {
        await using var bench = await Bench.Start();
        var main = await bench.RemoteHead("main");
        var job = await ReadingJob(bench);
        var before = await Refs(bench);

        string? copyHead = null;
        string[] entries = [];
        var agent = new ScriptedAgent(
            async (run, _) =>
            {
                // The reader's copy is the branch it reads, at its head, and nothing beside it.
                copyHead = await Bench.Git(run.WorkingDirectory, "rev-parse", "HEAD");
                entries = [.. Directory.GetFileSystemEntries(run.WorkingDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
            },
            () => AgentResult.Success(Reading(), [], "", "oauth_token"));

        await bench.Read(agent).Run(job, Ct);

        // The brief and the reading's form, read only, in a copy of main.
        var given = agent.Given!;
        Assert.Equal(AgentAccess.ReadOnly, given.Access);
        Assert.Equal(job.GetProperty("brief").GetString(), given.Brief);
        Assert.Equal(job.GetProperty("model").GetString(), given.Model);
        Assert.True(JsonNode.DeepEquals(Protocol.Reading.Schema(), JsonNode.Parse(given.ReportSchema)));
        Assert.Equal(main, copyHead);
        Assert.Equal([".git", ".gitignore", "README.md"], entries);

        // The reading went back as it came, with no commit and no check.
        var jobId = job.GetProperty("job_id").GetGuid();
        var sent = bench.SentResult(jobId);
        Assert.Equal("succeeded", (string)sent["outcome"]!);
        Assert.True(JsonNode.DeepEquals(Reading(), sent["report"]));
        Assert.Null(sent["head_commit"]);
        Assert.Null(sent["check"]);
        Assert.Equal("main", (string)sent["branch"]!);

        // Nothing was pushed or asked for to push with: the remote's refs are as they were, no push
        // key was asked for, and neither user ran git push or git commit.
        Assert.Equal(before, await Refs(bench));
        Assert.DoesNotContain(bench.Handler.Calls, call => call.Path.EndsWith("/push-key", StringComparison.Ordinal));
        Assert.DoesNotContain(
            bench.AsRoot.Started.Concat(bench.AsAgent.Started),
            started => started.Spec.FileName == "git" && started.Spec.Arguments.Any(argument => argument is "push" or "commit"));

        // Nothing of the job is left on the machine.
        Assert.Empty(Bench.Files(bench.Folders.Work));
        Assert.Empty(Bench.Files(bench.Folders.Handover));
        Assert.Empty(Bench.Files(bench.Folders.Secrets));
    }

    [Fact]
    public async Task AReadingOfABranchThatIsGoneFailsOutsideItsCards()
    {
        // The branch it was handed is gone by the time the worker fetches: nothing is read, and the
        // failure is not the cards'.
        await using var bench = await Bench.Start();
        var job = await ReadingJob(bench, "feature-9-gone");
        var agent = new ScriptedAgent((_, _) => Task.CompletedTask, () => AgentResult.Success(Reading(), [], "", "oauth_token"));

        await bench.Read(agent).Run(job, Ct);

        Assert.Null(agent.Given);
        var sent = bench.SentResult(job.GetProperty("job_id").GetGuid());
        Assert.Equal("failed", (string)sent["outcome"]!);
        Assert.False((bool)sent["failure"]!["in_card"]!);
        Assert.Contains("feature-9-gone has no commit", (string)sent["failure"]!["reason"]!, StringComparison.Ordinal);
    }
}